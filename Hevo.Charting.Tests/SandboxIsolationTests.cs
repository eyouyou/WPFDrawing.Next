using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §D2.8 沙箱 BlockedImports 真拦截 — 通过子进程隔离绕开 CPython single-init 约束。
    ///
    /// <para>
    /// <b>背景</b>:<c>ImportInterceptor.Install(BlockedImports)</c> 在 PythonEngine.Initialize
    /// 时一次性装,后续不更新。同进程 fixture 已经 Initialize 过(空 BlockedImports),
    /// 没法在主测进程内"另起一份带不同沙箱的 runtime"。
    /// </para>
    ///
    /// <para>
    /// <b>解法</b>:把沙箱探针嵌进 <c>Hevo.Charting.Benchmarks</c> exe 的 Program.cs(<c>--sandbox-probe</c>
    /// 参数命中走 SandboxProbeMain),每个测试场景启一个新进程,进程内 Python 单例独立、
    /// BlockedImports 当次配置生效。
    /// </para>
    ///
    /// <para>
    /// <b>退出码契约</b>:0 = import 成功;42 = 被沙箱拦(ImportError);99 = 其他错误。
    /// </para>
    /// </summary>
    public sealed class SandboxIsolationTests
    {
        private const int EXIT_IMPORT_OK     = 0;
        private const int EXIT_IMPORT_BLOCKED = 42;
        private const int EXIT_OTHER_ERROR    = 99;

        // ── helpers ────────────────────────────────────────────────────────

        /// <summary>
        /// 跨配置兼容定位 Hevo.Charting.Benchmarks.exe —— Tests 跑 Debug,Benchmarks 一般 Release,
        /// 但开发阶段 Debug 也能跑。两边都试,先找到的为准。
        /// </summary>
        private static string? FindBenchmarksExe()
        {
            // 走 BaseDirectory 父链找 Benchmarks 项目目录
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                var benchProj = Path.Combine(dir.FullName, "Hevo.Charting.Benchmarks");
                if (!Directory.Exists(benchProj)) continue;
                // 优先 Release(perf 友好+一般 CI 跑这个),fallback Debug
                foreach (var conf in new[] { "Release", "Debug" })
                {
                    var exe = Path.Combine(benchProj, "bin", conf, "net8.0-windows10.0.19041.0", "Hevo.Charting.Benchmarks.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
            return null;
        }

        private static (int ExitCode, string Stdout, string Stderr) RunProbe(string blocked, string testImport)
        {
            var exe = FindBenchmarksExe();
            if (exe == null) return (-1, "", "Benchmarks.exe 未构建");

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "--sandbox-probe", $"--blocked={blocked}", $"--test-import={testImport}" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string so = p.StandardOutput.ReadToEnd();
            string se = p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            return (p.ExitCode, so, se);
        }

        // ── tests ─────────────────────────────────────────────────────────

        [Fact]
        public void Sandbox_BlocksConfiguredImport()
        {
            var (rc, so, se) = RunProbe(blocked: "os", testImport: "os");
            if (rc < 0) return;   // skip:Benchmarks 没构建,本地开发场景跑不了
            Assert.Equal(EXIT_IMPORT_BLOCKED, rc);
            Assert.Contains("被拦", so);
        }

        [Fact]
        public void Sandbox_EmptyBlockedList_AllowsAnything()
        {
            var (rc, so, se) = RunProbe(blocked: "", testImport: "json");
            if (rc < 0) return;
            Assert.Equal(EXIT_IMPORT_OK, rc);
            Assert.Contains("导入成功", so);
        }

        [Fact]
        public void Sandbox_BlockedListNotMatchingImport_StillAllows()
        {
            // BlockedImports 里有 'os' 但用户 import 'json' 应放过
            var (rc, so, se) = RunProbe(blocked: "os,subprocess", testImport: "json");
            if (rc < 0) return;
            Assert.Equal(EXIT_IMPORT_OK, rc);
        }

        [Fact]
        public void Sandbox_MultipleBlocked_AllRespected()
        {
            // 多个 import 各自单独探:确认列表里的都被拦
            foreach (var target in new[] { "os", "subprocess", "socket" })
            {
                var (rc, so, se) = RunProbe(blocked: "os,subprocess,socket", testImport: target);
                if (rc < 0) return;
                Assert.Equal(EXIT_IMPORT_BLOCKED, rc);
            }
        }

        [Fact]
        public void Sandbox_NumpyImport_NotBlockedBySubprocessBan()
        {
            // 关键 case:沙箱开 'subprocess' / 'socket' 拦截 —— numpy 自己不 import 这俩,
            // 所以应仍可加载。这是真实业务场景(blocked={subprocess,socket} 是合理 baseline)。
            //
            // 反例:blocked={os} 会把 numpy 也连带拦掉 —— numpy.__init__ 内部 import os。
            // 这是 ImportInterceptor §D2.5.4 设计文档明示的限制:"BlockedImports 默认空集合,
            // 因为 numpy/pandas/typing 启动期依赖 sys/importlib/shutil 等";业务侧按需 opt-in,
            // 但 'os' 太底层不能列入(numpy 都跑不起来)。
            var (rc, so, se) = RunProbe(blocked: "subprocess,socket", testImport: "numpy");
            if (rc < 0) return;
            Assert.Equal(EXIT_IMPORT_OK, rc);
        }

        [Fact]
        public void Sandbox_OsBan_BlocksNumpy_DocumentedLimitation()
        {
            // 反向 case 文档化:开 'os' 拦截会把 numpy 也带挂 —— 不是 bug 是设计权衡。
            // 这条测试存在的目的:防止有人后续"修复"它(以为是 bug),误改 ImportInterceptor 让安全降级。
            var (rc, so, se) = RunProbe(blocked: "os", testImport: "numpy");
            if (rc < 0) return;
            Assert.Equal(EXIT_IMPORT_BLOCKED, rc);
        }
    }
}
