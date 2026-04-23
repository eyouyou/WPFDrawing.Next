using Hevo.Charting.LowCode;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 💥 动态 Schema（Phase 10 / plan §G）
    ///
    /// 空壳 <see cref="ReactiveSchema"/>：不预置任何 DataFlow / Feature，供业务在运行期通过
    /// <see cref="ReactiveSchema.Transact"/> 与 <see cref="ReactiveSchema.Own{T}"/> 动态装配。
    ///
    /// 适用：自选股横评、概念板块对比、策略叠加等需要"运行期加减数据源/Feature"的业务。
    /// 不适用：固定业务（单源 FinancingBalance、K 线等），直接继承 ReactiveSchema 写专属 schema 更合适。
    ///
    /// 极简门面原则：不引入任何新的数据流机制。
    ///   - 多源时间对齐 → <see cref="WorkFlow.TimeAxisCoordinator"/>（§B.1）
    ///   - UI 事务原子性 → 基类 <see cref="ReactiveSchema.Transact"/>
    ///   - 资源生命周期 → 基类 <see cref="ReactiveSchema.Own{T}"/>
    /// </summary>
    public sealed class DynamicSchema : ReactiveSchema
    {
        // 占位触发器：BindTo 需要一个 IWorkflow 源，但动态 schema 不预置管线。
        // 用户通过 Transact 动态装配 Feature 后，数据流由各 Feature（如 TimeAxisCoordinator.BindTo）接管。
        private readonly WorkflowTrigger<DataBlackboard> _idleTrigger = new();

        protected override void DefineDataFlow(ChartCell chart)
            => _idleTrigger.BindTo(chart);

        protected override void DefineFeatures(IFeatureContext canvas) { }
    }
}
