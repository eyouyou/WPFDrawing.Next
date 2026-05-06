using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Hevo.Charting.LowCode.Designer.Python
{
    /// <summary>
    /// §D2.2 Python signature → .NET <see cref="Delegate"/> 类型的推断映射。
    ///
    /// <para>
    /// <b>动机</b>:Python 是动态类型语言,函数本身没有静态签名。但蓝图层 (协议 §K) 要求 handler
    /// 注册时绑定到一个**已知的 .NET 委托类型** (Func&lt;TIn, TOut&gt;),才能让 Feature Properties
    /// 里 Delegate 类型属性的 IsAssignableFrom 校验通过。
    /// </para>
    ///
    /// <para>
    /// <b>解法</b>:分析师在 Python 装饰器声明签名串(纯字符串,不需要 Python 运行时即可解析):
    /// </para>
    /// <code>
    /// @register("ma_close_20", signature="(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]")
    /// def moving_average(close): ...
    /// </code>
    /// <para>
    /// PythonTypeMapper 解析签名串 → 构造对应 <c>Func&lt;ReadOnlyMemory&lt;double&gt;, ReadOnlyMemory&lt;double&gt;&gt;</c>
    /// 委托类型 → 跟 Feature 属性的目标类型对齐。
    /// </para>
    ///
    /// <para>
    /// <b>支持的类型</b>(MVP):
    /// </para>
    /// <list type="bullet">
    ///   <item>标量:<c>int / long / float / double / bool / string</c> (含别名 <c>byte</c>)</item>
    ///   <item>时间:<c>DateTime</c></item>
    ///   <item>列流:<c>ReadOnlyMemory[T]</c> (Python 端是 numpy.ndarray;mapping 由 IPythonRuntime 实现做)</item>
    ///   <item>void(返回值):<c>None</c> 或省略 <c>-&gt;</c></item>
    /// </list>
    /// 不支持的(暂):泛型自定义类型 / 多返回值 / Optional / Union。
    /// </summary>
    public static class PythonTypeMapper
    {
        // 签名形态:"(T1, T2, ...) -> TR"  或  "(T1, T2, ...)"  或  "() -> TR"
        // T 元素:identifier 可带 [...] 内嵌泛型,例 "ReadOnlyMemory[double]"。
        private static readonly Regex _signatureRegex = new(
            @"^\s*\(\s*(?<params>.*?)\s*\)\s*(->\s*(?<ret>[\w\[\]\.]+))?\s*$",
            RegexOptions.Compiled);

        /// <summary>
        /// 解析签名串 → .NET 委托类型 (返回 typeof(Action&lt;...&gt;) 或 typeof(Func&lt;..., TR&gt;))。
        /// 签名非法或类型未识别返回 null,让调用方决定是 silent skip 还是显式拒。
        /// </summary>
        public static Type? ResolveDelegateType(string signature)
        {
            if (string.IsNullOrWhiteSpace(signature)) return null;
            var m = _signatureRegex.Match(signature);
            if (!m.Success) return null;

            var paramsRaw = m.Groups["params"].Value.Trim();
            var retRaw    = m.Groups["ret"].Success ? m.Groups["ret"].Value.Trim() : null;

            // 参数列表 (空字符串 = 0 参数)
            var paramTypes = string.IsNullOrEmpty(paramsRaw)
                ? Array.Empty<Type>()
                : paramsRaw.Split(',').Select(s => MapType(s.Trim())).ToArray();
            if (paramTypes.Any(t => t == null)) return null;

            // 返回类型:省略 "->" 或 "-> None" / "-> void" 都当 void
            bool isVoid = retRaw == null
                       || string.Equals(retRaw, "None", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(retRaw, "void", StringComparison.OrdinalIgnoreCase);

            try
            {
                if (isVoid)
                {
                    return paramTypes.Length == 0
                        ? typeof(Action)
                        : Expression.GetActionType(paramTypes!);
                }
                else
                {
                    var ret = MapType(retRaw!);
                    if (ret == null) return null;
                    var all = new Type[paramTypes.Length + 1];
                    for (int i = 0; i < paramTypes.Length; i++) all[i] = paramTypes[i]!;
                    all[paramTypes.Length] = ret;
                    return Expression.GetFuncType(all);
                }
            }
            catch (ArgumentException)
            {
                // GetActionType / GetFuncType 在 >16 参数时抛
                return null;
            }
        }

        /// <summary>
        /// 类型名 → CLR Type 映射。识别:
        /// 基础类型(int/long/float/double/bool/string/byte/DateTime/object) 及别名,
        /// 一层泛型 <c>ReadOnlyMemory[T]</c> 形态。
        /// </summary>
        public static Type? MapType(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();

            // 一层泛型 ReadOnlyMemory[T]
            int lb = name.IndexOf('[');
            if (lb > 0 && name.EndsWith("]"))
            {
                var head = name.Substring(0, lb).Trim();
                var inner = name.Substring(lb + 1, name.Length - lb - 2).Trim();
                if (string.Equals(head, "ReadOnlyMemory", StringComparison.OrdinalIgnoreCase))
                {
                    var elem = MapType(inner);
                    return elem == null ? null : typeof(ReadOnlyMemory<>).MakeGenericType(elem);
                }
                // 暂不展开其他泛型(Memory / Span / IList 等)。需要再加。
                return null;
            }

            // 基础标量
            switch (name.ToLowerInvariant())
            {
                case "int":      case "int32":  return typeof(int);
                case "long":     case "int64":  return typeof(long);
                case "float":                   return typeof(float);
                case "double":                  return typeof(double);
                case "bool":     case "boolean":return typeof(bool);
                case "string":   case "str":    return typeof(string);
                case "byte":                    return typeof(byte);
                case "datetime":                return typeof(DateTime);
                case "object":                  return typeof(object);
                case "none":     case "void":   return typeof(void);
            }
            return null;
        }
    }
}
