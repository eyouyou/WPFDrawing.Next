using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Features
{
    public static class PlotAreaDecorFeatureExtensions
    {
        public static EnvironmentBuilder SetupPlotDecor(this EnvironmentBuilder builder, LineStyle borderStyle)
        {
            // 装饰器允许存在多个，如果业务需要唯一，可以加 Remove
            builder.Canvas.Remove<PlotAreaDecorFeature>();
            builder.Canvas.Add(new PlotAreaDecorFeature { BorderStyle = borderStyle });
            return builder;
        }
    }

    public class PlotAreaDecorFeature : ChartFeature
    {
        // 💥 布局算完 (Layout) 之后就可以立刻执行它的投影了
        public override FeaturePhase Phase => FeaturePhase.Layout;

        public IHevoBrush? BackgroundBrush { get; init; }
        public LineStyle? BorderStyle { get; init; }

        private readonly PlotAreaDecorLayer _layer = new();

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            // 使用基类规范挂载图层
            this.AttachLayer(_layer);
        }

        protected override void OnProject(FeatureContext ctx)
        {
            // 只要有任何一项配置，就推送给图层
            if (BackgroundBrush != null || BorderStyle != null)
            {
                ctx.For(_layer).PublishData(new PlotAreaDecorTrait(BackgroundBrush, BorderStyle));
            }
        }
    }
}
