using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using System.Windows.Media;

namespace Hevo.Charting
{
    /// <summary>🌍 环境与空间构建器 (处理网格、视口、自适应缩放等物理空间域)</summary>
    public readonly struct EnvironmentBuilder
    {
        public IFeatureContext Canvas { get; }
        public EnvironmentBuilder(IFeatureContext canvas) => Canvas = canvas;
        public EnvironmentBuilder Remove<TFeature>(Func<TFeature, bool>? predicate = null) where TFeature : ChartFeature { Canvas.Remove(predicate); return this; }
        public IFeatureContext End() => Canvas;
    }

    /// <summary>📏 坐标系统构建器 (处理自变量、因变量等映射拓扑域)</summary>
    public readonly struct AxesBuilder
    {
        public IFeatureContext Canvas { get; }
        public AxesBuilder(IFeatureContext canvas) => Canvas = canvas;
        public AxesBuilder Remove<TFeature>(Func<TFeature, bool>? predicate = null) where TFeature : ChartFeature { Canvas.Remove(predicate); return this; }
        public IFeatureContext End() => Canvas;
    }

    /// <summary>🕯️ 数据序列构建器 (处理 K线、折线、面积体等视觉渲染域)</summary>
    public readonly struct SeriesBuilder
    {
        public IFeatureContext Canvas { get; }
        public SeriesBuilder(IFeatureContext canvas) => Canvas = canvas;
        public SeriesBuilder Remove<TFeature>(Func<TFeature, bool>? predicate = null) where TFeature : ChartFeature { Canvas.Remove(predicate); return this; }
        public IFeatureContext End() => Canvas;
    }

    /// <summary>🖱️ 交互与外挂构建器 (处理十字光标、Tooltip、拖拽等行为域)</summary>
    public readonly struct InteractionBuilder
    {
        public IFeatureContext Canvas { get; }
        public InteractionBuilder(IFeatureContext canvas) => Canvas = canvas;
        public InteractionBuilder Remove<TFeature>(Func<TFeature, bool>? predicate = null) where TFeature : ChartFeature { Canvas.Remove(predicate); return this; }
        public IFeatureContext End() => Canvas;
    }

    // ==========================================
    // 💥 根入口：支持 Action 闭包与链式调用！
    // ==========================================
    public static class FeatureCanvasRootExtensions
    {
        public static EnvironmentBuilder Environment(this IFeatureContext canvas) => new(canvas);
        public static IFeatureContext Environment(this IFeatureContext canvas, Action<EnvironmentBuilder> configure) { configure(new EnvironmentBuilder(canvas)); return canvas; }

        public static AxesBuilder Axes(this IFeatureContext canvas) => new(canvas);
        public static IFeatureContext Axes(this IFeatureContext canvas, Action<AxesBuilder> configure) { configure(new AxesBuilder(canvas)); return canvas; }

        public static SeriesBuilder Series(this IFeatureContext canvas) => new(canvas);
        public static IFeatureContext Series(this IFeatureContext canvas, Action<SeriesBuilder> configure) { configure(new SeriesBuilder(canvas)); return canvas; }

        public static InteractionBuilder Interactions(this IFeatureContext canvas) => new(canvas);
        public static IFeatureContext Interactions(this IFeatureContext canvas, Action<InteractionBuilder> configure) { configure(new InteractionBuilder(canvas)); return canvas; }
    }

    /// <summary>
    /// 辅助配置项 (Options & Metas)
    /// </summary>
    /// <typeparam name="TX"></typeparam>
    public class InteractionOptions<TX>
    {
        public FieldMeta? TooltipXMeta { get; set; }
        public bool ShowIntersectionDots { get; set; } = true;
        public ChartInteractionMode Modes { get; set; } = ChartInteractionMode.All;

        // 💥 对齐更名：BoundsStrategy -> SnapMode
        public PointerOutOfBoundsStrategy SnapMode { get; set; } = PointerOutOfBoundsStrategy.SnapToValidData;

        // 💥 接入新的分组配置项
        public ZoomOptions ZoomConfig { get; init; } = new();
        public DataFetchOptions FetchConfig { get; init; } = new();

        // 💥 允许业务层注入自定义缩放策略
        public IZoomStrategy? ZoomStrategy { get; init; }

