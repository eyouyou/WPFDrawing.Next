namespace Hevo.Charting.PythonNet
{
    /// <summary>
    /// §D2.4 Python traceback → .NET 异常的包装。
    /// 当 Python 函数在 <see cref="IPythonModule.Invoke"/> 内部抛出 Python 异常时，
    /// IPythonRuntime 实现应将其包装成此异常向 .NET 层传播，以便：
    /// <list type="bullet">
    ///   <item>caller 能区分"Python 自身出错"和".NET 调用基础设施出错"。</item>
    ///   <item>DryRun 阶段的 import 检查可以捕获并报告为 <see cref="BlueprintDiagnostic"/>。</item>
    ///   <item>UI 层可以在错误面板直接展示 Python traceback 而不是 .NET 堆栈。</item>
    /// </list>
    /// </summary>
    public sealed class PythonDiagnosticsException : Exception
    {
        /// <summary>原始 Python traceback 字符串（来自 Python traceback.format_exc() 或等价）。</summary>
        public string PythonTraceback { get; }

        /// <summary>Python 异常类型名（例 "ValueError", "ImportError"）。</summary>
        public string PythonExceptionType { get; }

        /// <summary>触发异常的模块路径（.py 文件），null 表示未知。</summary>
        public string? SourceFilePath { get; }

        /// <summary>触发异常的函数名，null 表示 module-level（import 阶段）错误。</summary>
        public string? FunctionName { get; }

        public PythonDiagnosticsException(
            string message,
            string pythonExceptionType,
            string pythonTraceback,
            string? sourceFilePath = null,
            string? functionName   = null,
            Exception? innerException = null)
            : base(message, innerException)
        {
            PythonExceptionType = pythonExceptionType ?? string.Empty;
            PythonTraceback     = pythonTraceback     ?? string.Empty;
            SourceFilePath      = sourceFilePath;
            FunctionName        = functionName;
        }

        public override string ToString()
        {
            var where = FunctionName != null
                ? $"{SourceFilePath ?? "?"}::{FunctionName}"
                : (SourceFilePath ?? "unknown");
            return $"[Python {PythonExceptionType}] in {where}: {Message}\n{PythonTraceback}";
        }
    }

    /// <summary>
    /// §D2.4 DryRun 阶段的 Python import 诊断结果。
    /// <see cref="PythonHandlerRegistry.DryRunImports"/> 汇总所有 .py 文件的 import 状态，
    /// 每个失败条目对应一条 <see cref="BlueprintDiagnostic"/>（Code = BP_PYIMPORT_FAILED）。
    /// </summary>
    public sealed class PythonImportDiagnostic
    {
        /// <summary>被检查的 .py 文件路径。</summary>
        public string FilePath { get; init; } = string.Empty;

        /// <summary>import 是否成功。false 时 <see cref="Error"/> 非 null。</summary>
        public bool Success { get; init; }

        /// <summary>失败时的错误信息（来自 PythonDiagnosticsException 或普通 Exception）。</summary>
        public string? Error { get; init; }

        /// <summary>Python traceback（仅 PythonDiagnosticsException 时有）。</summary>
        public string? PythonTraceback { get; init; }

        /// <summary>该文件内扫描到的 handler 描述（无论 import 成功与否，基于静态 regex 扫描）。</summary>
        public IReadOnlyList<PythonHandlerDescriptor> Handlers { get; init; } = Array.Empty<PythonHandlerDescriptor>();
    }
}
