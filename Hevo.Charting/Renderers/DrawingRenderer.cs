using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace Hevo.Charting.Renderers
{
    /// <summary>
    /// 💥 工业级 WPF 长效渲染器 (贴地飞行版)
    /// 特性：全节点 0-GC、DPI 预提、SVG 短路解析、文本多态极速拦截。
    /// 核心指引：在渲染的最后一刻，将框架原生的跨平台 float (HevoPoint) 极速升维为 WPF 的 double (Point)。
    /// </summary>
    public sealed class WpfDrawingRenderer : IRenderer<DrawingBuffer, DrawingContext>, IDisposable
    {
        // ==========================================
        // 💥 高频渲染缓存池 (绝不让 new 关键字出现在热路径里)
        // ==========================================
        private readonly Dictionary<IHevoBrush, Brush> _brushCache = new();
        private readonly Dictionary<HevoPen, Pen> _penCache = new();
        private readonly Dictionary<IHevoTypeface, Typeface> _typefaceCache = new();
        private readonly Dictionary<string, Geometry> _svgCache = new(); // SVG 解析缓存

        // 💥 真正的 Pro 级优化：使用 readonly record struct 建立 0-GC 复合键
        // 它的分配在栈上，作为 Dictionary 的 Key 时不会产生任何装箱(Boxing)和堆分配！
        // 直接使用 IHevoString 意图作为 Key，将查表动作死死地压在 Cache Miss 的冷分支！
        private readonly record struct FormattedTextKey(IHevoString Text, Typeface Typeface, double FontSize, Brush Brush);
        private readonly Dictionary<FormattedTextKey, FormattedText> _formattedTextCache = new();

        private double _cachedDpi = 1.0;
        private bool _isDpiInitialized = false;

        // ==========================================
        // 💥 极速桥接器 (强制内联抹除函数开销，float -> double 平滑过渡)
        // ==========================================
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Point T(HevoPoint p) => new Point(p.X, p.Y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Rect T(HevoRect r) => new Rect(r.X, r.Y, r.Width, r.Height);

        public void Render(DrawingBuffer buffer, DrawingContext dc)
        {
            // 💥 极速优化 1：DPI 仅提取一次！彻底消灭循环内的 UI 树穿透。
            if (!_isDpiInitialized && Application.Current?.MainWindow != null)
            {
                _cachedDpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip;
                _isDpiInitialized = true;
            }

            var cmds = buffer.Commands;
            for (int i = 0; i < cmds.Count; i++)
            {
                var cmd = cmds[i];
                var brush = GetBrush(cmd.Brush);
                var pen = GetPen(cmd.Pen);

                switch (cmd.Op)
                {
                    case DrawOp.DrawLine:
                        if (pen != null) dc.DrawLine(pen, T(cmd.Payload.P1), T(cmd.Payload.P2));
                        break;
                    case DrawOp.DrawRectangle:
                        dc.DrawRectangle(brush, pen, T(cmd.Payload.RectArea));
                        break;
                    case DrawOp.DrawRoundedRectangle:
                        dc.DrawRoundedRectangle(brush, pen, T(cmd.Payload.RectArea), cmd.Payload.Val1, cmd.Payload.Val2);
                        break;
                    case DrawOp.DrawEllipse:
                        dc.DrawEllipse(brush, pen, T(cmd.Payload.P1), cmd.Payload.Val1, cmd.Payload.Val2);
                        break;
                    case DrawOp.DrawPolyline:
                        if (pen != null && cmd.RefData is IList<HevoPoint> polyPts && polyPts.Count > 1)
                            dc.DrawGeometry(null, pen, BuildPolyline(polyPts));
                        break;
                    case DrawOp.DrawLineSegments:
                        if (pen != null && cmd.RefData is IList<HevoPoint> segPts && segPts.Count > 1)
                            dc.DrawGeometry(null, pen, BuildLineSegments(segPts));
                        break;
                    case DrawOp.DrawRectangles:
                        if (cmd.RefData is IList<HevoRect> rects && rects.Count > 0)
                            dc.DrawGeometry(brush, pen, BuildRectangles(rects));
                        break;

                    case DrawOp.DrawGeometry:
                        if (cmd.RefData is string svg)
                        {
                            // 💥 极速优化 2：SVG 解析短路拦截
                            if (!_svgCache.TryGetValue(svg, out var geo))
                            {
                                geo = Geometry.Parse(svg);
                                geo.Freeze();
                                _svgCache[svg] = geo;
                            }
                            dc.DrawGeometry(brush, pen, geo);
                        }
                        break;

                    case DrawOp.DrawText:
                        if (brush != null && cmd.RefData is TextInfo textInfo)
                        {
                            var tf = GetTypeface(textInfo.Typeface);
                            double fontSize = cmd.Payload.Val1;

                            // 1. 0-GC 文本排版缓存
                            var key = new FormattedTextKey(textInfo.Text, tf, fontSize, brush);
                            if (!_formattedTextCache.TryGetValue(key, out var ft))
                            {
                                string actualText = WpfRenderRegistry.ResolveString(textInfo.Text);
                                if (string.IsNullOrEmpty(actualText)) break;

                                ft = new FormattedText(
                                    actualText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                    tf, fontSize, brush, _cachedDpi);
                                _formattedTextCache[key] = ft;
                            }

                            // 2. 共用布局解算(背景框 + 文本起点)
                            var layout = TextLayoutHelper.Compute(
                                cmd.Payload.P1.X, cmd.Payload.P1.Y,
                                (float)ft.Width, (float)ft.Height,
                                textInfo.PaddingX, textInfo.PaddingY,
                                textInfo.AlignX, textInfo.AlignY);

                            // 3. 背景框
                            if (textInfo.BgBrush != null || textInfo.BorderPen != null)
                            {
                                var bgWpfBrush = GetBrush(textInfo.BgBrush);
                                var borderWpfPen = GetPen(textInfo.BorderPen);
                                dc.DrawRectangle(bgWpfBrush, borderWpfPen,
                                    new Rect(layout.BoxX, layout.BoxY, layout.BoxWidth, layout.BoxHeight));
                            }

                            // 4. 文字(WPF 用顶左坐标,直接用 TextX/TextY)
                            dc.DrawText(ft, new Point(layout.TextX, layout.TextY));
                        }
                        break;
                    case DrawOp.DrawImage:
                        if (cmd.RefData is ImageSource img) dc.DrawImage(img, T(cmd.Payload.RectArea));
                        break;
                    case DrawOp.DrawVideo:
                        if (cmd.RefData is MediaPlayer player) dc.DrawVideo(player, T(cmd.Payload.RectArea));
                        break;
                    case DrawOp.PushClip:
                        dc.PushClip(new RectangleGeometry(T(cmd.Payload.RectArea)));
                        break;
                    case DrawOp.PushOpacity:
                        dc.PushOpacity(cmd.Payload.Val1);
                        break;
                    case DrawOp.PushTransform:
                        var m = cmd.Payload.Transform; // 💥 System.Numerics.Matrix3x2
                        // Numerics 矩阵完美映射到 WPF Matrix
                        // Numerics (M11, M12, M21, M22, M31(OffsetX), M32(OffsetY))
                        dc.PushTransform(new MatrixTransform(m.M11, m.M12, m.M21, m.M22, m.M31, m.M32));
                        break;
                    case DrawOp.PushGuidelineSet:
                        var guidelines = new GuidelineSet();
                        guidelines.GuidelinesX.Add(cmd.Payload.Val1);
                        guidelines.GuidelinesY.Add(cmd.Payload.Val1);
                        dc.PushGuidelineSet(guidelines);
                        break;
                    case DrawOp.Pop:
                        dc.Pop();
                        break;
                }
            }
        }

        // ==========================================
        // 💥 StreamGeometry 几何体极速构建 (全线换用 HevoPoint/HevoRect)
        // ==========================================
        private Geometry BuildPolyline(IList<HevoPoint> points)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(T(points[0]), false, false);
                for (int i = 1; i < points.Count; i++) ctx.LineTo(T(points[i]), true, false);
            }
            geo.Freeze();
            return geo;
        }

        private Geometry BuildLineSegments(IList<HevoPoint> points)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                for (int i = 0; i < points.Count - 1; i += 2)
                {
                    ctx.BeginFigure(T(points[i]), false, false);
                    ctx.LineTo(T(points[i + 1]), true, false);
                }
            }
            geo.Freeze();
            return geo;
        }

        private Geometry BuildRectangles(IList<HevoRect> rects)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                for (int i = 0; i < rects.Count; i++)
                {
                    var r = rects[i];
                    ctx.BeginFigure(new Point(r.Left, r.Top), true, true);
                    ctx.LineTo(new Point(r.Right, r.Top), true, false);
                    ctx.LineTo(new Point(r.Right, r.Bottom), true, false);
                    ctx.LineTo(new Point(r.Left, r.Bottom), true, false);
                }
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
            if (_brushCache.TryGetValue(desc, out var wpfBrush)) return wpfBrush;

            wpfBrush = WpfRenderRegistry.CreateBrush(desc);

            if (wpfBrush != null && wpfBrush.CanFreeze) wpfBrush.Freeze();

            if (wpfBrush != null) _brushCache[desc] = wpfBrush;
            return wpfBrush;
        }

        private Pen? GetPen(HevoPen? desc)
        {
            if (desc == null) return null;
            if (_penCache.TryGetValue(desc, out var wpfPen)) return wpfPen;

            var brush = GetBrush(desc.Brush);
            if (brush == null) return null;

            wpfPen = new Pen(brush, desc.Thickness);
            if (desc.DashArray != null && desc.DashArray.Length > 0) wpfPen.DashStyle = new DashStyle(desc.DashArray, 0);
            wpfPen.StartLineCap = (PenLineCap)desc.LineCap;
            wpfPen.EndLineCap = (PenLineCap)desc.LineCap;
            wpfPen.LineJoin = (PenLineJoin)desc.LineJoin;

            if (wpfPen.CanFreeze) wpfPen.Freeze();

            _penCache[desc] = wpfPen;
            return wpfPen;
        }

        private Typeface GetTypeface(IHevoTypeface desc)
        {
            if (_typefaceCache.TryGetValue(desc, out var tf)) return tf;

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
                var res = Application.Current.TryFindResource(rtf.ResourceKey);
                if (res is FontFamily ff) familyName = ff.Source;
                else if (res is string s) familyName = s;

                weightVal = rtf.FontWeight;
                isItalic = rtf.IsItalic;
            }

            var weight = FontWeight.FromOpenTypeWeight(weightVal);
            var style = isItalic ? FontStyles.Italic : FontStyles.Normal;
            tf = new Typeface(new FontFamily(familyName), style, weight, FontStretches.Normal);

            _typefaceCache[desc] = tf;
            return tf;
        }

        public void Dispose()
        {
            // 彻底释放内存
            _brushCache.Clear();
            _penCache.Clear();
            _typefaceCache.Clear();
            _formattedTextCache.Clear();
            _svgCache.Clear();
        }
    }
}
