using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using Hevo.Charting.Core;
using Hevo.Charting.DevTools;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.Converters;
using Hevo.Charting.LowCode.Designer.GraphViewer;

namespace Hevo.Charting.Benchmarks
{
    /// <summary>
    /// 蓝图端到端:LowCodeDemo 的 default_kline_dashboard.json(主图 + 成交量 + 策略三张联动图)
    /// → DashboardLauncher.LaunchEx(DryRun + 实例化数据源 / Feature + 装配 LinkedChartDashboard)→ 放进窗口
    /// → 等每张图都在数据到达后出过一帧。数据源用 <see cref="ProbeFeedSource"/> 以别名 MockKLineDataSource 顶替
    /// (Benchmarks 不引用 LowCodeDemo)。进程内第 1 次是冷启动(含 JIT),之后热启动。
    /// </summary>
    internal static class BlueprintProbe
    {
        internal sealed record Run(int Index, double LaunchMs, double ShowMs, double FirstFrameMs, double TotalMs, int Cells, int Layers, int Warnings, string? Error);

        public static SuiteResult RunBlueprint(ProbeOptions opt)
        {
            GraphViewerBootstrap.Initialize();
            ComponentRegistry.Register(typeof(ProbeFeedSource), "MockKLineDataSource");
            var json = File.ReadAllText(Path.Combine(RepoRoot(), "Hevo.Drawing.LowCodeDemo", "Assets", "default_kline_dashboard.json"));

            // strategy 图的 PlotFeature 引用 Python handler(bb_breakout):跟 LowCodeDemo 一样把 PyIndicators/*.py 注册进
            // Python handler registry,按 cell 传给 LaunchEx。Python 不可用时整套跳过(没有 handler 装配会直接抛)。
            var py = ProbePython.Shared;
            if (!py.Available)
                return new SuiteResult("Blueprint", "\n### Blueprint\n\nPython 不可用(strategy 图需要 bb_breakout handler),跳过。\n", new { skipped = true }, "");
            foreach (var f in Directory.GetFiles(Path.Combine(RepoRoot(), "Hevo.Drawing.LowCodeDemo", "PyIndicators"), "*.py"))
                File.Copy(f, Path.Combine(py.IndicatorsDir, Path.GetFileName(f)), overwrite: true);
            py.Registry.AutoDiscoverDirectory(py.IndicatorsDir);

            var runs = new List<Run>();
            for (int i = 0; i < 1 + opt.BlueprintWarmRuns; i++)
            {
                runs.Add(LaunchOnce(opt, json, i, py.Registry));
                var r = runs[^1];
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[blueprint] #{i}{(i == 0 ? "(冷)" : "")}:LaunchEx {r.LaunchMs:F1} ms + 显示 {r.ShowMs:F1} ms + 首帧 {r.FirstFrameMs:F1} ms = {r.TotalMs:F1} ms") +
                    (r.Error != null ? $" 失败:{r.Error}" : ""));
            }

