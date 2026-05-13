using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// 状态变化通知签名(主要用于属性面板 / 脏标记 / 串行化按钮启用判断)。
    /// </summary>
    public delegate void GraphStateChanged(GraphState newState, GraphState oldState);

    /// <summary>
    /// graph editor 的 source-of-truth(2026-05 单值 DataSource 重构)。
    /// <para>
    /// <b>架构定位</b>:跟 chart 侧 <see cref="KLineDataSource"/> / <see cref="MarketScannerDataSource"/> 等量齐观 ——
    /// 直继承 <see cref="DataSource{TSource, T}"/>(T = <see cref="GraphState"/>),走 framework
    /// 标准单值 publish 协议,Stream 类型直接是 <c>IWorkflow&lt;GraphState&gt;</c>,不再自管 board。
    /// </para>
    /// <para>
    /// <b>跟 chart cell 联动</b>:旧版 BlueprintDataSource 自持 <c>DataBlackboard + DataPort&lt;GraphState&gt; + WorkflowTrigger&lt;DataBlackboard&gt;</c>
    /// 把 board "前置"到 DS。重构后 board 归属上移到 <see cref="GraphEditorSchema"/>,DS 只管 publish state;
    /// schema 在 DefineDataFlow 内订阅 <see cref="DataSource{TSource,T}.Stream"/> 桥接到自己的 board + 主流。
    /// 这跟 KLineSchema 的 <c>_dataSource.Pipe().BindTo(chart)</c> 形态对仗 —— DS 不知道 chart 存在。
    /// </para>
    /// <para>
    /// <b>编辑器特有</b>:Undo/Redo 栈、<see cref="ApplyUserEdit"/> 入口、<see cref="StateChanged"/> 事件 ——
    /// 都是 graph editor 专属,不进 framework 单值 DS 基类。
    /// </para>
    /// </summary>
    public sealed class BlueprintDataSource : DataSource<BlueprintDataSource, GraphState>
    {
        public BlueprintDataSource()
        {
            // 初始 publish 一次 Empty —— 跟旧版 _state 字段默认 GraphState.Empty 行为对齐,
            // 让 Current 非 null,subscribe 在 Stream 上的消费者一定能拿到首屏。
            Publish(GraphState.Empty);
        }

        /// <summary>
        /// 外部读写当前状态。
        /// <para>
        /// get:同步快读 <see cref="DataSource{TSource,T}.Current"/>(单值 DS 永远非 null —— ctor 已 publish 过 Empty)。<br/>
        /// set:等价 <see cref="ApplyUserEdit"/>(...) 的 mutate=_=>value 形态,但<b>不入 Undo 栈</b> ——
        /// 适用于初始化 / 反序列化恢复 / 外部强制重置场景。
        /// </para>
        /// </summary>
        public GraphState State
        {
            get => Current ?? GraphState.Empty;
            set => SetState(value);
        }

        /// <summary>状态变化时触发(用于属性面板 / 脏标记 / JSON 预览刷新等)。</summary>
        public event GraphStateChanged? StateChanged;

        // ==========================================
        //  Undo / Redo
        // ==========================================
        private readonly Stack<GraphState> _undoStack = new();
        private readonly Stack<GraphState> _redoStack = new();
        private const int UndoLimit = 100;

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        /// <summary>
        /// 用户级编辑统一入口:NodeEditorWindow 改属性、Picker 加节点、清空画布,都走这里。
        /// 把当前 model snapshot 入 undo 栈、清空 redo 栈、套 mutate 拿新态、SetState。
        /// </summary>
        public void ApplyUserEdit(Func<GraphState, GraphState> mutate)
        {
            if (mutate == null) throw new ArgumentNullException(nameof(mutate));
            var current = State;
            var next = mutate(current);
            if (!ModelChanged(current, next)) { SetState(next); return; }
            PushSnapshotToUndo(current);
            SetState(next);
        }

        public void Undo()
        {
            if (_undoStack.Count == 0) return;
            var prev = _undoStack.Pop();
            var carryView = MergeViewOnly(prev, State);
            _redoStack.Push(State);
            SetState(carryView);
        }

        public void Redo()
        {
            if (_redoStack.Count == 0) return;
            var next = _redoStack.Pop();
            var carryView = MergeViewOnly(next, State);
            _undoStack.Push(State);
            SetState(carryView);
        }

        /// <summary>
        /// schema 内部的编辑路径(键盘删除 / 拖拽落点)统一通过此方法入栈 + 应用。
        /// 比较模型变化决定是否真入栈(0 位移拖拽不污染历史)。
        /// </summary>
        public void PushUndoAndApply(GraphState newState)
        {
            var current = State;
            if (!ModelChanged(current, newState)) { SetState(newState); return; }
            PushSnapshotToUndo(current);
            SetState(newState);
        }

        /// <summary>
        /// 拖拽落点统一收口入栈:跟拖前 snapshot 比对,模型有变化才入栈。
        /// </summary>
        public void PushUndoIfChanged(GraphState snapshot)
        {
            if (ModelChanged(snapshot, State))
            {
                PushSnapshotToUndo(snapshot);
            }
        }

        private void PushSnapshotToUndo(GraphState snapshot)
        {
            _undoStack.Push(snapshot);
            _redoStack.Clear();
            if (_undoStack.Count > UndoLimit)
            {
                var keep = _undoStack.Take(UndoLimit).ToArray();
                _undoStack.Clear();
                for (int i = keep.Length - 1; i >= 0; i--) _undoStack.Push(keep[i]);
            }
        }

        // 比较两个 GraphState 的"模型部分"是否变化。with{ Nodes = newList } 一定换引用,
        // 所以纯 view 改动(Transform / SelectedNodeIds 等)不会误判。
        private static bool ModelChanged(GraphState a, GraphState b)
            => !ReferenceEquals(a.Nodes, b.Nodes) || !ReferenceEquals(a.Edges, b.Edges);

        // 把 source 的模型(Nodes/Edges)与 viewKeeper 的 view(Transform/SelectedNodeIds 等) 合并。
        // Undo/Redo 还原历史模型时,保留用户当前视口位置,体验更连贯。
        private static GraphState MergeViewOnly(GraphState source, GraphState viewKeeper)
            => source with
            {
                Transform = viewKeeper.Transform,
                SelectedNodeIds = new HashSet<string>(),
                RubberBand = null,
                BoxSelection = null,
                HoveredPort = null,
            };

        // ==========================================
        //  状态推送内核 —— 走基类 Publish 推到 Stream<GraphState>;board 桥接由 schema 负责。
        // ==========================================
        private void SetState(GraphState newState)
        {
            var old = State;
            if (ReferenceEquals(newState, old)) return;
            StateChanged?.Invoke(newState, old);
            Publish(newState);
        }
    }
}
