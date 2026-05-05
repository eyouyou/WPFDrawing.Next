using System.ComponentModel;
using System.Reflection;

namespace Hevo.Charting.LowCode.Designer
{
    public static class ComponentRegistry
    {
        private static readonly Dictionary<string, Type> _types = new();

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
            throw new Exception($"[VM 致命错误] 未知的组件类型: {typeName}。请确保已注册。");
        }

        /// <summary>查询是否已登记;蓝图加载前的预检场景用。</summary>
        public static bool IsRegistered(string typeName) => _types.ContainsKey(typeName);

        /// <summary>枚举所有已登记的 (alias, type) 对。GraphViewer 拉组件选择列表时用。</summary>
        public static IEnumerable<KeyValuePair<string, Type>> ListAll() => _types;

        /// <summary>清空登记表 (单元测试或动态重载场景用)。</summary>
        public static void Reset() => _types.Clear();
    }

    public static class SmartActivator
    {
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
                // 1. 查找对应的公开实例属性
                PropertyInfo? prop = targetType.GetProperty(
                    kvp.Key,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                if (prop == null || !prop.CanWrite) continue;

                try
                {
                    // 2. 💥 核心魔法：安全类型转换
                    object? safeValue = SafeChangeType(kvp.Value, prop.PropertyType);

                    // 3. 反射赋值
                    prop.SetValue(target, safeValue);
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
            var instance = Activator.CreateInstance(type, constructorArgs ?? Array.Empty<object>())!;
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
                object? instance = ResolveStaticPreset(type, def.Preset!);
                if (instance == null)
                    throw new InvalidOperationException($"[蓝图错误] {type.Name} 上找不到 public static '{def.Preset}' 预设字段或属性。");

                if (def.Properties != null && def.Properties.Count > 0)
                {
                    // record 的 init-only 属性反射 SetValue 在 .NET 5+ 仍可写穿透,直接覆盖即可;
                    // 对 immutable trait 实例需谨慎(会污染共享单例),约定:Preset + Properties 组合时
                    // 业务侧应自觉只对“非全局共享”的预设这样用。
                    InjectProperties(instance, def.Properties);
                }
                return instance;
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
