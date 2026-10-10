using Hevo.Charting.Core;
using Hevo.Charting.LowCode.Designer.Handlers;
using Hevo.Charting.WorkFlow;
using System.Linq.Expressions;
using System.Reflection;

namespace Hevo.Charting.LowCode.Designer
{
    /// <summary>
    /// 给业务 handler 方法贴这个 attribute,被 <see cref="BlueprintHandlerRegistry.AutoDiscover"/>
    /// 反射扫到后用方法名 (或 <see cref="Name"/> 重命名) 注册到 registry。
    ///
    /// <para>
    /// 跟手写 <c>registry.RegisterFetch("on_heartbeat", OnHeartbeatAsync)</c> 一行的差距:
    /// </para>
    /// <list type="number">
    ///   <item>不用每个 handler 写 <c>new Func&lt;...&gt;(method)</c>(C# 委托推断税)</item>
    ///   <item>handler 模块换 ctor 签名时不用回头改 wire-up 代码</item>
    ///   <item>蓝图用的 handler 名字直接贴在方法上,IDE Find All References 能反查</item>
    /// </list>
    ///
    /// <para>
    /// <b>AllowMultiple</b>:同方法可贴多个 <see cref="BlueprintHandlerAttribute"/>(不同 <see cref="Name"/> 或不同 <see cref="Lifecycle"/>),
    /// 等价 IoC 的 keyed services with different lifetimes。典型场景:同一 <c>Compute</c> 既注册成 <c>"ema_node"</c>(PerNode 累加器)
    /// 也注册成 <c>"ema_shared"</c>(Scoped 共享缓存),两个 handler 名解析出两份独立宿主实例。
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class BlueprintHandlerAttribute : Attribute
    {
        /// <summary>
        /// 蓝图侧引用此 handler 用的名字。一般跟方法名一致即可,
        /// 留空时 <see cref="BlueprintHandlerRegistry.AutoDiscover"/> 默认用方法名。
        /// </summary>
        public string? Name { get; }

        /// <summary>
        /// 方法级 lifecycle 覆盖。<see cref="NodeLifetime.Inherit"/>(默认)= 跟随类级 <see cref="BlueprintNodeAttribute"/>
        /// (没贴时 → <see cref="NodeLifetime.Singleton"/>)。
        /// 仅对 <see cref="BlueprintHandlerRegistry.AutoDiscoverType"/>(framework 接管实例)路径生效;
        /// 走 <see cref="BlueprintHandlerRegistry.AutoDiscover(object)"/> / <see cref="BlueprintHandlerRegistry.AutoDiscoverStatic"/>
        /// 的旧路径忽略此字段(那两条路径 instance 由调用方持有)。
        /// <para>
        /// 用 <see cref="NodeLifetime.Inherit"/> 哨兵代替 <c>Nullable&lt;NodeLifetime&gt;</c> ——
        /// C# attribute 参数不支持 nullable enum (CS0655)。
        /// </para>
        /// </summary>
        public NodeLifetime Lifecycle { get; set; } = NodeLifetime.Inherit;

        public BlueprintHandlerAttribute(string? name = null) { Name = name; }
    }
}

