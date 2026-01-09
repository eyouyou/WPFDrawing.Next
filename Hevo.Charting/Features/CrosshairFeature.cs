using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using System.Windows;

namespace Hevo.Charting.Features
{
    // ==========================================
    // 💥 1. 十字光标显示配置标志
    // ==========================================
    [Flags]
    public enum CrosshairDisplayMode
    {
        None = 0,
        XLabel = 1 << 0,
        YLabel = 1 << 1,
        Both = XLabel | YLabel
    }

    /// <summary>
    /// Y 轴追踪配置聚合体
    /// </summary>
    /// <param name="Port"></param>
    /// <param name="Meta"></param>
    /// <param name="Placement"></param>
    public record struct CrosshairYTrackInfo(
            DataPort<RealRange> Port,
            FieldMeta? Meta,
            AxisPlacement Placement,

            // 💥 强类型句柄：默认是 null。
            // 不传就是触发“模糊匹配”，传了就是触发“精确打击”！
            AxisHandle? Handle = null
        );

    /// <summary>
    /// 流式闭包装配器，收集多个 Y 轴的追踪诉求
    /// </summary>
    public class CrosshairYTrackerBuilder
    {
        internal List<CrosshairYTrackInfo> Trackers { get; } = new();

        public CrosshairYTrackerBuilder Track(DataPort<RealRange> port, FieldMeta? meta = null, AxisPlacement placement = AxisPlacement.Left, AxisHandle? handle = null)
        {
            Trackers.Add(new CrosshairYTrackInfo(port, meta, placement, handle));
            return this;
        }
    }

    public static class InteractionsBuilderExtensions
    {
        /// <summary>
        /// 十字光标语法糖重载 1：单轴极简傻瓜版
        /// </summary>
        /// <typeparam name="TX"></typeparam>
        /// <param name="builder"></param>
        /// <param name="hitPort"></param>
        /// <param name="domainDataPort"></param>
        /// <param name="domainMeta"></param>
        /// <param name="displayMode"></param>
        /// <param name="showIntersectionDots"></param>
        /// <param name="xLabelFormatter"></param>
        /// <param name="futureXLabelFormatter"></param>
        /// <param name="yLabelFormatter"></param>
        /// <param name="yRangePort"></param>
        /// <param name="yMeta"></param>
        /// <returns></returns>
        public static InteractionBuilder AddCrosshair<TX>(
                    this InteractionBuilder builder,
                    DataPort<PointerHitState?> hitPort,
                    DataPort<ReadOnlyMemory<TX>> domainDataPort,
                    FieldMeta domainMeta,
                    CrosshairDisplayMode displayMode = CrosshairDisplayMode.Both,
                    bool showIntersectionDots = true,
                    Func<TX, string>? xLabelFormatter = null,
                    Func<int, string>? futureXLabelFormatter = null,
                    Func<double, string>? yLabelFormatter = null,
                    DataPort<RealRange>? yRangePort = null,
                    FieldMeta? yMeta = null)
        {
            var trackers = yRangePort != null
                ? new[] { new CrosshairYTrackInfo(yRangePort, yMeta, AxisPlacement.Left) }
                : Array.Empty<CrosshairYTrackInfo>();

            builder.Canvas.Add(new CrosshairFeature<TX>
            {
                HitStatePort = hitPort,
                XAxisDataPort = domainDataPort,
                XMeta = domainMeta,
                DisplayMode = displayMode,
                ShowIntersectionDots = showIntersectionDots,
                XLabelFormatter = xLabelFormatter,
                FutureXLabelFormatter = futureXLabelFormatter,
                YLabelFormatter = yLabelFormatter,
                YTrackers = trackers
            });
            return builder;
        }

