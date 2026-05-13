using System;
using System.Collections.Generic;

namespace Hevo.Charting.LowCode.Designer.Handlers
{
    /// <summary>
    /// Handler 实例池的"作用域"抽象 —— 三层缓存(进程级 Singleton / 蓝图级 Scoped / 节点级 PerNode),
    /// 加上每次新建的 Transient。<see cref="BlueprintHandlerRegistry.Resolve(string, IScopeContext, string?)"/>
    /// 走这个接口拿 handler 宿主实例。
    /// <para>
    /// 一个 <see cref="IScopeContext"/> 实例的生命周期 = 一份 ChartBlueprint Apply 周期。Dispose 时
    /// 清理 Scoped/PerNode 缓存(Singleton 池进程级,跟随宿主 shutdown)。
    /// </para>
    /// </summary>
    public interface IScopeContext : IDisposable
    {
        /// <summary>
        /// 按 lifecycle 拿到 handler 宿主类的实例。framework 装配 Cascade / Trigger / Feature handler 时调用。
        /// </summary>
        /// <param name="ownerType">handler 所在类(<see cref="BlueprintHandlerRegistry.AutoDiscoverType"/> 注册过)。</param>
        /// <param name="lifecycle">解析优先级:method-level &gt; class-level &gt; 默认 Singleton。</param>
        /// <param name="handlerName">handler 注册名 —— Singleton/Scoped 池的 cache key。
        ///     同类的两个 handler 名走两条独立池(模式 3:keyed services with different lifetimes)。</param>
        /// <param name="nodeId">PerNode 路径的节点 Id,Cascade/Trigger 自身约定为 <c>cascade:{From}-&gt;{To}</c>
        ///     等复合 Id;Feature 节点直接传 FeatureId。null 等价 Singleton 行为。</param>
        object GetOrCreate(Type ownerType, NodeLifetime lifecycle, string handlerName, string? nodeId = null);

        /// <summary>
        /// 测试 / 集成预注入用 —— 把外部构造好的 instance 直接挂进 Singleton 池,绕开 <see cref="GetOrCreate"/>
        /// 内部 Activator。同名(typeof(T) 维度)二次注入会覆盖。
        /// </summary>
        void RegisterInstance<T>(T instance) where T : class;

        /// <summary>
        /// 按类型注入跨 handler 共享的 service(典型:<c>ChartCell</c> / <c>BlueprintCanvas</c>)。
        /// handler 宿主类 ctor 形参按类型从这里取(等价 <c>UriArgs.RequireService&lt;T&gt;</c> 同款机制)。
        /// </summary>
        void AddService(Type serviceType, object instance);

        /// <summary>service dict 视图(由 Activator 路径回查)。</summary>
        IReadOnlyDictionary<Type, object> Services { get; }
    }
}
