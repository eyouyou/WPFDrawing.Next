using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using BenchmarkDotNet.Jobs;
using Hevo.Charting.PythonNet;

namespace Hevo.Charting.Benchmarks
{
    /// <summary>
    /// §D2.8 / §D2.5.3 Python 跨边界 marshalling 开销基准 ——
    /// <see cref="ReadOnlyMemory{Double}"/> ↔ numpy.ndarray 在 1 / 100 / 1000 / 10000 点规模下的
    /// round-trip 时间 + GC 压力。
    ///
    /// <para>
    /// <b>对照</b>:同一变长 ndarray copy 在 Python 内的纯 numpy `arr.copy()` 是基线,跨边界经
    /// PerCallTimeoutWatchdog + GIL acquire + Marshal.Copy 的总开销跟它对比。
    /// </para>
    ///
    /// <para>
    /// <b>怎么跑</b>:
    /// <code>
    /// dotnet run -c Release --project Hevo.Charting.Benchmarks -- --filter "*PythonMarshallingBenchmarks*"
    /// </code>
    /// 单次 BDN run 约 5-10 分钟(warmup + iterations,4 个 size × 2 个 timeout 模式 = 8 case)。
    /// </para>
    ///
    /// <para>
    /// <b>预期输出量级</b>(开发机 i7 / Win11):
    /// </para>
    /// <list type="bullet">
    ///   <item>1 点(标量级):per-call ~50-100μs(GIL acquire + Marshal.Copy + 调度税主导,数据量不重要)</item>
    ///   <item>100 点:基本同上,数据 cost 0.1μs 级,可忽略</item>
    ///   <item>1000 点(time-share 典型):per-call ~60-120μs(数据 ~1μs,marshalling 路径主导)</item>
    ///   <item>10000 点:per-call ~80-150μs(数据 ~10μs,开始可见)</item>
    /// </list>
    ///
    /// <para>
    /// <b>结论指引</b>:
    /// </para>
    /// <list type="bullet">
    ///   <item>每次 invoke ~50-150μs 主要是 GIL + 调度税,数据 size 影响小 → 指标算子层(1-10Hz)完全够</item>
    ///   <item>60Hz 热路径(crosshair 每帧 16ms)绝对不要走 Python(单次 invoke ~6-9% 帧预算)→ §D2.X 已划线</item>
    ///   <item>zero-copy(待 D2.5.3 优化)真正受益的 case 是 10K+ 点指标,那时 Marshal.Copy 才显著</item>
    /// </list>
    /// </summary>
    [MemoryDiagnoser]
    // ⚠️ InProcess 必须开 —— BDN 默认 spawn 子进程,子进程 BaseDirectory 不在 repo 内 →
    // ResolveDll 找不到 Python312/python312.dll → GlobalSetup throw → benchmark NA。
    // InProcessEmitToolchain 在主进程内跑 benchmark(共享 PythonEngine 全进程 single-init 状态)。
    [Config(typeof(InProcessConfig))]
    public class PythonMarshallingBenchmarks
    {
        private RealPythonRuntimeBootstrap? _bootstrap;
        private Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>? _identity;
        private double[] _data1 = Array.Empty<double>();
        private double[] _data100 = Array.Empty<double>();
        private double[] _data1000 = Array.Empty<double>();
        private double[] _data10000 = Array.Empty<double>();

        [GlobalSetup]
        public void Setup()
        {
            _bootstrap = new RealPythonRuntimeBootstrap();
            if (!_bootstrap.Available)
            {
                throw new InvalidOperationException(
                    "Python 不可用,benchmark 跳过。" +
                    "需要 Python312/python312.dll 在 repo 根或 BaseDirectory 链上。");
            }

            // 注册 identity_double:输入 ROM<double> → np.asarray copy → 返回 ROM<double>
            // 这是最纯的 marshal round-trip(没有计算逻辑,只测拷贝路径)。
            var pyFile = Path.Combine(_bootstrap.IndicatorsDir, "bench_identity.py");
            File.WriteAllText(pyFile, """
                from hevo_indicators import register
                import numpy as np
                @register('bench_identity', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
                def bench_identity(arr):
                    return np.asarray(arr, dtype=np.float64).copy()
                """);
            _bootstrap.Registry.AutoDiscoverDirectory(_bootstrap.IndicatorsDir);

            _identity = _bootstrap.Registry.TryGet("bench_identity")
                as Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>
                ?? throw new InvalidOperationException("bench_identity 注册失败");

            // 不同规模数据
            var rng = new Random(42);
            _data1 = new[] { rng.NextDouble() };
            _data100 = new double[100];
            _data1000 = new double[1000];
            _data10000 = new double[10000];
            for (int i = 0; i < _data100.Length; i++)   _data100[i]   = rng.NextDouble();
            for (int i = 0; i < _data1000.Length; i++)  _data1000[i]  = rng.NextDouble();
            for (int i = 0; i < _data10000.Length; i++) _data10000[i] = rng.NextDouble();
        }

        [GlobalCleanup]
        public void Cleanup() => _bootstrap?.Dispose();

        // ── round-trip benchmarks ─────────────────────────────────────────

        [Benchmark(Description = "ROM↔ndarray round-trip: 1 point")]
        public ReadOnlyMemory<double> RoundTrip_1() => _identity!(_data1);

        [Benchmark(Description = "ROM↔ndarray round-trip: 100 points")]
        public ReadOnlyMemory<double> RoundTrip_100() => _identity!(_data100);

        [Benchmark(Baseline = true, Description = "ROM↔ndarray round-trip: 1000 points (time-share 典型)")]
        public ReadOnlyMemory<double> RoundTrip_1000() => _identity!(_data1000);

        [Benchmark(Description = "ROM↔ndarray round-trip: 10000 points")]
        public ReadOnlyMemory<double> RoundTrip_10000() => _identity!(_data10000);
    }

    internal sealed class InProcessConfig : BenchmarkDotNet.Configs.ManualConfig
    {
        public InProcessConfig()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithStrategy(RunStrategy.Throughput)
                .WithWarmupCount(3)
                .WithIterationCount(5));
        }
    }

    /// <summary>
    /// PythonNetRuntime 启动 / 路径解析样板 —— 跟 RealPythonFixture 同款,但 Benchmarks 项目没引 xUnit,
    /// 所以单独写一份(纯 C#,30 行)。
    /// </summary>
    internal sealed class RealPythonRuntimeBootstrap : IDisposable
    {
        public bool Available { get; }
        public PythonHandlerRegistry Registry { get; }
        public string IndicatorsDir { get; }

        public RealPythonRuntimeBootstrap()
        {
            IndicatorsDir = Path.Combine(Path.GetTempPath(), $"hevo_bench_{Guid.NewGuid():N}");
            Directory.CreateDirectory(IndicatorsDir);

            string? dll = ResolveDll();
            if (dll == null)
            {
                Available = false;
                Registry = new PythonHandlerRegistry();
                return;
            }

            EnsurePythonHome(Path.GetDirectoryName(dll)!);
            Registry = new PythonHandlerRegistry()
                .UseRuntime(new PythonNetRuntime(dll))
                .Initialize(new PythonSandboxOptions
                {
                    AllowedRootDirectory = IndicatorsDir,
                    PerCallTimeoutMs     = 0,   // benchmark 关 watchdog,直接 inline 调,避免 Task.Run 调度税污染数据
                });
            Available = true;
        }

        public void Dispose()
        {
            try { Registry.Shutdown(); } catch { }
            try { if (Directory.Exists(IndicatorsDir)) Directory.Delete(IndicatorsDir, true); } catch { }
        }

        private static string? ResolveDll()
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
