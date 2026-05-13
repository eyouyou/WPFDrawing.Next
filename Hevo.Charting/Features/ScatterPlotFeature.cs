using System;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// 散点 plot 一等 Feature —— 接 <c>DataPort&lt;ROM&lt;ScatterPoint&gt;&gt;</c>,自管 <see cref="ScatterPlotLayer"/>
    /// + hover/双击 hit-test。<see cref="XDomain"/> / <see cref="YDomain"/> 设值 = 显式 mode(publish layer-local
    /// XAxis/YAxis 给自己的 layer);留空 = inherit 主图 shared XAxis/YAxis(跟 arrow_markers 同款)。
    /// 跟数据来源(Python handler / C# 直接喂)无关,纯渲染 + 交互。
    ///
    /// <para>
    /// <b>跟 Tooltip 联动</b>:配置基类 <see cref="ElementPlotFeatureBase{TSpec, TLayer}.HitStatePort"/>
    /// 后,hover 命中时下发 <see cref="PointerHitState"/>(LocalIndex=元素 idx)。
    /// <see cref="TooltipWidgetFeature{TX}"/> 接这个端口可 anchor 到散点位置。
    /// Tooltip 行内容由业务侧自行决定:可只用 XAxisDataPort 显示一行,也可加自定义 Feature 在
    /// 散点 layer 上 publish <see cref="MetaTrait"/> + <see cref="DoubleSeriesDataTrait"/> 让 tooltip 自动捞多行。
    /// </para>
    /// </summary>
    public sealed class ScatterPlotFeature : ElementPlotFeatureBase<ScatterPoint, ScatterPlotLayer>
    {
        /// <summary>显式 X 轴范围。null = inherit shared XAxisTrait(跟主图共享 X)。</summary>
        public RealRange? XDomain { get; init; }

        /// <summary>显式 Y 轴范围。null = inherit shared YAxisTrait。</summary>
        public RealRange? YDomain { get; init; }

        protected override ScatterPlotLayer CreateLayer()
            => new ScatterPlotLayer { LayerName = string.IsNullOrEmpty(LayerName) ? "ScatterPlot" : LayerName };

        protected override void OnAfterAttachLayer(RenderContext ctx)
        {
            // 显式 X/Y range → publish layer-local XAxisTrait/YAxisTrait,layer 据此投影。
            // null = inherit,layer 消费 shared XAxisTrait/YAxisTrait(跟主图共享 axes)。
            if (XDomain.HasValue) Proxy.PublishData(new XAxisTrait(XDomain.Value));
            if (YDomain.HasValue) Proxy.PublishData(new YAxisTrait(YDomain.Value));
        }

        protected override void PublishSpecTrait(VisualProxy<ScatterPlotLayer> proxy, ReadOnlyMemory<ScatterPoint> spec)
            => proxy.PublishData(new ScatterSpecTrait(spec));

        protected override HevoPoint? ProjectToScreen(RenderContext ctx, in ScatterPoint elem)
        {
            var plotArea = ctx.GetPlotArea();
            if (plotArea.IsEmpty) return null;

            // 显式 X/Y 用本地配置,缺省的 axis 从 shared 拿(跟 layer 同源)。
            RealRange xRange, yRange;
            if (XDomain.HasValue) xRange = XDomain.Value;
            else
            {
                var sharedX = ctx.Shared().Read<XAxisTrait>();
                if (sharedX == null) return null;
                xRange = sharedX.Viewport;
            }
            if (YDomain.HasValue) yRange = YDomain.Value;
            else
            {
                var sharedY = ctx.Shared().Read<YAxisTrait>();
                if (sharedY == null) return null;
                yRange = sharedY.Viewport;
            }
            if (!xRange.IsValid || !yRange.IsValid) return null;

            double sx = plotArea.Left + (elem.X - xRange.Min) / xRange.Span * plotArea.Width;
            double sy = plotArea.Top + (1 - (elem.Y - yRange.Min) / yRange.Span) * plotArea.Height;
            if (sx < plotArea.Left || sx > plotArea.Right) return null;
            if (sy < plotArea.Top || sy > plotArea.Bottom) return null;
            return new HevoPoint((float)sx, (float)sy);
        }

    }
}
