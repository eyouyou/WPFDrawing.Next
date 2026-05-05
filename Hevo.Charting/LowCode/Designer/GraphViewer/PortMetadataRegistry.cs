using System.ComponentModel;
using System.Reflection;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// 端口侧元数据(方向 / 注释)的"GraphViewer 层覆盖表"。
    /// <para>
    /// 设计目标:不污染 Hevo.Charting 框架源码即可给现有 Feature 标方向 + 加端口注释。
    /// 反射时优先级:本注册表 &gt; <c>[PortDirection]</c> 属性 &gt; 默认 (Input + 无注释)。
    /// 业务侧也可在启动时为自定义 Feature 调一次 Register,GraphViewer 立即生效。
    /// </para>
    /// </summary>
    public static class PortMetadataRegistry
    {
        // (类型, 属性名) → 方向覆盖
        private static readonly Dictionary<(Type, string), PortDirection> _direction = new();
        // (类型, 属性名) → 端口注释覆盖
        private static readonly Dictionary<(Type, string), string> _description = new();

        /// <summary>批量登记某类型的某些属性为 Output 端口。</summary>
        public static void RegisterOutputs(Type type, params string[] propertyNames)
        {
            foreach (var n in propertyNames) _direction[(type, n)] = PortDirection.Output;
        }

        /// <summary>泛型快捷:<c>RegisterOutputs&lt;UniversalAutoScaleFeature&gt;("YRangePort", ...)</c>。</summary>
        public static void RegisterOutputs<T>(params string[] propertyNames)
            => RegisterOutputs(typeof(T), propertyNames);

        /// <summary>登记某属性的端口注释。同一对二次登记时新覆盖旧。</summary>
        public static void RegisterDescription(Type type, string propertyName, string description)
            => _description[(type, propertyName)] = description;

        public static void RegisterDescription<T>(string propertyName, string description)
            => RegisterDescription(typeof(T), propertyName, description);

        /// <summary>查方向。优先级:registry → <see cref="PortDirectionAttribute"/> → 默认 Input。</summary>
        public static PortDirection ResolveDirection(Type ownerType, PropertyInfo property)
        {
            // 沿继承链回溯查 (registry 是 nominal 类型 key,子类查不到时翻祖先)
            for (var t = ownerType; t != null && t != typeof(object); t = t.BaseType)
            {
                if (_direction.TryGetValue((t, property.Name), out var d)) return d;
            }
            var attr = property.GetCustomAttribute<PortDirectionAttribute>();
            return attr?.Direction ?? PortDirection.Input;
        }

        /// <summary>查注释。优先级:registry → <c>[Description]</c> → null。</summary>
        public static string? ResolveDescription(Type ownerType, PropertyInfo property)
        {
            for (var t = ownerType; t != null && t != typeof(object); t = t.BaseType)
            {
                if (_description.TryGetValue((t, property.Name), out var d)) return d;
            }
            return property.GetCustomAttribute<DescriptionAttribute>()?.Description;
        }
    }
}
