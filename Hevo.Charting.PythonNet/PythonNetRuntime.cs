using System;
using System.Collections.Generic;
using System.IO;
using Hevo.Charting.Features;
using Hevo.Trade;
using Python.Runtime;

namespace Hevo.Charting.PythonNet
{
    /// <summary>
    /// §D2.5.2 真实 <see cref="IPythonRuntime"/> 实装 —— 嵌入 CPython,首次 <see cref="Initialize"/>
    /// 启动 <c>PythonEngine</c>,之后用 <c>Py.GIL()</c> 跨线程拿全局解释器锁,模块 import / 函数 invoke
    /// 全走 pythonnet 桥接。
    ///
    /// <para>
    /// <b>使用前置</b>:
    /// </para>
    /// <list type="number">
    ///   <item>本机已装 Python 3.8-3.12(推荐 3.10/3.11),pip install numpy</item>
    ///   <item>设置 env <c>PYTHONNET_PYDLL</c> 指向 python3X.dll(或在 ctor 显式传 <paramref name="pythonDllPath"/>),
    ///         未设置时走 <see cref="PythonHomeResolver"/> 探测链</item>
    ///   <item>分析师写的 .py 落在 <see cref="PythonSandboxOptions.AllowedRootDirectory"/> 下</item>
    /// </list>
    ///
    /// <para>
    /// <b>本波 (D2.5.2 / D2.5.3) 不含的</b>:沙箱 (D2.5.4) / 超时 (D2.5.5) / hevo_indicators 包 (D2.5.6)。
    /// 轮 B 再补。当前是"真能跑"的最小可工作版本。
    /// </para>
    /// </summary>
    public sealed class PythonNetRuntime : IPythonRuntime
    {
        private readonly object _initLock = new();
        private readonly string? _explicitDllPath;
        private bool _initialized;
        private bool _shutdown;
        private IntPtr _gilState;
        private PythonSandboxOptions _options = new();
        private ITradeService? _trade;

        /// <param name="pythonDllPath">显式指定 Python DLL 路径(优先级高于 env vars)。null = 走 <see cref="PythonHomeResolver.Resolve"/>。</param>
        public PythonNetRuntime(string? pythonDllPath = null)
        {
            _explicitDllPath = pythonDllPath;
        }

        /// <summary>
        /// §D3.7 注入 trade backend —— Python 端 <c>from hevo_indicators import trade</c> 拿到的就是
        /// 这里传入的 <see cref="ITradeService"/> 实例(包成 _TradeFacade 暴露 snake_case 同步 API)。
        /// 必须在 <see cref="Initialize"/> 之前调,Initialize 阶段一并注入。
        /// </summary>
        public PythonNetRuntime UseTradeService(ITradeService trade)
        {
            if (_initialized)
                throw new InvalidOperationException("UseTradeService 必须在 Initialize 之前调用。");
            _trade = trade ?? throw new ArgumentNullException(nameof(trade));
            return this;
        }

