using Hevo.Charting.Features;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// <see cref="GraphSerializer"/> 的反向桥:把已有的 <see cref="ChartBlueprint"/> JSON 还原成
    /// 可在 GraphViewer 编辑器里继续拖动/改连线的 <see cref="GraphState"/>。
    /// <para>
    /// 用途:业务侧把生产蓝图 JSON 灌进编辑器二次调整(等价于把 ChartBlueprint 当成持久层,
    /// 编辑器当成 IDE)。低代码.md §7.12 待补清单的"反序列化已有 ChartBlueprint JSON → 还原成 GraphState"。
    /// </para>
    /// <para>
    /// <b>规则</b>:
    /// <list type="number">
    ///   <item><b>节点</b>:每个 DataSource / Feature / Trait 走 <see cref="NodeFactory.CreateNode"/> 派生(端口元数据按 .NET 反射重建);
    ///         位置由内部 <see cref="AutoLayout"/> 按 <see cref="FeatureCategory"/> 分泳道、按拓扑深度分列自动派。</item>
    ///   <item><b>Viewport</b>:扫描所有 <c>PortBindings</c> + <c>DataSource.ScalarMappings</c>,
    ///         发现指向 <c>VP_LogicalLength</c> / <c>VP_UserRange</c> / <c>VP_ActiveRange</c> 这几个 well-known id 时,
    ///         自动孵化一个 <see cref="NodeKind.Viewport"/> 节点并把对应连线引过去 —— 跟 GraphSerializer 的 Viewport 路径对称。</item>
    ///   <item><b>Edges</b>:每个全局 portId 反查"是哪个节点的哪个 OutputPort 写出来的",对每个引用它的 InputPort 拉一根边。
    ///         数组扇入端口(CSV)拆开后逐个还原。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>遗漏与降级</b>:
    /// <list type="bullet">
    ///   <item>蓝图里出现但 ComponentRegistry 未登记的 TypeName → 忽略整个节点(打印警告,不抛)。
    ///         避免"业务自定义 feature 没注册就把整个画布带崩"。</item>
    ///   <item>PortBindings 引用的 globalId 在所有 OutputPort 反查表里找不到来源 → 该 Edge 直接丢,
    ///         保留节点。这种情况通常发生在蓝图被人手撸过、或者业务自定义 PortGenerator 派的 portId。</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class GraphDeserializer
    {
        /// <summary>
        /// 把 <see cref="ChartBlueprint"/> 还原成可编辑的 <see cref="GraphState"/>。
        /// 失败原因(类型未注册、端口反查不到等)走 <see cref="Console.WriteLine"/> 警告,不抛。
        /// </summary>
        public static GraphState FromBlueprint(ChartBlueprint blueprint)
        {
            if (blueprint == null) throw new ArgumentNullException(nameof(blueprint));

            var nodes = new List<Node>();
            // (nodeId, outputPortId) 表:同一全局 portId → 哪个节点的哪个 output 端口写出。
            // 反向构建 Edge 时需要这张表把 globalId 翻译回 (FromNodeId, FromPortId)。
            var globalIdToOutput = new Dictionary<string, (string nodeId, string portId)>();

            // 1. DataSource 节点 + 它的标量/向量映射(顺便填 globalIdToOutput)
            Node? dsNode = null;
            if (blueprint.DataSource != null && !string.IsNullOrEmpty(blueprint.DataSource.TypeName))
            {
                dsNode = TryCreateDataSourceNode(blueprint.DataSource, globalIdToOutput);
                if (dsNode != null) nodes.Add(dsNode);
            }

            // 2. Trait 节点(InitialTraits)
            foreach (var sm in blueprint.InitialTraits)
            {
                var n = TryCreateTraitNode(sm);
                if (n != null) nodes.Add(n);
            }

            // 3. Feature 节点 —— 同时把 OutputPort 的 globalId(来自 fm.PortBindings)反查表也填上,
            //    供后面拼 Edge 用。
            var featureNodes = new List<Node>();
            foreach (var fm in blueprint.Features)
            {
                var n = TryCreateFeatureNode(fm, globalIdToOutput);
                if (n != null)
                {
                    nodes.Add(n);
                    featureNodes.Add(n);
                }
            }

            // 4. Viewport 节点 —— 蓝图里只要任何一处引用了 VP_* well-known id,就孵化一个 Viewport 节点。
            //    它的 OutputPort 的 globalId 写进反查表,这样从 Viewport 出去的连线也能正确拼出来。
            Node? viewportNode = null;
            if (BlueprintReferencesViewport(blueprint))
            {
                viewportNode = NodeFactory.CreateViewportNode(new HevoPoint(0, 0));
                nodes.Add(viewportNode);
                foreach (var op in viewportNode.OutputPorts)
                {
                    var wk = NodeFactory.ViewportWellKnownId(op.Id);
                    if (wk != null) globalIdToOutput[wk] = (viewportNode.Id, op.Id);
                }
            }

            // 5. 拼 Edges
            //    a. DataSource.ScalarMappings/VectorMappings 中指向 VP_* 的 → 拉一根 ds.{port} → viewport.{port_input}
            //       (普通映射没对应 sink,不画边)
            //    b. Feature.PortBindings 里凡是 InputPort 的 globalId 在反查表里找得到来源 → 拉边
            var edges = new List<Edge>();
            if (dsNode != null && blueprint.DataSource != null && viewportNode != null)
            {
                BuildDataSourceToViewportEdges(blueprint.DataSource, dsNode, viewportNode, edges);
            }
            foreach (var (n, fm) in featureNodes.Zip(blueprint.Features, (n, fm) => (n, fm)))
            {
                BuildFeatureInputEdges(n, fm, globalIdToOutput, edges);
            }

            // 6. 自动布局:DataSource 在最左,然后按 FeatureCategory 分列(Environment / Axes / Series / Interaction)。
            //    同列按拓扑深度上下展开,Viewport 节点放 Environment 列尾(给 ds.LogicalLength 一个落脚点)。
            AutoLayout.Apply(nodes, edges);

            return new GraphState(
                Nodes: nodes,
                Edges: edges,
                SelectedNodeIds: new HashSet<string>(),
                Transform: CanvasTransform.Identity,
                RubberBand: null,
                BoxSelection: null);
        }

        // ==========================================
        //  节点构造
        // ==========================================

        private static Node? TryCreateDataSourceNode(DataSourceModel dsm, Dictionary<string, (string, string)> globalIdToOutput)
        {
            Type dsType;
            try { dsType = ComponentRegistry.Resolve(dsm.TypeName); }
            catch { Console.WriteLine($"[Graph 反序列化警告] DataSource '{dsm.TypeName}' 未登记,忽略整个数据源节点。"); return null; }

            // NodeFactory 会反射出 ScalarMappings/VectorMappings 默认值,这里覆盖回蓝图里的具体 globalId。
            var node = NodeFactory.CreateNode(dsType, new HevoPoint(0, 0));
            if (dsm.ScalarMappings.Count > 0)
                node.Properties["ScalarMappings"] = new Dictionary<string, string>(dsm.ScalarMappings);
            if (dsm.VectorMappings.Count > 0)
                node.Properties["VectorMappings"] = new Dictionary<string, string>(dsm.VectorMappings);
            // DataSource 自己的 init 属性(MinuteCeiling 等)
            foreach (var kv in dsm.Properties)
            {
                node.Properties[kv.Key] = kv.Value;
            }

            // OutputPort → globalId 反查表:DataSource 用 ScalarMappings/VectorMappings 里 fieldName→globalId 映射。
            foreach (var p in node.OutputPorts)
            {
                if (dsm.ScalarMappings.TryGetValue(p.Id, out var gid) || dsm.VectorMappings.TryGetValue(p.Id, out gid))
                    globalIdToOutput[gid] = (node.Id, p.Id);
            }
            return node;
        }

        private static Node? TryCreateTraitNode(StyleModel sm)
        {
            Type traitType;
            try { traitType = ComponentRegistry.Resolve(sm.TraitTypeName); }
            catch { Console.WriteLine($"[Graph 反序列化警告] Trait '{sm.TraitTypeName}' 未登记,忽略此 trait 节点。"); return null; }

            var node = NodeFactory.CreateNode(traitType, new HevoPoint(0, 0));
            // Preset 字符串以及其他 Property 全都按 key 写回(NodeEditorWindow 会把"Preset"特殊处理,
            // 其余键正常通过 BuildOneField 反射展开)
            if (!string.IsNullOrEmpty(sm.Preset)) node.Properties["Preset"] = sm.Preset!;
            foreach (var kv in sm.Properties) node.Properties[kv.Key] = kv.Value;
            return node;
        }

        private static Node? TryCreateFeatureNode(FeatureModel fm, Dictionary<string, (string, string)> globalIdToOutput)
        {
            Type featureType;
            try { featureType = ComponentRegistry.Resolve(fm.TypeName); }
            catch { Console.WriteLine($"[Graph 反序列化警告] Feature '{fm.TypeName}' 未登记,忽略此 feature 节点。"); return null; }

            var node = NodeFactory.CreateNode(featureType, new HevoPoint(0, 0));

            // Properties: PortBindings 里 key 是端口名,普通 init 属性也在 fm.Properties 里。
            // PortBindings 是边重建的依据,不写回 node.Properties(避免编辑器把它当普通字段编辑)。
            foreach (var kv in fm.Properties)
            {
                node.Properties[kv.Key] = kv.Value;
            }

            // OutputPort → globalId 反查表:Feature 的输出端口在 fm.PortBindings 里也有条目
            //(GraphSerializer 把每个 OutputPort 都写了 binding,所以这里能直接读)。
            foreach (var op in node.OutputPorts)
            {
                if (fm.PortBindings.TryGetValue(op.Id, out var raw))
                {
                    var gid = PortBindingValue.ExtractSingle(raw);
                    if (!string.IsNullOrEmpty(gid)) globalIdToOutput[gid] = (node.Id, op.Id);
                }
            }
            return node;
        }

        // ==========================================
        //  边重建
        // ==========================================

        private static void BuildDataSourceToViewportEdges(
            DataSourceModel dsm, Node dsNode, Node viewportNode, List<Edge> edges)
        {
            // 仅 ScalarMappings → Viewport (LogicalLength / UserRange);VectorMappings 不接 Viewport。
            foreach (var kv in dsm.ScalarMappings)
            {
                var fieldName = kv.Key;
                var gid = kv.Value;
                if (gid != NodeFactory.ViewportLogicalLengthId && gid != NodeFactory.ViewportUserRangeId)
                    continue;
                var fromPort = dsNode.OutputPorts.FirstOrDefault(p => p.Id == fieldName);
                if (fromPort == null) continue;
                // 找 viewport 上的对应输入端口
                var vpInputId = gid switch
                {
                    NodeFactory.ViewportLogicalLengthId => "LogicalLength",
                    NodeFactory.ViewportUserRangeId => "UserRange",
                    _ => null,
                };
                if (vpInputId == null) continue;
                var toPort = viewportNode.InputPorts.FirstOrDefault(p => p.Id == vpInputId);
                if (toPort == null) continue;
                edges.Add(new Edge(
                    Id: Guid.NewGuid().ToString("N").Substring(0, 8),
                    FromNodeId: dsNode.Id, FromPortId: fromPort.Id,
                    ToNodeId: viewportNode.Id, ToPortId: toPort.Id));
            }
        }

        private static void BuildFeatureInputEdges(
            Node node, FeatureModel fm,
            Dictionary<string, (string nodeId, string portId)> globalIdToOutput,
            List<Edge> edges)
        {
            var inputsById = node.InputPorts.ToDictionary(p => p.Id);
            foreach (var kv in fm.PortBindings)
            {
                if (!inputsById.TryGetValue(kv.Key, out var inputPort)) continue; // OUTPUT binding,跳过
                // PortBindingValue 统一识别 string / list / JSON array / CSV(<v1 兼容)。
                var ids = inputPort.IsArray
                    ? PortBindingValue.ExtractList(kv.Value)
                    : (IReadOnlyList<string>)new[] { PortBindingValue.ExtractSingle(kv.Value) };
                foreach (var id in ids)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    if (!globalIdToOutput.TryGetValue(id, out var src))
                    {
                        Console.WriteLine($"[Graph 反序列化警告] Feature {fm.TypeName}.{kv.Key} 引用 globalId '{id}' 未在任何 OutputPort 派出,边丢弃。");
                        continue;
                    }
                    edges.Add(new Edge(
                        Id: Guid.NewGuid().ToString("N").Substring(0, 8),
                        FromNodeId: src.nodeId, FromPortId: src.portId,
                        ToNodeId: node.Id, ToPortId: inputPort.Id));
                }
            }
        }

        // ==========================================
        //  Viewport 命中
        // ==========================================

        private static bool BlueprintReferencesViewport(ChartBlueprint blueprint)
        {
            if (blueprint.DataSource != null)
            {
                foreach (var kv in blueprint.DataSource.ScalarMappings)
                    if (IsViewportId(kv.Value)) return true;
            }
            foreach (var fm in blueprint.Features)
            {
                foreach (var kv in fm.PortBindings)
                {
                    // 单端口 / 扇入数组 / 老 CSV 一并枚举,任何一根碰到 well-known id 就 return。
                    foreach (var id in PortBindingValue.ExtractList(kv.Value))
                        if (IsViewportId(id)) return true;
                }
            }
            return false;
        }

        private static bool IsViewportId(string id) =>
            id == NodeFactory.ViewportLogicalLengthId
         || id == NodeFactory.ViewportUserRangeId
         || id == NodeFactory.ViewportActiveRangeId;
    }

    /// <summary>
    /// 简易布局算法 —— 按 <see cref="FeatureCategory"/> 横向分泳道,每个泳道独占自己的 Y 行,
    /// 内部按入度排序后水平铺开。
    /// <para>
    /// 设计目标:跟 <see cref="GraphCanvasLayer"/> 渲染的"类别横幅"视觉模型对齐 —— 横幅按节点 Y 范围
    /// 聚合,所以"每 category 一个 Y 行"才不会让带子互相重叠。曾经版本是按 category 分列(每列从 y=60
    /// 起头),结果所有泳道竖向重合,UX 完全乱套,踩坑后改成本设计。
    /// </para>
    /// <para>公开给 Window4 的"自动布局"按钮复用 —— 一键把杂乱画布重排成清晰的 6 行泳道。</para>
    /// </summary>
    public static class AutoLayout
    {
        // 列水平参数(每泳道内部从左到右排开)
        private const float ColW = 260f;
        private const float ColX0 = 40f;
        // 泳道起点 + 泳道间垂直间距(给 ComputeCategoryBands 的 pad=14 留视觉缓冲)
        private const float LaneY0 = 60f;
        private const float LaneGap = 60f;

        /// <summary>
        /// 就地修改 <paramref name="nodes"/> 列表里的节点位置(Node 是 record,with 出新引用回写),
        /// 不改 <paramref name="edges"/>。
        /// <para>
        /// 算法:横向泳道(Y 行 = Category)+ 拓扑深度强制 forward(X 列 ≥ DAG 深度)。
        /// 每个节点先算 DAG 深度(从所有"入度 0 源"出发的最长路径),然后在自己泳道内分配 col,
        /// 满足两条硬约束:(1) col ≥ depth(确保所有边 X 方向向右,无反向边);
        /// (2) 同泳道内 col 互不相同(避免同 Y 重叠)。无连接节点(decor/grid/vpManager 这种纯
        /// 配置节点)推到泳道末尾,不挤占主流数据通道。
        /// </para>
        /// <para>
        /// 跟旧版 barycentric 比:旧版尝试用邻居重心拉拢节点,但孤立节点占住 col 0-N 把后续 depth-N 节点
        /// 顶到右边,跨泳道边反而出现 backward S 钩。新版用 depth 显式分层,任何 (a→b) 边天然 a.col < b.col。
        /// </para>
        /// </summary>
        public static void Apply(List<Node> nodes, List<Edge> edges)
        {
            // 1. 泳道分桶
            var byLane = new Dictionary<FeatureCategory, List<Node>>();
            foreach (var n in nodes)
            {
                var lane = LaneFor(n);
                if (!byLane.TryGetValue(lane, out var list)) { list = new List<Node>(); byLane[lane] = list; }
                list.Add(n);
            }

            // 2. 计算每个节点的 DAG 深度(longest-path-from-any-source)。
            //    入度 0 节点 depth = 0;其它 = max(predecessor.depth) + 1。
            //    用拓扑序保证每个节点处理时其前驱已完成。带环图回退到 BFS 单调放松,
            //    画布上一般都是 DAG,环来自业务自定义闭环 feature,不在本次低代码 demo 范围。
            var depth = ComputeDepth(nodes, edges);

            // 3. 邻接表(只统计"是否有连接")用来识别孤立节点,推到泳道末尾。
            var hasEdge = new HashSet<string>();
            foreach (var e in edges) { hasEdge.Add(e.FromNodeId); hasEdge.Add(e.ToNodeId); }

            // 4. 每条泳道内排序:连接节点优先 (按 depth 升序 → alphabetic);孤立节点(配置类)排在后面。
            //    这一步是 forward 性的关键 —— 连接节点先抢占低 col,孤立节点不挤占数据通道。
            foreach (var list in byLane.Values)
            {
                list.Sort((a, b) =>
                {
                    bool ca = hasEdge.Contains(a.Id);
                    bool cb = hasEdge.Contains(b.Id);
                    if (ca != cb) return ca ? -1 : 1;            // 连接者在前
                    int da = depth.GetValueOrDefault(a.Id, 0);
                    int db = depth.GetValueOrDefault(b.Id, 0);
                    if (da != db) return da.CompareTo(db);
                    return string.Compare(a.TypeName, b.TypeName, StringComparison.Ordinal);
                });
            }

            // 5. 派 (X, Y):每泳道独占 Y 行;X 用贪心 max(node.depth, lastColInLane + 1)。
            //    单节点泳道(DataSource / Axes / Series 通常 1 个节点)的 col 直接 = depth,
            //    跨泳道的边自然成 forward(右下)。
            var laneOrder = new[]
            {
                FeatureCategory.DataSource,
                FeatureCategory.Environment,
                FeatureCategory.Axes,
                FeatureCategory.Series,
                FeatureCategory.Interaction,
                FeatureCategory.Trait,
                FeatureCategory.Unknown,
            };

            float yCursor = LaneY0;
            foreach (var cat in laneOrder)
            {
                if (!byLane.TryGetValue(cat, out var list) || list.Count == 0) continue;
                int lastCol = -1;
                float maxH = 0f;
                for (int i = 0; i < list.Count; i++)
                {
                    var n = list[i];
                    int nodeDepth = depth.GetValueOrDefault(n.Id, 0);
                    int col = Math.Max(nodeDepth, lastCol + 1);
                    int idx = nodes.IndexOf(n);
                    if (idx < 0) continue;
                    nodes[idx] = n with { Position = new HevoPoint(ColX0 + col * ColW, yCursor) };
                    lastCol = col;
                    if (n.Size.Y > maxH) maxH = n.Size.Y;
                }
                yCursor += maxH + LaneGap;
            }
        }

        /// <summary>
        /// 计算每个节点的 DAG 深度(longest path from any indeg-0 source)。
        /// 拓扑顺序遍历 + 单调松弛;带环图最坏情况下深度被截到当前已松弛步数,不会无限循环。
        /// </summary>
        private static Dictionary<string, int> ComputeDepth(List<Node> nodes, List<Edge> edges)
        {
            var depth = new Dictionary<string, int>();
            var indeg = new Dictionary<string, int>();
            var outAdj = new Dictionary<string, List<string>>();
            foreach (var n in nodes) { depth[n.Id] = 0; indeg[n.Id] = 0; outAdj[n.Id] = new List<string>(); }
            foreach (var e in edges)
            {
                if (!indeg.ContainsKey(e.ToNodeId) || !outAdj.ContainsKey(e.FromNodeId)) continue;
                indeg[e.ToNodeId]++;
                outAdj[e.FromNodeId].Add(e.ToNodeId);
            }

            // Kahn 拓扑排序:入度 0 进队列,弹出时把所有后继的 depth 用 max(currentDepth+1, succ.depth) 更新。
            var queue = new Queue<string>();
            foreach (var kv in indeg) if (kv.Value == 0) queue.Enqueue(kv.Key);
            int processed = 0;
            int safetyLimit = nodes.Count + edges.Count + 10;  // 环图兜底
            while (queue.Count > 0 && processed < safetyLimit)
            {
                processed++;
                var u = queue.Dequeue();
                int du = depth[u];
                foreach (var v in outAdj[u])
                {
                    if (du + 1 > depth[v]) depth[v] = du + 1;
                    if (--indeg[v] == 0) queue.Enqueue(v);
                }
            }
            return depth;
        }

        // 节点 → 实际泳道 Category。NodeKind 优先 (DataSource / Viewport 强制泳道),
        // 其它走 Node.Category。Trait 默认 Trait 泳道,但业务可在 FeatureCategoryRegistry 重定向
        // (例如 ScaleStrategyTrait 在 Bootstrap 里被改派到 Environment 泳道)。
        private static FeatureCategory LaneFor(Node n) => n.Kind switch
        {
            NodeKind.DataSource => FeatureCategory.DataSource,
            NodeKind.Viewport   => FeatureCategory.Environment,
            _ => n.Category == FeatureCategory.Unknown ? FeatureCategory.Environment : n.Category,
        };
    }
}
