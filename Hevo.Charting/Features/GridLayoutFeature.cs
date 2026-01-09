using Hevo.Charting.Core;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Features
{
    public static class GridLayoutFeatureExtensions
    {
        public static EnvironmentBuilder SetupLayout(this EnvironmentBuilder builder, ChartLength? left = null, ChartLength? top = null, ChartLength? right = null, ChartLength? bottom = null)
        {
            // 💥 致命防线：彻底解决“白屏”Bug！
            // 强行摘除系统默认初始化的 Grid，防止双重包裹导致 WPF 画布被挤压成 0 像素！
            builder.Canvas.Remove<GridLayoutFeature>();

            var layout = new GridLayoutFeature();
            if (left != null) layout.Left = left.Value;
            if (top != null) layout.Top = top.Value;
            if (right != null) layout.Right = right.Value;
            if (bottom != null) layout.Bottom = bottom.Value;

            builder.Canvas.Add(layout);
            return builder;
        }
    }
    /// <summary>
    /// 修复 L2：布局配置值类型，供子类通过 <see cref="ReactiveSchema.DefaultLayout"/> 覆盖默认边距。
    /// </summary>
    public readonly record struct GridLayoutConfig(
        ChartLength Left,
        ChartLength Top,
        ChartLength Right,
        ChartLength Bottom)
    {
        public static GridLayoutConfig Default => new(
            Left: ChartLength.Star(0.05, 50),
            Top: ChartLength.Pixel(10),
            Right: ChartLength.Pixel(20),
            Bottom: ChartLength.Star(0.06, 20));
    }

    public class GridLayoutFeature : ChartFeature
    {
        public override FeaturePhase Phase => FeaturePhase.Layout;

        public ChartLength Left { get; set; } = ChartLength.Pixel(0);
        public ChartLength Right { get; set; } = ChartLength.Pixel(0);
        public ChartLength Top { get; set; } = ChartLength.Pixel(0);
        public ChartLength Bottom { get; set; } = ChartLength.Pixel(0);

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
        }

        protected override void OnProject(FeatureContext ctx)
        {
            ReadOnlySpan<ChartLength> colDefs = stackalloc ChartLength[] { Left, ChartLength.Star(1), Right };
            ReadOnlySpan<ChartLength> rowDefs = stackalloc ChartLength[] { Top, ChartLength.Star(1), Bottom };

            // 💥 安全代理：直接呼叫 Shared 的扩展方法
            ctx.Shared().ExecuteGrid3x3Layout(colDefs, rowDefs);
        }
    }
}
