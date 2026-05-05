using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using System.Windows;
using System.Windows.Input;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// 编辑器收到的状态变化通知 (主要用于属性面板 / 脏标记 / 串行化按钮启用判断)。
    /// </summary>
    public delegate void GraphStateChanged(GraphState newState, GraphState oldState);

    /// <summary>
    /// 拖拽蓝图编辑器骨架。
    /// 4 个 layer (Canvas / Node / Edge / Selection / Preview),全部由 GraphRenderTrait 引用判脏驱动重画。
    /// 鼠标事件直接挂在 ChartCell,改 trait → RequestUpdate → SubmitSync 引用判脏 → MarkDirty → Update。
    /// 不依赖 ReactiveSchema 的 board/feature 体系——纯像素坐标系,一帧一锁的渲染机制不必引入。
    /// </summary>
    public sealed class GraphSchema : ChartSchema
    {
        private readonly GraphCanvasLayer _canvasLayer = new();
        private readonly GraphNodeLayer _nodeLayer = new();
        private readonly GraphEdgeLayer _edgeLayer = new();
        private readonly GraphSelectionLayer _selectionLayer = new();
        private readonly GraphPreviewLayer _previewLayer = new();
        private readonly GraphMinimapLayer _minimapLayer = new();

        private ChartCell? _chart;
        private GraphState _state = GraphState.Empty;

        /// <summary>外部读写当前状态。set 会 diff 并通知变更。</summary>
        public GraphState State
        {
            get => _state;
            set => SetState(value);
        }

        /// <summary>状态变化时触发 (例如串行化、撤销栈、属性面板刷新)。</summary>
        public event GraphStateChanged? StateChanged;

        /// <summary>节点被双击时触发。Window4 监听此事件,弹出 NodeEditorWindow 编辑 Properties。</summary>
        public event Action<Node>? NodeEditRequested;

        // ==========================================
        //  交互运行时状态(纯 UI,不进 GraphState,避免污染序列化产物)
        // ==========================================
        private enum DragMode { None, PanCanvas, MoveNodes, RubberEdge, BoxSelect }
        private DragMode _drag = DragMode.None;
        private HevoPoint _dragStartScreen;          // 屏幕坐标:按下点
        private CanvasTransform _dragStartTransform; // 按下时的画布变换
        private Dictionary<string, HevoPoint>? _dragStartPositions; // 节点 id → 按下时的画布坐标
        private string? _rubberFromNodeId;
        private string? _rubberFromPortId;

        // ==========================================
        //  撤销栈 / Redo —— 简易模型版
        // ==========================================
        // 思路:模型变更前打 snapshot 入 _undoStack,Ctrl+Z 把 snapshot 弹回当前态(并把当前态推 _redoStack),
        // Ctrl+Y / Ctrl+Shift+Z 反向。pan/zoom/选择/橡皮筋这种纯 view 操作不入栈。
        // 栈深 capLimit 控住内存(GraphState 自身是 record 引用语义,Nodes/Edges 列表也只是引用,
        // 共享底层数据不复制,真实占用极小)。
        private readonly Stack<GraphState> _undoStack = new();
        private readonly Stack<GraphState> _redoStack = new();
        private const int UndoLimit = 100;
        // 拖拽前快照,用于 OnMouseUp 时跟当前态比对决定是否真正入栈(0 位移拖拽不污染历史)。
        private GraphState? _dragStartModelSnapshot;

        public GraphSchema()
        {
            ((ChartTemplate)this).TemplateName = "GraphSchema";
        }

        public override void ComposeAll(ChartCell chart, RenderContext ctx)
        {
            _chart = chart;

            // 1. 注册图层。同 Level 内按添加顺序决定 z-order,后加的覆盖先加的。
            //    edge 故意放在 node **之上** —— n-graph 编辑器惯例(Houdini / n8n / Unreal Blueprint
            //    都是这样):节点完整性次要,数据流连续可见性优先。配合 GraphEdgeLayer 的避障路由,
            //    多数情况曲线会自动绕开节点 AABB,只有"实在没绕开的局部"才会画在节点上,
            //    用户至少看得到连线没有断。
            chart.AddUnmanagedLayer(_canvasLayer);
            chart.AddUnmanagedLayer(_nodeLayer);
            chart.AddUnmanagedLayer(_edgeLayer);
            chart.AddUnmanagedLayer(_selectionLayer);
            chart.AddUnmanagedLayer(_previewLayer);
            chart.AddUnmanagedLayer(_minimapLayer);

            // 2. 初始化每层本地槽位(写本地 = 唤醒条件,首帧 SubmitSync 才会 MarkDirty)
            PushTraitToLayers(ctx);

            // 3. WPF 事件钩子(由 ChartCell 的 Loaded/Unloaded 控制生命周期,这里直接挂)
            chart.MouseLeftButtonDown += OnMouseDown;
            chart.MouseMove += OnMouseMove;
            chart.MouseLeftButtonUp += OnMouseUp;
            chart.MouseRightButtonDown += OnMouseRightDown;
            chart.MouseRightButtonUp += OnMouseRightUp;
            chart.MouseWheel += OnMouseWheel;
            chart.MouseLeave += OnMouseLeave;
            chart.LostMouseCapture += OnLostCapture;
            chart.Focusable = true;
            chart.KeyDown += OnKeyDown;

            // 4. 让 base 把 Aspect (默认 Empty) 也跑一遍生命周期
            base.ComposeAll(chart, ctx);
        }

        public override void DecomposeAll(ChartCell chart, RenderContext ctx)
        {
            chart.MouseLeftButtonDown -= OnMouseDown;
            chart.MouseMove -= OnMouseMove;
            chart.MouseLeftButtonUp -= OnMouseUp;
            chart.MouseRightButtonDown -= OnMouseRightDown;
            chart.MouseRightButtonUp -= OnMouseRightUp;
            chart.MouseWheel -= OnMouseWheel;
            chart.MouseLeave -= OnMouseLeave;
            chart.LostMouseCapture -= OnLostCapture;
            chart.KeyDown -= OnKeyDown;

            chart.RemoveUnmanagedLayer(_minimapLayer);
            chart.RemoveUnmanagedLayer(_previewLayer);
            chart.RemoveUnmanagedLayer(_selectionLayer);
            chart.RemoveUnmanagedLayer(_edgeLayer);
            chart.RemoveUnmanagedLayer(_nodeLayer);
            chart.RemoveUnmanagedLayer(_canvasLayer);

            _chart = null;
            base.DecomposeAll(chart, ctx);
        }

        // ==========================================
        //  状态推送
        // ==========================================
        private void SetState(GraphState newState)
        {
            if (ReferenceEquals(newState, _state)) return;
            var old = _state;
            _state = newState;
            StateChanged?.Invoke(newState, old);
            if (_chart != null) _chart.RequestUpdate(PushTraitToLayers);
        }

        // ==========================================
        //  撤销栈 API
        // ==========================================
        /// <summary>
        /// 外部"用户级编辑"统一入口:Window4 弹 NodeEditorWindow 改属性、点 Picker 加节点、清空画布,都走这里。
        /// 把当前 model snapshot 入 undo 栈、清空 redo 栈、套 mutate 拿新态、SetState。
        /// 拖拽 / 删除等 schema 内部触发的编辑直接走 PushUndoAndApply,不需要外部调。
        /// </summary>
        public void ApplyUserEdit(Func<GraphState, GraphState> mutate)
        {
            if (mutate == null) throw new ArgumentNullException(nameof(mutate));
            var next = mutate(_state);
            if (!ModelChanged(_state, next)) { SetState(next); return; }
            PushSnapshotToUndo(_state);
            SetState(next);
        }

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        public void Undo()
        {
            if (_undoStack.Count == 0) return;
            var prev = _undoStack.Pop();
            // 当前态推 redo 栈;view 部分(Transform 等)不要丢,保留当前 view。
            var carryView = MergeViewOnly(prev, _state);
            _redoStack.Push(_state);
            SetState(carryView);
        }

        public void Redo()
        {
            if (_redoStack.Count == 0) return;
            var next = _redoStack.Pop();
            var carryView = MergeViewOnly(next, _state);
            _undoStack.Push(_state);
            SetState(carryView);
        }

        // 入栈时只关心模型(Nodes/Edges)。
        // Note:压栈值是历史"模型态";恢复时跟当前 view (Transform / SelectedNodeIds) 合一,避免撤销
        // 把视口拉回到几小时前的位置。
        private void PushSnapshotToUndo(GraphState snapshot)
        {
            _undoStack.Push(snapshot);
            _redoStack.Clear();
            // capLimit:深度超过 UndoLimit 时砍掉栈底(最老那批)。Stack<T> 没有 RemoveAt,
            // 简单做法:转 List 切尾再回填。栈不长,O(N) 也无所谓。
            if (_undoStack.Count > UndoLimit)
            {
                var keep = _undoStack.Take(UndoLimit).ToArray();
                _undoStack.Clear();
                for (int i = keep.Length - 1; i >= 0; i--) _undoStack.Push(keep[i]);
            }
        }

        // schema 内部的编辑路径(键盘删除 / 拖拽落点)统一通过此方法入栈 + 应用。
        private void PushUndoAndApply(GraphState newState)
        {
            if (!ModelChanged(_state, newState)) { SetState(newState); return; }
            PushSnapshotToUndo(_state);
            SetState(newState);
        }

        // 比较两个 GraphState 的"模型部分"是否变化。Nodes / Edges 列表引用变了即视为变化
        // (with{ Nodes = newList } 一定换引用,所以纯 view 改动不会误判)。
        private static bool ModelChanged(GraphState a, GraphState b)
            => !ReferenceEquals(a.Nodes, b.Nodes) || !ReferenceEquals(a.Edges, b.Edges);

        // 把 source 的模型(Nodes/Edges)与 viewKeeper 的 view(Transform/SelectedNodeIds 等) 合并。
        // Undo/Redo 还原历史模型时,保留用户当前视口位置,体验更连贯。
        private static GraphState MergeViewOnly(GraphState source, GraphState viewKeeper)
            => source with
            {
                Transform = viewKeeper.Transform,
                SelectedNodeIds = new HashSet<string>(),  // 历史的选中没意义,清空
                RubberBand = null,
                BoxSelection = null,
                HoveredPort = null,
            };

        private void PushTraitToLayers(RenderContext ctx)
        {
            var trait = new GraphRenderTrait(_state);
            ctx.For(_canvasLayer).PublishData(trait);
            ctx.For(_nodeLayer).PublishData(trait);
            ctx.For(_edgeLayer).PublishData(trait);
            ctx.For(_selectionLayer).PublishData(trait);
            ctx.For(_previewLayer).PublishData(trait);
            ctx.For(_minimapLayer).PublishData(trait);
        }

        // ==========================================
        //  Hit test
        // ==========================================
        private record HitResult(Node? Node, Port? Port, bool IsInput);

        private HitResult HitTest(HevoPoint canvasPt)
        {
            const float portRadius = 8f; // 命中半径放大些,方便点中
            // 倒序遍历(后画的在上)
            for (int i = _state.Nodes.Count - 1; i >= 0; i--)
            {
                var node = _state.Nodes[i];
                // 先测端口(端口圆点跨出节点边界)
                foreach (var p in node.OutputPorts)
                {
                    var c = node.GetPortPosition(p);
                    if (Distance(canvasPt, c) <= portRadius)
                        return new HitResult(node, p, false);
                }
                foreach (var p in node.InputPorts)
                {
                    var c = node.GetPortPosition(p);
                    if (Distance(canvasPt, c) <= portRadius)
                        return new HitResult(node, p, true);
                }
                if (node.GetBounds().Contains(canvasPt))
                    return new HitResult(node, null, false);
            }
            return new HitResult(null, null, false);
        }

        private static float Distance(HevoPoint a, HevoPoint b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        // ==========================================
        //  Pointer 事件
        // ==========================================
        private static HevoPoint Pt(MouseEventArgs e, IInputElement el)
        {
            var p = e.GetPosition(el);
            return new HevoPoint((float)p.X, (float)p.Y);
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_chart == null) return;
            _chart.Focus();
            var screen = Pt(e, _chart);

            // Minimap 优先拦截:左键落在右下角 minimap 浮窗内 → 进入 PanCanvas 的镜像模式
            //(后续 mouse-move 跟随,松开退出),不进入节点/端口的 hit-test 流程。
            if (TryHandleMinimapClick(screen))
            {
                _drag = DragMode.PanCanvas;
                _dragStartScreen = screen;
                _dragStartTransform = _state.Transform;
                _chart.CaptureMouse();
                e.Handled = true;
                return;
            }

            var canvas = _state.Transform.ScreenToCanvas(screen);
            var hit = HitTest(canvas);

            _dragStartScreen = screen;
            _dragStartTransform = _state.Transform;

            // 1. 输出端口 → 拖出连线
            if (hit.Node != null && hit.Port != null && !hit.IsInput)
            {
                _drag = DragMode.RubberEdge;
                _dragStartModelSnapshot = _state;   // 拖到目标 input 落下时,跟这个 snapshot 比对决定入栈
                _rubberFromNodeId = hit.Node.Id;
                _rubberFromPortId = hit.Port.Id;
                SetState(_state with { RubberBand = new EdgeRubberBand(hit.Node.Id, hit.Port.Id, canvas, IsValidTarget: false) });
                _chart.CaptureMouse();
                e.Handled = true;
                return;
            }

            // 2. 输入端口直接点 → 移除已连线 (便利操作)
            if (hit.Node != null && hit.Port != null && hit.IsInput)
            {
                var edges = _state.Edges.Where(x => !(x.ToNodeId == hit.Node.Id && x.ToPortId == hit.Port.Id)).ToList();
                if (edges.Count != _state.Edges.Count)
                {
                    PushUndoAndApply(_state with { Edges = edges });
                }
                e.Handled = true;
                return;
            }

            // 3. 节点本体 → 双击进编辑器 / 单击选中 + 准备拖移
            if (hit.Node != null)
            {
                // 双击不进入拖动模式,直接抛事件给宿主开 NodeEditorWindow。
                // (拖拽开始的 mouse-down 是 ClickCount=1;ClickCount=2 时 WPF 已经判定双击,不会再触发拖动)
                if (e.ClickCount >= 2)
                {
                    NodeEditRequested?.Invoke(hit.Node);
                    e.Handled = true;
                    return;
                }

                bool addToSelection = (Keyboard.Modifiers & ModifierKeys.Control) != 0
                                   || (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                var sel = new HashSet<string>(addToSelection ? _state.SelectedNodeIds : Enumerable.Empty<string>());
                if (addToSelection && sel.Contains(hit.Node.Id)) sel.Remove(hit.Node.Id);
                else sel.Add(hit.Node.Id);
                SetState(_state with { SelectedNodeIds = sel });

                _drag = DragMode.MoveNodes;
                _dragStartModelSnapshot = _state;   // 落点时跟它比对,0 位移就不入栈
                _dragStartPositions = sel.ToDictionary(id => id, id => _state.FindNode(id)?.Position ?? default);
                _chart.CaptureMouse();
                e.Handled = true;
                return;
            }

            // 4. 空白 → 框选 (默认就开框选;Shift 表示叠加,这里先简化为替换)
            _drag = DragMode.BoxSelect;
            SetState(_state with
            {
                SelectedNodeIds = new HashSet<string>(),
                BoxSelection = new BoxSelection(canvas, canvas)
            });
            _chart.CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseRightDown(object sender, MouseButtonEventArgs e)
        {
            if (_chart == null) return;
            _drag = DragMode.PanCanvas;
            _dragStartScreen = Pt(e, _chart);
            _dragStartTransform = _state.Transform;
            _chart.CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseRightUp(object sender, MouseButtonEventArgs e)
        {
            if (_drag == DragMode.PanCanvas) { _drag = DragMode.None; _isMinimapDrag = false; _chart?.ReleaseMouseCapture(); e.Handled = true; }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_chart == null) return;
            var screen = Pt(e, _chart);
            var canvas = _state.Transform.ScreenToCanvas(screen);

            // 非拖拽态:只更新 hover tooltip
            if (_drag == DragMode.None)
            {
                UpdateHoveredPort(canvas);
                return;
            }

            switch (_drag)
            {
                case DragMode.PanCanvas:
                {
                    // Minimap 拖动:鼠标在 minimap 浮窗里时,把屏幕拖动量按 minimap 缩放因子的倒数
                    // 反向应用到 Transform.OffsetX/Y,等价于"在缩略图里抓取并平移"。
                    if (TryHandleMinimapDrag(screen))
                    {
                        break;
                    }
                    float dx = screen.X - _dragStartScreen.X;
                    float dy = screen.Y - _dragStartScreen.Y;
                    var t = _dragStartTransform;
                    SetState(_state with { Transform = new CanvasTransform(t.OffsetX + dx, t.OffsetY + dy, t.Scale) });
                    break;
                }
                case DragMode.MoveNodes:
                {
                    if (_dragStartPositions == null) break;
                    var startCanvas = _dragStartTransform.ScreenToCanvas(_dragStartScreen);
                    float ddx = canvas.X - startCanvas.X;
                    float ddy = canvas.Y - startCanvas.Y;

                    // 吸附:对被拖节点的 left/center/right 与 top/center/bottom 找最近的非拖节点对应位,
                    // 命中阈值内 → 修正 ddx/ddy 让它对齐 + 收集要画的辅助线。
                    var (snappedDdx, snappedDdy, guides) = ComputeAlignmentSnap(_state, _dragStartPositions, ddx, ddy);

                    var nodes = new List<Node>(_state.Nodes.Count);
                    foreach (var n in _state.Nodes)
                    {
                        if (_dragStartPositions.TryGetValue(n.Id, out var orig))
                            nodes.Add(n with { Position = new HevoPoint(orig.X + snappedDdx, orig.Y + snappedDdy) });
                        else
                            nodes.Add(n);
                    }
                    SetState(_state with { Nodes = nodes, AlignmentGuides = guides });
                    break;
                }
                case DragMode.RubberEdge:
                {
                    if (_rubberFromNodeId == null || _rubberFromPortId == null) break;
                    var hit = HitTest(canvas);
                    bool valid = hit.Node != null && hit.Port != null && hit.IsInput
                                 && hit.Node.Id != _rubberFromNodeId
                                 && PortsCompatible(_rubberFromNodeId, _rubberFromPortId, hit.Node, hit.Port);
                    SetState(_state with { RubberBand = new EdgeRubberBand(_rubberFromNodeId, _rubberFromPortId, canvas, valid) });
                    break;
                }
                case DragMode.BoxSelect:
                {
                    if (_state.BoxSelection == null) break;
                    SetState(_state with { BoxSelection = _state.BoxSelection with { EndCanvas = canvas } });
                    break;
                }
            }
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_chart == null || _drag == DragMode.None) return;
            switch (_drag)
            {
                case DragMode.RubberEdge:
                {
                    if (_rubberFromNodeId != null && _rubberFromPortId != null)
                    {
                        var screen = Pt(e, _chart);
                        var canvas = _state.Transform.ScreenToCanvas(screen);
                        var hit = HitTest(canvas);
                        if (hit.Node != null && hit.Port != null && hit.IsInput
                            && hit.Node.Id != _rubberFromNodeId
                            && PortsCompatible(_rubberFromNodeId, _rubberFromPortId, hit.Node, hit.Port))
                        {
                            var edge = new Edge(
                                Id: Guid.NewGuid().ToString("N").Substring(0, 8),
                                FromNodeId: _rubberFromNodeId, FromPortId: _rubberFromPortId,
                                ToNodeId: hit.Node.Id, ToPortId: hit.Port.Id);
                            // 拖出连线成功:跟拖前 snapshot 比对入栈
                            CommitDrag(_state.AddEdge(edge) with { RubberBand = null });
                        }
                        else
                        {
                            // 没落到合法 input,只是清掉橡皮筋,不算编辑
                            SetState(_state with { RubberBand = null });
                            _dragStartModelSnapshot = null;
                        }
                    }
                    _rubberFromNodeId = null; _rubberFromPortId = null;
                    break;
                }
                case DragMode.BoxSelect:
                {
                    if (_state.BoxSelection != null)
                    {
                        var rect = _state.BoxSelection.ToRect();
                        var sel = new HashSet<string>();
                        foreach (var n in _state.Nodes)
                        {
                            if (rect.Intersect(n.GetBounds()).IsEmpty) continue;
                            sel.Add(n.Id);
                        }
                        // 框选只改 view (SelectedNodeIds),不入 undo 栈
                        SetState(_state with { SelectedNodeIds = sel, BoxSelection = null });
                    }
                    _dragStartModelSnapshot = null;
                    break;
                }
                case DragMode.MoveNodes:
                {
                    // 落点时:OnMouseMove 一路调 SetState 写新位置,这里只决定要不要入 undo 栈
                    // 顺手清掉对齐辅助线,免得拖完留在画布上
                    CommitDrag(_state with { AlignmentGuides = null });
                    _dragStartPositions = null;
                    break;
                }
            }
            _drag = DragMode.None;
            _isMinimapDrag = false;
            _chart.ReleaseMouseCapture();
            e.Handled = true;
        }

        // 拖拽落点统一收口:跟拖前 snapshot 比对,模型有变化才入栈,避免"按下没动"也产生 undo 条目。
        // newState 就是当前 _state(MoveNodes 已经被 OnMouseMove 实时更新)或者刚拼好的新态(RubberEdge)。
        private void CommitDrag(GraphState newState)
        {
            var snap = _dragStartModelSnapshot;
            _dragStartModelSnapshot = null;
            if (snap != null && ModelChanged(snap, newState))
            {
                PushSnapshotToUndo(snap);
            }
            if (!ReferenceEquals(newState, _state)) SetState(newState);
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            if (_state.HoveredPort != null)
                SetState(_state with { HoveredPort = null });
        }

        private void OnLostCapture(object sender, MouseEventArgs e)
        {
            if (_drag == DragMode.None) return;
            _drag = DragMode.None;
            _isMinimapDrag = false;
            _dragStartPositions = null;
            _dragStartModelSnapshot = null;
            _rubberFromNodeId = null; _rubberFromPortId = null;
            if (_state.RubberBand != null || _state.BoxSelection != null || _state.AlignmentGuides != null)
                SetState(_state with { RubberBand = null, BoxSelection = null, AlignmentGuides = null });
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_chart == null) return;
            var screen = Pt(e, _chart);
            var canvasUnderMouse = _state.Transform.ScreenToCanvas(screen);

            float factor = e.Delta > 0 ? 1.15f : 1f / 1.15f;
            float newScale = Math.Clamp(_state.Transform.Scale * factor, 0.2f, 4f);

            // 保持鼠标下的画布点不动:newOffset = screen - canvasPt * newScale
            float ox = screen.X - canvasUnderMouse.X * newScale;
            float oy = screen.Y - canvasUnderMouse.Y * newScale;
            SetState(_state with { Transform = new CanvasTransform(ox, oy, newScale) });
            e.Handled = true;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z 统一在这里拦,优先于 Delete。
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (ctrl && (e.Key == Key.Z) && !shift) { Undo(); e.Handled = true; return; }
            if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && shift))) { Redo(); e.Handled = true; return; }

            if (e.Key == Key.Delete && _state.SelectedNodeIds.Count > 0)
            {
                var s = _state;
                foreach (var id in s.SelectedNodeIds.ToArray()) s = s.WithoutNode(id);
                PushUndoAndApply(s with { SelectedNodeIds = new HashSet<string>() });
                e.Handled = true;
            }
        }

        // hover 检测:命中端口才更新 HoveredPort,空白时清掉。
        // 用 ReferenceEquals 短路避免每次 mouse-move 都 SetState (即使 HoveredPort 没变也会重画 preview 层)。
        private void UpdateHoveredPort(HevoPoint canvasPt)
        {
            const float r = 8f;
            HoveredPort? next = null;
            for (int i = _state.Nodes.Count - 1; i >= 0; i--)
            {
                var node = _state.Nodes[i];
                foreach (var p in node.OutputPorts)
                {
                    var c = node.GetPortPosition(p);
                    if (Distance(canvasPt, c) <= r)
                    {
                        var screen = _state.Transform.CanvasToScreen(c);
                        next = new HoveredPort(node.Id, p.Id, false, screen);
                        goto done;
                    }
                }
                foreach (var p in node.InputPorts)
                {
                    var c = node.GetPortPosition(p);
                    if (Distance(canvasPt, c) <= r)
                    {
                        var screen = _state.Transform.CanvasToScreen(c);
                        next = new HoveredPort(node.Id, p.Id, true, screen);
                        goto done;
                    }
                }
            }
            done:
            // 同样的 hover 状态不重发 (按 tuple 等价比较)
            if (HoverEquals(_state.HoveredPort, next)) return;
            SetState(_state with { HoveredPort = next });
        }

        private static bool HoverEquals(HoveredPort? a, HoveredPort? b)
        {
            if (a is null && b is null) return true;
            if (a is null || b is null) return false;
            return a.NodeId == b.NodeId && a.PortId == b.PortId && a.IsInput == b.IsInput;
        }

        // ==========================================
        //  Minimap 命中 + 拖动跳转
        // ==========================================

        // _isMinimapDrag = true 时 PanCanvas 走 minimap 模式(屏幕坐标按缩放反向应用到 Transform)。
        private bool _isMinimapDrag;
        // minimap 拖动开始时的 minimap 内"中心点 → 画布坐标"换算因子缓存,避免每个 mouse-move 重算。
        private float _minimapDragOx, _minimapDragOy, _minimapDragScale;

        /// <summary>
        /// 落在 minimap 浮窗内 → 立即把视口中心移到点击位对应的画布点;返回 true 表示被本路径接管。
        /// </summary>
        private bool TryHandleMinimapClick(HevoPoint screen)
        {
            if (_chart == null || _state.Nodes.Count == 0) return false;
            if (!ComputeMinimapMapping(out var mx, out var my, out var ox, out var oy, out var scale, out var winW, out var winH)) return false;
            // 点击是否落在 minimap 浮窗
            var rect = new HevoRect(mx, my, GraphMinimapLayer.MinimapWidth, GraphMinimapLayer.MinimapHeight);
            if (!rect.Contains(screen)) return false;

            _isMinimapDrag = true;
            _minimapDragOx = ox; _minimapDragOy = oy; _minimapDragScale = scale;
            // 把视口中心移到点击位对应的画布点
            CenterViewportOnMinimapClick(screen, ox, oy, scale, winW, winH);
            return true;
        }

        /// <summary>
        /// PanCanvas 模式下,如果当前是 minimap 拖动,沿用同样的"点 → 视口中心"逻辑跟随鼠标;
        /// 返回 true 表示走了 minimap 路径,外层别再做普通画布平移。
        /// </summary>
        private bool TryHandleMinimapDrag(HevoPoint screen)
        {
            if (!_isMinimapDrag || _chart == null) return false;
            float winW = (float)_chart.ActualWidth;
            float winH = (float)_chart.ActualHeight;
            CenterViewportOnMinimapClick(screen, _minimapDragOx, _minimapDragOy, _minimapDragScale, winW, winH);
            return true;
        }

        private void CenterViewportOnMinimapClick(HevoPoint screen, float ox, float oy, float scale, float winW, float winH)
        {
            // 屏幕点(在 minimap 内) → 画布坐标:canvasPt = (screenPt - (ox, oy)) / scale
            float canvasX = (screen.X - ox) / scale;
            float canvasY = (screen.Y - oy) / scale;
            // 让该画布点出现在 chart cell 的中心:Transform.Offset = winSize/2 - canvasPt * scale_view
            var tr = _state.Transform;
            float newOffsetX = winW / 2f - canvasX * tr.Scale;
            float newOffsetY = winH / 2f - canvasY * tr.Scale;
            SetState(_state with { Transform = new CanvasTransform(newOffsetX, newOffsetY, tr.Scale) });
        }

        // 计算当前 minimap 浮窗的几何与缩放(跟 GraphMinimapLayer.OnUpdate 完全对齐),命中/拖动复用。
        private bool ComputeMinimapMapping(
            out float mx, out float my, out float ox, out float oy, out float scale, out float winW, out float winH)
        {
            mx = my = ox = oy = scale = winW = winH = 0f;
            if (_chart == null) return false;
            winW = (float)_chart.ActualWidth;
            winH = (float)_chart.ActualHeight;
            if (winW <= 0 || winH <= 0) return false;
            mx = winW - GraphMinimapLayer.MinimapWidth - GraphMinimapLayer.MinimapMargin;
            my = winH - GraphMinimapLayer.MinimapHeight - GraphMinimapLayer.MinimapMargin;
            var (bbX, bbY, bbW, bbH) = GraphMinimapLayer.ComputeContentBounds(_state.Nodes);
            float drawAreaW = GraphMinimapLayer.MinimapWidth - 2 * GraphMinimapLayer.MinimapPadding;
            float drawAreaH = GraphMinimapLayer.MinimapHeight - 2 * GraphMinimapLayer.MinimapPadding;
            scale = Math.Min(drawAreaW / Math.Max(bbW, 1f), drawAreaH / Math.Max(bbH, 1f));
            ox = mx + GraphMinimapLayer.MinimapPadding + (drawAreaW - bbW * scale) / 2f - bbX * scale;
            oy = my + GraphMinimapLayer.MinimapPadding + (drawAreaH - bbH * scale) / 2f - bbY * scale;
            return true;
        }

        // ==========================================
        //  对齐吸附 (MoveNodes 拖动时)
        // ==========================================

        /// <summary>
        /// 拖动节点的吸附 + 辅助线计算。
        /// 思路:候选位 = 所有"未被拖"节点的 left/centerX/right + top/centerY/bottom。
        /// 对每个被拖节点的同三档位计算"靠近后的差距",取最小;若 <= 阈值就吸附 + 收一根辅助线。
        /// 阈值用画布坐标的固定值(不随 zoom 缩),保证视觉感受一致。
        /// </summary>
        private static (float ddx, float ddy, AlignmentGuides? guides) ComputeAlignmentSnap(
            GraphState state, Dictionary<string, HevoPoint> dragStartPositions, float ddx, float ddy)
        {
            const float snapThreshold = 6f;   // 画布坐标距离 ≤ 6 即吸附
            // 1) 收集"参考位"——所有未被拖节点的边/中线坐标
            var refXs = new List<float>(state.Nodes.Count * 3);
            var refYs = new List<float>(state.Nodes.Count * 3);
            foreach (var n in state.Nodes)
            {
                if (dragStartPositions.ContainsKey(n.Id)) continue;
                var b = n.GetBounds();
                refXs.Add(b.X);
                refXs.Add(b.X + b.Width / 2f);
                refXs.Add(b.X + b.Width);
                refYs.Add(b.Y);
                refYs.Add(b.Y + b.Height / 2f);
                refYs.Add(b.Y + b.Height);
            }
            if (refXs.Count == 0 && refYs.Count == 0) return (ddx, ddy, null);

            // 2) 对每个被拖节点的"目标位"(orig + ddx/ddy)计算同三档与参考位的最小差距,挑总冠军
            //    stackalloc 提到循环外避免重复分配(CA2014)。
            Span<float> tgtXs = stackalloc float[3];
            Span<float> tgtYs = stackalloc float[3];
            float bestDx = 0f, bestDxAbs = float.MaxValue, bestSnapX = 0f;
            float bestDy = 0f, bestDyAbs = float.MaxValue, bestSnapY = 0f;
            foreach (var kv in dragStartPositions)
            {
                var node = state.FindNode(kv.Key);
                if (node == null) continue;
                var orig = kv.Value;
                var size = node.Size;
                tgtXs[0] = orig.X + ddx;
                tgtXs[1] = orig.X + ddx + size.X / 2f;
                tgtXs[2] = orig.X + ddx + size.X;
                tgtYs[0] = orig.Y + ddy;
                tgtYs[1] = orig.Y + ddy + size.Y / 2f;
                tgtYs[2] = orig.Y + ddy + size.Y;
                for (int i = 0; i < 3; i++)
                {
                    foreach (var rx in refXs)
                    {
                        float diff = rx - tgtXs[i];
                        if (Math.Abs(diff) < bestDxAbs) { bestDxAbs = Math.Abs(diff); bestDx = diff; bestSnapX = rx; }
                    }
                    foreach (var ry in refYs)
                    {
                        float diff = ry - tgtYs[i];
                        if (Math.Abs(diff) < bestDyAbs) { bestDyAbs = Math.Abs(diff); bestDy = diff; bestSnapY = ry; }
                    }
                }
            }

            float finalDdx = bestDxAbs <= snapThreshold ? ddx + bestDx : ddx;
            float finalDdy = bestDyAbs <= snapThreshold ? ddy + bestDy : ddy;

            // 3) 命中的对齐位写成一根辅助线;两个方向分别独立判定
            var vlines = bestDxAbs <= snapThreshold ? new[] { bestSnapX } : Array.Empty<float>();
            var hlines = bestDyAbs <= snapThreshold ? new[] { bestSnapY } : Array.Empty<float>();
            AlignmentGuides? guides = vlines.Length == 0 && hlines.Length == 0
                ? null
                : new AlignmentGuides(vlines, hlines);

            return (finalDdx, finalDdy, guides);
        }

        private bool PortsCompatible(string fromNodeId, string fromPortId, Node toNode, Port toPort)
        {
            var fromNode = _state.FindNode(fromNodeId);
            if (fromNode == null) return false;
            var fromPort = fromNode.FindPort(fromPortId, isInput: false);
            if (fromPort == null) return false;
            // MVP:类型名字符串相等即兼容;通配 "object" 永远兼容
            return fromPort.DataTypeName == toPort.DataTypeName
                || fromPort.DataTypeName == "object"
                || toPort.DataTypeName == "object";
        }
    }
}