            var md = new StringBuilder();
            md.AppendLine();
            md.AppendLine("### Blueprint(蓝图端到端:default_kline_dashboard.json → DashboardLauncher.LaunchEx → 窗口 → 每张图首帧)");
            md.AppendLine();
            md.AppendLine("| 次序 | LaunchEx(DryRun+实例化+装配)(ms) | 显示 + 布局(ms) | 数据到达 → 全部首帧(ms) | 合计(ms) | 图数 | 图层数 | DryRun 警告 |");
            md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var r in runs)
                md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {(r.Index == 0 ? "冷启动" : $"热 #{r.Index}")} | {r.LaunchMs:F1} | {r.ShowMs:F1} | {r.FirstFrameMs:F1} | {r.TotalMs:F1} | {r.Cells} | {r.Layers} | {r.Warnings} |") +
                    (r.Error != null ? $" 失败:{r.Error}" : ""));
            var warm = runs.Skip(1).Where(r => r.Error == null).ToList();
            if (warm.Count > 0)
            {
                md.AppendLine();
                md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"热启动中位数:LaunchEx {Stats.Percentile(warm.Select(r => r.LaunchMs), 0.5):F1} ms,合计 {Stats.Percentile(warm.Select(r => r.TotalMs), 0.5):F1} ms。") +
                    "strategy 图的 bb_breakout 是 LowCodeDemo/PyIndicators 里的真 Python handler,首帧时间含它第一次计算。");
            }
            var csv = new StringBuilder("index,launch_ms,show_ms,first_frame_ms,total_ms,cells,layers,warnings,error\n");
            foreach (var r in runs)
                csv.Append(string.Create(CultureInfo.InvariantCulture,
                    $"{r.Index},{r.LaunchMs:F3},{r.ShowMs:F3},{r.FirstFrameMs:F3},{r.TotalMs:F3},{r.Cells},{r.Layers},{r.Warnings},{r.Error}\n"));
            return new SuiteResult("Blueprint", md.ToString(), runs, csv.ToString());
        }

        private static Run LaunchOnce(ProbeOptions opt, string json, int index, BlueprintHandlerRegistry handlers)
        {
            var dashboard = JsonSerializer.Deserialize<Dashboard>(json, BlueprintJsonOptions.Default)!;
            var options = new DashboardLaunchOptions { Handlers = dashboard.Cells.ToDictionary(c => c.Id, _ => handlers) };
            var sources = new List<ProbeFeedSource>();
            void OnCreated(ProbeFeedSource s) { lock (sources) sources.Add(s); }
            ProbeFeedSource.Created += OnCreated;

            var rendered = new Dictionary<ChartCell, List<long>>();
            IncrementalRenderProbe.FrameRendered = cell =>
            {
                if (!rendered.TryGetValue(cell, out var list)) rendered[cell] = list = new List<long>();
                list.Add(Stopwatch.GetTimestamp());
            };

            Window? window = null;
            try
            {
                long t0 = Stopwatch.GetTimestamp();
                var result = DashboardLauncher.LaunchEx(dashboard, options);
                long t1 = Stopwatch.GetTimestamp();
                if (result.Dashboard == null)
                    return new Run(index, Ms(t0, t1), 0, 0, Ms(t0, t1), 0, 0, result.Diagnostics.Count, result.Error ?? "Dashboard 为 null");

                var dash = result.Dashboard;
                dash.Width = opt.WindowWidth;
                dash.Height = opt.WindowHeight;
                dash.IsHitTestVisible = false; // 不让真实鼠标位置影响首帧(悬停会多算十字光标 / tooltip)
                window = new Window
                {
                    Title = "Hevo render probe (blueprint)",
                    SizeToContent = SizeToContent.WidthAndHeight,
                    Content = dash,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };
                window.Show();
                window.UpdateLayout();
                long t2 = Stopwatch.GetTimestamp();

                // 等:每张图的数据源 fetch 完,且该图在自己的数据到达之后出过一帧。
                // DashboardLauncher 按 cell 顺序实例化数据源,sources[i] 就是 cells[i] 的数据源。
                var cells = dash.Cells;
                long deadline = t2 + Stopwatch.Frequency * 15;
                long done = 0;
                long lastPump = Stopwatch.GetTimestamp();
                double maxStallMs = 0;
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    RenderProbe.Pump(5);
                    long nowPump = Stopwatch.GetTimestamp();
                    maxStallMs = Math.Max(maxStallMs, Ms(lastPump, nowPump) - 5);
                    lastPump = nowPump;
                    long[] fetched;
                    lock (sources) fetched = sources.Select(x => x.FetchedAt).ToArray();
                    if (fetched.Length < cells.Count || fetched.Any(f => f == 0)) continue;
                    var firstAfter = cells.Select((c, i) =>
                        rendered.TryGetValue(c, out var list) ? list.FirstOrDefault(t => t > fetched[i]) : 0).ToArray();
                    if (firstAfter.All(t => t != 0))
                    {
                        done = firstAfter.Max();
                        break;
                    }
                }
                int layers = cells.Sum(c => c.ActiveLayers.Count);
                if (Environment.GetEnvironmentVariable("HEVO_PROBE_DEBUG") != null)
                {
                    lock (sources)
                        Console.WriteLine("[debug] fetch(ms after show): " + string.Join(", ", sources.Select(x => x.FetchedAt == 0 ? "未完成" : Ms(t2, x.FetchedAt).ToString("F0"))));
                    Console.WriteLine("[debug] 各图帧(ms after show): " + string.Join(" | ", cells.Select(c => rendered.TryGetValue(c, out var l) ? string.Join(",", l.Select(t => Ms(t2, t).ToString("F0"))) : "无")));
                    Console.WriteLine($"[debug] UI 线程最长卡顿 {maxStallMs:F0} ms");
                }
                if (done == 0)
                    return new Run(index, Ms(t0, t1), Ms(t1, t2), double.NaN, double.NaN, cells.Count, layers, result.Diagnostics.Count, "15 秒内没等到所有图出首帧");
                return new Run(index, Ms(t0, t1), Ms(t1, t2), Ms(t2, done), Ms(t0, done), cells.Count, layers, result.Diagnostics.Count, null);
            }
            finally
            {
                IncrementalRenderProbe.FrameRendered = null;
                ProbeFeedSource.Created -= OnCreated;
                window?.Close();
                RenderProbe.Pump(50);
            }
        }

        private static double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;

        internal static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Hevo.Drawing.slnx"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根(Hevo.Drawing.slnx)");
        }
    }
}
