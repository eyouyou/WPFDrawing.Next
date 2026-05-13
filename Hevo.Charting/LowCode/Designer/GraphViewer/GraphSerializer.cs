namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// GraphState ↔ ChartBlueprint 桥。
    /// 节点 + 连线视图 → JSON 蓝图,反向亦可重建一张可编辑的图。
    /// </summary>
    public static class GraphSerializer
    {
        /// <summary>
        /// 把当前画布状态导出成 ChartBlueprint。
        /// 规则:
        /// 1. <see cref="NodeKind.DataSource"/> 的节点最多 1 个,失败则 DataSource = null。
        /// 2. <see cref="NodeKind.Trait"/> 节点 → InitialTraits[]。
        ///    Properties 字典里若含 "Preset" 键,会拎到独立 Preset 字段;其余作为 trait 字段注入。
        /// 3. <see cref="NodeKind.Feature"/> 节点 → Features[]。
        ///    PortBindings 由【流向该节点 InputPort】的 Edge 列表反推:
        ///       PortBindings[inputPort.Id] = $"{fromNode.Id}_{fromPort.Id}" (全局唯一引脚 ID)
        ///    — Output → Input 共享同一全局引脚,跨 Feature 自动对齐。
        /// </summary>
        public static ChartBlueprint ToBlueprint(GraphState state)
        {
            var bp = new ChartBlueprint();

            // 1. DataSources (Phase 4 节点化协议)。
            //    Properties 是 DS init 配置(非端口映射类的项);PortBindings / UpstreamRefs 由 codec 写。
            var dsNodes = state.Nodes.Where(n => n.Kind == NodeKind.DataSource).ToList();
            var dsModelByNodeId = new Dictionary<string, DataSourceModel>(StringComparer.Ordinal);
            foreach (var node in dsNodes)
            {
                var dsm = new DataSourceModel { Id = node.Id, TypeName = node.TypeName };
                foreach (var kv in node.Properties)
                {
                    // 老 ScalarMappings/VectorMappings 字典字段不再写出 —— PortBindings 是新 canonical 路径。
                    // ⚠️ 历史 bug:这条原本是 `kv.Key == "ScalarMappings" || kv.Value is null && kv.Key == "VectorMappings"`,
                    //    &&/|| 优先级错位 → VectorMappings 只在 value=null 时跳,非 null 时漏到 dsm.Properties。
                    //    InjectProperties 配 fail-fast 后启动直接抛(VectorMappings 不是任何 DS 的真实属性,
                    //    旧版被 swallow 兜底掩盖了)—— 两个 key 应该无条件过滤掉。
                    if (kv.Key == "ScalarMappings" || kv.Key == "VectorMappings") continue;
                    if (kv.Key == "DefaultContext" && kv.Value is string dc) { dsm.DefaultContext = dc; continue; }
                    dsm.Properties[kv.Key] = kv.Value;
                }
                bp.DataSources.Add(dsm);
                dsModelByNodeId[node.Id] = dsm;
            }

            // 2. InitialTraits
            foreach (var n in state.Nodes.Where(n => n.Kind == NodeKind.Trait))
            {
                var sm = new StyleModel { TraitTypeName = n.TypeName };
                foreach (var kv in n.Properties)
                {
                    if (kv.Key == "Preset" && kv.Value is string s) sm.Preset = s;
                    else sm.Properties[kv.Key] = kv.Value;
                }
                bp.InitialTraits.Add(sm);
            }

            // 3. Feature models(Properties + Events 只;PortBindings 由 DataEdgeCodec 写)
            //
            // 先建好所有 fm 并加入 bp.Features,prepass 才能在 step 4 里读取完整的 OutputPort 集合。
            var featureModelByNodeId = new Dictionary<string, FeatureModel>(StringComparer.Ordinal);
            foreach (var n in state.Nodes.Where(n => n.Kind == NodeKind.Feature))
            {
                var fm = new FeatureModel { TypeName = n.TypeName };
                foreach (var kv in n.Properties)
                {
                    // Events 路由表走保留 key __Events__ 在 Properties 字典里搭车(GraphState 的 Node 没有 Events 槽),
                    // 这里反向取回到 fm.Events,顶层 ChartBlueprint JSON 跟手写资产形态一致。
                    if (kv.Key == FeatureModel.EventsPropertyKey)
                    {
                        if (kv.Value is IDictionary<string, string> events && events.Count > 0)
                            fm.Events = new Dictionary<string, string>(events);
                        continue;
                    }
                    fm.Properties[kv.Key] = kv.Value;
                }
                bp.Features.Add(fm);
                featureModelByNodeId[n.Id] = fm;
            }

            // 4. Prepass:跨节点单遍解析 outputPort → globalPortId 表(同一 output 多次连接共享同一 id)
            //
            // 优先级(高 → 低):
            //   ① OutputPort.BindingId  —— 显式 binding(原 JSON 的 globalId 经 ApplyBindingIds 写到 Port 上)
            //   ② Viewport 节点自身的 OutputPort → ViewportWellKnownId(opName)
            //   ③ Edge target-driven Viewport 覆盖:源端 globalId = ViewportWellKnownId(Viewport input port name)
            //   ④ "{nodeId}_{portId}" 自动生成 fallback
            //
            // 全表在 codec dispatch 之前完成解析 —— 保证 codec 写 PortBindings 时跨 feature 顺序无关。
            var outputPortGlobalId = new Dictionary<(string nodeId, string portId), string>();
            var nodeById = state.Nodes.ToDictionary(n => n.Id);

            // ① + ② 跨所有 nodes 解析 OutputPort 的 source-driven globalId
            foreach (var node in state.Nodes)
                foreach (var op in node.OutputPorts)
                    outputPortGlobalId[(node.Id, op.Id)] = ResolveOutputPortGlobalId(node, op);

            // ③ Edge target-driven Viewport 覆盖
            foreach (var e in state.Edges)
            {
                if (e.Kind == EdgeKind.Cascade) continue;
                if (!nodeById.TryGetValue(e.ToNodeId, out var toNode) || toNode.Kind != NodeKind.Viewport) continue;
                var wk = NodeFactory.ViewportWellKnownId(e.ToPortId);
                if (wk == null) continue;

                // 显式 BindingId 已经定了源端 id,不被 viewport 共享规则覆盖。
                if (nodeById.TryGetValue(e.FromNodeId, out var fromNode))
                {
                    var fromPort = fromNode.OutputPorts.FirstOrDefault(p => p.Id == e.FromPortId);
                    if (fromPort != null && !string.IsNullOrEmpty(fromPort.BindingId)) continue;
                }
                outputPortGlobalId[(e.FromNodeId, e.FromPortId)] = wk;
            }

            // 5. Codec dispatch:PortBindings / UpstreamRefs / Cascades 全部交给各 codec 写入 bp。
            var ctx = new EdgeEncodeContext(state, bp, nodeById, outputPortGlobalId, dsModelByNodeId, featureModelByNodeId);
            foreach (var codec in EdgeKindCodecs.All)
                codec.Encode(ctx);

            return bp;
        }

        /// <summary>
        /// 单一来源解析"一个 OutputPort 对外暴露的 globalId"(source-driven,跟 edge target 无关)。
        /// <para>优先级:</para>
        /// <list type="number">
        ///   <item><b>Port.BindingId</b> 显式 binding —— ApplyBindingIds 反序列化时把原 JSON globalId 写到 Port 上,
        ///         scope-qualified id(<c>dashboard:*</c> / <c>cell:*</c>)和用户自定 id(<c>s_BB_Upper</c>)走这里。</item>
        ///   <item><b>Viewport 节点自身的 OutputPort</b> → <see cref="NodeFactory.ViewportWellKnownId"/> ——
        ///         schema 启动时这些 well-known id 已注册到 <c>_portRegistry</c>,跨节点共享。</item>
        ///   <item><b>"{nodeId}_{portId}" 自动生成</b> —— 无 BindingId、无 well-known 的兜底,
        ///         同图唯一,但不跨 round-trip 稳定(nodeId 可能变)。</item>
        /// </list>
        /// <para>
        /// 注意:edge target-driven viewport 覆盖(edge X.out → Viewport.in,源端 globalId = VP_X_well-known)
        /// 不在本函数里 —— 它要看 edge,在 outputPortGlobalId 表初始化的第二阶段单独处理。
        /// </para>
        /// </summary>
        private static string ResolveOutputPortGlobalId(Node node, Port port)
        {
            if (!string.IsNullOrEmpty(port.BindingId)) return port.BindingId!;

            if (node.Kind == NodeKind.Viewport)
            {
                var wk = NodeFactory.ViewportWellKnownId(port.Id);
                if (wk != null) return wk;
            }

            return $"{node.Id}_{port.Id}";
        }
    }
}