namespace Hevo.Charting.LowCode.Designer
{
    /// <summary>
    /// 蓝图侧"命名委托表" —— 蓝图 JSON 用 <c>string</c> 名字引用业务 handler,
    /// 业务侧把同名的具体 <see cref="Delegate"/> 注册进来,蓝图运行时在 trigger 装配 / Feature 注入阶段查表使用。
    ///
    /// <para>
    /// 用途场景:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Trigger handler</b>:<see cref="TriggerModel.Handler"/> = "OnHeartbeat" → 注册
    ///         <c>Func&lt;VersionToken, CancellationToken, Task&lt;bool&gt;&gt;</c>。</item>
    ///   <item><b>Feature 上 Delegate 类型属性</b>:典型如
    ///         <c>ChartInteractionFeature.OnRequireDataAsync</c> /
    ///         <c>CrosshairFeature.FutureXLabelFormatter</c> ——
    ///         蓝图 Properties 里写字符串名字,DynamicChartSchema 装 feature 前查表替换为实际 Delegate。</item>
    /// </list>
    ///
    /// <para>
    /// JSON 表达不出来的"业务闭包"全部通过这个注册表桥接;蓝图本身保持纯数据,
    /// AI 生成 / diff 评审 / 持久化都不受影响。
    /// </para>
    /// </summary>
    public class BlueprintHandlerRegistry
    {
        // 统一存储:Pre-bound delegate(旧路径:RegisterDelegate / AutoDiscover / AutoDiscoverStatic)
        //          + Lifecycle-aware entry(新路径:AutoDiscoverType,framework 按 scope 创建宿主实例)。
        // 同名后注册覆盖,跟旧 _handlers 行为对齐。
        private readonly Dictionary<string, HandlerEntry> _entries = new(StringComparer.Ordinal);

        // §D2.X 多输入指标:可选 input 形参名列表(跟委托的位置参数顺序一致)。
        // 单输入 handler 不登记,GetInputNames 返回 null,Feature 走旧 InputPort 兼容路径。
        private readonly Dictionary<string, IReadOnlyList<string>> _handlerInputs = new(StringComparer.Ordinal);

        // TryGet 兜底用的内部默认 ScopeContext —— Lifecycle-aware 但不传 scope 的旧调用路径
        // 退化成 Singleton 解析(framework Phase 1 唯一可能的形态)。
        // PerNode/Scoped 类 handler 需要走 Resolve(name, scope, nodeId) 显式传 scope。
        private readonly Lazy<ScopeContext> _defaultScope = new(() => new ScopeContext(), isThreadSafe: true);

        /// <summary>
        /// 注册一条 FetchExclusive 用的 fetch handler ——
        /// 给 <see cref="TriggerModel"/> Kind=Interval 用,签名跟 <see cref="Workflow"/>.FetchExclusive 对齐。
        /// </summary>
        public BlueprintHandlerRegistry RegisterFetch(string name, Func<VersionToken, CancellationToken, Task<bool>> handler)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _entries[name] = HandlerEntry.OfStatic(name, handler);
            return this;
        }

