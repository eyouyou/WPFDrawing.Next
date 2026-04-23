using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Features
{
    public enum ViewportAlignment { RightEdge, LeftEdge }

    /// <summary>
    /// 视口越界策略：用户拖到数据边界外时怎么处理。
    /// </summary>
    public enum OverscrollPolicy
    {
        /// <summary>硬钳位：UserRange 越界时强制拉回数据范围。简单稳定，适合数据量固定的业务。</summary>
        Hard,
        /// <summary>越界留白：允许 UserRange 超出数据范围，超出部分显示为空白。
        /// 适合分页业务（K 线）：用户的拖拽意图直接保留，分页加载完成后由真实数据填充空白。</summary>
        Overscroll
    }

    /// <summary>
    /// 视口跨度限制 trait（视图层产生，交互层消费）：
    /// <list type="bullet">
    ///   <item>生产者：<see cref="ViewportManagerFeature"/> —— 它持有 <see cref="ViewportManagerFeature.MinVisibleCount"/> 与 <see cref="ViewportManagerFeature.MaxSpanMultiplier"/> 配置</item>
    ///   <item>消费者：<see cref="ChartInteractionFeature"/>.HandleZoom —— 构造 <c>ZoomContext.Limits</c> 喂给策略 clamp</item>
    /// </list>
    /// 注意：MaxSpanMultiplier 是相对乘数，需要结合当前 LogicalLength 才能解析为绝对 MaxSpan。
    /// </summary>
    public record ViewportSpanLimitsTrait(double MinSpan, double MaxSpanMultiplier) : IVisualTrait;

    public class ViewportManagerFeature : ChartFeature
    {
        public override FeaturePhase Phase => FeaturePhase.Layout;

        public ViewportAlignment Alignment { get; init; } = ViewportAlignment.RightEdge;

        /// <summary>视口最少需要包含几根数据（限制最大放大倍数）。同时也是 SafeBuffer 的下限。</summary>
        public double MinVisibleCount { get; init; } = 3;

        /// <summary>初次布局时显示几根数据（null = 显示全部）。分页业务必传，否则视图贴死数据两端导致 pan 无反应。</summary>
        public double? DefaultVisibleCount { get; init; }

        /// <summary>视口最大可缩放至数据量的几倍（限制最大缩小倍数）。
        /// 注意：这是**视口跨度的硬性约束**，应用于所有 UserRange 写入（Pan/Zoom 都会被钳制），
        /// **不是缩放算法参数**。缩放具体行为由 <see cref="Buildin.IZoomStrategy"/> 决定。</summary>
        public double MaxSpanMultiplier { get; init; } = 1.5;

        public OverscrollPolicy OverscrollMin { get; init; } = OverscrollPolicy.Hard;
        public OverscrollPolicy OverscrollMax { get; init; } = OverscrollPolicy.Hard;
        public double HistoryLoadTolerance { get; init; } = 30;
        public double RightEdgeStickTolerance { get; init; } = 2;

        private int _lastLogicalLength = -1;
        private RealRange _lastProcessedUserRange = new RealRange(double.NaN, double.NaN);

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            ctx.Shared().PublishData(new ViewportSpanLimitsTrait(Math.Max(0, MinVisibleCount - 1), MaxSpanMultiplier));

            flow.Watch(new object[] { Viewport.LogicalLength, Viewport.UserRange }, board =>
            {
                using (board.AcquireUpgradeableReadLock())
                {
                    int currentLength = board.Read(Viewport.LogicalLength);
                    var userRange = board.Read(Viewport.UserRange);
                    var activeRange = board.Read(Viewport.ActiveRange);

                    if (currentLength <= 0)
                    {
                        using (board.AcquireWriteLock()) board.WriteIfChanged(Viewport.ActiveRange, new RealRange(0, 1));
                        return;
                    }

                    double maxIndex = currentLength - 1;
                    double minSpan = Math.Max(0, MinVisibleCount - 1);
                    RealRange targetRange;

                    if (_lastLogicalLength <= 0)
                    {
                        double initialSpan = DefaultVisibleCount.HasValue ? Math.Max(minSpan, Math.Min(DefaultVisibleCount.Value - 1, maxIndex)) : Math.Max(minSpan, maxIndex);
                        targetRange = new RealRange(maxIndex - initialSpan, maxIndex);
                    }
                    else if (currentLength > _lastLogicalLength)
                    {
                        int delta = currentLength - _lastLogicalLength;
                        double lastMaxIndex = _lastLogicalLength - 1;

                        if (activeRange.IsValid && activeRange.Max >= lastMaxIndex - RightEdgeStickTolerance) targetRange = new RealRange(activeRange.Min + delta, activeRange.Max + delta);
                        else if (activeRange.IsValid && activeRange.Min <= HistoryLoadTolerance) targetRange = new RealRange(activeRange.Min + delta, activeRange.Max + delta);
                        else targetRange = activeRange;
                    }
                    else
                    {
                        bool isNewUserIntent = userRange.IsValid && !userRange.Equals(_lastProcessedUserRange);
                        if (isNewUserIntent) { targetRange = userRange; _lastProcessedUserRange = userRange; }
                        else targetRange = activeRange;
                    }

                    _lastLogicalLength = currentLength;

                    // 💥 物理钳制：重心锁定 + 终极虚空法则
                    double targetSpan = targetRange.Span;
                    double targetCenter = targetRange.Min + targetSpan / 2.0;
                    double clampedSpan = Math.Clamp(targetSpan, minSpan, Math.Max(minSpan, maxIndex * MaxSpanMultiplier));

                    double min = targetCenter - clampedSpan / 2.0;
                    double max = targetCenter + clampedSpan / 2.0;

                    // 💥 边界处理：分两步走
                    //   Step 1：Span 超过数据时（极致缩小），按 Alignment 选择停靠边
                    //   Step 2：Span 在数据范围内，对各边界按 OverscrollPolicy 决定钳/越
                    if (clampedSpan > maxIndex)
                    {
                        if (Alignment == ViewportAlignment.RightEdge) { max = maxIndex; min = max - clampedSpan; }
                        else { min = 0; max = clampedSpan; }
                    }
                    else
                    {
                        // 钳位顺序：Alignment 决定哪边优先被检查（避免两边同时越界时反向覆盖）
                        if (Alignment == ViewportAlignment.RightEdge)
                        {
                            ApplyMaxBoundary(ref min, ref max, maxIndex, clampedSpan, OverscrollMax);
                            ApplyMinBoundary(ref min, ref max, maxIndex, clampedSpan, OverscrollMin);
                        }
                        else
                        {
                            ApplyMinBoundary(ref min, ref max, maxIndex, clampedSpan, OverscrollMin);
                            ApplyMaxBoundary(ref min, ref max, maxIndex, clampedSpan, OverscrollMax);
                        }
                    }

                    // 💥 绝对单向流：管家只负责输出现实，不再管意图！打破一切死锁。
                    using (board.AcquireWriteLock()) board.WriteIfChanged(Viewport.ActiveRange, new RealRange(min, max));
                }
            });

        }

        protected override void OnProject(FeatureContext ctx) { }
        public void ResetState() { _lastLogicalLength = -1; _lastProcessedUserRange = new RealRange(double.NaN, double.NaN); }

        // 💥 边界判定原子操作：Hard 钳回 maxIndex/0；Overscroll 直接放行（保留越界 UserRange 给 series/axis 渲染）。
        private static void ApplyMaxBoundary(ref double min, ref double max, double maxIndex, double clampedSpan, OverscrollPolicy policy)
        {
            if (max > maxIndex && policy == OverscrollPolicy.Hard)
            {
                max = maxIndex;
                min = max - clampedSpan;
            }
        }

        private static void ApplyMinBoundary(ref double min, ref double max, double maxIndex, double clampedSpan, OverscrollPolicy policy)
        {
            if (min < 0 && policy == OverscrollPolicy.Hard)
            {
                min = 0;
                max = clampedSpan;
            }
        }
    }
}