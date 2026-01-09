using System.Numerics;
using System.Windows;
using System.Windows.Media;
using Windows.Devices.Geolocation;

namespace Hevo.Charting.Renderers
{
    public readonly struct DrawingStateScope : IDisposable
    {
        private readonly IDrawingSink _sink;

        public DrawingStateScope(IDrawingSink sink)
        {
            _sink = sink;
        }

        public void Dispose()
        {
            // 作用域结束，自动发射 Pop 指令！
            _sink.Push(new DrawCmd(DrawOp.Pop, null, null, default));
        }
    }

    /// <summary>
    /// 💥 绘图指令一站式枢纽 (业务层唯一合法的绘图入口)。
    /// 职责：将高层的业务绘图请求，即时包装为“轻量级纯数据指令(DrawCmd)”并送入 Sink 录制。
    /// 优势：
    /// 1. [极致性能] 合并工厂与推送逻辑，录制期 0-GC，无任何 Middleman 开销。
    /// 2. [完全解耦] 业务层只需关心“画什么”，无 WPF 依赖，全面采用 float (HevoPoint/HevoRect)。
    /// 3. [线程安全] 生成的指令包为只读结构体体，支持后台线程录制，UI 线程异步回放。
    /// </summary>
    public static class DrawingExtensions
    {
        // ==========================================
        // 1. 线条与路径 (Line & Path)
        // ==========================================

        /// <summary>
        /// 绘制单条直线。
        /// </summary>
        /// <param name="pen">画笔描述符：包含颜色(IHevoBrush)、粗细及线型。若为 null 则不绘制描边。</param>
        /// <param name="p1">起点坐标 (相对画布左上角)。</param>
        /// <param name="p2">终点坐标 (相对画布左上角)。</param>
        public static void DrawLine(this IDrawingSink sink, HevoPen? pen, HevoPoint p1, HevoPoint p2)
            => sink.Push(new DrawCmd(DrawOp.DrawLine, pen, null, new DrawPayload(p1, p2)));

        /// <summary>
        /// 绘制连续折线。
        /// </summary>
        /// <param name="pen">画笔描述符。建议使用预冻结的静态实例以优化性能。</param>
        /// <param name="points">顶点集合：顺序连接的坐标列表。内部直接引用该对象，请确保录制后不被外部清空。</param>
        public static void DrawPolyline(this IDrawingSink sink, HevoPen? pen, IList<HevoPoint> points)
            => sink.Push(new DrawCmd(DrawOp.DrawPolyline, pen, null, default, points));

        /// <summary>
        /// 绘制不连续线段组 (批量连线)。
        /// </summary>
        /// <param name="points">坐标列表：按照 (p1, p2), (p3, p4)... 的配对逻辑绘制。列表总数应为偶数。</param>
        public static void DrawLineSegments(this IDrawingSink sink, HevoPen pen, IList<HevoPoint> points)
            => sink.Push(new DrawCmd(DrawOp.DrawLineSegments, pen, null, default, points));

        /// <summary>
        /// 绘制复杂矢量路径 (SVG 格式支持)。
        /// </summary>
        /// <param name="brush">填充笔刷：支持静态色或资源 Key。若为 null 则不填充内部。</param>
        /// <param name="pen">描边画笔：若为 null 则不绘制轮廓。</param>
        /// <param name="svgPath">标准 SVG 路径字符串 (如 "M 0,0 L 100,100 Z")。</param>
        public static void DrawGeometry(this IDrawingSink sink, IHevoBrush? brush, HevoPen? pen, string svgPath)
            => sink.Push(new DrawCmd(DrawOp.DrawGeometry, pen, brush, default, svgPath));

        // ==========================================
        // 2. 基础几何 (Basic Shapes)
        // ==========================================

        /// <summary>
        /// 绘制矩形。
        /// </summary>
        /// <param name="rect">矩形区域定义。</param>
        public static void DrawRectangle(this IDrawingSink sink, IHevoBrush? brush, HevoPen? pen, HevoRect rect)
            => sink.Push(new DrawCmd(DrawOp.DrawRectangle, pen, brush, new DrawPayload(rect)));

        /// <summary>
        /// 绘制圆角矩形。
        /// </summary>
        /// <param name="rx">X 轴圆角半径。</param>
        /// <param name="ry">Y 轴圆角半径。</param>
        public static void DrawRoundedRectangle(this IDrawingSink sink, IHevoBrush? brush, HevoPen? pen, HevoRect rect, float rx, float ry)
            => sink.Push(new DrawCmd(DrawOp.DrawRoundedRectangle, pen, brush, new DrawPayload(rect, rx, ry)));

        /// <summary>
        /// 绘制椭圆 (或圆形)。
        /// </summary>
        /// <param name="center">圆心坐标。</param>
        /// <param name="rx">水平半径。</param>
        /// <param name="ry">垂直半径 (rx=ry 时即为圆形)。</param>
        public static void DrawEllipse(this IDrawingSink sink, IHevoBrush? brush, HevoPen? pen, HevoPoint center, float rx, float ry)
            => sink.Push(new DrawCmd(DrawOp.DrawEllipse, pen, brush, new DrawPayload(center, rx, ry)));

