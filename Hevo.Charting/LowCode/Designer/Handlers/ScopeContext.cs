using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Hevo.Charting.LowCode.Designer.Handlers
{
    /// <summary>
    /// <see cref="IScopeContext"/> 默认实现 —— 三层缓存:进程级 Singleton / 实例级 Scoped / 节点级 PerNode。
    /// <para>
    /// Singleton 池走静态 <see cref="ConcurrentDictionary{TKey,TValue}"/>,跨进程内所有 ScopeContext 共享。
    /// Scoped/PerNode 跟实例同生灭,Dispose 时把 IDisposable 实例释放掉。
    /// </para>
    /// <para>
    /// Cache key 不是 Type,而是 <c>(handlerName, ?nodeId)</c> 复合:
    /// 同一宿主类的两个 handler 名各持一份实例(模式 3:keyed services with different lifetimes)。
    /// </para>
    /// </summary>
    public sealed class ScopeContext : IScopeContext
    {
        // 进程级:key = handlerName。同类不同 handlerName 各一份;不同类同 handlerName 概率上不会撞(handlerName 全局唯一约束)。
        private static readonly ConcurrentDictionary<string, object> _singletons = new(StringComparer.Ordinal);

        private readonly Dictionary<string, object> _scoped  = new(StringComparer.Ordinal);
        private readonly Dictionary<string, object> _perNode = new(StringComparer.Ordinal);

        // ctor DI 用的 service map。复用 SmartActivator.CreateInstance(Type, services) overload。
        private readonly Dictionary<Type, object> _services = new();

        public IReadOnlyDictionary<Type, object> Services => _services;

        public void AddService(Type serviceType, object instance)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            _services[serviceType] = instance;
        }

        public void RegisterInstance<T>(T instance) where T : class
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            _singletons[typeof(T).FullName ?? typeof(T).Name] = instance;
        }

        public object GetOrCreate(Type ownerType, NodeLifetime lifecycle, string handlerName, string? nodeId = null)
        {
            if (ownerType == null) throw new ArgumentNullException(nameof(ownerType));
            if (string.IsNullOrEmpty(handlerName)) throw new ArgumentNullException(nameof(handlerName));

            switch (lifecycle)
            {
                case NodeLifetime.Singleton:
                    // Singleton 池是 process-wide,handlerName 当 key —— 同 handler 名跨多个 ScopeContext 共享
                    // (跟 .NET DI Singleton 一致:整个进程一份)。
                    return _singletons.GetOrAdd(handlerName, _ => Activate(ownerType));

                case NodeLifetime.Scoped:
                    if (!_scoped.TryGetValue(handlerName, out var sc))
                    {
                        sc = Activate(ownerType);
                        _scoped[handlerName] = sc;
                    }
                    return sc;

                case NodeLifetime.PerNode:
                    // nodeId == null 等价 Singleton 维度(Cascade/Trigger driver 在 edge 上,通常自带复合 Id;
                    // 没传就退化成"该 handlerName 一份"避免空指针)。
                    var key = nodeId == null ? handlerName : $"{handlerName}:{nodeId}";
                    if (!_perNode.TryGetValue(key, out var pn))
                    {
                        pn = Activate(ownerType);
                        _perNode[key] = pn;
                    }
                    return pn;

                case NodeLifetime.Transient:
                    return Activate(ownerType);

                default:
                    throw new ArgumentOutOfRangeException(nameof(lifecycle));
            }
        }

        // Activator 钩到 SmartActivator 的 ctor-DI overload 上 —— ctor 形参按类型从 _services 取,
        // 跟 UriArgs.RequireService<T> 同源(framework 在 Configure / Apply 阶段把 ChartCell/Canvas 等放进来)。
        // 调 SmartActivator 而不是 Activator.CreateInstance,因为后者只支持无参 / 反射匹配,无 service 概念。
        private object Activate(Type type)
            => SmartActivator.CreateInstance(type, _services);

        public void Dispose()
        {
            foreach (var v in _scoped.Values)  (v as IDisposable)?.Dispose();
            foreach (var v in _perNode.Values) (v as IDisposable)?.Dispose();
            _scoped.Clear();
            _perNode.Clear();
            // Singleton 池跟 .NET DI 一致:不在 Dispose 里清,跟随进程退出。
            // 真有需要(测试隔离 / 热重载),调 ScopeContext.ClearSingletons() 显式清。
        }

        /// <summary>测试 / 热重载用 —— 显式清掉进程级 Singleton 池。生产代码不该调。</summary>
        public static void ClearSingletons()
        {
            foreach (var v in _singletons.Values) (v as IDisposable)?.Dispose();
            _singletons.Clear();
        }
    }
}
