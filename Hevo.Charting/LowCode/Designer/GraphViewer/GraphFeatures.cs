using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// graph editor 的 A 类纯渲染 features。
    /// <para>
    /// 这一组 feature 都直接继承 <see cref="Feature"/>(不要 ChartFeature —— 它们不需要
    /// <see cref="ViewportPorts"/>;graph 是 2D <c>CanvasTransform</c>,不是 1D 量程)。每个 feature
    /// 接管一个图层 + 一份 trait 投影逻辑:OnCompose 里 <c>AttachLayer</c>,OnProject 里
    /// <c>ctx.UsePort(StatePort)</c> 拉新状态 → 推 trait。
    /// </para>
    /// <para>
    /// <b>StatePort init 注入</b>:跟 chart 侧 <c>LineSeriesFeature { DataPort = candlePorts.Close }</c>
    /// 同款"port-as-input"协议 —— Configurator(<see cref="GraphEditorSchema.DefineFeatures"/>)
    /// 显式喂 port,feature 不做 <c>Find&lt;T&gt;</c> 反查兄弟。
    /// </para>
    /// </summary>
    internal abstract class GraphLayerFeatureBase<TLayer> : Feature where TLayer : IChartLayer, new()
    {
        protected readonly TLayer Layer = new();

        /// <summary>由装配方(GraphEditorSchema)注入的状态端口。null = 未注入,装配错误。</summary>
        public DataPort<GraphState> StatePort { get; init; } = null!;

        // 跟 ChartFeature 模式一致:layer 在 OnCompose 里挂(由 Feature.AttachLayer 录入 _managedLayers,
        // Decompose 时统一摘除)。OnCompose 不订阅 flow.Watch —— 我们走 ProjectAll 路径(UsePort
        // 隐式注册到 Registry),省一份 Watch 闭包。
        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            AttachLayer(Layer);
        }

        protected sealed override void OnProject(FeatureContext ctx)
        {
            var (state, changed) = ctx.UsePort(StatePort);
            if (!changed) return;
            PublishTrait(ctx, state);
        }

        protected abstract void PublishTrait(FeatureContext ctx, GraphState state);
    }

    /// <summary>画布背景 + 缩放感知网格。消费 GraphRenderTrait + ViewportSizeTrait。</summary>
    internal sealed class GraphCanvasFeature : GraphLayerFeatureBase<GraphCanvasLayer>
    {
        protected override void PublishTrait(FeatureContext ctx, GraphState state)
            => ctx.For(Layer).PublishData(new GraphRenderTrait(state));
    }

    /// <summary>节点矩形渲染。</summary>
    internal sealed class GraphNodeFeature : GraphLayerFeatureBase<GraphNodeLayer>
    {
        protected override void PublishTrait(FeatureContext ctx, GraphState state)
            => ctx.For(Layer).PublishData(new GraphRenderTrait(state));
    }

    /// <summary>边曲线渲染(画在 node 之上,n-graph 编辑器惯例)。</summary>
    internal sealed class GraphEdgeFeature : GraphLayerFeatureBase<GraphEdgeLayer>
    {
        protected override void PublishTrait(FeatureContext ctx, GraphState state)
            => ctx.For(Layer).PublishData(new GraphRenderTrait(state));
    }

    /// <summary>选中高亮 + box-select 矩形渲染。</summary>
    internal sealed class GraphSelectionFeature : GraphLayerFeatureBase<GraphSelectionLayer>
    {
        protected override void PublishTrait(FeatureContext ctx, GraphState state)
            => ctx.For(Layer).PublishData(new GraphRenderTrait(state));
    }

    /// <summary>橡皮筋连线 + 对齐辅助线 + hover tooltip 渲染。</summary>
    internal sealed class GraphPreviewFeature : GraphLayerFeatureBase<GraphPreviewLayer>
    {
        protected override void PublishTrait(FeatureContext ctx, GraphState state)
            => ctx.For(Layer).PublishData(new GraphRenderTrait(state));
    }

    /// <summary>
    /// Minimap 浮窗渲染。复用通用 <see cref="MinimapLayer"/>;本 feature 负责把 GraphState 翻译成
    /// <see cref="MinimapTrait"/>(节点 AABB → contentBounds,nodes → contentItems,Transform 反算
    /// → viewportInContent)。窗口几何常量与 GraphInteractionFeature 的 minimap 命中逻辑共享同一份默认值。
    /// </summary>
    internal sealed class GraphMinimapFeature : Feature
    {
        // 跟 GraphMinimapInteractionFeature 共享同一份 Geometry —— GraphEditorSchema 装配时把
        // 同一个实例 init 注入两边,改尺寸只动一处,渲染 + 命中两端表现一致。
        public MinimapGeometry Geometry { get; init; } = MinimapGeometry.Default;

        /// <summary>由装配方(GraphEditorSchema)注入的状态端口。</summary>
        public DataPort<GraphState> StatePort { get; init; } = null!;

        private readonly MinimapLayer _layer = new();

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            AttachLayer(_layer);
            // chart 完成首次 layout 后 ChartCell 会 Publish ViewportSizeTrait + 拨 _environmentClock,
            // 触发 isFullPass 让首帧也能补上 minimap(那时 ActualWidth/Height 才落定)。
        }

        protected override void OnProject(FeatureContext ctx)
        {
            var (state, stateChanged) = ctx.UsePort(StatePort);
            if (!stateChanged) return;

            // 没节点不推 trait,layer 的 OnUpdate 拿不到就跳过绘制。
            if (state.Nodes.Count == 0) return;
            float winW = (float)Chart.ActualWidth;
            float winH = (float)Chart.ActualHeight;
            if (winW <= 0 || winH <= 0) return; // 还没 layout

            var (bbX, bbY, bbW, bbH) = GraphGeometry.ComputeNodesAabb(state.Nodes);
            var contentBounds = new HevoRect(bbX, bbY, bbW, bbH);

            // ContentItems —— 每个 node 一个矩形。用 list copy 而非 lazy enumerator,trait 是 IVisualTrait 引用判脏的。
            var items = new List<HevoRect>(state.Nodes.Count);
            foreach (var n in state.Nodes) items.Add(n.GetBounds());

            var tr = state.Transform;
            float vpL = -tr.OffsetX / tr.Scale;
            float vpT = -tr.OffsetY / tr.Scale;
            float vpR = (winW - tr.OffsetX) / tr.Scale;
            float vpB = (winH - tr.OffsetY) / tr.Scale;
            var viewport = new HevoRect(vpL, vpT, vpR - vpL, vpB - vpT);

            ctx.For(_layer).PublishData(new MinimapTrait(
                contentBounds, items, viewport,
                Geometry.Width, Geometry.Height, Geometry.Margin, Geometry.Padding,
                StretchToFit: false /* graph 用等比 fit + 居中,跟旧版视觉一致 */));
        }
    }
}