        /// <summary>
        /// 💥 批量绘制矩形 (工业级优化入口)。
        /// 适用于成千上万个矩形的极速渲染（如密集成交量柱状图、热力图）。
        /// 渲染器内部会针对此类指令进行底层批处理优化。
        /// </summary>
        /// <param name="rects">待绘制的矩形集合。内部直接引用该对象以降低内存分配风险。</param>
        public static void DrawRectangles(this IDrawingSink sink, IHevoBrush? brush, HevoPen? pen, IList<HevoRect> rects)
            => sink.Push(new DrawCmd(DrawOp.DrawRectangles, pen, brush, default, rects));

        // ==========================================
        // 3. 文本处理 (Text - 支持 Late Binding)
        // ==========================================

        /// <summary>
        /// 💥 [终极入口] 绘制多态文本 (自带自适应背景框支持！)
        /// </summary>
        public static void DrawText(
            this IDrawingSink sink, IHevoString text, IHevoTypeface typeface, IHevoBrush brush, float fontSize, HevoPoint origin,
            TextAlignX alignX = TextAlignX.Left, TextAlignY alignY = TextAlignY.Top,
            IHevoBrush? bgBrush = null, HevoPen? borderPen = null, float paddingX = 0, float paddingY = 0)
        {
            var textInfo = new TextInfo(text, typeface, alignX, alignY, bgBrush, borderPen, paddingX, paddingY);
            sink.Push(new DrawCmd(DrawOp.DrawText, null, brush, new DrawPayload(origin, fontSize, 0), textInfo));
        }

        /// <summary>
        /// 💥 [便利重载] 绘制原生字符串文本。
        /// </summary>
        public static void DrawText(
            this IDrawingSink sink, string text, IHevoTypeface typeface, IHevoBrush brush, float fontSize, HevoPoint origin,
            TextAlignX alignX = TextAlignX.Left, TextAlignY alignY = TextAlignY.Top,
            IHevoBrush? bgBrush = null, HevoPen? borderPen = null, float paddingX = 0, float paddingY = 0)
        {
            sink.DrawText(new HevoLiteralString(text), typeface, brush, fontSize, origin, alignX, alignY, bgBrush, borderPen, paddingX, paddingY);
        }

        // ==========================================
        // 4. 媒体与状态堆栈 (Media & State)
        // ==========================================

        /// <summary>
        /// 绘制图片。
        /// </summary>
        /// <param name="imageSource">图片源：可以是 Uri 字符串、资源 Key 或平台原生图像对象引用。</param>
        public static void DrawImage(this IDrawingSink sink, object imageSource, HevoRect rect)
            => sink.Push(new DrawCmd(DrawOp.DrawImage, null, null, new DrawPayload(rect), imageSource));

        /// <summary>
        /// 推入剪裁区域。
        /// 调用后，后续所有的绘图操作将被限制在 clipRect 内部。必须成对调用 Pop()。
        /// </summary>
        public static DrawingStateScope PushClip(this IDrawingSink sink, HevoRect clipRect)
        {
            sink.Push(new DrawCmd(DrawOp.PushClip, null, null, new DrawPayload(clipRect)));
            return new DrawingStateScope(sink);
        }

        /// <summary>
        /// 推入不透明度层。
        /// 调用后，后续所有操作将具有全局透明度。必须成对调用 Pop()。
        /// </summary>
        /// <param name="opacity">0.0 (全透) 到 1.0 (不透)。</param>
        public static DrawingStateScope PushOpacity(this IDrawingSink sink, float opacity)
        {
            sink.Push(new DrawCmd(DrawOp.PushOpacity, null, null, new DrawPayload(opacity)));
            return new DrawingStateScope(sink);
        }

        /// <summary>
        /// 推入坐标系变换 (平移、旋转、缩放)。
        /// 必须成对调用 Pop()。
        /// </summary>
        /// <param name="m">System.Numerics 标准硬件加速矩阵。</param>
        public static DrawingStateScope PushTransform(this IDrawingSink sink, Matrix3x2 m)
        {
            sink.Push(new DrawCmd(DrawOp.PushTransform, null, null, new DrawPayload(m)));
            return new DrawingStateScope(sink);
        }

        /// <summary>
        /// 开启像素级防模糊伞。
        /// 奇数线宽自动偏移半像素，实现 Crisp Edges 锐利边缘。
        /// </summary>
        public static DrawingStateScope PushPixelSnapping(this IDrawingSink sink, float strokeThickness = 1.0f)
        {
            float offset = (strokeThickness % 2 != 0) ? 0.5f : 0.0f;
            sink.Push(new DrawCmd(DrawOp.PushGuidelineSet, null, null, new DrawPayload(offset)));
            return new DrawingStateScope(sink);
        }

        /// <summary>
        /// 弹出最近一次推入的状态堆栈 (Clip, Opacity 或 Transform)。
        /// </summary>
        public static void Pop(this IDrawingSink sink)
            => sink.Push(new DrawCmd(DrawOp.Pop, null, null, default));
    }
}