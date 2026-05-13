using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Hevo.Charting.PythonNet;
using Hevo.Trade;
using Hevo.Trade.Mock;
using Python.Runtime;

namespace Hevo.Drawing.LowCodeDemo
{
    /// <summary>
    /// §D2.5.E2E 进程级共享 Python plumbing —— 一个 PythonNetRuntime + 一份 PythonHandlerRegistry,
    /// 跨 Tab 共用(GraphViewer 蓝图运行 / PyIndicatorView 实时推流 都接同一个解释器实例)。
    /// <para>
    /// CPython 设计上整个进程只允许一份 PythonEngine,所以多 Tab 必须共享 —— 各自起 runtime 会导致
    /// PythonEngine.Initialize 二次调用 silent skip 但 trade 注入 / hevo_indicators 部署只来自第一次,
    /// 状态混乱。这个静态 host 把所有人锁在同一份初始化路径上。
    /// </para>
    /// <para>
    /// <b>indicators 目录</b>:%TEMP%\hevo_pyindicator_demo\,启动时自动部署 demo .py 文件
    /// (complex_indicators.py / pine_demo.py / multi_input_demo.py / bb_breakout_strategy.py / demo_signal.py)。
    /// 蓝图 ComputeFeature.Compute = "bollinger_upper" 等 string 引用就在这里查表。
    /// </para>
    /// </summary>
    public static class DemoPythonHost
    {
        public static readonly string IndicatorsDir =
            Path.Combine(Path.GetTempPath(), "hevo_pyindicator_demo");

        // 持久化"哪些 .py 被禁用"的 sidecar 文件,一行一个 filename(不含路径)。
        // PyEditor 切换勾选时读写这个文件,EnsureInitialized 时跳过禁用项。
        public static readonly string DisabledListFile =
            Path.Combine(IndicatorsDir, ".hevo_disabled.txt");

        private static readonly object _initLock = new();
        private static PythonNetRuntime? _runtime;
        private static PythonHandlerRegistry? _registry;
        private static MockTradeService? _trade;
        private static IDisposable? _orderUpdatesSub;
        private static bool _filesDeployed;

        /// <summary>共享的 BlueprintHandlerRegistry(实际是 PythonHandlerRegistry)。蓝图 launcher 直接传它。</summary>
        public static PythonHandlerRegistry Registry
        {
            get
            {
                EnsureInitialized();
                return _registry!;
            }
        }

        /// <summary>共享的 mock trade service —— PyIndicator demo 验证 Python → trade 桥用。</summary>
        public static MockTradeService Trade
        {
            get
            {
                EnsureInitialized();
                return _trade!;
            }
        }

        /// <summary>幂等:首次访问时建 runtime + registry + 部署 .py 文件 + AutoDiscover。</summary>
        public static void EnsureInitialized()
        {
            if (_registry != null) return;
            lock (_initLock)
            {
                if (_registry != null) return;

                Directory.CreateDirectory(IndicatorsDir);
                DeployDemoIndicators();   // 写 ma.py / complex_indicators.py / demo_signal.py 到目录

                EnsurePythonHome();

                _trade = new MockTradeService();
                _trade.Initialize(new TradeServiceOptions
                {
                    UserId = "demo-user",
                    EnablePreCheck = false,
                });

                // §D2.X 通知中心 — 装饰 trade service:PlaceOrderAsync 成功 ack 后灌 (BrokerOrderId → OrderRequest)
                // 元数据缓存,让 OrderUpdates 推的纯 BrokerOrderId 状态能反查回 symbol/direction/qty 呈现可读订单事件。
                // 装饰器透传所有其他接口,Python facade 无感(还是收到原 OrderAck)。
                var recordingTrade = new RecordingTradeService(_trade);
                _orderUpdatesSub = _trade.OrderUpdates.Subscribe(new OrderUpdateObserver());

                _runtime = new PythonNetRuntime(ResolveLocalPythonDll())
                    .UseTradeService(recordingTrade);

                _registry = new PythonHandlerRegistry()
                    .UseRuntime(_runtime)
                    .Initialize(new PythonSandboxOptions
                    {
                        AllowedRootDirectory = IndicatorsDir,
                        PerCallTimeoutMs     = 1000,
                    });

                // §D2.X 副作用 builtin —— Python 端经 __main__ 拿到 send_email/dingtalk/webhook/sms/log_alert。
                // Mock 实现仅落进程级历史 + 触发 NotificationFired,不发真实网络。
                // 真实业务把 5 个 Action<PyObject> 换成 SmtpClient / HttpClient 即可。
                // 必须在 LoadEnabledIndicators 之前注入 —— 否则被 import 的 .py 文件在 import 那一刻
                // 抓 __main__ 拿不到 builtin。bb_breakout_strategy.py 现在用 late-binding 抓,顺序无关,
                // 但保守起见仍前置,新增其他策略文件无需注意此微妙顺序。
                RegisterNotificationBuiltins();

                LoadEnabledIndicators();

                // §D2.6 plot DSL —— PythonNetRuntime.Initialize 已经自动把 GetIndicatorMeta 装进
                // IndicatorMetadataRegistry.UseSource 作 lazy fetcher,这里不需额外注册。
            }
        }

