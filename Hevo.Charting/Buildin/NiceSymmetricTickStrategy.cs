using Hevo.Charting.Abstractions;

namespace Hevo.Charting.Buildin
{
    /// <summary>
    /// 围绕 0 / 围绕参考值对称域专用刻度策略。
    /// 与 AutoScaleStrategy.SymmetricZero / SymmetricReference 配合使用。
    /// </summary>
    /// <remarks>
    /// 单边数据保护：如果 <see cref="RealRange"/> 实际不跨越 center（比如股价之类的纯正数），
    /// 强行展开成对称会把整个 y 轴拉到 -X..+X，数据被压到一侧出现大片空白。
    /// 此时退化为按实际 range 做 nice tick（普通 TV 风格），不再强对称。
    /// </remarks>
    public class NiceSymmetricTickStrategy : ITickStrategy
    {
        private readonly int _gridPerSide;
        private readonly double _center;
        private readonly double _pixelsPerTick;

        public NiceSymmetricTickStrategy(int gridPerSide = 2, double center = 0.0, double pixelsPerTick = 50.0)
        {
            _gridPerSide = System.Math.Max(1, gridPerSide);
            _center = center;
            _pixelsPerTick = pixelsPerTick > 0 ? pixelsPerTick : 50.0;
        }

        public IEnumerable<TickMathResult> Calculate(RealRange range, double physicalLength)
        {
            if (!range.IsValid || range.Span <= 0 || physicalLength <= 0) yield break;

            // 真正跨过 center 才走对称展开；不跨过则退化为普通 nice，避免硬拉空间
            bool crossesCenter = range.Min < _center && range.Max > _center;
            if (!crossesCenter)
            {
                int desired = System.Math.Max(2, (int)(physicalLength / _pixelsPerTick));
                double rough = range.Span / desired;
                double step = NiceStepHelper.NiceStep(rough);
                if (step <= 0) yield break;

                double start = System.Math.Ceiling(range.Min / step) * step;
                int n = (int)System.Math.Floor((range.Max - start) / step + MathTolerance.NumericEqual) + 1;
                for (int i = 0; i < n; i++)
                {
                    double val = start + i * step;
                    // 退化分支:可能遭遇大数 step,用相对容差 ε·step 而非绝对 ε
                    if (System.Math.Abs(val) < MathTolerance.NumericEqual * step) val = 0;
                    yield return new TickMathResult(val, isAnchor: val == _center);
                }
                yield break;
            }

            double half = System.Math.Max(System.Math.Abs(range.Max - _center), System.Math.Abs(range.Min - _center));
            if (half <= 0) yield break;

            double niceStep = NiceStepHelper.NiceStep(half / _gridPerSide);
            if (niceStep <= 0) yield break;

            for (int i = -_gridPerSide; i <= _gridPerSide; i++)
            {
                double val = _center + i * niceStep;
                if (System.Math.Abs(val) < MathTolerance.NumericEqual) val = 0;
                yield return new TickMathResult(val, isAnchor: i == 0);
            }
        }
    }
}
