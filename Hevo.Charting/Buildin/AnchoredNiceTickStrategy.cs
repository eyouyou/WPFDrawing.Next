using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;

namespace Hevo.Charting.Buildin
{
    /// <summary>
    /// 锚点优先的 nice tick 策略：grid 直接以 anchor 为原点向外铺开，避免"先 nice 再修正"导致的
    /// baseline 双重定义、anchor 替换破坏等距等问题。
    ///
    /// <para>语义：</para>
    /// <list type="bullet">
    ///   <item><c>anchor</c>：grid 原点 + 唯一 baseline（典型：分时图昨收价 / 双时分图 0%）</item>
    ///   <item><c>hints</c>：两个候选锚点（典型：当日 high/low）。距离 grid 太近时会让位临近 grid，
    ///         但不会让位 anchor —— anchor 永远精确显示。</item>
    /// </list>
    /// </summary>
    public class AnchoredNiceTickStrategy : ITickStrategy
    {
        private readonly RefBox<double>? _anchor;
        private readonly RefBox<RealRange>? _hints;
        private readonly double _pixelsPerTick;
        private readonly int _minTicks;
        private readonly int _maxTicks;
        private readonly double _displaceFactor;

        public AnchoredNiceTickStrategy(
            RefBox<double>? anchor = null,
            RefBox<RealRange>? hints = null,
            double pixelsPerTick = 50.0,
            int minTicks = 2,
            int maxTicks = 64,
            double displaceFactor = 0.5)
        {
            _anchor = anchor;
            _hints = hints;
            _pixelsPerTick = pixelsPerTick > 0 ? pixelsPerTick : 50.0;
            _minTicks = System.Math.Max(2, minTicks);
            _maxTicks = System.Math.Max(_minTicks, maxTicks);
            _displaceFactor = System.Math.Clamp(displaceFactor, 0.0, 1.0);
        }

        public IEnumerable<TickMathResult> Calculate(RealRange range, double physicalLength)
        {
            if (range.IsEmpty || range.Span <= 0 || physicalLength <= 0) yield break;

            int desired = System.Math.Clamp((int)(physicalLength / _pixelsPerTick), _minTicks, _maxTicks);
            double step = NiceStepHelper.NiceStep(range.Span / desired);
            if (step <= 0) yield break;

            // 1. 决定 grid 原点：有效 anchor 直接当原点；否则退化为标准 ceil 对齐
            double anchorValue = _anchor?.Value ?? double.NaN;
            bool hasAnchor = !double.IsNaN(anchorValue) && range.Contains(anchorValue);
            double origin = hasAnchor
                ? anchorValue
                : System.Math.Ceiling(range.Min / step) * step;

            // 2. 生成 grid（hasAnchor 时 i==0 即 anchor，永远精确出现）
            int firstIdx = (int)System.Math.Ceiling((range.Min - origin) / step - MathTolerance.NumericEqual);
            int lastIdx = (int)System.Math.Floor((range.Max - origin) / step + MathTolerance.NumericEqual);

            int capacity = System.Math.Max(0, lastIdx - firstIdx + 1) + 2;
            var ticks = new List<TickMathResult>(capacity);
            for (int i = firstIdx; i <= lastIdx; i++)
            {
                double val = origin + i * step;
                // 大数 step 用相对容差 ε·step;详见 NiceQuantityTickStrategy 同类注释
                if (System.Math.Abs(val) < MathTolerance.NumericEqual * step) val = 0;
                bool isBase = hasAnchor && i == 0;
                ticks.Add(new TickMathResult(val, isAnchor: isBase));
            }

            // 3. hints：让位临近 grid，但不让位 baseline
            if (_hints != null)
            {
                var h = _hints.Value;
                if (h.IsValid)
                {
                    ApplyHint(ticks, h.Min, step * _displaceFactor, range);
                    ApplyHint(ticks, h.Max, step * _displaceFactor, range);
                }
            }

            ticks.Sort(static (a, b) => a.Value.CompareTo(b.Value));
            foreach (var t in ticks)
            {
                yield return t;
            }
        }

        private static void ApplyHint(List<TickMathResult> ticks, double value, double displaceThreshold, RealRange range)
        {
            if (double.IsNaN(value) || !range.Contains(value)) return;

            int nearestIdx = -1;
            double nearestDist = double.MaxValue;
            for (int i = 0; i < ticks.Count; i++)
            {
                double d = System.Math.Abs(ticks[i].Value - value);
                if (d < nearestDist)
                {
                    nearestDist = d;
                    nearestIdx = i;
                }
            }

            if (nearestIdx < 0)
            {
                ticks.Add(new TickMathResult(value, isAnchor: false));
                return;
            }

            // baseline 永远不让位：hint 离 baseline 太近 → 弃；够远 → 单独追加
            if (ticks[nearestIdx].IsAnchor)
            {
                if (nearestDist > displaceThreshold)
                {
                    ticks.Add(new TickMathResult(value, isAnchor: false));
                }
                return;
            }

            // 普通 grid tick：太近就让位，否则追加
            if (nearestDist <= displaceThreshold)
            {
                ticks[nearestIdx] = new TickMathResult(value, isAnchor: false);
            }
            else
            {
                ticks.Add(new TickMathResult(value, isAnchor: false));
            }
        }
    }
}
