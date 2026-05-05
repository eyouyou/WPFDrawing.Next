using Hevo.Charting.Abstractions;
using System.Collections.Generic;

namespace Hevo.Charting.Buildin
{
    /// <summary>
    /// 双轴副轴策略：按主轴当帧 tick 的归一化比例 (Ratio) 在副轴 RealRange 上反算 value,
    /// 让左右轴的横向网格 / 文字标签像素级对齐。
    /// 仅适合线性比例尺 (Y 值域)。要求宿主 Schema:
    /// <list type="bullet">
    ///   <item>持有同一个 <see cref="MirrorTickAnchor"/> 实例,既挂在主轴的 broadcastTicksTo,也挂在副轴的 mirrorFrom；</item>
    ///   <item>在 <c>axes.Add...</c> 注册顺序中主轴必须先于副轴,保证同帧主轴先广播、副轴再读取。</item>
    /// </list>
    /// </summary>
    public sealed class MirrorRatioTickStrategy : ITickStrategy
    {
        private readonly MirrorTickAnchor _anchor;

        public MirrorRatioTickStrategy(MirrorTickAnchor anchor)
        {
            _anchor = anchor ?? throw new System.ArgumentNullException(nameof(anchor));
        }

        public IEnumerable<TickMathResult> Calculate(RealRange range, double physicalLength)
        {
            if (!range.IsValid || range.Span <= 0 || physicalLength <= 0) yield break;

            int count = _anchor.Count;
            for (int i = 0; i < count; i++)
            {
                double r = _anchor.GetRatio(i);
                if (r < 0 || r > 1) continue;
                // 线性反归一化：value = min + r * span。AxisFeature 随后会再做一次 scale.Normalize,
                // 线性比例尺下结果回到原 ratio,保证副轴标签像素对齐主轴 grid。
                double val = range.Min + r * range.Span;
                yield return new TickMathResult(val, _anchor.GetIsAnchor(i));
            }
        }
    }
}
