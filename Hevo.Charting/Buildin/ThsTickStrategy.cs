using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;

namespace Hevo.Charting.Buildin
{
    // ==========================================
    // 💥 1. THS 风格 Y轴 (价格/指标) 等分策略
    // 【架构说明】：Y 轴是连续域纯数学推演，不依赖任何数组数据，
    // 因此天生就是 0-GC，无需做 RefBox 改造，保持原样即可！
    // ==========================================
    public class ThsYAxisMathStrategy : ITickStrategy<double>
    {
        private readonly int _gridCount;

        public ThsYAxisMathStrategy(int gridCount = 4)
        {
            _gridCount = Math.Max(1, gridCount);
        }

        public IEnumerable<TickMathResult<double>> Calculate(RealRange range, double physicalLength)
        {
            if (!range.IsValid || range.Span <= 0 || physicalLength <= 0)
                yield break;

            double step = range.Span / _gridCount;

            for (int i = 0; i <= _gridCount; i++)
            {
                double val = range.Min + step * i;
                if (Math.Abs(val) < 1e-6) val = 0;

                double ratio = (val - range.Min) / range.Span;
                bool isBase = val == 0;

                yield return new TickMathResult<double>(ratio, val, isBaseLine: isBase);
            }
        }
    }

    // ==========================================
    // 💥 2. THS 风格 X轴 (时间/索引) 等分策略
    // 【终极 0-GC 改造】：将 ReadOnlyMemory 替换为 RefBox，策略终身只 new 一次！
    // ==========================================
    public class ThsTimeMathStrategy : ITickStrategy<double>
    {
        // 💥 改造点：持有长生命周期的“数据盒子”，而不是一次性的数据切片
        private readonly RefBox<ReadOnlyMemory<DateTime>> _timesBox;
        private readonly int _gridCount;

        public ThsTimeMathStrategy(RefBox<ReadOnlyMemory<DateTime>> timesBox, int gridCount = 4)
        {
            _timesBox = timesBox;
            _gridCount = Math.Max(1, gridCount);
        }

        public IEnumerable<TickMathResult<double>> Calculate(RealRange logicalRange, double physicalWidth)
        {
            // 💥 极速开盒：每一帧渲染时，从盒子里取出最新推流过来的切片，0 分配！
            var times = _timesBox.Value;

            if (!logicalRange.IsValid || logicalRange.Span <= 0 || physicalWidth <= 0 || times.IsEmpty)
                yield break;

            // 安全钳制在数组范围内
            int startIdx = Math.Max(0, (int)Math.Ceiling(logicalRange.Min));
            int endIdx = Math.Min(times.Length - 1, (int)Math.Floor(logicalRange.Max));

            if (startIdx > endIdx) yield break;

            double stepRatio = 1.0 / _gridCount;
            int lastIndex = -1;

            for (int i = 0; i <= _gridCount; i++)
            {
                double targetRatio = i * stepRatio;
                double exactIndex = logicalRange.Min + targetRatio * logicalRange.Span;

                int closestIndex = (int)Math.Round(exactIndex);
                closestIndex = Math.Clamp(closestIndex, startIdx, endIdx);

                if (closestIndex == lastIndex) continue;
                lastIndex = closestIndex;

                double realRatio = (closestIndex - logicalRange.Min) / logicalRange.Span;
                yield return new TickMathResult<double>(realRatio, closestIndex, isBaseLine: false);
            }
        }
    }

    // ==========================================
    // 💥 3. X轴 边界首中尾策略 (Domain 专用)
    // 【架构说明】：纯依赖 RealRange 的数学计算，不依赖数组，无需改造。
    // ==========================================
    public class DomainBoundaryTickStrategy : ITickStrategy<double>
    {
        public IEnumerable<TickMathResult<double>> Calculate(RealRange range, double physicalLength)
        {
            if (!range.IsValid) yield break;

            int minIdx = (int)Math.Ceiling(range.Min);
            int maxIdx = (int)Math.Floor(range.Max);

            if (minIdx > maxIdx) yield break;

            yield return new TickMathResult<double>((minIdx - range.Min) / range.Span, minIdx, false);

            int midIdx = minIdx + (maxIdx - minIdx) / 2;
            if (midIdx > minIdx && midIdx < maxIdx)
            {
                yield return new TickMathResult<double>((midIdx - range.Min) / range.Span, midIdx, false);
            }

            if (maxIdx > minIdx)
            {
                yield return new TickMathResult<double>((maxIdx - range.Min) / range.Span, maxIdx, false);
            }
        }
    }

    // ==========================================
    // 💥 4. 固定时间表策略
    // 【终极 0-GC 改造】：替换为 RefBox
    // ==========================================
    public class FixedScheduleTickStrategy : ITickStrategy<double>
    {
        private readonly RefBox<ReadOnlyMemory<DateTime>> _timesBox;
        private readonly List<TimeSpan> _schedules;

        public FixedScheduleTickStrategy(RefBox<ReadOnlyMemory<DateTime>> timesBox, List<TimeSpan> schedules)
        {
            _timesBox = timesBox;
            _schedules = schedules;
        }

