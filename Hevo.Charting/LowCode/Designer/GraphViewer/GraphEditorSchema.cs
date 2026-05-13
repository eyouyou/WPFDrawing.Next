using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// Graph editor schema(2026-05 单值 DataSource 重构后)。
    /// <para>
    /// <b>架构定位</b>:跟 <c>KLineSchema</c> / <c>AttentionSchema</c> 等量齐观的 ReactiveSchema 子类。
    /// 持有一份 <see cref="BlueprintDataSource"/>(纯 single-value <c>DataSource&lt;_, GraphState&gt;</c>),
    /// schema 负责把 <see cref="DataSource{TSource,T}.Stream"/> 桥接到自己的 board + 主流。
    /// </para>
    /// <para>
    /// <b>Board 归属</b>:旧版 board 前置在 BlueprintDataSource 内;重构后 board 归 schema,
    /// DS 不知 chart cell 存在 —— 跟 chart 侧 <c>_ds.Pipe().BindTo(chart)</c> 形态对仗。
    /// </para>
    /// <para>
    /// <b>消费者用法</b>:
    /// <code>
    /// var schema = new GraphEditorSchema();
    /// schema.DataSource.State = BuildSampleGraph();
    /// schema.DataSource.StateChanged += (n, o) =&gt; ...;
    /// schema.Interaction.NodeEditRequested += node =&gt; ...;
    /// chartHost.Schema = schema;
    /// </code>
    /// </para>
    /// </summary>
    public sealed class GraphEditorSchema : ReactiveSchema
    {
        /// <summary>
        /// graph 状态 source-of-truth。读 <see cref="BlueprintDataSource.State"/>、订阅
        /// <see cref="BlueprintDataSource.StateChanged"/>、调 <see cref="BlueprintDataSource.ApplyUserEdit"/> 都走它。
        /// </summary>
        public BlueprintDataSource DataSource { get; }

        /// <summary>
        /// schema 持有的 GraphState 端口 —— features 通过 init 注入消费,<see cref="DefineDataFlow"/>
        /// 内部把 <see cref="DataSource"/>.Stream 桥接到此 port + board。
        /// </summary>
        public DataPort<GraphState> StatePort { get; } = new("Graph_State");

        /// <summary>
        /// 交互 feature 引用。消费者订阅 <see cref="GraphInteractionFeature.NodeEditRequested"/>
        /// 弹 NodeEditorWindow,免去 Find&lt;T&gt; 反查。跟 chart 侧 ChartInteractionFeature 单类模式对齐。
        /// </summary>
        public GraphInteractionFeature Interaction { get; }

        /// <summary>
        /// Minimap 浮窗几何参数 —— 渲染(<see cref="GraphMinimapFeature"/>)与命中(<see cref="Interaction"/>)
        /// 共享。消费者 ctor 时 init 改尺寸,两端同步生效。
        /// </summary>
        public MinimapGeometry MinimapGeometry { get; init; } = MinimapGeometry.Default;

        // schema 持有的 board + 主流 trigger —— BlueprintDataSource.Stream 桥到这里。
        // ChartCell BindTo 后,推 _board 进 _mainFlow → ReactiveSchema 主流登记 → ProjectAll 标脏 → features OnProject 跑。
        private readonly DataBlackboard _board = new();
        private readonly WorkflowTrigger<DataBlackboard> _mainFlow = new();

        public GraphEditorSchema()
        {
            DataSource = new BlueprintDataSource();
            Interaction = new GraphInteractionFeature { DataSource = DataSource, Geometry = MinimapGeometry };
        }

        protected override void DefineDataFlow(ChartCell chart)
        {
            DataSource.OwnedBy(this);

            // graph editor 的 main flow:
            //   1) 把 DataSource.Current(初始 GraphState.Empty)写到 _board 上的 StatePort,
            //      让首帧 _mainFlow.StartWith(_board) 推出去时,features ctx.UsePort(StatePort) 能立即拿到值。
            //   2) 订阅 DataSource.Stream → 写 board → 推 _mainFlow,后续状态变化按这个路径上推。
            //   3) _mainFlow.StartWith(_board).BindTo(chart):跟 KLineSchema 的 Pipe().BindTo 调用层对齐。
            using (_board.AcquireWriteLock())
            {
                _board.WriteIfChanged(StatePort, DataSource.State);
            }

            var sub = DataSource.Stream.Subscribe(state =>
            {
                using (_board.AcquireWriteLock())
                {
                    _board.WriteIfChanged(StatePort, state);
                }
                _mainFlow.Push(_board);
            });
            Own(sub);

            _mainFlow.StartWith(_board).BindTo(chart);
        }

        protected override void DefineFeatures(IFeatureContext canvas)
        {
            // graph editor 不挂 ViewportManagerFeature —— graph 是 2D CanvasTransform 自由画布,
            // 不走 1D Viewport.UserRange/ActiveRange 协议。SchemaContext 默认 Standalone,
            // 业务侧也不该在 graph schema 上挂依赖 Viewport 的 ChartFeature。

            // 状态消费 features 通过 init port 拿 schema.StatePort,不做 Find<T> 反查 —— 跟 chart 侧
            // LineSeriesFeature { DataPort = ... } 同款"port-as-input"协议(05.md §12.5 偏差 B 修正)。
            var port = StatePort;
            canvas.Add(new GraphCanvasFeature    { StatePort = port });
            canvas.Add(new GraphNodeFeature      { StatePort = port });
            canvas.Add(new GraphEdgeFeature      { StatePort = port });
            canvas.Add(new GraphSelectionFeature { StatePort = port });
            canvas.Add(new GraphPreviewFeature   { StatePort = port });
            canvas.Add(new GraphMinimapFeature   { StatePort = port, Geometry = MinimapGeometry });
            canvas.Add(Interaction);
        }
    }
}
