namespace Hevo.Charting.LowCode.Designer.Handlers
{
    /// <summary>
    /// Handler 实例的生命周期粒度,跟 IoC 容器同概念。
    /// 用在 <see cref="BlueprintNodeAttribute"/> / <see cref="BlueprintHandlerAttribute.Lifecycle"/> 上,
    /// 由 <see cref="IScopeContext"/> 三层缓存兑现。
    /// <para>
    /// 决定优先级:method-level <c>[BlueprintHandler(Lifecycle=...)]</c> &gt; class-level <c>[BlueprintNode]</c> &gt; 默认 <see cref="Singleton"/>。
    /// 同方法多个 <c>[BlueprintHandler]</c>(AllowMultiple)各自独立 lifecycle —— 等价 IoC 的 keyed services with different lifetimes。
    /// </para>
    /// </summary>
    public enum NodeLifetime
    {
        /// <summary>
        /// 哨兵值,仅给 <see cref="BlueprintHandlerAttribute.Lifecycle"/> 当默认 —— 表示"方法级未指定,跟随类级 <see cref="BlueprintNodeAttribute"/>"。
        /// C# 属性参数不支持 <c>Nullable&lt;TEnum&gt;</c>(CS0655),用枚举哨兵代替 nullable。
        /// 类级 <see cref="BlueprintNodeAttribute.Lifecycle"/> 不应使用此值(没有上层可继承,装配期当成 Singleton 处理)。
        /// </summary>
        Inherit = 0,

        /// <summary>整个进程共享一个实例(等价旧 static handler 行为)。无状态 / 纯函数 handler 默认。</summary>
        Singleton,

        /// <summary>一个 ChartBlueprint 实例期内共享(从 Apply 到 schema dispose)。跨节点共享的本地缓存。</summary>
        Scoped,

        /// <summary>一个 Feature/Driver 节点一份实例。有状态指标(EMA/RSI 累加器、滚动窗口)用。</summary>
        PerNode,

        /// <summary>每次 Resolve 都新建。测试 / 极特殊场景。</summary>
        Transient,
    }
}
