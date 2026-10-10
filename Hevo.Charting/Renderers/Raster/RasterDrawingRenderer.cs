using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.DevTools;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hevo.Charting.Renderers.Raster
{
    /// <summary>
    /// 位图后端的矢量渲染器:把 <see cref="DrawingBuffer"/> 里同一套 DrawCmd 光栅化到 <see cref="RasterSurface"/>。
    /// <para>
    /// 对齐 WPF 路径的约定:
    /// <list type="bullet">
    /// <item>坐标先按 <see cref="PixelSnap"/> 在 DIP 上对齐,再乘 DPI 进设备像素,跟 WpfDrawingRenderer 同源;</item>
    /// <item>覆盖规则 = 像素中心采样、无抗锯齿,等同 ChartCell 给画布设的 EdgeMode.Aliased;</item>
    /// <item>WPF Pen 的默认值:DashCap = Square,MiterLimit = 10。</item>
    /// </list>
    /// 已知差异:PushOpacity 逐图元乘透明度(WPF 是整组合成后再乘,组内重叠处会略深);
    /// 渐变只取首尾两个色标;字形落在整像素上(WPF 是亚像素定位);DrawVideo 不支持(ChartCell 自动回退矢量)。
    /// </para>
    /// <para>热路径 0 分配:画刷 / 画笔 / 几何 / 字形都有缓存,点缓冲、边表跨帧复用。</para>
    /// </summary>
    internal sealed class RasterDrawingRenderer : IRenderer<DrawingBuffer, RasterSurface>, IRenderer<BitmapBuffer, RasterSurface>
    {
        private readonly RenderDiagnostics? _diag;
        public RasterDrawingRenderer(RenderDiagnostics? diagnostics = null) { _diag = diagnostics; }

        // ── 状态栈 ──────────────────────────────────────────────────────────────
        private struct State
        {
            public Matrix3x2 M;
            public float Opacity;
            public int ClipX0, ClipY0, ClipX1, ClipY1;
        }

        private State[] _stack = new State[16];
        private int _depth;
        private RasterSurface _s = new(); // Begin 时换成本帧的表面
        private Matrix3x2 _m;
        private bool _axisAligned;   // 当前变换无旋转 / 斜切 → 矩形、水平垂直线走快路径
        private float _lineScale;    // 线宽缩放(DIP → 设备)
        private float _opacity;

        // ── 复用缓冲 ────────────────────────────────────────────────────────────
        private readonly ScanlineFiller _filler = new();
        private readonly Stroker _stroker = new();
        private readonly RasterTextRenderer _text = new();
        private Vector2[] _pts = new Vector2[256];
        private readonly Vector2[] _shape = new Vector2[256];

        // ── 资源缓存 ────────────────────────────────────────────────────────────
        private struct BrushInfo
        {
            public bool Valid;
            public bool Gradient;
            public bool Relative;
            public Color C0, C1;
            public Point Start, End;
            public float Opacity;
        }

        private sealed class PenInfo
        {
            public BrushInfo Brush;
            public float Thickness;
            public StrokeCap Cap;
            public StrokeJoin Join;
            public double[]? Dash;
        }

        private sealed class FlatGeometry
        {
            public Vector2[][] Figures = Array.Empty<Vector2[]>();
            public bool[] Closed = Array.Empty<bool>();
            public bool[] Filled = Array.Empty<bool>();
            public bool EvenOdd;
        }

        private sealed class PixelImage
        {
            public int[] Pixels = Array.Empty<int>();
            public int W, H;
        }

        private const int MaxPaints = 256;
        private const int MaxGeometries = 256;
        private readonly Dictionary<IHevoBrush, BrushInfo> _brushes = new();
        private readonly Dictionary<HevoPen, PenInfo?> _pens = new();
        private readonly Dictionary<string, FlatGeometry> _geometries = new();
        private readonly ConditionalWeakTable<ImageSource, PixelImage> _images = new();
        private IHevoBrush? _lastBrushDesc;
        private BrushInfo _lastBrush;
        private HevoPen? _lastPenDesc;
        private PenInfo? _lastPen;

        /// <summary>这批指令能否走位图(DrawVideo 只能交给 WPF)。</summary>
        public static bool CanRasterize(DrawingBuffer buffer)
        {
            var span = CollectionsMarshal.AsSpan(buffer.Commands);
            for (int i = 0; i < span.Length; i++)
                if (span[i].Op == DrawOp.DrawVideo) return false;
            return true;
        }

        // ====================================================================
        // DrawingBuffer
        // ====================================================================

        public void Render(DrawingBuffer buffer, RasterSurface surface)
        {
            long startTicks = _diag != null ? Stopwatch.GetTimestamp() : 0L;
            Begin(surface);
            var span = CollectionsMarshal.AsSpan(buffer.Commands);
            for (int i = 0; i < span.Length; i++)
            {
                ref readonly var cmd = ref span[i];
                switch (cmd.Op)
                {
                    case DrawOp.DrawLine:
                    {
                        var pen = GetPen(cmd.Pen);
                        if (pen == null) break;
                        float half = PixelSnap.HalfPx(pen.Thickness);
                        PixelSnap.SnapEndpoints(cmd.Payload.P1, cmd.Payload.P2, half, out var a, out var b);
                        StrokeLine(a, b, pen);
                        break;
                    }
                    case DrawOp.DrawLineSegments:
                    {
                        var pen = GetPen(cmd.Pen);
                        if (pen == null || cmd.RefData is not List<HevoPoint> pts || pts.Count < 2) break;
                        float half = PixelSnap.HalfPx(pen.Thickness);
                        var ps = CollectionsMarshal.AsSpan(pts);
                        for (int j = 0; j + 1 < ps.Length; j += 2)
                        {
                            PixelSnap.SnapEndpoints(ps[j], ps[j + 1], half, out var a, out var b);
                            StrokeLine(a, b, pen);
                        }
                        break;
                    }
                    case DrawOp.DrawPolyline:
                    {
                        var pen = GetPen(cmd.Pen);
                        if (pen == null || cmd.RefData is not List<HevoPoint> pts || pts.Count < 2) break;
                        float half = PixelSnap.HalfPx(pen.Thickness);
                        var ps = CollectionsMarshal.AsSpan(pts);
                        var dev = EnsurePts(ps.Length);
                        for (int j = 0; j < ps.Length; j++)
                            dev[j] = Tx(PixelSnap.Vertex(ps[j].X, half), PixelSnap.Vertex(ps[j].Y, half));
                        StrokePath(dev.Slice(0, ps.Length), false, pen);
                        break;
                    }
                    case DrawOp.DrawCubicBezier:
                    {
                        var pen = GetPen(cmd.Pen);
                        if (pen == null || cmd.RefData is not HevoPoint[] { Length: 2 } c) break;
                        float half = PixelSnap.HalfPx(pen.Thickness);
                        var p0 = Tx(PixelSnap.Vertex(cmd.Payload.P1.X, half), PixelSnap.Vertex(cmd.Payload.P1.Y, half));
                        var p3 = Tx(PixelSnap.Vertex(cmd.Payload.P2.X, half), PixelSnap.Vertex(cmd.Payload.P2.Y, half));
                        var c1 = Tx(c[0].X, c[0].Y);
                        var c2 = Tx(c[1].X, c[1].Y);
                        float len = Vector2.Distance(p0, c1) + Vector2.Distance(c1, c2) + Vector2.Distance(c2, p3);
                        int segs = Math.Clamp((int)(len / 4f), 8, 128);
                        var dev = EnsurePts(segs + 1);
                        for (int k = 0; k <= segs; k++)
                        {
                            float t = (float)k / segs, u = 1 - t;
                            dev[k] = u * u * u * p0 + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * p3;
                        }
                        StrokePath(dev.Slice(0, segs + 1), false, pen);
                        break;
                    }
                    case DrawOp.DrawRectangle:
                    {
                        var r = cmd.Payload.RectArea;
                        var brush = GetBrush(cmd.Brush);
                        if (brush.Valid) FillRectDip(r, brush);
                        var pen = GetPen(cmd.Pen);
                        if (pen != null) StrokeRectDip(PixelSnap.InsideStroke(r, pen.Thickness), pen);
                        break;
                    }
                    case DrawOp.DrawRectangles:
                    {
                        if (cmd.RefData is not IList<HevoRect> rects || rects.Count == 0) break;
                        var brush = GetBrush(cmd.Brush);
                        var pen = GetPen(cmd.Pen);
                        if (rects is List<HevoRect> list)
                        {
                            var rs = CollectionsMarshal.AsSpan(list);
                            for (int j = 0; j < rs.Length; j++)
                            {
                                if (brush.Valid) FillRectDip(rs[j], brush);
                                if (pen != null) StrokeRectDip(PixelSnap.InsideStroke(rs[j], pen.Thickness), pen);
                            }
                        }
                        else
                        {
                            for (int j = 0; j < rects.Count; j++)
                            {
                                var rj = rects[j];
                                if (brush.Valid) FillRectDip(rj, brush);
                                if (pen != null) StrokeRectDip(PixelSnap.InsideStroke(rj, pen.Thickness), pen);
                            }
                        }
                        break;
                    }
                    case DrawOp.DrawRoundedRectangle:
                    {
                        var r = cmd.Payload.RectArea;
                        var brush = GetBrush(cmd.Brush);
                        var pen = GetPen(cmd.Pen);
                        if (brush.Valid)
                        {
                            int n = BuildRoundedRect(r, cmd.Payload.Val1, cmd.Payload.Val2);
                            FillShape(n, brush, r);
                        }
                        if (pen != null)
                        {
                            var sr = PixelSnap.InsideStroke(r, pen.Thickness);
                            int n = BuildRoundedRect(sr, cmd.Payload.Val1, cmd.Payload.Val2);
                            StrokeShape(n, pen);
                        }
                        break;
                    }
                    case DrawOp.DrawEllipse:
                    {
                        // 圆心对齐到整数 DIP,跟 WpfDrawingRenderer 一致
                        var c = cmd.Payload.P1;
                        float cx = MathF.Round(c.X), cy = MathF.Round(c.Y);
                        float rx = cmd.Payload.Val1, ry = cmd.Payload.Val2;
                        if (rx <= 0 || ry <= 0) break;
                        int n = BuildEllipse(cx, cy, rx, ry);
                        var bounds = new HevoRect(cx - rx, cy - ry, rx * 2, ry * 2);
                        var brush = GetBrush(cmd.Brush);
                        if (brush.Valid) FillShape(n, brush, bounds);
                        var pen = GetPen(cmd.Pen);
                        if (pen != null) StrokeShape(n, pen);
                        break;
                    }
                    case DrawOp.DrawGeometry:
                        if (cmd.RefData is string svg) DrawSvg(svg, GetBrush(cmd.Brush), GetPen(cmd.Pen));
                        break;
                    case DrawOp.DrawText:
                        DrawText(cmd);
                        break;
                    case DrawOp.DrawImage:
                        if (cmd.RefData is ImageSource img) DrawImage(img, cmd.Payload.RectArea);
                        break;
                    case DrawOp.DrawVideo:
                        break; // CanRasterize 已把含视频的图层挡回 WPF
                    case DrawOp.PushClip:
                    {
                        Push();
                        var r = cmd.Payload.RectArea;
                        DeviceBounds(r, out float l, out float t, out float rr, out float b);
                        _s.ClipX0 = Math.Max(_s.ClipX0, PixelMath.CenterCeil(l));
                        _s.ClipY0 = Math.Max(_s.ClipY0, PixelMath.CenterCeil(t));
                        _s.ClipX1 = Math.Min(_s.ClipX1, PixelMath.CenterCeil(rr));
                        _s.ClipY1 = Math.Min(_s.ClipY1, PixelMath.CenterCeil(b));
                        break;
                    }
                    case DrawOp.PushOpacity:
                        Push();
                        _opacity *= Math.Clamp(cmd.Payload.Val1, 0f, 1f);
                        break;
                    case DrawOp.PushTransform:
                        Push();
                        SetMatrix(cmd.Payload.Transform * _m);
                        break;
                    case DrawOp.Pop:
                        Pop();
                        break;
                }
            }
            while (_depth > 0) Pop(); // Push/Pop 不配平时不把状态漏到下一图层

            if (_diag != null) _diag.OnLayerRender(Stopwatch.GetTimestamp() - startTicks, span.Length);
        }

        // ====================================================================
        // BitmapBuffer:业务预栅格化好的像素直接贴(跟 WpfRasterRenderer 一样画在 (0,0, PixelWidth, PixelHeight) DIP)
        // ====================================================================

        public unsafe void Render(BitmapBuffer buffer, RasterSurface surface)
        {
            if (buffer.PixelData == IntPtr.Zero) return;
            Begin(surface);
            DeviceBounds(new HevoRect(0, 0, buffer.PixelWidth, buffer.PixelHeight), out float l, out float t, out float r, out float b);
            surface.BlitScaled((uint*)buffer.PixelData, buffer.PixelWidth, buffer.PixelHeight, buffer.Stride >> 2, l, t, r, b, 255);
        }

        private void Begin(RasterSurface surface)
        {
            _s = surface;
            _depth = 0;
            _opacity = 1f;
            surface.ResetClip();
            SetMatrix(Matrix3x2.CreateScale(surface.Scale));
        }

        private void SetMatrix(Matrix3x2 m)
        {
            _m = m;
            _axisAligned = m.M12 == 0 && m.M21 == 0;
            _lineScale = MathF.Sqrt(MathF.Abs(m.GetDeterminant()));
        }

        private void Push()
        {
            if (_depth == _stack.Length) Array.Resize(ref _stack, _depth * 2);
            ref var st = ref _stack[_depth++];
            st.M = _m;
            st.Opacity = _opacity;
            st.ClipX0 = _s.ClipX0; st.ClipY0 = _s.ClipY0; st.ClipX1 = _s.ClipX1; st.ClipY1 = _s.ClipY1;
        }

        private void Pop()
        {
            if (_depth == 0) return;
            ref var st = ref _stack[--_depth];
            SetMatrix(st.M);
            _opacity = st.Opacity;
            _s.ClipX0 = st.ClipX0; _s.ClipY0 = st.ClipY0; _s.ClipX1 = st.ClipX1; _s.ClipY1 = st.ClipY1;
        }

        // ====================================================================
        // 几何
        // ====================================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector2 Tx(float x, float y) => Vector2.Transform(new Vector2(x, y), _m);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector2 Tx(HevoPoint p) => Vector2.Transform(new Vector2(p.X, p.Y), _m);

        private Span<Vector2> EnsurePts(int n)
        {
            if (_pts.Length < n) _pts = new Vector2[Math.Max(n, _pts.Length * 2)];
            return _pts;
        }

        private void DeviceBounds(HevoRect r, out float l, out float t, out float rr, out float b)
        {
            var p0 = Tx(r.Left, r.Top);
            var p1 = Tx(r.Right, r.Bottom);
            if (_axisAligned)
            {
                l = MathF.Min(p0.X, p1.X); rr = MathF.Max(p0.X, p1.X);
                t = MathF.Min(p0.Y, p1.Y); b = MathF.Max(p0.Y, p1.Y);
                return;
            }
            var p2 = Tx(r.Right, r.Top);
            var p3 = Tx(r.Left, r.Bottom);
            l = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X));
            rr = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
            t = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y));
            b = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));
        }

        private void StrokeLine(HevoPoint a, HevoPoint b, PenInfo pen)
        {
            var da = Tx(a);
            var db = Tx(b);
            float w = pen.Thickness * _lineScale;
            if (w <= 0) return;

            // 快路径:实线水平 / 垂直线 = 一个轴对齐矩形
            if (_axisAligned && pen.Dash == null && (da.Y == db.Y || da.X == db.X))
            {
                float h = w * 0.5f;
                float ext = pen.Cap == StrokeCap.Flat ? 0f : h; // 方 / 圆 / 三角头在 1~2px 线上都等价于外延半线宽
                if (da.Y == db.Y)
                {
                    if (da.X == db.X && pen.Cap == StrokeCap.Flat) return;
                    float l = MathF.Min(da.X, db.X) - ext, r = MathF.Max(da.X, db.X) + ext;
                    if (!MakePaint(pen.Brush, l, da.Y - h, r, da.Y + h, out var paint)) return;
                    _s.FillRect(l, da.Y - h, r, da.Y + h, paint);
                }
                else
                {
                    float t = MathF.Min(da.Y, db.Y) - ext, bt = MathF.Max(da.Y, db.Y) + ext;
                    if (!MakePaint(pen.Brush, da.X - h, t, da.X + h, bt, out var paint)) return;
                    _s.FillRect(da.X - h, t, da.X + h, bt, paint);
                }
                return;
            }

            var dev = EnsurePts(2);
            dev[0] = da; dev[1] = db;
            StrokePath(dev.Slice(0, 2), false, pen);
        }

        private void StrokePath(ReadOnlySpan<Vector2> devPts, bool closed, PenInfo pen)
        {
            float w = pen.Thickness * _lineScale;
            if (w <= 0) return;
            var style = new StrokeStyle(w, pen.Cap, pen.Cap, pen.Join, pen.Dash);
            _filler.Reset();
            _stroker.Stroke(_filler, devPts, closed, style);
            if (_filler.IsEmpty) return;
            Bounds(devPts, out float l, out float t, out float r, out float b);
            if (!MakePaint(pen.Brush, l, t, r, b, out var paint)) return;
            _filler.Fill(_s, paint);
        }

        private static void Bounds(ReadOnlySpan<Vector2> pts, out float l, out float t, out float r, out float b)
        {
            l = t = float.MaxValue; r = b = float.MinValue;
            for (int i = 0; i < pts.Length; i++)
            {
                var p = pts[i];
                if (p.X < l) l = p.X;
                if (p.X > r) r = p.X;
                if (p.Y < t) t = p.Y;
                if (p.Y > b) b = p.Y;
            }
        }

        private void FillRectDip(HevoRect r, in BrushInfo brush)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            if (_axisAligned)
            {
                DeviceBounds(r, out float l, out float t, out float rr, out float b);
                if (MakePaint(brush, l, t, rr, b, out var paint)) _s.FillRect(l, t, rr, b, paint);
                return;
            }
            _shape[0] = new Vector2(r.Left, r.Top); _shape[1] = new Vector2(r.Right, r.Top);
            _shape[2] = new Vector2(r.Right, r.Bottom); _shape[3] = new Vector2(r.Left, r.Bottom);
            FillShape(4, brush, r);
        }

        private void StrokeRectDip(HevoRect r, PenInfo pen)
        {
            float w = pen.Thickness * _lineScale;
            if (w <= 0) return;
            if (_axisAligned && pen.Dash == null)
            {
                // 外框减内框拆成上下左右四条互不重叠的带,尖角(Miter)跟 WPF 矩形描边一致
                DeviceBounds(r, out float l, out float t, out float rr, out float b);
                float h = w * 0.5f;
                if (!MakePaint(pen.Brush, l - h, t - h, rr + h, b + h, out var paint)) return;
                if (b - t <= w || rr - l <= w)
                {
                    _s.FillRect(l - h, t - h, rr + h, b + h, paint);
                    return;
                }
                _s.FillRect(l - h, t - h, rr + h, t + h, paint);
                _s.FillRect(l - h, b - h, rr + h, b + h, paint);
                _s.FillRect(l - h, t + h, l + h, b - h, paint);
                _s.FillRect(rr - h, t + h, rr + h, b - h, paint);
                return;
            }
            _shape[0] = new Vector2(r.Left, r.Top); _shape[1] = new Vector2(r.Right, r.Top);
            _shape[2] = new Vector2(r.Right, r.Bottom); _shape[3] = new Vector2(r.Left, r.Bottom);
            StrokeShape(4, pen);
        }

        /// <summary>填充 _shape[0..n)(DIP 闭合多边形)。</summary>
        private void FillShape(int n, in BrushInfo brush, HevoRect boundsDip)
        {
            var dev = EnsurePts(n);
            for (int i = 0; i < n; i++) dev[i] = Vector2.Transform(_shape[i], _m);
            _filler.Reset();
            _filler.AddPolygon(dev.Slice(0, n));
            DeviceBounds(boundsDip, out float l, out float t, out float r, out float b);
            if (MakePaint(brush, l, t, r, b, out var paint)) _filler.Fill(_s, paint);
        }

        /// <summary>描 _shape[0..n)(DIP 闭合多边形)的边。</summary>
        private void StrokeShape(int n, PenInfo pen)
        {
            var dev = EnsurePts(n);
            for (int i = 0; i < n; i++) dev[i] = Vector2.Transform(_shape[i], _m);
            StrokePath(dev.Slice(0, n), true, pen);
        }

        private int BuildEllipse(float cx, float cy, float rx, float ry)
        {
            int n = Math.Min(Stroker.EllipseSegments(rx * _lineScale, ry * _lineScale), _shape.Length);
            for (int i = 0; i < n; i++)
            {
                float t = i * (MathF.PI * 2f / n);
                _shape[i] = new Vector2(cx + rx * MathF.Cos(t), cy + ry * MathF.Sin(t));
            }
            return n;
        }

        private int BuildRoundedRect(HevoRect r, float rx, float ry)
        {
            rx = Math.Clamp(rx, 0, r.Width / 2);
            ry = Math.Clamp(ry, 0, r.Height / 2);
            if (rx <= 0 || ry <= 0)
            {
                _shape[0] = new Vector2(r.Left, r.Top); _shape[1] = new Vector2(r.Right, r.Top);
                _shape[2] = new Vector2(r.Right, r.Bottom); _shape[3] = new Vector2(r.Left, r.Bottom);
                return 4;
            }
            int k = Math.Clamp((int)MathF.Ceiling((rx + ry) * _lineScale / 2f), 2, 16); // 每个角的段数
            int n = 0;
            // 右上 → 右下 → 左下 → 左上,每个角 k+1 个点
            Corner(r.Right - rx, r.Top + ry, -MathF.PI / 2, ref n);
            Corner(r.Right - rx, r.Bottom - ry, 0, ref n);
            Corner(r.Left + rx, r.Bottom - ry, MathF.PI / 2, ref n);
            Corner(r.Left + rx, r.Top + ry, MathF.PI, ref n);
            return n;

            void Corner(float cx, float cy, float a0, ref int idx)
            {
                for (int i = 0; i <= k; i++)
                {
                    float a = a0 + i * (MathF.PI / 2 / k);
                    _shape[idx++] = new Vector2(cx + rx * MathF.Cos(a), cy + ry * MathF.Sin(a));
                }
            }
        }

        private void DrawSvg(string svg, in BrushInfo brush, PenInfo? pen)
        {
            if (!brush.Valid && pen == null) return;
            var geo = GetGeometry(svg);
            if (geo == null) return;

            if (brush.Valid)
            {
                _filler.Reset();
                float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
                for (int f = 0; f < geo.Figures.Length; f++)
                {
                    if (!geo.Filled[f]) continue;
                    var fig = geo.Figures[f];
                    var dev = EnsurePts(fig.Length);
                    for (int i = 0; i < fig.Length; i++) dev[i] = Vector2.Transform(fig[i], _m);
                    _filler.AddPolygon(dev.Slice(0, fig.Length));
                    Bounds(dev.Slice(0, fig.Length), out float fl, out float ft, out float fr, out float fb);
                    l = MathF.Min(l, fl); t = MathF.Min(t, ft); r = MathF.Max(r, fr); b = MathF.Max(b, fb);
                }
                if (!_filler.IsEmpty && MakePaint(brush, l, t, r, b, out var paint)) _filler.Fill(_s, paint, geo.EvenOdd);
            }
            if (pen != null)
            {
                for (int f = 0; f < geo.Figures.Length; f++)
                {
                    var fig = geo.Figures[f];
                    var dev = EnsurePts(fig.Length);
                    for (int i = 0; i < fig.Length; i++) dev[i] = Vector2.Transform(fig[i], _m);
                    StrokePath(dev.Slice(0, fig.Length), geo.Closed[f], pen);
                }
            }
        }

        private FlatGeometry? GetGeometry(string svg)
        {
            if (_geometries.TryGetValue(svg, out var g)) return g;
            if (_geometries.Count >= MaxGeometries) _geometries.Clear();
            try
            {
                var flat = Geometry.Parse(svg).GetFlattenedPathGeometry(0.1, ToleranceType.Absolute);
                var figs = new List<Vector2[]>();
                var closed = new List<bool>();
                var filled = new List<bool>();
                var buf = new List<Vector2>();
                foreach (var fig in flat.Figures)
                {
                    buf.Clear();
                    buf.Add(new Vector2((float)fig.StartPoint.X, (float)fig.StartPoint.Y));
                    foreach (var seg in fig.Segments)
                    {
                        if (seg is LineSegment ls) buf.Add(new Vector2((float)ls.Point.X, (float)ls.Point.Y));
                        else if (seg is PolyLineSegment pls)
                            foreach (var p in pls.Points) buf.Add(new Vector2((float)p.X, (float)p.Y));
                    }
                    figs.Add(buf.ToArray());
                    closed.Add(fig.IsClosed);
                    filled.Add(fig.IsFilled);
                }
                g = new FlatGeometry
                {
                    Figures = figs.ToArray(), Closed = closed.ToArray(), Filled = filled.ToArray(),
                    EvenOdd = flat.FillRule == FillRule.EvenOdd,
                };
            }
            catch (FormatException)
            {
                g = new FlatGeometry { Figures = Array.Empty<Vector2[]>(), Closed = Array.Empty<bool>(), Filled = Array.Empty<bool>() };
            }
            _geometries[svg] = g;
            return g;
        }

        // ====================================================================
        // 文字 / 图片
        // ====================================================================

        private void DrawText(in DrawCmd cmd)
        {
            var brush = GetBrush(cmd.Brush);
            if (!brush.Valid || cmd.RefData is not TextInfo info) return;
            string text = WpfRenderRegistry.ResolveString(info.Text);
            float fontSize = cmd.Payload.Val1;
            if (!_text.Measure(text, info.Typeface, fontSize, _lineScale, out var m)) return;

            var layout = TextLayoutHelper.Compute(
                cmd.Payload.P1.X, cmd.Payload.P1.Y, m.Width, m.Height,
                info.PaddingX, info.PaddingY, info.AlignX, info.AlignY);

            if (info.BgBrush != null || info.BorderPen != null)
            {
                var box = new HevoRect(layout.BoxX, layout.BoxY, layout.BoxWidth, layout.BoxHeight);
                var bg = GetBrush(info.BgBrush);
                if (bg.Valid) FillRectDip(box, bg);
                var border = GetPen(info.BorderPen);
                if (border != null) StrokeRectDip(PixelSnap.InsideStroke(box, border.Thickness), border);
            }

            var origin = Tx(layout.TextX, layout.TextY);
            uint color = Premul(brush.C0, brush.Opacity);
            if (color != 0) _text.Draw(_s, text, m, origin.X, origin.Y, color);
        }

        private unsafe void DrawImage(ImageSource src, HevoRect rect)
        {
            var img = GetImage(src);
            if (img == null) return;
            DeviceBounds(rect, out float l, out float t, out float r, out float b);
            uint op = (uint)Math.Clamp((int)(_opacity * 255f + 0.5f), 0, 255);
            fixed (int* p = img.Pixels)
                _s.BlitScaled((uint*)p, img.W, img.H, img.W, l, t, r, b, op);
        }

        private PixelImage? GetImage(ImageSource src)
        {
            if (_images.TryGetValue(src, out var img)) return img;
            BitmapSource? bs = src as BitmapSource;
            if (bs == null)
            {
                int w = (int)Math.Ceiling(src.Width), h = (int)Math.Ceiling(src.Height);
                if (w <= 0 || h <= 0) return null;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, w, h));
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                bs = rtb;
            }
            else if (bs.Format != PixelFormats.Pbgra32)
            {
                bs = new FormatConvertedBitmap(bs, PixelFormats.Pbgra32, null, 0);
            }
            var px = new int[bs.PixelWidth * bs.PixelHeight];
            bs.CopyPixels(px, bs.PixelWidth * 4, 0);
            img = new PixelImage { Pixels = px, W = bs.PixelWidth, H = bs.PixelHeight };
            _images.AddOrUpdate(src, img);
            return img;
        }

        // ====================================================================
        // 画刷 / 画笔
        // ====================================================================

        private static uint Premul(Color c, float opacity) => PixelMath.Premultiply(c.A, c.R, c.G, c.B, opacity);

        /// <summary>按图元设备包围盒把画刷换成像素颜色(渐变的相对坐标以图元包围盒为准,跟 WPF 默认 MappingMode 一致)。</summary>
        private bool MakePaint(in BrushInfo bi, float l, float t, float r, float b, out RasterPaint paint)
        {
            float op = bi.Opacity * _opacity;
            if (!bi.Gradient)
            {
                paint = new RasterPaint(Premul(bi.C0, op));
                return paint.Color != 0;
            }
            Vector2 s, e;
            if (bi.Relative)
            {
                s = new Vector2(l + (float)bi.Start.X * (r - l), t + (float)bi.Start.Y * (b - t));
                e = new Vector2(l + (float)bi.End.X * (r - l), t + (float)bi.End.Y * (b - t));
            }
            else
            {
                s = Tx((float)bi.Start.X, (float)bi.Start.Y);
                e = Tx((float)bi.End.X, (float)bi.End.Y);
            }
            var d = e - s;
            float len2 = d.LengthSquared();
            if (len2 < 1e-6f) d = Vector2.Zero; else d /= len2;
            paint = new RasterPaint(Premul(bi.C0, op), Premul(bi.C1, op), s.X, s.Y, d.X, d.Y);
            return paint.IsVisible;
        }

        private BrushInfo GetBrush(IHevoBrush? desc)
        {
            if (desc == null) return default;
            if (ReferenceEquals(desc, _lastBrushDesc)) { _diag?.OnPaintCacheHit(); return _lastBrush; }
            if (!_brushes.TryGetValue(desc, out var bi))
            {
                _diag?.OnPaintCacheMiss();
                if (_brushes.Count >= MaxPaints) _brushes.Clear();
                bi = Resolve(WpfRenderRegistry.CreateBrush(desc));
                _brushes[desc] = bi;
            }
            else _diag?.OnPaintCacheHit();
            _lastBrushDesc = desc; _lastBrush = bi;
            return bi;
        }

        private static BrushInfo Resolve(Brush? brush)
        {
            switch (brush)
            {
                case SolidColorBrush sc:
                    return new BrushInfo { Valid = true, C0 = sc.Color, Opacity = (float)sc.Opacity };
                case LinearGradientBrush lg when lg.GradientStops.Count > 0:
                {
                    GradientStop first = lg.GradientStops[0], last = lg.GradientStops[0];
                    foreach (var gs in lg.GradientStops)
                    {
                        if (gs.Offset < first.Offset) first = gs;
                        if (gs.Offset > last.Offset) last = gs;
                    }
                    return new BrushInfo
                    {
                        Valid = true, Gradient = true, C0 = first.Color, C1 = last.Color,
                        Start = lg.StartPoint, End = lg.EndPoint, Opacity = (float)lg.Opacity,
                        Relative = lg.MappingMode == BrushMappingMode.RelativeToBoundingBox,
                    };
                }
                case GradientBrush gb when gb.GradientStops.Count > 0:
                    // 径向等其它渐变:取首个色标当纯色
                    return new BrushInfo { Valid = true, C0 = gb.GradientStops[0].Color, Opacity = (float)gb.Opacity };
                default:
                    return default; // ImageBrush / VisualBrush 等不支持,跳过
            }
        }

        private PenInfo? GetPen(HevoPen? desc)
        {
            if (desc == null) return null;
            if (ReferenceEquals(desc, _lastPenDesc)) { _diag?.OnPaintCacheHit(); return _lastPen; }
            if (!_pens.TryGetValue(desc, out var pi))
            {
                _diag?.OnPaintCacheMiss();
                if (_pens.Count >= MaxPaints) _pens.Clear();
                var brush = GetBrush(desc.Brush);
                pi = brush.Valid && desc.Thickness > 0
                    ? new PenInfo
                    {
                        Brush = brush,
                        Thickness = (float)desc.Thickness,
                        Cap = (StrokeCap)Math.Clamp(desc.LineCap, 0, 3),
                        Join = (StrokeJoin)Math.Clamp(desc.LineJoin, 0, 2),
                        Dash = desc.DashArray is { Length: > 0 } ? desc.DashArray : null,
                    }
                    : null;
                _pens[desc] = pi;
            }
            else _diag?.OnPaintCacheHit();
            _lastPenDesc = desc; _lastPen = pi;
            return pi;
        }

        public void ClearCaches()
        {
            _brushes.Clear();
            _pens.Clear();
            _geometries.Clear();
            _text.Clear();
            _lastBrushDesc = null; _lastPenDesc = null; _lastPen = null;
        }
    }
}
