using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §D2.8 PythonNetRuntime 行为契约 e2e 测试 —— Init 幂等 / 热重载 importlib.reload 语义 /
    /// 模块缓存复用。沙箱 BlockedImports 实测较 tricky(进程级 single-init 约束让"再开一份带不同沙箱的 runtime"
    /// 跑不动),先文档化 + 后续单独 fixture 处理。
    /// </summary>
    [Collection(nameof(RealPythonCollection))]
    public sealed class PythonRuntimeBehaviorE2ETests
    {
        private readonly RealPythonFixture _fx;

        public PythonRuntimeBehaviorE2ETests(RealPythonFixture fx)
        {
            _fx = fx;
        }

        // ── Init 幂等 ──────────────────────────────────────────────────────

        [Fact]
        public void Initialize_CalledTwice_DoesNotThrow()
        {
            if (!_fx.Available) return;
            var registry = _fx.Registry!;

            // fixture 已经 Initialize 过一次,这里再调一次应静默 no-op。
            // 内部 PythonNetRuntime._initialized 守卫拦下重入,不会出现 PythonEngine 二次启动。
            var ex = Record.Exception(() => registry.Initialize(new Hevo.Charting.PythonNet.PythonSandboxOptions
            {
                AllowedRootDirectory = _fx.IndicatorsDir,
            }));
            Assert.Null(ex);
        }

        [Fact]
        public void Registry_TryGet_AfterMultipleInits_ReturnsSameInstance()
        {
            if (!_fx.Available) return;
            var registry = _fx.Registry!;

            // 先注册一个 handler
            var pyFile = Path.Combine(_fx.IndicatorsDir, $"idem_{Guid.NewGuid():N}.py");
            File.WriteAllText(pyFile, """
                from hevo_indicators import register
                @register('idem_ma', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
                def idem_ma(close): return close
                """);
            registry.AutoDiscoverDirectory(_fx.IndicatorsDir);

            var del1 = registry.TryGet("idem_ma");

            // 再 Initialize 一次
            registry.Initialize();

            // handler 应仍然在(Init 幂等不破坏既注册条目)
            var del2 = registry.TryGet("idem_ma");
            Assert.NotNull(del2);
            Assert.Same(del1, del2);
        }

        // ── 热重载:importlib.reload 语义 ─────────────────────────────────

        [Fact]
        public void HotReload_FileContentChanges_HandlerInvokesNewImpl()
        {
            if (!_fx.Available) return;
            var registry = _fx.Registry!;

            var pyFile = Path.Combine(_fx.IndicatorsDir, $"reload_{Guid.NewGuid():N}.py");

            // v1:返回 close * 2
            File.WriteAllText(pyFile, """
                from hevo_indicators import register
                import numpy as np
                @register('reload_test', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
                def reload_test(close):
                    return np.asarray(close, dtype=np.float64) * 2.0
                """);
            registry.RegisterModule("reload_test", pyFile, "reload_test", "(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]");

            var del = registry.TryGet("reload_test") as Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>;
            Assert.NotNull(del);

            var input = new[] { 1.0, 2.0, 3.0 };
            var v1 = del!(input);
            Assert.Equal(2.0, v1.Span[0]);
            Assert.Equal(4.0, v1.Span[1]);

            // v2:返回 close + 100(改 .py 内容,触发 InternalImportModule 走 importlib.reload)
            File.WriteAllText(pyFile, """
                from hevo_indicators import register
                import numpy as np
                @register('reload_test', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
                def reload_test(close):
                    return np.asarray(close, dtype=np.float64) + 100.0
                """);
            // 模拟 hot reloader 调用路径:Unregister + Re-import + Re-register
            registry.UnregisterBySourceFile(pyFile);
            registry.RegisterModule("reload_test", pyFile, "reload_test", "(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]");

            var del2 = registry.TryGet("reload_test") as Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>;
            Assert.NotNull(del2);

            var v2 = del2!(input);
            Assert.Equal(101.0, v2.Span[0], 1e-9);
            Assert.Equal(102.0, v2.Span[1], 1e-9);
            Assert.Equal(103.0, v2.Span[2], 1e-9);
        }

        // ── UnregisterBySourceFile 删干净相关 handler ─────────────────────

        [Fact]
        public void UnregisterBySourceFile_RemovesHandlersAndInputsMetadata()
        {
            if (!_fx.Available) return;
            var registry = _fx.Registry!;

            var pyFile = Path.Combine(_fx.IndicatorsDir, $"unreg_{Guid.NewGuid():N}.py");
            File.WriteAllText(pyFile, """
                from hevo_indicators import register
                @register('unreg_a', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
                def a(c): return c

                @register('unreg_b',
                          signature='(ReadOnlyMemory[double], ReadOnlyMemory[double]) -> ReadOnlyMemory[double]',
                          inputs=['x', 'y'])
                def b(x, y): return x
                """);
            registry.AutoDiscoverDirectory(_fx.IndicatorsDir);

            Assert.True(registry.Contains("unreg_a"));
            Assert.True(registry.Contains("unreg_b"));
            Assert.NotNull(registry.GetInputNames("unreg_b"));   // 多输入元数据登记进去了

            registry.UnregisterBySourceFile(pyFile);

            Assert.False(registry.Contains("unreg_a"));
            Assert.False(registry.Contains("unreg_b"));
            Assert.Null(registry.GetInputNames("unreg_b"));    // inputs 元数据也清掉
        }

        // ── 沙箱 AllowedRootDirectory 守护 ────────────────────────────────

        [Fact]
        public void RegisterModule_PathOutsideAllowedRoot_Throws()
        {
            if (!_fx.Available) return;
            var registry = _fx.Registry!;

            // fixture AllowedRootDirectory 是 _fx.IndicatorsDir,这里故意拼一个外部路径
            var outsidePath = Path.Combine(Path.GetTempPath(), "definitely_not_inside_sandbox.py");
            Assert.Throws<UnauthorizedAccessException>(() =>
                registry.RegisterModule("pwn", outsidePath, "f", "(int) -> int"));
        }

        // ── PerCallTimeoutWatchdog 真 Python 路径 ─────────────────────────

        [Fact]
        public void Timeout_LongRunningPythonHandler_ThrowsTimeoutException()
        {
            if (!_fx.Available) return;
            var registry = _fx.Registry!;

            // fixture PerCallTimeoutMs = 5000,我们写个 sleep 6.5 秒的 handler,期待 ~5s 抛 TimeoutException。
            // time.sleep 释放 GIL,所以不会卡死后续测试(orphan 线程睡到自然结束)。
            var pyFile = Path.Combine(_fx.IndicatorsDir, $"slow_{Guid.NewGuid():N}.py");
            File.WriteAllText(pyFile, """
                from hevo_indicators import register
                import time

                @register('slow_handler', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
                def slow_handler(close):
                    time.sleep(6.5)   # > fixture timeout 5s
                    return close
                """);
            registry.AutoDiscoverDirectory(_fx.IndicatorsDir);

            var del = registry.TryGet("slow_handler") as Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>;
            Assert.NotNull(del);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ex = Assert.Throws<TimeoutException>(() => del!(new[] { 1.0, 2.0, 3.0 }));
            sw.Stop();

            // 调用方应在 ~5000ms 处拿到 TimeoutException,不该等满 6500ms
            Assert.True(sw.ElapsedMilliseconds >= 4500, $"超时应至少等到配置阈值 5000ms 才触发,实际 {sw.ElapsedMilliseconds}ms");
            Assert.True(sw.ElapsedMilliseconds < 6300, $"超时应早于 sleep 完成时间 6500ms,实际 {sw.ElapsedMilliseconds}ms");
            Assert.Contains("超时", ex.Message);
        }

        [Fact]
        public void Timeout_FastHandler_NotAffected()
        {
            if (!_fx.Available) return;
            var registry = _fx.Registry!;

            // 短作业(< fixture 5s timeout)不该被切
            var pyFile = Path.Combine(_fx.IndicatorsDir, $"fast_{Guid.NewGuid():N}.py");
            File.WriteAllText(pyFile, """
                from hevo_indicators import register
                import numpy as np

                @register('fast_handler', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
                def fast_handler(close):
                    return np.asarray(close, dtype=np.float64) + 1.0
                """);
            registry.AutoDiscoverDirectory(_fx.IndicatorsDir);

            var del = (Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>)registry.TryGet("fast_handler")!;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var output = del(new[] { 1.0, 2.0, 3.0 });
            sw.Stop();

            Assert.Equal(3, output.Length);
            Assert.Equal(2.0, output.Span[0]);
            Assert.Equal(3.0, output.Span[1]);
            Assert.Equal(4.0, output.Span[2]);
            Assert.True(sw.ElapsedMilliseconds < 1000, $"短作业不应进 timeout 等待,实际 {sw.ElapsedMilliseconds}ms");
        }
    }
}
