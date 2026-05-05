using Hevo.Charting.Abstractions;

namespace Hevo.Charting.Buildin
{
    /// <summary>
    /// 💥 TradingView 同款算法：基于"优美数 (Nice Number)"的动态 Y 轴推导
    /// </summary>
    public class TradingViewYAxisMathStrategy : ITickStrategy
    {
        private readonly double _pixelsPerTick;
        private readonly int _minTicks;
        private readonly int _maxTicks;

        // 默认 maxTicks 给一个非常宽松的上限——master 行为本来就没硬限制，
        // 钳位主要是兜底防爆，不应该在 600px 中等高度的面板上就触发。
        public TradingViewYAxisMathStrategy(double pixelsPerTick = 50.0, int minTicks = 2, int maxTicks = 64)
        {
            _pixelsPerTick = pixelsPerTick > 0 ? pixelsPerTick : 50.0;
            _minTicks = System.Math.Max(2, minTicks);
            _maxTicks = System.Math.Max(_minTicks, maxTicks);
        }

        public IEnumerable<TickMathResult> Calculate(RealRange range, double physicalHeight)
        {
            if (range.IsEmpty || range.Span == 0 || physicalHeight <= 0) yield break;

            // 1. 像素密度自适应 + 上下限钳位（避免矮面板退化为 1 刻度 / 全屏图刻度过密）
            int desiredTicks = System.Math.Clamp((int)(physicalHeight / _pixelsPerTick), _minTicks, _maxTicks);

            // 2. 优美步长（与 NiceNumberYTickStrategy 共用算法收口）
            double roughStep = range.Span / desiredTicks;
            double actualStep = NiceStepHelper.NiceStep(roughStep);
            if (actualStep <= 0) yield break;

            // 3. 对齐网格起始点
            double startVal = System.Math.Ceiling(range.Min / actualStep) * actualStep;

            // 4. 整数迭代避免浮点累加丢边界刻度；0 标记为 baseline
            int count = (int)System.Math.Floor((range.Max - startVal) / actualStep + MathTolerance.NumericEqual) + 1;
            for (int i = 0; i < count; i++)
            {
                double val = startVal + i * actualStep;
                if (System.Math.Abs(val) < MathTolerance.NumericEqual) val = 0;
                yield return new TickMathResult(val, isAnchor: val == 0);
            }
        }
    }
}
