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

    // 改 `: Feature`:本 feature 不消费 Viewport,
    // 历史继承 ChartFeature 仅借生命周期钩子,导致 ChartFeature.OnAttached 强制要求 schema 装 viewport
    // 持有者,把"框架自动装的 layout"跟"业务侧自助配置 viewport"耦合到一起 —— 蓝图缺 PortsFeature 时
    // 框架自动 SetupLayout 加的 GridLayoutFeature.OnAttached 立刻 throw。改 Feature 基类后两者解耦。
    public class GridLayoutFeature : Feature
    {
        public override FeaturePhase Phase => FeaturePhase.Layout;

        // 一个图最多一个 GridLayoutFeature —— 多个并存会让 PlotAreaTrait 互相覆盖,
        // 历史白屏 bug 的根因。Add 检测同类型已存在时自动替换。
        public override bool IsSingleton => true;

        /// <summary>左侧边距(给 Y 轴留位)。中间列固定吃 1*,所以这里只决定左边栏宽。</summary>
        public ChartLength Left { get; set; } = ChartLength.Pixel(0);

        /// <summary>右侧边距(双 Y 轴右边或 Tooltip 安全留白)。</summary>
        public ChartLength Right { get; set; } = ChartLength.Pixel(0);

        /// <summary>顶部边距(预留给 Header / 标题等浮层)。</summary>
        public ChartLength Top { get; set; } = ChartLength.Pixel(0);

        /// <summary>底部边距(给 X 轴时间栏留位)。</summary>
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
