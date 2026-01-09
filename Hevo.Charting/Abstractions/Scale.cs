using Hevo.Charting.Core;

namespace Hevo.Charting.Abstractions
{
    /// <summary>
    /// 💥 实数区间 (RealRange)：图表引擎的核心值对象。
    /// 用于表示坐标轴量程、视口范围或数据极值。
    /// 彻底解决 DoubleRange 命名的冲突问题。
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public readonly struct RealRange : IEquatable<RealRange>
    {
        public readonly double Min;
        public readonly double Max;

        public RealRange(double min, double max)
        {
            Min = min;
            Max = max;
        }

        // ==========================================
        // 核心属性
        // ==========================================

        /// <summary> 区间跨度：$Span = Max - Min$ </summary>
        public double Span => Max - Min;

        /// <summary> 中心点：$Center = \frac{Min + Max}{2}$ </summary>
        public double Center => (Min + Max) * 0.5;

        /// <summary> 是否包含某个值 </summary>
        public bool Contains(double value) => value >= Min && value <= Max;

        /// <summary>
        /// 💥 有效性检查：
        /// 1. 不是 Empty (没有 NaN)
        /// 2. Span 必须大于 0 (在图表映射中，Span 为 0 会导致除零异常)
        /// </summary>
        public bool IsValid => !IsEmpty && (Max > Min);

        /// <summary>
        /// 💥 空区间定义：只有包含 NaN 才是真正的空。
        /// [0, 0] 在某些业务场景（如所有价格都一样）下是合法的。
        /// </summary>
        public static readonly RealRange Empty = new(double.NaN, double.NaN);

        /// <summary> 检查是否包含无效数字 </summary>
        public bool IsEmpty => double.IsNaN(Min) || double.IsNaN(Max);

        /// <summary> 确保 Min <= Max </summary>
        public RealRange Normalized()
            => Min > Max ? new RealRange(Max, Min) : this;

        // ==========================================
        // 性能与对齐 (0-GC 核心)
        // ==========================================

        public bool Equals(RealRange other)
        {
            // 提醒：如果业务对微小浮点误差敏感，可考虑使用 Math.Abs(Min - other.Min) < epsilon
            // 但作为引擎状态判断，直接比较 bit 是最快的。
            return Min.Equals(other.Min) && Max.Equals(other.Max);
        }

        public override bool Equals(object? obj) => obj is RealRange other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Min, Max);

        public static bool operator ==(RealRange left, RealRange right) => left.Equals(right);

        public static bool operator !=(RealRange left, RealRange right) => !left.Equals(right);

        // ==========================================
        // 语法糖
        // ==========================================

        /// <summary> 支持解构赋值：var (min, max) = range; </summary>
        public void Deconstruct(out double min, out double max)
        {
            min = Min;
            max = Max;
        }

        public override string ToString() => IsEmpty ? "Empty" : $"[{Min:F4}, {Max:F4}]";
    }


    /// <summary>
    /// 归一化比例尺
    /// 核心职责：负责 "Data Space" (业务域) <-> "Normal Space" ([0.0 ~ 1.0] 归一域) 的双向转换
    /// </summary>
    public interface IScale
    {
        // 核心改变：Range 是参数，不是属性！
        // 输入：数值 + 范围
        // 输出：0.0 ~ 1.0
        double Normalize(double value, RealRange range);
        // 输入：0.0 ~ 1.0 + 范围
        // 输出：数值
        double Denormalize(double normalValue, RealRange range);
    }

    /// <summary>
    /// 轴体 tick 刻度模型
    /// </summary>
    /// <typeparam name="TDomain"></typeparam>
    public struct TickModel<TDomain>
    {
        public string Label;
        public TDomain Value;
        public double Ratio;
        public bool IsBaseLine;
        public IHevoBrush? OverrideTextBrush;
    }

    public readonly struct TickMathResult<TDomain>
    {
        public readonly double Ratio;
        public readonly TDomain Value;
        public readonly bool IsBaseLine;

        public TickMathResult(double ratio, TDomain value, bool isBaseLine)
        {
            Ratio = ratio; Value = value; IsBaseLine = isBaseLine;
        }
    }
    /// <summary>
    /// 刻度修饰策略：负责在计算时，顺手决定外观
    /// </summary>
    public interface ITickStylePolicy<TDomain>
    {
        // 决定显示的文字 (代替原来的 ToString)
        string FormatLabel(TDomain value);

        // 决定覆写的颜色 (代替另外写一个 Processor)
        IHevoBrush? GetOverrideBrush(TDomain value);
    }

    /// <summary>
    /// TDomain 是业务类型（int/double），TValue 是生成的刻度值类型
    /// </summary>
    /// <typeparam name="TDomain"></typeparam>
    public interface ITickStrategy<TDomain>
    {
        /// <summary>
        /// 结合逻辑范围和物理屏幕尺寸，生成携带业务语义的刻度集合。
        /// 不包含屏幕坐标计算 (Ratio)，坐标计算由外层 Scale 负责。
        /// </summary>
        /// <param name="logicalRange">逻辑极值（如最低价~最高价，或 0~241 索引）</param>
        /// <param name="physicalLength">当前轴在屏幕上的物理像素长度</param>
        IEnumerable<TickMathResult<TDomain>> Calculate(RealRange logicalRange, double physicalLength);
    }

    /// <summary>
    /// 💥 刻度策略提供者：它知道如何从黑板榨取数据，并生成当帧所需的策略！
    /// </summary>
    public interface ITickProvider<TDomain>
    {
        ITickStrategy<TDomain> GetStrategy(FeatureContext ctx);
        ITickStylePolicy<TDomain> GetStyle(FeatureContext ctx);
    }

    public static class TickStrategyExtensions
    {
        public static IEnumerable<TickModel<TDomain>> ApplyStyle<TDomain>(
            this IEnumerable<TickMathResult<TDomain>> mathResults,
            ITickStylePolicy<TDomain> stylePolicy)
        {
            foreach (var math in mathResults)
            {
                yield return new TickModel<TDomain>
                {
                    Ratio = math.Ratio,
                    Value = math.Value,
                    IsBaseLine = math.IsBaseLine,
                    Label = stylePolicy.FormatLabel(math.Value), // 穿上文字衣服
                    OverrideTextBrush = stylePolicy.GetOverrideBrush(math.Value) // 穿上颜色衣服
                };
            }
        }
    }
}