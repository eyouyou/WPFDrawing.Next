using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Hevo.Charting.LowCode.Designer;
using Python.Runtime;

namespace Hevo.Charting.PythonNet
{
    /// <summary>
    /// 进程级共享 <see cref="PythonNetRuntime"/> + <see cref="PythonHandlerRegistry"/>。
    /// 业务侧用源码字符串直接注册 Python handler,不再操心:
    /// <list type="bullet">
    ///   <item>Python DLL 路径解析(走 <see cref="PythonHomeResolver"/>)</item>
    ///   <item>PythonNetRuntime 启动 + 沙箱配置</item>
    ///   <item>.py 文件落盘到 sandbox-allowed dir</item>
    ///   <item>初始化失败错误提示(numpy 没装 / dll 缺失等)</item>
    /// </list>
    ///
    /// <para>
    /// <b>用法</b>:
    /// </para>
    /// <code>
    /// EmbeddedPythonHost.Default
    ///     .RegisterPythonFunction("scatter", scatterPySource, "scatter", new[] { "x", "y" })
    ///     .RegisterPythonFunction("buy_signals", maPySource, "buy_signals", new[] { "close" });
    /// var registry = EmbeddedPythonHost.Default.Registry;
    /// </code>
    ///
    /// <para>
    /// 替代各 demo 自家 ~150 行 PythonHost 类。Python 不可用(没装 / numpy 缺)→ 注册抛
    /// <see cref="InvalidOperationException"/> 带详细修复 hint,InnerException 保留原始 trace。
    /// </para>
    /// </summary>
    public sealed class EmbeddedPythonHost
    {
        /// <summary>进程级单例。同一份 runtime + registry,所有窗口共享。</summary>
        public static EmbeddedPythonHost Default { get; } = new();

        // 沙箱根目录:%TEMP%/hevo_embedded_python/。.py 必须落到允许目录内。
        // 同进程多次启动复用此目录,handler 文件按 name 命名(name 冲突时后注册覆盖前注册)。
        private readonly string _indicatorsDir =
            Path.Combine(Path.GetTempPath(), "hevo_embedded_python");

        private readonly object _initLock = new();
        private PythonNetRuntime? _runtime;
        private PythonHandlerRegistry? _registry;
        private Exception? _initException;

        /// <summary>true = 已成功初始化;false = 还没初始化或初始化失败(此时调 <see cref="Registry"/> 重抛缓存异常)。</summary>
        public bool Available => _registry != null;

        /// <summary>
        /// handler registry 入口。首次访问触发 init;init 失败缓存异常,后续访问重抛同一份。
        /// </summary>
        public BlueprintHandlerRegistry Registry
        {
            get
            {
                EnsureInitialized();
                return _registry!;
            }
        }

        /// <summary>
        /// 用源码字符串注册 Python handler。返回 self 支持链式。
        /// 内部 1) 写源码到 sandbox 内 .py 文件;2) 调 <see cref="PythonHandlerRegistry.RegisterPythonFunction"/>。
        /// </summary>
        public EmbeddedPythonHost RegisterPythonFunction(
            string handlerName,
            string sourceCode,
            string functionName,
            IReadOnlyList<string> inputs)
        {
            if (string.IsNullOrEmpty(handlerName)) throw new ArgumentNullException(nameof(handlerName));
            if (sourceCode == null) throw new ArgumentNullException(nameof(sourceCode));
            if (string.IsNullOrEmpty(functionName)) throw new ArgumentNullException(nameof(functionName));

            EnsureInitialized();

            var path = Path.Combine(_indicatorsDir, $"{handlerName}.py");
            File.WriteAllText(path, sourceCode, new UTF8Encoding(false));

            _registry!.RegisterPythonFunction(handlerName, path, functionName, inputs);
            return this;
        }

