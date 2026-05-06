using System.Collections.Concurrent;

namespace Hevo.Charting.LowCode.Designer.Python
{
    /// <summary>
    /// §D2.3 Python 热重载：监听一个目录下所有 .py 文件的变更（mtime 变化 / 新增 / 删除），
    /// 变更时重新 import 模块、重新扫描 @register 装饰器、将新委托写回 <see cref="PythonHandlerRegistry"/>。
    ///
    /// <para>
    /// <b>优雅切换（graceful swap）协议</b>：
    /// <list type="bullet">
    ///   <item>变更发生时先在后台线程完成新模块 import + 委托构造，期间旧委托继续服务。</item>
    ///   <item>新委托就绪后，调用 <see cref="PythonHandlerRegistry.RegisterDelegate"/> 原子覆写同名 handler。</item>
    ///   <item>覆写后 <see cref="HotReloadOccurred"/> 事件通知宿主（典型：UI 刷新 / schema 触发重画）。</item>
    ///   <item>删除的 .py → 对应 handler 调用 <see cref="PythonHandlerRegistry.Unregister"/> 摘除。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>不进入热路径</b>（见低代码.md §D2 划线）：crosshair / tooltip 等 60Hz 渲染路径全程走 C# handler，
    /// 只有 ComputeNode 指标函数走 Python，热重载延迟一帧内完成不影响主渲染。
    /// </para>
    /// </summary>
    public sealed class PythonHotReloader : IDisposable
    {
        private readonly PythonHandlerRegistry _registry;
        private readonly string _directory;
        private readonly PythonSandboxOptions _options;

        private FileSystemWatcher? _watcher;
        private readonly ConcurrentDictionary<string, DateTime> _lastMtime = new();
        private readonly object _reloadLock = new();
        private bool _disposed;

        /// <summary>
        /// 热重载成功时触发：参数为变更的文件路径 + 该文件内本次重新注册的 handler 数量。
        /// 从后台线程发出；宿主如需操作 UI，需 Dispatcher.InvokeAsync。
        /// </summary>
        public event Action<string, int>? HotReloadOccurred;

        /// <summary>
        /// 重载发生错误（import 失败 / 签名解析失败 / 沙箱越界）时触发。不阻止后续文件的重载。
        /// </summary>
        public event Action<string, Exception>? HotReloadFailed;

        public PythonHotReloader(PythonHandlerRegistry registry, string directory, PythonSandboxOptions options)
        {
            _registry  = registry  ?? throw new ArgumentNullException(nameof(registry));
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            _options   = options;
        }

        /// <summary>
        /// 启动文件监听。同时做一次全量扫描，把已有 .py 文件都注册一遍（首次启动等价于批量 import）。
        /// 多次调用幂等（已启动直接返回）。
        /// </summary>
        public void Start()
        {
            if (_watcher != null || _disposed) return;

            // 全量首次扫描
            if (Directory.Exists(_directory))
            {
                foreach (var file in Directory.EnumerateFiles(_directory, "*.py", SearchOption.TopDirectoryOnly))
                    TryReloadFile(file);
            }

            _watcher = new FileSystemWatcher(_directory, "*.py")
            {
                NotifyFilter            = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents     = true,
                IncludeSubdirectories   = false,
            };

            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Deleted += OnDeleted;
            _watcher.Renamed += OnRenamed;
        }

        /// <summary>停止监听并释放 FileSystemWatcher。注册表里已注册的 handler 保持不变。</summary>
        public void Stop()
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        // ── watcher callbacks ─────────────────────────────────────────────────

        private void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            // FileSystemWatcher can fire multiple events per save; debounce via mtime
            try
            {
                var mtime = File.GetLastWriteTimeUtc(e.FullPath);
                if (_lastMtime.TryGetValue(e.FullPath, out var prev) && prev == mtime) return;
                _lastMtime[e.FullPath] = mtime;
            }
            catch { /* file may be locked briefly; ignore */ }

            // reload on thread-pool to not block the watcher thread
            ThreadPool.QueueUserWorkItem(_ => TryReloadFile(e.FullPath));
        }

        private void OnDeleted(object sender, FileSystemEventArgs e)
        {
            _lastMtime.TryRemove(e.FullPath, out _);
            ThreadPool.QueueUserWorkItem(_ => TryUnregisterFile(e.FullPath));
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            // old path → unregister; new path → register
            _lastMtime.TryRemove(e.OldFullPath, out _);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                TryUnregisterFile(e.OldFullPath);
                if (e.FullPath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                    TryReloadFile(e.FullPath);
            });
        }

        // ── reload logic ──────────────────────────────────────────────────────

        private void TryReloadFile(string filePath)
        {
            // serialize concurrent reloads of the same file
            lock (_reloadLock)
            {
                try
                {
                    var text        = File.ReadAllText(filePath);
                    var descriptors = PythonRegisterScanner.ScanText(text);
                    int registered  = 0;

                    foreach (var d in descriptors)
                    {
                        if (string.IsNullOrEmpty(d.Signature)) continue;   // no signature → skip

                        var delegateType = PythonTypeMapper.ResolveDelegateType(d.Signature);
                        if (delegateType == null) continue;

                        // Re-import module (IPythonRuntime.ImportModule is idempotent = hot-reload semantics)
                        var module = _registry.InternalImportModule(filePath);
                        if (!module.HasFunction(d.FunctionName)) continue;

                        var del = _registry.BuildPythonInvokerDelegate(delegateType, module, d.FunctionName);
                        _registry.RegisterDelegate(d.Name, del);   // atomic overwrite of old handler
                        registered++;
                    }

                    HotReloadOccurred?.Invoke(filePath, registered);
                }
                catch (Exception ex)
                {
                    HotReloadFailed?.Invoke(filePath, ex);
                }
            }
        }

        private void TryUnregisterFile(string filePath)
        {
            try
            {
                // We can only unregister handlers declared in this file.
                // Re-read the last known text is impractical (file deleted); we use the scanner's last scan
                // result if available. Simplest: unregister any handler whose source file = filePath.
                // PythonHandlerRegistry tracks source file per handler for exactly this case.
                _registry.UnregisterBySourceFile(filePath);
                HotReloadOccurred?.Invoke(filePath, 0);
            }
            catch (Exception ex)
            {
                HotReloadFailed?.Invoke(filePath, ex);
            }
        }
    }
}
