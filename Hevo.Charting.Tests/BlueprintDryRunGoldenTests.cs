using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.Converters;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// DryRun 诊断输出的金标准快照:一组蓝图覆盖全部诊断码(未登记类型 / 端口类型冲突 / Output 未焊接 /
    /// Output 未接 AutoScale / 多输入 InputOrder / IncrementalCompute handler / Plot series / Delegate handler / Trigger),
    /// 外加 50 Feature 基准蓝图和 demo dashboard 资产。断言 (Severity, Code, Feature, Port, Message) 序列逐条一致,
    /// 包括顺序。快照由优化前的实现生成,用来锁住 DryRun 性能改造不改变任何诊断。
    /// <para>诊断规则有意变更时重新生成:设环境变量 <c>HEVO_DRYRUN_GOLDEN_WRITE=1</c> 跑一次本测试,检查 diff 后提交。</para>
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class BlueprintDryRunGoldenTests
    {
        [Fact]
        public void DryRun_Diagnostics_MatchGoldenSnapshot()
        {
            var actual = new StringBuilder();
            foreach (var (name, bp, handlers) in Cases())
            {
                var r = BlueprintLauncher.DryRun(bp, handlers);
                actual.Append("## ").Append(name).Append('\n');
                if (r.Error != null) actual.Append("ERROR ").Append(r.Error).Append('\n');
                foreach (var d in r.Diagnostics)
                    actual.Append(d.Severity).Append(" | ").Append(d.Code).Append(" | ").Append(d.FeatureTypeName)
                          .Append(" | ").Append(d.PortName).Append(" | ").Append(d.Message).Append('\n');
            }

            var path = Path.Combine(RepoRoot(), "Hevo.Charting.Tests", "BlueprintDryRunGolden.txt");
            if (Environment.GetEnvironmentVariable("HEVO_DRYRUN_GOLDEN_WRITE") == "1")
            {
                File.WriteAllText(path, actual.ToString(), new UTF8Encoding(false));
                return;
            }
            Assert.True(File.Exists(path), $"缺少快照 {path}");
            Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), actual.ToString());
        }

        private static IEnumerable<(string Name, ChartBlueprint Bp, BlueprintHandlerRegistry? Handlers)> Cases()
        {
            BlueprintTestFixture.EnsureTestDataSourceRegistered();

            // 1. 覆盖全部诊断码的混合蓝图
            var handlers = new BlueprintHandlerRegistry()
                .RegisterFetch("on_heartbeat", (_, _) => Task.FromResult(true));
            handlers.RegisterDelegate("wrong_handler",
                new Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>(close => close));

            var mixed = new ChartBlueprint
            {
                DataSources =
                {
                    new DataSourceModel
                    {
                        Id = "primary",
                        TypeName = nameof(TestDataSource),
                        OutputBindings = new Dictionary<string, object?> { ["Value"] = "ds_v" },
                    },
                },
                InitialTraits = { new StyleModel { TraitTypeName = "NoSuchTrait_Golden" } },
                Triggers =
                {
                    new TriggerModel { Kind = "Cron", IntervalSeconds = 1.0, Handler = "on_cron" },
                    new TriggerModel { Kind = "Interval", IntervalSeconds = 0, Handler = "on_zero" },
                    new TriggerModel { Kind = "Interval", IntervalSeconds = 1.0, Handler = "on_heartbeat" },
                    new TriggerModel { Kind = "Interval", IntervalSeconds = 1.0, Handler = "on_missing" },
                },
                Features =
                {
                    new FeatureModel { TypeName = "TotallyMadeUpFeature_Golden" },
                    // 端口类型冲突:同一 id 先作 RealRange 再作 ROM<double>
                    new FeatureModel
                    {
                        TypeName = nameof(LineSeriesFeature),
                        InputBindings = new Dictionary<string, object?> { [nameof(LineSeriesFeature.YRangePort)] = "shared_id" },
                    },
                    new FeatureModel
                    {
                        TypeName = nameof(LineSeriesFeature),
                        InputBindings = new Dictionary<string, object?> { [nameof(LineSeriesFeature.DataPort)] = "shared_id" },
                    },
                    // Output 接到 LineSeries 但没进 AutoScale.ValuePorts → BP_OUTPUT_NOT_SCALED
                    new FeatureModel
                    {
                        TypeName = nameof(ComputeFeature),
                        Properties = new Dictionary<string, object?> { ["InputOrder"] = new[] { "high", "low", "close" } },
                        InputBindings = new Dictionary<string, object?> { ["Inputs.high"] = "ds_v" },
                        OutputBindings = new Dictionary<string, object?> { [nameof(ComputeFeature.OutputPort)] = "indicator_out" },
                    },
                    new FeatureModel
                    {
                        TypeName = nameof(LineSeriesFeature),
                        InputBindings = new Dictionary<string, object?> { [nameof(LineSeriesFeature.DataPort)] = "indicator_out" },
                    },
                    new FeatureModel
                    {
                        TypeName = nameof(UniversalAutoScaleFeature),
                        InputBindings = new Dictionary<string, object?>
                        {
                            [nameof(UniversalAutoScaleFeature.ValuePorts)] = new List<string> { "RealTime_Price", "other_scaled" },
                        },
                    },
                    // 多输入:有 nested key 但没 InputOrder
                    new FeatureModel
                    {
                        TypeName = nameof(ComputeFeature),
                        InputBindings = new Dictionary<string, object?> { ["Inputs.x"] = "ds_v" },
                        OutputBindings = new Dictionary<string, object?> { [nameof(ComputeFeature.OutputPort)] = "out2" },
                    },
                    // IncrementalCompute handler 返回值不是 ValueTuple
                    new FeatureModel
                    {
                        TypeName = nameof(IncrementalComputeFeature),
                        Properties = new Dictionary<string, object?> { ["Compute"] = "wrong_handler" },
                        InputBindings = new Dictionary<string, object?> { [nameof(IncrementalComputeFeature.InputPort)] = "candle_close" },
                        OutputBindings = new Dictionary<string, object?> { [nameof(IncrementalComputeFeature.OutputPort)] = "ind" },
                    },
                    // Plot series:未知 kind + arrow_markers 配了 domain
                    new FeatureModel
                    {
                        TypeName = nameof(PlotFeature),
                        Properties =
                        {
                            ["IndicatorName"] = "golden",
                            ["Series"] = new[]
                            {
                                new PlotSeriesSpec(Name: "a", Kind: "scatterz", Color: Colors.White, Width: 0),
                                new PlotSeriesSpec(Name: "b", Kind: "arrow_markers", Color: Colors.White, Width: 0,
                                                   XDomain: new RealRange(0, 1), YDomain: new RealRange(0, 1)),
                            },
                        },
                    },
                    // Delegate 属性引用未注册 handler
                    new FeatureModel
                    {
                        TypeName = nameof(DataPagingFeature),
                        Properties = new Dictionary<string, object?> { ["OnRequireDataAsync"] = "on_require_paging_data" },
                    },
                },
            };
            yield return ("mixed", mixed, handlers);
            yield return ("mixed-no-handlers", mixed, null);

            // 2. 常见内置 Feature 各一个、全部不焊 → 每个 Output 端口按属性顺序报 BP_OUTPUT_PORT_UNBOUND
            var all = new ChartBlueprint { DataSources = { new DataSourceModel { Id = "primary", TypeName = nameof(TestDataSource) } } };
            foreach (var t in new[]
            {
                typeof(LineSeriesFeature), typeof(AxisFeature), typeof(GridLayoutFeature), typeof(PlotAreaDecorFeature),
                typeof(UniversalAutoScaleFeature), typeof(ChartInteractionFeature), typeof(BarSeriesFeature),
                typeof(ViewportManagerFeature), typeof(UniversalHeaderFeature), typeof(DataPagingFeature),
                typeof(ComputeFeature), typeof(IncrementalComputeFeature), typeof(PlotFeature),
            })
                all.Features.Add(new FeatureModel { TypeName = t.Name, InputBindings = new Dictionary<string, object?>() });
            yield return ("builtin-unbound", all, null);

            // 3. demo dashboard 资产里的每张图
            var json = File.ReadAllText(Path.Combine(RepoRoot(), "Hevo.Drawing.LowCodeDemo", "Assets", "default_kline_dashboard.json"));
            var dashboard = JsonSerializer.Deserialize<Dashboard>(json, BlueprintJsonOptions.Default)!;
            foreach (var cell in dashboard.Cells)
            {
                // demo 的 MockKLineDataSource 只在 LowCodeDemo 里登记,换成测试 DS,让 Feature 校验跑完整
                foreach (var ds in cell.Blueprint.DataSources) ds.TypeName = nameof(TestDataSource);
                yield return ($"dashboard:{cell.Id}", cell.Blueprint, null);
            }
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Hevo.Drawing.slnx"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
