using System;

namespace Hevo.Charting.LowCode.Designer.Handlers
{
    /// <summary>
    /// 给 handler 宿主类贴这个 attribute,声明类级默认 <see cref="NodeLifetime"/>。
    /// 类内所有 <see cref="BlueprintHandlerAttribute"/> 方法默认继承该 lifecycle;
    /// 方法级 <c>[BlueprintHandler(Lifecycle=...)]</c> 可覆盖。
    /// <para>
    /// 仅对 <see cref="BlueprintHandlerRegistry.AutoDiscoverType"/>(framework 接管实例创建)路径生效。
    /// 走 <see cref="BlueprintHandlerRegistry.AutoDiscover"/>(用户传 instance)的旧路径不读这个属性 ——
    /// 那条路径下实例由调用方持有,lifecycle 等价"用户托管 Singleton"。
    /// </para>
    /// <example>
    /// <code>
    /// [BlueprintNode(Lifecycle = NodeLifetime.PerNode)]
    /// public class EmaIndicator
    /// {
    ///     private readonly EmaAccumulator _acc = new();   // 每个引用节点一份
    ///
    ///     [BlueprintHandler("ema")]                       // 继承类级 → PerNode
    ///     public ReadOnlyMemory&lt;double&gt; Compute(...) { ... }
    /// }
    /// </code>
    /// </example>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class BlueprintNodeAttribute : Attribute
    {
        /// <summary>类级默认 lifecycle。未设置 → <see cref="NodeLifetime.Singleton"/>。</summary>
        public NodeLifetime Lifecycle { get; set; } = NodeLifetime.Singleton;
    }
}
