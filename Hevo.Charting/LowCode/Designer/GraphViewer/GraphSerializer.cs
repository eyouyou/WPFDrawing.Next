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

            // 1. DataSource (取第一个;允许没有)
            var dsNode = state.Nodes.FirstOrDefault(n => n.Kind == NodeKind.DataSource);
            if (dsNode != null)
            {
                var dsm = new DataSourceModel { TypeName = dsNode.TypeName };
                foreach (var kv in dsNode.Properties)
                {
                    // ScalarMappings / VectorMappings 走单独字段;其余 init/setter 属性都进 Properties。
                    if (kv.Key == "ScalarMappings" && kv.Value is IDictionary<string, string> scalarDict)
                    {
                        foreach (var s in scalarDict) dsm.ScalarMappings[s.Key] = s.Value;
                    }
                    else if (kv.Key == "VectorMappings" && kv.Value is IDictionary<string, string> vectorDict)
                    {
                        foreach (var v in vectorDict) dsm.VectorMappings[v.Key] = v.Value;
                    }
                    else
                    {
                        dsm.Properties[kv.Key] = kv.Value;
                    }
                }
                bp.DataSource = dsm;
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

            // 3. Features + PortBindings
            // 3a. 提前算 outputPort -> globalPortId 表(同一 output 多次连接共享同一 id)
            //
            // 💥 Viewport 特殊路径:edge 末端是 Viewport 节点的端口 → globalId 强制使用
            //    schema 内部 well-known id (VP_LogicalLength / VP_UserRange / VP_ActiveRange)。
            //    DynamicChartSchema 启动时把这些 id 预登记到 _portRegistry,指向 schema
            //    顶层 ViewportPorts 自己持有的 DataPort 实例,数据流就直接打通了。
            var outputPortGlobalId = new Dictionary<(string nodeId, string portId), string>();
            var nodeById = state.Nodes.ToDictionary(n => n.Id);
            foreach (var e in state.Edges)
            {
                var key = (e.FromNodeId, e.FromPortId);
                string globalId;
                if (nodeById.TryGetValue(e.ToNodeId, out var toNode) && toNode.Kind == NodeKind.Viewport)
                {
                    globalId = NodeFactory.ViewportWellKnownId(e.ToPortId)
                               ?? $"{e.FromNodeId}_{e.FromPortId}";
                }
                else
                {
                    globalId = $"{e.FromNodeId}_{e.FromPortId}";
                }
                outputPortGlobalId[key] = globalId; // viewport 覆盖优先
            }

            // Viewport 自身的 OUTPUT 端口(Axis X 等可能从 Viewport.ActiveRange 读)也收编 well-known id。
            foreach (var n in state.Nodes.Where(n => n.Kind == NodeKind.Viewport))
            {
                foreach (var op in n.OutputPorts)
                {
                    var key = (n.Id, op.Id);
                    var wk = NodeFactory.ViewportWellKnownId(op.Id);
                    if (wk != null) outputPortGlobalId[key] = wk;
                }
            }

            // 数据源的输出端口收编 (蓝图里 ScalarMappings/VectorMappings 用)
            if (dsNode != null)
            {
                foreach (var p in dsNode.OutputPorts)
                {
                    var key = (dsNode.Id, p.Id);
                    if (!outputPortGlobalId.ContainsKey(key))
                        outputPortGlobalId[key] = $"{dsNode.Id}_{p.Id}";
                }

                // 💥 把已经算好的 viewport-aware globalId 反写回 DataSource Mappings,
                //    这样 DynamicChartSchema 的 ScalarIngestor/ScatterIngestor 就直接写到
                //    schema 顶层 Viewport 端口,不再绕中间桥。
                if (bp.DataSource != null)
                {
                    foreach (var p in dsNode.OutputPorts)
                    {
                        if (!outputPortGlobalId.TryGetValue((dsNode.Id, p.Id), out var gid)) continue;
                        if (bp.DataSource.ScalarMappings.ContainsKey(p.Id))
                            bp.DataSource.ScalarMappings[p.Id] = gid;
                        else if (bp.DataSource.VectorMappings.ContainsKey(p.Id))
                            bp.DataSource.VectorMappings[p.Id] = gid;
                    }
                }
            }

            foreach (var n in state.Nodes.Where(n => n.Kind == NodeKind.Feature))
            {
                var fm = new FeatureModel { TypeName = n.TypeName };
                foreach (var kv in n.Properties) fm.Properties[kv.Key] = kv.Value;

                // 💥 关键 fix:Feature 自己的 OUTPUT 端口也要写 PortBindings 字段。
                //
                // 典型坑:UniversalAutoScaleFeature.YRangePort 的 init 默认值是 null!,
                //   蓝图侧不焊接它的话,Watch 回调跑到 board.WriteIfChanged(null, ...) 直接 NRE,
                //   被 WorkflowEngine 静默吞,Axis 永远读不到有效 range,axis/grid/line 全黑。
                //
                // 同样的全局 portId 同时被本 feature 的 output 写、被下游的 input 读 —— 共享同一根
                // DataPort<T> 实例(GetOrCreatePort 走 portId 缓存)→ 端口管线打通。
                foreach (var port in n.OutputPorts)
                {
                    var key = (n.Id, port.Id);
                    if (!outputPortGlobalId.TryGetValue(key, out var gid))
                    {
                        gid = $"{n.Id}_{port.Id}";
                        outputPortGlobalId[key] = gid;
                    }
                    fm.PortBindings[port.Id] = gid;
                }

                // 按目标 input 端口聚合 edges:扇入(IsArray) 输出 CSV;普通输出单一 portId。
                var inputsById = n.InputPorts.ToDictionary(p => p.Id);
                var byTargetPort = state.Edges
                    .Where(e => e.ToNodeId == n.Id)
                    .GroupBy(e => e.ToPortId);

                foreach (var grp in byTargetPort)
                {
                    var ids = grp.Select(edge =>
                    {
                        var key = (edge.FromNodeId, edge.FromPortId);
                        return outputPortGlobalId.TryGetValue(key, out var gid)
                            ? gid
                            : $"{edge.FromNodeId}_{edge.FromPortId}";
                    }).Distinct().ToList();

                    bool isArrayPort = inputsById.TryGetValue(grp.Key, out var port) && port.IsArray;
                    fm.PortBindings[grp.Key] = isArrayPort
                        ? string.Join(",", ids)
                        : ids.LastOrDefault() ?? string.Empty;
                }

                bp.Features.Add(fm);
            }

            return bp;
        }
    }
}
