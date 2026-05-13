using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Hevo.Charting.PythonNet
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
        // @register("name") / @register('name') / @register("name", signature="...") /
        // @register("name", signature="...", inputs=['h','l','c'], incremental=True)
        // 接受位置参数 1 个 string + 可选 signature / inputs / incremental kwarg(顺序无关)。
        // signature / inputs / incremental 任一可缺;inputs 元素只接受单 / 双引号字面量字符串。
        // incremental 接受 True / False(裸 identifier)。
        private static readonly Regex _decoratorRegex = new(
            """
            @register\s*\(\s*
                (?:"(?<name>[^"]*)"|'(?<name>[^']*)')
                (?<kwargs>(?:\s*,\s*\w+\s*=\s*(?:"[^"]*"|'[^']*'|\[[^\]]*\]|True|False))*)
                \s*\)
            \s*\r?\n
            \s*def\s+(?<func>\w+)\s*\(
            """,
            RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

        // 从 kwargs 子串里抓 signature= / inputs= / incremental=
        private static readonly Regex _signatureKwargRegex = new(
            "signature\\s*=\\s*(?:\"(?<v>[^\"]*)\"|'(?<v>[^']*)')",
            RegexOptions.Compiled);
        private static readonly Regex _inputsKwargRegex = new(
            "inputs\\s*=\\s*\\[(?<items>[^\\]]*)\\]",
            RegexOptions.Compiled);
        // §D2.6.4 incremental kwarg(裸 True/False;不识别动态 expression)
        private static readonly Regex _incrementalKwargRegex = new(
            @"incremental\s*=\s*(?<v>True|False)\b",
            RegexOptions.Compiled);
        // 列表里的元素 'a' / "a"
        private static readonly Regex _listItemRegex = new(
            "(?:\"(?<v>[^\"]*)\"|'(?<v>[^']*)')",
            RegexOptions.Compiled);

        /// <summary>
        /// 扫单个 .py 文件源码,返回所有 <c>@register</c> 命中。<paramref name="text"/> 是 .py 文件全文本。
        /// </summary>
        public static IReadOnlyList<PythonHandlerDescriptor> ScanText(string text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<PythonHandlerDescriptor>();
            var result = new List<PythonHandlerDescriptor>();
            foreach (Match m in _decoratorRegex.Matches(text))
            {
                var kwargs = m.Groups["kwargs"].Value;
                string? signature = null;
                IReadOnlyList<string>? inputs = null;
                bool isIncremental = false;

                var sigMatch = _signatureKwargRegex.Match(kwargs);
                if (sigMatch.Success) signature = sigMatch.Groups["v"].Value;

                var inMatch = _inputsKwargRegex.Match(kwargs);
                if (inMatch.Success)
                {
                    var items = new List<string>();
                    foreach (Match it in _listItemRegex.Matches(inMatch.Groups["items"].Value))
                        items.Add(it.Groups["v"].Value);
                    inputs = items;
                }

                var incMatch = _incrementalKwargRegex.Match(kwargs);
                if (incMatch.Success)
                    isIncremental = string.Equals(incMatch.Groups["v"].Value, "True", StringComparison.Ordinal);

                result.Add(new PythonHandlerDescriptor
                {
                    Name = m.Groups["name"].Value,
                    FunctionName = m.Groups["func"].Value,
                    Signature = signature,
                    Inputs = inputs,
                    IsIncremental = isIncremental,
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
