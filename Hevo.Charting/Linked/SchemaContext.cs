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
        /// <summary>schema 的顶层视口端口集。独立=自建;联动=共享。</summary>
        public abstract ViewportPorts Viewport { get; }

        /// <summary>schema 的指针 hit 端口。独立=自建;联动=共享。</summary>
        public abstract DataPort<PointerHitState?> HitPort { get; }

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
        public override ViewportPorts Viewport { get; } = new();
        public override DataPort<PointerHitState?> HitPort { get; } = new("LocalHit");
    }

    internal sealed class LinkedMasterSchemaContext : SchemaContext
    {
        private readonly LinkedChartContext _ctx;
        public LinkedMasterSchemaContext(LinkedChartContext ctx) => _ctx = ctx;

        public override ViewportPorts Viewport => _ctx.SharedViewport;
        public override DataPort<PointerHitState?> HitPort => _ctx.SharedHit;

        /// <summary>主图装饰:仅强制水平边距与 ctx 对齐(crosshair 跨 cell 对齐前提)。</summary>
        public override void Decorate(IFeatureContext canvas)
            => ForceHorizontalMargin(canvas, _ctx.HorizontalLeft, _ctx.HorizontalRight);

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

        public override ViewportPorts Viewport => _ctx.SharedViewport;
        public override DataPort<PointerHitState?> HitPort => _ctx.SharedHit;

        /// <summary>
        /// 联动副图装饰:
        /// <list type="bullet">
        ///   <item>强制水平边距与 ctx 对齐(crosshair 对齐前提)</item>
        ///   <item>摘除 <see cref="ViewportManagerFeature"/>(避免双管家)</item>
        ///   <item>挂 <see cref="ExternalViewportFeature"/> 标记,告知 AddDomainAxis 视口由外部提供</item>
        ///   <item>摘除 <see cref="TooltipWidgetFeature"/>(业务规约:tooltip 只主图弹)</item>
        /// </list>
        /// </summary>
        public override void Decorate(IFeatureContext canvas)
        {
            LinkedMasterSchemaContext.ForceHorizontalMargin(canvas, _ctx.HorizontalLeft, _ctx.HorizontalRight);
            canvas.Remove<ViewportManagerFeature>();
            canvas.Add(new ExternalViewportFeature());
            canvas.Remove<TooltipWidgetFeature>();
        }
    }
}
