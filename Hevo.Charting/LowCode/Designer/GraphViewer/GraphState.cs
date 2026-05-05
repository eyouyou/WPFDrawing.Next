using Hevo.Charting.Abstractions;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// 节点上的端口。蓝图侧映射到 DataPort&lt;T&gt; 字段或全局引脚 ID。
    /// </summary>
    /// <param name="Id">节点内唯一,典型 = 字段名 (例 "DataPort"、"YRangePort")。</param>
    /// <param name="Name">UI 显示名 (端口标签)。</param>
    /// <param name="DataTypeName">承载数据类型的 .NET 短名,用于连线类型校验 (例 "ReadOnlyMemory&lt;float&gt;")。</param>
    /// <param name="IsInput">true=输入(左侧),false=输出(右侧)。</param>
    /// <param name="Description">端口注释,鼠标悬停时浮窗显示。来自 .NET 属性上的 <c>[Description]</c> 标注或 XML 文档摘要。</param>
    /// <param name="IsArray">true = 扇入端口,可同时接受多根连线 (对应 <c>DataPort&lt;T&gt;[]</c> 数组属性,如 UniversalAutoScale.ValuePorts)。
    /// 序列化时所有 incoming edges 合并成 CSV;运行时由 DynamicChartSchema 拆出来塞数组。</param>
    public record Port(string Id, string Name, string DataTypeName, bool IsInput, string? Description = null, bool IsArray = false);

    public enum NodeKind
    {
        DataSource,
        Trait,
        Feature,
        /// <summary>
        /// schema 顶层 ViewportPorts 的可视化节点。本身不实例化 .NET 类型,
        /// 序列化时把它的端口映射到 schema 自己持有的 <c>Viewport.LogicalLength/UserRange/ActiveRange</c>
        /// (按 ViewportPorts.cs 里 DisplayName 的"VP_*"协议作 well-known ID)。
        /// </summary>
        Viewport
    }

    /// <summary>
    /// 画布节点。蓝图侧对应 Feature / DataSource / Trait。
    /// </summary>
    /// <param name="Id">节点 GUID,序列化端用作 PortBindings key 的 NodeId 部分。</param>
    /// <param name="TypeName">ComponentRegistry 里登记的 TypeName(例 "LineSeriesFeature")。</param>
    /// <param name="Title">UI 显示标题(可与 TypeName 不同,允许业务自定义)。</param>
    /// <param name="Kind">用于决定渲染色和序列化目标(Feature/DataSource/Trait)。</param>
    /// <param name="Position">画布坐标系下左上角像素位置(经过 CanvasTransform 变换)。</param>
    /// <param name="Size">节点尺寸(画布坐标系像素)。</param>
    /// <param name="Category">类别 (跟 FeatureCanvasExtensions 4 类 builder 对齐),决定泳道与配色。</param>
    public record Node(
        string Id,
        string TypeName,
        string Title,
        NodeKind Kind,
        HevoPoint Position,
        HevoPoint Size,
        IReadOnlyList<Port> InputPorts,
        IReadOnlyList<Port> OutputPorts,
        Dictionary<string, object?> Properties,
        FeatureCategory Category = FeatureCategory.Unknown
    )
    {
        /// <summary>画布坐标系下的节点 AABB。</summary>
        public HevoRect GetBounds() => new(Position.X, Position.Y, Size.X, Size.Y);

        /// <summary>端口圆点中心(画布坐标)。Input 在左 / Output 在右,垂直均分。</summary>
        public HevoPoint GetPortPosition(Port port)
        {
            var ports = port.IsInput ? InputPorts : OutputPorts;
            int idx = -1;
            for (int i = 0; i < ports.Count; i++)
            {
                if (ports[i].Id == port.Id) { idx = i; break; }
            }
            if (idx < 0) idx = 0;
            int n = ports.Count;
            const float headerH = 24f;
            float bodyTop = Position.Y + headerH;
            float bodyH = Size.Y - headerH;
            float step = bodyH / Math.Max(n, 1);
            float y = bodyTop + step * (idx + 0.5f);
            float x = port.IsInput ? Position.X : Position.X + Size.X;
            return new HevoPoint(x, y);
        }

        public Port? FindPort(string portId, bool isInput)
        {
            var ports = isInput ? InputPorts : OutputPorts;
            foreach (var p in ports) if (p.Id == portId) return p;
            return null;
        }
    }

    /// <summary>
    /// 节点间连线。FromPort 必须是 Output,ToPort 必须是 Input。
    /// 不在此处做类型校验——交给 GraphSchema.TryConnect。
    /// </summary>
    public record Edge(string Id, string FromNodeId, string FromPortId, string ToNodeId, string ToPortId);

    /// <summary>
    /// 画布的全局 pan/zoom 变换。canvasPx → screenPx: screen = canvas * Scale + Offset。
    /// </summary>
    public readonly record struct CanvasTransform(float OffsetX, float OffsetY, float Scale)
    {
        public static readonly CanvasTransform Identity = new(0f, 0f, 1f);

        public HevoPoint CanvasToScreen(HevoPoint canvas)
            => new(canvas.X * Scale + OffsetX, canvas.Y * Scale + OffsetY);

        public HevoPoint ScreenToCanvas(HevoPoint screen)
            => new((screen.X - OffsetX) / Scale, (screen.Y - OffsetY) / Scale);

        public HevoRect CanvasToScreen(HevoRect r)
            => new(r.X * Scale + OffsetX, r.Y * Scale + OffsetY, r.Width * Scale, r.Height * Scale);
    }

    /// <summary>
    /// 拖出连线时的橡皮筋预览 (从 source 端口到鼠标位置)。
    /// </summary>
    public record EdgeRubberBand(string FromNodeId, string FromPortId, HevoPoint MousePosCanvas, bool IsValidTarget);

    /// <summary>
    /// 鼠标悬停的端口标识 + 屏幕坐标(渲染 tooltip 用)。
    /// 屏幕坐标避开 transform 反算的精度损失,直接由交互层下发。
    /// </summary>
    public record HoveredPort(string NodeId, string PortId, bool IsInput, HevoPoint AnchorScreen);

    /// <summary>
    /// 拖动节点时的对齐辅助线快照。<see cref="GraphSchema"/> MoveNodes 路径在每帧 mouse-move 里
    /// 检测被拖节点的边/中线与其它节点是否吸附,命中则填充本结构,<see cref="GraphPreviewLayer"/> 渲染。
    /// 拖拽结束(MouseUp/LostCapture)清成 null。
    /// </summary>
    /// <param name="VerticalLines">画布坐标系下需要画的竖线 X 值列表(贯穿画布上下)。</param>
    /// <param name="HorizontalLines">画布坐标系下需要画的横线 Y 值列表(贯穿画布左右)。</param>
    public record AlignmentGuides(IReadOnlyList<float> VerticalLines, IReadOnlyList<float> HorizontalLines);

    /// <summary>
    /// 框选橡皮筋。SelectionLayer 渲染。
    /// </summary>
    public record BoxSelection(HevoPoint StartCanvas, HevoPoint EndCanvas)
    {
        public HevoRect ToRect()
        {
            float x = Math.Min(StartCanvas.X, EndCanvas.X);
            float y = Math.Min(StartCanvas.Y, EndCanvas.Y);
            float w = Math.Abs(EndCanvas.X - StartCanvas.X);
            float h = Math.Abs(EndCanvas.Y - StartCanvas.Y);
            return new HevoRect(x, y, w, h);
        }
    }

    /// <summary>
    /// 整个画布的不可变状态快照。任意修改都必须 with { ... } 出新实例,
    /// trait 引用判脏 (VisualDependencyTracker.CheckIfDirty) 才能命中。
    /// </summary>
    public record GraphState(
        IReadOnlyList<Node> Nodes,
        IReadOnlyList<Edge> Edges,
        IReadOnlySet<string> SelectedNodeIds,
        CanvasTransform Transform,
        EdgeRubberBand? RubberBand,
        BoxSelection? BoxSelection,
        HoveredPort? HoveredPort = null,
        AlignmentGuides? AlignmentGuides = null
    )
    {
        public static readonly GraphState Empty = new(
            Array.Empty<Node>(),
            Array.Empty<Edge>(),
            new HashSet<string>(),
            CanvasTransform.Identity,
            null,
            null,
            null,
            null
        );

        public Node? FindNode(string id)
        {
            foreach (var n in Nodes) if (n.Id == id) return n;
            return null;
        }

        public GraphState WithNode(Node updated)
        {
            var list = new List<Node>(Nodes.Count);
            bool replaced = false;
            foreach (var n in Nodes)
            {
                if (n.Id == updated.Id) { list.Add(updated); replaced = true; }
                else list.Add(n);
            }
            if (!replaced) list.Add(updated);
            return this with { Nodes = list };
        }

        public GraphState WithoutNode(string id)
        {
            var list = new List<Node>(Nodes.Count);
            foreach (var n in Nodes) if (n.Id != id) list.Add(n);
            var edges = new List<Edge>(Edges.Count);
            foreach (var e in Edges) if (e.FromNodeId != id && e.ToNodeId != id) edges.Add(e);
            var sel = new HashSet<string>(SelectedNodeIds);
            sel.Remove(id);
            return this with { Nodes = list, Edges = edges, SelectedNodeIds = sel };
        }

        public GraphState AddEdge(Edge edge)
        {
            // 扇入端口 (Port.IsArray) 允许多根连线;普通端口一根覆盖一根。
            var toNode = FindNode(edge.ToNodeId);
            var toPort = toNode?.FindPort(edge.ToPortId, isInput: true);
            bool fanIn = toPort?.IsArray ?? false;

            var list = new List<Edge>(Edges.Count + 1);
            foreach (var e in Edges)
            {
                if (e.ToNodeId == edge.ToNodeId && e.ToPortId == edge.ToPortId)
                {
                    if (!fanIn) continue; // 普通端口:扔掉旧的
                    // 扇入端口:同源同目标的精确重复扔掉,保留其它 incoming
                    if (e.FromNodeId == edge.FromNodeId && e.FromPortId == edge.FromPortId) continue;
                }
                list.Add(e);
            }
            list.Add(edge);
            return this with { Edges = list };
        }
    }

    /// <summary>
    /// 4 个 GraphLayer 共享同一个 trait 实例(都从 ctx.For(layer).PublishData 注入)。
    /// 引用相等判脏:State 引用一变,所有 layer 都被标脏。
    /// </summary>
    public record GraphRenderTrait(GraphState State) : IVisualTrait;
}
