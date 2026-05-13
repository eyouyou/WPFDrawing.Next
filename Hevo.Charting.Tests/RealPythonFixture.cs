using System;
using System.Collections.Generic;
using System.IO;
using Hevo.Charting.PythonNet;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §D2.8 真 Python 端到端测试基础设施 ——
    /// 进程级单例 <see cref="PythonNetRuntime"/> + <see cref="PythonHandlerRegistry"/>(CPython 嵌入式
    /// 全进程只能 <c>PythonEngine.Initialize</c> 一次,xUnit Collection Fixture 强制 e2e 测试串行 + 共享一份)。
    ///
    /// <para>
    /// <b>Python 不可用时</b>(CI 没安装 / Windows 找不到 python312.dll)→ <see cref="Available"/>=false,
    /// 测试方法用 <c>Skip.IfNot(_fixture.Available, ...)</c> 跳过,不让 CI 因为环境硬挂。
    /// </para>
    /// </summary>
    public sealed class RealPythonFixture : IDisposable
    {
        public bool Available { get; private set; }
        public string? SkipReason { get; private set; }
        public PythonHandlerRegistry? Registry { get; private set; }

        public string IndicatorsDir { get; }

        public RealPythonFixture()
        {
            // 临时目录给测试用 .py 落盘 —— 测试结束 Dispose 清理
            IndicatorsDir = Path.Combine(Path.GetTempPath(), $"hevo_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(IndicatorsDir);

            var dllPath = ResolveLocalPythonDll();
            if (dllPath == null)
            {
                SkipReason = "本机找不到 Python312/python312.dll —— e2e 测试跳过(添加 Python 嵌入式即可启用)";
                Available = false;
                return;
            }

            try
            {
                EnsurePythonHome(Path.GetDirectoryName(dllPath)!);
                var runtime = new PythonNetRuntime(dllPath);
                Registry = new PythonHandlerRegistry()
                    .UseRuntime(runtime)
                    .Initialize(new PythonSandboxOptions
                    {
                        AllowedRootDirectory = IndicatorsDir,
                        PerCallTimeoutMs     = 5000,
                    });
                Available = true;
            }
            catch (Exception ex)
            {
                SkipReason = $"PythonNetRuntime.Initialize 抛异常: {ex.Message}";
                Available = false;
            }
        }

        public void Dispose()
        {
            try { Registry?.Shutdown(); } catch { /* shutdown best-effort */ }
            try
            {
                if (Directory.Exists(IndicatorsDir)) Directory.Delete(IndicatorsDir, recursive: true);
            }
            catch { /* cleanup best-effort */ }
        }

        // ── Python DLL / HOME 解析(跟 DemoPythonHost 同款,但不依赖业务) ─────────

        private static string? ResolveLocalPythonDll()
        {
            // 1. 测试 bin 目录里(若 csproj 已配置 robocopy 拷贝)
            var fromBin = Path.Combine(AppContext.BaseDirectory, "Python312", "python312.dll");
            if (File.Exists(fromBin)) return fromBin;

            // 2. 沿 BaseDirectory 父目录链找 Python312/python312.dll(典型路径:repo 根)
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Python312", "python312.dll");
                if (File.Exists(candidate)) return candidate;
            }

            // 3. PYTHONNET_PYDLL env(用户显式配置)
            var fromEnv = Environment.GetEnvironmentVariable("PYTHONNET_PYDLL");
            if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;

            return null;
        }

        private static void EnsurePythonHome(string pythonDir)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PYTHONHOME")))
                Environment.SetEnvironmentVariable("PYTHONHOME", pythonDir);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PYTHONPATH")))
            {
                var libDir = Path.Combine(pythonDir, "Lib");
                var dllsDir = Path.Combine(pythonDir, "DLLs");
                var sitePackages = Path.Combine(libDir, "site-packages");
                Environment.SetEnvironmentVariable(
                    "PYTHONPATH",
                    string.Join(Path.PathSeparator, libDir, dllsDir, sitePackages));
            }
        }
    }

    /// <summary>
    /// xUnit Collection 定义 —— 所有标 <c>[Collection(nameof(RealPythonCollection))]</c> 的测试类
    /// 共享一份 <see cref="RealPythonFixture"/>(必要,因为 CPython 进程级 single-init),且**串行执行**
    /// (xUnit collection 内部默认 sequential)。
    /// </summary>
    [CollectionDefinition(nameof(RealPythonCollection))]
    public sealed class RealPythonCollection : ICollectionFixture<RealPythonFixture>
    {
        // 占位符 — xUnit 通过 collection name 找 fixture
    }
}
