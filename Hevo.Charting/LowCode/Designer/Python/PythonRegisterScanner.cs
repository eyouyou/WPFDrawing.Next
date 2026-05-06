using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Hevo.Charting.LowCode.Designer.Python
{
    /// <summary>
    /// §D2.1 Python AutoDiscover 的"无运行时"扫描器:用正则在 .py 文件文本里找
    /// <c>@register("name", signature="...")</c> 装饰器声明,产出 <see cref="PythonHandlerDescriptor"/>
    /// 列表。<b>不需要 Python 解释器在线</b>——DryRun 阶段就能跑,蓝图诊断也基于此。
    ///
    /// <para>
    /// <b>为什么 regex 而不调 Python AST</b>:DryRun 默认无 Python 运行时(Null 实现);要做静态校验
    /// (handler 名是否已声明 / 签名串是否合法) 必须无依赖。装饰器写法是约定,只接受
    /// "<c>@register(name="..."[, signature="..."])</c>" 这种字面量形态(动态拼字符串作 name 的不识别)。
    /// </para>
    /// </summary>
    public static class PythonRegisterScanner
    {
        // @register("name") / @register('name') / @register("name", signature="...")
        // 接受位置参数 1 个 string + 可选 signature kwarg。
        private static readonly Regex _decoratorRegex = new(
            """
            @register\s*\(\s*
                (?:"(?<name>[^"]*)"|'(?<name>[^']*)')
                (?:\s*,\s*signature\s*=\s*(?:"(?<sig>[^"]*)"|'(?<sig>[^']*)'))?
                \s*\)
            \s*\r?\n
            \s*def\s+(?<func>\w+)\s*\(
            """,
            RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

        /// <summary>
        /// 扫单个 .py 文件源码,返回所有 <c>@register</c> 命中。<paramref name="text"/> 是 .py 文件全文本。
        /// </summary>
        public static IReadOnlyList<PythonHandlerDescriptor> ScanText(string text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<PythonHandlerDescriptor>();
            var result = new List<PythonHandlerDescriptor>();
            foreach (Match m in _decoratorRegex.Matches(text))
            {
                result.Add(new PythonHandlerDescriptor
                {
                    Name = m.Groups["name"].Value,
                    FunctionName = m.Groups["func"].Value,
                    Signature = m.Groups["sig"].Success ? m.Groups["sig"].Value : null,
                });
            }
            return result;
        }

        /// <summary>
        /// 扫一个目录下所有 *.py(非递归),返回每个文件路径 → 该文件内所有 handler descriptor。
        /// 子目录默认不递归(分析师约定:每个 .py 是一个独立模块,不要复杂的包层级)。
        /// </summary>
        public static IReadOnlyDictionary<string, IReadOnlyList<PythonHandlerDescriptor>> ScanDirectory(string directory)
        {
            var result = new Dictionary<string, IReadOnlyList<PythonHandlerDescriptor>>();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return result;
            foreach (var file in Directory.EnumerateFiles(directory, "*.py", SearchOption.TopDirectoryOnly))
            {
                var text = File.ReadAllText(file);
                var descriptors = ScanText(text);
                if (descriptors.Count > 0) result[file] = descriptors;
            }
            return result;
        }
    }
}
