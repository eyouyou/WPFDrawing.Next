using System.Collections.Concurrent;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;

namespace Hevo.Charting.LowCode.Designer
{
    public static class ComponentRegistry
    {
        private static readonly Dictionary<string, Type> _types = new();

        // 💥 无参 ctor 编译缓存:Expression.New(ctor).Compile() 出来的委托等价于直接 newobj IL,
        // 反射调度开销清零。50 个 Feature 蓝图加载从 ~30ms 反射 ctor 降到 ~3ms。
        // 类型没有 public 无参 ctor 时缓存 null,调用方走 Activator 兜底(typically 只有 record 主构造)。
        private static readonly ConcurrentDictionary<Type, Func<object>?> _ctorCache = new();

        public static void Register<T>(string? alias = null)
        {
            _types[alias ?? typeof(T).Name] = typeof(T);
        }

        /// <summary>非泛型登记口,给反射扫描批量注册用 (BuiltinRegistration)。已存在同名时覆盖。</summary>
        public static void Register(Type type, string? alias = null)
        {
            if (type is null) throw new ArgumentNullException(nameof(type));
            _types[alias ?? type.Name] = type;
        }

        public static Type Resolve(string typeName)
        {
            if (_types.TryGetValue(typeName, out var type)) return type;
            // §11 作用域隔离 fallback:精确匹配失败时,若 typeName 含 "prefix:Name" 形态,
            // 退回去找无前缀的注册项。这让旧蓝图(写"LineSeriesFeature")在新装配
            // (用 RegisterAssemblyOf<T>("kline") 把所有 feature 套了 prefix)的进程里仍能命中
            // 框架自带的无前缀基类注册;新蓝图反过来用 prefix 引用业务专属版,各取所需。
            if (TryStripPrefix(typeName, out var fallback) && _types.TryGetValue(fallback, out type))
                return type;
            throw new Exception($"[VM 致命错误] 未知的组件类型: {typeName}。请确保已注册。");
        }

        /// <summary>查询是否已登记;蓝图加载前的预检场景用。带 §11 prefix 兜底:
        /// 查 <c>"kline:LineSeriesFeature"</c> 找不到 → 回退查 <c>"LineSeriesFeature"</c>。</summary>
        public static bool IsRegistered(string typeName)
        {
            if (_types.ContainsKey(typeName)) return true;
            return TryStripPrefix(typeName, out var fallback) && _types.ContainsKey(fallback);
        }

        // 仅当 typeName 含 ':' 时返回去掉前缀的尾段。空段(如 "kline:")也视为非法,返回 false。
        private static bool TryStripPrefix(string typeName, out string stripped)
        {
            stripped = string.Empty;
            if (string.IsNullOrEmpty(typeName)) return false;
            int idx = typeName.IndexOf(':');
            if (idx < 0 || idx >= typeName.Length - 1) return false;
            stripped = typeName.Substring(idx + 1);
            return true;
        }

        /// <summary>枚举所有已登记的 (alias, type) 对。GraphViewer 拉组件选择列表时用。</summary>
        public static IEnumerable<KeyValuePair<string, Type>> ListAll() => _types;

        /// <summary>清空登记表 (单元测试或动态重载场景用)。</summary>
        public static void Reset()
        {
            _types.Clear();
            _ctorCache.Clear();
        }

        /// <summary>
        /// 用编译委托缓存创建无参 ctor 实例。Feature / DataSource / 简单 Trait 走这条快路。
        /// 类型无 public 无参 ctor → 落回 <see cref="Activator.CreateInstance(Type)"/>(record 主构造场景),
        /// 让调用方原有兜底链路不变。
        /// </summary>
        public static object CreateInstance(Type type)
        {
            if (type is null) throw new ArgumentNullException(nameof(type));
            var compiled = _ctorCache.GetOrAdd(type, BuildCompiledCtor);
            if (compiled != null) return compiled();
            return Activator.CreateInstance(type)!;
        }

        private static Func<object>? BuildCompiledCtor(Type t)
        {
            var ctor = t.GetConstructor(BindingFlags.Public | BindingFlags.Instance, binder: null,
                                        types: Type.EmptyTypes, modifiers: null);
            if (ctor == null) return null;
            // Expression.New 把 newobj 编出来,委托第一次调用后 JIT 内联成接近直接 new T() 的速度。
            return Expression.Lambda<Func<object>>(Expression.Convert(Expression.New(ctor), typeof(object)))
                .Compile();
        }
    }