        public void Initialize(PythonSandboxOptions options)
        {
            lock (_initLock)
            {
                if (_initialized) return;
                if (_shutdown)
                    throw new InvalidOperationException(
                        "PythonNetRuntime 已 Shutdown,无法再 Initialize。请新建实例。");

                _options = options;

                // 1. 解析 Python DLL 路径并写入 Runtime.PythonDLL(必须在 PythonEngine.Initialize 之前)
                var dllPath = _explicitDllPath ?? PythonHomeResolver.Resolve();
                if (!string.IsNullOrEmpty(dllPath))
                {
                    Runtime.PythonDLL = dllPath;
                }

                // 诊断:Initialize 失败的 70% case 是 dll 路径定位错。打日志告诉用户最终选了什么。
                Console.WriteLine($"[Hevo.PythonNet] Runtime.PythonDLL = '{Runtime.PythonDLL ?? "<未设,走 pythonnet 自身探测>"}'");
                Console.WriteLine($"[Hevo.PythonNet]   PYTHONNET_PYDLL env = '{Environment.GetEnvironmentVariable("PYTHONNET_PYDLL") ?? "<未设>"}'");
                Console.WriteLine($"[Hevo.PythonNet]   PYTHONHOME env     = '{Environment.GetEnvironmentVariable("PYTHONHOME") ?? "<未设>"}'");
                Console.WriteLine($"[Hevo.PythonNet]   Process arch       = {(Environment.Is64BitProcess ? "x64" : "x86")}");

                // 2. 启动嵌入解释器
                try
                {
                    PythonEngine.Initialize();
                }
                catch (Exception ex) when (ex.GetType().Name == "BadPythonDllException")
                {
                    // pythonnet 抛 BadPythonDllException(internal type)但 message 简洁,补充诊断 + 安装指引重抛。
                    throw new InvalidOperationException(
                        $"Python.NET 加载 dll 失败:'{Runtime.PythonDLL ?? "<未设>"}'。" +
                        "可能原因:" +
                        "① 该 dll 不存在或路径错;" +
                        "② Python 版本超出 pythonnet 3.0.5 支持范围(本项目限定 3.7-3.12,3.13+ 不支持);" +
                        "③ 进程架构与 dll 架构不匹配(x64 进程不能加载 x86 dll);" +
                        "④ dll 依赖项缺失(vcruntime140.dll 等)。" +
                        "建议显式 set PYTHONNET_PYDLL 指向 Python 3.11 / 3.12 的 python3X.dll 绝对路径。",
                        ex);
                }

                // 3. 释放主线程 GIL —— 让其他线程能 Py.GIL() 抢锁。
                _gilState = PythonEngine.BeginAllowThreads();

                // 4. 应用 sys.path / 部署 hevo_indicators 包 / 安装沙箱 / 注入 trade facade —— 全部 GIL 内做
                using (Py.GIL())
                {
                    foreach (var p in EnumerateSysPath(_options))
                    {
                        AppendSysPathOnce(p);
                    }

                    // §D2.5.6 hevo_indicators Python 端运行时包(register / _trade.facade / ta.*)落盘 + sys.path
                    HevoIndicatorsBootstrap.EnsureInstalled();

                    // §D2.5.4 安装 import 沙箱 —— 在 hevo_indicators 落盘 + 加 sys.path 之后,
                    // 防止"沙箱启用后 hevo_indicators 包自己 import numpy 失败"。
                    ImportInterceptor.Install(_options.BlockedImports);

                    // §D3.7 trade backend 注入 —— Python 端 hevo_indicators.trade 拿到 _TradeFacade 实例
                    if (_trade != null)
                    {
                        try
                        {
                            using var hevoIndicators = Py.Import("hevo_indicators");
                            using var setupFn = hevoIndicators.GetAttr("_setup_trade");
                            using var pyTrade = _trade.ToPython();
                            using var _ = setupFn.Invoke(pyTrade);
                        }
                        catch (PythonException pex)
                        {
                            // hevo_indicators import 失败(罕见 —— 我们刚刚自己落的盘)→ 翻成可读异常
                            throw TranslateException(pex, "<hevo_indicators bootstrap>", functionName: "_setup_trade");
                        }
                    }
                }

                // 把"从 Python @indicator 装饰器拉 metadata"作为 lazy source 装进主项目 registry。
                // PlotFeature.OnCompose 调 IndicatorMetadataRegistry.Get → 命中走缓存,miss → 调这个回调拉一次。
                IndicatorMetadataRegistry.UseSource(GetIndicatorMeta);

                _initialized = true;
            }
        }

        public IPythonModule ImportModule(string moduleName, string filePath)
        {
            EnsureInitialized();
            using (Py.GIL())
            {
                // 文件父目录加 sys.path,防 ModuleNotFoundError
                var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
                if (!string.IsNullOrEmpty(dir))
                {
                    AppendSysPathOnce(dir!);
                }

                try
                {
                    PyObject mod;
                    using (var sysModules = PyModule.SysModules)
                    {
                        if (sysModules.HasKey(moduleName))
                        {
                            // 热重载语义:importlib.reload(existing) —— 蓝图侧改 .py 后 PythonHotReloader
                            // 调过来时,模块对象保持同一性、cached refs 仍然有效,只是函数体被重新绑定。
                            using var importlib = Py.Import("importlib");
                            using var existing = sysModules[moduleName];
                            mod = importlib.InvokeMethod("reload", new PyObject[] { existing });
                        }
                        else
                        {
                            mod = Py.Import(moduleName);
                        }
                    }

                    return new PythonNetModule(mod, moduleName, filePath, _options.PerCallTimeoutMs);
                }
                catch (PythonException pex)
                {
                    throw TranslateException(pex, filePath, functionName: null);
                }
            }
        }

