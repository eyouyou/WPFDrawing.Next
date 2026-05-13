using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using System.Windows;

namespace Hevo.Charting.Linked
{
    /// <summary>
    /// schema 运行所在的上下文——由外部注入,决定本 schema 用哪套端口,以及 <c>DefineFeatures</c>
    /// 跑完后是否动 feature 列表(强制 layout 统一、摘除独占 feature 等)。
    ///
    /// <para>
    /// schema 永远像独立模式那样写:用基类暴露的 <c>Viewport</c> / <c>HitPort</c>,自己 hardcode
    /// 想要的水平边距、调用 <c>SetupViewport</c> / <c>AddTooltip</c>。是否被装饰成联动副图,
    /// 由本上下文的 <see cref="Decorate"/> 钩子悄悄完成,schema body 全程无感知。
    /// </para>
    ///
    /// <para>
    /// 与 <see cref="LinkedChartContext"/> 的区别:后者是 dashboard 级共享态(端口镜像桥 +
    /// 统一边距),本类是 per-schema 上下文(每个 schema 不同——主图 / 副图 / 独立)。两者
    /// 通过 <see cref="LinkedMaster(LinkedChartContext)"/> / <see cref="LinkedPane(LinkedChartContext)"/>
    /// 工厂衔接。
    /// </para>
    /// </summary>
    public abstract class SchemaContext
    {
        // Viewport 抽象属性已删:viewport 唯一权威 = ViewportManagerFeature.Ports / ExternalViewportFeature.Ports
        // —— 由 Decorate 钩子在装配阶段把共享端口塞进 feature,schema 不再"持有"viewport。

        /// <summary>schema 的指针 hit 端口。独立=自建;联动=共享。</summary>
        public abstract DataPort<PointerHitState?> HitPort { get; }

        /// <summary>
        /// dashboard 共享的 viewport ports —— Standalone 返 null(各 schema 自带 local viewport);
        /// LinkedMaster/Pane 返 <see cref="LinkedChartContext.SharedViewport"/>(共享同一实例)。
        /// ChartBlueprint.DefineFeatures 用此对象提前注册 <c>cell:viewport.*</c> 到 _portRegistry,
        /// 让 features 的 PortBinding 在解析时拿到 Decorate 后真正写入的 port instance,
        /// 不被"DefineFeatures 跑在 Decorate 之前"的时序坑掉。
        /// </summary>
        public abstract ViewportPorts? SharedViewport { get; }

        /// <summary>
        /// schema 可见的 dashboard 级共享端口注册表。
        /// <list type="bullet">
        ///   <item>Standalone:返空字典(无 dashboard 上下文)</item>
        ///   <item>LinkedMaster / LinkedPane:返 <see cref="LinkedChartContext.SharedPorts"/>(共享 dict 引用)</item>
        /// </list>
        /// <para>
        /// <see cref="LowCode.Designer.ChartBlueprint"/> 在 DefineFeatures 期间遍历此字典,把每项以
        /// <c>"dashboard:{name}"</c> 形态注入 schema 私有 <c>_portRegistry</c>,Feature 的 PortBindings
        /// 引用 <c>"dashboard:linkedHit"</c> 等即拿到同一 DataPort 实例(实现跨 cell 同步)。
        /// </para>
        /// </summary>
        public abstract IReadOnlyDictionary<string, IDataPort> SharedPorts { get; }

        /// <summary>
        /// 装饰钩子:由 <see cref="ReactiveSchema"/> 在 <c>DefineFeatures</c> 跑完之后调用。
        /// 默认 no-op(独立);联动场景重写来强制水平边距统一(crosshair 对齐前提)、
        /// 摘除视口管家 / tooltip 等独占 feature,并挂 <see cref="ExternalViewportFeature"/> 标记。
        /// </summary>
        public virtual void Decorate(IFeatureContext canvas) { }

        // ── 通过 WPF 视觉树传递 ──────────────────────────────────────────
        //
        // 由容器(典型 LinkedChartDashboard)在 ChartCell 上 SetAttached(...) 注入,
        // ReactiveSchema 在 compose 时从自己的 ChartCell 读出。schema 不需要 ctor 参数,
        // 调用面只剩 `new VolumeSchema(ds)`,联动 / 独立的差异完全由容器决定。

        public static readonly DependencyProperty AttachedProperty =
            DependencyProperty.RegisterAttached(
                "Attached",
                typeof(SchemaContext),
                typeof(SchemaContext),
                new FrameworkPropertyMetadata(default(SchemaContext)));

        /// <summary>读取附加在 <paramref name="d"/> 上的 schema 上下文(典型:ChartCell)。</summary>
        public static SchemaContext? GetAttached(DependencyObject d) =>
            (SchemaContext?)d.GetValue(AttachedProperty);

        /// <summary>把 schema 上下文附加到 <paramref name="d"/>(典型:dashboard 给每个 cell 注入对应的 master/pane 上下文)。</summary>
        public static void SetAttached(DependencyObject d, SchemaContext? value) =>
            d.SetValue(AttachedProperty, value);

        // ── 工厂 ─────────────────────────────────────────────────────────

