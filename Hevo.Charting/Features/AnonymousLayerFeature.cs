using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Features
{
    public static class SimpleLayerExtensions
    {
        /// <summary>
        /// 💥 极简逃生舱：允许用户一键添加物理图层，无需编写 Feature 类！
        /// </summary>
        public static IFeatureContext AddLayer(this IFeatureContext context, IChartLayer layer, FeaturePhase phase = FeaturePhase.Series)
        {
            // 框架在底层偷偷把它包装成 Feature，完美享受 0-GC 查杀和生命周期托管
            return context.Add(new AnonymousLayerFeature(layer, phase));
        }
    }
    /// <summary>
    /// 💥 匿名图层特征：专为“偷懒”和“简单调用”设计的包装器
    /// 它没有任何业务逻辑，唯一的职责就是把用户传进来的图层托管给系统生命周期！
    /// </summary>
    internal class AnonymousLayerFeature : ChartFeature
    {
        private readonly IChartLayer _layer;
        public override FeaturePhase Phase { get; }

        public AnonymousLayerFeature(IChartLayer layer, FeaturePhase phase)
        {
            _layer = layer;
            Phase = phase;
        }

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            // 💥 完美闭环：这里调用了安全的 AttachLayer，图层被系统接管！
            this.AttachLayer(_layer);
        }

        protected override void OnProject(FeatureContext ctx) { }
    }
}
