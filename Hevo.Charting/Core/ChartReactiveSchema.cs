namespace Hevo.Charting.Core
{
    /// <summary>
    /// chart-specific 的 <see cref="ReactiveSchema"/> 中间层。
    /// <para>
    /// <b>架构定位</b>:framework 通过继承层次明确"chart 业务 schema 跟通用 schema"的契约 ——
    /// </para>
    /// <list type="bullet">
    ///   <item><see cref="ReactiveSchema"/>:通用反应式 schema 框架,**不知 viewport**(graph editor 等
    ///         不需要 viewport 的 schema 直接继承它);</item>
    ///   <item><see cref="ChartReactiveSchema"/>(本类):chart-specific 中间层,
    ///         <see cref="InitializeRegistry"/> 强制 ensure <see cref="Features.ViewportPortsFeature"/> ——
    ///         任何继承本类的 chart schema 永远有 viewport ports 就位,业务侧 helper 调用顺序自由;</item>
    ///   <item>业务 chart schema(KLineSchema / AttentionSchema 等):继承本类,自动获得 viewport ports ensure;
    ///         无需在 DefineFeatures 内自助 add(SetupViewport helper 内部 IsSingleton 替换语义不破坏 framework 实例)。</item>
    /// </list>
    /// <para>
    /// <b>跟"feature 借 viewport 的端口又跟 viewport 无关"问题的关系</b>:
    /// chart schema 继承本类后,framework 保证 PortsFeature 在,任何 ChartFeature 通过 base.Viewport 取 ports
    /// 永远成功。如果某 feature 概念上跟 viewport 无关却需要其中某些 port(如 LogicalLength),业务有两种选择:
    /// </para>
    /// <list type="number">
    ///   <item>feature 继承 <see cref="Feature"/> 基类(不是 ChartFeature),init 接它需要的 specific port,
    ///         业务侧显式提供 —— 表达 "我跟 chart viewport 无关,只是借端口";</item>
    ///   <item>feature 继承 <see cref="ChartFeature"/>,base.Viewport 隐式取 chart 上下文的 ports —— 表达
    ///         "我是 chart 业务一部分,跟 viewport 同上下文"(典型:UniversalHeaderFeature 借 LogicalLength 做数据脉冲)。</item>
    /// </list>
    /// </summary>
    public abstract class ChartReactiveSchema : ReactiveSchema
    {
        /// <summary>
        /// chart-specific ensure:在 SetupLayout / DefineFeatures 之前 add 一份
        /// <see cref="Features.ViewportPortsFeature"/> —— 让继承本类的 chart 业务 schema 永远有 viewport
        /// ports 就位,业务侧 helper 链(SetupUniversalHeader / AddDomainAxis 等)调用顺序自由,
        /// 不依赖 SetupViewport 必须最先。
        /// <para>
        /// 业务侧 SetupViewport helper 不再 Add PortsFeature(否则 IsSingleton 替换破坏 framework 实例),
        /// 只 Add VPM 配置钳制策略 —— 见 <see cref="FeatureCanvasScopedExtensions.SetupViewport"/>。
        /// </para>
        /// </summary>
        protected override void EnsureBaseFeatures()
        {
            base.EnsureBaseFeatures();
            this.Add(new Features.ViewportPortsFeature());
        }
    }
}
