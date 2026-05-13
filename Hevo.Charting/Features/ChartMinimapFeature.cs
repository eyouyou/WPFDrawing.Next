using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// Chart minimap feature。在 chart 右下角画一个浮窗,显示"数据全范围 + 当前可见 X 范围"指示器。
    /// <para>
    /// **跟 GraphSchema 共用同一个 <see cref="MinimapLayer"/>**。本 feature 只负责把 chart 业务概念
    /// (<see cref="ViewportPorts.LogicalLength"/> / <see cref="ViewportPorts.ActiveRange"/>)翻译成
    /// 几何 <see cref="MinimapTrait"/>:
    /// </para>
    /// <list type="bullet">
    ///   <item>ContentBounds = (0, 0, length, 1) —— 数据 X 范围,Y 单位 1</item>
    ///   <item>ContentItems = null —— chart 不画"数据点细节"(几千根点堆成方块没意义),只剩 viewport 框</item>
    ///   <item>ViewportInContent = (Active.Min, 0, span, 1) —— 当前可见 X 范围矩形</item>
    ///   <item>StretchToFit = true —— X/Y 独立缩放,横条铺满浮窗(graph 用等比 fit + 居中,但 chart 这种长条数据等比缩会出现极细线条)</item>
    /// </list>
    /// </summary>
    public class ChartMinimapFeature : ChartFeature
    {
        // Layout 阶段:浮窗只依赖 ViewportSizeTrait + Viewport.ActiveRange/LogicalLength,
        // 不依赖 series 数据,无需等 Series 阶段算完。
        public override FeaturePhase Phase => FeaturePhase.Layout;

        /// <summary>浮窗宽度(屏幕 px)。默认 200,跟 GraphSchema minimap 一致。</summary>
        public float Width { get; init; } = 200f;

        /// <summary>浮窗高度(屏幕 px)。chart 版只显示横条 + viewport,默认 36 — graph 用 140 高画节点缩略,chart 不需要那么高。</summary>
        public float Height { get; init; } = 36f;

        /// <summary>距 chart 右下角的 margin(屏幕 px)。</summary>
        public float Margin { get; init; } = 12f;

        /// <summary>浮窗内部 padding(屏幕 px)。</summary>
        public float Padding { get; init; } = 4f;

        // Viewport 由 ChartFeature 基类统一持有 (L6 / §B.2.6),直接用 this.Viewport。
        private readonly MinimapLayer _layer = new();

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            AttachLayer(_layer);
        }

        protected override void OnProject(FeatureContext ctx)
        {
            var (length, _) = ctx.UsePort(Viewport.LogicalLength);
            var (active, _) = ctx.UsePort(Viewport.ActiveRange);
            if (length <= 0) return; // 数据未到,不推 trait,layer 自己跳过

            // 把 chart 数据范围 / viewport 翻译成几何矩形,送给通用 MinimapLayer。
            // 内容空间用 (0..length, 0..1) 单位坐标,viewport 拿真实 Active 区间投影即可。
            var contentBounds     = new HevoRect(0, 0, length, 1);
            var viewportInContent = new HevoRect(
                (float)active.Min, 0,
                (float)(active.Max - active.Min), 1);

            ctx.For(_layer).PublishData(new MinimapTrait(
                contentBounds, ContentItems: null, viewportInContent,
                Width, Height, Margin, Padding,
                StretchToFit: true /* chart 长条数据需要拉伸 fit,等比 fit 会让横条极细 */));
        }
    }
}
