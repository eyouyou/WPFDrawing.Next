using Hevo.Charting.Abstractions;

namespace Hevo.Charting.Buildin
{
    // ==========================================
    // 💥 老框架 XAxisSecurityTradeMinuteTime 的等价实现 (Schedule-driven, ruler-backed)
    //
    // 输入只两件事:
    //   - totalLengthProvider:全网格长度(对应 ruler.TotalLength,已计算好直接用)
    //   - indexToTime:逻辑索引 → 实际时间(对应 ruler.IndexToTime,跨越休市自动跳过)
    //
    // 业务侧 (TimeShareDataSource) 把 ruler 的 TotalLength / IndexToTime 转成 Func 喂进来,
    // 框架侧 0 业务依赖。strategy 完全不知道 ruler / sessions / barInterval,
    // 用扫相邻 delta 检测 session 跳变(同 TradingViewTimeMathStrategy)。
    //
    // 算法对齐老代码 XAxisSecurityTradeMinuteTime:
    //   - 步长 offset = MinUnit × round(effectiveHours / width × 60) × 2
    //   - session 内整点起跳 → +offset → 直到越过 session 末或离 session 末 < MinUnit/2 → 停
    //   - session 末位强发 (老代码的 featurePoints)
    //   - 头 (viewStart) / 尾 (viewEnd) 强发 (新增,补偿新框架无 axis 视觉贴边)
    //   - session break 只发上段末,下段始让位给新段第一个 nice tick
    // ==========================================
    public class TradeTimeTickStrategy : ITickStrategy
    {
        private readonly Func<int> _totalLengthProvider;
        private readonly Func<int, DateTime> _indexToTime;
        private readonly long _minUnitTicks;
        private readonly double _gapMultiplier;

        /// <param name="totalLengthProvider">每帧返回全网格逻辑长度(典型:<c>() => dataSource.LogicalLength</c>)。</param>
        /// <param name="indexToTime">逻辑索引 → 实际时间,跨越休市自动跳过(典型:<c>idx => dataSource.IndexToTime(idx)</c>)。</param>
        /// <param name="minUnit">基础步长单位,也是"近邻 session 末"过滤半径(MinUnit/2)。默认 30min。</param>
        /// <param name="gapMultiplier">相邻 delta 超过"典型 × 该倍数"判为 session 切换。默认 3.0。</param>
        public TradeTimeTickStrategy(
            Func<int> totalLengthProvider,
            Func<int, DateTime> indexToTime,
            TimeSpan? minUnit = null,
            double gapMultiplier = 3.0)
        {
            _totalLengthProvider = totalLengthProvider ?? throw new ArgumentNullException(nameof(totalLengthProvider));
            _indexToTime = indexToTime ?? throw new ArgumentNullException(nameof(indexToTime));
            _minUnitTicks = (minUnit ?? TimeSpan.FromMinutes(30)).Ticks;
            _gapMultiplier = Math.Max(1.5, gapMultiplier);
        }

        public IEnumerable<TickMathResult> Calculate(RealRange logicalRange, double physicalWidth)
        {
            if (!logicalRange.IsValid || logicalRange.Span <= 0 || physicalWidth <= 0) yield break;

            int totalLen = _totalLengthProvider();
            if (totalLen <= 0) yield break;

            int startIdx = Math.Max(0, (int)Math.Floor(logicalRange.Min));
            int endIdx = Math.Min(totalLen - 1, (int)Math.Ceiling(logicalRange.Max));
            if (startIdx > endIdx) yield break;
            if (startIdx == endIdx)
            {
                yield return new TickMathResult(startIdx, isAnchor: false);
                yield break;
            }

            // === 推断典型 bar 间隔 + session 阈值 ===
            long typicalTicks = InferTypicalBarTicks(_indexToTime, startIdx, endIdx);
            long gapTicks = (long)(typicalTicks * _gapMultiplier);

            int barCount = endIdx - startIdx + 1;
            double effectiveHours = typicalTicks * barCount / (double)TimeSpan.TicksPerHour;
            double ratio = Math.Round(effectiveHours / physicalWidth * 60) * 2;
            if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio < 1) ratio = 1;
            long stepTicks = _minUnitTicks * (long)ratio;
            long halfMinUnitTicks = _minUnitTicks / 2;

            // === 头强发 ===
            yield return new TickMathResult(startIdx, isAnchor: false);
            int lastEmitted = startIdx;

            // 步进游标:整点向下 → MinUnit 跳到 ≥ startTime → +stepTicks
            DateTime startTime = _indexToTime(startIdx);
            long cursor = (startTime.Ticks / TimeSpan.TicksPerHour) * TimeSpan.TicksPerHour;
            while (cursor < startTime.Ticks && (startTime.Ticks - cursor) > halfMinUnitTicks)
                cursor += _minUnitTicks;
            cursor += stepTicks;

            DateTime prevTime = startTime;

            // === 主循环:session 跳变 + nice tick ===
            for (int i = startIdx + 1; i <= endIdx; i++)
            {
                DateTime t = _indexToTime(i);
                long delta = (t - prevTime).Ticks;
                bool isSessionBreak = delta > gapTicks;

                if (isSessionBreak)
                {
                    // 上段末 (i-1) 强发,下段始 (i) 让位给新段第一个 nice tick(避免 1 根 bar 内俩 label 撞)
                    if (i - 1 > lastEmitted)
                    {
                        yield return new TickMathResult(i - 1, isAnchor: false);
                        lastEmitted = i - 1;
                    }
                    // 重置 cursor 到新 session 起点对齐
                    cursor = (t.Ticks / TimeSpan.TicksPerHour) * TimeSpan.TicksPerHour;
                    while (cursor < t.Ticks && (t.Ticks - cursor) > halfMinUnitTicks)
                        cursor += _minUnitTicks;
                    cursor += stepTicks;
                    prevTime = t;
                    continue;
                }

                // nice tick:t 跨过 cursor → 候选;尾留给最后强发,这里不和 endIdx 抢
                if (t.Ticks >= cursor && i != endIdx)
                {
                    if (i > lastEmitted)
                    {
                        yield return new TickMathResult(i, isAnchor: false);
                        lastEmitted = i;
                    }
                    while (cursor <= t.Ticks) cursor += stepTicks;
                }
                prevTime = t;
            }

            // === 尾强发 ===
            if (endIdx > lastEmitted)
                yield return new TickMathResult(endIdx, isAnchor: false);
        }

        /// <summary>取相邻 delta 最小值作为典型 bar 间隔(取样 10 对,默认回退 1 分钟)。</summary>
        private static long InferTypicalBarTicks(Func<int, DateTime> indexToTime, int startIdx, int endIdx)
        {
            long minTicks = long.MaxValue;
            int sampleEnd = Math.Min(startIdx + 10, endIdx);
            DateTime prev = indexToTime(startIdx);
            for (int i = startIdx + 1; i <= sampleEnd; i++)
            {
                DateTime cur = indexToTime(i);
                long delta = (cur - prev).Ticks;
                if (delta > 0 && delta < minTicks) minTicks = delta;
                prev = cur;
            }
            return minTicks == long.MaxValue ? TimeSpan.FromMinutes(1).Ticks : minTicks;
        }
    }
}