        /// <summary>
        /// 把 5 个 mock 通知 builtin 注入 Python <c>__main__</c> 命名空间。
        /// PyBusinessBridge.RegisterBuiltin 内部锁 Py.GIL,调用线程任意。
        /// </summary>
        private static void RegisterNotificationBuiltins()
        {
            PyBusinessBridge.RegisterBuiltin("send_email",    new Action<PyObject>(MockNotifier.SendEmail));
            PyBusinessBridge.RegisterBuiltin("send_dingtalk", new Action<PyObject>(MockNotifier.SendDingTalk));
            PyBusinessBridge.RegisterBuiltin("send_webhook",  new Action<PyObject>(MockNotifier.SendWebhook));
            PyBusinessBridge.RegisterBuiltin("send_sms",      new Action<PyObject>(MockNotifier.SendSms));
            PyBusinessBridge.RegisterBuiltin("log_alert",     new Action<PyObject>(MockNotifier.LogAlert));
        }

        /// <summary>
        /// 把 demo .py 落盘到 IndicatorsDir(覆盖式)—— Python 源代码现在以 EmbeddedResource 嵌在
        /// <c>Hevo.Drawing.LowCodeDemo.PyIndicators.*.py</c> 命名空间,启动时从 assembly manifest 提取
        /// 写到 %TEMP% 给 PythonHandlerRegistry.AutoDiscoverDirectory 扫。
        ///
        /// <para>
        /// 真 .py 文件好处:① IDE 语法高亮 + 直接编辑;② 不被 C# verbatim string 的 ASCII 双引号
        /// 截断(以前中文注释带 "..." 会让 @"..." 字符串提前关闭炸编译);③ 多文件一目了然,
        /// 加新指标 = 加 .py 文件 + 加 csproj EmbeddedResource glob(已经 <c>**/*.py</c>,新文件零配置)。
        /// </para>
        /// </summary>
        private static void DeployDemoIndicators()
        {
            if (_filesDeployed) return;
            var utf8NoBom = new UTF8Encoding(false);
            const string resourcePrefix = "Hevo.Drawing.LowCodeDemo.PyIndicators.";

            var asm = Assembly.GetExecutingAssembly();
            int deployed = 0;
            foreach (var resName in asm.GetManifestResourceNames())
            {
                if (!resName.StartsWith(resourcePrefix, StringComparison.Ordinal)) continue;
                if (!resName.EndsWith(".py", StringComparison.Ordinal)) continue;

                // 资源名形如 "Hevo.Drawing.LowCodeDemo.PyIndicators.bb_breakout_strategy.py"
                // → 去掉前缀剩 "bb_breakout_strategy.py" 作为目标文件名。
                var fileName = resName.Substring(resourcePrefix.Length);

                using var stream = asm.GetManifestResourceStream(resName);
                if (stream == null) continue;
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var content = reader.ReadToEnd();

                File.WriteAllText(Path.Combine(IndicatorsDir, fileName), content, utf8NoBom);
                deployed++;
            }
            Console.WriteLine($"[DemoPythonHost] 部署 {deployed} 个 .py 文件到 {IndicatorsDir}");
            _filesDeployed = true;
        }

        /// <summary>仓库根 / bin 目录下的 embeddable Python dll 路径(按 csproj robocopy 拷过来的)。</summary>
        private static string? ResolveLocalPythonDll()
        {
            var embedded = Path.Combine(AppContext.BaseDirectory, "Python312", "python312.dll");
            if (File.Exists(embedded)) return embedded;
            var loose = Path.Combine(AppContext.BaseDirectory, "python312.dll");
            return File.Exists(loose) ? loose : null;
        }

        /// <summary>设 PYTHONHOME 让嵌入解释器找得到 stdlib(否则启动报 init_fs_encoding fatal error)。</summary>
        private static void EnsurePythonHome()
        {
            var home = Path.Combine(AppContext.BaseDirectory, "Python312");
            if (!Directory.Exists(home)) return;
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PYTHONHOME")))
                Environment.SetEnvironmentVariable("PYTHONHOME", home);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PYTHONPATH")))
            {
                var libDir = Path.Combine(home, "Lib");
                var dllsDir = Path.Combine(home, "DLLs");
                var sitePackages = Path.Combine(libDir, "site-packages");
                Environment.SetEnvironmentVariable(
                    "PYTHONPATH",
                    string.Join(Path.PathSeparator, libDir, dllsDir, sitePackages));
            }
        }