        /// <summary>
        /// 十字光标语法糖重载 2：多轴流式强绑定版
        /// </summary>
        /// <typeparam name="TX"></typeparam>
        /// <param name="builder"></param>
        /// <param name="hitPort"></param>
        /// <param name="domainDataPort"></param>
        /// <param name="domainMeta"></param>
        /// <param name="yTrackers"></param>
        /// <param name="displayMode"></param>
        /// <param name="showIntersectionDots"></param>
        /// <param name="xLabelFormatter"></param>
        /// <param name="futureXLabelFormatter"></param>
        /// <param name="yLabelFormatter"></param>
        /// <returns></returns>
        public static InteractionBuilder AddCrosshair<TX>(
                    this InteractionBuilder builder,
                    DataPort<PointerHitState?> hitPort,
                    DataPort<ReadOnlyMemory<TX>> domainDataPort,
                    FieldMeta domainMeta,
                    Action<CrosshairYTrackerBuilder> yTrackers, // 👈 闭包流式传入
                    CrosshairDisplayMode displayMode = CrosshairDisplayMode.Both,
                    bool showIntersectionDots = true,
                    Func<TX, string>? xLabelFormatter = null,
                    Func<int, string>? futureXLabelFormatter = null,
                    Func<double, string>? yLabelFormatter = null)
        {
            var trackerBuilder = new CrosshairYTrackerBuilder();
            yTrackers(trackerBuilder);

            builder.Canvas.Add(new CrosshairFeature<TX>
            {
                HitStatePort = hitPort,
                XAxisDataPort = domainDataPort,
                XMeta = domainMeta,
                DisplayMode = displayMode,
                ShowIntersectionDots = showIntersectionDots,
                XLabelFormatter = xLabelFormatter,
                FutureXLabelFormatter = futureXLabelFormatter,
                YLabelFormatter = yLabelFormatter,
                YTrackers = trackerBuilder.Trackers.ToArray()
            });
            return builder;
        }
    }

    public interface IIndexBrushResolver
    {
        IHevoBrush ResolveByIndex(int fieldIdx, int localIdx);
    }
    /// <summary>
    /// 基于坐标索引的解析器
    /// </summary>
    /// <param name="ResolveByIndex"></param>
    public record IndexBrushResolverTrait(IIndexBrushResolver Resolver) : IVisualTrait;

    /// <summary>
    /// 💥 纯净版十字光标特征 (支持 X/Y 轴标签及精准数据交汇点)
    /// </summary>
    public class CrosshairFeature<TX> : ChartFeature
    {
        public override FeaturePhase Phase => FeaturePhase.Interaction;
        public CrosshairDisplayMode DisplayMode { get; init; } = CrosshairDisplayMode.Both;
        public bool ShowIntersectionDots { get; init; } = true;

        public CrosshairYTrackInfo[] YTrackers { get; init; } = Array.Empty<CrosshairYTrackInfo>();
        public DataPort<PointerHitState?> HitStatePort { get; init; } = null!;
        public DataPort<ReadOnlyMemory<TX>> XAxisDataPort { get; init; } = null!;
        public FieldMeta? XMeta { get; init; }
        public Func<TX, string>? XLabelFormatter { get; init; }
        public Func<int, string>? FutureXLabelFormatter { get; init; }
        public Func<double, string>? YLabelFormatter { get; init; }

        private readonly CrosshairLayer _layer = new();
        private DataPort<RealRange>[] _trackingPorts = Array.Empty<DataPort<RealRange>>();
        private RealRange[] _rangeBuffer = Array.Empty<RealRange>();
        private readonly List<AxisLabel> _yLabelBuffer = new();
        private readonly List<CrosshairDotInfo> _dotBuffer = new();

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            _trackingPorts = YTrackers.Select(x => x.Port).ToArray();
            _rangeBuffer = new RealRange[_trackingPorts.Length];
            AttachLayer(_layer);
        }

