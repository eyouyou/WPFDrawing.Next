using System;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Running;
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

            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
            return 0;
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