        /// <summary>
        /// 注册任意命名委托。Feature 上 Delegate 类型属性(例 OnRequireDataAsync / FutureXLabelFormatter)
        /// 通过这个查 + 在装配时按目标属性类型校验 IsAssignableFrom。
        /// </summary>
        public BlueprintHandlerRegistry RegisterDelegate(string name, Delegate handler)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _entries[name] = HandlerEntry.OfStatic(name, handler);
            return this;
        }

        /// <summary>
        /// §D2.X 注册带 input 形参名列表的 handler(典型用于多输入 Python 指标)。
        /// 单输入或不需要 nested PortBindings 的 handler 走 <see cref="RegisterDelegate(string, Delegate)"/> 即可。
        /// inputs 为 null / 空时等价 RegisterDelegate(name, handler)。
        /// </summary>
        public BlueprintHandlerRegistry RegisterDelegate(string name, Delegate handler, IReadOnlyList<string>? inputs)
        {
            RegisterDelegate(name, handler);
            if (inputs != null && inputs.Count > 0) _handlerInputs[name] = inputs;
            return this;
        }

        /// <summary>
        /// 查询委托。返回 null 表示未登记。
        /// <para>
        /// Lifecycle-aware entry(<see cref="AutoDiscoverType"/> 注册的)在没传 scope 的旧调用路径上,
        /// 仅 <see cref="NodeLifetime.Singleton"/> 能解析(用内部默认 ScopeContext)。
        /// PerNode/Scoped/Transient 必须走 <see cref="Resolve(string, IScopeContext, string?)"/>,这里返回 null。
        /// </para>
        /// </summary>
        public Delegate? TryGet(string name)
        {
            if (!_entries.TryGetValue(name, out var entry)) return null;
            try
            {
                return entry.Resolve(_defaultScope.Value, nodeId: null);
            }
            catch
            {
                // PerNode/Scoped/Transient 没传 scope 解析失败 —— TryGet 契约是不抛,返回 null 让调用方 fallback。
                return null;
            }
        }

        /// <summary>
        /// Lifecycle-aware 解析 handler。framework Cascade / Trigger / Feature 装配时调用,
        /// 按 entry 的 lifecycle + 调用方提供的 <paramref name="scope"/> 决定宿主实例池。
        /// 静态(pre-bound)entry 忽略 scope/nodeId,直接返回原 delegate。
        /// </summary>
        /// <param name="name">handler 注册名。</param>
        /// <param name="scope">Cascade/Trigger/Feature 装配期持有的 ScopeContext(蓝图实例级)。</param>
        /// <param name="nodeId">PerNode 路径用 —— Feature 节点传 FeatureId,Cascade/Trigger driver 传 <c>cascade:{From}-&gt;{To}</c> 等复合 Id。</param>
        public Delegate? Resolve(string name, IScopeContext scope, string? nodeId = null)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (!_entries.TryGetValue(name, out var entry)) return null;
            return entry.Resolve(scope, nodeId);
        }

        /// <summary>类型化查询:专给 trigger 装配的 fetch handler 用。返回 null = 未登记或类型不匹配。</summary>
        public Func<VersionToken, CancellationToken, Task<bool>>? TryGetFetch(string name)
            => TryGet(name) as Func<VersionToken, CancellationToken, Task<bool>>;

        /// <summary>是否登记过该 handler 名字 (任何类型)。dry-run 校验用。</summary>
        public bool Contains(string name) => _entries.ContainsKey(name);

        /// <summary>
        /// §D2.X 查 input 形参名列表 —— 多输入指标用。
        /// 返回 null = 没登记 input metadata(单输入或非 Python 路径),Feature 走单 InputPort 兼容路径。
        /// </summary>
        public IReadOnlyList<string>? GetInputNames(string name)
            => _handlerInputs.TryGetValue(name, out var list) ? list : null;

        /// <summary>
        /// §D2.X 枚举所有"多输入" handler(GetInputNames 非 null)及其形参列表。
        /// 给 LowCodeDemo picker 等 UX 用 —— 列出可作为 ComputeFeature / PlotFeature / HandlerFeature
        /// 多输入源的候选 handler。
        /// </summary>
        public IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> EnumerateMultiInputHandlers()
            => _handlerInputs;

        /// <summary>
        /// §D2.X 枚举**所有** handler(单 + 多输入)及其 inputs 元数据(单输入返 null,多输入返形参列表)。
        /// 给 picker UX 一站式列表用 —— 用户在编辑器里给 Compute / Indicator / Handler 等 Delegate 字段
        /// 选 handler 时,候选范围应包含全部 handler,而不仅限多输入。
        /// </summary>
        public IEnumerable<(string Name, IReadOnlyList<string>? Inputs)> EnumerateAllHandlers()
        {
            foreach (var name in _entries.Keys)
            {
                _handlerInputs.TryGetValue(name, out var inputs);
                yield return (name, inputs);
            }
        }

        /// <summary>
        /// 枚举所有已注册 handler 名。给 picker / 调试器列表用,顺序按 ordinal sort。
        /// </summary>
        public IEnumerable<string> EnumerateNames() => _entries.Keys;

        /// <summary>
        /// 查询 handler 的实际委托类型(用于 picker UX 按返回值分类:Func/Action / 返回 ROM/object/void)。
        /// Lifecycle-aware entry 返回方法签名推断的目标委托类型,不需要实例化宿主。
        /// </summary>
        public Type? GetDelegateType(string name)
            => _entries.TryGetValue(name, out var entry) ? entry.DelegateType : null;

        /// <summary>§D2.3: 摘除已注册 handler。handler 不存在时静默忽略。</summary>
        public void Unregister(string name)
        {
            _entries.Remove(name);
            _handlerInputs.Remove(name);
        }

        /// <summary>
        /// 反射扫描 <paramref name="module"/> 上所有贴了 <see cref="BlueprintHandlerAttribute"/> 的方法,
        /// 自动构造目标委托类型 (Action / Func) 并注册。
        /// <para>
        /// 委托类型从 method 签名自动推断:返回值 void → Action&lt;...&gt;,其它 → Func&lt;..., TReturn&gt;。
        /// 跟手写 <c>RegisterFetch("on_heartbeat", h.OnHeartbeatAsync)</c> 等价,但省掉每个 handler 一行 ceremony。
        /// </para>
        /// <para>
        /// 用法:
        /// </para>
        /// <code>
        /// public class KLineHandlers
        /// {
        ///     public KLineHandlers(KLineDataSource ds) { _ds = ds; }
        ///     [BlueprintHandler("on_heartbeat")]
        ///     public Task&lt;bool&gt; OnHeartbeat(VersionToken tick, CancellationToken token) {...}
        /// }
        /// // wire-up:
        /// var registry = new BlueprintHandlerRegistry().AutoDiscover(new KLineHandlers(ds));
        /// </code>
        /// </summary>
        public BlueprintHandlerRegistry AutoDiscover(object module)
        {
            if (module is null) throw new ArgumentNullException(nameof(module));

            // public + non-public + instance 都扫 —— 业务 handler 经常是 private 方法
            // (从 Schema 内迁过来时不需要因为加了 attribute 就改成 public)。
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var method in module.GetType().GetMethods(Flags))
                RegisterIfHandler(method, target: module, ownerType: null);
            return this;
        }

        /// <summary>静态方法版的 AutoDiscover —— 给"无状态 handler 工具集"用。</summary>
        public BlueprintHandlerRegistry AutoDiscoverStatic(Type moduleType)
        {
            if (moduleType is null) throw new ArgumentNullException(nameof(moduleType));
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (var method in moduleType.GetMethods(Flags))
                RegisterIfHandler(method, target: null, ownerType: null);
            return this;
        }

        /// <summary>
        /// Framework 接管实例创建版 —— 扫 <paramref name="moduleType"/> 上的实例方法 <see cref="BlueprintHandlerAttribute"/>,
        /// 注册成 lifecycle-aware entry。运行时 <see cref="Resolve(string, IScopeContext, string?)"/>
        /// 按方法级 / 类级 <see cref="BlueprintNodeAttribute"/> 推断的 lifecycle 决定宿主实例池。
        /// <para>
        /// 跟 <see cref="AutoDiscover(object)"/> 的区别:那条路径 instance 由调用方 new + 持有(等价用户托管 Singleton);
        /// 这条路径 framework 按 <see cref="NodeLifetime"/> 在 <see cref="ScopeContext"/> 池里 lazy-create,
        /// PerNode 同 handler 名按 nodeId 隔离(EMA 累加器场景)。
        /// </para>
        /// </summary>
        public BlueprintHandlerRegistry AutoDiscoverType(Type moduleType)
        {
            if (moduleType is null) throw new ArgumentNullException(nameof(moduleType));
            if (moduleType.IsAbstract || moduleType.IsInterface)
                throw new ArgumentException(
                    $"AutoDiscoverType 仅接受可实例化的具体类,{moduleType.Name} 是 abstract/interface。", nameof(moduleType));

            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var method in moduleType.GetMethods(Flags))
                RegisterIfHandler(method, target: null, ownerType: moduleType);
            return this;
        }

        // 共用注册:除了构造 delegate,顺手把 ParameterInfo.Name 抽出存进 _handlerInputs。
        // 多输入 ComputeFeature/PlotFeature/HandlerFeature 蓝图侧用 nested PortBindings("Inputs.latest")
        // 焊接,需要 GetInputNames 返回形参名列表。AutoDiscover 之前不填 inputs → 多输入 handler
        // 自动注册路径上不能用,业务侧被迫退回手写 RegisterDelegate(name, del, inputs) ceremony。
        // C# 方法的形参名通过 reflection 永远可得(只要不是 lambda),AutoDiscover 自动填即可。
        //
        // 三条路径合一:
        //   target != null → AutoDiscover(instance):pre-bound delegate,等价用户托管 Singleton
        //   target == null && ownerType == null → AutoDiscoverStatic:静态方法 pre-bound,Singleton
        //   target == null && ownerType != null → AutoDiscoverType:lifecycle-aware,framework 接管实例
        //
        // Phase 2:同时扫 [ContextDriver] —— Cascade driver 跟 BlueprintHandler 共享同一注册表,
        // 蓝图 CascadeEdge.ContextDriver 字段按裸 string 名查 _entries。语义层面 driver 跟 handler
        // 都是"命名委托",存储/解析路径一致,语义区别仅在调用方(BlueprintCascadeWiring vs Feature 装配)。
        private void RegisterIfHandler(MethodInfo method, object? target, Type? ownerType)
        {
            // AllowMultiple = true:同方法可贴多个 [BlueprintHandler],各自独立 name + lifecycle。
            // GetCustomAttributes(typeof) 返回 object[]; cast 后避免装拆箱。
            var handlerAttrs = method.GetCustomAttributes(typeof(BlueprintHandlerAttribute), inherit: false);
            // [ContextDriver] / [TriggerDriver] 都是 AllowMultiple=false 但同方法可跟 [BlueprintHandler] 并存。
            var ctxDriverAttrs     = method.GetCustomAttributes(typeof(Handlers.ContextDriverAttribute), inherit: false);
            var triggerDriverAttrs = method.GetCustomAttributes(typeof(Handlers.TriggerDriverAttribute), inherit: false);
            if (handlerAttrs.Length == 0 && ctxDriverAttrs.Length == 0 && triggerDriverAttrs.Length == 0) return;
            // 合并扫描成统一 (name, lifecycle?) 列表 —— 装配期下游不区分 handler/driver。
            var registrations = new List<(string Name, NodeLifetime Lifecycle)>(
                handlerAttrs.Length + ctxDriverAttrs.Length + triggerDriverAttrs.Length);
            for (int i = 0; i < handlerAttrs.Length; i++)
            {
                var a = (BlueprintHandlerAttribute)handlerAttrs[i];
                registrations.Add((string.IsNullOrEmpty(a.Name) ? method.Name : a.Name!, a.Lifecycle));
            }
            for (int i = 0; i < ctxDriverAttrs.Length; i++)
            {
                var a = (Handlers.ContextDriverAttribute)ctxDriverAttrs[i];
                registrations.Add((a.Name, a.Lifecycle));
            }
            for (int i = 0; i < triggerDriverAttrs.Length; i++)
            {
                var a = (Handlers.TriggerDriverAttribute)triggerDriverAttrs[i];
                registrations.Add((a.Name, a.Lifecycle));
            }
            var delegateType = ResolveDelegateType(method);
            // 形参名捞一份给 inputs,所有 attr 共用(handler 名变 lifecycle 不变形参顺序)。
            var parameters = method.GetParameters();
            string[]? inputs = null;
            if (parameters.Length > 0)
            {
                inputs = new string[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                    inputs[i] = parameters[i].Name ?? $"arg{i}";
            }

            foreach (var (name, attrLifecycle) in registrations)
            {
                if (delegateType == null)
                    throw new InvalidOperationException(
                        $"BlueprintHandler/ContextDriver '{name}' 方法签名 ({method}) 无法构造委托。" +
                        $"参数 / 返回值需是闭合泛型(无 generic parameter)。");

                HandlerEntry entry;
                if (ownerType == null)
                {
                    // 旧路径:pre-bound delegate(static / instance-bound)。Lifecycle 字段忽略 ——
                    // 实例已经 new 好了,framework 没有创建权,何谈 PerNode 隔离。
                    var del = target == null
                        ? method.CreateDelegate(delegateType)
                        : method.CreateDelegate(delegateType, target);
                    entry = HandlerEntry.OfStatic(name, del);
                }
                else
                {
                    // 新路径:framework 按 lifecycle 在 ScopeContext 池里 lazy-create。
                    var lifecycle = ResolveLifetime(ownerType, attrLifecycle);
                    entry = HandlerEntry.OfInstance(name, ownerType, method, delegateType, lifecycle);
                }

                _entries[name] = entry;
                if (inputs != null) _handlerInputs[name] = inputs;
            }
        }

        // method-level Lifecycle > class-level [BlueprintNode] > 默认 Singleton。
        // Inherit 哨兵 = 方法级未指定,继续往上查;类级 Inherit(理论不应出现)也降级 Singleton。
        private static NodeLifetime ResolveLifetime(Type ownerType, NodeLifetime attrLifecycle)
        {
            if (attrLifecycle != NodeLifetime.Inherit) return attrLifecycle;
            var nodeAttr = ownerType.GetCustomAttribute<BlueprintNodeAttribute>();
            if (nodeAttr == null) return NodeLifetime.Singleton;
            return nodeAttr.Lifecycle == NodeLifetime.Inherit
                ? NodeLifetime.Singleton
                : nodeAttr.Lifecycle;
        }

        // method 签名 → 闭合 Action<...> / Func<..., TReturn> 类型。
        // 含开放泛型参数 / 超过 16 参数 → null(调用方报错,handler 不可注册)。
        internal static Type? ResolveDelegateType(MethodInfo method)
        {
            var paramTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
            if (Array.Exists(paramTypes, t => t.ContainsGenericParameters)) return null;
            if (method.ReturnType.ContainsGenericParameters) return null;

            try
            {
                return method.ReturnType == typeof(void)
                    ? Expression.GetActionType(paramTypes)
                    : Expression.GetFuncType(paramTypes.Append(method.ReturnType).ToArray());
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    // =================================================================
    // HandlerEntry —— 统一存储 pre-bound delegate 跟 lifecycle-aware instance handler。
    //
    // OfStatic:旧 RegisterDelegate / AutoDiscover / AutoDiscoverStatic 路径。Resolve 直接吐 StaticFn。
    // OfInstance:AutoDiscoverType 路径。Resolve 走 IScopeContext.GetOrCreate 拿宿主,临时 CreateDelegate(类型, 宿主)。
    //   每次 Resolve 都重新 CreateDelegate(boundDelegate cache 不优化也可)—— 同 owner 的 method 已经 JIT 过,
    //   调用层一次反射开销可忽略。需要时再加 (owner identity) → cached delegate 内层 Dict。
    // =================================================================

    internal sealed class HandlerEntry
    {
        public string Name { get; }
        public NodeLifetime Lifecycle { get; }
        public Type? OwnerType { get; }
        public MethodInfo? Method { get; }
        public Delegate? StaticFn { get; }
        public Type DelegateType { get; }

        private HandlerEntry(string name, NodeLifetime lifecycle, Type? ownerType, MethodInfo? method,
                             Delegate? staticFn, Type delegateType)
        {
            Name = name;
            Lifecycle = lifecycle;
            OwnerType = ownerType;
            Method = method;
            StaticFn = staticFn;
            DelegateType = delegateType;
        }

        public static HandlerEntry OfStatic(string name, Delegate fn)
            => new(name, NodeLifetime.Singleton, ownerType: null, method: null,
                   staticFn: fn, delegateType: fn.GetType());

        public static HandlerEntry OfInstance(string name, Type ownerType, MethodInfo method,
                                              Type delegateType, NodeLifetime lifecycle)
            => new(name, lifecycle, ownerType, method, staticFn: null, delegateType);

        public Delegate Resolve(IScopeContext scope, string? nodeId)
        {
            if (StaticFn != null) return StaticFn;
            if (OwnerType == null || Method == null)
                throw new InvalidOperationException($"HandlerEntry '{Name}' 既不是静态也不是实例,内部状态错乱。");
            if (scope == null)
                throw new InvalidOperationException(
                    $"HandlerEntry '{Name}' 是 {Lifecycle} lifecycle,需要 IScopeContext。" +
                    $"调用方应改走 Resolve(name, scope, nodeId) 或经 framework 装配路径。");

            var owner = scope.GetOrCreate(OwnerType, Lifecycle, Name, nodeId);
            return Method.CreateDelegate(DelegateType, owner);
        }
    }

    // =================================================================
    // 强类型 handler key + Register/Get 扩展(协议增强 §K-2)
    //
    // 现状(弱类型路径):
    //   registry.RegisterDelegate("on_heartbeat", new Func<...>(WrongSignature));
    //   // 编译过 → 运行时 ResolveHandlerReferences 的 IsAssignableFrom 失败 → silent skip → 黑屏
    //
    // 强类型路径:
    //   registry.Register(WellKnownHandlers.Trigger.Fetch("on_heartbeat"), OnHeartbeatAsync);
    //   // 签名错配编译期就报 CS1503,IDE 实时红下划线
    //
    // 两条路径并存,业务侧按需选 —— AutoDiscover 是默认便利路径(一行扫整模块),
    // HandlerKey 是显式严谨路径(主线 handler 多一道编译期防线)。
    // =================================================================

    /// <summary>
    /// 强类型 handler 名字 —— "name + 期望委托签名"绑成一个 zero-cost 值类型。
    /// 编译期保证 register/get 时签名跟期望一致,蓝图 JSON 一侧仍然是裸 string(不可避免)。
    /// </summary>
    public readonly struct HandlerKey<TDelegate> where TDelegate : Delegate
    {
        public string Name { get; }
        public HandlerKey(string name) { Name = name ?? throw new ArgumentNullException(nameof(name)); }

        // 隐式转换:string 直接当 HandlerKey 用,业务 ad-hoc 需要时不必显式 new
        // (例: registry.Register<Func<int,string>>("foo", ...) 也能写)。
        public static implicit operator HandlerKey<TDelegate>(string name) => new(name);
        public override string ToString() => Name;
    }

    public static class BlueprintHandlerRegistryStrongTypedExtensions
    {
        /// <summary>
        /// 强类型注册:<typeparamref name="TDelegate"/> 锁定签名,handler 跟期望签名不匹配编译期报错。
        /// 等价 RegisterDelegate(key.Name, handler) 但带 compile-time check。
        /// </summary>
        public static BlueprintHandlerRegistry Register<TDelegate>(
            this BlueprintHandlerRegistry registry,
            HandlerKey<TDelegate> key,
            TDelegate handler) where TDelegate : Delegate
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return registry.RegisterDelegate(key.Name, handler);
        }

        /// <summary>
        /// 强类型查询:返回 <typeparamref name="TDelegate"/> 类型的委托。
        /// 未注册或类型不匹配 → null。跟 <c>TryGet(name) as TDelegate</c> 等价但带类型对齐 IDE 提示。
        /// </summary>
        public static TDelegate? Get<TDelegate>(
            this BlueprintHandlerRegistry registry,
            HandlerKey<TDelegate> key) where TDelegate : Delegate
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            return registry.TryGet(key.Name) as TDelegate;
        }
    }
}