    public static class SmartActivator
    {
        // 💥 setter 编译缓存:Expression.Assign 出来的委托等价于直接 callvirt set_X,反射调度开销清零。
        // 50 Feature × 平均 5 属性 = 250 次 SetValue 全走编译路径,加载阶段省掉一笔可观开销。
        // null 表示 prop 不存在或 read-only,调用方 skip。case-insensitive 匹配在 Build 里完成,
        // 不同大小写的 key 各自缓存一份(蓝图侧 key 一般跟 PropertyInfo.Name 同 case,重复成本可忽略)。
        private sealed class SetterEntry
        {
            public Type PropertyType = typeof(object);
            public Action<object, object?> Setter = static (_, _) => { };
        }
        private static readonly ConcurrentDictionary<(Type Owner, string Name), SetterEntry?> _setterCache = new();

        private static SetterEntry? GetSetterEntry(Type targetType, string propName)
            => _setterCache.GetOrAdd((targetType, propName), static key => BuildSetterEntry(key.Owner, key.Name));

        private static SetterEntry? BuildSetterEntry(Type targetType, string propName)
        {
            var pi = targetType.GetProperty(propName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (pi == null || !pi.CanWrite) return null;

            // 💥 框架托管字段防注入护栏:setter 不是 public 的属性(典型 internal set / private set)
            //    一律视为框架内部装配点,蓝图 Properties 字典即便挟带同名 key 也禁止写入。
            //    踩过的坑:ChartFeature.Viewport(internal set,由 ReactiveSchema.Add 在 Add 期间正确注入)
            //    一旦走 SmartActivator 反射路径会被覆盖成另一个 ViewportPorts 实例 →
            //    ScalarIngestor 写到 schema 实例的 port,Feature 读到 inject 进来的另一个实例的 port →
            //    LogicalLength 永远 0 → ActiveRange invalid → OnProject 早退黑屏(详见低代码.md §F PyPlot bug)。
            //    init 编译为带 modreq 的 public setter,IsPublic == true,init-only 配置字段不受影响。
            if (pi.SetMethod == null || !pi.SetMethod.IsPublic) return null;

            var instParam = Expression.Parameter(typeof(object), "instance");
            var valParam  = Expression.Parameter(typeof(object), "value");

            // 💥 struct 走 Unbox(返回 boxed 内部 ref),写穿透到外层 boxed object;
            //    class 走 Convert (引用转换,无装箱开销)。两条路 IL 行为对齐 PropertyInfo.SetValue。
            Expression instCast = pi.DeclaringType!.IsValueType
                ? Expression.Unbox(instParam, pi.DeclaringType)
                : Expression.Convert(instParam, pi.DeclaringType);

            var valCast = Expression.Convert(valParam, pi.PropertyType);
            // init-only 属性:CLR 不强制 modreq(IsExternalInit) 在 ctor 外不可写,
            // Expression.Assign 跟原 PropertyInfo.SetValue 一样直接穿透。
            var assign  = Expression.Assign(Expression.Property(instCast, pi), valCast);

            var compiled = Expression.Lambda<Action<object, object?>>(assign, instParam, valParam).Compile();
            return new SetterEntry { PropertyType = pi.PropertyType, Setter = compiled };
        }

        /// <summary>
        /// 把松散 Properties 字典强类型注入到目标对象。<b>Fail-fast 语义</b>:
        /// <list type="bullet">
        ///   <item>未知 key(typo / 字段已删)→ 抛 <see cref="InvalidOperationException"/></item>
        ///   <item>类型不兼容(string→int 转不过 / null→非 Nullable 值类型)→ 抛(由 <see cref="CoerceValue"/> 上抛)</item>
        ///   <item>属性存在但 setter 非 public → 静默跳过(框架托管字段防注入护栏,见 <see cref="BuildSetterEntry"/>)</item>
        ///   <item><see cref="SkipInjection"/> sentinel → 静默跳过(接口字段无多态 Converter,Preset 默认值保留)</item>
        /// </list>
        /// <para>
        /// 历史教训:这层之前 try-catch 把所有异常吞成 console warning,导致蓝图 typo 跟类型错配的 bug
        /// 全部表现为"属性悄无声息留默认值"(典型案例:scanner.blueprint.json 配 MergeMode="WhenAll"
        /// 跑出来却是 WhenAny)。fail-fast 让配置错在装配期就翻车,免得运行时神秘行为。
        /// </para>
        /// </summary>
        public static void InjectProperties(object target, Dictionary<string, object?>? properties)
        {
            if (target == null || properties == null || properties.Count == 0) return;

            Type targetType = target.GetType();

            foreach (var kvp in properties)
            {
                var entry = GetSetterEntry(targetType, kvp.Key);
                if (entry == null)
                {
                    // 区分三种"BuildSetterEntry 不给 entry"原因 —— 前两种是真错,第三种是显式护栏:
                    //   (a) 属性根本不存在 (pi == null) → typo / 字段已删,fail-fast 抛
                    //   (b) 属性存在但无 setter (!pi.CanWrite) → 只读属性,蓝图配它也是配错,fail-fast 抛
                    //   (c) 属性存在 + 有 setter,但 setter 非 public → 框架托管字段(典型 ChartFeature.Viewport),
                    //       显式契约要求静默跳过,蓝图作者写了同名 key 不该影响装配
                    var pi = targetType.GetProperty(kvp.Key,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (pi == null)
                        throw new InvalidOperationException(
                            $"[Hevo 蓝图配置错] 属性 '{kvp.Key}' 在 {targetType.Name} 上不存在 —— " +
                            $"检查蓝图 JSON 拼写、字段是否被删、TypeName 是否对。");
                    if (!pi.CanWrite)
                        throw new InvalidOperationException(
                            $"[Hevo 蓝图配置错] 属性 '{kvp.Key}' 在 {targetType.Name} 上只读(无 setter)—— " +
                            $"蓝图不能配置这种字段。");
                    continue;   // (c) 框架护栏,静默跳过
                }

                object? value = CoerceValue(kvp.Value, entry.PropertyType);
                if (ReferenceEquals(value, SkipInjection)) continue;
                entry.Setter(target, value);
            }
        }

        /// <summary>
        /// 按类型从 services 注入 ctor 参数实例化。
        /// 优先级:
        /// <list type="number">
        ///   <item>public 无参 ctor(走 <see cref="ComponentRegistry.CreateInstance"/> 编译委托缓存)</item>
        ///   <item>public 单 ctor:全部形参按类型从 <paramref name="services"/> 取(类比 ASP.NET Core ctor DI)</item>
        ///   <item>多 public ctor:挑参数最长且能全部解析的那一个</item>
        ///   <item>都不行:抛 InvalidOperationException</item>
        /// </list>
        /// services 为 null / 空 → 等价 <see cref="ComponentRegistry.CreateInstance"/>(只支持无参 ctor)。
        /// 用于 <see cref="Handlers.IScopeContext.GetOrCreate"/> 内部 Activate handler 宿主类。
        /// </summary>
        public static object CreateInstance(Type type, IReadOnlyDictionary<Type, object>? services)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            // 无参 ctor 快路径:绝大多数 handler 类无依赖,走 ComponentRegistry 编译委托缓存。
            var noArgCtor = type.GetConstructor(BindingFlags.Public | BindingFlags.Instance,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            if (noArgCtor != null)
                return ComponentRegistry.CreateInstance(type);

            if (services == null || services.Count == 0)
                throw new InvalidOperationException(
                    $"[SmartActivator] {type.Name} 无 public 无参 ctor,且未提供 services dict 给 ctor DI。");

            // 多 public ctor → 优先挑参数最长且能全部从 services 解析的那一个。
            // 跟 .NET DI 同策略:多个可选时 framework 选 most-specific(参数最多)。
            var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                            .OrderByDescending(c => c.GetParameters().Length);
            foreach (var ctor in ctors)
            {
                var parameters = ctor.GetParameters();
                var args = new object?[parameters.Length];
                bool allResolved = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    var pType = parameters[i].ParameterType;
                    if (services.TryGetValue(pType, out var exact))
                    {
                        args[i] = exact;
                        continue;
                    }
                    // 回查兜底:支持基类 / 接口注册(跟 UriArgs.GetService<T> 同款)。
                    object? match = null;
                    foreach (var kv in services)
                    {
                        if (pType.IsInstanceOfType(kv.Value)) { match = kv.Value; break; }
                    }
                    if (match != null) { args[i] = match; continue; }
                    if (parameters[i].HasDefaultValue) { args[i] = parameters[i].DefaultValue; continue; }

                    allResolved = false;
                    break;
                }
                if (allResolved) return ctor.Invoke(args);
            }

            throw new InvalidOperationException(
                $"[SmartActivator] {type.Name} 的所有 public ctor 都无法从 services dict({services.Count} 项)" +
                $"解析全部参数。检查 ScopeContext.AddService 是否注入了所需类型。");
        }

        /// <summary>
        /// 💥 实例化并同时注入属性的语法糖
        /// </summary>
        public static object CreateAndInject(Type type, object[]? constructorArgs, Dictionary<string, object?> properties)
        {
            // 无参路径走 ComponentRegistry.CreateInstance 的编译委托缓存 (蓝图主路径,占绝大多数)。
            // 带参 ctor 是边缘场景(SmartActivator 内部 ctor 注入回落分支自己用 ConstructorInfo.Invoke),
            // 这里走 Activator 通用反射保持兼容。
            var instance = (constructorArgs == null || constructorArgs.Length == 0)
                ? ComponentRegistry.CreateInstance(type)
                : Activator.CreateInstance(type, constructorArgs)!;
            InjectProperties(instance, properties);
            return instance;
        }

        /// <summary>
        /// 💥 把 StyleModel 物化为 trait 实例。
        /// 选取顺序:
        ///   1. Preset 不空 → 按名查 public static field/property,直接拿到那个实例(支持 record / 位置参数 trait)。
        ///      若同时设了 Properties,会在拷贝(record 用 with {...} 模拟)上覆盖字段。
        ///   2. 否则尝试无参 ctor + InjectProperties (传统 POCO trait 流程)。
        /// 找不到合适入口直接抛,蓝图阶段就翻车,免得运行时神秘 NRE。
        /// </summary>
        public static object MaterializeTrait(Type type, StyleModel def)
        {
            if (!string.IsNullOrEmpty(def.Preset))
            {
                object? presetInstance = ResolveStaticPreset(type, def.Preset!);
                if (presetInstance == null)
                    throw new InvalidOperationException($"[蓝图错误] {type.Name} 上找不到 public static '{def.Preset}' 预设字段或属性。");

                if (def.Properties == null || def.Properties.Count == 0)
                    return presetInstance;

                // 💥 单例污染兜底:Preset + Properties 同时存在时,先克隆出新实例再 InjectProperties。
                // 不做克隆的话 InjectProperties 直接写到 ResolveStaticPreset 拿到的 public static 字段单例上,
                // 整个进程内所有引用该 Preset 的蓝图都被污染 —— 一类难以复现的隐性 bug。
                // record 走 <Clone>$ (浅拷贝,init-only 字段都带过来) 是 99% 真实场景。
                // 非 record 类只能退到 ctor 重建路径 (无参 ctor 优先,失败则沿用旧 silent skip 兼容路径)。
                var cloned = TryCloneRecord(presetInstance, type);
                if (cloned != null)
                {
                    InjectProperties(cloned, def.Properties);
                    return cloned;
                }
                if (type.GetConstructor(Type.EmptyTypes) != null)
                {
                    // 非 record 但有无参 ctor:重新 new 一个,把 Properties 应用上去。
                    // 注意:preset 上未列在 Properties 里的字段值会丢,这是非 record 场景的代价 ——
                    // 业务想保留 preset 全部默认值,自己注册成 record 类型即可。
                    Console.WriteLine($"[Hevo 蓝图警告] {type.Name} 不是 record,Preset+Properties 退化为 ctor+InjectProperties 路径,preset 未列出的字段不会被复制。");
                    return CreateAndInject(type, null, def.Properties);
                }
                // 走兼容路径:写穿 preset 单例并打印警告 (旧行为保留)。
                Console.WriteLine($"[Hevo 蓝图警告] {type.Name} 既非 record 也无无参 ctor,Preset+Properties 仍写穿到 public static 单例,可能污染共享状态。");
                InjectProperties(presetInstance, def.Properties);
                return presetInstance;
            }

            // 优先无参 ctor + InjectProperties;失败回落到主构造按参数名从 Properties 拼实参 ——
            // 兼容 positional record 这种没无参 ctor 的 trait(如 ScaleStrategyTrait(IScale, IScale))。
            // 编辑器侧把 IScale 这类接口参数走"实例选择器"存进 Properties,这里按名找回。
            var props = def.Properties ?? new Dictionary<string, object?>();
            if (type.GetConstructor(Type.EmptyTypes) != null)
                return CreateAndInject(type, null, props);

            var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"[蓝图错误] {type.Name} 既无无参 ctor 也无可用主构造,Materialize 失败。");

            var ctorParams = ctor.GetParameters();
            var args = new object?[ctorParams.Length];
            for (int i = 0; i < ctorParams.Length; i++)
            {
                var p = ctorParams[i];
                if (p.Name != null && props.TryGetValue(p.Name, out var raw))
                {
                    var safe = CoerceValue(raw, p.ParameterType);
                    // SkipInjection 路径:接口字段无多态 Converter。ctor 必填参数走默认值,
                    // 蓝图作者得显式 Preset 才能拿到正确实例;返 null 跟基础类型 ctor 默认值同语义。
                    args[i] = ReferenceEquals(safe, SkipInjection)
                        ? (p.HasDefaultValue ? p.DefaultValue
                           : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType)
                           : null)
                        : safe;
                }
                else if (p.HasDefaultValue)
                    args[i] = p.DefaultValue;
                else if (p.ParameterType.IsValueType)
                    args[i] = Activator.CreateInstance(p.ParameterType);
                else
                    args[i] = null;
            }
            var built = ctor.Invoke(args);
            // ctor 之外的字段仍走 setter 注入(例如 record 上额外的 init 属性,或非 ctor 参数)
            var extraProps = props.Where(kv => !ctorParams.Any(cp =>
                string.Equals(cp.Name, kv.Key, StringComparison.OrdinalIgnoreCase)))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            if (extraProps.Count > 0) InjectProperties(built, extraProps);
            return built;
        }

