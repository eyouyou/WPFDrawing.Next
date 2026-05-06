using System.Collections.Generic;

namespace Hevo.Charting.LowCode.Designer.Python
{
    /// <summary>
    /// §D2 Python 运行时抽象 —— 把 Python.NET / IronPython / subprocess 几条不同的实现路径
    /// 统一到一个 IPythonRuntime 接口背后,框架核心不强依赖 pythonnet NuGet,业务侧按需启用。
    ///
    /// <para>
    /// <b>默认行为</b>:框架内置 <see cref="NullPythonRuntime"/>(任何调用都抛 InvalidOperationException
    /// + 安装指引)。要真跑 Python,业务侧引用 <c>Python.Runtime</c> NuGet,实现 <c>PythonNetRuntime : IPythonRuntime</c>
    /// (典型在配套 <c>Hevo.Charting.PythonNet</c> 子项目),启动时调用
    /// <see cref="PythonHandlerRegistry.UseRuntime"/> 注入。
    /// </para>
    ///
    /// <para>
    /// <b>为什么不直接 NuGet pythonnet</b>:它带 +30MB CPython 嵌入,且需要本地 Python 安装版本兼容
    /// (3.8-3.11 等),不是所有用户场景都需要。可选化保护"只用 C# 的轻量场景"。
    /// </para>
    /// </summary>
    public interface IPythonRuntime
    {
        /// <summary>
        /// 启动 Python 解释器并应用沙箱配置。多次调用幂等(已启动直接返回)。
        /// 沙箱配置一旦应用,本进程内不可降级(避免运行时被业务回退)。
        /// </summary>
        void Initialize(PythonSandboxOptions options);

        /// <summary>
        /// 加载一个 .py 文件作为命名 Python 模块。同名 module 重新加载等价于 Python <c>importlib.reload</c>(热重载语义)。
        /// </summary>
        IPythonModule ImportModule(string moduleName, string filePath);

        /// <summary>关闭 Python 解释器(典型应用程序退出钩子)。重复调用幂等。</summary>
        void Shutdown();
    }

    /// <summary>
    /// Python 模块的最小化抽象:取函数 + 调用。不暴露 PyObject 让业务直接 fiddle,降低耦合面 + 沙箱风险。
    /// </summary>
    public interface IPythonModule
    {
        /// <summary>模块名(等价 Python <c>__name__</c>)。</summary>
        string Name { get; }

        /// <summary>
        /// 调用 Python 函数。<paramref name="args"/> 按位置传递。
        /// 实现需把 .NET 类型(<see cref="System.ReadOnlyMemory{T}"/> / 标量 / DateTime)
        /// 转成对应 Python 对象;返回值反向转回 .NET 类型。
        /// 调用超时由沙箱 <see cref="PythonSandboxOptions.PerCallTimeout"/> 强制硬切。
        /// </summary>
        object? Invoke(string functionName, params object?[] args);

        /// <summary>查询模块上是否存在该函数,DryRun / 注册时校验用。</summary>
        bool HasFunction(string functionName);

        /// <summary>查模块上 @register(...) / 等价装饰器声明的所有 handler 名 + 函数名。AutoDiscover 用。</summary>
        IReadOnlyList<PythonHandlerDescriptor> ListRegisteredHandlers();
    }

    /// <summary>
    /// Python 模块上"自动登记"出来的 handler 元信息(由 <c>@register("name", signature="...")</c> 装饰器吐出)。
    /// </summary>
    public sealed class PythonHandlerDescriptor
    {
        /// <summary>蓝图侧引用 handler 用的字符串名字 (装饰器第 1 个参数)。</summary>
        public string Name { get; init; } = string.Empty;
        /// <summary>Python 函数名 (def 后面的 identifier)。</summary>
        public string FunctionName { get; init; } = string.Empty;
        /// <summary>
        /// 签名描述串 (装饰器 signature kwarg)。形如 <c>"(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]"</c>。
        /// 让 <see cref="PythonTypeMapper"/> 据此构造对应 <see cref="System.Delegate"/> 类型。
        /// 缺省 = 业务侧自定义指定时由调用方自己传 expectedDelegate。
        /// </summary>
        public string? Signature { get; init; }
    }

    /// <summary>
    /// Python 沙箱配置 —— 启动时一次性应用,运行期不可降级。
    /// </summary>
    public sealed class PythonSandboxOptions
    {
        /// <summary>禁止 import 的模块名(完全匹配)。默认拦 OS / IO / 子进程能力,避免分析师误用 + 注入风险。</summary>
        public HashSet<string> BlockedImports { get; init; } = new(StringComparer.Ordinal)
        {
            "os", "subprocess", "sys", "socket", "shutil", "ctypes", "importlib",
        };

        /// <summary>每次 handler 调用的超时(毫秒)。超时硬切,不让单个坏指标毒化主流。</summary>
        public int PerCallTimeoutMs { get; init; } = 100;

        /// <summary>允许扫描的 Python 文件根目录。AutoDiscoverDirectory 只在此根下找 .py。null = 不限。</summary>
        public string? AllowedRootDirectory { get; init; }

        /// <summary>额外允许的 sys.path(Python 模块搜索路径)。典型 [&quot;C:\\indicators&quot;, ...]。</summary>
        public List<string> AllowedSysPath { get; init; } = new();
    }

    /// <summary>
    /// 默认 NullPythonRuntime —— 任何调用立即抛 + 提示安装路径。
    /// 框架核心默认走这个,业务侧不显式启用 = 干净不带 Python 依赖。
    /// </summary>
    public sealed class NullPythonRuntime : IPythonRuntime
    {
        public static readonly NullPythonRuntime Instance = new();

        private static InvalidOperationException NotInstalled() => new(
            "Python.NET 未启用。要使用 §D2 Python 嵌入指标,请:" +
            "1) 引用 NuGet Python.Runtime; " +
            "2) 实现 PythonNetRuntime : IPythonRuntime(典型在 Hevo.Charting.PythonNet 子项目); " +
            "3) 启动时调用 PythonHandlerRegistry.UseRuntime(new PythonNetRuntime())。");

        public void Initialize(PythonSandboxOptions options) => throw NotInstalled();
        public IPythonModule ImportModule(string moduleName, string filePath) => throw NotInstalled();
        public void Shutdown() { /* 幂等空操作,允许调用方不区分启用/未启用都 finally 调一次 */ }
    }
}
