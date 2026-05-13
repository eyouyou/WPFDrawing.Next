using System;

namespace Hevo.Charting.LowCode.Designer.Handlers
{
    /// <summary>
    /// Phase 2 级联边的"投影函数" attribute —— 给纯函数贴这个,声明它是 <see cref="CascadeEdge.ContextDriver"/>
    /// 可用的命名 driver。
    /// <para>
    /// 签名约定:<b>第一参 = 上游 DS 强类型,后续参数 = 触发 payload</b>(<see cref="Hevo.Charting.Core.DataSnapshot{T}"/>
    /// for <c>"Stream"</c> trigger / <c>EventArgs</c> for Feature 事件 trigger),<b>返回 = 下游 TContext</b>。零 UI 副作用,可单测。
    /// </para>
    /// <example>
    /// <code>
    /// [ContextDriver("list_to_scanner_ctx")]
    /// public static MarketScannerContext FromList(Composite&lt;Security&gt; _, DataSnapshot&lt;Security&gt; snap)
    ///     =&gt; new MarketScannerContext(snap.Items.Take(snap.Count).ToList());
    /// </code>
    /// </example>
    /// <para>
    /// 实现上,Driver 跟 <see cref="BlueprintHandlerAttribute"/> 共享一份 <see cref="BlueprintHandlerRegistry"/>:
    /// 同一注册表 / 同一 Resolve / 同一 lifecycle 三件套,蓝图侧引用名跟 BlueprintHandler 一样裸 string,
    /// AutoDiscoverStatic / AutoDiscoverType 都能扫到。<see cref="BlueprintCascadeWiring"/> 装配期按 driver 名查表 + 强类型校验。
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class ContextDriverAttribute : Attribute
    {
        /// <summary>蓝图 <see cref="CascadeEdge.ContextDriver"/> 引用名。</summary>
        public string Name { get; }

        /// <summary>
        /// 方法级 lifecycle 覆盖。<see cref="NodeLifetime.Inherit"/>(默认)= 跟随类级 <see cref="BlueprintNodeAttribute"/>。
        /// 静态 driver(无宿主)始终 Singleton-like,此字段忽略;<see cref="AutoDiscoverType"/> 扫的实例 driver 才走 lifecycle 池。
        /// </summary>
        public NodeLifetime Lifecycle { get; set; } = NodeLifetime.Inherit;

        public ContextDriverAttribute(string name) => Name = name;
    }
}
