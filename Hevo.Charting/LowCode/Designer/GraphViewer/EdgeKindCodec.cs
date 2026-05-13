using Hevo.Charting.LowCode.Designer;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    // ============================================================================
    //  Edge Codec Protocol —— GraphState ↔ ChartBlueprint 边的 encode/decode 分发
    // ============================================================================
    //
    // 背景:GraphState.Edges 是统一表示,但 ChartBlueprint 里"边的语义"散落在三处字段:
    //   ① 普通 PortBindings 数据流(Kind=DataPort/Stream,占绝大多数)
    //   ② DS-to-DS 上下文驱动                → ChartBlueprint.Cascades[]
    //   ③ Composite fan-in upstream refs      → DataSourceModel.UpstreamRefs[]
    //
    // 历史:GraphSerializer / GraphDeserializer 主体里把这三种逻辑混着写,加新边语义就要再开
    // 一处 ad-hoc 分支。改为 codec dispatch 后,加新 EdgeKind 只要实现一个 IEdgeKindCodec 即可。
    //
    // 命名:用 Codec(encode+decode pair)而非 Handler —— 后者已被 BlueprintHandlerRegistry /
    // HandlerFeature / PythonHandlerRegistry 占满(都指"运行期委托/函数引用"),再叫 Handler 歧义。
    //
    // 互斥契约:每个 codec 自己 filter 关心的边 / 蓝图字段,集合两两互斥:
    //   - CascadeEdgeCodec     认领 Kind=Cascade 的边 / bp.Cascades
    //   - UpstreamRefCodec     认领 ToPortId=="Upstreams" 的边 / dsm.UpstreamRefs
    //   - DataEdgeCodec        认领其余所有(PortBindings 数据流 + Viewport share)
    // ============================================================================

    /// <summary>
    /// GraphState ↔ ChartBlueprint 一种 EdgeKind / 一类边语义的 encode/decode pair。
    /// <para>主控权在 <see cref="GraphSerializer"/> / <see cref="GraphDeserializer"/>,codec 只做分发逻辑、共享上下文由 caller 注入。</para>
    /// </summary>
    internal interface IEdgeKindCodec
    {
        /// <summary>调试 / 日志显示用,不参与 dispatch 决策。</summary>
        string Name { get; }

        /// <summary>
        /// Encode(序列化):从 <see cref="IEdgeEncodeContext.State"/>.Edges 自己 filter 关心的边,
        /// 把它们落到 <see cref="IEdgeEncodeContext.Output"/> 上(写 PortBindings / Cascades / UpstreamRefs 任一字段)。
        /// </summary>
        void Encode(IEdgeEncodeContext ctx);

        /// <summary>
        /// Decode(反序列化):从 <see cref="IEdgeDecodeContext.Input"/> 读自家关心的字段,
        /// 拉边 <see cref="IEdgeDecodeContext.EmitEdge"/> 出来。
        /// </summary>
        void Decode(IEdgeDecodeContext ctx);
    }

    /// <summary>
    /// 序列化阶段 codec 共享上下文。
    /// <para>
    /// caller(<see cref="GraphSerializer"/>)在 prepass 阶段填好 OutputPortGlobalId 表 + dsModel/featureModel
    /// 索引;codec Encode 时只读这些表 + mutate <see cref="Output"/> 上对应字段。
    /// </para>
    /// </summary>
    internal interface IEdgeEncodeContext
    {
        /// <summary>当前要序列化的 graph state。codec 自己 filter <see cref="GraphState.Edges"/>。</summary>
        GraphState State { get; }

        /// <summary>目标 blueprint(可变)。codec 把对应字段写到这里。</summary>
        ChartBlueprint Output { get; }

        /// <summary>node id → Node 索引,避免 codec 自己 ToDictionary。</summary>
        IReadOnlyDictionary<string, Node> NodeById { get; }

        /// <summary>
        /// 跨节点 OutputPort → globalId 表(prepass 已固化:BindingId → Viewport WK → edge target-driven viewport
        /// → fallback "{nodeId}_{portId}")。codec 反查 edge.From 端的 globalId 时只读此表。
        /// </summary>
        IReadOnlyDictionary<(string nodeId, string portId), string> OutputPortGlobalId { get; }

        /// <summary>nodeId → DataSourceModel 索引(codec 往 DS 字段写时用)。</summary>
        IReadOnlyDictionary<string, DataSourceModel> DataSourceModelByNodeId { get; }

        /// <summary>nodeId → FeatureModel 索引(codec 往 Feature 字段写时用)。</summary>
        IReadOnlyDictionary<string, FeatureModel> FeatureModelByNodeId { get; }
    }

    /// <summary>
    /// 反序列化阶段 codec 共享上下文。
    /// <para>
    /// caller(<see cref="GraphDeserializer"/>)先创建好所有 nodes + 填好 GlobalIdToOutput 反查表;
    /// codec Decode 时从 <see cref="Input"/> 读自家字段、用反查表把 globalId 翻成 (fromNode, fromPort)、
    /// 通过 <see cref="EmitEdge"/> 累积输出。
    /// </para>
    /// </summary>
    internal interface IEdgeDecodeContext
    {
        /// <summary>源 blueprint(只读)。codec 自己读关心的字段(PortBindings / Cascades / UpstreamRefs)。</summary>
        ChartBlueprint Input { get; }

        /// <summary>已建立的所有 nodes(含 Viewport 孵化的 fallback 节点)。</summary>
        IReadOnlyList<Node> Nodes { get; }

        /// <summary>node id → Node 索引。</summary>
        IReadOnlyDictionary<string, Node> NodeById { get; }

        /// <summary>
        /// globalId → (nodeId, outputPortId) 反查表。caller 在创建 node 阶段已填好
        /// (Feature OutputPort BindingId、DS OutputPort、Viewport well-known)。
        /// codec 拉 edge 时用此表把 PortBindings 里的 globalId 翻成源端坐标。
        /// </summary>
        IReadOnlyDictionary<string, (string nodeId, string portId)> GlobalIdToOutput { get; }

        /// <summary>
        /// Feature 反序列化时 fm 和 node 是按列表顺序对齐 zip 的,codec 处理 PortBindings 时需要
        /// "本 node 对应的 FeatureModel"。
        /// </summary>
        IReadOnlyDictionary<string, FeatureModel> FeatureModelByNodeId { get; }

        /// <summary>同上,DS 侧。</summary>
        IReadOnlyDictionary<string, DataSourceModel> DataSourceModelByNodeId { get; }

        /// <summary>累积输出边的入口。codec 调用 N 次,caller 收齐后构成 GraphState.Edges。</summary>
        void EmitEdge(Edge edge);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Default 实现 —— GraphSerializer / GraphDeserializer 拥有的 context,持有所有共享表。
    //  codec 只读,不持有 context 实例(传参注入)。
    // ─────────────────────────────────────────────────────────────────────────────

    internal sealed class EdgeEncodeContext : IEdgeEncodeContext
    {
        public GraphState State { get; }
        public ChartBlueprint Output { get; }
        public IReadOnlyDictionary<string, Node> NodeById { get; }
        public IReadOnlyDictionary<(string nodeId, string portId), string> OutputPortGlobalId { get; }
        public IReadOnlyDictionary<string, DataSourceModel> DataSourceModelByNodeId { get; }
        public IReadOnlyDictionary<string, FeatureModel> FeatureModelByNodeId { get; }

        public EdgeEncodeContext(
            GraphState state,
            ChartBlueprint output,
            IReadOnlyDictionary<string, Node> nodeById,
            IReadOnlyDictionary<(string, string), string> outputPortGlobalId,
            IReadOnlyDictionary<string, DataSourceModel> dataSourceModelByNodeId,
            IReadOnlyDictionary<string, FeatureModel> featureModelByNodeId)
        {
            State = state;
            Output = output;
            NodeById = nodeById;
            OutputPortGlobalId = outputPortGlobalId;
            DataSourceModelByNodeId = dataSourceModelByNodeId;
            FeatureModelByNodeId = featureModelByNodeId;
        }
    }

    internal sealed class EdgeDecodeContext : IEdgeDecodeContext
    {
        private readonly List<Edge> _emittedEdges;

        public ChartBlueprint Input { get; }
        public IReadOnlyList<Node> Nodes { get; }
        public IReadOnlyDictionary<string, Node> NodeById { get; }
        public IReadOnlyDictionary<string, (string nodeId, string portId)> GlobalIdToOutput { get; }
        public IReadOnlyDictionary<string, FeatureModel> FeatureModelByNodeId { get; }
        public IReadOnlyDictionary<string, DataSourceModel> DataSourceModelByNodeId { get; }

        public EdgeDecodeContext(
            ChartBlueprint input,
            IReadOnlyList<Node> nodes,
            IReadOnlyDictionary<string, Node> nodeById,
            IReadOnlyDictionary<string, (string, string)> globalIdToOutput,
            IReadOnlyDictionary<string, FeatureModel> featureModelByNodeId,
            IReadOnlyDictionary<string, DataSourceModel> dataSourceModelByNodeId,
            List<Edge> emittedEdges)
        {
            Input = input;
            Nodes = nodes;
            NodeById = nodeById;
            GlobalIdToOutput = globalIdToOutput;
            FeatureModelByNodeId = featureModelByNodeId;
            DataSourceModelByNodeId = dataSourceModelByNodeId;
            _emittedEdges = emittedEdges;
        }

        public void EmitEdge(Edge edge) => _emittedEdges.Add(edge);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  静态注册表 —— 3 个 codec 写死,runtime 业务不开放扩展。
    //  dispatcher(GraphSerializer / GraphDeserializer)按此顺序遍历调 Encode / Decode。
    //  Encode 顺序 / Decode 顺序都不影响结果(codec 互斥契约保证)。
    // ─────────────────────────────────────────────────────────────────────────────

    internal static class EdgeKindCodecs
    {
        /// <summary>
        /// 注册的 3 个 codec。<see cref="DataEdgeCodec"/> 主流量(普通 PortBindings + Viewport share),
        /// <see cref="CascadeEdgeCodec"/> 走 <c>bp.Cascades</c>,
        /// <see cref="UpstreamRefCodec"/> 走 <c>dsm.UpstreamRefs</c>。互斥契约保证 Encode/Decode 顺序无关。
        /// </summary>
        public static IReadOnlyList<IEdgeKindCodec> All { get; } = new IEdgeKindCodec[]
        {
            new DataEdgeCodec(),
            new CascadeEdgeCodec(),
            new UpstreamRefCodec(),
        };
    }
}
