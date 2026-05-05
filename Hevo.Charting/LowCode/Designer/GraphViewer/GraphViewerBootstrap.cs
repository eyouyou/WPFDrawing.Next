using Hevo.Charting.Features;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// GraphViewer 一键初始化:
    /// 1. 触发 <see cref="BuiltinRegistration"/> 把 Hevo.Charting 自带 Feature/Trait/DataSource 全登记进 ComponentRegistry。
    /// 2. 把 GraphViewer 自带的 wrappers (<see cref="GraphViewer.Wrappers.NumericAxisFeature"/> 等) 也登记进去。
    /// 3. 给框架内置 Feature 上点端口元数据 (Output 方向、注释) —— 走
    ///    <see cref="PortMetadataRegistry"/>,绝不污染框架源码。
    ///
    /// 业务侧自定义 Feature 想被画布识别,只需:
    /// <code>
    ///   BuiltinRegistration.RegisterAssemblyOf&lt;MyFeature&gt;();
    ///   PortMetadataRegistry.RegisterOutputs&lt;MyFeature&gt;("MyOutPort");
    /// </code>
    /// 这样框架本身一行不动,新业务也能享受可视化端口方向。
    /// </summary>
    public static class GraphViewerBootstrap
    {
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            // 1. 内置 + GraphViewer wrappers 登记
            BuiltinRegistration.RegisterBuiltins();
            BuiltinRegistration.RegisterAssemblyOf<Wrappers.CrosshairDoubleFeature>();

            // 类别登记 —— 跟 FeatureCanvasExtensions 里的 4 类 builder 对齐。
            // 业务自定义 Feature 启动时自行 Register<>(Category) 即可,GraphViewer 跟着走。
            FeatureCategoryRegistry.Register<GridLayoutFeature>(FeatureCategory.Environment);
            FeatureCategoryRegistry.Register<ViewportManagerFeature>(FeatureCategory.Environment);
            FeatureCategoryRegistry.Register<UniversalAutoScaleFeature>(FeatureCategory.Environment);
            FeatureCategoryRegistry.Register<PlotAreaDecorFeature>(FeatureCategory.Environment);
            FeatureCategoryRegistry.Register<UniversalHeaderFeature>(FeatureCategory.Environment);
            FeatureCategoryRegistry.Register<DataPagingFeature>(FeatureCategory.Environment);

            FeatureCategoryRegistry.Register<AxisFeature>(FeatureCategory.Axes);

            FeatureCategoryRegistry.Register<LineSeriesFeature>(FeatureCategory.Series);
            FeatureCategoryRegistry.Register<BarSeriesFeature>(FeatureCategory.Series);
            FeatureCategoryRegistry.Register<CandleFeature>(FeatureCategory.Series);
            // AnonymousLayerFeature 是 internal,不通过 picker 暴露;业务 DSL 自己 new。

            FeatureCategoryRegistry.Register<ChartInteractionFeature>(FeatureCategory.Interaction);
            FeatureCategoryRegistry.Register<CrosshairFeature<double>>(FeatureCategory.Interaction);
            FeatureCategoryRegistry.Register<TooltipWidgetFeature<double>>(FeatureCategory.Interaction);
            FeatureCategoryRegistry.Register<Wrappers.CrosshairDoubleFeature>(FeatureCategory.Interaction);
            FeatureCategoryRegistry.Register<Wrappers.TooltipDoubleWidgetFeature>(FeatureCategory.Interaction);
            FeatureCategoryRegistry.Register<ConsoleDebuggerFeature>(FeatureCategory.Interaction);

            // Trait 概念上属于"环境配置"(全局样式 / 坐标策略 / 画刷主题), 默认归入 Environment 泳道。
            // 业务可对自家 trait 单独 Register<MyTrait>(FeatureCategory.Trait) 拉回独立泳道。
            FeatureCategoryRegistry.Register<Hevo.Charting.Core.ScaleStrategyTrait>(FeatureCategory.Environment);

            // 2. 端口方向 —— 框架 first-party feature 直接用 [PortDirection] 标注 (见 UniversalAutoScale /
            //    ChartInteraction 源码)。这里只补注释。第三方 / 不便改源码的类型,业务侧用
            //    PortMetadataRegistry.RegisterOutputs 兜底即可。

            // 3. 端口注释 (鼠标悬停浮窗显示;长句子放这里比硬塞 [Description("...")] 进框架更体面)
            PortMetadataRegistry.RegisterDescription<LineSeriesFeature>(
                nameof(LineSeriesFeature.DataPort),
                "线段值数据源,每根索引对应一个 double 值。允许 NaN 制造断点。");
            PortMetadataRegistry.RegisterDescription<LineSeriesFeature>(
                nameof(LineSeriesFeature.YRangePort),
                "Y 轴量程。多线共用 Y 轴时由 UniversalAutoScale 算好后流入。");

            PortMetadataRegistry.RegisterDescription<UniversalAutoScaleFeature>(
                nameof(UniversalAutoScaleFeature.YRangePort),
                "极值计算结果。下游 Axis / Series 共用同一根。");
            PortMetadataRegistry.RegisterDescription<UniversalAutoScaleFeature>(
                nameof(UniversalAutoScaleFeature.BaseLinePort),
                "基准线 (0 / 昨收价 / 半轴悬浮等)。柱状图等从基线向上下画的图层消费。");
            PortMetadataRegistry.RegisterDescription<UniversalAutoScaleFeature>(
                nameof(UniversalAutoScaleFeature.RawExtremaPort),
                "视口内未经对称/padding 扩展的真实 high/low,供 AnchoredNiceTick 把当日极值标在 Y 轴上。");
            PortMetadataRegistry.RegisterDescription<UniversalAutoScaleFeature>(
                nameof(UniversalAutoScaleFeature.ReferencePort),
                "对称参考值。仅 SymmetricReference 策略用 —— 分时图传昨收价。");

            PortMetadataRegistry.RegisterDescription<ChartInteractionFeature>(
                nameof(ChartInteractionFeature.PointerHitPort),
                "光标命中状态。Crosshair / Tooltip 据此决定显隐与定位。");

            PortMetadataRegistry.RegisterDescription<CrosshairFeature<double>>(
                nameof(CrosshairFeature<double>.HitStatePort),
                "上游 ChartInteractionFeature 写入的命中状态。这里只读。");
            PortMetadataRegistry.RegisterDescription<CrosshairFeature<double>>(
                nameof(CrosshairFeature<double>.XAxisDataPort),
                "X 轴原始数据。命中索引会回查这根拿到 TX 用于格式化。");

            PortMetadataRegistry.RegisterDescription<TooltipWidgetFeature<double>>(
                nameof(TooltipWidgetFeature<double>.HitStatePort),
                "上游 ChartInteractionFeature 写入的命中状态。Tooltip 据此显隐。");
            PortMetadataRegistry.RegisterDescription<TooltipWidgetFeature<double>>(
                nameof(TooltipWidgetFeature<double>.XAxisDataPort),
                "X 轴原始数据,用于 Tooltip 第一行的 X 字段显示。");

            PortMetadataRegistry.RegisterDescription<AxisFeature>(
                nameof(AxisFeature.RangePort),
                "本轴消费的逻辑量程。横轴接 Viewport.ActiveRange,纵轴接 AutoScale 算出的 YRangePort。");


            PortMetadataRegistry.RegisterDescription<UniversalAutoScaleFeature>(
                nameof(UniversalAutoScaleFeature.ValuePorts),
                "极值统计参与列(扇入端口,可同时接多根)。从 DataSource 各 vector 输出 / 多线指标的列流过来。");
        }
    }
}
