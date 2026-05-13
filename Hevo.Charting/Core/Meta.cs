using Hevo.Charting.Abstractions;
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
    // 设计选型:record class 而非 record struct。
    // 4 个字段全是引用 (IHevoString / IBrushResolver / string / IHevoFormatter?),
    // 即"指针沙拉",struct 的内联值数据红利消失;且创建集中在 schema build 一次性,
    // GC 不构成压力。class 在 C# 9 LangVersion 下原生支持 with 表达式,且
    // FieldMeta? 是 8B 引用 (struct 下是 40B Nullable<FieldMeta>),整体更轻。
    // ==========================================
    /// <summary>
    /// 字段元数据(图例 / 头部 / Tooltip / Crosshair 共用的最小描述单元)。
    /// 业务侧极少直接 new,通常走静态工厂(Literal / Resource / Mixed / Dynamic)。
    /// </summary>
    /// <param name="Name">显示名(支持字面量 + 多语言资源 Key)。</param>
    /// <param name="Resolver">画刷解析器(纯色 = <see cref="BrushResolver.Constant{T}"/>;阈值变色 = ThresholdBrushResolver 等)。</param>
    /// <param name="Format">数值格式字符串,默认 "G"。</param>
    /// <param name="Provider">自定义格式化器(IHevoFormatter&lt;T&gt;);null 时走 IFormattable.ToString。</param>
    public record class FieldMeta(
        IHevoString Name,
        IBrushResolver<double> Resolver,
        string Format = "G",
        IHevoFormatter? Provider = null
    )
    {
        // 💥 重载 1：完美兼容老代码 (传 IHevoBrush，底层自动套一层静态解析器)
        public static FieldMeta Literal(string text, IHevoBrush brush, string format = "G", IHevoFormatter? provider = null)
            => new(new HevoLiteralString(text), BrushResolver.Constant<double>(brush), format, provider);

        // 重载 2：完美兼容老代码 (传 Color)
        public static FieldMeta Literal(string text, Color color, string format = "G", IHevoFormatter? provider = null)
            => Literal(text, new HevoSolidBrush(color), format, provider);

        // 💥 重载 3：支持我们全新的动态画刷！(传入 ThresholdBrushResolver 等)
        public static FieldMeta Dynamic(string text, IBrushResolver<double> resolver, string format = "G", IHevoFormatter? provider = null)
                    => new(new HevoLiteralString(text), resolver, format, provider);

        // 💥 静态工厂 2：全动态资源 (推荐！语言 Key + 主题画刷 Key)
        public static FieldMeta Resource(string textKey, string brushKey, string format = "G")
            => new(new HevoResourceString(textKey), BrushResolver.Constant<double>(new HevoResourceBrush(brushKey)), format);

        // 💥 静态工厂 3：混合模式 (动态多语言 + 静态指定颜色)
        public static FieldMeta Mixed(string textKey, IHevoBrush brush, string format = "G")
            => new(new HevoResourceString(textKey), BrushResolver.Constant<double>(brush), format);
    }

    /// <summary>
    /// 统一的元数据包:支持 1~N 列数据。
    /// 数据流:Series Feature 在 OnCompose / OnProject 时下发到自家 Layer 的 board → Crosshair / Tooltip / Header 反向 Read 用于渲染。
    /// </summary>
    /// <param name="GroupName">图例分组名(如"Candle"、"MACD"、"成交量")。</param>
    /// <param name="Fields">字段元数据数组(开高低收 / 主副指标线等)。</param>
    public record MetaTrait(string GroupName, params FieldMeta[] Fields) : IVisualTrait
    {
        // 极简构建：单线静态指标
        public static MetaTrait SingleLiteral(string name, IHevoBrush brush, string format = "G")
            => new(name, FieldMeta.Literal(name, brush, format));

        // 极简构建：单线动态指标
        public static MetaTrait SingleResource(string groupKey, string textKey, string brushKey, string format = "G")
            => new(groupKey, FieldMeta.Resource(textKey, brushKey, format));
    }

    /// <summary>
    /// 热数据通用切片(纯 double 数组,每帧由 Series Feature 下发)。
    /// 数据流:Series 写入 → Crosshair 算交点 / Tooltip 算行值 / Header 算最新值 全靠 Read 这根。
    /// FieldValues 与 <see cref="MetaTrait.Fields"/> 一一对应:索引 i 的列对应第 i 个字段的实时数据。
    /// </summary>
    public record DoubleSeriesDataTrait(params ReadOnlyMemory<double>[] FieldValues) : IVisualTrait;

    /// <summary>
    /// 字符串版热数据切片,跟 <see cref="DoubleSeriesDataTrait"/> 平级 ——
    /// 业务侧静态/慢变元数据(证券名称、行业、地区代码等)通过这条 trait 喂给 Tooltip / Header 等下游,
    /// 走跟 double 指标完全一致的 "ActiveLayers 遍历 + MetaTrait 配对" 协议,不再走专用端口后门。
    /// FieldValues 与 <see cref="MetaTrait.Fields"/> 一一对应:索引 i 的列对应第 i 个字段的字符串列。
    /// </summary>
    public record StringSeriesDataTrait(params ReadOnlyMemory<string>[] FieldValues) : IVisualTrait;

    /// <summary>
    /// 动态颜色外挂包:只有红涨绿跌 / 多空底色这类业务才需要发布。
    /// Evaluator 签名 = (fieldIndex, localIndex) → IHevoBrush。
    /// 由 Series Feature 下发,Crosshair 圆点 / Header 颜色块从此处取色。
    /// </summary>
    public record DynamicColorTrait(Func<int, int, IHevoBrush> Evaluator) : IVisualTrait;

    /// <summary>
    /// 💥 万物皆有底色：所有画刷(静态/动态)在渲染时,都表现为"按上下文取画刷"的解析器。
    /// <para>
    /// <see cref="Resolve"/> 是值依赖路径(per-bar / per-point 取色);<see cref="DefaultBrush"/>
    /// 是无上下文路径(legend / header / 单色 fallback)。对动态 resolver,<see cref="DefaultBrush"/>
    /// 是其 fallback 色,语义不是"唯一真色"——这一点跟历史命名 ConstantBrush 不同。
    /// </para>
    /// <para>
    /// 纯色场景请用 <see cref="BrushResolver.Constant{T}"/> 工厂,避免直接 new <see cref="StaticBrushResolver{T}"/>。
    /// </para>
    /// </summary>
    public interface IBrushResolver<TContext>
    {
        IHevoBrush Resolve(in TContext context);

        /// <summary>无上下文 fallback 画刷:legend / header / 单色路径取它;
        /// 动态 resolver 内部决定它的回放策略(threshold 用 equal,palette 用首色等)。</summary>
        IHevoBrush DefaultBrush { get; }
    }

    /// <summary>
    /// <see cref="IBrushResolver{T}"/> 的工厂入口 —— 取代调用方直接 new <see cref="StaticBrushResolver{T}"/>。
    /// </summary>
    public static class BrushResolver
    {
        /// <summary>纯色 resolver:Resolve / DefaultBrush 都返回同一支 brush。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IBrushResolver<T> Constant<T>(IHevoBrush brush) => new StaticBrushResolver<T>(brush);
    }

    /// <summary>
    /// 纯色画刷的马甲(JSON 多态判别符依赖具体类型,因此保留 public)。
    /// 业务侧请走 <see cref="BrushResolver.Constant{T}"/> 工厂,不要直接 new。
    /// </summary>
    public class StaticBrushResolver<TValue> : IBrushResolver<TValue>
    {
        public IHevoBrush DefaultBrush { get; }
        public StaticBrushResolver(IHevoBrush brush) => DefaultBrush = brush;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IHevoBrush Resolve(in TValue value) => DefaultBrush;
    }

    public static class BrushExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IHevoBrush GetDefaultBrush(this FieldMeta meta) => meta.Resolver.DefaultBrush;
    }
}
