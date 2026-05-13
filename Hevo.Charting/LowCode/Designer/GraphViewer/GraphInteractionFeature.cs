using System.Linq;
using System.Windows;
using System.Windows.Input;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using static Hevo.Charting.LowCode.Designer.GraphViewer.GraphInteractionHelpers;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// graph editor 的"交互层"feature(Phase 3.1 init field DataSource;Phase 3.2 取消拆分,
    /// 跟 chart 侧 <see cref="Features.ChartInteractionFeature"/> 单类模式对齐)。
    /// <para>
    /// 接管 ChartCell 上的鼠标 / 键盘 / 触屏事件,翻译成对 <see cref="BlueprintDataSource"/> 的状态变更:
    /// 拖拽节点、橡皮筋连线、框选、画布平移 / 缩放 / 触屏捏合、Ctrl+Z/Y 撤销重做、Delete 删除节点、
    /// minimap 浮窗的点击 / 拖动跳转。所有命中检测 + 对齐吸附逻辑也在本 feature 内。
    /// </para>
    /// <para>
    /// <b>跟 ChartInteractionFeature 模式对齐</b>:单 Feature 类持完整 drag 状态机(<see cref="DragMode"/>)+
    /// 各事件 handler 共用一组 transient 字段,内部用静态 helper(<see cref="GraphInteractionHelpers"/>)
    /// 抽 hit/坐标/minimap 几何。
    /// </para>
    /// <para>
    /// 跟 <see cref="BlueprintDataSource"/> 的边界:本 feature 不持有状态,只读 <c>DataSource.State</c>
    /// 算 hit / mutate,拿到结果回写到 <c>DataSource.State</c> / <c>DataSource.PushUndoAndApply</c> 等。
    /// </para>
    /// </summary>
    public sealed class GraphInteractionFeature : Feature
    {
        public override FeaturePhase Phase => FeaturePhase.Interaction;

        /// <summary>
        /// 由装配方(<see cref="GraphEditorSchema"/>)注入的状态 source-of-truth。null = 未注入,装配错误。
        /// </summary>
        public BlueprintDataSource DataSource { get; init; } = null!;

        /// <summary>
        /// Minimap 几何参数(渲染 + 命中两端共享)。改尺寸只动一处,
        /// 由 <see cref="GraphEditorSchema.MinimapGeometry"/> init 注入,跟 <see cref="GraphMinimapFeature"/> 同一份。
        /// </summary>
        public MinimapGeometry Geometry { get; init; } = MinimapGeometry.Default;

        /// <summary>节点被双击时触发。NodeEditorWindow 监听此事件,弹出节点属性编辑器。</summary>
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
        // 拖前模型快照,用于 OnMouseUp 时跟当前态比对决定是否真正入栈(0 位移拖拽不污染历史)。
        private GraphState? _dragStartModelSnapshot;

        // _isMinimapDrag = true 时 PanCanvas 走 minimap 模式(屏幕坐标按缩放反向应用到 Transform)。
        private bool _isMinimapDrag;
        // minimap 拖动开始时的"中心点 → 画布坐标"换算因子缓存,避免每个 mouse-move 重算。
        private float _minimapDragOx, _minimapDragOy, _minimapDragScale;

        // ==========================================
        //  Lifecycle
        // ==========================================
        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            chart.MouseLeftButtonDown  += OnMouseDown;
            chart.MouseMove            += OnMouseMove;
            chart.MouseLeftButtonUp    += OnMouseUp;
            chart.MouseRightButtonDown += OnMouseRightDown;
            chart.MouseRightButtonUp   += OnMouseRightUp;
            chart.MouseWheel           += OnMouseWheel;
            chart.MouseLeave           += OnMouseLeave;
            chart.LostMouseCapture     += OnLostCapture;
            chart.Focusable             = true;
            chart.KeyDown              += OnKeyDown;
            // 触屏 Manipulation:Mode 配置必须早于 Delta,所以用 Starting。Translate(平移)+ Scale(捏合)
            // 同时启用,跟鼠标 right-drag pan + wheel zoom 行为对齐。
            chart.ManipulationStarting += OnManipulationStarting;
            chart.ManipulationDelta    += OnManipulationDelta;
        }

        protected override void OnDecompose()
        {
            var chart = Chart;
            if (chart == null) return;
            chart.MouseLeftButtonDown  -= OnMouseDown;
            chart.MouseMove            -= OnMouseMove;
            chart.MouseLeftButtonUp    -= OnMouseUp;
            chart.MouseRightButtonDown -= OnMouseRightDown;
            chart.MouseRightButtonUp   -= OnMouseRightUp;
            chart.MouseWheel           -= OnMouseWheel;
            chart.MouseLeave           -= OnMouseLeave;
            chart.LostMouseCapture     -= OnLostCapture;
            chart.KeyDown              -= OnKeyDown;
            chart.ManipulationStarting -= OnManipulationStarting;
            chart.ManipulationDelta    -= OnManipulationDelta;
        }

        protected override void OnProject(FeatureContext ctx)
        {
            // 不渲染 —— 交互层只 mutate 状态,渲染交给 GraphCanvasFeature / NodeFeature 等。
        }

        // ==========================================
        //  Pointer 事件
        // ==========================================
        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            var chart = Chart;
            if (chart == null) return;
            chart.Focus();
            var screen = Pt(e, chart);
            var s = DataSource.State;

            // Minimap 优先拦截:左键落在右下角 minimap 浮窗内 → 进入 PanCanvas 的镜像模式
            //(后续 mouse-move 跟随,松开退出),不进入节点/端口的 hit-test 流程。
            if (TryHandleMinimapClick(screen, s))
            {
                _drag = DragMode.PanCanvas;
                _dragStartScreen = screen;
                _dragStartTransform = DataSource.State.Transform;
                chart.CaptureMouse();
                e.Handled = true;
                return;
            }

            var canvas = s.Transform.ScreenToCanvas(screen);
            var hit = HitTest(s, canvas);

            _dragStartScreen = screen;
            _dragStartTransform = s.Transform;

            // 1. 输出端口 → 拖出连线
            if (hit.Node != null && hit.Port != null && !hit.IsInput)
            {
                _drag = DragMode.RubberEdge;
                _dragStartModelSnapshot = s;   // 拖到目标 input 落下时,跟这个 snapshot 比对决定入栈
                _rubberFromNodeId = hit.Node.Id;
                _rubberFromPortId = hit.Port.Id;
                DataSource.State = s with { RubberBand = new EdgeRubberBand(hit.Node.Id, hit.Port.Id, canvas, IsValidTarget: false) };
                chart.CaptureMouse();
                e.Handled = true;
                return;
            }

            // 2. 输入端口直接点 → 移除已连线 (便利操作)
            if (hit.Node != null && hit.Port != null && hit.IsInput)
            {
                var edges = s.Edges.Where(x => !(x.ToNodeId == hit.Node.Id && x.ToPortId == hit.Port.Id)).ToList();
                if (edges.Count != s.Edges.Count)
                {
                    DataSource.PushUndoAndApply(s with { Edges = edges });
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
                var sel = new HashSet<string>(addToSelection ? s.SelectedNodeIds : Enumerable.Empty<string>());
                if (addToSelection && sel.Contains(hit.Node.Id)) sel.Remove(hit.Node.Id);
                else sel.Add(hit.Node.Id);
                DataSource.State = s with { SelectedNodeIds = sel };

                _drag = DragMode.MoveNodes;
                _dragStartModelSnapshot = DataSource.State;   // 落点时跟它比对,0 位移就不入栈
                _dragStartPositions = sel.ToDictionary(id => id, id => DataSource.State.FindNode(id)?.Position ?? default);
                chart.CaptureMouse();
                e.Handled = true;
                return;
            }

            // 4. 空白 → 框选 (默认就开框选;Shift 表示叠加,这里先简化为替换)
            _drag = DragMode.BoxSelect;
            DataSource.State = s with
            {
                SelectedNodeIds = new HashSet<string>(),
                BoxSelection = new BoxSelection(canvas, canvas)
            };
            chart.CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseRightDown(object sender, MouseButtonEventArgs e)
        {
            var chart = Chart;
            if (chart == null) return;
            _drag = DragMode.PanCanvas;
            _dragStartScreen = Pt(e, chart);
            _dragStartTransform = DataSource.State.Transform;
            chart.CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseRightUp(object sender, MouseButtonEventArgs e)
        {
            if (_drag == DragMode.PanCanvas) { _drag = DragMode.None; _isMinimapDrag = false; Chart?.ReleaseMouseCapture(); e.Handled = true; }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            var chart = Chart;
            if (chart == null) return;
            var screen = Pt(e, chart);
            var s = DataSource.State;
            var canvas = s.Transform.ScreenToCanvas(screen);

            // 非拖拽态:只更新 hover tooltip
            if (_drag == DragMode.None)
            {
                UpdateHoveredPort(s, canvas);
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
                    DataSource.State = s with { Transform = new CanvasTransform(t.OffsetX + dx, t.OffsetY + dy, t.Scale) };
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
                    var (snappedDdx, snappedDdy, guides) = ComputeAlignmentSnap(s, _dragStartPositions, ddx, ddy);

                    var nodes = new List<Node>(s.Nodes.Count);
                    foreach (var n in s.Nodes)
                    {
                        if (_dragStartPositions.TryGetValue(n.Id, out var orig))
                            nodes.Add(n with { Position = new HevoPoint(orig.X + snappedDdx, orig.Y + snappedDdy) });
                        else
                            nodes.Add(n);
                    }
                    DataSource.State = s with { Nodes = nodes, AlignmentGuides = guides };
                    break;
                }
                case DragMode.RubberEdge:
                {
                    if (_rubberFromNodeId == null || _rubberFromPortId == null) break;
                    var hit = HitTest(s, canvas);
                    bool valid = hit.Node != null && hit.Port != null && hit.IsInput
                                 && hit.Node.Id != _rubberFromNodeId
                                 && PortsCompatible(s, _rubberFromNodeId, _rubberFromPortId, hit.Node, hit.Port);
                    DataSource.State = s with { RubberBand = new EdgeRubberBand(_rubberFromNodeId, _rubberFromPortId, canvas, valid) };
                    break;
                }
                case DragMode.BoxSelect:
                {
                    if (s.BoxSelection == null) break;
                    DataSource.State = s with { BoxSelection = s.BoxSelection with { EndCanvas = canvas } };
                    break;
                }
            }
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            var chart = Chart;
            if (chart == null || _drag == DragMode.None) return;
            switch (_drag)
            {
                case DragMode.RubberEdge:
                {
                    if (_rubberFromNodeId != null && _rubberFromPortId != null)
                    {
                        var screen = Pt(e, chart);
                        var s = DataSource.State;
                        var canvas = s.Transform.ScreenToCanvas(screen);
                        var hit = HitTest(s, canvas);
                        if (hit.Node != null && hit.Port != null && hit.IsInput
                            && hit.Node.Id != _rubberFromNodeId
                            && PortsCompatible(s, _rubberFromNodeId, _rubberFromPortId, hit.Node, hit.Port))
                        {
                            var edge = new Edge(
                                Id: Guid.NewGuid().ToString("N").Substring(0, 8),
                                FromNodeId: _rubberFromNodeId, FromPortId: _rubberFromPortId,
                                ToNodeId: hit.Node.Id, ToPortId: hit.Port.Id);
                            // 拖出连线成功:跟拖前 snapshot 比对入栈
                            CommitDrag(s.AddEdge(edge) with { RubberBand = null });
                        }
                        else
                        {
                            // 没落到合法 input,只是清掉橡皮筋,不算编辑
                            DataSource.State = s with { RubberBand = null };
                            _dragStartModelSnapshot = null;
                        }
                    }
                    _rubberFromNodeId = null; _rubberFromPortId = null;
                    break;
                }
                case DragMode.BoxSelect:
                {
                    var s = DataSource.State;
                    if (s.BoxSelection != null)
                    {
                        var rect = s.BoxSelection.ToRect();
                        var sel = new HashSet<string>();
                        foreach (var n in s.Nodes)
                        {
                            if (rect.Intersect(n.GetBounds()).IsEmpty) continue;
                            sel.Add(n.Id);
                        }
                        // 框选只改 view (SelectedNodeIds),不入 undo 栈
                        DataSource.State = s with { SelectedNodeIds = sel, BoxSelection = null };
                    }
                    _dragStartModelSnapshot = null;
                    break;
                }
                case DragMode.MoveNodes:
                {
                    // 落点时:OnMouseMove 一路写新位置,这里只决定要不要入 undo 栈
                    // 顺手清掉对齐辅助线,免得拖完留在画布上
                    CommitDrag(DataSource.State with { AlignmentGuides = null });
                    _dragStartPositions = null;
                    break;
                }
            }
            _drag = DragMode.None;
            _isMinimapDrag = false;
            chart.ReleaseMouseCapture();
            e.Handled = true;
        }

        // 拖拽落点统一收口:跟拖前 snapshot 比对,模型有变化才入栈,避免"按下没动"也产生 undo 条目。
        // newState 就是当前 DataSource.State(MoveNodes 已经被 OnMouseMove 实时更新)或者刚拼好的新态(RubberEdge)。
        private void CommitDrag(GraphState newState)
        {
            var snap = _dragStartModelSnapshot;
            _dragStartModelSnapshot = null;
            if (snap != null)
            {
                DataSource.PushUndoIfChanged(snap);
            }
            DataSource.State = newState;
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            var s = DataSource.State;
            if (s.HoveredPort != null)
                DataSource.State = s with { HoveredPort = null };
        }

        private void OnLostCapture(object sender, MouseEventArgs e)
        {
            if (_drag == DragMode.None) return;
            _drag = DragMode.None;
            _isMinimapDrag = false;
            _dragStartPositions = null;
            _dragStartModelSnapshot = null;
            _rubberFromNodeId = null; _rubberFromPortId = null;
            var s = DataSource.State;
            if (s.RubberBand != null || s.BoxSelection != null || s.AlignmentGuides != null)
                DataSource.State = s with { RubberBand = null, BoxSelection = null, AlignmentGuides = null };
        }

        // ──────────────────────────────────────────
        // 触屏 Manipulation —— 复用 ChartInteractionFeature 的思路:
        //   Starting → 配 Mode + Container;Delta → 翻译 Translation → pan,Scale → zoom 围绕 Origin。
        //   chart 那边走 board / Viewport 反应式,这里 graph 是像素空间直接改 Transform,算法等价但状态层不同。
        // ──────────────────────────────────────────
        private void OnManipulationStarting(object? sender, ManipulationStartingEventArgs e)
        {
            var chart = Chart;
            if (chart == null) return;
            // Container 必须显式给到 ChartCell —— 默认 manipulation origin 跑到 visual root 偏移失真。
            e.ManipulationContainer = chart;
            e.Mode = ManipulationModes.Translate | ManipulationModes.Scale;
        }

        private void OnManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
        {
            var chart = Chart;
            if (chart == null) return;
            var d  = e.DeltaManipulation;
            var s  = DataSource.State;
            var tr = s.Transform;

            // 1. 平移:DeltaManipulation.Translation 已经是相对前一帧的累计(屏幕像素),直接加到 Offset。
            float ox = tr.OffsetX + (float)d.Translation.X;
            float oy = tr.OffsetY + (float)d.Translation.Y;

            // 2. 缩放:跟 OnMouseWheel 同款公式,只是缩放因子来自 d.Scale.X(捏合)而非 wheel delta。
            //    保持 ManipulationOrigin 下的画布点不动:newOffset = origin - canvasPt * newScale
            float scale = tr.Scale;
            float deltaScale = (float)d.Scale.X;
            if (Math.Abs(deltaScale - 1f) > 1e-4f)
            {
                float newScale = Math.Clamp(scale * deltaScale, 0.2f, 4f);
                if (Math.Abs(newScale - scale) > 1e-6f)
                {
                    var origin = e.ManipulationOrigin;
                    // ScreenToCanvas 用的是当前 Transform(平移已合进 ox/oy 但 Scale 还是 tr.Scale),
                    // 这里手算 canvasPt 跟 ScreenToCanvas 等价但避开半成品 Transform。
                    float canvasX = (float)((origin.X - tr.OffsetX) / scale);
                    float canvasY = (float)((origin.Y - tr.OffsetY) / scale);
                    ox = (float)origin.X - canvasX * newScale;
                    oy = (float)origin.Y - canvasY * newScale;
                    scale = newScale;
                }
            }

            if (ox == tr.OffsetX && oy == tr.OffsetY && scale == tr.Scale) return;
            DataSource.State = s with { Transform = new CanvasTransform(ox, oy, scale) };
            e.Handled = true;
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var chart = Chart;
            if (chart == null) return;
            var screen = Pt(e, chart);
            var s = DataSource.State;
            var canvasUnderMouse = s.Transform.ScreenToCanvas(screen);

            float factor = e.Delta > 0 ? 1.15f : 1f / 1.15f;
            float newScale = Math.Clamp(s.Transform.Scale * factor, 0.2f, 4f);

            // 保持鼠标下的画布点不动:newOffset = screen - canvasPt * newScale
            float ox = screen.X - canvasUnderMouse.X * newScale;
            float oy = screen.Y - canvasUnderMouse.Y * newScale;
            DataSource.State = s with { Transform = new CanvasTransform(ox, oy, newScale) };
            e.Handled = true;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z 统一在这里拦,优先于 Delete。
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (ctrl && (e.Key == Key.Z) && !shift) { DataSource.Undo(); e.Handled = true; return; }
            if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && shift))) { DataSource.Redo(); e.Handled = true; return; }

            var s = DataSource.State;
            if (e.Key == Key.Delete && s.SelectedNodeIds.Count > 0)
            {
                var next = s;
                foreach (var id in s.SelectedNodeIds.ToArray()) next = next.WithoutNode(id);
                DataSource.PushUndoAndApply(next with { SelectedNodeIds = new HashSet<string>() });
                e.Handled = true;
            }
        }

        // hover 检测:命中端口才更新 HoveredPort,空白时清掉。
        // 用 ReferenceEquals 短路避免每次 mouse-move 都 SetState (即使 HoveredPort 没变也会重画 preview 层)。
        private void UpdateHoveredPort(GraphState s, HevoPoint canvasPt)
        {
            const float r = 8f;
            HoveredPort? next = null;
            for (int i = s.Nodes.Count - 1; i >= 0; i--)
            {
                var node = s.Nodes[i];
                foreach (var p in node.OutputPorts)
                {
                    var c = node.GetPortPosition(p);
                    if (Distance(canvasPt, c) <= r)
                    {
                        var screen = s.Transform.CanvasToScreen(c);
                        next = new HoveredPort(node.Id, p.Id, false, screen);
                        goto done;
                    }
                }
                foreach (var p in node.InputPorts)
                {
                    var c = node.GetPortPosition(p);
                    if (Distance(canvasPt, c) <= r)
                    {
                        var screen = s.Transform.CanvasToScreen(c);
                        next = new HoveredPort(node.Id, p.Id, true, screen);
                        goto done;
                    }
                }
            }
            done:
            // 同样的 hover 状态不重发 (按 tuple 等价比较)
            if (HoverEquals(s.HoveredPort, next)) return;
            DataSource.State = s with { HoveredPort = next };
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

        /// <summary>
        /// 落在 minimap 浮窗内 → 立即把视口中心移到点击位对应的画布点;返回 true 表示被本路径接管。
        /// </summary>
        private bool TryHandleMinimapClick(HevoPoint screen, GraphState s)
        {
            var chart = Chart;
            if (chart == null) return false;
            if (!ComputeMinimapMapping(chart, s, Geometry, out var mx, out var my, out var ox, out var oy, out var scale, out var winW, out var winH)) return false;
            // 点击是否落在 minimap 浮窗
            var rect = new HevoRect(mx, my, Geometry.Width, Geometry.Height);
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
            var chart = Chart;
            if (!_isMinimapDrag || chart == null) return false;
            float winW = (float)chart.ActualWidth;
            float winH = (float)chart.ActualHeight;
            CenterViewportOnMinimapClick(screen, _minimapDragOx, _minimapDragOy, _minimapDragScale, winW, winH);
            return true;
        }

        private void CenterViewportOnMinimapClick(HevoPoint screen, float ox, float oy, float scale, float winW, float winH)
        {
            // 屏幕点(在 minimap 内) → 画布坐标:canvasPt = (screenPt - (ox, oy)) / scale
            float canvasX = (screen.X - ox) / scale;
            float canvasY = (screen.Y - oy) / scale;
            // 让该画布点出现在 chart cell 的中心:Transform.Offset = winSize/2 - canvasPt * scale_view
            var s = DataSource.State;
            var tr = s.Transform;
            float newOffsetX = winW / 2f - canvasX * tr.Scale;
            float newOffsetY = winH / 2f - canvasY * tr.Scale;
            DataSource.State = s with { Transform = new CanvasTransform(newOffsetX, newOffsetY, tr.Scale) };
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

        private static bool PortsCompatible(GraphState s, string fromNodeId, string fromPortId, Node toNode, Port toPort)
        {
            var fromNode = s.FindNode(fromNodeId);
            if (fromNode == null) return false;
            var fromPort = fromNode.FindPort(fromPortId, isInput: false);
            if (fromPort == null) return false;
            return GraphTypeCompatibility.AreCompatible(fromPort.DataTypeName, toPort.DataTypeName, toPort.IsArray);
        }
    }
}
