using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace Hevo.Charting.LowCode.Designer.UriRouting
{
    /// <summary>
    /// 进程级 URI handler 注册表。蓝图节点 <c>Events</c> 字段声明的 URI 模板触发时,通过本类分发到业务 handler。
    /// <para>
    /// <b>注册</b>:业务侧 <c>UriDispatcher.Default.Register("hevo://open/kline_detail", args => ...)</c>
    /// 或更常见 <c>BlueprintCanvas.OnUri(...)</c>(canvas 自动 lifecycle 管理)。
    /// </para>
    /// <para>
    /// <b>路由 key</b>:<c>scheme://authority/path</c>(query 部分进 <see cref="UriArgs"/>)。
    /// 同 key 后注册覆盖前注册(允许 demo 间复用同 URI 干不同事 —— 不推荐但允许)。
    /// </para>
    /// <para>
    /// <b>未注册 URI 触发</b>:<see cref="Dispatch"/> 静默返回 false,业务侧 handler 配错时不会硬抛
    /// (蓝图侧 typo / handler 还没 register 都是合法状态,容错)。需要严格校验时业务侧自行检查。
    /// </para>
    /// </summary>
    public sealed class UriDispatcher
    {
        public static UriDispatcher Default { get; } = new();

        // ConcurrentDictionary 防止业务侧多线程注册时撞车(实际 9 成场景都在 UI 线程,但 0 成本防线)。
        private readonly ConcurrentDictionary<string, Action<UriArgs>> _handlers = new(StringComparer.Ordinal);

        /// <summary>注册 URI handler。同 routeKey 后注册覆盖前。返回 IDisposable,Dispose 时摘除。</summary>
        public IDisposable Register(string uriRoute, Action<UriArgs> handler)
        {
            if (string.IsNullOrEmpty(uriRoute)) throw new ArgumentNullException(nameof(uriRoute));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            // 注册时只接 routeKey 形态(无 query),业务侧不该把 ?xxx 写进 Register 第一参数。
            int qIdx = uriRoute.IndexOf('?');
            if (qIdx >= 0) throw new ArgumentException(
                $"UriDispatcher.Register: '{uriRoute}' 含 query 部分,Register 只接路由 key " +
                $"(scheme://authority/path);query 在 Dispatch 时由 URI 模板填充。", nameof(uriRoute));

            _handlers[uriRoute] = handler;
            return new Unregisterer(this, uriRoute, handler);
        }

        /// <summary>分发 URI(已填充模板的完整字符串)。返回是否找到 handler 调用。</summary>
        public bool Dispatch(string uri) => Dispatch(uri, services: null);

        /// <summary>
        /// 分发 URI 并注入 services。services 字典通过 <see cref="UriArgs.GetService{T}"/> /
        /// <see cref="UriArgs.RequireService{T}"/> 暴露给 handler。典型 framework 用法:
        /// 在事件触发时把 DataSource 实例塞进 services,handler 拿强类型对象引用,
        /// 不必序列化 → 反序列化业务对象走 URI query。
        /// </summary>
        public bool Dispatch(string uri, System.Collections.Generic.IReadOnlyDictionary<Type, object>? services)
        {
            if (string.IsNullOrEmpty(uri)) return false;
            try
            {
                var (routeKey, args) = UriArgs.Parse(uri, services);
                if (!_handlers.TryGetValue(routeKey, out var handler)) return false;
                handler(args);
                return true;
            }
            catch (Exception ex)
            {
                // handler 内业务异常不能拖崩事件源 —— 蓝图节点上的事件订阅 callback 抛会让 WPF 进程 crash。
                // 翻译成 Debug 日志,业务侧自负诊断责任。
                System.Diagnostics.Debug.WriteLine($"[UriDispatcher] handler '{uri}' 抛出异常:{ex}");
                return false;
            }
        }

        /// <summary>列已注册路由(诊断 / 编辑器 dropdown 用)。</summary>
        public System.Collections.Generic.IEnumerable<string> EnumerateRoutes() => _handlers.Keys;

        /// <summary>
        /// 反射扫 <paramref name="moduleType"/> 上贴 <see cref="UriHandlerAttribute"/> 的静态方法,
        /// 自动注册到本 dispatcher。签名要求 <c>void(UriArgs)</c>,不匹配 silent skip + Debug 警告。
        /// 进程级注册,不返 IDisposable —— App 启动期一次性 wire。
        /// </summary>
        public UriDispatcher AutoDiscoverStatic(Type moduleType)
        {
            if (moduleType == null) throw new ArgumentNullException(nameof(moduleType));
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (var method in moduleType.GetMethods(Flags))
            {
                var attr = method.GetCustomAttribute<UriHandlerAttribute>();
                if (attr == null) continue;

                var pars = method.GetParameters();
                if (pars.Length != 1 || pars[0].ParameterType != typeof(UriArgs) || method.ReturnType != typeof(void))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[UriDispatcher] AutoDiscoverStatic 跳过 '{moduleType.Name}.{method.Name}':签名不匹配 void(UriArgs)。");
                    continue;
                }

                try
                {
                    var del = (Action<UriArgs>)method.CreateDelegate(typeof(Action<UriArgs>));
                    _handlers[attr.UriRoute] = del;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[UriDispatcher] AutoDiscoverStatic 注册 '{method.Name}' 失败:{ex.Message}");
                }
            }
            return this;
        }

        private sealed class Unregisterer : IDisposable
        {
            private UriDispatcher? _owner;
            private readonly string _key;
            private readonly Action<UriArgs> _handler;

            public Unregisterer(UriDispatcher owner, string key, Action<UriArgs> handler)
            {
                _owner = owner; _key = key; _handler = handler;
            }

            public void Dispose()
            {
                var owner = System.Threading.Interlocked.Exchange(ref _owner, null);
                if (owner == null) return;
                // 仅当当前 handler 仍是注册的那个时才摘 —— 避免同 routeKey 被后续 Register 覆盖后误删
                owner._handlers.TryRemove(new System.Collections.Generic.KeyValuePair<string, Action<UriArgs>>(_key, _handler));
            }
        }
    }
}
