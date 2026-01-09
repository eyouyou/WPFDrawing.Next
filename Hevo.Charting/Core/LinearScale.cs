using Hevo.Charting.Abstractions;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 线性比例尺
    /// </summary>
    public record LinearScale : IScale
    {
        // 单例模式 (0 GC)
        public static readonly LinearScale Instance = new();

        public double Normalize(double value, RealRange range)
        {
            if (range.Span == 0) return 0;
            return (value - range.Min) / range.Span;
        }

        public double Denormalize(double normalValue, RealRange range)
        {
            return (normalValue * range.Span) + range.Min;
        }
    }

    /// <summary>
    /// 对数比例尺
    /// 场景：长期股价趋势（从 10 涨到 1000）、加密货币、指数增长数据。
    /// 特点：视觉上 10->100 的距离等于 100->1000。
    /// 限制：数据必须 >0。
    /// </summary>
    public record LogarithmicScale : IScale
    {
        public static readonly LogarithmicScale Instance = new();

        public double Normalize(double value, RealRange range)
        {
            // 保护：对数不能处理 <= 0
            if (value <= 0 || range.Min <= 0) return 0;

            double logVal = Math.Log10(value);
            double logMin = Math.Log10(range.Min);
            double logMax = Math.Log10(range.Max);
            double logSpan = logMax - logMin;

            if (logSpan == 0) return 0;

            return (logVal - logMin) / logSpan;
        }

        public double Denormalize(double normalValue, RealRange range)
        {
            if (range.Min <= 0) return 0;

            double logMin = Math.Log10(range.Min);
            double logMax = Math.Log10(range.Max);
            double logSpan = logMax - logMin;

            // 先还原到 Log 空间
            double logVal = (normalValue * logSpan) + logMin;

            // 再还原到线性空间 (10^x)
            return Math.Pow(10, logVal);
        }
    }

    /// <summary>
    /// 平方根标尺 (SquareRootScale)
    /// 场景：成交量 (Volume)。
    /// 特点：压缩极值。当某一天成交量是平时的 100 倍时，线性轴会让平时看起来像 0；平方根轴能让平时也能看清。
    /// </summary>
    public record SquareRootScale : IScale
    {
        public static readonly SquareRootScale Instance = new();

        public double Normalize(double value, RealRange range)
        {
            // 保护：非负
            double v = Math.Max(0, value);
            double min = Math.Max(0, range.Min);
            double max = Math.Max(0, range.Max);

            double sqrtVal = Math.Sqrt(v);
            double sqrtMin = Math.Sqrt(min);
            double sqrtSpan = Math.Sqrt(max) - sqrtMin;

            if (sqrtSpan == 0) return 0;

            return (sqrtVal - sqrtMin) / sqrtSpan;
        }

        public double Denormalize(double normalValue, RealRange range)
        {
            double min = Math.Max(0, range.Min);
            double max = Math.Max(0, range.Max);

            double sqrtMin = Math.Sqrt(min);
            double sqrtSpan = Math.Sqrt(max) - sqrtMin;

            double sqrtVal = (normalValue * sqrtSpan) + sqrtMin;
            return sqrtVal * sqrtVal;
        }
    }

    /// <summary>
    /// 幂指树标尺 (PowerScale)
    /// 场景：更通用的非线性压缩，比如 Gamma 校正。
    /// 特点：SquareRootScale 其实就是 Exponent = 0.5 的特例。
    /// </summary>
    /// <param name="Exponent"></param>
    public record PowerScale(double Exponent) : IScale
    {
        public double Normalize(double value, RealRange range)
        {
            // 简化处理：假设处理正数区间
            double v = Math.Pow(Math.Max(0, value), Exponent);
            double min = Math.Pow(Math.Max(0, range.Min), Exponent);
            double max = Math.Pow(Math.Max(0, range.Max), Exponent);
            double span = max - min;

            return span == 0 ? 0 : (v - min) / span;
        }

        public double Denormalize(double normalValue, RealRange range)
        {
            double min = Math.Pow(Math.Max(0, range.Min), Exponent);
            double max = Math.Pow(Math.Max(0, range.Max), Exponent);
            double span = max - min;

            double val = (normalValue * span) + min;
            return Math.Pow(val, 1.0 / Exponent); // 开方还原
        }
    }

    /// <summary>
    /// 反转标尺 (InvertedScale)
    /// 场景：外汇(USD/CNY -> CNY/USD)，或者深度图(Ask/Bid)。
    /// 特点：装饰器模式，包装任意一个 Scale 并反转结果。
    /// </summary>
    /// <param name="Inner"></param>
    public record InvertedScale(IScale Inner) : IScale
    {
        // 默认反转线性
        public static readonly InvertedScale Default = new(LinearScale.Instance);

        public double Normalize(double value, RealRange range)
        {
            // 1.0 - 结果
            return 1.0 - Inner.Normalize(value, range);
        }

        public double Denormalize(double normalValue, RealRange range)
        {
            // 1.0 - 输入
            return Inner.Denormalize(1.0 - normalValue, range);
        }
    }
}
