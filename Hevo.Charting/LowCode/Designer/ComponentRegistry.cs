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

        public static Type Resolve(string typeName)
        {
            if (_types.TryGetValue(typeName, out var type)) return type;
            throw new Exception($"[VM 致命错误] 未知的组件类型: {typeName}。请确保已注册。");
        }
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
