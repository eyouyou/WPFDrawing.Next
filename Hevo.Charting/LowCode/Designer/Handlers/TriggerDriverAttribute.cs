using System;

namespace Hevo.Charting.LowCode.Designer.Handlers
{
    /// <summary>
    /// Phase 3 触发器绑定 verb=<c>SwitchContext</c> / <c>Request</c> 时的"参数函数"attribute ——
    /// 给纯函数贴这个,声明它是 <see cref="TriggerBinding.Driver"/> 可用的命名 driver。
    /// <para>
    /// 签名约定:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>SwitchContext</c> verb:<c>(TDs ds, VersionToken tick) → TContext</c></item>
    ///   <item><c>Request</c> verb:<c>(TDs ds, VersionToken tick) → TRequest</c></item>
    /// </list>
    /// <para>
    /// 跟 <see cref="ContextDriverAttribute"/> 共享同一份 <see cref="BlueprintHandlerRegistry"/> 存储,
    /// 装配期 <see cref="BlueprintTriggerBindingWiring"/> 按 driver 名查表 + 强类型校验返回值匹配下游 verb 期望。
    /// </para>
    /// <example>
    /// <code>
    /// [TriggerDriver("kline_incremental_demand")]
    /// public static KLineDemand BuildIncrementalDemand(KLineDataSource ds, VersionToken tick)
    /// {
    ///     var snap = ds.GetSnapshot();
    ///     return (snap.Count == 0 || snap.AsSpan()[0].Count == 0)
    ///         ? KLineDemand.Latest(200)
    ///         : KLineDemand.Latest(5);
    /// }
    /// </code>
    /// </example>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class TriggerDriverAttribute : Attribute
    {
        /// <summary>蓝图 <see cref="TriggerBinding.Driver"/> 引用名。</summary>
        public string Name { get; }

        /// <summary>方法级 lifecycle 覆盖,语义跟 <see cref="ContextDriverAttribute.Lifecycle"/> 一致。</summary>
        public NodeLifetime Lifecycle { get; set; } = NodeLifetime.Inherit;

        public TriggerDriverAttribute(string name) => Name = name;
    }
}