        /// <summary>
        /// 自动发现 + 注册:扫描 <paramref name="assembly"/> 内以 <paramref name="resourcePrefix"/> 起头的所有
        /// <c>*.py</c> EmbeddedResource,用 <see cref="PythonRegisterScanner"/> 解析每个文件里的
        /// <c>@register(name, inputs=[...])</c> 装饰器,自动注册到本 host。
        ///
        /// <para>
        /// <b>业务侧"零 manifest"路径</b>:
        /// </para>
        /// <code>
        /// // App 启动一次性
        /// EmbeddedPythonHost.Default.LoadPythonAssetsFromAssembly(
        ///     typeof(MyApp).Assembly,
        ///     resourcePrefix: "MyApp.Assets.python.");
        /// </code>
        /// <para>
        /// 加 Python handler:加 <c>.py</c> 文件 + csproj 的 <c>EmbeddedResource Include="..."</c>,
        /// 启动自动 discover。蓝图节点引用 handler name 即可,业务侧无需写任何 RegisterPythonFunction。
        /// </para>
        ///
        /// <para>
        /// 返回 self 支持链式。同名 handler 重复注册 = 后注册覆盖前注册。
        /// </para>
        /// </summary>
        public EmbeddedPythonHost LoadPythonAssetsFromAssembly(Assembly assembly, string resourcePrefix)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            if (resourcePrefix == null) throw new ArgumentNullException(nameof(resourcePrefix));

            EnsureInitialized();

            // 1. 把所有 .py 写到 sandbox dir,记录 file path
            var sandboxFiles = new List<string>();
            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                if (!resourceName.StartsWith(resourcePrefix, StringComparison.Ordinal)) continue;
                if (!resourceName.EndsWith(".py", StringComparison.Ordinal)) continue;

                using var stream = assembly.GetManifestResourceStream(resourceName)!;
                using var reader = new StreamReader(stream);
                string source = reader.ReadToEnd();

                string stem = resourceName.Substring(resourcePrefix.Length);
                if (stem.EndsWith(".py", StringComparison.Ordinal)) stem = stem.Substring(0, stem.Length - 3);
                stem = stem.Replace('.', '_');
                string path = Path.Combine(_indicatorsDir, stem + ".py");
                File.WriteAllText(path, source, new UTF8Encoding(false));
                sandboxFiles.Add(path);

                // 2. 静态 regex 注册(基础保底):字面量 @register 跑 regex 扫源码即时拿到 (name, fn, inputs);
                //    Python 在线时 import 触发装饰器,_hevo_handlers 字典也填好,后面会跟 regex 取并集。
                var descriptors = PythonRegisterScanner.ScanText(source);
                foreach (var desc in descriptors)
                {
                    if (string.IsNullOrEmpty(desc.Name) || string.IsNullOrEmpty(desc.FunctionName)) continue;
                    try
                    {
                        _registry!.RegisterPythonFunction(
                            handlerName:  desc.Name,
                            filePath:     path,
                            functionName: desc.FunctionName,
                            inputs:       desc.Inputs);
                    }
                    catch
                    {
                        // regex 抓到但 import 失败 / fn 不存在 → fall through;Python dict 路径(下面)补一遍。
                    }
                }
            }

