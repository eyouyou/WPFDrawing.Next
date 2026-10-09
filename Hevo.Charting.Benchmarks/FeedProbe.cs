using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.DevTools;
using Hevo.Charting.Features;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Benchmarks
{
    /// <summary>进程内共享一份 Python 运行时(CPython 只能初始化一次;PyFeed / Blueprint 都用它)。</summary>
    internal static class ProbePython
    {
        private static readonly Lazy<RealPythonRuntimeBootstrap> s_shared = new(() => new RealPythonRuntimeBootstrap());
        public static RealPythonRuntimeBootstrap Shared => s_shared.Value;
    }

    /// <summary>render-probe 里"不是逐帧脚本"的测量套件(数据源链路 / Python / 蓝图 / 长跑)的输出。</summary>
    internal sealed record SuiteResult(string Name, string Markdown, object Json, string Csv);

    /// <summary>实时行情的一根 K 线;Seq = 推入这根(或改写这根)的那个 tick 的序号,用来在帧里反查 tick 时间戳。</summary>
    public readonly struct ProbeFeedBar
    {
        public DateTime Time { get; init; }
        public double Open { get; init; }
        public double High { get; init; }
        public double Low { get; init; }
        public double Close { get; init; }
        public double Volume { get; init; }
        public double Seq { get; init; }
    }

    /// <summary>
    /// 实时数据源:OnFetchAsync 先灌一段历史,之后后台线程调 <see cref="PushTick"/>(走 UpdateBuffer:加锁改 buffer → Publish →
    /// Stream 推 DataSnapshot → pipe 的 Ingestor 写黑板 → ChartCell.RequestUpdate),跟真实行情源同一条路。
    /// 蓝图端到端用别名 "MockKLineDataSource" 登记,顶替 LowCodeDemo 的同名数据源。
    /// </summary>
    public sealed class ProbeFeedSource : ReactiveDataSource<ProbeFeedSource, string, ProbeFeedBar>
    {
        public static int SeedBars = 2000;
        public static int TicksPerBar = 50;
        /// <summary>最近一次 OnFetchAsync 完成的时刻(Stopwatch ticks),蓝图端到端用来判断首帧是否已带数据。</summary>
        public long FetchedAt;

        private int _count;
        private readonly ProbeData _seed;
        private double _lastClose;

        /// <summary>每创建一个实例触发一次(蓝图端到端里数据源由 DashboardLauncher 实例化,靠它拿到引用)。</summary>
        public static event Action<ProbeFeedSource>? Created;

        public ProbeFeedSource()
        {
            _seed = ProbeData.Generate(SeedBars);
            Created?.Invoke(this);
        }

        public override int LogicalLength => Volatile.Read(ref _count);

        protected override Task<int> OnFetchAsync(string context, CancellationToken token)
        {
            UpdateBuffer(buf =>
            {
                buf.Clear();
                for (int i = 0; i < SeedBars; i++)
                    buf.Add(new ProbeFeedBar
                    {
                        Time = _seed.Time[i], Open = _seed.Open[i], High = _seed.High[i], Low = _seed.Low[i],
                        Close = _seed.Close[i], Volume = _seed.Volume[i], Seq = -1,
                    });
                _lastClose = _seed.Close[SeedBars - 1];
                Volatile.Write(ref _count, buf.Count);
            });
            FetchedAt = Stopwatch.GetTimestamp();
            return Task.FromResult(SeedBars);
        }

        /// <summary>任意线程:推一个 tick。每 <see cref="TicksPerBar"/> 个 tick 收一根新 K 线,其余改写最后一根。</summary>
        public void PushTick(long seq)
        {
            UpdateBuffer(buf =>
            {
                double c = _lastClose + Math.Sin(seq * 0.37) * 0.2;
                _lastClose = c;
                var last = buf[^1];
                if (seq > 0 && seq % TicksPerBar == 0)
                {
                    buf.Add(new ProbeFeedBar
                    {
                        Time = last.Time.AddMinutes(1), Open = last.Close, High = Math.Max(last.Close, c), Low = Math.Min(last.Close, c),
                        Close = c, Volume = 100, Seq = seq,
                    });
                    Volatile.Write(ref _count, buf.Count);
                }
                else
                {
                    buf[^1] = last with { High = Math.Max(last.High, c), Low = Math.Min(last.Low, c), Close = c, Volume = last.Volume + 1, Seq = seq };
                }
            });
        }
    }

    /// <summary>在帧里(ProjectAll,UI 线程)读到的最新 tick 序号 —— 帧做完时据此结算这一批 tick 的延迟。</summary>
    internal sealed class SeqProbeFeature : Feature
    {
        public required DataPort<ReadOnlyMemory<double>> SeqPort { get; init; }
        public long LastSeq = -1;

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) { }

        protected override void OnProject(FeatureContext ctx)
        {
            var (v, _) = ctx.UsePort(SeqPort);
            if (v.Length > 0) LastSeq = (long)v.Span[^1];
        }
    }

    /// <summary>
    /// 实时行情主图:DataSource → pipe(LinkStream Map 各字段 → 黑板)→ ComputeFeature 算 SMA20(后台 WatchAsync)
    /// → 蜡烛 + SMA + 轴 + 标准交互。可选再挂 N 个 Python 指标(各自一个 ComputeFeature + 折线)。
    /// </summary>
    internal sealed class ProbeFeedSchema : ChartReactiveSchema
    {
        private readonly ProbeFeedSource _ds;
        private readonly IReadOnlyList<(string Name, Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>> Compute)> _extraIndicators;

        public DataPort<ReadOnlyMemory<DateTime>> TimePort { get; } = new("F_Time");
        public DataPort<ReadOnlyMemory<double>> OpenPort { get; } = new("F_Open");
        public DataPort<ReadOnlyMemory<double>> HighPort { get; } = new("F_High");
        public DataPort<ReadOnlyMemory<double>> LowPort { get; } = new("F_Low");
        public DataPort<ReadOnlyMemory<double>> ClosePort { get; } = new("F_Close");
        public DataPort<ReadOnlyMemory<double>> SeqPort { get; } = new("F_Seq");
        private readonly DataPort<ReadOnlyMemory<double>> _smaPort = new("F_SMA20");
        private readonly DataPort<RealRange> _yRangePort = new("F_YRange");
        public SeqProbeFeature SeqFeature { get; }

        public ProbeFeedSchema(ProbeFeedSource ds,
            IReadOnlyList<(string, Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>)>? extraIndicators = null)
        {
            _ds = ds;
            _extraIndicators = extraIndicators ?? Array.Empty<(string, Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>)>();
            SeqFeature = new SeqProbeFeature { SeqPort = SeqPort };
        }

        /// <summary>
        /// --feed-map=scatter(默认):<c>Map(port, b =&gt; b.X)</c> → ScatterIngestor(每次推送都 ForceWrite);
        /// source:<c>Map(port, (b, _) =&gt; b.X)</c> → FastSourceMapIngestor,跟 AutoMap 生成的代码同一条路径。
        /// </summary>
        public static bool SourceMap;

        protected override void DefineDataFlow(ChartCell chart)
        {
            var pipe = _ds.Pipe();
            if (SourceMap)
                pipe.LinkStream(cfg => cfg
                    .Map(TimePort, static (b, _) => b.Time)
                    .Map(OpenPort, static (b, _) => b.Open)
                    .Map(HighPort, static (b, _) => b.High)
                    .Map(LowPort, static (b, _) => b.Low)
                    .Map(ClosePort, static (b, _) => b.Close)
                    .Map(SeqPort, static (b, _) => b.Seq));
            else
                pipe.LinkStream(cfg => cfg
                    .Map(TimePort, b => b.Time)
                    .Map(OpenPort, b => b.Open)
                    .Map(HighPort, b => b.High)
                    .Map(LowPort, b => b.Low)
                    .Map(ClosePort, b => b.Close)
                    .Map(SeqPort, b => b.Seq));
            pipe.ProjectExtent(ViewportPorts.RequireAttached(Chart)).BindTo(chart);
        }

        protected override void DefineFeatures(IFeatureContext canvas)
        {
            canvas.Seed<ScaleStrategyTrait>(ScaleStrategyTrait.CandleMode);
            var hitPort = HitPort;
            var timeMeta = FieldMeta.Literal("时间", Colors.White, "yyyy-MM-dd HH:mm");
            var priceMeta = FieldMeta.Literal("价", Colors.LightGray, "F2");
            var smaMeta = FieldMeta.Literal("SMA20", Color.FromRgb(0xFF, 0xB7, 0x4D), "F2");

            canvas.Add(new ComputeFeature { InputPort = ClosePort, OutputPort = _smaPort, Compute = (Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>)Sma20 });
            canvas.Add(SeqFeature);

            canvas
                .Environment(env => env
                    .SetupLayout(left: ChartLength.Pixel(60), top: ChartLength.Pixel(28), right: ChartLength.Pixel(0), bottom: ChartLength.Pixel(24))
                    .SetupViewport(minVisibleCount: 10, alignment: ViewportAlignment.RightEdge, defaultVisibleCount: 120,
                        overscrollMin: OverscrollPolicy.Hard, overscrollMax: OverscrollPolicy.Hard)
                    .SetupAutoScale(yRangePort: _yRangePort, valuePorts: new[] { HighPort, LowPort, _smaPort }, paddingRatio: 0.05)
                    .SetupUniversalHeader(hitPort: hitPort))
                .Axes(axes => axes
                    .AddDomainAxis(TimePort, ViewportPorts.RequireAttached(Chart), timeMeta)
                    .AddRangeAxis(_yRangePort, priceMeta, AxisPlacement.Right))
                .Series(series => series
                    .AddCandle(rangePort: _yRangePort, ports: new CandlePorts(TimePort, OpenPort, HighPort, LowPort, ClosePort), groupName: "FeedCandle",
                        metas: new CandleMetas(FieldMeta.Literal("开", Colors.Gray, "F2"), FieldMeta.Literal("高", Colors.Gray, "F2"),
                                               FieldMeta.Literal("低", Colors.Gray, "F2"), FieldMeta.Literal("收", Colors.Gray, "F2")))
                    .AddLine(dataPort: _smaPort, rangePort: _yRangePort, meta: smaMeta, thickness: 1.5))
                .Interactions(i => i.EnableStandard(domainDataPort: TimePort, domainMeta: timeMeta,
                    options: new InteractionOptions<DateTime> { HitPort = hitPort, Modes = ChartInteractionMode.All }));

            // Python 指标:各自一个 ComputeFeature(后台 WatchAsync 并发跑,抢 GIL)+ 一条折线(不进 AutoScale)
            for (int k = 0; k < _extraIndicators.Count; k++)
            {
                var (name, compute) = _extraIndicators[k];
                var outPort = new DataPort<ReadOnlyMemory<double>>($"F_Py{k}");
                canvas.Add(new ComputeFeature { InputPort = ClosePort, OutputPort = outPort, Compute = compute });
                canvas.Series(s => s.AddLine(dataPort: outPort, rangePort: _yRangePort,
                    meta: FieldMeta.Literal(name, Colors.MediumPurple, "F2"), thickness: 1.0));
            }
        }

        private static ReadOnlyMemory<double> Sma20(ReadOnlyMemory<double> close)
        {
            const int len = 20;
            var src = close.Span;
            var result = new double[src.Length];
            double sum = 0;
            for (int i = 0; i < src.Length; i++)
            {
                sum += src[i];
                if (i >= len) sum -= src[i - len];
                result[i] = i >= len - 1 ? sum / len : double.NaN;
            }
            return result;
        }
    }

    /// <summary>
    /// Feed / PyFeed / Soak 套件:真窗口、真 CompositionTarget 渲染节奏(不手动跑帧),后台线程按固定频率推 tick。
    /// 每帧做完(IncrementalRenderProbe.FrameRendered)时读帧里看到的最新 tick 序号,
    /// 这一帧"交付"的 tick = 上一帧之后、到这个序号为止的所有 tick,各自的延迟 = 帧完成时刻 − tick 推入时刻。
    /// 只算到 UI 线程这一帧做完,不含渲染线程合成 / Present(那部分见 --latency-probe)。
    /// </summary>
    internal static class FeedProbe
    {
        internal sealed record FeedRun(
            string Label, int TargetRate, double Seconds, long Pushed, double PushRate, long Delivered, int Frames,
            double FrameRate, List<double> LatencyMs, double AllocMBps, double Gen0ps, double Gen1ps, double Gen2ps,
            double CpuPct, List<double>? ComputeMs, int Indicators = 0, double GcPauseMsPerSec = 0, double WorkingSetMB = 0,
            double FeaturesPerFrame = 0, double LayersPerFrame = 0);

        private sealed class Rig : IDisposable
        {
            public required Window Window { get; init; }
            public required ChartCell Cell { get; init; }
            public required ProbeFeedSource Source { get; init; }
            public required ProbeFeedSchema Schema { get; init; }
            public void Dispose() { Window.Close(); RenderProbe.Pump(50); }
        }

        private static Rig CreateRig(ProbeOptions opt, IReadOnlyList<(string, Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>)>? indicators = null)
        {
            var ds = new ProbeFeedSource();
            var schema = new ProbeFeedSchema(ds, indicators);
            // 不接收鼠标:真实鼠标停在窗口上会额外触发十字光标 / tooltip 重算,干扰测量
            var cell = new ChartCell { Template = schema, Width = opt.WindowWidth, Height = opt.WindowHeight, IsHitTestVisible = false };
            var window = new Window
            {
                Title = "Hevo render probe (feed)",
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                Content = cell,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            window.Show();
            RenderProbe.Pump(300);
            ds.SwitchContext("probe");
            RenderProbe.Pump(500);
            return new Rig { Window = window, Cell = cell, Source = ds, Schema = schema };
        }

        /// <summary>按 rate tick/s 推 seconds 秒,返回交付统计。warmup 秒先推不计。</summary>
        /// <summary>--alloc-types 时 Feed / Soak 按全进程采样分配类型(推送线程上的 Publish 也算进来)。</summary>
        internal static AllocTypeSampler? Sampler;
        internal static readonly List<(string Label, List<AllocTypeSampler.Row> Rows, double Seconds)> SamplerResults = new();

        private static FeedRun Measure(Rig rig, string label, int rate, double seconds, double warmup, ConcurrentQueue<double>? computeMs = null)
        {
            long total = (long)Math.Ceiling(rate * (seconds + warmup)) + 1;
            var pushAt = new long[total + 1];
            long pushed = 0;
            long warmupEndSeq = (long)(rate * warmup);
            long seqBase = rig.Schema.SeqFeature.LastSeq + 1; // 多次测量共用一个 rig 时序号接着往后排
            var latencies = new List<double>((int)Math.Min(int.MaxValue, total));
            long prevSeen = seqBase - 1;
            int frames = 0;
            long featureSum = 0, layerSum = 0, featureMark = IncrementalRenderProbe.FeatureProjections;
            long measureStartTicks = 0;
            bool measuring = false;

            IncrementalRenderProbe.FrameRendered = cell =>
            {
                if (!ReferenceEquals(cell, rig.Cell)) return;
                long now = Stopwatch.GetTimestamp();
                long fp = IncrementalRenderProbe.FeatureProjections;
                if (measuring) { featureSum += fp - featureMark; layerSum += cell.LastFrameDirtyLayers; }
                featureMark = fp;
                long seen = rig.Schema.SeqFeature.LastSeq;
                if (seen <= prevSeen) return;
                for (long s = Math.Max(prevSeen + 1, seqBase); s <= seen; s++)
                {
                    long local = s - seqBase;
                    if (local < 0 || local >= pushAt.Length) continue;
                    long t = Volatile.Read(ref pushAt[local]);
                    if (t == 0 || local < warmupEndSeq) continue;
                    latencies.Add((now - t) * 1000.0 / Stopwatch.Frequency);
                }
                if (measuring) frames++;
                prevSeen = seen;
            };

            var stop = new ManualResetEventSlim(false);
            var pusher = new Thread(() =>
            {
                long period = Stopwatch.Frequency / rate;
                long start = Stopwatch.GetTimestamp();
                for (long k = 0; k < total - 1 && !stop.IsSet; k++)
                {
                    long due = start + k * period;
                    while (true)
                    {
                        long now = Stopwatch.GetTimestamp();
                        if (now >= due) break;
                        if (due - now > Stopwatch.Frequency / 500) Thread.Sleep(1);
                        else Thread.SpinWait(20);
                    }
                    Volatile.Write(ref pushAt[k], Stopwatch.GetTimestamp());
                    rig.Source.PushTick(seqBase + k);
                    Interlocked.Increment(ref pushed);
                }
            }) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "probe-feed" };

            pusher.Start();
            RenderProbe.Pump((int)(warmup * 1000));
            computeMs?.Clear();
            var proc = Process.GetCurrentProcess();
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long alloc0 = GC.GetTotalAllocatedBytes(false);
            var pause0 = GC.GetTotalPauseDuration();
            var cpu0 = proc.TotalProcessorTime;
            long pushed0 = Interlocked.Read(ref pushed);
            measureStartTicks = Stopwatch.GetTimestamp();
            measuring = true;
            int latCountAtStart = latencies.Count;
            bool sample = Sampler != null && label != "warmup";
            if (sample) Sampler!.Begin(allThreads: true);
            RenderProbe.Pump((int)(seconds * 1000));
            measuring = false;
            if (sample)
            {
                RenderProbe.Pump(300);
                SamplerResults.Add((label, Sampler!.End(), seconds));
            }
            var computeSnapshot = computeMs?.ToList(); // 只算测量窗口内完成的调用
            double elapsed = (Stopwatch.GetTimestamp() - measureStartTicks) / (double)Stopwatch.Frequency;
            long pushedDuring = Interlocked.Read(ref pushed) - pushed0;
            proc.Refresh();
            var cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds / elapsed / Environment.ProcessorCount * 100;
            long alloc = GC.GetTotalAllocatedBytes(false) - alloc0;
            double pauseMs = (GC.GetTotalPauseDuration() - pause0).TotalMilliseconds;
            double wsMB = proc.WorkingSet64 / (1024.0 * 1024);
            int d0 = GC.CollectionCount(0) - g0, d1 = GC.CollectionCount(1) - g1, d2 = GC.CollectionCount(2) - g2;

            stop.Set();
            pusher.Join();
            RenderProbe.Pump(300); // 让最后一批 tick 交付完,再摘钩子
            IncrementalRenderProbe.FrameRendered = null;

            return new FeedRun(label, rate, elapsed, pushedDuring, pushedDuring / elapsed, latencies.Count - latCountAtStart, frames,
                frames / elapsed, latencies, alloc / elapsed / (1024 * 1024), d0 / elapsed, d1 / elapsed, d2 / elapsed, cpu,
                computeSnapshot, 0, pauseMs / elapsed, wsMB,
                frames > 0 ? (double)featureSum / frames : 0, frames > 0 ? (double)layerSum / frames : 0);
        }

        // ── Feed:C# SMA,100 / 500 / 1000 tick/s ───────────────────────────────

        public static SuiteResult RunFeed(ProbeOptions opt)
        {
            ProbeFeedSource.SeedBars = opt.FeedBars;
            ProbeFeedSchema.SourceMap = opt.FeedSourceMap;
            var runs = new List<FeedRun>();
            using (var rig = CreateRig(opt))
            {
                Measure(rig, "warmup", 200, 1.0, 0.5); // JIT
                foreach (int rate in opt.FeedRates)
                {
                    var r = Measure(rig, $"C# SMA @{rate}/s", rate, opt.FeedSeconds, 1.0);
                    runs.Add(r);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"[feed] {rate}/s:交付 {r.Delivered} tick / {r.Frames} 帧,延迟中位 {Stats.Percentile(r.LatencyMs, 0.5):F1} ms"));
                }
            }
            string loh = Environment.GetEnvironmentVariable("DOTNET_GCLOHThreshold") is { Length: > 0 } t ? t : "默认(85000)";
            string map = ProbeFeedSchema.SourceMap ? "FastSourceMap(AutoMap 同路径)" : "ScatterIngestor";
            return Report("Feed", $"数据源链路(DataSource → pipe Ingestor → RequestUpdate → ComputeFeature SMA → 上屏),{opt.FeedBars} 根,摄入器 {map},LOH 阈值 {loh}", runs);
        }

        // ── PyFeed:N 个 Python 指标同时挂在 Close 上 ──────────────────────────

        public static SuiteResult RunPyFeed(ProbeOptions opt)
        {
            var py = ProbePython.Shared;
            if (!py.Available)
                return new SuiteResult("PyFeed", "\n### PyFeed\n\nPython 不可用(找不到 Python312/python312.dll),跳过。\n",
                    new { skipped = true }, "");

            File.WriteAllText(Path.Combine(py.IndicatorsDir, "probe_indicators.py"), PyIndicators);
            py.Registry.AutoDiscoverDirectory(py.IndicatorsDir);
            var rsi = py.Registry.TryGet("probe_rsi_14") as Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>
                      ?? throw new InvalidOperationException("probe_rsi_14 注册失败");
            var ema = py.Registry.TryGet("probe_ema_20") as Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>
                      ?? throw new InvalidOperationException("probe_ema_20 注册失败");

            var runs = new List<FeedRun>();
            foreach (int n in opt.PyIndicators)
            {
                var times = new ConcurrentQueue<double>();
                var list = Enumerable.Range(0, n).Select(k =>
                {
                    var inner = k % 2 == 0 ? rsi : ema;
                    string name = (k % 2 == 0 ? "RSI14#" : "EMA20#") + k;
                    Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>> timed = close =>
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        var r = inner(close);
                        times.Enqueue((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                        return r;
                    };
                    return (name, timed);
                }).ToList();

                using var rig = CreateRig(opt, list);
                Measure(rig, "warmup", opt.PyRate, 1.0, 0.5, times);
                var r = Measure(rig, $"{n} 个 Python 指标 @{opt.PyRate}/s", opt.PyRate, opt.FeedSeconds, 1.0, times) with { Indicators = n };
                runs.Add(r);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[pyfeed] {n} 指标:单次调用中位 {Stats.Percentile(r.ComputeMs ?? new List<double>(), 0.5):F2} ms,延迟中位 {Stats.Percentile(r.LatencyMs, 0.5):F1} ms"));
            }
            return Report("PyFeed", $"Python 指标(RSI14 / EMA20 交替,纯 Python 循环,{ProbeFeedSource.SeedBars}+ 根),多指标并发抢 GIL", runs);
        }

        private const string PyIndicators = """
            from hevo_indicators import register
            import numpy as np

            @register('probe_rsi_14', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
            def probe_rsi_14(close):
                arr = np.asarray(close, dtype=np.float64)
                n = arr.size
                out = np.full(n, np.nan, dtype=np.float64)
                if n < 15:
                    return out
                delta = np.diff(arr)
                gain = np.where(delta > 0, delta, 0.0)
                loss = np.where(delta < 0, -delta, 0.0)
                avg_g = gain[:14].mean()
                avg_l = loss[:14].mean()
                for i in range(14, n - 1):
                    avg_g = (avg_g * 13 + gain[i]) / 14.0
                    avg_l = (avg_l * 13 + loss[i]) / 14.0
                    rs = avg_g / (avg_l if avg_l > 0 else 1e-12)
                    out[i + 1] = 100.0 - 100.0 / (1.0 + rs)
                return out

            @register('probe_ema_20', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
            def probe_ema_20(close):
                arr = np.asarray(close, dtype=np.float64)
                out = np.empty_like(arr)
                alpha = 2.0 / 21.0
                prev = arr[0]
                for i in range(arr.size):
                    prev = arr[i] * alpha + prev * (1 - alpha)
                    out[i] = prev
                return out
            """;

        // ── Soak:长跑,每分钟采样 ────────────────────────────────────────────

        internal sealed record SoakSample(int Minute, double LiveHeapMB, double WorkingSetMB, int Gen2Total, int Frames, long Delivered, double LatP95Ms);

        public static SuiteResult RunSoak(ProbeOptions opt)
        {
            var samples = new List<SoakSample>();
            using (var rig = CreateRig(opt))
            {
                Measure(rig, "warmup", opt.SoakRate, 2.0, 0.5);
                var proc = Process.GetCurrentProcess();
                int gen2Start = GC.CollectionCount(2);
                for (int m = 1; m <= opt.SoakMinutes; m++)
                {
                    var r = Measure(rig, $"soak minute {m}", opt.SoakRate, 60, 0);
                    // 采样前强制完整 GC,"存活托管堆"才可比(这次 GC 计入下一分钟的 Gen2 计数,每分钟 +1 是采样本身造成的)
                    long live = GC.GetTotalMemory(forceFullCollection: true);
                    proc.Refresh();
                    var s = new SoakSample(m, live / (1024.0 * 1024), proc.WorkingSet64 / (1024.0 * 1024),
                        GC.CollectionCount(2) - gen2Start, r.Frames, r.Delivered, Stats.Percentile(r.LatencyMs, 0.95));
                    samples.Add(s);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"[soak] 第 {m}/{opt.SoakMinutes} 分钟:存活堆 {s.LiveHeapMB:F1} MB,工作集 {s.WorkingSetMB:F0} MB,Gen2 累计 {s.Gen2Total},延迟 P95 {s.LatP95Ms:F1} ms"));
                }
            }

            var md = new StringBuilder();
            md.AppendLine();
            md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"### Soak(长跑:{opt.SoakRate} tick/s × {opt.SoakMinutes} 分钟,每分钟强制 GC 后采样)"));
            md.AppendLine();
            md.AppendLine("| 分钟 | 存活托管堆(MB) | 工作集(MB) | Gen2 累计 | 帧数 | 交付 tick | 延迟 P95(ms) |");
            md.AppendLine("|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var s in samples)
                md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {s.Minute} | {s.LiveHeapMB:F2} | {s.WorkingSetMB:F0} | {s.Gen2Total} | {s.Frames} | {s.Delivered} | {s.LatP95Ms:F1} |"));
            if (samples.Count >= 3)
            {
                // 去掉第 1 分钟(缓存 / 池子还在长),对剩余点做最小二乘斜率
                var pts = samples.Skip(1).ToList();
                double heapSlope = Slope(pts.Select(p => (double)p.Minute).ToList(), pts.Select(p => p.LiveHeapMB).ToList());
                double wsSlope = Slope(pts.Select(p => (double)p.Minute).ToList(), pts.Select(p => p.WorkingSetMB).ToList());
                bool stable = Math.Abs(heapSlope) < 0.5 && Math.Abs(wsSlope) < 2.0;
                md.AppendLine();
                md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"斜率(第 2 分钟起,最小二乘):存活堆 {heapSlope:+0.00;-0.00} MB/分钟,工作集 {wsSlope:+0.0;-0.0} MB/分钟 → ") +
                    (stable ? "✅ 平稳(存活堆 < 0.5 MB/分钟 且工作集 < 2 MB/分钟)" : "⚠ 有增长趋势,需要排查"));
            }
            var csv = new StringBuilder("minute,live_heap_mb,working_set_mb,gen2_total,frames,delivered,lat_p95_ms\n");
            foreach (var s in samples)
                csv.Append(string.Create(CultureInfo.InvariantCulture,
                    $"{s.Minute},{s.LiveHeapMB:F3},{s.WorkingSetMB:F1},{s.Gen2Total},{s.Frames},{s.Delivered},{s.LatP95Ms:F2}\n"));
            return new SuiteResult("Soak", md.ToString(), new { rate = opt.SoakRate, minutes = opt.SoakMinutes, samples }, csv.ToString());
        }

        private static double Slope(List<double> x, List<double> y)
        {
            double mx = x.Average(), my = y.Average();
            double num = 0, den = 0;
            for (int i = 0; i < x.Count; i++) { num += (x[i] - mx) * (y[i] - my); den += (x[i] - mx) * (x[i] - mx); }
            return den == 0 ? 0 : num / den;
        }

        // ── 报告 ─────────────────────────────────────────────────────────────

        private static SuiteResult Report(string name, string title, List<FeedRun> runs)
        {
            var md = new StringBuilder();
            md.AppendLine();
            md.AppendLine($"### {name}:{title}");
            md.AppendLine();
            md.AppendLine("真窗口 + CompositionTarget 渲染节奏(不手动跑帧);延迟 = tick 推入 → 含该 tick 的那一帧 UI 线程做完,不含渲染线程合成。");
            md.AppendLine();
            md.AppendLine("| 负载 | 实际推送(tick/s) | 交付 tick | 帧/s | tick/帧 | Feature重算/帧 | 图层重录/帧 | 延迟中位(ms) | P95 | P99 | 最大 | 分配(MB/s) | GC 0/1/2 每秒 | GC 暂停(ms/s) | 工作集(MB) | CPU(%,全核) | 指标单次调用 中位/P95(ms) | 指标完成次数 / (tick×指标数) |");
            md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var r in runs)
            {
                string compute = r.ComputeMs is { Count: > 0 } c
                    ? string.Create(CultureInfo.InvariantCulture, $"{Stats.Percentile(c, 0.5):F2} / {Stats.Percentile(c, 0.95):F2}")
                    : "-";
                // 指标跟不上时 WatchAsync 会合并(只算最新一份),完成次数 < tick×指标数;比值 = 实际算了几成
                string keepUp = r.ComputeMs is { Count: > 0 } c2 && r.Indicators > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{(double)c2.Count / (r.Pushed * r.Indicators):P0}")
                    : "-";
                md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {r.Label} | {r.PushRate:F0} | {r.Delivered} | {r.FrameRate:F1} | {(r.Frames > 0 ? (double)r.Delivered / r.Frames : 0):F1} | {r.FeaturesPerFrame:F2} | {r.LayersPerFrame:F2} | " +
                    $"{Stats.Percentile(r.LatencyMs, 0.5):F1} | {Stats.Percentile(r.LatencyMs, 0.95):F1} | {Stats.Percentile(r.LatencyMs, 0.99):F1} | " +
                    $"{(r.LatencyMs.Count > 0 ? r.LatencyMs.Max() : double.NaN):F1} | {r.AllocMBps:F1} | {r.Gen0ps:F2}/{r.Gen1ps:F2}/{r.Gen2ps:F2} | {r.GcPauseMsPerSec:F2} | {r.WorkingSetMB:F0} | {r.CpuPct:F1} | {compute} | {keepUp} |"));
            }
            var csv = new StringBuilder("suite,label,target_rate,latency_ms\n");
            foreach (var r in runs)
                foreach (var l in r.LatencyMs)
                    csv.Append(string.Create(CultureInfo.InvariantCulture, $"{name},{r.Label},{r.TargetRate},{l:F3}\n"));
            var json = runs.Select(r => new
            {
                r.Label, r.TargetRate, seconds = Math.Round(r.Seconds, 2), pushRate = Math.Round(r.PushRate, 1), r.Delivered, r.Frames,
                frameRate = Math.Round(r.FrameRate, 2),
                featuresPerFrame = Math.Round(r.FeaturesPerFrame, 3), layersPerFrame = Math.Round(r.LayersPerFrame, 3),
                latencyMs = new
                {
                    p50 = Math.Round(Stats.Percentile(r.LatencyMs, 0.5), 3), p95 = Math.Round(Stats.Percentile(r.LatencyMs, 0.95), 3),
                    p99 = Math.Round(Stats.Percentile(r.LatencyMs, 0.99), 3),
                },
                allocMBps = Math.Round(r.AllocMBps, 2), gcPerSec = new[] { r.Gen0ps, r.Gen1ps, r.Gen2ps }, cpuPct = Math.Round(r.CpuPct, 1),
                gcPauseMsPerSec = Math.Round(r.GcPauseMsPerSec, 3), workingSetMB = Math.Round(r.WorkingSetMB, 1),
                computeMs = r.ComputeMs is { Count: > 0 } cm
                    ? new { p50 = Math.Round(Stats.Percentile(cm, 0.5), 3), p95 = Math.Round(Stats.Percentile(cm, 0.95), 3) }
                    : null,
            }).ToList();
            return new SuiteResult(name, md.ToString(), json, csv.ToString());
        }
    }
}
