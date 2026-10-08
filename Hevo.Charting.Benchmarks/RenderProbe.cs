using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.DevTools;
using Hevo.Charting.Features;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using Hevo.Charting.Renderers;

namespace Hevo.Charting.Benchmarks
{
    /// <summary>
    /// 增量渲染前后对比测量(不是 BenchmarkDotNet,需要真窗口 + WPF 渲染管线)。
    /// <para>
    /// 用法(Windows,Release):
    /// <c>dotnet run -c Release --project Hevo.Charting.Benchmarks -- --render-probe [--bars=2000] [--steps=600] [--out=render-probe.csv]</c>
    /// </para>
    /// <para>
    /// 做法:开一个 1280x720 窗口,里面是一张手写 K 线图(蜡烛 + SMA + 双轴 + 联动头 + 十字光标交互,
    /// 跟 LowCodeDemo 的 KLineMainSchema 同款装配)。每个场景按固定脚本逐步改黑板,每步手动跑一帧
    /// (ChartCell.ExecutePipeline + Invalidate,跟 CompositionTarget 回调里那一帧是同一段代码),
    /// 不依赖 VSync 节奏,结果可复现。
    /// </para>
    /// <para>
    /// 场景:Hover(十字光标横扫,写 PointerHitPort)/ Pan(每步平移 1 根,写 Viewport.UserRange)/
    /// Tick(行情心跳,改最后一根 K 线)。
    /// 对照组:用 <see cref="IncrementalRenderProbe"/> 人为关掉某一层增量机制,同一脚本再跑一遍。
    /// </para>
    /// </summary>
    internal static class RenderProbe
    {
        private sealed record Mode(string Name, bool FullPass, bool BypassShortCircuit, bool LayerRedraw);

        private static readonly Mode[] Modes =
        {
            new("增量(默认)",       false, false, false),
            new("去掉Bag短路",       false, true,  false),
            new("图层侧全重绘",      false, false, true),
            new("Feature侧全量",     true,  false, false),
            new("全量(两侧都关)",   true,  false, true),
        };

        private sealed record Sample(double PipelineMs, double InputMs, long Features, int Layers, long DrawCmds, long AllocBytes);

        private sealed record Result(string Scenario, string Mode, List<Sample> Samples,
                                     long ShortCircuitMisses, Dictionary<string, long> MissDetail);

