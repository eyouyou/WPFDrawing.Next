using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
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
    /// 用法(Windows,Release),参数见 <see cref="ProbeOptions"/> 和 README:
    /// <c>dotnet run -c Release --project Hevo.Charting.Benchmarks -- --render-probe [--bars=2000,100000] [--charts=1,4] [--rounds=5]</c>
    /// </para>
    /// <para>
    /// 做法:开一个窗口,里面是 1 张或 N 张手写 K 线图(蜡烛 + SMA + 双轴 + 联动头 + 十字光标交互,
    /// 跟 LowCodeDemo 的 KLineMainSchema 同款装配)。每个场景按固定脚本逐步改黑板,每步手动跑一帧
    /// (<see cref="ChartCell.RunFrameNow"/>,跟 CompositionTarget 回调里那一帧是同一段代码),
    /// 不依赖 VSync 节奏,结果可复现。这里只测 UI 线程的 CPU 管线;合成上屏和输入延迟见 <see cref="LatencyProbe"/>。
    /// </para>
    /// <para>
    /// 场景:Hover(十字光标横扫)/ Pan(平移)/ Zoom(滚轮缩放,可见根数在 60 和上限之间来回)/
    /// Tick(行情心跳,改最后一根)/ Append(实时追加新 K 线并跟随)/ Resize(窗口宽度逐步变化,走 SizeChanged 全量)。
    /// 另有 Startup:新建窗口 → 装配 → 首帧出图的耗时,单独成表。
    /// 对照组:用 <see cref="IncrementalRenderProbe"/> 人为关掉某一层增量机制,同一脚本再跑一遍;
    /// 另有两组用 <see cref="PlotMode.Parallel"/> 跑图层录制。
    /// </para>
    /// <para>
    /// 统计:每个组合跑 <c>--rounds</c> 轮,各轮轮流穿插(轮 1 的所有组合 → 轮 2 ...),漂移对各组合影响一致;
    /// 帧耗时报"各轮中位数的均值 ± 95% 置信区间",同时报全部样本的 P95/P99、每千帧 GC 次数。
    /// 计数类指标(Feature 重算/帧、图层重录/帧、绘制命令/帧)是确定的,可用 <c>--baseline</c> 做回归门槛。
    /// </para>
    /// </summary>
    internal static class RenderProbe
    {
        internal sealed record Mode(string Key, string Name, bool FullPass, bool BypassShortCircuit, bool LayerRedraw, PlotMode Plot);

        internal static readonly Mode[] AllModes =
        {
            new("inc",       "增量(默认)",        false, false, false, PlotMode.Sync),
            new("nobag",     "去掉Bag短路",        false, true,  false, PlotMode.Sync),
            new("layerfull", "图层侧全重绘",       false, false, true,  PlotMode.Sync),
            new("featfull",  "Feature侧全量",      true,  false, false, PlotMode.Sync),
            new("full",      "全量(两侧都关)",    true,  false, true,  PlotMode.Sync),
            new("inc-par",   "增量+Parallel",      false, false, false, PlotMode.Parallel),
            new("full-par",  "全量+Parallel",      true,  false, true,  PlotMode.Parallel),
        };

        /// <summary>默认跑的逐帧场景(不带 --scenarios 时)。</summary>
        internal static readonly string[] AllScenarios = { "Hover", "Pan", "Zoom", "Tick", "Append", "Resize" };

        /// <summary>
        /// 需要另一种测量窗口的逐帧场景(显式 --scenarios 或 all 才跑):
        /// HoverNoTip = 摘掉 Tooltip 的 Hover(跟 Hover 对照);Dash* = 联动 dashboard(主图 + 标记 + 成交量副图);
        /// Zoom@150 / Zoom@200 = 模拟高 DPI 下的 Zoom(LOD 按物理像素的分支)。
        /// </summary>
        internal static readonly string[] ExtraScenarios = { "HoverNoTip", "DashHover", "DashPan", "DashTick", "Zoom@150", "Zoom@200" };

        internal static readonly string[] AllFrameScenarios = AllScenarios.Concat(ExtraScenarios).ToArray();

        /// <summary>
        /// 不是逐帧脚本的测量套件(显式 --scenarios 才跑;all 包含除 Soak 以外的全部,Soak 默认 10 分钟只能显式点名):
        /// Feed = 后台线程按 --feed-rates 推 tick 走真实数据源链路;PyFeed = 再挂 N 个 Python 指标;
        /// Blueprint = demo dashboard 蓝图端到端冷 / 热启动;Soak = 长跑采样内存。
        /// </summary>
        internal static readonly string[] Suites = { "Feed", "PyFeed", "Blueprint", "Soak" };

        internal static RigKind RigKindOf(string scenario) => scenario switch
        {
            "HoverNoTip" => RigKind.NoTooltip,
            "DashHover" or "DashPan" or "DashTick" => RigKind.Dashboard,
            "Zoom@150" => RigKind.Dpi150,
            "Zoom@200" => RigKind.Dpi200,
            _ => RigKind.Standard,
        };

        private readonly record struct Sample(double PipelineMs, double InputMs, long Features, int Layers, long DrawCmds, long AllocBytes, bool TooltipShown);

        private sealed class Combo
        {
            public required int Bars;
            public required int Charts;
            public required string Scenario;
            public required Mode Mode;
            public readonly List<List<Sample>> Rounds = new();
            public readonly List<(int Gen0, int Gen1, int Gen2)> RoundGc = new();
            public long ShortCircuitMisses;
            public readonly Dictionary<string, long> MissDetail = new();
            public string? Error;
            // --alloc-types:各轮 GCAllocationTick 采样按 (类型, LOH) 累加
            public readonly Dictionary<(string Type, bool Large), (long Ticks, long Bytes, long MaxObj)> AllocTypes = new();
            public int AllocFrames;
            public string Key => $"bars={Bars}|charts={Charts}|{Scenario}|{Mode.Key}";
        }

        private sealed record StartupResult(int Bars, List<double> ComposeMs, List<double> FirstFrameMs, List<long> AllocBytes, int Layers);

        public static int Run(string[] args)
        {
            var opt = ProbeOptions.Parse(args);
            if (opt.AllocTypes) s_allocSampler = new AllocTypeSampler();
            int exit = 0;
            var thread = new Thread(() =>
            {
                try { exit = RunOnUiThread(opt); }
                catch (Exception ex) { Console.Error.WriteLine(ex); exit = 99; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return exit;
        }

        private static int RunOnUiThread(ProbeOptions opt)
        {
            ProbeEnvironment.Standardize();
            var env = ProbeEnvironment.Capture();
            Console.WriteLine(env.Describe());
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[render-probe] bars={string.Join(',', opt.Bars)} charts={string.Join(',', opt.Charts)} steps={opt.Steps} " +
                $"warmup={opt.Warmup} rounds={opt.Rounds} size={opt.WindowWidth}x{opt.WindowHeight} " +
                $"scenarios={string.Join(',', opt.Scenarios)} modes={string.Join(',', opt.Modes.Select(m => m.Key))}"));

            ProbeKLineSchema.HeavyLayers = opt.HeavyLayers;
            ProbeKLineSchema.HeavyMs = opt.HeavyMs;
            if (opt.HeavyLayers > 0)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[render-probe] 每张图额外挂 {opt.HeavyLayers} 个加重图层,每层录制忙等 {opt.HeavyMs} ms"));
            if (opt.Modes.Any(m => m.Plot == PlotMode.Parallel))
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[env] 逻辑核 {Environment.ProcessorCount};Parallel.ForEach 空任务调度开销中位数:3 项 {ParallelOverhead.MedianMicros(3):F1} μs / 10 项 {ParallelOverhead.MedianMicros(10):F1} μs / 30 项 {ParallelOverhead.MedianMicros(30):F1} μs"));

            var combos = new List<Combo>();
            var startups = new List<StartupResult>();

            foreach (int bars in opt.Bars)
            {
                if (opt.Startup) startups.Add(MeasureStartup(opt, bars));

                foreach (int charts in opt.Charts)
                foreach (var kindGroup in opt.Scenarios.GroupBy(RigKindOf))
                {
                    // dashboard 自带两张图(主图 + 副图),只在 charts=1 那一组跑
                    if (kindGroup.Key == RigKind.Dashboard && charts != 1) continue;
                    using var rig = ProbeRig.Create(opt, bars, charts, extraCapacity: Math.Max(opt.Steps, opt.Warmup) + 1, kindGroup.Key);
                    if (rig == null) return 99;
                    Console.WriteLine($"[render-probe] bars={bars} charts={charts} 窗口={kindGroup.Key} layers/图={rig.Charts[0].Cell.ActiveLayers.Count} " +
                                      $"features/图={rig.Charts[0].Schema.ListFeatures().Count}" +
                                      (rig.Panes.Count > 0 ? $" 副图 layers={rig.Panes[0].Cell.ActiveLayers.Count}" : ""));

                    var local = new List<Combo>();
                    foreach (var sc in kindGroup)
                        foreach (var m in opt.Modes)
                            local.Add(new Combo { Bars = bars, Charts = charts, Scenario = sc, Mode = m });

                    // 轮 0 之前:每个组合都先跑一遍预热(JIT 分层编译 + 缓存),再歇一下让后台 Tier1 编译落地
                    foreach (var c in local) RunSegment(rig, c, opt.Warmup, record: false);
                    Pump(300);

                    for (int round = 0; round < opt.Rounds; round++)
                    {
                        foreach (var c in local)
                        {
                            if (c.Error != null) continue;
                            RunSegment(rig, c, opt.Steps, record: true);
                        }
                        Console.WriteLine($"[render-probe] bars={bars} charts={charts} 第 {round + 1}/{opt.Rounds} 轮完成");
                    }
                    combos.AddRange(local);
                }
            }

            var suites = new List<SuiteResult>();
            if (s_allocSampler != null) FeedProbe.Sampler = s_allocSampler;
            foreach (var name in opt.SuiteNames)
            {
                Console.WriteLine($"[render-probe] 套件 {name} 开始");
                suites.Add(name switch
                {
                    "Feed" => FeedProbe.RunFeed(opt),
                    "PyFeed" => FeedProbe.RunPyFeed(opt),
                    "Blueprint" => BlueprintProbe.RunBlueprint(opt),
                    "Soak" => FeedProbe.RunSoak(opt),
                    _ => throw new ArgumentException(name),
                });
            }

            var report = BuildReport(env, opt, combos, startups);
            if (s_allocSampler != null) report = report with { Markdown = report.Markdown + AllocTypesMarkdown(combos) };
            if (FeedProbe.SamplerResults.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine().AppendLine("### 分配类型采样(套件,全进程所有线程;\"每帧\"列按每秒计)").AppendLine();
                foreach (var (label, rows, seconds) in FeedProbe.SamplerResults)
                    sb.AppendLine(AllocTypeSampler.Format(label, rows, (int)Math.Round(seconds)));
                report = report with { Markdown = report.Markdown + sb };
            }
            if (suites.Count > 0)
            {
                report = report with { Markdown = report.Markdown + string.Concat(suites.Select(x => x.Markdown)) };
                // JSON:在主报告对象末尾追加 suites 字段(主报告是缩进 JSON 对象,去掉最后的 } 再拼)
                var suitesJson = JsonSerializer.Serialize(suites.ToDictionary(x => x.Name, x => x.Json),
                    new JsonSerializerOptions
                    {
                        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
                    });
                int end = report.Json.LastIndexOf('}');
                report = report with { Json = report.Json[..end].TrimEnd() + ",\n  \"suites\": " + suitesJson.Replace("\n", "\n  ") + "\n}" };
            }
            Console.WriteLine(report.Markdown);

            foreach (var f in new[] { opt.OutCsv, opt.OutMd, opt.OutJson })
                if (Path.GetDirectoryName(Path.GetFullPath(f)) is { } dir) Directory.CreateDirectory(dir);
            File.WriteAllText(opt.OutCsv, report.Csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.WriteAllText(opt.OutMd, report.Markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.WriteAllText(opt.OutJson, report.Json, new UTF8Encoding(false));
            foreach (var x in suites.Where(x => x.Csv.Length > 0))
            {
                var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(opt.OutCsv))!,
                    Path.GetFileNameWithoutExtension(opt.OutCsv) + "-" + x.Name.ToLowerInvariant() + ".csv");
                File.WriteAllText(path, x.Csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                Console.WriteLine($"[render-probe] {x.Name} 明细 {path}");
            }
            Console.WriteLine($"[render-probe] 逐帧明细 {Path.GetFullPath(opt.OutCsv)}");
            Console.WriteLine($"[render-probe] 汇总表 {Path.GetFullPath(opt.OutMd)} / {Path.GetFullPath(opt.OutJson)}");

            if (opt.WriteBaseline != null)
            {
                File.WriteAllText(opt.WriteBaseline, ProbeBaseline.FromEntries(report.Entries).ToJson(), new UTF8Encoding(false));
                Console.WriteLine($"[render-probe] 计数基线已写入 {Path.GetFullPath(opt.WriteBaseline)}");
            }

            int exit = combos.Any(c => c.Error != null) ? 98 : 0;
            if (opt.Baseline != null)
            {
                var baseline = ProbeBaseline.Load(opt.Baseline);
                var check = baseline.Check(report.Entries, opt.Tolerance);
                Console.WriteLine(check.Text);
                if (opt.SummaryFile != null) File.AppendAllText(opt.SummaryFile, check.Text + Environment.NewLine);
                if (check.Regressed) exit = 3;
            }
            if (opt.SummaryFile != null) File.AppendAllText(opt.SummaryFile, report.Markdown + Environment.NewLine);
            return exit;
        }

        // ── 一段测量:设模式 → 归位 → 跑 steps 帧 ───────────────────────────────

        private static void RunSegment(ProbeRig rig, Combo c, int steps, bool record)
        {
            var step = ScenarioStep(rig, c.Scenario);
            Apply(c.Mode);
            try
            {
                rig.ResetAll();
                RunFrame(rig, c.Mode.Plot);
                if (record)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                IncrementalRenderProbe.ShortCircuitMisses = 0; // 只统计测量段
                IncrementalRenderProbe.ShortCircuitMissDetail.Clear();

                var samples = new List<Sample>(steps);
                var sampler = record ? s_allocSampler : null;
                sampler?.Begin();
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                bool checkTip = c.Scenario.Contains("Hover", StringComparison.Ordinal);
                for (int i = 0; i < steps; i++) samples.Add(RunStep(rig, step, i, c.Mode.Plot, checkTip));
                if (sampler != null)
                {
                    Pump(500); // EventPipe 异步派发,等缓冲里的采样事件到齐
                    foreach (var r in sampler.End())
                    {
                        var k = (r.Type, r.Large);
                        var old = c.AllocTypes.GetValueOrDefault(k);
                        c.AllocTypes[k] = (old.Ticks + r.Ticks, old.Bytes + r.SampledBytes, Math.Max(old.MaxObj, r.MaxObjectSize));
                    }
                    c.AllocFrames += steps;
                }
                if (!record) return;

                c.Rounds.Add(samples);
                c.RoundGc.Add((GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2));
                c.ShortCircuitMisses += IncrementalRenderProbe.ShortCircuitMisses;
                foreach (var kv in IncrementalRenderProbe.ShortCircuitMissDetail)
                    c.MissDetail[kv.Key] = c.MissDetail.GetValueOrDefault(kv.Key) + kv.Value;
            }
            catch (Exception ex)
            {
                // 对照组(尤其 Parallel)出错不拖垮整轮测量,记下来进报告
                c.Error = $"{ex.GetType().Name}: {ex.Message}";
                Console.Error.WriteLine($"[render-probe] {c.Key} 失败:{ex}");
            }
            finally
            {
                IncrementalRenderProbe.Reset();
                rig.ResetAll();
                RunFrame(rig, PlotMode.Sync);
            }
        }

        private static AllocTypeSampler? s_allocSampler;

        private static string AllocTypesMarkdown(List<Combo> combos)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("### 分配类型采样(--alloc-types,GCAllocationTick,UI 线程,含脚本输入)");
            sb.AppendLine();
            foreach (var c in combos.Where(c => c.AllocFrames > 0))
            {
                var rows = c.AllocTypes
                    .Select(kv => new AllocTypeSampler.Row(kv.Key.Type, kv.Key.Large, kv.Value.Ticks, kv.Value.Bytes, kv.Value.MaxObj))
                    .OrderByDescending(r => r.SampledBytes).ToList();
                sb.AppendLine(AllocTypeSampler.Format($"{c.Key}", rows, c.AllocFrames));
            }
            return sb.ToString();
        }

        private static void Apply(Mode m)
        {
            IncrementalRenderProbe.Reset();
            IncrementalRenderProbe.ForceFullPass = m.FullPass;
            IncrementalRenderProbe.BypassBagShortCircuit = m.BypassShortCircuit;
            IncrementalRenderProbe.ForceLayerRedraw = m.LayerRedraw;
        }

        private static Sample RunStep(ProbeRig rig, Action<int> step, int i, PlotMode plot, bool checkTip)
        {
            long t0 = Stopwatch.GetTimestamp();
            step(i); // 写黑板 / 改窗口尺寸(含 Watch 副作用:视口钳位、自动量程、布局)
            long t1 = Stopwatch.GetTimestamp();
            long alloc0 = GC.GetAllocatedBytesForCurrentThread(); // 只统计帧本身的分配,不含脚本输入

            long features0 = IncrementalRenderProbe.FeatureProjections;
            var (layers, drawCmds) = RunFrame(rig, plot);
            long t2 = Stopwatch.GetTimestamp();
            long alloc1 = GC.GetAllocatedBytesForCurrentThread();

            return new Sample(
                PipelineMs: (t2 - t1) * 1000.0 / Stopwatch.Frequency,
                InputMs: (t1 - t0) * 1000.0 / Stopwatch.Frequency,
                Features: IncrementalRenderProbe.FeatureProjections - features0,
                Layers: layers,
                DrawCmds: drawCmds,
                AllocBytes: alloc1 - alloc0,
                TooltipShown: checkTip && rig.TooltipShown());
        }

        // 一帧 = 每张图各跑一次 CompositionTarget 回调里的那段(排队事务 → ExecutePipeline → Invalidate)。
        private static (int Layers, long DrawCmds) RunFrame(ProbeRig rig, PlotMode plot)
        {
            int layers = 0;
            long cmds = 0;
            foreach (var ch in rig.Charts)
            {
                layers += ch.Cell.RunFrameNow(plot);
                cmds += ch.Cell.GetDiagnostics().LastFrameDrawCmds;
            }
            foreach (var pane in rig.Panes)
            {
                layers += pane.Cell.RunFrameNow(plot);
                cmds += pane.Cell.GetDiagnostics().LastFrameDrawCmds;
            }
            if (s_debugFrames > 0)
            {
                s_debugFrames--;
                var cells = rig.Charts.Select(c => c.Cell).Concat(rig.Panes.Select(p => p.Cell));
                Console.WriteLine("[debug] 脏图层: " + string.Join(" | ", cells.Select(c => string.Join(",", DirtyNames(c)))));
            }
            return (layers, cmds);
        }

        // HEVO_PROBE_DEBUG=N:打印前 N 帧每张图重录的图层名(排查计数异常用)
        private static int s_debugFrames = int.TryParse(Environment.GetEnvironmentVariable("HEVO_PROBE_DEBUG"), out var n) ? n : 0;
        private static readonly System.Reflection.FieldInfo s_dirtyField =
            typeof(ChartCell).GetField("_dirtyLayerBuffer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        private static IEnumerable<string> DirtyNames(ChartCell cell) =>
            ((System.Collections.IEnumerable)s_dirtyField.GetValue(cell)!).Cast<ChartLayer>().Select(l => l.Name);

        // ── 场景脚本 ───────────────────────────────────────────────────────────

        private static Action<int> ScenarioStep(ProbeRig rig, string scenario) => scenario switch
        {
            "Hover" => i => { foreach (var ch in rig.Charts) HoverStep(ch, i); },
            "Pan" => i => { foreach (var ch in rig.Charts) PanStep(ch, i); },
            "Zoom" => i => { foreach (var ch in rig.Charts) ZoomStep(ch, i); },
            "Tick" => i => { foreach (var ch in rig.Charts) TickStep(ch, i); },
            "Append" => i => { foreach (var ch in rig.Charts) AppendStep(ch, i); },
            "Resize" => i => rig.ResizeStep(i),
            "HoverNoTip" or "DashHover" => i => { foreach (var ch in rig.Charts) HoverStep(ch, i); },
            "DashPan" => i => { foreach (var ch in rig.Charts) PanStep(ch, i); },
            "DashTick" => i =>
            {
                foreach (var ch in rig.Charts) TickStep(ch, i);
                foreach (var pane in rig.Panes) pane.TickStep(i);
            },
            "Zoom@150" or "Zoom@200" => i => { foreach (var ch in rig.Charts) ZoomStep(ch, i); },
            _ => throw new ArgumentException($"未知场景 {scenario}"),
        };

        // 十字光标在绘图区内来回横扫,每步 3px;Y 同步小幅摆动(真实鼠标移动 Y 也会变)。
        // 命中计算复刻 ChartInteractionFeature.UpdatePointerStateFromMouse(吸附到可见 K 线中心)。
        private static void HoverStep(ProbeChart ch, int i)
        {
            HevoRect plot;
            ScaleStrategyTrait? scale;
            using (var ctx = ch.Cell.CreateContext())
            {
                plot = ctx.GetPlotArea();
                scale = ctx.Shared().Read<ScaleStrategyTrait>();
            }
            if (scale == null || plot.IsEmpty) return;

            int span = Math.Max(1, (int)(plot.Width / 3));
            int k = i % (2 * span);
            double x = plot.Left + 3.0 * (k < span ? k : 2 * span - k);
            float y = plot.Y + plot.Height * (0.3f + 0.4f * (i % 50) / 50f);

            var board = ch.Board;
            var schema = ch.Schema;
            using (board.AcquireUpgradeableReadLock())
            {
                var active = board.Read(schema.Viewport.ActiveRange);
                if (!active.IsValid) return;
                var ds = scale.DomainScale;
                double rel = Math.Clamp((x - plot.Left) / plot.Width, 0.0, 1.0);
                int idx = (int)Math.Round(ds.Denormalize(rel, active));
                int lo = Math.Max(0, (int)Math.Ceiling(ds.Denormalize(0.0, active)));
                int hi = Math.Min(ch.Length - 1, (int)Math.Floor(ds.Denormalize(1.0, active)));
                if (hi < lo) { lo = 0; hi = ch.Length - 1; }
                idx = Math.Clamp(idx, lo, hi);
                double cRel = ds.Normalize(idx, active);
                double cx = plot.Left + plot.Width * cRel;

                using (board.AcquireWriteLock())
                    board.WriteIfChanged(schema.HitPortForProbe, new PointerHitState(
                        new HevoPoint((float)cx, y), new DomainHitResult(idx, cx, cRel), cx, idx, false));
            }
        }

        // 每步平移 1 根 K 线,每 100 步换方向,始终停留在数据范围内。
        private static void PanStep(ProbeChart ch, int i)
        {
            double delta = (i / 100) % 2 == 0 ? -1 : 1;
            var board = ch.Board;
            using (board.AcquireUpgradeableReadLock())
            {
                var active = board.Read(ch.Schema.Viewport.ActiveRange);
                if (!active.IsValid) return;
                using (board.AcquireWriteLock())
                    board.WriteIfChanged(ch.Schema.Viewport.UserRange, new RealRange(active.Min + delta, active.Max + delta));
            }
        }

        // 滚轮缩放:右缘锚定,可见跨度每步 ×1.1(一格滚轮),在 60 根和上限(min(数据量, 20000))之间来回。
        // 数据量 10 万时上限 2 万根,用来看"可见根数增长"下的伸缩曲线。
        private static void ZoomStep(ProbeChart ch, int i)
        {
            const double minSpan = 60, factor = 1.1;
            double maxSpan = Math.Min(ch.Length - 1, 20000);
            int k = Math.Max(1, (int)Math.Ceiling(Math.Log(maxSpan / minSpan) / Math.Log(factor)));
            int e = i % (2 * k);
            if (e > k) e = 2 * k - e;
            double span = Math.Min(maxSpan, minSpan * Math.Pow(factor, e));
            double max = ch.Length - 1;
            using (ch.Board.AcquireWriteLock())
                ch.Board.WriteIfChanged(ch.Schema.Viewport.UserRange, new RealRange(Math.Round(max - span), max));
        }

        // 行情心跳:最后一根 K 线收盘价跳动。数组原地改 + ForceWrite(ROM 同底层数组同长度,WriteIfChanged 判不出变化,
        // 真实数据管线也是这样推送的)。
        private static void TickStep(ProbeChart ch, int i)
        {
            var data = ch.Data;
            int last = ch.Length - 1;
            double c = data.Open[last] + Math.Sin(i * 0.37) * 2.0;
            data.Close[last] = c;
            data.High[last] = Math.Max(data.High[last], c);
            data.Low[last] = Math.Min(data.Low[last], c);
            var board = ch.Board;
            var p = ch.Schema.Ports;
            using (board.BeginTransaction())
            {
                board.ForceWrite(p.High, ch.Slice(data.High));
                board.ForceWrite(p.Low, ch.Slice(data.Low));
                board.ForceWrite(p.Close, ch.Slice(data.Close));
            }
        }

        // 实时追加:每步新收一根 K 线(数据预生成在数组尾部),所有序列长度 +1,视口跟随最新一根。
        private static void AppendStep(ProbeChart ch, int i)
        {
            if (ch.Length >= ch.Data.Close.Length) return; // 预留容量用完(steps > 容量时)就停在末尾
            ch.Length++;
            ch.WriteSeries(follow: true);
        }

        // ── Startup:新建窗口 → 装配 → 首帧出图 ─────────────────────────────────

        private static StartupResult MeasureStartup(ProbeOptions opt, int bars)
        {
            var compose = new List<double>();
            var first = new List<double>();
            var alloc = new List<long>();
            int layers = 0;
            int runs = 1 + opt.Rounds * 3; // 第 0 次是冷启动(含 JIT),单独报
            for (int r = 0; r < runs; r++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                long t0 = Stopwatch.GetTimestamp();

                var ch = ProbeChart.Create(bars, 0);
                ch.Cell.Width = opt.WindowWidth;
                ch.Cell.Height = opt.WindowHeight;
                ch.Cell.IsHitTestVisible = false; // 同 ProbeRig:不让真实鼠标位置影响测量
                var window = new Window
                {
                    Title = "Hevo render probe (startup)",
                    SizeToContent = SizeToContent.WidthAndHeight,
                    Content = ch.Cell,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ShowActivated = false,
                };
                window.Show();
                window.UpdateLayout(); // OnApplyTemplate → ComposeAll + SizeChanged 排队
                long t1 = Stopwatch.GetTimestamp();

                ch.LoadData();
                ch.Cell.RunFrameNow(PlotMode.Sync);
                long t2 = Stopwatch.GetTimestamp();
                long a1 = GC.GetAllocatedBytesForCurrentThread();
                layers = ch.Cell.ActiveLayers.Count;

                compose.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
                first.Add((t2 - t1) * 1000.0 / Stopwatch.Frequency);
                alloc.Add(a1 - a0);
                window.Close();
                Pump(30);
            }
            Console.WriteLine($"[render-probe] Startup bars={bars} 完成 {runs} 次");
            return new StartupResult(bars, compose, first, alloc, layers);
        }

        // ── 报告 ─────────────────────────────────────────────────────────────

        internal sealed record Entry(
            int Bars, int Charts, string Scenario, string ModeKey, string ModeName, int Rounds, int Frames,
            double MedianMs, double CiMs, double P95Ms, double P99Ms, double InputMedianMs,
            double FeaturesPerFrame, double LayersPerFrame, double DrawCmdsPerFrame, double AllocPerFrame,
            double Gen0Per1k, double Gen1Per1k, double Gen2Per1k, bool CountersDeterministic, string? Error)
        {
            public string Key => $"bars={Bars}|charts={Charts}|{Scenario}|{ModeKey}";
        }

        private sealed record Report(string Markdown, string Csv, string Json, List<Entry> Entries);

        private static Report BuildReport(ProbeEnvironment env, ProbeOptions opt, List<Combo> combos, List<StartupResult> startups)
        {
            var entries = new List<Entry>();
            foreach (var c in combos)
            {
                if (c.Rounds.Count == 0)
                {
                    entries.Add(new Entry(c.Bars, c.Charts, c.Scenario, c.Mode.Key, c.Mode.Name, 0, 0,
                        double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
                        double.NaN, double.NaN, double.NaN, false, c.Error ?? "无数据"));
                    continue;
                }
                var all = c.Rounds.SelectMany(r => r).ToList();
                var roundMedians = c.Rounds.Select(r => Stats.Percentile(r.Select(s => s.PipelineMs), 0.5)).ToList();
                var (mean, ci) = Stats.MeanCi95(roundMedians);
                var featuresPerRound = c.Rounds.Select(r => r.Average(s => (double)s.Features)).ToList();
                var layersPerRound = c.Rounds.Select(r => r.Average(s => (double)s.Layers)).ToList();
                var cmdsPerRound = c.Rounds.Select(r => r.Average(s => (double)s.DrawCmds)).ToList();
                bool deterministic = Spread(featuresPerRound) < 1e-9 && Spread(layersPerRound) < 1e-9 && Spread(cmdsPerRound) < 1e-9;
                double perK = 1000.0 / all.Count;
                entries.Add(new Entry(c.Bars, c.Charts, c.Scenario, c.Mode.Key, c.Mode.Name, c.Rounds.Count, all.Count,
                    mean, ci,
                    Stats.Percentile(all.Select(s => s.PipelineMs), 0.95),
                    Stats.Percentile(all.Select(s => s.PipelineMs), 0.99),
                    Stats.Percentile(all.Select(s => s.InputMs), 0.5),
                    featuresPerRound.Average(), layersPerRound.Average(), cmdsPerRound.Average(),
                    all.Average(s => (double)s.AllocBytes),
                    c.RoundGc.Sum(g => g.Gen0) * perK, c.RoundGc.Sum(g => g.Gen1) * perK, c.RoundGc.Sum(g => g.Gen2) * perK,
                    deterministic, c.Error));
            }

            var md = new StringBuilder();
            md.AppendLine("## render-probe 结果");
            md.AppendLine();
            md.AppendLine("```");
            md.AppendLine(env.Describe());
            md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"steps={opt.Steps} warmup={opt.Warmup} rounds={opt.Rounds} size={opt.WindowWidth}x{opt.WindowHeight}"));
            md.AppendLine("```");

            foreach (var g in entries.GroupBy(e => (e.Bars, e.Charts)))
            {
                md.AppendLine();
                md.AppendLine($"### {g.Key.Bars} 根 K 线 × {g.Key.Charts} 张图");
                md.AppendLine();
                md.AppendLine("| 场景 | 模式 | 帧耗时中位(ms) 均值±95%CI | P95(ms) | P99(ms) | 输入中位(ms) | Feature重算/帧 | 图层重录/帧 | 绘制命令/帧 | 分配/帧(B) | GC 0/1/2 每千帧 |");
                md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
                foreach (var e in g)
                {
                    if (e.Error != null && e.Frames == 0)
                    {
                        md.AppendLine($"| {e.Scenario} | {e.ModeName} | 失败:{e.Error} | | | | | | | | |");
                        continue;
                    }
                    string mark = e.CountersDeterministic ? "" : " ⚠";
                    md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                        $"| {e.Scenario} | {e.ModeName} | {e.MedianMs:F3} ± {e.CiMs:F3} | {e.P95Ms:F3} | {e.P99Ms:F3} | {e.InputMedianMs:F3} | " +
                        $"{e.FeaturesPerFrame:F1}{mark} | {e.LayersPerFrame:F1}{mark} | {e.DrawCmdsPerFrame:F0}{mark} | {e.AllocPerFrame:F0} | " +
                        $"{e.Gen0Per1k:F1}/{e.Gen1Per1k:F1}/{e.Gen2Per1k:F1} |"));
                }
            }
            if (entries.Any(e => !e.CountersDeterministic && e.Frames > 0))
                md.AppendLine().AppendLine("⚠ = 计数在各轮之间不一致(脚本或引擎里有不确定因素),不能作回归门槛。");

            if (startups.Count > 0)
            {
                md.AppendLine();
                md.AppendLine("### Startup(新建窗口 → 装配 → 首帧)");
                md.AppendLine();
                md.AppendLine("| K 线根数 | 冷启动 装配/首帧(ms) | 热启动 装配(ms) 中位±95%CI | 热启动 首帧(ms) 中位±95%CI | 分配/次(KB) | 图层数 |");
                md.AppendLine("|---:|---:|---:|---:|---:|---:|");
                foreach (var s in startups)
                {
                    var warmCompose = s.ComposeMs.Skip(1).ToList();
                    var warmFirst = s.FirstFrameMs.Skip(1).ToList();
                    var (cm, cci) = Stats.MedianCi95(warmCompose);
                    var (fm, fci) = Stats.MedianCi95(warmFirst);
                    md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                        $"| {s.Bars} | {s.ComposeMs[0]:F1} / {s.FirstFrameMs[0]:F1} | {cm:F2} ± {cci:F2} | {fm:F2} ± {fci:F2} | " +
                        $"{s.AllocBytes.Skip(1).DefaultIfEmpty(s.AllocBytes[0]).Average() / 1024:F0} | {s.Layers} |"));
                }
            }

            var tipCombos = combos.Where(c => c.Scenario.Contains("Hover", StringComparison.Ordinal) && c.Rounds.Count > 0).ToList();
            if (tipCombos.Count > 0)
            {
                md.AppendLine();
                md.AppendLine("Tooltip 实际显示的帧占比(帧末 TooltipWidgetLayer 的 widget 指令非空):");
                md.AppendLine();
                foreach (var c in tipCombos)
                {
                    var all = c.Rounds.SelectMany(r => r).ToList();
                    md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                        $"- {c.Key}: {100.0 * all.Count(x => x.TooltipShown) / all.Count:F0}%"));
                }
            }

            foreach (var c in combos.Where(c => c.ShortCircuitMisses > 0))
            {
                md.AppendLine();
                md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"[render-probe] {c.Key}: 短路本会跳过、引用比对却判脏 {c.ShortCircuitMisses} 次(共 {c.Rounds.Sum(r => r.Count)} 帧)"));
                foreach (var kv in c.MissDetail.OrderByDescending(kv => kv.Value))
                    md.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {kv.Value,6}  {kv.Key}"));
            }

            var csv = new StringBuilder("bars,charts,scenario,mode,round,step,pipeline_ms,input_ms,features,layers,draw_cmds,alloc_bytes\n");
            foreach (var c in combos)
                for (int r = 0; r < c.Rounds.Count; r++)
                    for (int i = 0; i < c.Rounds[r].Count; i++)
                    {
                        var x = c.Rounds[r][i];
                        csv.Append(string.Create(CultureInfo.InvariantCulture,
                            $"{c.Bars},{c.Charts},{c.Scenario},{c.Mode.Key},{r},{i},{x.PipelineMs:F4},{x.InputMs:F4},{x.Features},{x.Layers},{x.DrawCmds},{x.AllocBytes}\n"));
                    }

            var json = JsonSerializer.Serialize(new
            {
                environment = env,
                options = new { opt.Steps, opt.Warmup, opt.Rounds, opt.WindowWidth, opt.WindowHeight },
                entries = entries.Select(e => new
                {
                    e.Key, e.Bars, e.Charts, e.Scenario, mode = e.ModeKey, e.Rounds, e.Frames,
                    medianMs = Num(e.MedianMs), ci95Ms = Num(e.CiMs), p95Ms = Num(e.P95Ms), p99Ms = Num(e.P99Ms),
                    inputMedianMs = Num(e.InputMedianMs),
                    featuresPerFrame = Num(e.FeaturesPerFrame), layersPerFrame = Num(e.LayersPerFrame),
                    drawCmdsPerFrame = Num(e.DrawCmdsPerFrame), allocBytesPerFrame = Num(e.AllocPerFrame),
                    gen0Per1kFrames = Num(e.Gen0Per1k), gen1Per1kFrames = Num(e.Gen1Per1k), gen2Per1kFrames = Num(e.Gen2Per1k),
                    countersDeterministic = e.CountersDeterministic, error = e.Error,
                }),
                startup = startups.Select(s => new { s.Bars, composeMs = s.ComposeMs, firstFrameMs = s.FirstFrameMs, s.Layers }),
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

            return new Report(md.ToString(), csv.ToString(), json, entries);
        }

        private static double? Num(double v) => double.IsFinite(v) ? Math.Round(v, 4) : null;
        private static double Spread(List<double> v) => v.Count == 0 ? 0 : v.Max() - v.Min();

        internal static void Pump(int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    /// <summary>命令行参数。</summary>
    internal sealed class ProbeOptions
    {
        public int[] Bars = { 2000 };
        public int[] Charts = { 1 };
        public int Steps = 300;
        public int Warmup = 100;
        public int Rounds = 5;
        public int WindowWidth = 1280, WindowHeight = 720;
        public string[] Scenarios = RenderProbe.AllScenarios;
        public RenderProbe.Mode[] Modes = RenderProbe.AllModes;
        public bool Startup = true;
        public string OutCsv = "render-probe.csv";
        public string OutMd = "render-probe.md";
        public string OutJson = "render-probe.json";
        public string? Baseline;
        public string? WriteBaseline;
        public double Tolerance = 0.02;
        public string? SummaryFile;
        public bool AllocTypes;
        public int HeavyLayers;
        public double HeavyMs = 1.0;
        public string[] SuiteNames = Array.Empty<string>();
        public int[] FeedRates = { 100, 500, 1000 };
        public double FeedSeconds = 5;
        public int FeedBars = 2000;
        public bool FeedSourceMap;
        public int[] PyIndicators = { 1, 4, 8 };
        public int PyRate = 100;
        public int SoakMinutes = 10;
        public int SoakRate = 100;
        public int BlueprintWarmRuns = 5;

        public static ProbeOptions Parse(string[] args)
        {
            var o = new ProbeOptions();
            if (Has(args, "--ci"))
            {
                // CI:只测增量(默认)模式的计数,窗口缩小到 960x540(托管 runner 屏幕 1024x768,窗口过大会被系统截掉,计数就跟本机对不上)
                o.Steps = 120; o.Warmup = 20; o.Rounds = 1; o.Startup = false;
                o.WindowWidth = 960; o.WindowHeight = 540;
                o.Modes = RenderProbe.AllModes.Where(m => m.Key == "inc").ToArray();
                o.Scenarios = RenderProbe.AllFrameScenarios;
                o.SummaryFile = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
            }
            if (Get(args, "--bars=") is { } bars) o.Bars = Ints(bars);
            if (Get(args, "--charts=") is { } charts) o.Charts = Ints(charts);
            if (Get(args, "--steps=") is { } steps) o.Steps = int.Parse(steps, CultureInfo.InvariantCulture);
            if (Get(args, "--warmup=") is { } warm) o.Warmup = int.Parse(warm, CultureInfo.InvariantCulture);
            if (Get(args, "--rounds=") is { } rounds) o.Rounds = Math.Max(1, int.Parse(rounds, CultureInfo.InvariantCulture));
            if (Get(args, "--window=") is { } win)
            {
                var wh = win.Split('x', 'X');
                o.WindowWidth = int.Parse(wh[0], CultureInfo.InvariantCulture);
                o.WindowHeight = int.Parse(wh[1], CultureInfo.InvariantCulture);
            }
            if (Get(args, "--scenarios=") is { } sc)
            {
                var want = sc.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                o.Scenarios = RenderProbe.AllFrameScenarios.Where(s => want.Contains(s, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (want.Contains("Startup", StringComparer.OrdinalIgnoreCase)) o.Startup = true;
                else if (!want.Contains("all", StringComparer.OrdinalIgnoreCase)) o.Startup = false;
                if (want.Contains("all", StringComparer.OrdinalIgnoreCase)) o.Scenarios = RenderProbe.AllFrameScenarios;
                o.SuiteNames = RenderProbe.Suites.Where(x => want.Contains(x, StringComparer.OrdinalIgnoreCase)
                    || (x != "Soak" && want.Contains("all", StringComparer.OrdinalIgnoreCase))).ToArray();
            }
            if (Get(args, "--modes=") is { } modes && modes != "all")
            {
                var want = modes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                o.Modes = RenderProbe.AllModes.Where(m => want.Contains(m.Key, StringComparer.OrdinalIgnoreCase)).ToArray();
            }
            if (Has(args, "--no-startup")) o.Startup = false;
            if (Has(args, "--alloc-types")) o.AllocTypes = true;
            if (Get(args, "--heavy-layers=") is { } hl) o.HeavyLayers = int.Parse(hl, CultureInfo.InvariantCulture);
            if (Get(args, "--heavy-ms=") is { } hm) o.HeavyMs = double.Parse(hm, CultureInfo.InvariantCulture);
            if (Get(args, "--feed-rates=") is { } fr) o.FeedRates = Ints(fr);
            if (Get(args, "--feed-seconds=") is { } fs) o.FeedSeconds = double.Parse(fs, CultureInfo.InvariantCulture);
            if (Get(args, "--feed-bars=") is { } fb) o.FeedBars = int.Parse(fb, CultureInfo.InvariantCulture);
            if (Get(args, "--feed-map=") is { } fm) o.FeedSourceMap = fm.Equals("source", StringComparison.OrdinalIgnoreCase);
            if (Get(args, "--py-indicators=") is { } pi) o.PyIndicators = Ints(pi);
            if (Get(args, "--py-rate=") is { } pr) o.PyRate = int.Parse(pr, CultureInfo.InvariantCulture);
            if (Get(args, "--soak-minutes=") is { } sm) o.SoakMinutes = int.Parse(sm, CultureInfo.InvariantCulture);
            if (Get(args, "--soak-rate=") is { } sr) o.SoakRate = int.Parse(sr, CultureInfo.InvariantCulture);
            if (Get(args, "--blueprint-runs=") is { } br) o.BlueprintWarmRuns = int.Parse(br, CultureInfo.InvariantCulture);
            if (Get(args, "--out=") is { } outCsv)
            {
                o.OutCsv = outCsv;
                var stem = Path.Combine(Path.GetDirectoryName(outCsv) ?? "", Path.GetFileNameWithoutExtension(outCsv));
                o.OutMd = stem + ".md";
                o.OutJson = stem + ".json";
            }
            o.Baseline = Get(args, "--baseline=");
            o.WriteBaseline = Get(args, "--write-baseline=");
            if (Get(args, "--tolerance=") is { } tol) o.Tolerance = double.Parse(tol, CultureInfo.InvariantCulture);
            return o;
        }

        private static int[] Ints(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => int.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();

        internal static bool Has(string[] args, string flag) => args.Contains(flag, StringComparer.Ordinal);

        internal static string? Get(string[] args, string prefix)
        {
            foreach (var a in args)
                if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length);
            return null;
        }
    }

    /// <summary>
    /// 一个测量窗口:1 张或 N 张图(UniformGrid 排布),每张图有自己的 schema / 黑板 / 数据。
    /// 尺寸定在内容上(窗口 SizeToContent),不定在窗口上 —— 不同 Windows 版本的标题栏 / 边框宽度不同,
    /// 定窗口尺寸会让绘图区差几个像素,计数类指标就跟基线对不上。
    /// </summary>
    internal sealed class ProbeRig : IDisposable
    {
        public Window Window { get; }
        /// <summary>场景脚本操作的图(dashboard 里是主图)。</summary>
        public List<ProbeChart> Charts { get; }
        /// <summary>dashboard 的副图(其余窗口为空);每帧跟主图一起跑。</summary>
        public List<ProbePane> Panes { get; }
        private readonly FrameworkElement _content;
        private readonly double _baseWidth;
        private readonly TooltipWidgetLayer? _tipLayer;

        private ProbeRig(Window window, FrameworkElement content, List<ProbeChart> charts, List<ProbePane> panes)
        {
            Window = window;
            _content = content;
            Charts = charts;
            Panes = panes;
            _baseWidth = content.Width;
            _tipLayer = charts[0].Cell.ActiveLayers.OfType<TooltipWidgetLayer>().FirstOrDefault();
        }

        /// <summary>第一张图的 tooltip 本帧是否真的上屏(TooltipWidgetLayer 前台 buffer 有 widget 指令)。</summary>
        public bool TooltipShown() => _tipLayer?.Buffer is LayerBuffer lb && !lb.Widget.IsEmpty;

        public static ProbeRig? Create(ProbeOptions opt, int bars, int charts, int extraCapacity, RigKind kind = RigKind.Standard)
        {
            var chartOptions = kind switch
            {
                RigKind.NoTooltip => new ProbeChartOptions(Tooltip: false),
                RigKind.Dashboard => new ProbeChartOptions(Markers: true),
                _ => ProbeChartOptions.Default,
            };
            double scale = ProbeDpi.ScaleOf(kind);

            List<ProbeChart> list;
            var panes = new List<ProbePane>();
            FrameworkElement content;
            if (kind == RigKind.Dashboard)
            {
                var masterSchema = new ProbeKLineSchema(chartOptions);
                var volumeSchema = new ProbeVolumeSchema();
                var dashboard = new Hevo.Charting.Linked.LinkedChartDashboard(new Hevo.Charting.Linked.LinkedChartContext())
                    .AddMaster(masterSchema, heightRatio: 3)
                    .AddPane(volumeSchema, heightRatio: 1);
                var master = ProbeChart.Create(bars, extraCapacity, masterSchema, dashboard.Cells[0]);
                list = new List<ProbeChart> { master };
                panes.Add(new ProbePane { Schema = volumeSchema, Cell = dashboard.Cells[1], Board = new DataBlackboard(), Master = master });
                content = dashboard;
            }
            else
            {
                list = Enumerable.Range(0, charts).Select(_ => ProbeChart.Create(bars, extraCapacity, chartOptions)).ToList();
                if (charts == 1) content = list[0].Cell;
                else
                {
                    int cols = (int)Math.Ceiling(Math.Sqrt(charts));
                    var grid = new UniformGrid { Columns = cols, Rows = (int)Math.Ceiling(charts / (double)cols) };
                    foreach (var ch in list) grid.Children.Add(ch.Cell);
                    content = grid;
                }
            }
            // 高 DPI 模拟:内容按 1/scale 的 DIP 尺寸布局,LayoutTransform 放大回原像素尺寸
            content.Width = opt.WindowWidth / scale;
            content.Height = opt.WindowHeight / scale;
            if (scale != 1.0) content.LayoutTransform = new ScaleTransform(scale, scale);
            content.HorizontalAlignment = HorizontalAlignment.Left;
            content.VerticalAlignment = VerticalAlignment.Top;

            // 外面再套一层定尺寸的宿主:Resize 场景只改图表宽度,窗口(HWND)尺寸不变。
            // 否则 SizeToContent 会跟着缩窗口,WM_SIZE 里 WPF 同步跑一次渲染,
            // CompositionTarget.Rendering 回调就在"输入"阶段把这一帧做掉,RunFrameNow 测到的是空帧。
            // IsHitTestVisible=false:窗口在屏幕正中,真实鼠标停在上面时 WPF 会合成 MouseMove,
            // ChartInteractionFeature 据此写 PointerHitPort —— 十字光标 / tooltip / 标题栏跟着每次缩放平移重算,
            // 计数就随鼠标位置漂移。测量全靠脚本直接写端口,窗口不需要接收鼠标。
            var host = new System.Windows.Controls.Grid { Width = opt.WindowWidth, Height = opt.WindowHeight, IsHitTestVisible = false };
            host.Children.Add(content);

            var window = new Window
            {
                Title = "Hevo render probe",
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                Content = host,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            window.Show();
            RenderProbe.Pump(300); // 等 OnApplyTemplate → ComposeAll + SizeChanged
            if (scale != 1.0)
            {
                foreach (var ch in list) ProbeDpi.Apply(ch.Cell, scale);
                RenderProbe.Pump(100);
            }

            foreach (var ch in list) ch.LoadData();
            foreach (var pane in panes) pane.LoadData();
            RenderProbe.Pump(300);
            foreach (var ch in list) ch.Cell.RunFrameNow(PlotMode.Sync);
            foreach (var pane in panes) pane.Cell.RunFrameNow(PlotMode.Sync);

            foreach (var ch in list)
            {
                using var ctx = ch.Cell.CreateContext();
                if (ctx.GetPlotArea().IsEmpty || ch.Cell.ActiveLayers.Count == 0)
                {
                    Console.Error.WriteLine("[render-probe] 图表没有完成装配(PlotArea 为空或没有图层),放弃测量。");
                    window.Close();
                    return null;
                }
            }
            return new ProbeRig(window, content, list, panes);
        }

        /// <summary>归位:数据长度回到初始、视口回到最右 120 根、清掉 hover、尺寸回到原宽。</summary>
        public void ResetAll()
        {
            if (Math.Abs(_content.Width - _baseWidth) > 0.5)
            {
                _content.Width = _baseWidth;
                Window.UpdateLayout();
            }
            foreach (var ch in Charts) ch.Reset();
            foreach (var pane in Panes) pane.WriteSeries(); // DashTick 改过的成交量 / Append 后的长度跟主图对齐
        }

        // 宽度三角波:每步 8px,在原宽和原宽 -168px 之间来回。走真实的 WPF 布局 → SizeChanged → 环境纪元 → FullPass。
        public void ResizeStep(int i)
        {
            int e = i % 40;
            if (e > 20) e = 40 - e;
            _content.Width = _baseWidth - 8 * (e + 1);
            Window.UpdateLayout();
        }

        public void Dispose()
        {
            Window.Close();
            RenderProbe.Pump(50);
        }
    }

    /// <summary>一张图:schema + cell + 黑板 + 数据(数组预留了追加用的容量)。</summary>
    internal sealed class ProbeChart
    {
        public required ProbeKLineSchema Schema { get; init; }
        public required ChartCell Cell { get; init; }
        public required DataBlackboard Board { get; init; }
        public required ProbeData Data { get; init; }
        public required int InitialLength { get; init; }
        /// <summary>当前对外可见的 K 线根数(Append 场景会增长)。</summary>
        public int Length { get; set; }

        public static ProbeChart Create(int bars, int extraCapacity, ProbeChartOptions? options = null)
        {
            var schema = new ProbeKLineSchema(options ?? ProbeChartOptions.Default);
            return Create(bars, extraCapacity, schema, new ChartCell { Template = schema });
        }

        /// <summary>cell 已由外部装配(如 LinkedChartDashboard.AddMaster)时用这个重载。</summary>
        public static ProbeChart Create(int bars, int extraCapacity, ProbeKLineSchema schema, ChartCell cell)
        {
            return new ProbeChart
            {
                Schema = schema,
                Cell = cell,
                Board = new DataBlackboard(),
                Data = ProbeData.Generate(bars + extraCapacity),
                InitialLength = bars,
                Length = bars,
            };
        }

        public ReadOnlyMemory<T> Slice<T>(T[] array) => new(array, 0, Length);

        public void LoadData()
        {
            WriteSeries(follow: false);
            Schema.Trigger.Push(Board);
            Schema.InvalidateEnvironment();
        }

        public void WriteSeries(bool follow)
        {
            var p = Schema.Ports;
            using (Board.BeginTransaction())
            {
                Board.WriteIfChanged(p.Time, Slice(Data.Time));
                Board.WriteIfChanged(p.Open, Slice(Data.Open));
                Board.WriteIfChanged(p.High, Slice(Data.High));
                Board.WriteIfChanged(p.Low, Slice(Data.Low));
                Board.WriteIfChanged(p.Close, Slice(Data.Close));
                Board.WriteIfChanged(Schema.SmaPort, Slice(Data.Sma));
                if (Schema.Options.Markers)
                {
                    Board.WriteIfChanged(Schema.ScatterPort, Data.MarkersUpTo(Data.Scatter, Length));
                    Board.WriteIfChanged(Schema.ArrowPort, Data.MarkersUpTo(Data.Arrows, Length));
                    Board.WriteIfChanged(Schema.TextPort, Data.MarkersUpTo(Data.Texts, Length));
                }
                Board.WriteIfChanged(Schema.Viewport.LogicalLength, Length);
                if (follow)
                {
                    var active = Board.Read(Schema.Viewport.ActiveRange);
                    double span = active.IsValid ? active.Max - active.Min : 119;
                    Board.WriteIfChanged(Schema.Viewport.UserRange, new RealRange(Length - 1 - span, Length - 1));
                }
            }
        }

        public void Reset()
        {
            if (Length != InitialLength)
            {
                Length = InitialLength;
                WriteSeries(follow: false);
            }
            using (Board.AcquireWriteLock())
            {
                Board.WriteIfChanged(Schema.HitPortForProbe, null);
                Board.WriteIfChanged(Schema.Viewport.UserRange, new RealRange(Length - 120, Length - 1));
            }
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
        public double[] Volume = Array.Empty<double>();
        // 标记按 K 线下标升序:scatter 每 10 根一个、arrow 每 25 根、text 每 50 根
        public ScatterPoint[] Scatter = Array.Empty<ScatterPoint>();
        public ArrowMarker[] Arrows = Array.Empty<ArrowMarker>();
        public TextMarker[] Texts = Array.Empty<TextMarker>();

        /// <summary>前 length 根 K 线范围内的标记(各标记数组按下标升序,步长固定,直接算个数)。</summary>
        public ReadOnlyMemory<T> MarkersUpTo<T>(T[] markers, int length)
        {
            int step = markers switch { ScatterPoint[] => 10, ArrowMarker[] => 25, _ => 50 };
            return new ReadOnlyMemory<T>(markers, 0, Math.Min(markers.Length, (length + step - 1) / step));
        }

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
            // 成交量 / 标记由价格序列确定性派生,不消耗随机数(不改变已有序列,计数基线不受影响)
            d.Volume = new double[n];
            for (int i = 0; i < n; i++) d.Volume[i] = 1000 + Math.Abs(d.Close[i] - d.Open[i]) * 800 + (i % 7) * 40;
            d.Scatter = new ScatterPoint[(n + 9) / 10];
            for (int k = 0; k < d.Scatter.Length; k++)
                d.Scatter[k] = new ScatterPoint(k * 10, (float)d.High[k * 10] + 0.5f, 3f, "#FFB74D");
            d.Arrows = new ArrowMarker[(n + 24) / 25];
            for (int k = 0; k < d.Arrows.Length; k++)
            {
                int i = k * 25;
                bool up = d.Close[i] >= d.Open[i];
                d.Arrows[k] = new ArrowMarker(i, up ? d.Low[i] - 0.5 : d.High[i] + 0.5, up ? "up" : "down", up ? "#26A69A" : "#EA476D", 8f);
            }
            d.Texts = new TextMarker[(n + 49) / 50];
            for (int k = 0; k < d.Texts.Length; k++)
            {
                int i = k * 50;
                d.Texts[k] = new TextMarker(i, d.High[i] + 1.0, k % 2 == 0 ? "BUY" : "SELL", "#FFFFFF", 11f, "above");
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
        public ProbeChartOptions Options { get; }
        public ProbeKLineSchema(ProbeChartOptions? options = null) => Options = options ?? ProbeChartOptions.Default;

        public DataPort<ReadOnlyMemory<ScatterPoint>> ScatterPort { get; } = new("P_Scatter");
        public DataPort<ReadOnlyMemory<ArrowMarker>> ArrowPort { get; } = new("P_Arrows");
        public DataPort<ReadOnlyMemory<TextMarker>> TextPort { get; } = new("P_Texts");

        public CandlePorts Ports { get; } = new(new("P_Time"), new("P_Open"), new("P_High"), new("P_Low"), new("P_Close"));
        public DataPort<ReadOnlyMemory<double>> SmaPort { get; } = new("P_SMA20");
        public DataPort<RealRange> YRangePort { get; } = new("P_YRange");
        public WorkflowTrigger<DataBlackboard> Trigger { get; } = new();

        /// <summary>--heavy-layers / --heavy-ms:额外挂的人为加重图层(0 = 不挂)。</summary>
        public static int HeavyLayers;
        public static double HeavyMs = 1.0;

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
            if (HeavyLayers > 0) canvas.Add(new HeavyLayersFeature(HeavyLayers, HeavyMs));

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

            // HoverNoTip 对照组:EnableStandard 总会挂 TooltipWidgetFeature,这里摘掉
            if (!Options.Tooltip) canvas.Remove<TooltipWidgetFeature>();

            // dashboard 主图:scatter / arrow / text 三类标记(跟 PlotFeature 展开出来的子 Feature 同一批类)
            if (Options.Markers)
            {
                canvas.Add(new ScatterPlotFeature { Spec = ScatterPort, Name = "probe_scatter", HitStatePort = hitPort });
                canvas.Add(new ArrowMarkerFeature { Spec = ArrowPort, Name = "probe_arrows", YRangePort = YRangePort, HitStatePort = hitPort });
                canvas.Add(new TextMarkerFeature { Spec = TextPort, Name = "probe_texts", YRangePort = YRangePort, HitStatePort = hitPort });
            }
        }
    }
}