        public DataPort<int>? ValidCountPort { get; init; }
        public DataPort<PointerHitState?>? HitPort { get; init; }
        public Func<int, int, Task<bool>>? OnRequireDataAsync { get; init; }
    }

    // ==========================================
    // 💥 领域扩展方法
    // ==========================================
    public static class FeatureCanvasScopedExtensions
    {
        // --- 🌍 1. Environment ---
        // Viewport 由 ReactiveSchema.Add 自动注入（L6 / §B.2.6），外部不再显式传 vp。
        public static EnvironmentBuilder SetupViewport(this EnvironmentBuilder builder, int minVisibleCount = 10, ViewportAlignment alignment = ViewportAlignment.LeftEdge)
        {
            builder.Canvas.Remove<ViewportManagerFeature>();
            builder.Canvas.Add(new ViewportManagerFeature { MinVisibleCount = minVisibleCount, Alignment = alignment });
            return builder;
        }

        // --- 📏 2. Axes (自变量与因变量) ---
        /// <summary>
        /// X 轴：支持传入各种策略 (ThsTime, Periodic, Fixed)
        /// </summary>
        /// <typeparam name="TX"></typeparam>
        /// <param name="builder"></param>
        /// <param name="domainData"></param>
        /// <param name="vp"></param>
        /// <param name="meta"></param>
        /// <param name="strategy"></param>
        /// <returns></returns>
        public static AxesBuilder AddDomainAxis<TX>(
            this AxesBuilder builder,
            DataPort<ReadOnlyMemory<TX>> domainData,
            ViewportPorts vp,
            FieldMeta meta,
            // 💥 终极修改：接收带有 RefBox 盒子的工厂，完美对齐 DomainTickProvider 的需求！
            Func<RefBox<ReadOnlyMemory<TX>>, ITickStrategy<double>>? strategyFactory = null)
        {
            if (!builder.Canvas.HasFeature<ViewportManagerFeature>())
            {
                // Viewport 由 ReactiveSchema.Add 自动注入（L6 / §B.2.6）
                builder.Canvas.Add(new ViewportManagerFeature
                {
                    Alignment = ViewportAlignment.RightEdge
                });
            }

            builder.Canvas.Add(new AxisFeature<double>(
                new DomainTickProvider<TX>(domainData, vp, meta.Format, meta.Provider, strategyFactory),
                "DomainAxis")
            {
                Mapping = ScaleMapping.Domain,
                IsShared = true,
                RangePort = vp.ActiveRange,
                AxisStyle = AxisStyleTrait.Create(AxisPlacement.Bottom, Colors.Gray),
                GridStyle = GridStyleTrait.Create(GridOrientation.Vertical, LineStyle.Create(Color.FromArgb(40, 255, 255, 255), 1))
            });
            return builder;
        }

        /// <summary>
        /// Y 轴：支持传入各种策略 (ThsY, AdaptiveY, TV)
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="rangePort"></param>
        /// <param name="meta"></param>
        /// <param name="placement"></param>
        /// <param name="handle"></param>
        /// <param name="strategy"></param>
        /// <returns></returns>
        public static AxesBuilder AddRangeAxis(
            this AxesBuilder builder,
            DataPort<RealRange> rangePort,
            FieldMeta meta,
            AxisPlacement placement = AxisPlacement.Right,
            AxisHandle? handle = null,
            ITickStrategy<double>? strategy = null) // 💥 核心：允许注入策略
        {
            builder.Canvas.Add(new AxisFeature<double>(
                new NumericTickProvider(meta.Format, meta.Provider, strategy),
                $"RangeAxis_{placement}")
            {
                Mapping = ScaleMapping.Value,
                Handle = handle ?? new AxisHandle(),
                RangePort = rangePort,
                AxisStyle = AxisStyleTrait.Create(placement, Colors.Gray, fontSize: 11.0),
                GridStyle = GridStyleTrait.Create(GridOrientation.Horizontal, LineStyle.Create(Color.FromArgb(40, 255, 255, 255), 1))
            });
            return builder;
        }
        // Viewport 由 ReactiveSchema.Add 自动注入（L6 / §B.2.6），外部不再显式传 vp。
        public static SeriesBuilder AddLine(this SeriesBuilder builder, DataPort<ReadOnlyMemory<double>> dataPort, DataPort<RealRange> rangePort, FieldMeta meta, double thickness = 1)
        {
            builder.Canvas.Add(new LineSeriesFeature
            {
                DataPort = dataPort,
                YRangePort = rangePort,
                Meta = meta,
                Style = LineStyle.Create(meta.GetConstantBrush(), thickness)
            });
            return builder;
        }