        public static int Run(string[] args)
        {
            int bars = int.Parse(ExtractArg(args, "--bars=") ?? "2000", CultureInfo.InvariantCulture);
            int steps = int.Parse(ExtractArg(args, "--steps=") ?? "600", CultureInfo.InvariantCulture);
            string outPath = ExtractArg(args, "--out=") ?? "render-probe.csv";

#if DEBUG
            Console.WriteLine("⚠ 当前是 Debug 构建:DEBUG 下有拓扑追踪和锁校验开销,数字偏大。正式取数请用 -c Release。");
#endif
            int exit = 0;
            var thread = new Thread(() =>
            {
                try { exit = RunOnUiThread(bars, steps, outPath); }
                catch (Exception ex) { Console.Error.WriteLine(ex); exit = 99; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return exit;
        }

        private static int RunOnUiThread(int bars, int steps, string outPath)
        {
            var schema = new ProbeKLineSchema();
            var cell = new ChartCell { Template = schema };
            var window = new Window
            {
                Title = "Hevo render probe",
                Width = 1280,
                Height = 720,
                Content = cell,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            window.Show();
            Pump(300); // 等 OnApplyTemplate → ComposeAll + SizeChanged

            var data = ProbeData.Generate(bars);
            var board = new DataBlackboard();
            using (board.BeginTransaction())
            {
                board.WriteIfChanged(schema.Ports.Time, (ReadOnlyMemory<DateTime>)data.Time);
                board.WriteIfChanged(schema.Ports.Open, (ReadOnlyMemory<double>)data.Open);
                board.WriteIfChanged(schema.Ports.High, (ReadOnlyMemory<double>)data.High);
                board.WriteIfChanged(schema.Ports.Low, (ReadOnlyMemory<double>)data.Low);
                board.WriteIfChanged(schema.Ports.Close, (ReadOnlyMemory<double>)data.Close);
                board.WriteIfChanged(schema.SmaPort, (ReadOnlyMemory<double>)data.Sma);
                board.WriteIfChanged(schema.Viewport.LogicalLength, bars);
            }
            schema.Trigger.Push(board);
            schema.InvalidateEnvironment();
            Pump(500);

            using (var ctx = cell.CreateContext())
            {
                if (ctx.GetPlotArea().IsEmpty || cell.ActiveLayers.Count == 0)
                {
                    Console.Error.WriteLine("[render-probe] 图表没有完成装配(PlotArea 为空或没有图层),放弃测量。");
                    return 99;
                }
            }

            var scenarios = new (string Name, Action<int> Step)[]
            {
                ("Hover", i => HoverStep(cell, schema, board, bars, i)),
                ("Pan",   i => PanStep(schema, board, i)),
                ("Tick",  i => TickStep(schema, board, data, i)),
            };

            Console.WriteLine($"[render-probe] bars={bars} steps={steps} layers={cell.ActiveLayers.Count} " +
                              $"features={schema.ListFeatures().Count}");

            var results = new List<Result>();
            foreach (var (name, step) in scenarios)
            {
                foreach (var mode in Modes)
                {
                    Apply(mode);
                    try
                    {
                        // 预热(JIT + 缓存),再归位到同一起点
                        for (int i = 0; i < Math.Min(100, steps); i++) RunStep(cell, step, i);
                        ResetView(schema, board, bars);
                        RunFrame(cell);
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();

                        IncrementalRenderProbe.ShortCircuitMisses = 0; // 只统计测量段,不含预热
                        IncrementalRenderProbe.ShortCircuitMissDetail.Clear();

                        var samples = new List<Sample>(steps);
                        for (int i = 0; i < steps; i++) samples.Add(RunStep(cell, step, i));
                        results.Add(new Result(name, mode.Name, samples, IncrementalRenderProbe.ShortCircuitMisses,
                            new Dictionary<string, long>(IncrementalRenderProbe.ShortCircuitMissDetail)));
                    }
                    finally { IncrementalRenderProbe.Reset(); }

                    ResetView(schema, board, bars);
                    RunFrame(cell);
                    Pump(50); // 清掉测量期间排进 RequestUpdate 队列的空回调
                }
            }

            window.Close();
            Report(results, outPath);
            return 0;
        }

        private static void Apply(Mode m)
        {
            IncrementalRenderProbe.Reset();
            IncrementalRenderProbe.ForceFullPass = m.FullPass;
            IncrementalRenderProbe.BypassBagShortCircuit = m.BypassShortCircuit;
            IncrementalRenderProbe.ForceLayerRedraw = m.LayerRedraw;
        }

        private static Sample RunStep(ChartCell cell, Action<int> step, int i)
        {
            long t0 = Stopwatch.GetTimestamp();
            step(i); // 写黑板(含 Watch 副作用:视口钳位、自动量程等)
            long t1 = Stopwatch.GetTimestamp();
            long alloc0 = GC.GetAllocatedBytesForCurrentThread(); // 只统计帧本身的分配,不含脚本输入

            long features0 = IncrementalRenderProbe.FeatureProjections;
            int layers = RunFrame(cell);
            long t2 = Stopwatch.GetTimestamp();
            long alloc1 = GC.GetAllocatedBytesForCurrentThread();

            return new Sample(
                PipelineMs: (t2 - t1) * 1000.0 / Stopwatch.Frequency,
                InputMs: (t1 - t0) * 1000.0 / Stopwatch.Frequency,
                Features: IncrementalRenderProbe.FeatureProjections - features0,
                Layers: layers,
                DrawCmds: cell.GetDiagnostics().LastFrameDrawCmds,
                AllocBytes: alloc1 - alloc0);
        }

        // 一帧 = OnCompositionTargetRendering 里的 ExecutePipeline + Invalidate。返回本帧重录的图层数。
        private static int RunFrame(ChartCell cell)
        {
            using var ctx = cell.CreateContext();
            cell.ExecutePipeline(ctx, PlotMode.Sync);
            int dirty = 0;
            var layers = cell.ActiveLayers;
            for (int i = 0; i < layers.Count; i++)
                if (layers[i] is ChartLayer cl && cl.IsDirty) dirty++;
            cell.Invalidate();
            return dirty;
        }

        // ── 场景脚本 ───────────────────────────────────────────────────────────

        // 十字光标在绘图区内来回横扫,每步 3px;Y 同步小幅摆动(真实鼠标移动 Y 也会变)。
        // 命中计算复刻 ChartInteractionFeature.UpdatePointerStateFromMouse(吸附到可见 K 线中心)。
        private static void HoverStep(ChartCell cell, ProbeKLineSchema schema, DataBlackboard board, int bars, int i)
        {
            HevoRect plot;
            ScaleStrategyTrait? scale;
            using (var ctx = cell.CreateContext())
            {
                plot = ctx.GetPlotArea();
                scale = ctx.Shared().Read<ScaleStrategyTrait>();
            }
            if (scale == null || plot.IsEmpty) return;

            int span = Math.Max(1, (int)(plot.Width / 3));
            int k = i % (2 * span);
            double x = plot.Left + 3.0 * (k < span ? k : 2 * span - k);
            float y = plot.Y + plot.Height * (0.3f + 0.4f * (i % 50) / 50f);

            using (board.AcquireUpgradeableReadLock())
            {
                var active = board.Read(schema.Viewport.ActiveRange);
                if (!active.IsValid) return;
                var ds = scale.DomainScale;
                double rel = Math.Clamp((x - plot.Left) / plot.Width, 0.0, 1.0);
                int idx = (int)Math.Round(ds.Denormalize(rel, active));
                int lo = Math.Max(0, (int)Math.Ceiling(ds.Denormalize(0.0, active)));
                int hi = Math.Min(bars - 1, (int)Math.Floor(ds.Denormalize(1.0, active)));
                if (hi < lo) { lo = 0; hi = bars - 1; }
                idx = Math.Clamp(idx, lo, hi);
                double cRel = ds.Normalize(idx, active);
                double cx = plot.Left + plot.Width * cRel;

                using (board.AcquireWriteLock())
                    board.WriteIfChanged(schema.HitPortForProbe, new PointerHitState(
                        new HevoPoint((float)cx, y), new DomainHitResult(idx, cx, cRel), cx, idx, false));
            }
        }

        // 每步平移 1 根 K 线,每 100 步换方向,始终停留在数据范围内。
        private static void PanStep(ProbeKLineSchema schema, DataBlackboard board, int i)
        {
            double delta = (i / 100) % 2 == 0 ? -1 : 1;
            using (board.AcquireUpgradeableReadLock())
            {
                var active = board.Read(schema.Viewport.ActiveRange);
                if (!active.IsValid) return;
                using (board.AcquireWriteLock())
                    board.WriteIfChanged(schema.Viewport.UserRange, new RealRange(active.Min + delta, active.Max + delta));
            }
        }

        // 行情心跳:最后一根 K 线收盘价跳动。数组原地改 + ForceWrite(ROM 同底层数组同长度,WriteIfChanged 判不出变化,
        // 真实数据管线也是这样推送的)。
        private static void TickStep(ProbeKLineSchema schema, DataBlackboard board, ProbeData data, int i)
        {
            int last = data.Close.Length - 1;
            double c = data.Open[last] + Math.Sin(i * 0.37) * 2.0;
            data.Close[last] = c;
            data.High[last] = Math.Max(data.High[last], c);
            data.Low[last] = Math.Min(data.Low[last], c);
            using (board.BeginTransaction())
            {
                board.ForceWrite(schema.Ports.High, (ReadOnlyMemory<double>)data.High);
                board.ForceWrite(schema.Ports.Low, (ReadOnlyMemory<double>)data.Low);
                board.ForceWrite(schema.Ports.Close, (ReadOnlyMemory<double>)data.Close);
            }
        }

        private static void ResetView(ProbeKLineSchema schema, DataBlackboard board, int bars)
        {
            using (board.AcquireWriteLock())
            {
                board.WriteIfChanged(schema.HitPortForProbe, null);
                board.WriteIfChanged(schema.Viewport.UserRange, new RealRange(bars - 120, bars - 1));
            }
        }

        // ── 报告 ─────────────────────────────────────────────────────────────

        private static void Report(List<Result> results, string outPath)
        {
            var md = new StringBuilder();
            md.AppendLine();
            md.AppendLine("| 场景 | 模式 | 帧耗时中位(ms) | 帧耗时P95(ms) | 输入耗时中位(ms) | Feature重算/帧 | 图层重录/帧 | 绘制命令/帧 | 分配/帧(B) |");
            md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
            var csv = new StringBuilder("scenario,mode,step,pipeline_ms,input_ms,features,layers,draw_cmds,alloc_bytes\n");

            foreach (var r in results)
            {
                var s = r.Samples;
                md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {r.Scenario} | {r.Mode} | {Pct(s.Select(x => x.PipelineMs), 0.5):F3} | {Pct(s.Select(x => x.PipelineMs), 0.95):F3} | " +
                    $"{Pct(s.Select(x => x.InputMs), 0.5):F3} | {s.Average(x => x.Features):F1} | {s.Average(x => x.Layers):F1} | " +
                    $"{s.Average(x => x.DrawCmds):F0} | {s.Average(x => x.AllocBytes):F0} |"));
                for (int i = 0; i < s.Count; i++)
                {
                    var x = s[i];
                    csv.Append(string.Create(CultureInfo.InvariantCulture,
                        $"{r.Scenario},{r.Mode},{i},{x.PipelineMs:F4},{x.InputMs:F4},{x.Features},{x.Layers},{x.DrawCmds},{x.AllocBytes}\n"));
                }
            }

            // 去掉短路的对照组里,引用比对判脏、而短路本会跳过的图层:不为空说明两者结论不一致,需要看是谁
            foreach (var r in results.Where(r => r.ShortCircuitMisses > 0))
            {
                md.AppendLine();
                md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"[render-probe] {r.Scenario}/{r.Mode}: 短路本会跳过、引用比对却判脏 {r.ShortCircuitMisses} 次(共 {r.Samples.Count} 帧)"));
                foreach (var kv in r.MissDetail.OrderByDescending(kv => kv.Value))
                    md.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {kv.Value,6}  {kv.Key}"));
            }

            Console.WriteLine(md.ToString());
            File.WriteAllText(outPath, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Console.WriteLine($"[render-probe] 逐帧明细已写入 {Path.GetFullPath(outPath)}");
        }

        private static double Pct(IEnumerable<double> values, double p)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            if (sorted.Length == 0) return double.NaN;
            int idx = (int)Math.Ceiling(p * sorted.Length) - 1;
            return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
        }