            // 3. Python dict 兜底:读 hevo_indicators._hevo_handlers 拿真注册结果,
            //    覆盖 regex 漏掉的"动态 name / 复杂装饰器叠加 / 一函数多 alias"等场景。
            //    Python 装饰器实际执行后字典是真相,regex 是文本近似;两路并存最稳。
            try { ImportDictDrivenHandlers(sandboxFiles); }
            catch (Exception ex)
            {
                // Python dict 读取失败(罕见 — 此时 sandboxFiles 已 import 过)→ regex 注册是最终结果,可用。
                System.Diagnostics.Debug.WriteLine($"[EmbeddedPythonHost] dict-driven discovery 跳过:{ex.Message}");
            }
            return this;
        }

        // 读 Python 端 hevo_indicators._hevo_handlers 全局 dict,filter 来自 sandboxFiles 的 entry,
        // 注册到 C# registry。dict 形态:name -> (fn_name, signature, inputs, source_file)。
        // 跟 regex 路径取并集 —— 同名 handler 后注册覆盖前注册(PythonHandlerRegistry 行为)。
        private void ImportDictDrivenHandlers(IReadOnlyList<string> sandboxFiles)
        {
            // 规整路径(大小写 / 斜杠 / "..\..\")用作 set 比对 key
            var sandboxSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in sandboxFiles) sandboxSet.Add(Path.GetFullPath(f));

            using (Py.GIL())
            {
                using var hev = Py.Import("hevo_indicators");
                if (!hev.HasAttr("_hevo_handlers")) return;
                using var dict = hev.GetAttr("_hevo_handlers");
                using var keys = dict.InvokeMethod("keys");
                using var iter = keys.GetIterator();

                while (iter.MoveNext())
                {
                    using var keyObj = iter.Current!;
                    string? handlerName = null;
                    try { handlerName = keyObj.As<string>(); } catch { continue; }
                    if (string.IsNullOrEmpty(handlerName)) continue;

                    using var entry = dict.GetItem(keyObj);
                    if (!entry.HasAttr("__len__")) continue;

                    int len = entry.InvokeMethod("__len__").As<int>();
                    if (len < 4) continue;  // 旧 framework dict 缺 source_file 字段,跳过(走 regex 那条结果)

                    string fnName = "";
                    string sourceFile = "";
                    var inputs = new List<string>();
                    try
                    {
                        using var fnObj = entry.GetItem(0);
                        fnName = fnObj.As<string>() ?? "";

                        // entry[1] = signature,本路径不强类型不需要
                        using var inputsObj = entry.GetItem(2);
                        if (!inputsObj.IsNone() && inputsObj.HasAttr("__len__"))
                        {
                            int n = inputsObj.InvokeMethod("__len__").As<int>();
                            for (int i = 0; i < n; i++)
                            {
                                using var item = inputsObj.GetItem(i);
                                inputs.Add(item.As<string>() ?? "");
                            }
                        }

                        using var srcObj = entry.GetItem(3);
                        sourceFile = srcObj.As<string>() ?? "";
                    }
                    catch { continue; }

                    if (string.IsNullOrEmpty(fnName) || string.IsNullOrEmpty(sourceFile)) continue;
                    var fullSource = Path.GetFullPath(sourceFile);
                    if (!sandboxSet.Contains(fullSource)) continue;  // 不是我们 sandbox 内的文件,跳过

                    try
                    {
                        _registry!.RegisterPythonFunction(
                            handlerName: handlerName,
                            filePath:    fullSource,
                            functionName: fnName,
                            inputs:      inputs.Count > 0 ? inputs : null);
                    }
                    catch
                    {
                        // 单个失败不拖整个扫描(跟 regex 路径同款行为)
                    }
                }
            }
        }

        /// <summary>
        /// 显式初始化(可选)—— 首次 <see cref="RegisterPythonFunction"/> / <see cref="Registry"/> 也会自动触发。
        /// 失败缓存异常,后续重抛同一份(避免半初始化状态)。
        /// </summary>
        public void EnsureInitialized()
        {
            if (_registry != null) return;
            if (_initException != null) throw WrapInitException(_initException);

            lock (_initLock)
            {
                if (_registry != null) return;
                if (_initException != null) throw WrapInitException(_initException);

                try
                {
                    Directory.CreateDirectory(_indicatorsDir);

                    // PythonHomeResolver 自带 portable 探测(AppContext.BaseDirectory + 祖先 / Python3*),
                    // 业务侧只要 Python312/ 跟着 .exe 走就 0 配置。
                    var dll = PythonHomeResolver.Resolve();
                    _runtime = new PythonNetRuntime(dll);
                    _registry = new PythonHandlerRegistry()
                        .UseRuntime(_runtime)
                        .Initialize(new PythonSandboxOptions
                        {
                            AllowedRootDirectory = _indicatorsDir,
                            // PlotFeature 已经走 WatchAsync 让 indicator 在后台线程跑(协议规范),
                            // watchdog 仍保留是为了"防止业务 Python handler 死循环长期占用线程池 worker"。
                            // 5000ms 宽松阈值给重指标足够时间,真死循环时硬切。
                            // 之前 1000ms 偏紧 + Watch 同步路径,watchdog 反而成了"持锁同步等" pathological case;
                            // 现在 WatchAsync 异步路径下,watchdog 只是后台 task 内部的二次保护,无 UI 阻塞副作用。
                            PerCallTimeoutMs = 5000,
                        });
                }
                catch (Exception ex)
                {
                    _initException = ex;
                    _registry = null;
                    throw WrapInitException(ex);
                }
            }
        }

        private static InvalidOperationException WrapInitException(Exception inner)
            => new(
                "EmbeddedPythonHost 初始化失败 —— Python handler 装配不上。\n" +
                $"原始异常({inner.GetType().Name}): {(inner.InnerException ?? inner).Message}\n" +
                "常见根因 + 修法:\n" +
                "  • numpy 未装 → repo 根目录跑 `scripts/setup-python.ps1 -Force`\n" +
                "  • python312.dll 找不到 → 检查 Python312/ 部署到 .exe 同目录\n" +
                "  • 网络拉 numpy 失败 → 加 -PipIndexUrl 走清华源:\n" +
                "      .\\scripts\\setup-python.ps1 -Force -PipIndexUrl \"https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple\"",
                inner);
    }
}
