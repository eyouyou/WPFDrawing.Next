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

        public NiceNumberYTickStrategy(int desiredTicks = 5)
        {
            _desiredTicks = Math.Max(2, desiredTicks);
        }

        public IEnumerable<TickMathResult> Calculate(RealRange range, double physicalLength)
        {
            if (!range.IsValid || range.Span == 0) yield break;

            // 1. 动态适应屏幕高度：如果屏幕太矮，强制减少期望的刻度数
            int maxTicks = Math.Max(2, (int)(physicalLength / 40.0)); // 保证每个刻度至少间隔 40px
            int ticksToUse = Math.Min(_desiredTicks, maxTicks);

            // 2. 核心魔法：计算“优雅的步长 (Nice Step)”
            double roughStep = range.Span / (ticksToUse - 1);
            double niceStep = CalculateNiceStep(roughStep);

            // 3. 计算优美的起止点 (向下取整到 niceStep 的倍数)
            double niceMin = Math.Floor(range.Min / niceStep) * niceStep;
            double niceMax = Math.Ceiling(range.Max / niceStep) * niceStep;

            // 4. 生成刻度
            for (double val = niceMin; val <= niceMax + 1e-9; val += niceStep)
            {
                // 剔除不在当前真实范围内的刻度 (如果你希望刻度超出去，把这行删掉)
                // if (val < range.Min || val > range.Max) continue; 

                // 强制修正由于浮点数产生的 -0.0000000001
                if (Math.Abs(val) < 1e-9) val = 0;

                bool isBase = val == 0;

                yield return new TickMathResult(val, isBase);
            }
        }

        // 💥 d3.js 和 Highcharts 底层的经典近似算法
        private double CalculateNiceStep(double roughStep)
        {
            double exponent = Math.Floor(Math.Log10(roughStep));
            double fraction = roughStep / Math.Pow(10, exponent);
            double niceFraction;

            if (fraction <= 1.0) niceFraction = 1.0;
            else if (fraction <= 2.0) niceFraction = 2.0;
            else if (fraction <= 5.0) niceFraction = 5.0;
            else niceFraction = 10.0;

            return niceFraction * Math.Pow(10, exponent);
        }
    }
}
