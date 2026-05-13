namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// "普通数据流"边的 codec —— 占绝大多数(占 ~80% 边),涵盖:
    /// <list type="bullet">
    ///   <item>Feature.InputPort ← Feature.OutputPort / DS.OutputPort(单端口)</item>
    ///   <item>Feature.ValuePorts[] ← N 个 OutputPort(扇入数组)</item>
    ///   <item>Feature.Inputs.{name} / Feature.SeriesOutputs.{name}(nested dict 子端口)</item>
    ///   <item>DS.{outputPort} → Viewport.{LogicalLength | UserRange}(VP_* well-known 共享)</item>
    /// </list>
    ///
    /// <para>
    /// <b>互斥契约</b>:本 codec 不认领:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>Edge.Kind == Cascade</c> —— 走 <c>CascadeEdgeCodec</c> / <c>bp.Cascades</c></item>
    ///   <item><c>Edge.ToPortId == "Upstreams"</c>(Composite fan-in) —— 走 <c>UpstreamRefCodec</c> / <c>dsm.UpstreamRefs</c></item>
    /// </list>
    /// <para>
    /// 这两条 filter 同时出现在 Encode 和 Decode 两侧,保证 round-trip 后边集合不会被两个 codec
    /// 重复认领或漏 emit。
    /// </para>
    ///
    /// <para>
    /// <b>Viewport share</b>:DS.OutputBindings 中 value 指向 <c>VP_LogicalLength</c> / <c>VP_UserRange</c>
    /// 这类 well-known id 时,目标 OutputPort 的 globalId 早在 prepass 阶段
    /// (<see cref="EdgeEncodeContext.OutputPortGlobalId"/>)就被改成 well-known id 了 ——
    /// 本 codec Encode 时直接读表,不用再 case-by-case;Decode 时按 well-known id 反查
    /// <c>ctx.NodeById</c> 找 Viewport 节点拼边。
    /// </para>
    /// </summary>
    internal sealed class DataEdgeCodec : IEdgeKindCodec
    {
        public string Name => nameof(DataEdgeCodec);

        // ==========================================
        //  Encode
        // ==========================================

        public void Encode(IEdgeEncodeContext ctx)
        {
            EncodeDataSourceOutputBindings(ctx);
            EncodeFeatureBindings(ctx);
        }

        /// <summary>
        /// DS 节点每个 OutputPort 写到 <see cref="DataSourceModel.OutputBindings"/>。
        /// globalId 从 <see cref="IEdgeEncodeContext.OutputPortGlobalId"/> 表查 —— 优先级 prepass 已固化。
        /// <para>
        /// 跳过合成 "Stream" 端口:它不是字段映射,而是 Composite Upstreams fan-in UX 虚拟端口,
        /// 由 <c>UpstreamRefCodec</c> 通过 <c>dsm.UpstreamRefs</c> 表达。
        /// </para>
        /// </summary>
        private static void EncodeDataSourceOutputBindings(IEdgeEncodeContext ctx)
        {
            foreach (var node in ctx.State.Nodes)
            {
                if (node.Kind != NodeKind.DataSource) continue;
                if (!ctx.DataSourceModelByNodeId.TryGetValue(node.Id, out var dsm)) continue;

                foreach (var p in node.OutputPorts)
                {
                    if (string.Equals(p.Id, NodeFactory.StreamPortId, StringComparison.Ordinal)) continue;
                    dsm.OutputBindings[p.Id] = ctx.OutputPortGlobalId.TryGetValue((node.Id, p.Id), out var gid)
                        ? gid
                        : $"{node.Id}_{p.Id}";   // 兜底:理论上 prepass 已注册,这里只是防御
                }
            }
        }

        /// <summary>
        /// Feature 节点写两类 binding:
        /// <list type="bullet">
        ///   <item><b>OutputPort</b>:每根 OutputPort 都写 globalId(从 prepass 表读)。必须写,即使没被本 cell
        ///         消费 —— 否则 init=null! 的端口在 OnCompose 走到 WriteIfChanged(null,...) 触发 NRE 静默吞,
        ///         整个 cell 黑屏。</item>
        ///   <item><b>InputPort</b>:
        ///         (a) 非数组 + 有 BindingId → verbatim 写出(scope-qualified <c>dashboard:*</c> / <c>cell:*</c>
        ///             round-trip 保留);
        ///         (b) 否则从 state.Edges 入边反推 globalId(<see cref="EdgeKind.Cascade"/> + Upstreams 端口
        ///             由其他 codec 认领,这里 filter 掉)。
        ///         单端口写 string,扇入数组写 <c>List&lt;string&gt;</c>。</item>
        /// </list>
        /// </summary>
        private static void EncodeFeatureBindings(IEdgeEncodeContext ctx)
        {
            foreach (var n in ctx.State.Nodes)
            {
                if (n.Kind != NodeKind.Feature) continue;
                if (!ctx.FeatureModelByNodeId.TryGetValue(n.Id, out var fm)) continue;

                // OutputPort: 全部写到 OutputBindings,globalId 来自 prepass 表
                foreach (var port in n.OutputPorts)
                {
                    fm.OutputBindings[port.Id] = ctx.OutputPortGlobalId.TryGetValue((n.Id, port.Id), out var gid)
                        ? gid
                        : $"{n.Id}_{port.Id}";
                }

                // InputPort: 互斥 filter —— 排除 Cascade / Upstreams 入边(另由 codec 认领)
                var edgesByTargetPort = ctx.State.Edges
                    .Where(e => e.ToNodeId == n.Id
                                && e.Kind != EdgeKind.Cascade
                                && !string.Equals(e.ToPortId, NodeFactory.UpstreamsPortId, StringComparison.Ordinal))
                    .GroupBy(e => e.ToPortId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                foreach (var inputPort in n.InputPorts)
                {
                    // (a) 非数组 + 有显式 BindingId → 直接 verbatim
                    if (!inputPort.IsArray && !string.IsNullOrEmpty(inputPort.BindingId))
                    {
                        fm.InputBindings[inputPort.Id] = inputPort.BindingId!;
                        continue;
                    }

                    // (b) 入边反推
                    if (!edgesByTargetPort.TryGetValue(inputPort.Id, out var incomingEdges)) continue;
                    var ids = incomingEdges.Select(edge =>
                    {
                        var key = (edge.FromNodeId, edge.FromPortId);
                        return ctx.OutputPortGlobalId.TryGetValue(key, out var gid)
                            ? gid
                            : $"{edge.FromNodeId}_{edge.FromPortId}";
                    }).Distinct().ToList();

                    // 单端口 string vs 扇入数组 List<string>:PortBindingValue.ExtractSingle/List 反序列化都能识别。
                    fm.InputBindings[inputPort.Id] = inputPort.IsArray
                        ? (object)ids
                        : ids.LastOrDefault() ?? string.Empty;
                }
            }
        }

        // ==========================================
        //  Decode
        // ==========================================

        public void Decode(IEdgeDecodeContext ctx)
        {
            DecodeDataSourceToViewportEdges(ctx);
            DecodeFeatureInputEdges(ctx);
        }

        /// <summary>
        /// DS.OutputBindings 中 value 指向 <c>VP_LogicalLength</c> / <c>VP_UserRange</c> well-known id 的项
        /// → 拉一根 ds.{port} → viewport.{port_input} 边。普通 globalId(指向 Feature)的项由
        /// <see cref="DecodeFeatureInputEdges"/> 通过 <c>fm.InputBindings</c> 入边反推处理。
        /// <para>
        /// 假设 Viewport node 已被 caller(<see cref="GraphDeserializer"/>)在 step 4 孵化 ——
        /// codec 不孵化节点,只在 <see cref="IEdgeDecodeContext.NodeById"/> 找现成的。
        /// </para>
        /// </summary>
        private static void DecodeDataSourceToViewportEdges(IEdgeDecodeContext ctx)
        {
            var viewportNode = ctx.Nodes.FirstOrDefault(n => n.Kind == NodeKind.Viewport);
            if (viewportNode == null) return;

            foreach (var dsm in ctx.Input.DataSources)
            {
                if (!ctx.DataSourceModelByNodeId.Values.Contains(dsm)) continue;
                // 反查 dsm 对应的 node(用 dsm 引用本身找 nodeId)
                var dsNodeId = ctx.DataSourceModelByNodeId
                    .Where(kv => ReferenceEquals(kv.Value, dsm))
                    .Select(kv => kv.Key)
                    .FirstOrDefault();
                if (dsNodeId == null || !ctx.NodeById.TryGetValue(dsNodeId, out var dsNode)) continue;

                foreach (var kv in dsm.OutputBindings)
                {
                    var fieldName = kv.Key;
                    var gid = PortBindingValue.ExtractSingle(kv.Value);
                    if (gid != NodeFactory.ViewportLogicalLengthId && gid != NodeFactory.ViewportUserRangeId)
                        continue;

                    var fromPort = dsNode.OutputPorts.FirstOrDefault(p => p.Id == fieldName);
                    if (fromPort == null) continue;

                    var vpInputId = gid switch
                    {
                        NodeFactory.ViewportLogicalLengthId => "LogicalLength",
                        NodeFactory.ViewportUserRangeId     => "UserRange",
                        _                                   => null,
                    };
                    if (vpInputId == null) continue;

                    var toPort = viewportNode.InputPorts.FirstOrDefault(p => p.Id == vpInputId);
                    if (toPort == null) continue;

                    ctx.EmitEdge(new Edge(
                        Id: Guid.NewGuid().ToString("N").Substring(0, 8),
                        FromNodeId: dsNode.Id, FromPortId: fromPort.Id,
                        ToNodeId:   viewportNode.Id, ToPortId: toPort.Id));
                }
            }
        }

        /// <summary>
        /// 每个 Feature 的 InputPort 从 <c>fm.InputBindings</c> 反查上游 OutputPort 拉边。
        /// <para>
        /// 互斥 filter:跳过 <c>ToPortId == "Upstreams"</c> 的 binding —— Composite fan-in 由
        /// <c>UpstreamRefCodec</c> 通过 <c>dsm.UpstreamRefs</c> 反向拉边。
        /// </para>
        /// <para>
        /// <see cref="ChartBlueprint.Cascades"/> 走独立字段,不在 fm.InputBindings 里出现,无需特殊 filter。
        /// </para>
        /// </summary>
        private static void DecodeFeatureInputEdges(IEdgeDecodeContext ctx)
        {
            foreach (var fm in ctx.Input.Features)
            {
                // 反查 fm 对应 nodeId(zip 顺序在主体已确定)
                var nodeId = ctx.FeatureModelByNodeId
                    .Where(kv => ReferenceEquals(kv.Value, fm))
                    .Select(kv => kv.Key)
                    .FirstOrDefault();
                if (nodeId == null || !ctx.NodeById.TryGetValue(nodeId, out var node)) continue;

                var inputsById = node.InputPorts.ToDictionary(p => p.Id);
                foreach (var kv in fm.InputBindings)
                {
                    if (!inputsById.TryGetValue(kv.Key, out var inputPort)) continue; // 端口不存在时跳(容错坏数据)

                    // 互斥契约:Upstreams 端口的 binding 不该出现在 fm.InputBindings(Composite 是 DS 不是 Feature),
                    // 此处不需要 filter Upstreams —— 但保留显式判断,以防未来 Feature 上意外出现 Upstreams 形态端口。
                    if (string.Equals(inputPort.Id, NodeFactory.UpstreamsPortId, StringComparison.Ordinal)) continue;

                    var ids = inputPort.IsArray
                        ? PortBindingValue.ExtractList(kv.Value)
                        : (IReadOnlyList<string>)new[] { PortBindingValue.ExtractSingle(kv.Value) };

                    foreach (var id in ids)
                    {
                        if (string.IsNullOrEmpty(id)) continue;
                        if (!ctx.GlobalIdToOutput.TryGetValue(id, out var src))
                        {
                            Console.WriteLine($"[Graph 反序列化警告] Feature {fm.TypeName}.{kv.Key} 引用 globalId '{id}' 未在任何 OutputPort 派出,边丢弃。");
                            continue;
                        }
                        ctx.EmitEdge(new Edge(
                            Id: Guid.NewGuid().ToString("N").Substring(0, 8),
                            FromNodeId: src.nodeId, FromPortId: src.portId,
                            ToNodeId:   node.Id,    ToPortId:   inputPort.Id));
                    }
                }
            }
        }
    }
}
