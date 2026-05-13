using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// Viewport ports 的纯持有者。
    /// <para>
    /// <b>架构定位</b>:从原 <see cref="ViewportManagerFeature"/> 拆出"持有 Ports"职责。
    /// 原 VPM 一身两职(Ports 持有 + UserRange→ActiveRange 钳制业务)导致:
    /// </para>
    /// <list type="bullet">
    ///   <item>联动副图不要钳制业务 → 副图被迫 Remove VPM + 装 marker(原 ExternalViewportFeature),
    ///         <see cref="ChartFeature"/> 注入路径需要双重 lookup;</item>
    ///   <item>蓝图不强制装 VPM → 兄弟 ChartFeature 拿不到 ports → 字段被迫声明 <c>null!</c> 撒谎。</item>
    /// </list>
    /// <para>
    /// 拆分后:
    /// </para>
    /// <list type="bullet">
    ///   <item>本 feature(<b>持有</b>)—— 由 <see cref="ReactiveSchema.InitializeRegistry"/> 框架强制 ensure,
    ///         任何 schema 一律有一份。Phase=PreLayout(早于 Layout 钳制业务)。</item>
    ///   <item><see cref="ViewportManagerFeature"/>(<b>钳制业务</b>)—— 业务可选挂,通过 base
    ///         <see cref="ChartFeature.Viewport"/> 拿本 feature 注入的 Ports 做 Watch + 写 ActiveRange。
    ///         联动副图不挂(钳制由主图独占,避免双写竞态)。</item>
    /// </list>
    /// <para>
    /// 因为本 feature 框架强制 ensure,<see cref="ChartFeature.Viewport"/> 字段从
    /// <c>ViewportPorts? = null!</c>(撒谎)恢复成 non-nullable 真相,所有消费点不再需要 <c>Viewport!.X</c>。
    /// </para>
    /// </summary>
    public sealed class ViewportPortsFeature : Feature
    {
        // PreLayout(-10):早于布局/钳制/序列绘制阶段。GridLayoutFeature 等 Layout 阶段 ChartFeature
        // OnAttached 注入 Viewport 时本 feature 已就位。
        public override FeaturePhase Phase => FeaturePhase.PreLayout;

        // 一个 schema 只该有一份 Ports 实例 —— 多份会让兄弟 ChartFeature 注入到不同 ports,失去 viewport
        // 联动语义。Add 检测同类型已存在自动替换;不阻止 ReactiveSchema.InitializeRegistry 的 ensure 路径。
        public override bool IsSingleton => true;

        private ViewportPorts _ports = new();

        /// <summary>
        /// Viewport ports 实例。
        /// <list type="bullet">
        ///   <item>独立 schema:默认自建一份,自家 VPM 钳制 ActiveRange,自家 InteractionFeature 写 UserRange;</item>
        ///   <item>联动 dashboard 主图:<see cref="Linked.SchemaContext.LinkedMaster"/> Decorate 时 mutate 为 SharedViewport;</item>
        ///   <item>联动 dashboard 副图:<see cref="Linked.SchemaContext.LinkedPane"/> Decorate 时同样 mutate 为 SharedViewport,
        ///         但额外 <c>Remove&lt;ViewportManagerFeature&gt;()</c> 让钳制由主图独占。</item>
        /// </list>
        /// setter mutate 后,若 feature 已 attach 到 chart,自动 SetAttached refresh —— 让兄弟 ChartFeature 的
        /// <see cref="ChartFeature.Viewport"/> property getter 下次 access 拿到最新 ports(无需 framework 协调)。
        /// </summary>
        public ViewportPorts Ports
        {
            get => _ports;
            internal set
            {
                _ports = value;
                // attach 后 mutate(典型 Decorate)→ 同步 refresh attached property,Viewport getter 自动拿到最新
                if (Chart != null) ViewportPorts.SetAttached(Chart, _ports);
            }
        }

        /// <summary>
        /// true = 视口 ports 来自外部(典型:dashboard 副图通过 <see cref="Linked.SchemaContext.LinkedPane"/>
        /// 注入主图共享 ports)—— 副图主动不挂 <see cref="ViewportManagerFeature"/>(钳制由主图独占),
        /// 这个标记替代旧 ExternalViewportFeature marker 角色:
        /// <see cref="FeatureCanvasScopedExtensions.AddDomainAxis"/> 等"安全闸"看到 IsExternal=true 视为
        /// 视口策略已配置(主图配),允许装 X 轴 trait,不强制要求本 schema 挂 VPM。
        /// </summary>
        public bool IsExternal { get; internal set; }

        /// <summary>
        /// 自己挂到 ChartCell attached property —— framework 完全不知 viewport 存在。
        /// <para>
        /// 时序:<see cref="ReactiveSchema.Add"/> 在 chart 已就位时立刻 AttachToHost,所以
        /// SetupViewport helper 一调完本 feature 就 SetAttached;后续业务 helper 链
        /// (env.SetupUniversalHeader / AddDomainAxis 等)在 DefineFeatures 内即可拿到 attached ports。
        /// </para>
        /// </summary>
        protected override void OnAttached() => ViewportPorts.SetAttached(Chart, _ports);

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) { }
        protected override void OnProject(FeatureContext ctx) { }
    }
}
