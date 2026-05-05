using Hevo.Charting.Abstractions;

namespace Hevo.Charting.Buildin
{
    /// <summary>
    /// 大数（亿/万级）专用刻度策略。直接在原始量纲上做 nice，由 formatter 负责亿/万的展示。
    /// 适用于成交额 / 余额 / 净额等大数业务。
    /// </summary>
    /// <remarks>
    /// 注意：早期版本会先按量级归一（如除以 1e8）再 nice，再乘回去——这会强制 step 对齐到
    /// {1,2,5,10}×1e8，丢掉 0.5 亿这样的中间档，刻度变粗。直接在原始量纲做 nice 即可，
    /// nice 算法本身就有数量级感知。
    /// </remarks>
    public class NiceQuantityTickStrategy : ITickStrategy
    {
        private readonly double _pixelsPerTick;
        private readonly int _minTicks;
        private readonly int _maxTicks;

        public NiceQuantityTickStrategy(double pixelsPerTick = 50.0, int minTicks = 2, int maxTicks = 64)
        {
            _pixelsPerTick = pixelsPerTick > 0 ? pixelsPerTick : 50.0;
            _minTicks = System.Math.Max(2, minTicks);
            _maxTicks = System.Math.Max(_minTicks, maxTicks);
        }

        public IEnumerable<TickMathResult> Calculate(RealRange range, double physicalLength)
        {
            if (!range.IsValid || range.Span <= 0 || physicalLength <= 0) yield break;

            int desired = System.Math.Clamp((int)(physicalLength / _pixelsPerTick), _minTicks, _maxTicks);
            double step = NiceStepHelper.NiceStep(range.Span / desired);
            if (step <= 0) yield break;

            double start = System.Math.Ceiling(range.Min / step) * step;
            int count = (int)System.Math.Floor((range.Max - start) / step + MathTolerance.NumericEqual) + 1;
            for (int i = 0; i < count; i++)
            {
                double val = start + i * step;
                // 大数策略下绝对容差 1e-9 不够,因为 step 可能是 1e8 量级,残差正比于 step → 用相对容差
                if (System.Math.Abs(val) < MathTolerance.NumericEqual * step) val = 0;
                yield return new TickMathResult(val, isAnchor: val == 0);
            }
        }
    }
}
