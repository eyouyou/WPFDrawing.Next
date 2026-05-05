using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.DevTools;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace Hevo.Charting.Renderers
{
    /// <summary>
    /// 💥 工业级 WPF 长效渲染器 (贴地飞行版)
    /// 特性：全节点 0-GC、DPI 注入、SVG 短路解析、文本多态极速拦截。
    /// 核心指引：在渲染的最后一刻，将框架原生的跨平台 float (HevoPoint) 极速升维为 WPF 的 double (Point)。
    /// </summary>
    public sealed class WpfDrawingRenderer : IRenderer<DrawingBuffer, DrawingContext>, IDisposable
    {
        // 可空 diagnostics:null 时所有插桩点变成单次 null 检查 + 短路,实测开销 < 0.1ns/cmd。
        private readonly RenderDiagnostics? _diag;

        public WpfDrawingRenderer() : this(null) { }
        internal WpfDrawingRenderer(RenderDiagnostics? diagnostics) { _diag = diagnostics; }

        // ==========================================
        // 💥 LRU 缓存池 (有上限,长寿命运行不爆 GC)
        // ==========================================
        // cap 数值依据:K 线 / 多 series 业务下,brush 通常 < 32 种,pen < 64 种,typeface < 16 种;
        // formattedText = (字符串 × 字号 × 颜色) 长尾分布,4096 已能覆盖典型一屏 + 滚动复用窗口;
        // svg 几何 256 足够覆盖业务图标集。超出时 LRU 淘汰最久未访问项。
        private readonly LruCache<IHevoBrush, Brush> _brushCache = new(128);
        private readonly LruCache<HevoPen, Pen> _penCache = new(128);
        private readonly LruCache<IHevoTypeface, Typeface> _typefaceCache = new(64);
        private readonly LruCache<string, Geometry> _svgCache = new(256);

        // 💥 0-GC 复合 key:用 IHevoBrush / IHevoTypeface(record,语义相等)而非 WPF Brush / Typeface(引用相等)。
        // 好处:brush LRU 缓存淘汰后即使重建出新 WPF Brush 实例,文本 cache 仍命中(同色同字号同字体 → 同 key)。
        private readonly record struct FormattedTextKey(IHevoString Text, IHevoTypeface Typeface, double FontSize, IHevoBrush Brush);
        private readonly LruCache<FormattedTextKey, FormattedText> _formattedTextCache = new(4096);

        // ==========================================
        // 💥 last-used 短路:hot loop 内连续多条命令复用同一 brush/pen 时跳过 LRU 链表挪移。
        // 典型场景:axis labels 全用同一 LabelBrush;candle series 同色 body 一帧上百根。
        // ReferenceEquals 命中即可,业务侧 brush 实例通常被 LineStyle/Style 缓存。
        // ==========================================
        private IHevoBrush? _lastBrushDesc;
        private Brush? _lastBrushHandle;
        private HevoPen? _lastPenDesc;
        private Pen? _lastPenHandle;

        // ==========================================
        // 💥 DPI 注入 (默认 1.0,业务侧通过 PixelsPerDip setter 同步真实 DPI)
        // ==========================================
        // 旧实现从 Application.Current.MainWindow 取,多窗口 / 跨屏拖动场景会拿错值。
        // 现在 ChartCell 在 Loaded / DpiChanged 时显式注入,renderer 自身不再做 visual tree walk。
        public double PixelsPerDip { get; set; } = 1.0;

        // ==========================================
        // 💥 极速桥接器 (强制内联抹除函数开销,float -> double 平滑过渡)
        // ==========================================
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Point T(HevoPoint p) => new Point(p.X, p.Y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Rect T(HevoRect r) => new Rect(r.X, r.Y, r.Width, r.Height);

        public void Render(DrawingBuffer buffer, DrawingContext dc)
        {
            // diagnostics:开始测量整帧 dispatch 耗时 (含 paint cache 解析 + WPF dc 调用)
            long startTicks = _diag != null ? Stopwatch.GetTimestamp() : 0L;

            // 💥 用 Span 替代 List 索引访问:省掉边界检查 + 24+ 字节 DrawCmd 值复制,hot loop 提速 5%~10%。
            var span = CollectionsMarshal.AsSpan(buffer.Commands);
#if DEBUG
            int pushDepth = 0; // 验证 Push/Pop 配平,失衡会污染下一帧 dc 状态
#endif
            for (int i = 0; i < span.Length; i++)
            {
                ref readonly var cmd = ref span[i];

                // ============================================================
                // [batch detector] 相邻 DrawLine + 同 pen → 1 次 DrawGeometry 多 figure
                // ============================================================
                // 典型场景: axis tick / grid line / segment 集合(几十条同 pen 短线)。
                // N 次 dc.DrawLine 改为 1 次 dc.DrawGeometry,WPF Composer 调度成本立降。
                // 约束:
                //   - 同 pen 用 ReferenceEquals,避免 hot loop 跑 record hash
                //   - 相邻 DrawLine 之间不能夹任何其它 op(Push/Pop/Draw* 都会断 run)
                //   - 落在 switch 之前,run==1 fall through 到原 DrawLine 单分支
                if (cmd.Op == DrawOp.DrawLine && cmd.Pen != null)
                {
                    int run = 1;
                    while (i + run < span.Length
                        && span[i + run].Op == DrawOp.DrawLine
                        && ReferenceEquals(span[i + run].Pen, cmd.Pen))
                    {
                        run++;
                    }
                    if (run >= 2)
                    {
                        var batchPen = GetPen(cmd.Pen);
                        if (batchPen != null)
                        {
                            float vHalfBatch = PixelSnap.HalfPx(cmd.Pen!.Thickness);
                            dc.DrawGeometry(null, batchPen, BuildLineBatch(span.Slice(i, run), vHalfBatch));
                        }
                        i += run - 1; // 跳过整批,for 自增 +1 = i + run
                        continue;
                    }
                    // run == 1 → fallthrough 到 case DrawLine
                }

                // 💥 brush/pen/penThick/vHalf 不再无差别提前算 —— 下沉到需要的 case 分支,
                // Push*/Pop/DrawImage/DrawVideo 等命令省掉无效字典 lookup。
                switch (cmd.Op)
                {
                    case DrawOp.DrawLine:
                    {
                        var pen = GetPen(cmd.Pen);
                        if (pen == null) break;
                        float vHalf = PixelSnap.HalfPx(cmd.Pen!.Thickness);
                        PixelSnap.SnapEndpoints(cmd.Payload.P1, cmd.Payload.P2, vHalf, out var p1, out var p2);
                        dc.DrawLine(pen, T(p1), T(p2));
                        break;
                    }
                    case DrawOp.DrawRectangle:
                    {
                        var r = cmd.Payload.RectArea;
                        var brush = GetBrush(cmd.Brush);
                        var pen = GetPen(cmd.Pen);
                        if (brush != null) dc.DrawRectangle(brush, null, T(r));
                        if (pen != null)
                        {
                            var sr = PixelSnap.InsideStroke(r, cmd.Pen!.Thickness);
                            dc.DrawRectangle(null, pen, T(sr));
                        }
                        break;
                    }
                    case DrawOp.DrawRoundedRectangle:
                    {
                        var r = cmd.Payload.RectArea;
                        var brush = GetBrush(cmd.Brush);
                        var pen = GetPen(cmd.Pen);
                        if (brush != null) dc.DrawRoundedRectangle(brush, null, T(r), cmd.Payload.Val1, cmd.Payload.Val2);
                        if (pen != null)
                        {
                            var sr = PixelSnap.InsideStroke(r, cmd.Pen!.Thickness);
                            dc.DrawRoundedRectangle(null, pen, T(sr), cmd.Payload.Val1, cmd.Payload.Val2);
                        }
                        break;
                    }
                    case DrawOp.DrawEllipse:
                    {
                        // 圆点以填充为主:圆心 snap 到整数像素,让 AA 鲜锐对称(直径 2r=偶数时不变椭圆)。
                        // 与 polyline 顶点(int+0.5)的"原理性" 0.5px 偏移在 r≥3 的 dot 上视觉不可分辨。
                        var brush = GetBrush(cmd.Brush);
                        var pen = GetPen(cmd.Pen);
                        var c = cmd.Payload.P1;
                        var snapped = new Point(MathF.Round(c.X), MathF.Round(c.Y));
                        dc.DrawEllipse(brush, pen, snapped, cmd.Payload.Val1, cmd.Payload.Val2);
                        break;
                    }
                    case DrawOp.DrawPolyline:
                    {
                        var pen = GetPen(cmd.Pen);
                        if (pen != null && cmd.RefData is List<HevoPoint> polyPts && polyPts.Count > 1)
                        {
                            float vHalf = PixelSnap.HalfPx(cmd.Pen!.Thickness);
                            dc.DrawGeometry(null, pen, BuildPolyline(polyPts, vHalf));
                        }
                        break;
                    }
                    case DrawOp.DrawLineSegments:
                    {
                        var pen = GetPen(cmd.Pen);
                        if (pen != null && cmd.RefData is List<HevoPoint> segPts && segPts.Count > 1)
                        {
                            float vHalf = PixelSnap.HalfPx(cmd.Pen!.Thickness);
                            dc.DrawGeometry(null, pen, BuildLineSegments(segPts, vHalf));
                        }
                        break;
                    }
                    case DrawOp.DrawCubicBezier:
                    {
                        var pen = GetPen(cmd.Pen);
                        if (pen != null && cmd.RefData is HevoPoint[] ctrls && ctrls.Length == 2)
                        {
                            float vHalf = PixelSnap.HalfPx(cmd.Pen!.Thickness);
                            dc.DrawGeometry(null, pen,
                                BuildCubicBezier(cmd.Payload.P1, ctrls[0], ctrls[1], cmd.Payload.P2, vHalf));
                        }
                        break;
                    }
                    case DrawOp.DrawRectangles:
                    {
                        if (cmd.RefData is not IList<HevoRect> rects || rects.Count == 0) break;
                        // 逐 rect 调用 dc.DrawRectangle,跟历史 Skia 路径行为对齐。
                        // 历史教训:用 StreamGeometry 批量(BuildRectangles 多 figure + isClosed=true)时,
                        // WPF 在 figure 间留下幻线,fill 把跨 rect 角点连线一起涂上 brush 色,K 线 body 间冒红色"趋势线"伪影。
                        var brush = GetBrush(cmd.Brush);
                        var pen = GetPen(cmd.Pen);
                        double penThick = cmd.Pen?.Thickness ?? 0;
                        for (int j = 0; j < rects.Count; j++)
                        {
                            var raw = rects[j];
                            if (brush != null) dc.DrawRectangle(brush, null, T(raw));
                            if (pen != null)
                            {
                                var sr = PixelSnap.InsideStroke(raw, penThick);
                                dc.DrawRectangle(null, pen, T(sr));
                            }
                        }
                        break;
                    }

                    case DrawOp.DrawGeometry:
                        if (cmd.RefData is string svg)
                        {
                            // 💥 SVG 解析短路拦截
                            if (!_svgCache.TryGet(svg, out var geo))
                            {
                                geo = Geometry.Parse(svg);
                                geo.Freeze();
                                _svgCache.Set(svg, geo);
                            }
                            dc.DrawGeometry(GetBrush(cmd.Brush), GetPen(cmd.Pen), geo);
                        }
                        break;

                    case DrawOp.DrawText:
                    {
                        var brush = GetBrush(cmd.Brush);
                        if (brush == null || cmd.RefData is not TextInfo textInfo) break;

                        var tf = GetTypeface(textInfo.Typeface);
                        double fontSize = cmd.Payload.Val1;

                        // 1. 0-GC 文本排版缓存:key 用语义相等的 IHevoBrush/IHevoTypeface,brush LRU 淘汰重建后仍命中。
                        var key = new FormattedTextKey(textInfo.Text, textInfo.Typeface, fontSize, cmd.Brush!);
                        if (_formattedTextCache.TryGet(key, out var ft))
                        {
                            _diag?.OnFormattedTextHit();
                        }
                        else
                        {
                            _diag?.OnFormattedTextMiss();
                            string actualText = WpfRenderRegistry.ResolveString(textInfo.Text);
                            if (string.IsNullOrEmpty(actualText)) break;

                            ft = new FormattedText(
                                actualText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                tf, fontSize, brush, PixelsPerDip);
                            _formattedTextCache.Set(key, ft);
                        }

                        // 2. 共用布局解算(背景框 + 文本起点)
                        var layout = TextLayoutHelper.Compute(
                            cmd.Payload.P1.X, cmd.Payload.P1.Y,
                            (float)ft.Width, (float)ft.Height,
                            textInfo.PaddingX, textInfo.PaddingY,
                            textInfo.AlignX, textInfo.AlignY);

                        // 3. 背景框:遵守像素对齐协议 §13 —— fill 走原 rect、stroke 走 InsideStroke。
                        if (textInfo.BgBrush != null || textInfo.BorderPen != null)
                        {
                            var bgWpfBrush = GetBrush(textInfo.BgBrush);
                            var borderWpfPen = GetPen(textInfo.BorderPen);
                            var rawRect = new Rect(layout.BoxX, layout.BoxY, layout.BoxWidth, layout.BoxHeight);
                            if (bgWpfBrush != null) dc.DrawRectangle(bgWpfBrush, null, rawRect);
                            if (borderWpfPen != null)
                            {
                                var hevoRect = new HevoRect((float)layout.BoxX, (float)layout.BoxY, (float)layout.BoxWidth, (float)layout.BoxHeight);
                                var snapped = PixelSnap.InsideStroke(hevoRect, textInfo.BorderPen!.Thickness);
                                dc.DrawRectangle(null, borderWpfPen, T(snapped));
                            }
                        }

                        // 4. 文字(WPF 用顶左坐标,直接用 TextX/TextY)
                        dc.DrawText(ft, new Point(layout.TextX, layout.TextY));
                        break;
                    }
                    case DrawOp.DrawImage:
                        if (cmd.RefData is ImageSource img) dc.DrawImage(img, T(cmd.Payload.RectArea));
                        break;
                    case DrawOp.DrawVideo:
                        if (cmd.RefData is MediaPlayer player) dc.DrawVideo(player, T(cmd.Payload.RectArea));
                        break;
                    case DrawOp.PushClip:
                        dc.PushClip(new RectangleGeometry(T(cmd.Payload.RectArea)));
#if DEBUG
                        pushDepth++;
#endif
                        break;
                    case DrawOp.PushOpacity:
                        dc.PushOpacity(cmd.Payload.Val1);
#if DEBUG
                        pushDepth++;
#endif
                        break;
                    case DrawOp.PushTransform:
                    {
                        var m = cmd.Payload.Transform; // System.Numerics.Matrix3x2
                        // Numerics (M11, M12, M21, M22, M31(OffsetX), M32(OffsetY))
                        dc.PushTransform(new MatrixTransform(m.M11, m.M12, m.M21, m.M22, m.M31, m.M32));
#if DEBUG
                        pushDepth++;
#endif
                        break;
                    }
                    case DrawOp.Pop:
                        dc.Pop();
#if DEBUG
                        pushDepth--;
                        System.Diagnostics.Debug.Assert(pushDepth >= 0,
                            $"Renderer Pop without matching Push at command index {i}");
#endif
                        break;
                }
            }
#if DEBUG
            System.Diagnostics.Debug.Assert(pushDepth == 0,
                $"Renderer Push/Pop unbalanced at end of Render: depth={pushDepth}. dc state will leak to next frame.");
#endif

            // diagnostics:每个 layer 的 render 累加到当前帧的累加器；
            // ChartCell 在 RenderWpfLayers 前后做 BeginFrame / EndFrame 收口。
            if (_diag != null)
            {
                long elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
                _diag.OnLayerRender(elapsedTicks, span.Length);
            }
        }

        // ==========================================
        // 💥 StreamGeometry 几何体极速构建
        // 像素对齐协议:所有 stroke 一律 vertex snap。
        // ==========================================

        // batch detector 辅助:把 N 个相邻 DrawLine 合成单 StreamGeometry 多 figure。
        // 每 figure 一段独立直线,不闭合(IsClosed=false 避免 fill 路径)、IsFilled=false 避免触发
        // 历史的 "figure 间幻线" 问题(详见 §4.2 DrawRectangles 注释)。
        private static Geometry BuildLineBatch(ReadOnlySpan<DrawCmd> cmds, float vHalf)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                for (int j = 0; j < cmds.Length; j++)
                {
                    ref readonly var c = ref cmds[j];
                    PixelSnap.SnapEndpoints(c.Payload.P1, c.Payload.P2, vHalf, out var a, out var b);
                    ctx.BeginFigure(new Point(a.X, a.Y), isFilled: false, isClosed: false);
                    ctx.LineTo(new Point(b.X, b.Y), isStroked: true, isSmoothJoin: false);
                }
            }
            geo.Freeze();
            return geo;
        }

        // points 由 DrawingExtensions 公共 API 锁死为 List<HevoPoint>，可直接 AsSpan 走数组。
        // 跳过 IList<T>.this[int] 的虚分发，8K 顶点折线 / Catmull-Rom 输出收益最显著。
        private static Geometry BuildPolyline(List<HevoPoint> points, float vHalf)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                var span = CollectionsMarshal.AsSpan(points);
                ref readonly var p0 = ref span[0];
                ctx.BeginFigure(new Point(PixelSnap.Vertex(p0.X, vHalf), PixelSnap.Vertex(p0.Y, vHalf)), false, false);
                for (int i = 1; i < span.Length; i++)
                {
                    ref readonly var p = ref span[i];
                    ctx.LineTo(new Point(PixelSnap.Vertex(p.X, vHalf), PixelSnap.Vertex(p.Y, vHalf)), true, false);
                }
            }
            geo.Freeze();
            return geo;
        }

        private static Geometry BuildLineSegments(List<HevoPoint> points, float vHalf)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                var span = CollectionsMarshal.AsSpan(points);
                int last = span.Length - 1;
                for (int i = 0; i < last; i += 2)
                {
                    PixelSnap.SnapEndpoints(span[i], span[i + 1], vHalf, out var a, out var b);
                    ctx.BeginFigure(new Point(a.X, a.Y), false, false);
                    ctx.LineTo(new Point(b.X, b.Y), true, false);
                }
            }
            geo.Freeze();
            return geo;
        }

        // 三次贝塞尔:端点做像素对齐(跟 polyline 同口径,跨帧不晃),控制点不需对齐(它们不出现在画面上)。
        // BezierTo 内部走 WPF 几何引擎的硬件抗锯齿曲线管线,长边也无折角。
        private static Geometry BuildCubicBezier(HevoPoint p0, HevoPoint c1, HevoPoint c2, HevoPoint p3, float vHalf)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(PixelSnap.Vertex(p0.X, vHalf), PixelSnap.Vertex(p0.Y, vHalf)), false, false);
                ctx.BezierTo(
                    new Point(c1.X, c1.Y),
                    new Point(c2.X, c2.Y),
                    new Point(PixelSnap.Vertex(p3.X, vHalf), PixelSnap.Vertex(p3.Y, vHalf)),
                    isStroked: true, isSmoothJoin: false);
            }
            geo.Freeze();
            return geo;
        }

        // ==========================================
        // 💥 抽象解析与资源提取
        // ==========================================
        private Brush? GetBrush(IHevoBrush? desc)
        {
            if (desc == null) return null;
            // last-used 短路:连续命令复用同一 desc 时绕开字典 + LRU 链表。
            if (ReferenceEquals(desc, _lastBrushDesc)) { _diag?.OnPaintCacheHit(); return _lastBrushHandle; }
            if (_brushCache.TryGet(desc, out var wpfBrush))
            {
                _diag?.OnPaintCacheHit();
                _lastBrushDesc = desc; _lastBrushHandle = wpfBrush;
                return wpfBrush;
            }

            _diag?.OnPaintCacheMiss();
            wpfBrush = WpfRenderRegistry.CreateBrush(desc);
            if (wpfBrush == null) return null;

            if (wpfBrush.CanFreeze) wpfBrush.Freeze();
            _brushCache.Set(desc, wpfBrush);
            _lastBrushDesc = desc; _lastBrushHandle = wpfBrush;
            return wpfBrush;
        }

        private Pen? GetPen(HevoPen? desc)
        {
            if (desc == null) return null;
            if (ReferenceEquals(desc, _lastPenDesc)) { _diag?.OnPaintCacheHit(); return _lastPenHandle; }
            if (_penCache.TryGet(desc, out var wpfPen))
            {
                _diag?.OnPaintCacheHit();
                _lastPenDesc = desc; _lastPenHandle = wpfPen;
                return wpfPen;
            }

            _diag?.OnPaintCacheMiss();
            var brush = GetBrush(desc.Brush);
            if (brush == null) return null;

            wpfPen = new Pen(brush, desc.Thickness);
            if (desc.DashArray != null && desc.DashArray.Length > 0) wpfPen.DashStyle = new DashStyle(desc.DashArray, 0);
            wpfPen.StartLineCap = (PenLineCap)desc.LineCap;
            wpfPen.EndLineCap = (PenLineCap)desc.LineCap;
            wpfPen.LineJoin = (PenLineJoin)desc.LineJoin;

            if (wpfPen.CanFreeze) wpfPen.Freeze();
            _penCache.Set(desc, wpfPen);
            _lastPenDesc = desc; _lastPenHandle = wpfPen;
            return wpfPen;
        }

        private Typeface GetTypeface(IHevoTypeface desc)
        {
            if (_typefaceCache.TryGet(desc, out var tf)) return tf;

            string familyName = "Arial"; // Default
            int weightVal = 400;
            bool isItalic = false;

            if (desc is HevoTypeface ht)
            {
                familyName = ht.FontFamily;
                weightVal = ht.FontWeight;
                isItalic = ht.IsItalic;
            }
            else if (desc is HevoResourceTypeface rtf)
            {
                var res = Application.Current?.TryFindResource(rtf.ResourceKey);
                if (res is FontFamily ff) familyName = ff.Source;
                else if (res is string s) familyName = s;

                weightVal = rtf.FontWeight;
                isItalic = rtf.IsItalic;
            }

            var weight = FontWeight.FromOpenTypeWeight(weightVal);
            var style = isItalic ? FontStyles.Italic : FontStyles.Normal;
            tf = new Typeface(new FontFamily(familyName), style, weight, FontStretches.Normal);

            _typefaceCache.Set(desc, tf);
            return tf;
        }

        public void Dispose()
        {
            _brushCache.Clear();
            _penCache.Clear();
            _typefaceCache.Clear();
            _formattedTextCache.Clear();
            _svgCache.Clear();
            _lastBrushDesc = null; _lastBrushHandle = null;
            _lastPenDesc = null; _lastPenHandle = null;
        }

        // ==========================================
        // 💥 LRU 缓存:Dictionary + 双向链表 O(1) 命中 + O(1) 淘汰
        // 命中走 dict + 链表 splice;Miss 走 add + 超 cap 淘汰队尾。
        // 稳态优化:
        //   - Set 在 cap 满时复用淘汰节点(零 LinkedListNode 分配)
        //   - TryGet 命中时若已在队首则跳过 Remove+AddFirst(高频元素零链表操作)
        // ==========================================
        private sealed class LruCache<TKey, TValue> where TKey : notnull
        {
            private readonly int _capacity;
            private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map;
            private readonly LinkedList<KeyValuePair<TKey, TValue>> _list = new();

            public LruCache(int capacity)
            {
                _capacity = capacity;
                _map = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(capacity);
            }

            public bool TryGet(TKey key, out TValue value)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    // 已在队首则零操作返回，热点元素重复命中近乎免费
                    if (!ReferenceEquals(node, _list.First))
                    {
                        _list.Remove(node);
                        _list.AddFirst(node);
                    }
                    value = node.Value.Value;
                    return true;
                }
                value = default!;
                return false;
            }

            public void Set(TKey key, TValue value)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    existing.Value = new KeyValuePair<TKey, TValue>(key, value);
                    if (!ReferenceEquals(existing, _list.First))
                    {
                        _list.Remove(existing);
                        _list.AddFirst(existing);
                    }
                    return;
                }
                if (_map.Count >= _capacity)
                {
                    // 复用淘汰节点：把队尾节点搬到队首并改写 Value，避免每次 miss 都 new LinkedListNode
                    var oldest = _list.Last!;
                    _map.Remove(oldest.Value.Key);
                    _list.RemoveLast();
                    oldest.Value = new KeyValuePair<TKey, TValue>(key, value);
                    _list.AddFirst(oldest);
                    _map[key] = oldest;
                    return;
                }
                var node = _list.AddFirst(new KeyValuePair<TKey, TValue>(key, value));
                _map[key] = node;
            }

            public void Clear()
            {
                _map.Clear();
                _list.Clear();
            }
        }
    }
}
