using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;

namespace Hevo.Charting.Buildin
{
    // ==========================================
    // 💥 Session 感知版时间轴策略
    //
    // 在 TradingViewTimeMathStrategy 的 nice-interval 算法基础上,叠加三层保护:
    //   1. 头 (startIdx) 永远发,绕过 minRatioGap 过滤
    //   2. 尾 (endIdx) 永远发,绕过 minRatioGap 过滤
    //   3. session 边界:扫到 "delta > 典型间隔 × GapMultiplier" 的位置,
    //      在切换两侧 (i-1, i) 各强发一根,nice-interval 在新 session 内重新对齐
    //
    // 适用场景:
    //   - A 股分时:09:30 / 11:30 / 13:00 / 15:00 + nice 中间刻度
    //   - 港股分时:09:30 / 12:00 / 13:00 / 16:00 + nice 中间刻度
    //   - 多日 K 线:每个交易日开收盘 + 内部 nice 网格
    //
    // 完全数据驱动——session 由数据相邻间隔披露,不依赖 ruler / 固定时间表,
    // 跨市场零配置。
    // ==========================================
    public class SessionAwareTimeMathStrategy : ITickStrategy
    {
        private readonly RefBox<ReadOnlyMemory<DateTime>> _timesBox;
        private readonly double _gapMultiplier;
        private readonly double _minPixelGap;

        /// <param name="timesBox">长生命周期数据盒子(0-GC)。</param>
        /// <param name="gapMultiplier">间隔超过"典型间隔 × 该倍数"判为 session 切换。默认 3.0。</param>
        /// <param name="minPixelGap">nice tick 最小像素间距(头/尾/session 边界不受此限制)。默认 80px。</param>
        public SessionAwareTimeMathStrategy(RefBox<ReadOnlyMemory<DateTime>> timesBox, double gapMultiplier = 3.0, double minPixelGap = 80.0)
        {
            _timesBox = timesBox;
            _gapMultiplier = Math.Max(1.5, gapMultiplier);
            _minPixelGap = Math.Max(0, minPixelGap);
        }

