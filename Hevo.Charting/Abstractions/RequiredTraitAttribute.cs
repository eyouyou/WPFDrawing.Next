namespace Hevo.Charting.Abstractions
{
    /// <summary>
    /// 标记 Layer 必须消费的 trait。Layer 首次 OnUpdate 时引擎会校验所有 Required trait 是否已被 Feature 发布,
    /// 缺失即 fail-fast 抛出指明 Layer 名 + 缺失 trait 名,避免静默"画不出来"。
    ///
    /// 使用约定:
    /// - 仅声明真正"无它则不能画"的 trait(如 CandleData / PlotAreaTrait)。
    /// - 有 fallback 默认值的(如 CandleStyle ?? CandleStyle.Default)不要标注。
    /// - 与 IConsumes&lt;T&gt; 是两套语义:IConsumes 由生成器扫描出"读过哪些 trait",可能含可选;
    ///   RequiredTrait 是开发者明确声明的"硬依赖"。
    ///
    /// 示例:
    /// <code>
    /// [RequiredTrait(typeof(CandleData))]
    /// [RequiredTrait(typeof(PlotAreaTrait))]
    /// public partial class CandleLayer : ChartLayer { ... }
    /// </code>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public sealed class RequiredTraitAttribute : Attribute
    {
        public Type TraitType { get; }
        public RequiredTraitAttribute(Type traitType)
        {
            TraitType = traitType;
        }
    }
}
