using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Features
{
    public enum ViewportAlignment { RightEdge, LeftEdge }

    public record ViewportLimitsTrait(double MinSpan, double MaxSpanMultiplier) : IVisualTrait;

    public class ViewportManagerFeature : ChartFeature
    {
        public override FeaturePhase Phase => FeaturePhase.Layout;

        public ViewportAlignment Alignment { get; init; } = ViewportAlignment.RightEdge;
        public double MinVisibleCount { get; init; } = 3;
        public double? DefaultVisibleCount { get; init; }
        public double MaxZoomOutMultiplier { get; init; } = 1.5;
        public double HistoryLoadTolerance { get; init; } = 30;
        public double RightEdgeStickTolerance { get; init; } = 2;

        private int _lastLogicalLength = -1;
        private RealRange _lastProcessedUserRange = new RealRange(double.NaN, double.NaN);

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            ctx.Shared().PublishData(new ViewportLimitsTrait(Math.Max(0, MinVisibleCount - 1), MaxZoomOutMultiplier));

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
                    double clampedSpan = Math.Clamp(targetSpan, minSpan, Math.Max(minSpan, maxIndex * MaxZoomOutMultiplier));

                    double min = targetCenter - clampedSpan / 2.0;
                    double max = targetCenter + clampedSpan / 2.0;

                    if (Alignment == ViewportAlignment.RightEdge)
                    {
                        if (clampedSpan > maxIndex) { max = maxIndex; min = max - clampedSpan; }
                        else
                        {
                            if (max > maxIndex) { max = maxIndex; min = max - clampedSpan; }
                            if (min < 0) { min = 0; max = clampedSpan; }
                        }
                    }
                    else if (Alignment == ViewportAlignment.LeftEdge)
                    {
                        if (clampedSpan > maxIndex) { min = 0; max = clampedSpan; }
                        else
                        {
                            if (min < 0) { min = 0; max = clampedSpan; }
                            if (max > maxIndex) { max = maxIndex; min = maxIndex - clampedSpan; }
                        }
                    }

                    // 💥 绝对单向流：管家只负责输出现实，不再管意图！打破一切死锁。
                    using (board.AcquireWriteLock()) board.WriteIfChanged(Viewport.ActiveRange, new RealRange(min, max));
                }
            });

        }

        protected override void OnProject(FeatureContext ctx) { }
        public void ResetState() { _lastLogicalLength = -1; _lastProcessedUserRange = new RealRange(double.NaN, double.NaN); }
    }
}