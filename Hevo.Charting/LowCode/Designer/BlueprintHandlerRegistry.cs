using Hevo.Charting.Core;
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
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class BlueprintHandlerAttribute : Attribute
    {
        /// <summary>
        /// 蓝图侧引用此 handler 用的名字。一般跟方法名一致即可,
        /// 留空时 <see cref="BlueprintHandlerRegistry.AutoDiscover"/> 默认用方法名。
        /// </summary>
        public string? Name { get; }

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
        private readonly Dictionary<string, Delegate> _handlers = new(StringComparer.Ordinal);

        /// <summary>
        /// 注册一条 FetchExclusive 用的 fetch handler ——
        /// 给 <see cref="TriggerModel"/> Kind=Interval 用,签名跟 <see cref="Workflow"/>.FetchExclusive 对齐。
        /// </summary>
        public BlueprintHandlerRegistry RegisterFetch(string name, Func<VersionToken, CancellationToken, Task<bool>> handler)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            _handlers[name] = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        /// <summary>
        /// 注册任意命名委托。Feature 上 Delegate 类型属性(例 OnRequireDataAsync / FutureXLabelFormatter)
        /// 通过这个查 + 在装配时按目标属性类型校验 IsAssignableFrom。
        /// </summary>
        public BlueprintHandlerRegistry RegisterDelegate(string name, Delegate handler)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            _handlers[name] = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        /// <summary>查询委托。返回 null 表示未登记。</summary>
        public Delegate? TryGet(string name)
            => _handlers.TryGetValue(name, out var d) ? d : null;

        /// <summary>类型化查询:专给 trigger 装配的 fetch handler 用。返回 null = 未登记或类型不匹配。</summary>
        public Func<VersionToken, CancellationToken, Task<bool>>? TryGetFetch(string name)
            => TryGet(name) as Func<VersionToken, CancellationToken, Task<bool>>;

        /// <summary>是否登记过该 handler 名字 (任何类型)。dry-run 校验用。</summary>
        public bool Contains(string name) => _handlers.ContainsKey(name);

        /// <summary>§D2.3: 摘除已注册 handler。handler 不存在时静默忽略。</summary>
        public void Unregister(string name) => _handlers.Remove(name);

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
            {
                var attr = method.GetCustomAttribute<BlueprintHandlerAttribute>();
                if (attr == null) continue;

                var name = string.IsNullOrEmpty(attr.Name) ? method.Name : attr.Name!;
                var del = BuildDelegate(method, module)
                    ?? throw new InvalidOperationException(
                        $"BlueprintHandler '{name}' 方法签名 ({method}) 无法构造委托。" +
                        $"参数 / 返回值需是闭合泛型(无 generic parameter)。");
                _handlers[name] = del;
            }
            return this;
        }

        /// <summary>静态方法版的 AutoDiscover —— 给"无状态 handler 工具集"用。</summary>
        public BlueprintHandlerRegistry AutoDiscoverStatic(Type moduleType)
        {
            if (moduleType is null) throw new ArgumentNullException(nameof(moduleType));
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (var method in moduleType.GetMethods(Flags))
            {
                var attr = method.GetCustomAttribute<BlueprintHandlerAttribute>();
                if (attr == null) continue;
                var name = string.IsNullOrEmpty(attr.Name) ? method.Name : attr.Name!;
                var del = BuildDelegate(method, target: null)
                    ?? throw new InvalidOperationException(
                        $"BlueprintHandler '{name}' 静态方法签名 ({method}) 无法构造委托。");
                _handlers[name] = del;
            }
            return this;
        }

        private static Delegate? BuildDelegate(MethodInfo method, object? target)
        {
            var paramTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
            // 含开放泛型参数(罕见)直接劝退,委托类型推断会失败。
            if (Array.Exists(paramTypes, t => t.ContainsGenericParameters)) return null;
            if (method.ReturnType.ContainsGenericParameters) return null;

            Type delegateType;
            try
            {
                delegateType = method.ReturnType == typeof(void)
                    ? Expression.GetActionType(paramTypes)
                    : Expression.GetFuncType(paramTypes.Append(method.ReturnType).ToArray());
            }
            catch (ArgumentException)
            {
                // Action/Func 最多 16 参数;超出的极少见 handler 不支持。
                return null;
            }
            return target == null
                ? method.CreateDelegate(delegateType)
                : method.CreateDelegate(delegateType, target);
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