        // --- 🖱️ 4. Interactions ---
        // Viewport 由 ReactiveSchema.Add 自动注入（L6 / §B.2.6），外部不再显式传 vp。
        public static InteractionBuilder EnableStandard<TX>(
                    this InteractionBuilder builder,
                    DataPort<ReadOnlyMemory<TX>> domainDataPort,
                    FieldMeta domainMeta,
                    InteractionOptions<TX>? options = null)
        {
            options ??= new InteractionOptions<TX>();

            var hitPort = options.HitPort ?? new DataPort<PointerHitState?>("InternalHitState");

            // 💥 装配大管家：所有参数严格对齐最新版的 ChartInteractionFeature
            builder.Canvas.Add(new ChartInteractionFeature
            {
                PointerHitPort = hitPort,
                SupportedModes = options.Modes,
                PointerSnapMode = options.SnapMode, // 👈 使用新名称
                ValidDataCountPort = options.ValidCountPort,
                OnRequireDataAsync = options.OnRequireDataAsync,
                ZoomConfig = options.ZoomConfig,    // 👈 注入缩放配置组
                FetchConfig = options.FetchConfig,  // 👈 注入推图配置组
                // 如果外部没有传策略，则兜底使用我们的“神级组合”
                ZoomStrategy = options.ZoomStrategy ?? new SmartAdaptiveZoomStrategy()
            });

            builder.Canvas.Add(new CrosshairFeature<TX> { HitStatePort = hitPort, XAxisDataPort = domainDataPort, XMeta = domainMeta, ShowIntersectionDots = options.ShowIntersectionDots });
            builder.Canvas.Add(new TooltipWidgetFeature<TX> { HitStatePort = hitPort, XAxisDataPort = domainDataPort, XMeta = options.TooltipXMeta ?? domainMeta });

            return builder;
        }
    }

    // ==========================================
    // 💥 交互域专属扩展 (高内聚，各自管理自己的语法糖)
    // ==========================================
    public static class InteractionFeatureExtensions
    {
        /// <summary>
        /// 🖱️ 挂载基础指针交互 (负责拖拽、缩放、命中测试)
        /// </summary>
        // Viewport 由 ReactiveSchema.Add 自动注入（L6 / §B.2.6），外部不再显式传 vp。
        public static InteractionBuilder EnableStandardPointer(
            this InteractionBuilder builder,
            DataPort<PointerHitState?> hitPort,
            ChartInteractionMode modes = ChartInteractionMode.Default,
            PointerOutOfBoundsStrategy snapMode = PointerOutOfBoundsStrategy.Free, // 👈 更名
            DataPort<int>? validCountPort = null,
            Func<int, int, Task<bool>>? onRequireDataAsync = null,
            // 💥 新增可选配置参数
            ZoomOptions? zoomConfig = null,
            DataFetchOptions? fetchConfig = null,
            IZoomStrategy? zoomStrategy = null)
        {
            builder.Canvas.Add(new ChartInteractionFeature
            {
                PointerHitPort = hitPort,
                SupportedModes = modes,
                PointerSnapMode = snapMode, // 👈 映射新属性
                ValidDataCountPort = validCountPort,
                OnRequireDataAsync = onRequireDataAsync,
                // 💥 完美融合选项组
                ZoomConfig = zoomConfig ?? new ZoomOptions(),
                FetchConfig = fetchConfig ?? new DataFetchOptions(),
                ZoomStrategy = zoomStrategy ?? new SmartAdaptiveZoomStrategy()
            });
            return builder;
        }

        /// <summary>
        /// 💬 挂载悬浮提示框 (Tooltip)
        /// </summary>
        public static InteractionBuilder AddTooltip<TX>(
            this InteractionBuilder builder,
            DataPort<PointerHitState?> hitPort,
            DataPort<ReadOnlyMemory<TX>> xAxisDataPort,
            FieldMeta xMeta)
        {
            builder.Canvas.Add(new TooltipWidgetFeature<TX>
            {
                HitStatePort = hitPort,
                XAxisDataPort = xAxisDataPort,
                XMeta = xMeta
            });
            return builder;
        }
    }
}
