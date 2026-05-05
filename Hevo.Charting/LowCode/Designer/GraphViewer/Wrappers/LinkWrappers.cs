using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.LowCode.Designer.GraphViewer.Wrappers
{
    // ==========================================
    //  Link DSL 蓝图化包装家族
    //
    //  统一抽象:画布上任何"转换节点"都是 input port → 黑盒 → output port,
    //            底层用什么 (Ingestor / IComputeNode / flow.Watch / ctx.UsePort) 是实现细节。
    //
    //  对应传统 schema 里 _dataSource.Pipe() 那一串 fluent 链:
    //      .Inject(ds => ds.PreClose).ForwardTo(PreClosePort)            ← ScalarMappings + edge 已覆盖
    //      .Inject(ds => ds.LogicalLength).ForwardTo(VP.LogicalLength)   ← 已升级:画布上拖到 Viewport 节点
    //      .LinkStream(cfg => cfg.AutoMap())                             ← VectorMappings 已覆盖
    //      .Compute<S>(s, action)                                        ← 用下面 ComputeNodeFeature 模板
    //
    //  ⚠️ Source Ingestor (ScalarMappings / VectorMappings) 因为依赖 DataSnapshot<TItem>,
    //     必须装在 UniversalDataPipe 的 ingestor 链上才能拿到 snapshot,框架不允许 ChartFeature
    //     直接读 snapshot —— 所以这一类继续走 DataSource.ScalarMappings/VectorMappings 字段,
    //     由 DynamicChartSchema 反射装配。
    //
    //  ✅ Board → Board 的 Compute,直接派 ChartFeature 即可,跟 Series Feature 同体系,
    //     OnProject 里 UsePort 取输入 → 算 → ctx.For(layer 或 Shared).PublishData / board.WriteIfChanged 写输出。
    //
    //  📋 后续模板(按需 copy/扩,~30 行/个,GraphViewer 层闭环,框架不动):
    //   - InjectComputeFeature<TIn, TOut>:port-to-port + lambda(Func 业务子类硬编码)
    //   - ConstantSeederFeature<T>:NodeEditorWindow 改值 → 写到 port
    //   - PortRebroadcastFeature<T>:port 透传(扇出 / 跨 schema 桥)
    // ==========================================

    /// <summary>
    /// 💥 通用 Compute 节点基类 —— 蓝图层的"用户自定义算子"模板。
    /// <para>
    /// 子类的玩法:
    /// <list type="number">
    ///   <item>声明 DataPort&lt;T&gt; 输入属性(默认 Input 方向),DataPort&lt;T&gt; 输出属性贴 <c>[PortDirection(Output)]</c>。</item>
    ///   <item>覆盖 <see cref="OnComputeProject"/>,内部 <c>ctx.UsePort(InputPort)</c> 取值 → 算 → 写出口。</item>
    ///   <item>无视觉层。 GraphViewer 把它当成 Series 类看待(端口可视、可连线、可双击编辑参数)。</item>
    /// </list>
    /// 同 <see cref="UniversalAutoScaleFeature"/>(也算 Compute,只不过把"输出量程"耦合进了视觉链)
    /// 的差异:Compute 节点纯粹 board → board,不挂 layer。
    /// </para>
    /// <para>
    /// 跟 Source Ingestor (ScalarMappings/VectorMappings) 的差异:
    /// 后者直接吃 <see cref="DataSnapshot{TItem}"/>,蓝图侧通过 DataSource.ScalarMappings 反射装 ContextIngestor;
    /// 这个基类吃的是 DataPort<T>,蓝图侧通过 PortBindings 走标准的 Feature 端口焊接路径。
    /// </para>
    /// </summary>
    public abstract class ComputeNodeFeature : ChartFeature
    {
        /// <summary>默认在 Layout 之后、Series 之前(50)跑;子类可重载。</summary>
        public override FeaturePhase Phase => (FeaturePhase)50;

        // 子类自己声明:
        //   public DataPort<T> InputPort { get; init; } = null!;
        //   [PortDirection(PortDirection.Output)]
        //   public DataPort<T> OutputPort { get; init; } = null!;

        protected sealed override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            OnComputeCompose(chart, ctx, flow);
        }

        /// <summary>子类可选重载:订阅 board 写入链(<c>flow.Watch(...)</c>) 等。</summary>
        protected virtual void OnComputeCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) { }

        protected sealed override void OnProject(FeatureContext ctx)
        {
            OnComputeProject(ctx);
        }

        /// <summary>核心:读 input port → 算 → 写 output port。</summary>
        protected abstract void OnComputeProject(FeatureContext ctx);
    }
}
