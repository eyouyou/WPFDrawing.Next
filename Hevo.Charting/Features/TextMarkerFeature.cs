using System;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// 文字 marker 一等 Feature —— 接 <c>DataPort&lt;ROM&lt;TextMarker&gt;&gt;</c>,自管 <see cref="TextMarkerLayer"/>
    /// + hover/双击 hit-test。典型场景是 K 线信号文字标注("BUY" / "SELL" / 价格批注)。
    ///
    /// <para>
    /// 跟 <see cref="ArrowMarkerFeature"/> 完全对称(共享 <see cref="ElementPlotFeatureBase{TSpec,TLayer}"/> 基类),
    /// 只是渲染 Spec 类型换成 <see cref="TextMarker"/>。Python 端通过 <c>@indicator series=[('name', 'text_markers', color, 0)]</c>
    /// + 返回 <c>list[{logical_x, logical_y, text, color, font_size, anchor}]</c> 触发本 Feature。
    /// </para>
    ///
    /// <para>
    /// <b>Axes 协议</b>(跟 <see cref="ArrowMarkerFeature"/> 同款):X 走 inherit(主图 shared XAxisTrait),
    /// Y 走 <see cref="YRangePort"/> 自 publish 到 layer-local;旧 inherit 兜底支持旧主图独占场景。
    /// </para>
    /// </summary>
    public sealed class TextMarkerFeature : ElementPlotFeatureBase<TextMarker, TextMarkerLayer>
    {
        /// <summary>
        /// Y 轴量程端口 —— 接 cell 内的 <see cref="UniversalAutoScaleFeature.YRangePort"/> 或 <c>ConstantRangeFeature.OutputPort</c>。
        /// null 时走旧 inherit 兜底(要求 cell shared 有 YAxisTrait,通常只对主图独占场景适用)。
        /// </summary>
        public DataPort<RealRange>? YRangePort { get; init; }

        protected override TextMarkerLayer CreateLayer()
            => new TextMarkerLayer { LayerName = string.IsNullOrEmpty(LayerName) ? "TextMarker" : LayerName };

        protected override void PublishSpecTrait(VisualProxy<TextMarkerLayer> proxy, ReadOnlyMemory<TextMarker> spec)
            => proxy.PublishData(new TextSpecTrait(spec));

        protected override void OnProject(FeatureContext ctx)
        {
            base.OnProject(ctx);

            if (YRangePort == null) return;
            var (yRange, _) = ctx.UsePort(YRangePort);
            if (Layer != null && yRange.IsValid)
            {
                // Y range publish 到 self layer-local。TextMarkerLayer 读 layer-local YAxisTrait,
                // 跟 ArrowMarkerFeature 协议一致。
                ctx.For(Layer).UpdateYAxis(yRange);
            }
        }

        protected override HevoPoint? ProjectToScreen(RenderContext ctx, in TextMarker elem)
        {
            var plotArea = ctx.GetPlotArea();
            if (plotArea.IsEmpty) return null;

            var xAxis = ctx.Shared().Read<XAxisTrait>();
            YAxisTrait? yAxis = null;
            if (Layer != null) yAxis = ctx.For(Layer).Read<YAxisTrait>();
            yAxis ??= ctx.Shared().Read<YAxisTrait>();
            var scale = ctx.Shared().Read<ScaleStrategyTrait>();
            if (xAxis == null || yAxis == null || scale == null) return null;

            var p = CoordinateExtensions.ProjectToScreen(plotArea, xAxis.Viewport, yAxis.Viewport, scale, elem.LogicalX, elem.LogicalY);
            if (float.IsNaN(p.X) || float.IsNaN(p.Y)) return null;
            if (p.X < plotArea.Left || p.X > plotArea.Right) return null;
            return p;
        }
    }
}
