using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace Hevo.Charting
{
    /// <summary>
    /// 💥 极致轻量的 float 结构体，替代 WPF 的 Point
    /// </summary>
    /// <param name="X"></param>
    /// <param name="Y"></param>
    public readonly record struct HevoPoint(float X, float Y);

    /// <summary>
    /// 💥 极致轻量的跨平台矩形 (基于 float)。
    /// 自带所有常用的空间数学算法，彻底干掉 System.Windows.Rect。
    /// </summary>
    public readonly record struct HevoRect(float X, float Y, float Width, float Height)
    {
        public static readonly HevoRect Empty = new HevoRect(0, 0, 0, 0);

        public float Left => X;
        public float Top => Y;
        public float Right => X + Width;
        public float Bottom => Y + Height;

        public HevoPoint TopLeft => new HevoPoint(Left, Top);
        public HevoPoint TopRight => new HevoPoint(Right, Top);
        public HevoPoint BottomLeft => new HevoPoint(Left, Bottom);
        public HevoPoint BottomRight => new HevoPoint(Right, Bottom);

        public HevoPoint Center => new HevoPoint(X + Width / 2f, Y + Height / 2f);
        public bool IsEmpty => Width <= 0 || Height <= 0;

        // ==========================================
        // 💥 核心空间数学算法
        // ==========================================

        /// <summary> 判断点是否在矩形内 </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(HevoPoint point)
            => point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;

        /// <summary> 判断另一个矩形是否完全被包含 </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(HevoRect rect)
            => rect.Left >= Left && rect.Right <= Right && rect.Top >= Top && rect.Bottom <= Bottom;

        /// <summary> 矩形偏移 (平移) </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public HevoRect Offset(float dx, float dy)
            => new HevoRect(X + dx, Y + dy, Width, Height);

        /// <summary> 矩形向外/向内膨胀 (如计算边框的外扩) </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public HevoRect Inflate(float dw, float dh)
            => new HevoRect(X - dw, Y - dh, Width + dw * 2f, Height + dh * 2f);

        /// <summary>
        /// 💥 求两个矩形的重叠交集 (极度高频使用的算法)
        /// 如果没有交集，返回 HevoRect.Empty
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public HevoRect Intersect(HevoRect rect)
        {
            float newLeft = Math.Max(Left, rect.Left);
            float newTop = Math.Max(Top, rect.Top);
            float newRight = Math.Min(Right, rect.Right);
            float newBottom = Math.Min(Bottom, rect.Bottom);

            if (newRight >= newLeft && newBottom >= newTop)
                return new HevoRect(newLeft, newTop, newRight - newLeft, newBottom - newTop);

            return Empty;
        }
    }

    public enum TextAlignX : byte { Left, Center, Right }
    public enum TextAlignY : byte { Top, Center, Bottom }
    // ==========================================
    // 💥 跨平台文本抽象契约
    // ==========================================
    public interface IHevoString { }

    /// <summary>
    /// 静态字面量文本
    /// </summary>
    public record HevoLiteralString(string Text) : IHevoString;

    /// <summary>
    /// 动态多语言资源文本 (后期绑定)
    /// </summary>
    public record HevoResourceString(string ResourceKey) : IHevoString;

    /// <summary>
    /// 笔刷基类接口
    /// </summary>
    public interface IHevoBrush { }

    /// <summary>
    /// 纯色笔刷
    /// </summary>
    /// <param name="Color"></param>
    public record HevoSolidBrush(Color Color) : IHevoBrush;

    /// <summary>
    /// 线性渐变笔刷
    /// </summary>
    /// <param name="StartColor"></param>
    /// <param name="EndColor"></param>
    /// <param name="StartPoint"></param>
    /// <param name="EndPoint"></param>
    public record HevoLinearGradientBrush(
        Color StartColor,
        Color EndColor,
        Point StartPoint,
        Point EndPoint) : IHevoBrush;

    /// <summary>
    /// 💥 主题资源笔刷 (后期绑定 / Late Binding)
    /// 它的本质是一个令牌(Token)，具体的渲染后端负责将其翻译为真正的动态画刷
    /// </summary>
    public record HevoResourceBrush(string ResourceKey) : IHevoBrush;

    /// <summary>
    /// 画笔描述符 (支持虚线、端点样式)
    /// </summary>
    /// <param name="Brush"></param>
    /// <param name="Thickness"></param>
    /// <param name="DashArray"></param>
    /// <param name="LineCap"></param>
    /// <param name="LineJoin"></param>
    public record HevoPen(
        IHevoBrush Brush,
        double Thickness,
        double[]? DashArray = null, // 传 null 就是实线
        int LineCap = 0,           // 对应 Flat/Round/Square
        int LineJoin = 0           // 对应 Miter/Bevel/Round
    );

    /// <summary>
    /// 跨平台字体排版描述符
    /// </summary>
    public interface IHevoTypeface { }

    public record HevoTypeface(string FontFamily, int FontWeight = 400, bool IsItalic = false) : IHevoTypeface;

    // 💥 新增：资源字体令牌 (将从平台的资源池中解析真正的 FontFamily)
    public record HevoResourceTypeface(string ResourceKey, int FontWeight = 400, bool IsItalic = false) : IHevoTypeface;
    /// <summary>
    /// 💥 终极 WPF 渲染策略注册表 (OCP 开闭原则的极致体现)
    /// 职责：
    /// 1. 为底层 Renderer 提供无 switch 的 O(1) 资源实例化与解析。
    /// 2. 为 UI 控件提供大一统的 DependencyProperty 绑定网关。
    /// </summary>
    public static class WpfRenderRegistry
    {
        // 💥 策略 1：供底层 Renderer 调用的工厂 (产出 Brush 实例)
        private static readonly Dictionary<Type, Func<IHevoBrush, Brush?>> _brushFactories = new();

        // 💥 策略 2：供底层 Renderer 调用的解析器 (将多态意图化为物理 string)
        private static readonly Dictionary<Type, Func<IHevoString, string>> _stringResolvers = new();

        // 💥 策略 3：供 UI 逻辑使用的全能绑定网关 (抹平所有抽象接口差异)
        private static readonly Dictionary<Type, Action<FrameworkElement, DependencyProperty, object>> _universalBinders = new();

        static WpfRenderRegistry()
        {
            // ==========================================
            // 1. 画刷策略注册
            // ==========================================
            RegisterBrush<HevoSolidBrush>(
                factory: b => new SolidColorBrush(b.Color),
                binder: (el, dp, b) => {
                    var brush = new SolidColorBrush(b.Color);
                    brush.Freeze(); // 性能优化：静态资源强制冻结
                    el.SetValue(dp, brush);
                });

            RegisterBrush<HevoResourceBrush>(
                factory: b => Application.Current.TryFindResource(b.ResourceKey) as Brush ?? new SolidColorBrush(Colors.Magenta),
                binder: (el, dp, b) => el.SetResourceReference(dp, b.ResourceKey));

            RegisterBrush<HevoLinearGradientBrush>(
                factory: b => new LinearGradientBrush(b.StartColor, b.EndColor, b.StartPoint, b.EndPoint),
                binder: (el, dp, b) => {
                    var brush = new LinearGradientBrush(b.StartColor, b.EndColor, b.StartPoint, b.EndPoint);
                    brush.Freeze();
                    el.SetValue(dp, brush);
                });

            // ==========================================
            // 2. 文本策略注册
            // ==========================================
            RegisterString<HevoLiteralString>(
                resolver: s => s.Text, // 给底层的：直接返原文
                binder: (el, dp, s) => el.SetValue(dp, s.Text) // 给 UI 的：静态赋值
            );

            RegisterString<HevoResourceString>(
                resolver: s => Application.Current.TryFindResource(s.ResourceKey) as string ?? s.ResourceKey, // 给底层的：去资源字典里捞翻译
                binder: (el, dp, s) => el.SetResourceReference(dp, s.ResourceKey) // 给 UI 的：挂载动态资源
            );
        }

        // --- 泛型注册入口 (供业务扩展) ---

        public static void RegisterBrush<TBrush>(Func<TBrush, Brush?> factory, Action<FrameworkElement, DependencyProperty, TBrush> binder)
            where TBrush : IHevoBrush
        {
            _brushFactories[typeof(TBrush)] = b => factory((TBrush)b);
            _universalBinders[typeof(TBrush)] = (el, dp, b) => binder(el, dp, (TBrush)b);
        }

        public static void RegisterString<TString>(Func<TString, string> resolver, Action<FrameworkElement, DependencyProperty, TString> binder)
            where TString : IHevoString
        {
            _stringResolvers[typeof(TString)] = s => resolver((TString)s);
            _universalBinders[typeof(TString)] = (el, dp, s) => binder(el, dp, (TString)s);
        }

        // --- 极速提取 API ---

        /// <summary>
        /// [底层专用] 查表获取 WPF 画刷实例
        /// </summary>
        public static Brush? CreateBrush(IHevoBrush brush)
        {
            return _brushFactories.TryGetValue(brush.GetType(), out var factory) ? factory(brush) : null;
        }

        /// <summary>
        /// [底层专用] 查表解析多态文本
        /// </summary>
        public static string ResolveString(IHevoString text)
        {
            return _stringResolvers.TryGetValue(text.GetType(), out var resolver) ? resolver(text) : string.Empty;
        }

        /// <summary>
        /// 💥 [UI 层专用] 终极全能绑定方法。一键处理所有多态！
        /// 开发者只需调 dot.BindHevo(Shape.FillProperty, f.Brush);
        /// </summary>
        public static void BindHevo(this FrameworkElement element, DependencyProperty property, object? abstraction)
        {
            if (abstraction == null) return;

            var type = abstraction.GetType();
            if (_universalBinders.TryGetValue(type, out var binder))
                binder(element, property, abstraction);
            else
                throw new NotSupportedException($"[Hevo 架构] 尚未注册对类型 {type.Name} 的 WPF 绑定策略。");
        }
    }
}
