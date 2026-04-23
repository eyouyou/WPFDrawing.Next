using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;

namespace Hevo.Charting.Buildin
{
    // ==========================================
    // 💥 8. TradingView 同款时间轴
    // 【终极 0-GC 改造】：替换为 RefBox
    // ==========================================
    public class TradingViewTimeMathStrategy : ITickStrategy
    {
        private readonly RefBox<ReadOnlyMemory<DateTime>> _timesBox;

        public TradingViewTimeMathStrategy(RefBox<ReadOnlyMemory<DateTime>> timesBox)
        {
            _timesBox = timesBox;
        }

        public IEnumerable<TickMathResult> Calculate(RealRange logicalRange, double physicalWidth)
        {
            var times = _timesBox.Value; // 💥 每一帧拉取盒子里的最新指针

            if (!logicalRange.IsValid || logicalRange.Span <= 0 || physicalWidth <= 0 || times.IsEmpty)
                yield break;

            // 删 Slicer 后世界索引 == 数组下标，logicalRange / times 同坐标系直用
            int startIdx = Math.Max(0, (int)Math.Floor(logicalRange.Min));
            int endIdx = Math.Min(times.Length - 1, (int)Math.Ceiling(logicalRange.Max));

            if (startIdx >= endIdx) yield break;

            // 💥 用"bar 数 × 典型 bar 间隔"代替墙钟差，跳过午休/隔夜等非交易黑洞
            var span = times.Span;
            TimeSpan barInterval = InferBarInterval(span, startIdx, endIdx);
            int barCount = endIdx - startIdx + 1;
            TimeSpan effectiveTime = TimeSpan.FromTicks(barInterval.Ticks * barCount);

            int desiredTicks = Math.Max(2, (int)(physicalWidth / 150.0));
            TimeSpan roughInterval = TimeSpan.FromTicks(effectiveTime.Ticks / desiredTicks);
            TimeSpan niceInterval = GetNiceTimeInterval(roughInterval);

            DateTime currentBoundary = RoundUp(span[startIdx], niceInterval);

            double lastRatio = -2.0;
            double minRatioGap = 80.0 / physicalWidth;

            for (int i = startIdx; i <= endIdx; i++)
            {
                DateTime t = times.Span[i];

                if (t >= currentBoundary)
                {
                    double ratio = (i - logicalRange.Min) / logicalRange.Span;

                    if (ratio - lastRatio >= minRatioGap)
                    {
                        yield return new TickMathResult(i, isBaseLine: false);
                        lastRatio = ratio;
                    }

                    currentBoundary = RoundUp(t.AddTicks(1), niceInterval);
                }
            }
        }

        /// <summary>
        /// 推断典型 bar 间隔：取相邻 delta 的最小值（取样最多 5 对，避开午休/隔夜的大跳）。
        /// 默认回退 1 分钟（最常见 K 线粒度）。
        /// </summary>
        private static TimeSpan InferBarInterval(ReadOnlySpan<DateTime> span, int startIdx, int endIdx)
        {
            if (endIdx <= startIdx) return TimeSpan.FromMinutes(1);
            long minTicks = long.MaxValue;
            int sampleEnd = Math.Min(startIdx + 5, endIdx);
            for (int i = startIdx; i < sampleEnd; i++)
            {
                long delta = (span[i + 1] - span[i]).Ticks;
                if (delta > 0 && delta < minTicks) minTicks = delta;
            }
            return minTicks == long.MaxValue ? TimeSpan.FromMinutes(1) : TimeSpan.FromTicks(minTicks);
        }

        private TimeSpan GetNiceTimeInterval(TimeSpan rough)
        {
            if (rough.TotalMinutes <= 1) return TimeSpan.FromMinutes(1);
            if (rough.TotalMinutes <= 2) return TimeSpan.FromMinutes(2);
            if (rough.TotalMinutes <= 5) return TimeSpan.FromMinutes(5);
            if (rough.TotalMinutes <= 10) return TimeSpan.FromMinutes(10);
            if (rough.TotalMinutes <= 15) return TimeSpan.FromMinutes(15);
            if (rough.TotalMinutes <= 30) return TimeSpan.FromMinutes(30);
            if (rough.TotalHours <= 1) return TimeSpan.FromHours(1);
            if (rough.TotalHours <= 2) return TimeSpan.FromHours(2);
            if (rough.TotalHours <= 4) return TimeSpan.FromHours(4);
            if (rough.TotalDays <= 1) return TimeSpan.FromDays(1);

            return TimeSpan.FromDays(Math.Ceiling(rough.TotalDays));
        }

        private DateTime RoundUp(DateTime dt, TimeSpan d)
        {
            long ticks = (dt.Ticks + d.Ticks - 1) / d.Ticks;
            return new DateTime(ticks * d.Ticks, dt.Kind);
        }
    }

    // ==========================================
    // 💥 9. TradingView 风格 X/Y 轴通用算法 (专治非 DateTime 的泛型数据)
    // 【架构说明】：不依赖任何数组，纯数学映射，无需改造！
    // ==========================================
    public class TradingViewAxisMathStrategy : ITickStrategy
    {
        private readonly double _minPixelsPerTick;

        public TradingViewAxisMathStrategy(double minPixelsPerTick = 80.0)
        {
            _minPixelsPerTick = minPixelsPerTick;
        }

        public IEnumerable<TickMathResult> Calculate(RealRange range, double physicalLength)
        {
            if (!range.IsValid || range.Span <= 0 || physicalLength <= 0)
                yield break;

            int targetTickCount = Math.Max(2, (int)(physicalLength / _minPixelsPerTick));
            double rawStep = range.Span / targetTickCount;
            double niceStep = CalculateNiceStep(rawStep);

            double start = Math.Ceiling(range.Min / niceStep) * niceStep;
            double end = Math.Floor(range.Max / niceStep) * niceStep;

            double epsilon = niceStep * 1e-5;

            for (double val = start; val <= end + epsilon; val += niceStep)
            {
                bool isBase = Math.Abs(val) < 1e-6;
                yield return new TickMathResult(val, isBaseLine: isBase);
            }
        }

        private double CalculateNiceStep(double rawStep)
        {
            if (rawStep <= 0) return 1.0;

            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
            double normalized = rawStep / magnitude;

            double multiplier;
            if (normalized <= 1.2) multiplier = 1.0;
            else if (normalized <= 2.5) multiplier = 2.0;
            else if (normalized <= 5.0) multiplier = 5.0;
            else multiplier = 10.0;

            return multiplier * magnitude;
        }
    }
}
