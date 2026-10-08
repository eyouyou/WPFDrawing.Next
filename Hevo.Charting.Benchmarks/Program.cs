using System;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;
using Hevo.Charting.PythonNet;
using Python.Runtime;

namespace Hevo.Charting.Benchmarks
{
    public static class Program
    {
        // 默认入口:BenchmarkSwitcher
        //   `dotnet run -c Release -- --filter "*"`
        //   `dotnet run -c Release -- --filter "*ReflectionVsCompiled*"`
        //
        // §D2.8 沙箱探针入口(子进程隔离测沙箱):
        //   `dotnet run -c Release -- --sandbox-probe --blocked=os,subprocess --test-import=os`
        // 退出码契约:
        //   0  = 目标 import 成功(沙箱没拦,或确实没设)
        //   42 = ImportError(沙箱拦截,这是 BlockedImports 命中)
        //   99 = 其他错误(Python 启动失败 / 配置缺失 / unknown exception)
        //
        // xUnit SandboxIsolationTests 通过 Process.Start 拉本 exe,跨进程绕开 CPython single-init。
        // Probe 一次只测一个场景 —— 测多个场景就启多个进程。
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--sandbox-probe")
                return SandboxProbeMain(args);

            // 增量渲染前后对比(真窗口,非 BenchmarkDotNet,UI 线程 CPU 管线):
            //   `dotnet run -c Release -- --render-probe [--bars=2000,100000] [--charts=1,4] [--rounds=5] [--baseline=...]`
            if (args.Length > 0 && args[0] == "--render-probe")
                return RenderProbe.Run(args);

            // 输入到画面的端到端延迟(SendInput + 抓屏,含 WPF 渲染线程合成上屏):
            //   `dotnet run -c Release -- --latency-probe [--samples=150]`
            if (args.Length > 0 && args[0] == "--latency-probe")
                return LatencyProbe.Run(args);

            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, BenchConfig(args));
            return 0;
        }

        // BDN 0.14 不认识 .NET 10:按宿主 runtime 生成的 boilerplate 是 TargetFramework=net10.0,
        // 引用本项目(net10.0-windows10.0.19041.0)时 NU1201,所有走子进程的基准都 NA。
        // 这里把默认 job 的工具链钉到本项目的 TFM;--warmupCount / --iterationCount 等在它上面叠加。
        // config 里的默认 job 会顶掉命令行 --job 选的基准 job,所以 --job 在这里自己映射。
        // InProcess 的基准([Config(typeof(InProcessConfig))])自带工具链,不受影响。
        private static IConfig BenchConfig(string[] args)
        {
            var toolchain = CsProjCoreToolchain.From(
                new NetCoreAppSettings("net10.0-windows10.0.19041.0", runtimeFrameworkVersion: null, name: ".NET 10.0 (windows)"));

            int i = Array.FindIndex(args, a => a is "--job" or "-j");
            string jobName = i >= 0 && i + 1 < args.Length ? args[i + 1].ToLowerInvariant() : "default";
            Job baseJob = jobName switch
            {
                "dry" => Job.Dry,
                "short" => Job.ShortRun,
                "medium" => Job.MediumRun,
                "long" => Job.LongRun,
                "verylong" => Job.VeryLongRun,
                _ => Job.Default,
            };
            return DefaultConfig.Instance.AddJob(baseJob.WithToolchain(toolchain).AsDefault());
        }

        private static int SandboxProbeMain(string[] args)
        {
            try
            {
                string blocked = ExtractArg(args, "--blocked=") ?? "";
                string testImport = ExtractArg(args, "--test-import=") ?? "";
                if (string.IsNullOrEmpty(testImport))
                {
                    Console.Error.WriteLine("[sandbox-probe] --test-import=<module> 必填");
                    return 99;
                }

                var blockedSet = new System.Collections.Generic.HashSet<string>(
                    blocked.Split(',', StringSplitOptions.RemoveEmptyEntries)
                           .Select(s => s.Trim()).Where(s => s.Length > 0),
                    StringComparer.Ordinal);

                var dll = ResolvePythonDll();
                if (dll == null)
                {
                    Console.Error.WriteLine("[sandbox-probe] Python312/python312.dll 找不到");
                    return 99;
                }
                EnsurePythonHome(Path.GetDirectoryName(dll)!);

                var runtime = new PythonNetRuntime(dll);
                runtime.Initialize(new PythonSandboxOptions
                {
                    BlockedImports = blockedSet,
                });

                using (Py.GIL())
                {
                    try
                    {
                        // 不写到磁盘,直接 PyEngine.Exec 跑 import 看抛不抛
                        PythonEngine.Exec($"import {testImport}");
                        Console.WriteLine($"[sandbox-probe] '{testImport}' 导入成功(blocked={blocked})");
                        return 0;
                    }
                    catch (PythonException pex) when (pex.Type != null && pex.Type.ToString()!.Contains("ImportError"))
                    {
                        Console.WriteLine($"[sandbox-probe] '{testImport}' 被拦:{pex.Message}");
                        return 42;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[sandbox-probe] unexpected: {ex.GetType().Name}: {ex.Message}");
                return 99;
            }
        }

        private static string? ExtractArg(string[] args, string prefix)
        {
            foreach (var a in args)
                if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length);
            return null;
        }

        // 跟 PythonMarshallingBenchmarks / RealPythonFixture 同款 dll 解析路径
        private static string? ResolvePythonDll()
        {
            var fromBin = Path.Combine(AppContext.BaseDirectory, "Python312", "python312.dll");
            if (File.Exists(fromBin)) return fromBin;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Python312", "python312.dll");
                if (File.Exists(candidate)) return candidate;
            }
            return Environment.GetEnvironmentVariable("PYTHONNET_PYDLL") is { Length: > 0 } env && File.Exists(env)
                ? env : null;
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
                Environment.SetEnvironmentVariable("PYTHONPATH",
                    string.Join(Path.PathSeparator, libDir, dllsDir, sitePackages));
            }
        }
    }
}
