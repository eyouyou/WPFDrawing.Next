using System.Numerics;
using System.Runtime.InteropServices;

namespace Hevo.Charting.Renderers
{
    /// <summary>
    /// 16 个原子绘图操作指令。
    /// 涵盖了从基础几何、批量优化到媒体流的所有能力。
    /// </summary>
    public enum DrawOp : byte
    {
        // --- 基础几何 (7个) ---
        DrawLine = 1,
        DrawPolyline,
        DrawLineSegments,
        DrawRectangle,
        DrawRoundedRectangle,
        DrawEllipse,
        DrawGeometry,         // 复杂矢量路径 (如 SVG Data)

        // --- 文本与媒体 (3个) ---
        DrawText,
        DrawImage,
        DrawVideo,

        // --- 批量优化 (1个) ---
        DrawRectangles,       // 极速绘制成千上万个矩形

        // --- 状态堆栈 (5个) ---
        PushClip,
        PushOpacity,
        PushTransform,
        PushGuidelineSet,     // 像素对齐利器
        Pop
    }

    /// <summary>
    /// 💥 终极文本元数据：自带背景排版能力的原子结构
    /// </summary>
    public readonly struct TextInfo
    {
        public readonly IHevoString Text;
        public readonly IHevoTypeface Typeface;
        public readonly TextAlignX AlignX;
        public readonly TextAlignY AlignY;

        // 💥 新增背景能力！
        public readonly IHevoBrush? BgBrush;
        public readonly HevoPen? BorderPen;
        public readonly float PaddingX;
        public readonly float PaddingY;

        public TextInfo(IHevoString text, IHevoTypeface typeface, TextAlignX alignX, TextAlignY alignY,
                        IHevoBrush? bgBrush = null, HevoPen? borderPen = null, float padX = 0, float padY = 0)
        {
            Text = text; Typeface = typeface; AlignX = alignX; AlignY = alignY;
            BgBrush = bgBrush; BorderPen = borderPen; PaddingX = padX; PaddingY = padY;
        }
    }

    // ==========================================
    // 💥 核心黑科技：24 字节 0 GC 联合体内存载荷！
    // (因为全线换成了 float，原来 48 字节的载荷直接砍半，内存占用暴降 50%！)
    // ==========================================
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct DrawPayload
    {
        // --- 内存字段映射 ---
        // HevoPoint 由两个 float 组成 (8 字节)
        [FieldOffset(0)] public HevoPoint P1;             // 0-7 字节
        [FieldOffset(8)] public HevoPoint P2;             // 8-15 字节
        [FieldOffset(16)] public float Val1;              // 16-19 字节
        [FieldOffset(20)] public float Val2;              // 20-23 字节

        // HevoRect 由四个 float 组成 (16 字节)
        [FieldOffset(0)] public HevoRect RectArea;        // 0-15 字节 (完美覆盖 P1, P2)

        // Matrix3x2 由六个 float 组成 (24 字节)
        [FieldOffset(0)] public Matrix3x2 Transform;      // 0-23 字节 (完美覆盖所有内存槽)

        // --- 💥 必须提供这一组构造函数，否则工厂方法会报 CS1729 ---

        // 给 Line 用的 (p1, p2)
        public DrawPayload(HevoPoint p1, HevoPoint p2) : this() { P1 = p1; P2 = p2; }

        // 给 RoundedRect 用的 (rect, rx, ry)
        public DrawPayload(HevoRect rect, float v1 = 0, float v2 = 0) : this() { RectArea = rect; Val1 = v1; Val2 = v2; }

        // 给 Ellipse (center, rx, ry) 或 Text (origin, fontSize, 0) 用的
        public DrawPayload(HevoPoint p1, float v1, float v2) : this() { P1 = p1; Val1 = v1; Val2 = v2; }

        // 给 Transform 用的 (System.Numerics.Matrix3x2)
        public DrawPayload(Matrix3x2 matrix) : this() { Transform = matrix; }

        // 给 Opacity 等单数值指令用的
        public DrawPayload(float v1) : this() { Val1 = v1; }
    }

    /// <summary>
    /// 绘图指令包：采用 readonly struct 以实现 0 GC 录制。
    /// 所有 Arg 槽位仅存储：Color, HevoPoint, HevoRect, Matrix3x2, string 或 IList。
    /// </summary>
    public readonly struct DrawCmd
    {
        public DrawOp Op { get; }
        public HevoPen? Pen { get; }
        public IHevoBrush? Brush { get; }
        public DrawPayload Payload { get; }
        public object? RefData { get; } // 引用类型走这里，杜绝装箱

        internal DrawCmd(DrawOp op, HevoPen? pen, IHevoBrush? brush, DrawPayload payload, object? refData = null)
        {
            Op = op; Pen = pen; Brush = brush; Payload = payload; RefData = refData;
        }
    }

    // 泛化接收接口 (Sink)
    // 这是一个漏斗，所有的 API 最终都汇聚于此
    public interface IDrawingSink
    {
        void Push(DrawCmd cmd);
    }
}