        /// <summary>独立运行(无 dashboard)。schema 自管全套——layout 由 schema body 自己决定。</summary>
        public static SchemaContext Standalone() => new StandaloneSchemaContext();

        /// <summary>主图嵌进 dashboard。共享 viewport/hit;Decorate 强制水平边距与 ctx 对齐。</summary>
        public static SchemaContext LinkedMaster(LinkedChartContext ctx) => new LinkedMasterSchemaContext(ctx);

        /// <summary>副图嵌进 dashboard。共享 + Decorate 强制边距 + 摘视口管家/tooltip。</summary>
        public static SchemaContext LinkedPane(LinkedChartContext ctx) => new LinkedPaneSchemaContext(ctx);
    }

    internal sealed class StandaloneSchemaContext : SchemaContext
    {
        public override DataPort<PointerHitState?> HitPort { get; } = new("LocalHit");

        private static readonly IReadOnlyDictionary<string, IDataPort> _empty
            = new Dictionary<string, IDataPort>();
        public override IReadOnlyDictionary<string, IDataPort> SharedPorts => _empty;

        // Standalone:无 dashboard 共享 viewport。schema 用自家 ViewportPortsFeature.Ports(local)。
        public override ViewportPorts? SharedViewport => null;
    }

    internal sealed class LinkedMasterSchemaContext : SchemaContext
    {
        private readonly LinkedChartContext _ctx;
        public LinkedMasterSchemaContext(LinkedChartContext ctx) => _ctx = ctx;

        public override DataPort<PointerHitState?> HitPort => _ctx.SharedHit;
        public override IReadOnlyDictionary<string, IDataPort> SharedPorts => _ctx.SharedPorts;
        public override ViewportPorts? SharedViewport => _ctx.SharedViewport;

        /// <summary>
        /// 主图装饰:
        /// <list type="bullet">
        ///   <item>强制水平边距与 ctx 对齐(crosshair 跨 cell 对齐前提)</item>
        ///   <item>把框架 ensure 的 <see cref="ViewportPortsFeature"/>.Ports 替换为 dashboard 共享端口
        ///         (viewport 权威源 = PortsFeature.Ports,联动多 cell 必须指向同一份;
        ///         主图保留 VPM 钳制业务,副图 Remove VPM 见 LinkedPane)</item>
        /// </list>
        /// </summary>
        public override void Decorate(IFeatureContext canvas)
        {
            ForceHorizontalMargin(canvas, _ctx.HorizontalLeft, _ctx.HorizontalRight);
            var ports = canvas.Find<ViewportPortsFeature>();
            if (ports != null) ports.Ports = _ctx.SharedViewport;
        }

        internal static void ForceHorizontalMargin(IFeatureContext canvas, ChartLength left, ChartLength right)
        {
            // schema 在 SetupLayout 里塞了独立模式偏好的 Left/Right;联动场景 dashboard 强制覆盖求统一。
            // 直接 mutate 现有 GridLayoutFeature 的 Left/Right——保留 schema 设的 Top/Bottom。
            var layout = canvas.Find<GridLayoutFeature>();
            if (layout == null) return;
            layout.Left  = left;
            layout.Right = right;
        }
    }

    internal sealed class LinkedPaneSchemaContext : SchemaContext
    {
        private readonly LinkedChartContext _ctx;
        public LinkedPaneSchemaContext(LinkedChartContext ctx) => _ctx = ctx;

        public override DataPort<PointerHitState?> HitPort => _ctx.SharedHit;
        public override IReadOnlyDictionary<string, IDataPort> SharedPorts => _ctx.SharedPorts;
        public override ViewportPorts? SharedViewport => _ctx.SharedViewport;

        /// <summary>
        /// 联动副图装饰:
        /// <list type="bullet">
        ///   <item>强制水平边距与 ctx 对齐(crosshair 对齐前提)</item>
        ///   <item>摘除 <see cref="ViewportManagerFeature"/>(避免双管家:副图 ActiveRange 由主图 VPM 写,自家挂会双写竞态)</item>
        ///   <item>把框架 ensure 的 <see cref="ViewportPortsFeature"/>.Ports 替换为 dashboard 共享端口 ——
        ///         兄弟 ChartFeature 通过 <see cref="ReactiveSchema.GetEffectiveViewport"/> 拿到共享 ports</item>
        ///   <item>摘除 <see cref="TooltipWidgetFeature"/>(业务规约:tooltip 只主图弹)</item>
        /// </list>
        /// </summary>
        public override void Decorate(IFeatureContext canvas)
        {
            LinkedMasterSchemaContext.ForceHorizontalMargin(canvas, _ctx.HorizontalLeft, _ctx.HorizontalRight);
            canvas.Remove<ViewportManagerFeature>();
            var ports = canvas.Find<ViewportPortsFeature>();
            if (ports != null)
            {
                ports.Ports = _ctx.SharedViewport;
                ports.IsExternal = true;  // 替代旧 ExternalViewportFeature marker —— 告诉 AddDomainAxis 视口策略主图配,本副图豁免 VPM 检查
            }
            canvas.Remove<TooltipWidgetFeature>();
        }
    }
}
