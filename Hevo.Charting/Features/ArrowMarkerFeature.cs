using System;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// 箭头 marker 一等 Feature —— 接 <c>DataPort&lt;ROM&lt;ArrowMarker&gt;&gt;</c>,自管 <see cref="ArrowMarkerLayer"/>
    /// + hover/双击 hit-test。典型场景是 K 线买点 / 卖点。
    ///
    /// <para>
    /// <b>Axes 协议</b>(跟 <see cref="LineSeriesFeature"/> 同款):
    /// <list type="bullet">
    ///   <item>X 轴:走 inherit —— 消费 cell shared <see cref="XAxisTrait"/>(主图 DomainAxisDateTimeFeature
    ///         IsShared=true 写入,联动 dashboard 自动跨 cell 镜像)</item>
    ///   <item>Y 轴:本 Feature 持有 <see cref="YRangePort"/>,OnProject 时把 Y range 转 <see cref="YAxisTrait"/>
    ///         publish 到 <b>self layer-local</b>(不污染 cell shared)。每个 pane 自己的 Y 量程独立。</item>
    /// </list>
    /// 旧版没有 YRangePort 字段,layer 走 inherit 拿 cell-shared YAxisTrait,但 framework 设计上 Y 是 cell-local
    /// 的(每个 pane 独立),cell shared 根本没人 publish,arrow 因此永远不渲染。本修复让 Feature 自己 publish
    /// 到 layer-local,跟 LineSeriesFeature / BarSeriesFeature 行为对齐。
    /// </para>
    /// </summary>
    public sealed class ArrowMarkerFeature : ElementPlotFeatureBase<ArrowMarker, ArrowMarkerLayer>
    {
        /// <summary>
        /// Y 轴量程端口 —— 接 cell 内的 <see cref="UniversalAutoScaleFeature.YRangePort"/> 或 <c>ConstantRangeFeature.OutputPort</c>。
        /// null 时走旧 inherit 兜底(要求 cell shared 有 YAxisTrait,通常只对主图独占场景适用)。
        /// </summary>
        public DataPort<RealRange>? YRangePort { get; init; }

        protected override ArrowMarkerLayer CreateLayer()
            => new ArrowMarkerLayer { LayerName = string.IsNullOrEmpty(LayerName) ? "ArrowMarker" : LayerName };

        protected override void PublishSpecTrait(VisualProxy<ArrowMarkerLayer> proxy, ReadOnlyMemory<ArrowMarker> spec)
            => proxy.PublishData(new ArrowSpecTrait(spec));

        protected override void OnProject(FeatureContext ctx)
        {
            // 先让基类发布 SpecTrait
            base.OnProject(ctx);

            // Hook 规则:UsePort 必须每次按相同顺序调用,不能放 if 内。
            // YRangePort 没绑时塞一个永不写入的虚拟 port,UsePort 返默认值,后续 IsValid 检查跳过 publish。
            if (YRangePort == null) return;
            var (yRange, _) = ctx.UsePort(YRangePort);
            if (Layer != null && yRange.IsValid)
            {
                // Y range publish 到 self layer-local。ArrowMarkerLayer 读 layer-local YAxisTrait,
                // 跟 LineSeriesFeature/BarSeriesFeature 协议一致。
                ctx.For(Layer).UpdateYAxis(yRange);
            }
        }

        protected override HevoPoint? ProjectToScreen(RenderContext ctx, in ArrowMarker elem)
        {
            var plotArea = ctx.GetPlotArea();
            if (plotArea.IsEmpty) return null;

            var xAxis = ctx.Shared().Read<XAxisTrait>();
            // Y 轴优先用本 Feature 自己 publish 的 layer-local —— 跟 layer 渲染时拿到的同一份;
            // YRangePort 没绑就 fallback shared(兼容旧主图场景)。
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
