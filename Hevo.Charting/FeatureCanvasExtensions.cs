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
        // 三类可配置手势:Box-zoom / 惯性 Pan / 双击复原
        public BoxZoomOptions BoxZoomConfig { get; init; } = new();
        public InertiaPanOptions InertiaPanConfig { get; init; } = new();
        public DoubleClickResetOptions DoubleClickResetConfig { get; init; } = new();

        /// <summary>true = 鼠标进 chart 不立即出十字光标,需先在 chart 内单击一次才武装。</summary>
        public bool RequireClickToActivate { get; init; } = false;
        /// <summary>true = 按下 Esc 立即解除 hover(隐藏 crosshair / tooltip),直至下次单击。</summary>
        public bool DismissOnEscape { get; init; } = false;

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
        // Viewport 由 ChartFeature.InternalCompose 从 ViewportManagerFeature.Ports 自动注入,
        // 外部不再显式传 vp。
        //
        // 参数：
        //   minVisibleCount: 最小可缩放到的根数（限制最大放大）
        //   alignment: 数据/视口变化时的停靠侧（RightEdge=新数据贴右；LeftEdge=贴左不动）
        //   defaultVisibleCount: 首屏显示根数（null=显示全部，分页业务必传以留出滚动空间）
        //   maxSpanMultiplier: 视口最大可缩放至数据量的几倍（限制最大缩小，**约束所有 UserRange 写入**，不只是缩放）
        //   overscrollMin/Max: 用户拖到数据边界外时的策略
        //                       Hard=钳回（默认，适合数据量固定）
        //                       Overscroll=允许越界留白（适合分页业务，用户意图保留）
        public static EnvironmentBuilder SetupViewport(
            this EnvironmentBuilder builder,
            int minVisibleCount = 10,
            ViewportAlignment alignment = ViewportAlignment.LeftEdge,
            int? defaultVisibleCount = null,
            double maxSpanMultiplier = 1.5,
            OverscrollPolicy overscrollMin = OverscrollPolicy.Hard,
            OverscrollPolicy overscrollMax = OverscrollPolicy.Hard)
        {
            // PortsFeature 由 ChartReactiveSchema(chart 中间层)在 EnsureBaseFeatures 阶段 framework
            // 强制 ensure,本 helper 不再自助 add ——
            // 否则 IsSingleton 替换会换掉 framework 实例,导致 OnAttached 时挂的 ports 跟其他 helper 引用
            // (UniversalHeaderFeature 内部已经 capture 的)Viewport.LogicalLength 实例不一致 → 黑屏。
            // 本 helper 只 IsSingleton 替换 VPM 配置钳制策略,业务侧调多次自动 reset 配置。
            builder.Canvas.Remove<ViewportManagerFeature>();
            builder.Canvas.Add(new ViewportManagerFeature
            {
                MinVisibleCount = minVisibleCount,
                Alignment = alignment,
                DefaultVisibleCount = defaultVisibleCount,
                MaxSpanMultiplier = maxSpanMultiplier,
                OverscrollMin = overscrollMin,
                OverscrollMax = overscrollMax
            });
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
            Func<RefBox<ReadOnlyMemory<TX>>, ITickStrategy>? strategyFactory = null,
            LineStyle? gridStyle = null)
        {
            if (!builder.Canvas.HasFeature<ViewportManagerFeature>()
                && !builder.Canvas.HasFeature<ViewportPortsFeature>(p => p.IsExternal))
                throw new InvalidOperationException(
                    "请先在 Environment 阶段调用 env.SetupViewport(...) 配置视口策略。" +
                    "缺省 ViewportManager 已废弃 —— 隐式默认会让初始视口贴满数据，造成拖拽无反馈。" +
                    "(联动副图通过 SchemaContext.LinkedPane 装饰,会把 ViewportPortsFeature.IsExternal 设为 true)");

            builder.Canvas.Add(new AxisFeature(
                new DomainTickProvider<TX>(domainData, vp, meta.Format, meta.Provider, strategyFactory),
                "DomainAxis")
            {
                Mapping = ScaleMapping.Domain,
                IsShared = true,
                RangePort = vp.ActiveRange,
                AxisStyle = AxisStyleTrait.Create(AxisPlacement.Bottom, Colors.Gray),
                GridStyle = GridStyleTrait.Create(GridOrientation.Vertical, gridStyle ?? LineStyle.Create(Color.FromArgb(40, 255, 255, 255), 1))
            });
            return builder;
        }

        /// <summary>
        /// X 轴 (schedule-driven):tick 位置和 label 都从 totalLength + indexToTime 推导,跟实际数据数组解耦。
        /// 适合分时图 / 强弱评级 / 权重表达——这类业务即使数据只到达一半,X 轴也要画完整时段的 tick。
        /// 跟 <see cref="AddDomainAxis{TX}"/> (data-driven) 并列,选用其一即可。
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="vp">视口端口集</param>
        /// <param name="meta">label 格式化元信息</param>
        /// <param name="totalLengthProvider">全网格逻辑长度,典型 <c>() => dataSource.LogicalLength</c></param>
        /// <param name="indexToTime">逻辑索引 → 实际时间,典型 <c>idx => dataSource.IndexToTime(idx)</c></param>
        /// <param name="strategyFactory">可选 strategy 工厂,null 时默认 <see cref="TradeTimeTickStrategy"/></param>
        public static AxesBuilder AddScheduleDomainAxis(
            this AxesBuilder builder,
            ViewportPorts vp,
            FieldMeta meta,
            Func<int> totalLengthProvider,
            Func<int, DateTime> indexToTime,
            Func<ITickStrategy>? strategyFactory = null,
            LineStyle? gridStyle = null)
        {
            if (!builder.Canvas.HasFeature<ViewportManagerFeature>()
                && !builder.Canvas.HasFeature<ViewportPortsFeature>(p => p.IsExternal))
                throw new InvalidOperationException(
                    "请先在 Environment 阶段调用 env.SetupViewport(...) 配置视口策略。" +
                    "(联动副图通过 SchemaContext.LinkedPane 装饰,会把 ViewportPortsFeature.IsExternal 设为 true)");

            builder.Canvas.Add(new AxisFeature(
                new ScheduleTickProvider(totalLengthProvider, indexToTime, meta.Format, meta.Provider, strategyFactory),
                "ScheduleDomainAxis")
            {
                Mapping = ScaleMapping.Domain,
                IsShared = true,
                RangePort = vp.ActiveRange,
                AxisStyle = AxisStyleTrait.Create(AxisPlacement.Bottom, Colors.Gray),
                GridStyle = GridStyleTrait.Create(GridOrientation.Vertical, gridStyle ?? LineStyle.Create(Color.FromArgb(40, 255, 255, 255), 1))
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
            ITickStrategy? strategy = null, // 💥 核心：允许注入策略
            MirrorTickAnchor? broadcastTicksTo = null) // 💥 双轴主轴：把 tick 比例广播给同槽副轴
        {
            builder.Canvas.Add(new AxisFeature(
                new NumericTickProvider(meta.Format, meta.Provider, strategy),
                $"RangeAxis_{placement}")
            {
                Mapping = ScaleMapping.Value,
                Handle = handle ?? new AxisHandle(),
                RangePort = rangePort,
                AxisStyle = AxisStyleTrait.Create(placement, Colors.Gray, fontSize: 11.0),
                GridStyle = GridStyleTrait.Create(GridOrientation.Horizontal, LineStyle.Create(Color.FromArgb(40, 255, 255, 255), 1)),
                BroadcastTicksTo = broadcastTicksTo
            });
            return builder;
        }

        /// <summary>
        /// 双轴副轴：tick 比例完全镜像主轴,自身不画 grid (避免与主轴 grid 重影)。
        /// 调用前提：先调用主轴的 <see cref="AddRangeAxis(AxesBuilder, DataPort{RealRange}, FieldMeta, AxisPlacement, AxisHandle?, ITickStrategy?, MirrorTickAnchor?)"/>
        /// 并把同一个 <paramref name="mirrorFrom"/> 实例传给 broadcastTicksTo,确保同帧主轴先写、副轴后读。
        /// </summary>
        public static AxesBuilder AddMirroredRangeAxis(
            this AxesBuilder builder,
            DataPort<RealRange> rangePort,
            FieldMeta meta,
            MirrorTickAnchor mirrorFrom,
            AxisPlacement placement = AxisPlacement.Right,
            AxisHandle? handle = null)
        {
            if (mirrorFrom == null) throw new System.ArgumentNullException(nameof(mirrorFrom));

            builder.Canvas.Add(new AxisFeature(
                new NumericTickProvider(meta.Format, meta.Provider, new MirrorRatioTickStrategy(mirrorFrom)),
                $"MirroredRangeAxis_{placement}")
            {
                Mapping = ScaleMapping.Value,
                Handle = handle ?? new AxisHandle(),
                RangePort = rangePort,
                AxisStyle = AxisStyleTrait.Create(placement, Colors.Gray, fontSize: 11.0),
                // GridStyle = null：副轴不画 grid,完全让位主轴。
                MirrorTicksFrom = mirrorFrom
            });
            return builder;
        }

        /// <summary>
        /// Y 轴（锚点感知）：grid 以 <paramref name="anchorPort"/> 为原点向外铺开，<paramref name="hintsPort"/>
        /// 中的 high/low 太靠近普通 grid tick 时会让位（替换），但永远不让位 baseline。
        /// 典型用法 —— 分时图：anchorPort = 昨收价（baseline 永远精确），hintsPort = 当日 high/low。
        /// <para>
        /// 不显式传 <paramref name="baseLineStyle"/> 时，扩展方法在 composition 阶段从 <paramref name="gridStyle"/>
        /// 同色派生一支 +1px 的笔作为 baseline，保证 baseline 跟 grid 视觉一脉相承又略加重。
        /// </para>
        /// </summary>
        public static AxesBuilder AddRangeAxis(
            this AxesBuilder builder,
            DataPort<RealRange> rangePort,
            FieldMeta meta,
            DataPort<double> anchorPort,
            DataPort<RealRange> hintsPort,
            AxisPlacement placement = AxisPlacement.Right,
            AxisHandle? handle = null,
            LineStyle? baseLineStyle = null,
            LineStyle? gridStyle = null,
            IHevoBrush? baselineTextBrush = null)
        {
            var resolvedGrid = gridStyle ?? LineStyle.Create(Color.FromArgb(40, 255, 255, 255), 1);
            // baseline pen 不再自动 +1。业务想要 anchor 处加粗强调，显式传 baseLineStyle；
            // 不传则 anchor tick 跟普通 grid 同 pen，纯靠 OverrideTextBrush / 文字着色区分。
            var anchorLineStyle = baseLineStyle;
            // AxisStyleTrait.BaseLineStyle 用于 axis tick mark 短线笔，与 grid 强调脱钩。
            // 这里仍提供一支 tick mark pen（默认 = grid pen），业务可独立配置。
            var tickMarkStyle = baseLineStyle ?? resolvedGrid;

            builder.Canvas.Add(new AxisFeature(
                new AnchoredNumericTickProvider(meta.Format, anchorPort, hintsPort, meta.Provider, baselineTextBrush, anchorLineStyle),
                $"RangeAxis_{placement}")
            {
                Mapping = ScaleMapping.Value,
                Handle = handle ?? new AxisHandle(),
                RangePort = rangePort,
                AxisStyle = AxisStyleTrait.Create(placement, Colors.Gray, baseLineStyle: tickMarkStyle, fontSize: 11.0),
                GridStyle = GridStyleTrait.Create(GridOrientation.Horizontal, resolvedGrid)
            });
            return builder;
        }
        // Viewport 由 ChartFeature.InternalCompose 从 ViewportManagerFeature.Ports 自动注入（L6 / §B.2.6），外部不再显式传 vp。
        public static SeriesBuilder AddLine(this SeriesBuilder builder, DataPort<ReadOnlyMemory<double>> dataPort, DataPort<RealRange> rangePort, FieldMeta meta, double thickness = 1)
        {
            builder.Canvas.Add(new LineSeriesFeature
            {
                DataPort = dataPort,
                YRangePort = rangePort,
                Meta = meta,
                Style = LineStyle.Create(meta.GetDefaultBrush(), thickness)
            });
            return builder;
        }

        // --- 🖱️ 4. Interactions ---
        // Viewport 由 ChartFeature.InternalCompose 从 ViewportManagerFeature.Ports 自动注入（L6 / §B.2.6），外部不再显式传 vp。
        public static InteractionBuilder EnableStandard<TX>(
                    this InteractionBuilder builder,
                    DataPort<ReadOnlyMemory<TX>> domainDataPort,
                    FieldMeta domainMeta,
                    InteractionOptions<TX>? options = null)
        {
            options ??= new InteractionOptions<TX>();

            var hitPort = options.HitPort ?? new DataPort<PointerHitState?>("InternalHitState");

            // AvailabilityPort 是分页 Feature 的对外通道；DSL 在此唯一处建实例并穿针给两个 Feature
            DataPort<DataAvailability>? availabilityPort = options.OnRequireDataAsync != null
                ? new DataPort<DataAvailability>("DataAvailability")
                : null;

            builder.Canvas.Add(new ChartInteractionFeature
            {
                PointerHitPort = hitPort,
                SupportedModes = options.Modes,
                PointerSnapMode = options.SnapMode,
                ValidDataCountPort = options.ValidCountPort,
                ZoomConfig = options.ZoomConfig,
                BoxZoomConfig = options.BoxZoomConfig,
                InertiaPanConfig = options.InertiaPanConfig,
                DoubleClickResetConfig = options.DoubleClickResetConfig,
                RequireClickToActivate = options.RequireClickToActivate,
                DismissOnEscape = options.DismissOnEscape,
                ZoomStrategy = options.ZoomStrategy ?? new SmartAdaptiveZoomStrategy(),
                AvailabilityPort = availabilityPort
            });

            // 仅当业务提供分页回调时挂载 DataPagingFeature；非分页 schema 不付出额外 Watch 成本
            if (availabilityPort != null)
            {
                builder.Canvas.Add(new DataPagingFeature
                {
                    OnRequireDataAsync = options.OnRequireDataAsync,
                    FetchConfig = options.FetchConfig,
                    AvailabilityPort = availabilityPort
                });
            }

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
        // Viewport 由 ChartFeature.InternalCompose 从 ViewportManagerFeature.Ports 自动注入（L6 / §B.2.6），外部不再显式传 vp。
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
            IZoomStrategy? zoomStrategy = null,
            BoxZoomOptions? boxZoomConfig = null,
            InertiaPanOptions? inertiaPanConfig = null,
            DoubleClickResetOptions? doubleClickResetConfig = null,
            bool requireClickToActivate = false,
            bool dismissOnEscape = false)
        {
            DataPort<DataAvailability>? availabilityPort = onRequireDataAsync != null
                ? new DataPort<DataAvailability>("DataAvailability")
                : null;

            builder.Canvas.Add(new ChartInteractionFeature
            {
                PointerHitPort = hitPort,
                SupportedModes = modes,
                PointerSnapMode = snapMode,
                ValidDataCountPort = validCountPort,
                ZoomConfig = zoomConfig ?? new ZoomOptions(),
                BoxZoomConfig = boxZoomConfig ?? new BoxZoomOptions(),
                InertiaPanConfig = inertiaPanConfig ?? new InertiaPanOptions(),
                DoubleClickResetConfig = doubleClickResetConfig ?? new DoubleClickResetOptions(),
                RequireClickToActivate = requireClickToActivate,
                DismissOnEscape = dismissOnEscape,
                ZoomStrategy = zoomStrategy ?? new SmartAdaptiveZoomStrategy(),
                AvailabilityPort = availabilityPort
            });

            if (availabilityPort != null)
            {
                builder.Canvas.Add(new DataPagingFeature
                {
                    OnRequireDataAsync = onRequireDataAsync,
                    FetchConfig = fetchConfig ?? new DataFetchOptions(),
                    AvailabilityPort = availabilityPort
                });
            }

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