        /// <summary>
        /// §D2.5.E2E Python 编辑器调:走 Runtime.SyntaxCheck → Python <c>compile()</c>,
        /// 实时拿 SyntaxError 行 / 列 / 消息。fname 显示在 traceback 里(<c>"&lt;editor&gt;"</c> 表示
        /// 内存文本,跟磁盘文件无关)。
        /// </summary>
        public static PythonSyntaxResult SyntaxCheck(string source, string fname = "<editor>")
        {
            EnsureInitialized();
            return _runtime!.SyntaxCheck(source, fname);
        }

        // ===== Per-file load/disable 管理(PyEditor UI 切换用)============================

        /// <summary>读取禁用清单(文件名,无路径)。文件不存在 = 全部启用。</summary>
        public static HashSet<string> ReadDisabledSet()
        {
            try
            {
                if (!File.Exists(DisabledListFile)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var lines = File.ReadAllLines(DisabledListFile)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("#"));
                return new HashSet<string>(lines, StringComparer.OrdinalIgnoreCase);
            }
            catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        }

        /// <summary>写禁用清单 sidecar。</summary>
        public static void WriteDisabledSet(IEnumerable<string> filenames)
        {
            try
            {
                var content = "# Each line = a .py filename to skip on AutoDiscover. Lines starting with # are comments." +
                              Environment.NewLine +
                              string.Join(Environment.NewLine, filenames.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
                File.WriteAllText(DisabledListFile, content, new System.Text.UTF8Encoding(false));
            }
            catch { /* 写失败忽略,下次启动还是按当前内存状态走 */ }
        }

        /// <summary>查询某文件名当前是否被禁用。</summary>
        public static bool IsDisabled(string filename) => ReadDisabledSet().Contains(filename);

        /// <summary>切换某文件的 enable/disable 状态。返回切换后的新状态(true=enabled, false=disabled)。</summary>
        public static bool ToggleEnabled(string filename)
        {
            var disabled = ReadDisabledSet();
            bool newEnabled;
            if (disabled.Contains(filename)) { disabled.Remove(filename); newEnabled = true; }
            else { disabled.Add(filename); newEnabled = false; }
            WriteDisabledSet(disabled);
            return newEnabled;
        }

        /// <summary>
        /// 按 .hevo_disabled.txt 过滤,逐文件 RegisterModule(替代 AutoDiscoverDirectory 的全量扫描)。
        /// 进程重启时由 EnsureInitialized 调一次;PyEditor toggle 时也会重新调,实现热生效。
        /// </summary>
        public static void LoadEnabledIndicators()
        {
            var reg = _registry;
            if (reg == null) return;
            var disabled = ReadDisabledSet();

            var allHandlers = Hevo.Charting.PythonNet.PythonRegisterScanner.ScanDirectory(IndicatorsDir);
            foreach (var (file, descriptors) in allHandlers)
            {
                var fname = Path.GetFileName(file);
                if (disabled.Contains(fname))
                {
                    // 该文件之前如果被注册过,这里 Unregister 让重启 / toggle 后立即生效。
                    reg.UnregisterBySourceFile(file);
                    continue;
                }
                foreach (var d in descriptors)
                {
                    if (string.IsNullOrEmpty(d.Signature)) continue;
                    // §D2.X 把 Scanner 抓到的 inputs=[...] 元数据也传进 registry,
                    // 后续 picker UX / DryRun 才能 GetInputNames(...) 找回多输入声明。
                    try
                    {
                        reg.RegisterModule(d.Name, file, d.FunctionName, d.Signature!, d.Inputs);
                    }
                    catch (Exception ex)
                    {
                        // demo 里 silent skip 会让"通知中心收不到 / handler 未注册"这类 bug
                        // 难以排查。日志走 stderr,不阻断后续文件扫描(单文件失败仍降级处理)。
                        Console.Error.WriteLine(
                            $"[DemoPythonHost] RegisterModule 失败:file={Path.GetFileName(file)} " +
                            $"handler={d.Name} fn={d.FunctionName} → {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }

        public static void Shutdown()
        {
            lock (_initLock)
            {
                _orderUpdatesSub?.Dispose();
                _orderUpdatesSub = null;
                _registry?.Shutdown();
                _trade?.Shutdown();
                _registry = null;
                _runtime = null;
                _trade = null;
            }
        }

        /// <summary>
        /// 极简 IObserver —— 把 OrderUpdates 推过来的状态变更直接转给 <see cref="MockNotifier.OnOrderUpdate"/>。
        /// 不引 System.Reactive 是因为 MockTradeService 自带了轻量 Subject 实现,这里手撸三方法满足接口即可。
        /// </summary>
        private sealed class OrderUpdateObserver : IObserver<OrderUpdate>
        {
            public void OnNext(OrderUpdate value) => MockNotifier.OnOrderUpdate(value);
            public void OnError(Exception error)    { /* MockTradeService 不发 OnError,demo 忽略 */ }
            public void OnCompleted()               { /* shutdown 时发,demo 忽略 */ }
        }
    }
}
