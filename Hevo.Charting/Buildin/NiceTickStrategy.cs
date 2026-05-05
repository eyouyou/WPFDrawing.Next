using Hevo.Charting.Abstractions;

namespace Hevo.Charting.Buildin
{
    /// <summary>
    /// 💥 工业级优雅 Y 轴刻度策略 (Nice Number Tick Strategy)
    /// 无论 AutoScale 给的极值多丑 (如 12.3~87.6)，都能算出完美的刻度 (如 20, 40, 60, 80)
    /// </summary>
    public class NiceNumberYTickStrategy : ITickStrategy
    {
        private readonly int _desiredTicks;
        private readonly double _minPixelsPerTick;

        /// <param name="desiredTicks">期望刻度个数(实际值会按 minPixelsPerTick 二次约束)。</param>
        /// <param name="minPixelsPerTick">每个刻度最少占多少像素 — 屏幕矮时按此削减刻度密度。
        /// 默认 40 比同族(NicePercent / NiceQuantity 用 50)略密,适配 Y 轴常见的紧凑高度。</param>
        public NiceNumberYTickStrategy(int desiredTicks = 5, double minPixelsPerTick = 40.0)
        {
            _desiredTicks = Math.Max(2, desiredTicks);
            _minPixelsPerTick = minPixelsPerTick > 0 ? minPixelsPerTick : 40.0;
        }

        public IEnumerable<TickMathResult> Calculate(RealRange range, double physicalLength)
        {
            if (!range.IsValid || range.Span == 0) yield break;

            // 1. 动态适应屏幕高度:屏幕太矮强制削减刻度数,保证每条 tick 之间不少于 minPixelsPerTick
            int maxTicks = Math.Max(2, (int)(physicalLength / _minPixelsPerTick));
            int ticksToUse = Math.Min(_desiredTicks, maxTicks);

            // 2. 核心魔法：计算“优雅的步长 (Nice Step)”
            double roughStep = range.Span / (ticksToUse - 1);
            double niceStep = NiceStepHelper.NiceStep(roughStep);
            if (niceStep <= 0) yield break;

            // 3. 计算优美的起止点 (向下取整到 niceStep 的倍数)
            double niceMin = Math.Floor(range.Min / niceStep) * niceStep;
            double niceMax = Math.Ceiling(range.Max / niceStep) * niceStep;

            // 4. 生成刻度
            for (double val = niceMin; val <= niceMax + MathTolerance.NumericEqual; val += niceStep)
            {
                // 剔除不在当前真实范围内的刻度 (如果你希望刻度超出去，把这行删掉)
                // if (val < range.Min || val > range.Max) continue;

                // 强制修正由于浮点数产生的 -0.0000000001
                if (Math.Abs(val) < MathTolerance.NumericEqual) val = 0;

                bool isBase = val == 0;

                yield return new TickMathResult(val, isBase);
            }
        }

    }
}