        /// <summary>
        /// §D2.5.E2E 实时语法检查 —— 调 Python 内建 <c>compile(source, filename, 'exec')</c>。
        /// 仅做静态语法分析(不执行 .py),所以快(典型 100KB .py 几毫秒),适合编辑器 debounce 触发。
        /// <para>
        /// 抓 <c>SyntaxError</c> / <c>IndentationError</c> / <c>TabError</c> 翻成
        /// <see cref="PythonSyntaxResult"/>;其它异常(IO / 编码异常)归到 ExceptionType + Message。
        /// </para>
        /// </summary>
        public PythonSyntaxResult SyntaxCheck(string source, string filename = "<editor>")
        {
            EnsureInitialized();
            using (Py.GIL())
            {
                try
                {
                    using var builtins = Py.Import("builtins");
                    using var compileFn = builtins.GetAttr("compile");
                    using var srcArg = new PyString(source ?? "");
                    using var fileArg = new PyString(filename ?? "<editor>");
                    using var modeArg = new PyString("exec");
                    using var _ = compileFn.Invoke(srcArg, fileArg, modeArg);
                    return PythonSyntaxResult.Success;
                }
                catch (PythonException pex)
                {
                    string typeName = "PythonException";
                    int? lineno = null, offset = null;
                    string msg = pex.Message ?? "";

                    try
                    {
                        var pyType = pex.Type;
                        if (pyType != null)
                        {
                            using var nameAttr = pyType.GetAttr("__name__");
                            typeName = nameAttr.As<string>() ?? typeName;
                        }
                        // SyntaxError 实例上有 lineno / offset / msg 属性
                        var pyVal = pex.Value;
                        if (pyVal != null)
                        {
                            if (pyVal.HasAttr("lineno"))
                            {
                                using var lineAttr = pyVal.GetAttr("lineno");
                                if (!lineAttr.IsNone()) lineno = lineAttr.As<int>();
                            }
                            if (pyVal.HasAttr("offset"))
                            {
                                using var colAttr = pyVal.GetAttr("offset");
                                if (!colAttr.IsNone()) offset = colAttr.As<int>();
                            }
                            if (pyVal.HasAttr("msg"))
                            {
                                using var msgAttr = pyVal.GetAttr("msg");
                                if (!msgAttr.IsNone()) msg = msgAttr.As<string>() ?? msg;
                            }
                        }
                    }
                    catch
                    {
                        // 取属性失败,退化成默认 Message
                    }

                    return new PythonSyntaxResult(
                        Ok: false,
                        Message: msg,
                        Line: lineno,
                        Column: offset,
                        ExceptionType: typeName);
                }
            }
        }