        private static object? ResolveStaticPreset(Type type, string name)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase;
            var field = type.GetField(name, F);
            if (field != null) return field.GetValue(null);
            var prop = type.GetProperty(name, F);
            if (prop != null) return prop.GetValue(null);
            return null;
        }

        // record 自动生成的浅拷贝方法,签名等价 `protected virtual T <Clone>$()`。
        // 反射调出来的实例就是 `instance with { }` 表达式的运行时形态:跟原始单例完全独立,
        // 后续 InjectProperties 写穿不再污染原 Preset。
        // ConcurrentDictionary 缓存 MethodInfo —— 类型 → method 是终身映射,不会变。
        private static readonly ConcurrentDictionary<Type, MethodInfo?> _cloneMethodCache = new();

        private static object? TryCloneRecord(object src, Type type)
        {
            var clone = _cloneMethodCache.GetOrAdd(type, static t =>
                t.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
            return clone?.Invoke(src, null);
        }

        /// <summary>
        /// 💥 终极安全类型转换转换器
        /// 完美处理 Nullable<T>, Enum, 字符串数字互转等 JSON 常见坑点
        /// </summary>
        /// <summary>
        /// <see cref="CoerceValue"/> 返回此 sentinel 表示"显式跳过赋值"——
        /// 主要用于"接口字段没注册多态 Converter"的死路:JSON 反序列化必失败,
        /// 但 Preset 路径下原值是有效的(典型 ScaleStrategyTrait.Default.DomainScale = CategoryScale.Edge),
        /// 不该被 null 覆写。<see cref="InjectProperties"/> 看到本 sentinel 不调 setter,默认值保留。
        /// <para>
        /// 注意:这是 framework 唯一保留的"silent skip" 路径(语义化的"这字段就是不该被注入"),
        /// 不要拿它来兜底掩盖配置错 —— 配错走 InjectProperties 的 fail-fast 抛异常。
        /// </para>
        /// </summary>
        internal static readonly object SkipInjection = new();

        // 设计 —— 两层分诊:JsonElement 拆包 vs 松类型强转
        //
        // 历史教训:旧版 SafeChangeType 把 6 个步骤 (Nullable / IsInstanceOf / IsEnum / JsonElement / TypeConverter /
        // Convert.ChangeType) 摊成一个 cascade,各步顺序匹配。问题:enum / TypeConverter / Nullable 等步骤如果
        // 排在 JsonElement 步骤之前优先吃,但又不认 JsonElement,就会调 Enum.ToObject(Type, JsonElement) 这种
        // 抛 ArgumentException 的 API —— 异常被外层 InjectProperties 的 try/catch 吞掉,属性悄无声息留默认值。
        // 典型踩坑:scanner.blueprint.json 配 "MergeMode": "WhenAll",composite 实际跑 WhenAny。
        //
        // 重构后:
        //   - 把"是不是 JsonElement"跟"目标类型怎么转"分两层 —— JsonElement 拆包前置(走 ConvertFromJsonElement),
        //     复杂类型走 STJ.Deserialize,简单 kind 拆成 native (.NET 原生)后递归 CoerceValue,共享 enum /
        //     TypeConverter / Convert.ChangeType 后续路径。后续步骤永远看不到 JsonElement,这类连环坑一次性消除。
        //   - 改名 SafeChangeType → CoerceValue:旧名暗示"出错也不抛"的 silent 语义,跟 fail-fast 矛盾。
        //     现在转换失败一律抛出由 InjectProperties 上传出去;唯一保留的"silent skip" 是 SkipInjection sentinel
        //     (语义化的"这字段就是不该被注入",非"出错了悄悄忽略")。
        private static object? CoerceValue(object? value, Type conversionType)
        {
            // 1. null 处理 —— 显式拒绝 null → 非 Nullable 值类型,避免 setter 把 null unbox 抛 NRE 这种隐晦错误,
            //    在 CoerceValue 一层给出清晰诊断信息。Nullable<T> / 引用类型放行(JsonElement Null 递归回来也走这条)。
            if (value == null)
            {
                if (conversionType.IsValueType && Nullable.GetUnderlyingType(conversionType) == null)
                    throw new InvalidOperationException(
                        $"[Hevo 蓝图配置错] 不能把 null 赋给非 Nullable 值类型 {conversionType.Name} —— " +
                        $"删掉这个 Properties key,或者把字段类型改成 Nullable<{conversionType.Name}>。");
                return null;
            }

            // 2. 类型已对齐(原生 string / 数值 / record 强类型 Properties 直接放过)
            if (conversionType.IsInstanceOfType(value))
                return value;

            // 3. JsonElement 拆包先行 —— 让后续 step 永远只面对 native 值。
            //    注意:Nullable<T> 拆壳要放在 JsonElement 之后,否则 int? 被剥成 int 后,JsonElement Null 递归
            //    回来撞 step 1 的"非 Nullable 拒绝 null" 误伤合法的"Nullable 字段接 JSON null"用例。
            if (value is System.Text.Json.JsonElement jsonElement)
                return ConvertFromJsonElement(jsonElement, conversionType);

            // 4. Nullable<T> 拆壳 —— 后续 step 拿 underlying type 处理(value 非 null 时拆壳合法,装回 Nullable<T>
            //    setter 由 Expression.Convert 自动 box)
            if (conversionType.IsGenericType && conversionType.GetGenericTypeDefinition() == typeof(Nullable<>))
                conversionType = Nullable.GetUnderlyingType(conversionType)!;

            // 5. string → enum (走到这里 value 已经是 .NET native;数值 → enum 由 Enum.ToObject 兜底)
            if (conversionType.IsEnum)
            {
                if (value is string strValue)
                    return Enum.Parse(conversionType, strValue, ignoreCase: true);
                return Enum.ToObject(conversionType, value);
            }

            // 6. TypeConverter (Color / Point 等带 converter 的复杂类型 —— 通常走 step 3 的 STJ 路径,
            //    但程序化注入 (Properties["foo"] = strValue) 时这里兜底)
            TypeConverter converter = TypeDescriptor.GetConverter(conversionType);
            if (converter.CanConvertFrom(value.GetType()))
                return converter.ConvertFrom(value);

            // 7. IConvertible 基础类型互转(long→int、double→float、bool→string 等)
            return Convert.ChangeType(value, conversionType);
        }

        // JsonElement 专用拆包入口 —— 三层分诊:
        //   1. 接口字段 + 无多态 Converter + 非集合接口 → SkipInjection
        //      (典型 IScale / IZoomStrategy 等策略接口,STJ 反序列化必失败;Preset 路径已填默认值,跳过 setter 即可。
        //      集合接口 IReadOnlyList<T> 等走 IEnumerable 白名单放行,STJ 完全支持 Array → List 反序列化。)
        //   2. 已注册 Converter / Object / Array 走 STJ.Deserialize —— 复杂类型 (LineStyle / List<UpstreamSpec> /
        //      Color / IHevoBrush) 一律走 BlueprintJsonOptions 全局选项,保留多态 / NaN 字面量 / case-insensitive 约定。
        //   3. 其它(String / Number / True / False / Null) → 拆成 native,递归 CoerceValue 让 enum / TypeConverter /
        //      Convert.ChangeType 路径接力。
        //
        // 异常策略:fail-fast。STJ 反序列化失败原样上抛(用户看到清晰的"哪个字段哪个 Path 反序列化挂了"诊断);
        // 唯一例外是接口字段的 NotSupportedException → 退化 SkipInjection(显式契约,Preset 默认值保留)。
        private static object? ConvertFromJsonElement(System.Text.Json.JsonElement jsonElement, Type conversionType)
        {
            if (conversionType.IsInterface
                && !HasRegisteredConverter(conversionType)
                && !typeof(System.Collections.IEnumerable).IsAssignableFrom(conversionType))
            {
                return SkipInjection;
            }

            bool shouldTryConverter =
                HasRegisteredConverter(conversionType)
                || jsonElement.ValueKind == System.Text.Json.JsonValueKind.Object
                || jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array;
            if (shouldTryConverter)
            {
                try
                {
                    var rawJson = jsonElement.GetRawText();
                    return System.Text.Json.JsonSerializer.Deserialize(rawJson, conversionType,
                        Converters.BlueprintJsonOptions.Default);
                }
                catch (NotSupportedException) when
                    (conversionType.IsInterface && !HasRegisteredConverter(conversionType))
                {
                    // STJ 显式拒绝接口反序列化 → SkipInjection 保留 Preset 默认值;非接口的 NotSupported 重抛 fail-fast。
                    return SkipInjection;
                }
                // 其它异常(JsonException / 业务 converter 异常 / etc.)原样上抛 —— InjectProperties 不再 swallow,
                // 蓝图作者能在装配期直接看到具体错误,免得运行时神秘默认值。
            }

            // 拆成 .NET native 再递归 CoerceValue —— 复用 enum-string-parse / TypeConverter / ChangeType。
            // Number 优先 long(整数 ≤ 2^63 精确),溢出 / 小数走 double。
            object? native = jsonElement.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => jsonElement.GetString(),
                System.Text.Json.JsonValueKind.Number => jsonElement.TryGetInt64(out var l) ? (object)l : jsonElement.GetDouble(),
                System.Text.Json.JsonValueKind.True   => true,
                System.Text.Json.JsonValueKind.False  => false,
                System.Text.Json.JsonValueKind.Null   => null,
                _                                     => jsonElement.GetRawText(),
            };
            return CoerceValue(native, conversionType);
        }

        /// <summary>
        /// 该类型是否已在 <see cref="Converters.BlueprintJsonOptions.Default"/> 里登记自定义 converter ——
        /// 决定 JsonElement → 复杂类型 是否走 JsonSerializer.Deserialize 路径。
        /// 缓存结果避免每次注入都遍历 converter 列表。
        /// </summary>
        private static readonly ConcurrentDictionary<Type, bool> _hasConverterCache = new();
        private static bool HasRegisteredConverter(Type t)
            => _hasConverterCache.GetOrAdd(t, static type =>
            {
                foreach (var c in Converters.BlueprintJsonOptions.Default.Converters)
                    if (c.CanConvert(type)) return true;
                return false;
            });
    }
}