        protected override void OnProject(FeatureContext ctx)
        {
            var (hitState, _) = ctx.UsePort(HitStatePort);
            var (xData, _) = ctx.UsePort(XAxisDataPort);

            bool isRangesChanged = ctx.UsePorts(_trackingPorts, _rangeBuffer);

            if (hitState == null)
            {
                ctx.For(_layer).PublishData(new InteractionTrait(false, default, null, null, null));
                return;
            }

            var state = hitState.Value;
            var style = ctx.Shared().Read<CrosshairStyleTrait>() ?? CrosshairStyleTrait.DefaultDark;
            HevoRect plotArea = ctx.PlotArea;
            _yLabelBuffer.Clear();
            _dotBuffer.Clear();

            // X轴标签计算
            AxisLabel? labelX = null;
            if (DisplayMode.HasFlag(CrosshairDisplayMode.XLabel))
            {
                if (!state.IsOutOfBounds && state.LocalIndex >= 0 && state.LocalIndex < xData.Length)
                {
                    TX val = xData.Span[state.LocalIndex];
                    string xStr = XLabelFormatter != null ? XLabelFormatter(val) : val.FormatValue(XMeta?.Format ?? "G", XMeta?.Provider);
                    labelX = new AxisLabel(xStr, style.LabelBrush);
                }
                else if (state.IsOutOfBounds && FutureXLabelFormatter != null)
                {
                    labelX = new AxisLabel(FutureXLabelFormatter(state.Hit.LogicalIndex), style.LabelBrush);
                }
            }

            var registry = ctx.Shared().Read<AxisLayoutRegistryTrait>();

            if (DisplayMode.HasFlag(CrosshairDisplayMode.YLabel) && plotArea.Height > 0)
            {
                double ratio = Math.Clamp((plotArea.Bottom - state.MousePos.Y) / plotArea.Height, 0, 1);

                for (int i = 0; i < YTrackers.Length; i++)
                {
                    var tracker = YTrackers[i];
                    var currentRange = _rangeBuffer[i];

                    if (currentRange.Span > 0)
                    {
                        double priceVal = currentRange.Min + ratio * currentRange.Span;
                        string yStr = priceVal.FormatValue(tracker.Meta?.Format ?? "F2", tracker.Meta?.Provider);

                        double? targetPhysicalX = null;

                        // ==========================================
                        // 🚀 智能三级定位策略：O(1) 寻找目标轴的绝对坐标
                        // ==========================================
                        if (registry != null)
                        {
                            // 🎯 1级精确防御：如果外界配置了确切的 Handle，直接锁定！
                            if (tracker.Handle != null && registry.VerticalAnchors.TryGetValue(tracker.Handle, out double exactX))
                                targetPhysicalX = exactX;
                            // 🎯 2级模糊防御：没传 Handle（单轴降级调用），根据方向去模糊备忘录里捞一个！
                            else if (registry.LastKnownAnchors.TryGetValue(tracker.Placement, out double fallbackX))
                                targetPhysicalX = fallbackX;
                        }

                        // 🎯 3级最终兜底：地图里没记录，退化到贴图表边缘
                        targetPhysicalX ??= (tracker.Placement == AxisPlacement.Right ? plotArea.Right : plotArea.Left);

                        _yLabelBuffer.Add(new AxisLabel(yStr, style.LabelBrush, tracker.Placement, targetPhysicalX));
                    }
                }
            }

            // 智能交点吸附雷达 (点集计算)
            if (ShowIntersectionDots && !state.IsOutOfBounds && state.LocalIndex >= 0)
            {
                foreach (var activeLayer in Chart.ActiveLayers)
                {
                    var proxy = ctx.For(activeLayer);
                    var yAxisTrait = proxy.Read<YAxisTrait>();

                    if (yAxisTrait == null || yAxisTrait.Viewport.Span <= 0) continue;

                    RealRange layerYRange = yAxisTrait.Viewport;
                    var doubleData = proxy.Read<DoubleSeriesDataTrait>();
                    double yPixel = double.NaN;

                    if (doubleData != null && doubleData.FieldValues.Length > 0 && state.LocalIndex < doubleData.FieldValues[0].Span.Length)
                    {
                        double val = doubleData.FieldValues[0].Span[state.LocalIndex];
                        if (!double.IsNaN(val)) yPixel = plotArea.Bottom - ((val - layerYRange.Min) / layerYRange.Span) * plotArea.Height;
                    }

                    if (!double.IsNaN(yPixel))
                    {
                        IHevoBrush? dotBrush = proxy.Read<IndexBrushResolverTrait>()?.Resolver.ResolveByIndex(0, state.LocalIndex) ?? proxy.Read<MetaTrait>()?.Fields[0].GetConstantBrush();
                        if (dotBrush != null) _dotBuffer.Add(new CrosshairDotInfo(new HevoPoint((float)state.CenterX, (float)yPixel), dotBrush));
                    }
                }
            }

            ctx.For(_layer).PublishData(new InteractionTrait(
                true, new HevoPoint((float)state.CenterX, state.MousePos.Y), labelX, _yLabelBuffer.AsReadOnly(), _dotBuffer.AsReadOnly()
            ));
        }
    }
}
