using Hevo.Charting.Abstractions;

namespace Hevo.Charting.Buildin
{
    /// <summary>
    /// 💥 TradingView 同款算法：基于“优美数 (Nice Number)”的动态 Y 轴推导
    /// </summary>
    public class TradingViewYAxisMathStrategy : ITickStrategy<double>
    {
        public IEnumerable<TickMathResult<double>> Calculate(RealRange range, double physicalHeight)
        {
            if (range.IsEmpty || range.Span == 0 || physicalHeight <= 0) yield break;

            // 1. 密度自适应：大约每 50 像素放一个刻度
            int desiredTicks = Math.Max(2, (int)(physicalHeight / 50.0));

            // 2. 粗略步长
            double roughStep = range.Span / desiredTicks;

            // 3. 💥 核心魔法：计算数量级 (Magnitude) 和 优美步长 (Nice Step)
            // 比如 roughStep 是 0.07，它会变成 0.1；如果是 34，会变成 50。
            double mag = Math.Pow(10, Math.Floor(Math.Log10(roughStep)));
            double norm = roughStep / mag;

            double niceMultiplier = norm < 1.5 ? 1.0 : (norm < 3.0 ? 2.0 : (norm < 7.0 ? 5.0 : 10.0));
            double actualStep = niceMultiplier * mag;

            // 4. 对齐网格起始点 (Snap to grid)
            double startVal = Math.Ceiling(range.Min / actualStep) * actualStep;

            // 5. 生成极其优美的刻度！
            for (double val = startVal; val <= range.Max; val += actualStep)
            {
                double ratio = (val - range.Min) / range.Span;
                yield return new TickMathResult<double>(ratio, val, isBaseLine: false); // 0 值或昨收可以通过后续修饰器判断
            }
        }
    }
}