        public IEnumerable<TickMathResult<double>> Calculate(RealRange range, double physicalLength)
        {
            var times = _timesBox.Value; // 💥 取最新切片

            if (!range.IsValid || times.IsEmpty) yield break;

            DateTime baseDate = times.Span[0].Date;

            foreach (var schedule in _schedules)
            {
                DateTime targetTime = baseDate.Add(schedule);
                int closestIdx = FindClosestIndex(times.Span, targetTime);

                if (closestIdx >= range.Min && closestIdx <= range.Max)
                {
                    double ratio = (closestIdx - range.Min) / range.Span;
                    yield return new TickMathResult<double>(ratio, closestIdx, false);
                }
            }
        }

        private int FindClosestIndex(ReadOnlySpan<DateTime> span, DateTime target)
        {
            int low = 0, high = span.Length - 1;
            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                if (span[mid] < target) low = mid + 1;
                else if (span[mid] > target) high = mid - 1;
                else return mid;
            }
            return Math.Clamp(low, 0, span.Length - 1);
        }
    }

    // ==========================================
    // 💥 5. 周期感知策略
    // 【终极 0-GC 改造】：替换为 RefBox
    // ==========================================
    public class PeriodicTickStrategy : ITickStrategy<double>
    {
        private readonly RefBox<ReadOnlyMemory<DateTime>> _timesBox;
        public PeriodicTickStrategy(RefBox<ReadOnlyMemory<DateTime>> timesBox) => _timesBox = timesBox;

        public IEnumerable<TickMathResult<double>> Calculate(RealRange range, double physicalLength)
        {
            var times = _timesBox.Value; // 💥 取最新切片

            if (!range.IsValid || times.IsEmpty) yield break;

            int start = (int)Math.Max(0, Math.Floor(range.Min));
            int end = (int)Math.Min(times.Length - 1, Math.Ceiling(range.Max));

            if (start > end) yield break;

            List<int> transitionIndices = new();
            var tempSpan = times.Span;
            for (int i = start + 1; i <= end; i++)
            {
                if (tempSpan[i].Month != tempSpan[i - 1].Month)
                    transitionIndices.Add(i);
            }

            int step = Math.Max(1, (int)Math.Ceiling(transitionIndices.Count * 80.0 / physicalLength));

            for (int i = 0; i < transitionIndices.Count; i += step)
            {
                int idx = transitionIndices[i];
                double ratio = (idx - range.Min) / range.Span;
                yield return new TickMathResult<double>(ratio, idx, false);
            }
        }
    }

    // ==========================================
    // 💥 6. 高度自适应 Y 轴策略
    // 【架构说明】：Y 轴纯计算，无需改造。
    // ==========================================
    public class AdaptiveYGridStrategy : ITickStrategy<double>
    {
        public IEnumerable<TickMathResult<double>> Calculate(RealRange range, double physicalHeight)
        {
            if (!range.IsValid || physicalHeight <= 0) yield break;

            int gridCount = physicalHeight > 150 ? 4 : 2;
            double step = range.Span / gridCount;

            for (int i = 0; i <= gridCount; i++)
            {
                double val = range.Min + step * i;
                if (Math.Abs(val) < 1e-6) val = 0;

                double ratio = (val - range.Min) / range.Span;
                yield return new TickMathResult<double>(ratio, val, val == 0);
            }
        }
    }

    // ==========================================
    // 💥 7. 智能交易时间策略
    // 【终极 0-GC 改造】：替换为 RefBox
    // ==========================================
    public class SmartTradeIntervalStrategy : ITickStrategy<double>
    {
        private readonly RefBox<ReadOnlyMemory<DateTime>> _timesBox;
        public SmartTradeIntervalStrategy(RefBox<ReadOnlyMemory<DateTime>> timesBox) => _timesBox = timesBox;

        public IEnumerable<TickMathResult<double>> Calculate(RealRange range, double physicalWidth)
        {
            var times = _timesBox.Value; // 💥 取最新切片

            if (!range.IsValid || times.IsEmpty || physicalWidth <= 0) yield break;

            int minIdx = Math.Max(0, (int)Math.Ceiling(range.Min));
            int maxIdx = Math.Min(times.Length - 1, (int)Math.Floor(range.Max));

            if (minIdx > maxIdx) yield break;

            double totalHours = (times.Span[maxIdx] - times.Span[minIdx]).TotalHours;
            if (totalHours <= 0) yield break;

            int intervalMinutes = (int)Math.Max(15, Math.Round(totalHours / physicalWidth * 120) * 15);
            DateTime lastTickTime = DateTime.MinValue;

            for (int i = minIdx; i <= maxIdx; i++)
            {
                DateTime currentTime = times.Span[i];

                if (currentTime.Minute % intervalMinutes == 0 && (currentTime - lastTickTime).TotalMinutes >= intervalMinutes)
                {
                    double ratio = (i - range.Min) / range.Span;
                    yield return new TickMathResult<double>(ratio, i, false);
                    lastTickTime = currentTime;
                }
            }
        }
    }
}