        /// <summary>
        /// §D2.6 Pine 风味 plot DSL —— 调 Python 端 <c>hevo_indicators.get_indicator_meta(name)</c>
        /// 拉取 <c>@indicator</c> 装饰器登记的 series 元数据,翻成 <see cref="PlotSeriesSpec"/> 数组。
        /// 找不到 / 解析失败返回 null。
        /// </summary>
        public PlotSeriesSpec[]? GetIndicatorMeta(string name)
        {
            EnsureInitialized();
            using (Py.GIL())
            {
                try
                {
                    using var hev = Py.Import("hevo_indicators");
                    if (!hev.HasAttr("get_indicator_meta")) return null;
                    using var fn = hev.GetAttr("get_indicator_meta");
                    using var pyName = new PyString(name ?? "");
                    using var result = fn.Invoke(pyName);
                    if (result == null || result.IsNone()) return null;

                    // result 是 dict {name, fn, overlay, series: [{name, kind, color, width}, ...]}
                    using var seriesKey = new PyString("series");
                    using var seriesObj = result.GetItem(seriesKey);
                    if (seriesObj == null || seriesObj.IsNone()) return System.Array.Empty<PlotSeriesSpec>();

                    int n = (int)seriesObj.Length();
                    var specs = new PlotSeriesSpec[n];
                    for (int i = 0; i < n; i++)
                    {
                        using var pyIdx = new PyInt(i);
                        using var item = seriesObj.GetItem(pyIdx);

                        using var sName = item.GetItem(new PyString("name"));
                        using var sKind = item.GetItem(new PyString("kind"));
                        using var sColor = item.GetItem(new PyString("color"));
                        using var sWidth = item.GetItem(new PyString("width"));

                        specs[i] = new PlotSeriesSpec(
                            Name: sName.As<string>() ?? "",
                            Kind: sKind.As<string>() ?? "line",
                            Color: ParseHexColor(sColor.As<string>() ?? "#888888"),
                            Width: sWidth.As<double>());
                    }
                    return specs;
                }
                catch (PythonException pex)
                {
                    System.Console.WriteLine($"[IndicatorMeta] '{name}' 解析失败: {pex.Message}");
                    return null;
                }
                catch (System.Exception ex)
                {
                    System.Console.WriteLine($"[IndicatorMeta] '{name}' 异常: {ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            }
        }

        // 简单 hex 颜色解析:"#RRGGBB" / "#AARRGGBB"。坏字串退化成灰色,不 throw。
        private static System.Windows.Media.Color ParseHexColor(string s)
        {
            if (string.IsNullOrEmpty(s) || s[0] != '#')
                return System.Windows.Media.Colors.Gray;
            try
            {
                if (s.Length == 7) // #RRGGBB
                {
                    byte r = System.Convert.ToByte(s.Substring(1, 2), 16);
                    byte g = System.Convert.ToByte(s.Substring(3, 2), 16);
                    byte b = System.Convert.ToByte(s.Substring(5, 2), 16);
                    return System.Windows.Media.Color.FromRgb(r, g, b);
                }
                if (s.Length == 9) // #AARRGGBB
                {
                    byte a = System.Convert.ToByte(s.Substring(1, 2), 16);
                    byte r = System.Convert.ToByte(s.Substring(3, 2), 16);
                    byte g = System.Convert.ToByte(s.Substring(5, 2), 16);
                    byte b = System.Convert.ToByte(s.Substring(7, 2), 16);
                    return System.Windows.Media.Color.FromArgb(a, r, g, b);
                }
            }
            catch { }
            return System.Windows.Media.Colors.Gray;
        }

        public void Shutdown()
        {
            lock (_initLock)
            {
                if (!_initialized || _shutdown) return;
                IndicatorMetadataRegistry.UseSource(null);
                try
                {
                    PythonEngine.EndAllowThreads(_gilState);
                    PythonEngine.Shutdown();
                }
                catch
                {
                    // Shutdown 阶段抛异常通常是 finalizer 顺序问题,吞掉避免污染主线程退出
                }
                finally
                {
                    _initialized = false;
                    _shutdown = true;
                }
            }
        }

        private void EnsureInitialized()
        {
            if (!_initialized)
                throw new InvalidOperationException(
                    "PythonNetRuntime 未 Initialize。请先调 Initialize(options)。");
        }

        /// <summary>GIL 内调用:sys.path.append(<paramref name="dir"/>),已存在则跳过。</summary>
        private static void AppendSysPathOnce(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            using var sys = Py.Import("sys");
            using var path = sys.GetAttr("path");
            using var pyDir = new PyString(dir);
            using var contains = path.InvokeMethod("__contains__", new PyObject[] { pyDir });
            if (contains.As<bool>()) return;
            using var _ = path.InvokeMethod("append", new PyObject[] { pyDir });
        }

        private static IEnumerable<string> EnumerateSysPath(PythonSandboxOptions opts)
        {
            if (!string.IsNullOrEmpty(opts.AllowedRootDirectory))
                yield return opts.AllowedRootDirectory!;
            foreach (var p in opts.AllowedSysPath)
                if (!string.IsNullOrEmpty(p)) yield return p;
        }

        // §D2.4: pythonnet 抛 PythonException → 抓 Python 异常类型名 + traceback,翻成
        // PythonDiagnosticsException 让 .NET 层拿到结构化诊断(UI 错误面板可直接展示 Python 风格 stack trace)。
        internal static PythonDiagnosticsException TranslateException(
            PythonException pex, string? sourceFile, string? functionName)
        {
            string traceback;
            string typeName = "PythonException";

            try
            {
                using (Py.GIL())
                {
                    traceback = pex.Format();
                    var pyType = pex.Type;
                    if (pyType != null)
                    {
                        try
                        {
                            using var nameAttr = pyType.GetAttr("__name__");
                            typeName = nameAttr.As<string>() ?? typeName;
                        }
                        catch { /* __name__ 取不到时退化成 PythonException */ }
                    }
                }
            }
            catch
            {
                // Format() / GIL 异常 → 退化成 .NET 默认序列化
                traceback = pex.ToString();
            }

            return new PythonDiagnosticsException(
                message:             pex.Message ?? "",
                pythonExceptionType: typeName,
                pythonTraceback:     traceback,
                sourceFilePath:      sourceFile,
                functionName:        functionName,
                innerException:      pex);
        }
    }
}
