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
        /// 💥 将字典中的松散数据，安全、强类型地注入到目标对象中
        /// </summary>
        /// <param name="target">要被注入的 C# 实例 (如 ChartFeature)</param>
        /// <param name="properties">从 JSON 反序列化出来的松散字典</param>
        public static void InjectProperties(object target, Dictionary<string, object?>? properties)
        {
            if (target == null || properties == null || properties.Count == 0) return;

            Type targetType = target.GetType();

            foreach (var kvp in properties)
            {
                var entry = GetSetterEntry(targetType, kvp.Key);
                if (entry == null) continue;   // 属性不存在 / 不可写,静默 skip 跟旧行为对齐

                try
                {
                    object? safeValue = SafeChangeType(kvp.Value, entry.PropertyType);
                    entry.Setter(target, safeValue);
                }
                catch (Exception ex)
                {
                    // 在低代码引擎中，某个属性注入失败不应导致整个图表崩溃，打印警告即可
                    Console.WriteLine($"[Hevo 注入警告] 无法将属性 {kvp.Key} 注入到 {targetType.Name}: {ex.Message}");
                }
            }
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
                    args[i] = SafeChangeType(raw, p.ParameterType);
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
        private static object? SafeChangeType(object? value, Type conversionType)
        {
            if (value == null) return null;

            // 1. 处理 Nullable<T> (例如 int?)
            if (conversionType.IsGenericType && conversionType.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                conversionType = Nullable.GetUnderlyingType(conversionType)!;
            }

            // 2. 类型已经匹配，直接返回
            if (conversionType.IsInstanceOfType(value))
            {
                return value;
            }

            // 3. 处理字符串转 Enum (例如 JSON 里的 "Bottom" 转为 AxisPlacement.Bottom)
            if (conversionType.IsEnum)
            {
                if (value is string strValue)
                {
                    return Enum.Parse(conversionType, strValue, ignoreCase: true);
                }
                return Enum.ToObject(conversionType, value);
            }

            // 4. 处理 System.Text.Json 的 JsonElement 降级 (如果你用了官方 JSON 库，这步极其救命)
            if (value is System.Text.Json.JsonElement jsonElement)
            {
                // 4.0 复杂目标走 BlueprintJsonOptions 的 Converter / record 默认反序列化:
                //   - 已登记 Converter 的类型 (Color / IHevoBrush) — 任何 ValueKind 都试一遍
                //   - Object / Array kind — 通常是 record / 集合 / 嵌套 trait,System.Text.Json
                //     默认按 primary ctor 反序列化 record 即可还原 (LineStyle / AxisStyleTrait /
                //     HevoPen 等 ChartFeature.Properties 里的 trait 都走这一支)。
                //   - String / Number / Bool / Null kind 且无 Converter — 走轻量 GetValueFromJsonElement
                //     (旧路径,处理 IConvertible / TypeConverter 兜底)。
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
                    catch { /* 静默回落到通用拆解,InjectProperties 顶层有 warning 打印兜底 */ }
                }
                return GetValueFromJsonElement(jsonElement, conversionType);
            }

            // 5. 尝试使用对象的默认 TypeConverter (支持复杂类型如 Color, Point 等)
            TypeConverter converter = TypeDescriptor.GetConverter(conversionType);
            if (converter.CanConvertFrom(value.GetType()))
            {
                return converter.ConvertFrom(value);
            }

            // 6. 终极兜底：强行利用 IConvertible 进行基础类型转换 (处理 long 转 int, double 转 float 等)
            return Convert.ChangeType(value, conversionType);
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

        /// <summary>
        /// 剥离 System.Text.Json 的动态外壳
        /// </summary>
        private static object? GetValueFromJsonElement(System.Text.Json.JsonElement element, Type targetType)
        {
            return element.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number =>
                    targetType == typeof(int) ? element.GetInt32() :
                    targetType == typeof(float) ? element.GetSingle() :
                    targetType == typeof(double) ? element.GetDouble() :
                    targetType == typeof(long) ? element.GetInt64() :
                    Convert.ChangeType(element.GetRawText(), targetType), // 兜底

                System.Text.Json.JsonValueKind.String =>
                    targetType.IsEnum ? Enum.Parse(targetType, element.GetString()!, true) :
                    element.GetString(),

                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.Null => null,
                _ => null
            };
        }
    }
}
