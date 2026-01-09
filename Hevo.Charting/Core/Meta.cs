using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Hevo.Charting.Core
{
    // 标记接口，只为了让 FieldMeta 能存下任意类型的 Formatter
    public interface IHevoFormatter { }

    // 💥 真正的强类型泛型接口！0 装箱！
    public interface IHevoFormatter<T> : IHevoFormatter
    {
        string Format(T value);
    }

    public static class FormatterExtensions
    {
        /// <summary>
        /// 💥 终极智能格式化分发器
        /// 解决数学域 (double) 与业务域 (float/int) 类型不匹配的“最后一公里”问题。
        /// </summary>
        public static string FormatValue<T>(this T value, string format, IHevoFormatter? formatter)
        {
            if (formatter == null) return DefaultFormat(value, format);

            // 1. 🎯 第一优先级：精确类型匹配 (如 double -> IHevoFormatter<double>)
            if (formatter is IHevoFormatter<T> typedFormatter)
            {
                return typedFormatter.Format(value);
            }

            // 2. 💥 第二优先级：数值类型智能桥接 (处理 float <-> double <-> int 的乱炖)
            // 我们通过将输入 value 拆包，尝试寻找“虽不精确但逻辑正确”的格式化器
            switch (value)
            {
                case double d:
                    {
                        if (formatter is IHevoFormatter<float> fFmt) return fFmt.Format((float)d);
                        if (formatter is IHevoFormatter<decimal> dcFmt) return dcFmt.Format((decimal)d);
                    }
                    break;
                case float f:
                    {
                        if (formatter is IHevoFormatter<double> dFmt) return dFmt.Format((double)f);
                        if (formatter is IHevoFormatter<decimal> dcFmt) return dcFmt.Format((decimal)f);
                    }
                    break;
                case int i:
                    {
                        if (formatter is IHevoFormatter<double> dFmt2) return dFmt2.Format((double)i);
                        if (formatter is IHevoFormatter<float> fFmt2) return fFmt2.Format((float)i);
                    }
                    break;
                case long l:
                    {
                        if (formatter is IHevoFormatter<double> dFmt3) return dFmt3.Format((double)l);
                        if (formatter is IHevoFormatter<float> fFmt3) return fFmt3.Format((float)l);
                    }
                    break;
            }
            // 4. 🛡️ 最终兜底：原码输出
            return DefaultFormat(value, format);
        }

        private static string DefaultFormat<T>(T value, string format)
        {
            return value is IFormattable f ? f.ToString(format, null) : value?.ToString() ?? "";
        }
    }

    // ==========================================
    // 💥 最小粒度的字段描述 (彻底抽象化！)
    // ==========================================
    public record struct FieldMeta(
        IHevoString Name,
        IBrushResolver<double> Resolver,
        string Format = "G",
        IHevoFormatter? Provider = null
    )
    {
        // 💥 重载 1：完美兼容老代码 (传 IHevoBrush，底层自动套一层静态解析器)
        public static FieldMeta Literal(string text, IHevoBrush brush, string format = "G", IHevoFormatter? provider = null)
            => new(new HevoLiteralString(text), new StaticBrushResolver<double>(brush), format, provider);

        // 重载 2：完美兼容老代码 (传 Color)
        public static FieldMeta Literal(string text, Color color, string format = "G", IHevoFormatter? provider = null)
            => Literal(text, new HevoSolidBrush(color), format, provider);

        // 💥 重载 3：支持我们全新的动态画刷！(传入 ThresholdBrushResolver 等)
        public static FieldMeta Dynamic(string text, IBrushResolver<double> resolver, string format = "G", IHevoFormatter? provider = null)
                    => new(new HevoLiteralString(text), resolver, format, provider);

        // 💥 静态工厂 2：全动态资源 (推荐！语言 Key + 主题画刷 Key)
        public static FieldMeta Resource(string textKey, string brushKey, string format = "G")
            => new(new HevoResourceString(textKey), new StaticBrushResolver<double>(new HevoResourceBrush(brushKey)), format);

        // 💥 静态工厂 3：混合模式 (动态多语言 + 静态指定颜色)
        public static FieldMeta Mixed(string textKey, IHevoBrush brush, string format = "G")
            => new(new HevoResourceString(textKey), new StaticBrushResolver<double>(brush), format);
    }

    /// <summary>
    /// 💥 统一的元数据包：支持 1~N 列数据
    /// </summary>
    public record MetaTrait(string GroupName, params FieldMeta[] Fields) : IVisualTrait
    {
        // 极简构建：单线静态指标
        public static MetaTrait SingleLiteral(string name, IHevoBrush brush, string format = "G")
            => new(name, FieldMeta.Literal(name, brush, format));

        // 极简构建：单线动态指标
        public static MetaTrait SingleResource(string groupKey, string textKey, string brushKey, string format = "G")
            => new(groupKey, FieldMeta.Resource(textKey, brushKey, format));
    }

    // ==========================================
    // 💥 热数据分离：彻底恢复纯净，只有数据切片，不带任何杂质！(每帧发布)
    // 完全都是用double
    // ==========================================
    public record DoubleSeriesDataTrait(params ReadOnlyMemory<double>[] FieldValues) : IVisualTrait;
    //public record FloatSeriesDataTrait(params ReadOnlyMemory<double>[] FieldValues) : IVisualTrait;

    // ==========================================
    // 💥 新增外挂能力包：动态颜色特质！
    // 只有像 K 线、MACD 红绿柱这种需要根据数据算颜色的组件，才需要发布这个 Trait！
    // 签名：(fieldIndex, localIndex) => Brush
    // ==========================================
    public record DynamicColorTrait(Func<int, int, IHevoBrush> Evaluator) : IVisualTrait;

    /// <summary>
    /// 基础契约：万物皆有底色
    /// </summary>
    public interface IBrushResolver
    {
        /// <summary>
        /// 常量画刷
        /// </summary>
        IHevoBrush ConstantBrush { get; }
    }

    /// <summary>
    /// 💥 终极契约：所有画刷（静态/动态）在渲染时，都必须表现为一个解析器
    /// </summary>
    /// <typeparam name="TValue"></typeparam>
    public interface IBrushResolver<TContext> : IBrushResolver
    {
        IHevoBrush Resolve(in TContext context);
    }

    /// <summary>
    /// 纯色画刷的马甲
    /// </summary>
    /// <typeparam name="TValue"></typeparam>
    public class StaticBrushResolver<TValue> : IBrushResolver<TValue>
    {
        public IHevoBrush ConstantBrush { get; }
        public StaticBrushResolver(IHevoBrush brush) => ConstantBrush = brush;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public IHevoBrush Resolve(in TValue value) => ConstantBrush;
    }

    public static class BrushExtensions
    {
        /// <summary>
        /// 获取基本
        /// </summary>
        /// <param name="brush"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IHevoBrush GetConstantBrush(this IHevoBrush brush)
        {
            // C# 9+ 模式匹配优化，性能略微优于 is 变量声明
            if (brush is IBrushResolver resolver)
            {
                return resolver.ConstantBrush;
            }
            return brush;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IHevoBrush GetConstantBrush(this FieldMeta meta)
        {
            return meta.Resolver.ConstantBrush;
        }
    }
}