        private static void Pump(int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        private static string? ExtractArg(string[] args, string prefix)
        {
            foreach (var a in args)
                if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length);
            return null;
        }
    }

    /// <summary>确定性随机游走 K 线(固定种子),外加预算好的 SMA(20)。</summary>
    internal sealed class ProbeData
    {
        public DateTime[] Time = Array.Empty<DateTime>();
        public double[] Open = Array.Empty<double>();
        public double[] High = Array.Empty<double>();
        public double[] Low = Array.Empty<double>();
        public double[] Close = Array.Empty<double>();
        public double[] Sma = Array.Empty<double>();

        public static ProbeData Generate(int n)
        {
            var rng = new Random(20261008);
            var d = new ProbeData
            {
                Time = new DateTime[n], Open = new double[n], High = new double[n],
                Low = new double[n], Close = new double[n], Sma = new double[n],
            };
            var t0 = new DateTime(2026, 1, 5, 9, 30, 0);
            double last = 100;
            for (int i = 0; i < n; i++)
            {
                double o = last;
                double c = o + (rng.NextDouble() - 0.5) * 2.0;
                d.Time[i] = t0.AddMinutes(i);
                d.Open[i] = o;
                d.Close[i] = c;
                d.High[i] = Math.Max(o, c) + rng.NextDouble();
                d.Low[i] = Math.Min(o, c) - rng.NextDouble();
                last = c;
            }
            const int len = 20;
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                sum += d.Close[i];
                if (i >= len) sum -= d.Close[i - len];
                d.Sma[i] = i >= len - 1 ? sum / len : double.NaN;
            }
            return d;
        }
    }

    /// <summary>
    /// 测量用 K 线 schema:装配跟 LowCodeDemo 的 KLineMainSchema 一致,
    /// 区别只在数据源 —— 主数据流是手动 Push 的 WorkflowTrigger,黑板由 RenderProbe 直接写。
    /// </summary>
    internal sealed class ProbeKLineSchema : ChartReactiveSchema
    {
        public CandlePorts Ports { get; } = new(new("P_Time"), new("P_Open"), new("P_High"), new("P_Low"), new("P_Close"));
        public DataPort<ReadOnlyMemory<double>> SmaPort { get; } = new("P_SMA20");
        public DataPort<RealRange> YRangePort { get; } = new("P_YRange");
        public WorkflowTrigger<DataBlackboard> Trigger { get; } = new();

        public ViewportPorts Viewport { get; private set; } = null!;
        public DataPort<PointerHitState?> HitPortForProbe => HitPort;

        protected override void DefineDataFlow(ChartCell chart)
        {
            Viewport = ViewportPorts.RequireAttached(Chart);
            Trigger.BindTo(chart);
        }

        protected override void DefineFeatures(IFeatureContext canvas)
        {
            canvas.Seed<ScaleStrategyTrait>(ScaleStrategyTrait.CandleMode);

            var hitPort = HitPort;
            var timeMeta = FieldMeta.Literal("时间", Colors.White, "yyyy-MM-dd HH:mm");
            var priceMeta = FieldMeta.Literal("价", Colors.LightGray, "F2");
            var smaMeta = FieldMeta.Literal("SMA20", Color.FromRgb(0xFF, 0xB7, 0x4D), "F2");
            var candleMetas = new CandleMetas(
                Open: FieldMeta.Literal("开", Colors.Gray, "F2"),
                High: FieldMeta.Literal("高", Colors.Gray, "F2"),
                Low: FieldMeta.Literal("低", Colors.Gray, "F2"),
                Close: FieldMeta.Literal("收", Colors.Gray, "F2"));

            canvas
                .Environment(env => env
                    .SetupLayout(
                        left: ChartLength.Pixel(60),
                        top: ChartLength.Pixel(28),
                        right: ChartLength.Pixel(0),
                        bottom: ChartLength.Pixel(24))
                    .SetupViewport(
                        minVisibleCount: 10,
                        alignment: ViewportAlignment.RightEdge,
                        defaultVisibleCount: 120,
                        overscrollMin: OverscrollPolicy.Hard,
                        overscrollMax: OverscrollPolicy.Hard)
                    .SetupAutoScale(
                        yRangePort: YRangePort,
                        valuePorts: new[] { Ports.High, Ports.Low, SmaPort },
                        paddingRatio: 0.05)
                    .SetupUniversalHeader(hitPort: hitPort))
                .Axes(axes => axes
                    .AddDomainAxis(Ports.Time, ViewportPorts.RequireAttached(Chart), timeMeta)
                    .AddRangeAxis(YRangePort, priceMeta, AxisPlacement.Right))
                .Series(series => series
                    .AddCandle(rangePort: YRangePort, ports: Ports, groupName: "MainCandle", metas: candleMetas)
                    .AddLine(dataPort: SmaPort, rangePort: YRangePort, meta: smaMeta, thickness: 1.5))
                .Interactions(i => i.EnableStandard(
                    domainDataPort: Ports.Time,
                    domainMeta: timeMeta,
                    options: new InteractionOptions<DateTime>
                    {
                        HitPort = hitPort,
                        Modes = ChartInteractionMode.All,
                        TooltipXMeta = timeMeta,
                    }));
        }
    }
}
