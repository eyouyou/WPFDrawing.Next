using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Linked
{
    /// <summary>
    /// 标记 feature:声明本 cell 的视口由外部提供(典型:跨 cell 联动 dashboard 中,
    /// 副图共享主图 <see cref="Features.ViewportManagerFeature"/> 写出的
    /// <see cref="ViewportPorts.UserRange"/> / <see cref="ViewportPorts.ActiveRange"/> /
    /// <see cref="ViewportPorts.LogicalLength"/>,经端口镜像桥同步到本 cell 的 board)。
    ///
    /// <para>
    /// 用作 <see cref="FeatureCanvasScopedExtensions.AddDomainAxis"/> 等装配方法的"安全闸"
    /// 第二选项:联动副图不挂 ViewportManagerFeature(避免双管家),但持有本 marker
    /// 同样视为视口已就位,可以正常装配 X 轴。
    /// </para>
    ///
    /// <para>
    /// 由 <see cref="SchemaContext.LinkedPane"/> 装饰策略中的 <c>SetupViewport</c> 钩子自动挂载,
    /// 业务方一般不直接 new。
    /// </para>
    /// </summary>
    public sealed class ExternalViewportFeature : ChartFeature
    {
        // 仅作 marker,Project 阶段不参与渲染。Phase 取 PreLayout 保证早于轴/序列等检查路径。
        public override FeaturePhase Phase => FeaturePhase.PreLayout;

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) { }

        protected override void OnProject(FeatureContext ctx) { }
    }
}
