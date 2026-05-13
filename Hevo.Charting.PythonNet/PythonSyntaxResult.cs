namespace Hevo.Charting.PythonNet
{
    /// <summary>
    /// §D2.5.E2E Python 编辑器实时语法检查 —— 调 Python <c>compile(source, filename, 'exec')</c>
    /// 抓 SyntaxError 后翻成结构化结果。
    /// </summary>
    /// <param name="Ok">true = 语法合法,false = 有 SyntaxError。</param>
    /// <param name="Message">SyntaxError.msg(无错误时 null)。</param>
    /// <param name="Line">SyntaxError.lineno(1-based)。null = 编译器没给。</param>
    /// <param name="Column">SyntaxError.offset(1-based,部分 Python 版本是 0-based,需要调用方归一)。</param>
    /// <param name="ExceptionType">非 SyntaxError 的其他异常类型名(IndentationError / TabError / etc.)。</param>
    public sealed record PythonSyntaxResult(
        bool Ok,
        string? Message = null,
        int? Line = null,
        int? Column = null,
        string? ExceptionType = null)
    {
        public static PythonSyntaxResult Success { get; } = new(true);
    }
}