        public IEnumerable<TickMathResult> Calculate(RealRange logicalRange, double physicalWidth)
        {
            var times = _timesBox.Value;

            if (!logicalRange.IsValid || logicalRange.Span <= 0 || physicalWidth <= 0 || times.IsEmpty)
                yield break;

            int startIdx = Math.Max(0, (int)Math.Floor(logicalRange.Min));
            int endIdx = Math.Min(times.Length - 1, (int)Math.Ceiling(logicalRange.Max));
            if (startIdx > endIdx) yield break;

            // 单点直接发
            if (startIdx == endIdx)
            {
                yield return new TickMathResult(startIdx, isAnchor: false);
                yield break;
            }

            // === nice-interval 准备(沿用 TradingView 算法) ===
            // 用"bar 数 × 典型 bar 间隔"代替墙钟差,跳过午休/隔夜等非交易黑洞
            long typicalTicks = InferTypicalBarTicks(times, startIdx, endIdx);
            int barCount = endIdx - startIdx + 1;
            TimeSpan effectiveTime = TimeSpan.FromTicks(typicalTicks * barCount);

            int desiredTicks = Math.Max(2, (int)(physicalWidth / 150.0));
            TimeSpan roughInterval = TimeSpan.FromTicks(effectiveTime.Ticks / desiredTicks);
            TimeSpan niceInterval = GetNiceTimeInterval(roughInterval);

            // === session 边界准备 ===
            long gapTicks = (long)(typicalTicks * _gapMultiplier);
            double minRatioGap = _minPixelGap / physicalWidth;
            int lookaheadBars = (int)Math.Ceiling(minRatioGap * logicalRange.Span);

            // === 头无条件发 ===
            yield return new TickMathResult(startIdx, isAnchor: false);
            int lastEmittedIdx = startIdx;
            double lastRatio = (startIdx - logicalRange.Min) / logicalRange.Span;
            // 推进 nice boundary 到 startIdx 之后,避免重复发头
            DateTime currentBoundary = RoundUp(times.Span[startIdx].AddTicks(1), niceInterval);

            // === 中段循环:session 边界 + nice tick ===
            for (int i = startIdx + 1; i <= endIdx; i++)
            {
                DateTime t = times.Span[i];
                long delta = (t - times.Span[i - 1]).Ticks;
                bool isSessionBreak = delta > gapTicks;

                if (isSessionBreak)
                {
                    // 只发上段末 (pre-gap i-1):跟下段始 (post-gap i) 仅相隔 1 根 bar,标签必撞,
                    //                         post-gap 跳过——新段第一个 nice tick 会自然补位。
                    if (i - 1 > lastEmittedIdx)
                    {
                        yield return new TickMathResult(i - 1, isAnchor: false);
                        lastEmittedIdx = i - 1;
                        lastRatio = (i - 1 - logicalRange.Min) / logicalRange.Span;
                    }
                    // nice boundary 跟着新 session 起点重对齐
                    currentBoundary = RoundUp(t.AddTicks(1), niceInterval);
                    continue;
                }

                // nice tick:t 跨过下一个 nice 边界 → 候选;尾留给最后强发,这里不和 endIdx 抢
                if (t >= currentBoundary && i != endIdx)
                {
                    double ratio = (i - logicalRange.Min) / logicalRange.Span;

                    // 前瞻:若 minRatioGap 范围内有 session break,nice tick 让位给上段末(避免 11:00 / 11:30 撞)
                    bool yieldsToBreak = HasSessionBreakWithin(times, i, lookaheadBars, gapTicks);

                    if (!yieldsToBreak && ratio - lastRatio >= minRatioGap)
                    {
                        yield return new TickMathResult(i, isAnchor: false);
                        lastEmittedIdx = i;
                        lastRatio = ratio;
                    }
                    currentBoundary = RoundUp(t.AddTicks(1), niceInterval);
                }
            }

            // === 尾无条件发(绕过 minRatioGap) ===
            if (endIdx > lastEmittedIdx)
                yield return new TickMathResult(endIdx, isAnchor: false);
        }

        /// <summary>从 fromIdx 往后 lookahead 根 bar 内是否存在 session break。
        /// 函数内部短命取 Span,不让 Span 逃逸到 enumerator。</summary>
        private static bool HasSessionBreakWithin(ReadOnlyMemory<DateTime> times, int fromIdx, int lookahead, long gapTicks)
        {
            var span = times.Span;
            int end = Math.Min(span.Length - 1, fromIdx + lookahead);
            for (int j = fromIdx + 1; j <= end; j++)
            {
                if ((span[j] - span[j - 1]).Ticks > gapTicks) return true;
            }
            return false;
        }

        // ===== 算法辅助函数(完整复用 TradingViewTimeMathStrategy 的逻辑) =====

        /// <summary>取相邻 delta 的最小值作为典型 bar 间隔(避开 session 跨越的大跳)。
        /// 取样最多 10 对,默认回退 1 分钟。函数内部短命取 Span,不让 Span 逃逸到 enumerator。</summary>
        private static long InferTypicalBarTicks(ReadOnlyMemory<DateTime> times, int startIdx, int endIdx)
        {
            var span = times.Span;
            long minTicks = long.MaxValue;
            int sampleEnd = Math.Min(startIdx + 10, endIdx);
            for (int i = startIdx; i < sampleEnd; i++)
            {
                long delta = (span[i + 1] - span[i]).Ticks;
                if (delta > 0 && delta < minTicks) minTicks = delta;
            }
            return minTicks == long.MaxValue ? TimeSpan.FromMinutes(1).Ticks : minTicks;
        }

        private static TimeSpan GetNiceTimeInterval(TimeSpan rough)
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

        private static DateTime RoundUp(DateTime dt, TimeSpan d)
        {
            long ticks = (dt.Ticks + d.Ticks - 1) / d.Ticks;
            return new DateTime(ticks * d.Ticks, dt.Kind);
        }
    }
}
