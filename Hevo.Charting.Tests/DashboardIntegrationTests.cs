using Hevo.Charting.Features;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.Converters;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using System.Text.Json;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §D1.5 集成 / 回归测试。
    /// 覆盖：
    ///   1) Dashboard JSON round-trip（序列化 ↔ 反序列化 cell 结构保持一致）
    ///   2) 三 cell 端到端 DryRun（主图 + 量副 + MA 指标型 cell）
    ///   3) GraphSerializer ↔ GraphDeserializer 对 Dashboard 中每个 cell 的 round-trip
    ///   4) DashboardWorkspace 状态到 Dashboard JSON 再还原（无 WPF 渲染依赖）
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class DashboardIntegrationTests
    {
        private static readonly JsonSerializerOptions _json = BlueprintJsonOptions.Default;

        public DashboardIntegrationTests()
        {
            ComponentRegistry.Register<TestDataSource>();
        }

        // ── D1.5-1: Dashboard JSON round-trip ────────────────────────────────

        [Fact]
        public void DashboardJson_RoundTrip_PreservesCellCount()
        {
            var d = MakeThreeCellDashboard();
            var json = JsonSerializer.Serialize(d, _json);
            var d2 = JsonSerializer.Deserialize<Dashboard>(json, _json)!;

            Assert.Equal(d.Cells.Count, d2.Cells.Count);
        }

        [Fact]
        public void DashboardJson_RoundTrip_PreservesCellIds()
        {
            var d = MakeThreeCellDashboard();
            var json = JsonSerializer.Serialize(d, _json);
            var d2 = JsonSerializer.Deserialize<Dashboard>(json, _json)!;

            var ids1 = d.Cells.Select(c => c.Id).ToArray();
            var ids2 = d2.Cells.Select(c => c.Id).ToArray();
            Assert.Equal(ids1, ids2);
        }

        [Fact]
        public void DashboardJson_RoundTrip_PreservesRolesAndHeightRatios()
        {
            var d = MakeThreeCellDashboard();
            var json = JsonSerializer.Serialize(d, _json);
            var d2 = JsonSerializer.Deserialize<Dashboard>(json, _json)!;

            for (int i = 0; i < d.Cells.Count; i++)
            {
                Assert.Equal(d.Cells[i].Role,        d2.Cells[i].Role);
                Assert.Equal(d.Cells[i].HeightRatio, d2.Cells[i].HeightRatio);
            }
        }

        [Fact]
        public void DashboardJson_RoundTrip_PreservesHorizontalMargins()
        {
            var d = MakeThreeCellDashboard();
            d.HorizontalLeftPixel  = 72;
            d.HorizontalRightPixel = 12;

            var json = JsonSerializer.Serialize(d, _json);
            var d2 = JsonSerializer.Deserialize<Dashboard>(json, _json)!;

            Assert.Equal(72, d2.HorizontalLeftPixel);
            Assert.Equal(12, d2.HorizontalRightPixel);
        }

        // ── D1.5-2: 三 cell DryRun 端到端 ───────────────────────────────────

        [Fact]
        public void ThreeCellDashboard_DryRun_ReturnsClean()
        {
            // 主图 (Master) + 量副 (Pane) + MA 指标副 (Pane) — 结构合法
            var d = MakeThreeCellDashboard();
            var result = DashboardLauncher.DryRun(d);

            Assert.Null(result.Error);
            // 可以有 HeightRatio 警告等，但不应有 Error 级别诊断
            Assert.DoesNotContain(result.Diagnostics,
                diag => diag.Severity == BlueprintDiagnosticSeverity.Error);
        }

        [Fact]
        public void ThreeCellDashboard_DryRun_MasterIsFirst()
        {
            var d = MakeThreeCellDashboard();
            Assert.Equal(DashboardCellRole.Master, d.Cells[0].Role);

            var result = DashboardLauncher.DryRun(d);
            Assert.Null(result.Error);
        }

        [Fact]
        public void ThreeCellDashboard_CellIds_AreUnique()
        {
            var d = MakeThreeCellDashboard();
            var ids = d.Cells.Select(c => c.Id).ToArray();
            Assert.Equal(ids.Length, ids.Distinct().Count());
        }

        // ── D1.5-3: per-cell blueprint GraphState round-trip ─────────────────

        [Fact]
        public void ThreeCellDashboard_PerCell_BlueprintRoundTrip_PreservesFeatureSet()
        {
            var d = MakeThreeCellDashboard();

            foreach (var cell in d.Cells)
            {
                var bp = cell.Blueprint;
                var state = GraphDeserializer.FromBlueprint(bp);
                var bp2 = GraphSerializer.ToBlueprint(state);

                var names1 = bp.Features.Select(f => f.TypeName).OrderBy(n => n).ToArray();
                var names2 = bp2.Features.Select(f => f.TypeName).OrderBy(n => n).ToArray();
                Assert.Equal(names1, names2);
            }
        }

        [Fact]
        public void ThreeCellDashboard_PerCell_BlueprintRoundTrip_PreservesDataSourceType()
        {
            var d = MakeThreeCellDashboard();

            foreach (var cell in d.Cells)
            {
                var bp = cell.Blueprint;
                if (bp.DataSource == null) continue;

                var state = GraphDeserializer.FromBlueprint(bp);
                var bp2 = GraphSerializer.ToBlueprint(state);

                Assert.Equal(bp.DataSource.TypeName, bp2.DataSource?.TypeName);
            }
        }

        // ── D1.5-4: Dashboard-level JSON ↔ per-cell blueprint ────────────────

        [Fact]
        public void Dashboard_JsonThenDeserialize_PerCellBlueprintsMatchOriginal()
        {
            var d = MakeThreeCellDashboard();
            var json = JsonSerializer.Serialize(d, _json);
            var d2 = JsonSerializer.Deserialize<Dashboard>(json, _json)!;

            for (int i = 0; i < d.Cells.Count; i++)
            {
                var bp1 = d.Cells[i].Blueprint;
                var bp2 = d2.Cells[i].Blueprint;

                // DataSource type preserved
                Assert.Equal(bp1.DataSource?.TypeName, bp2.DataSource?.TypeName);

                // Feature type names preserved
                Assert.Equal(
                    bp1.Features.Select(f => f.TypeName).OrderBy(x => x),
                    bp2.Features.Select(f => f.TypeName).OrderBy(x => x));
            }
        }

        // ── D1.5-5: Error propagation ────────────────────────────────────────

        [Fact]
        public void ThreeCellDashboard_PaneBeforeMaster_ReturnsError()
        {
            var d = MakeThreeCellDashboard();
            // swap: put a Pane first to violate "Master must be [0]" invariant
            var master = d.Cells[0];
            d.Cells.RemoveAt(0);
            d.Cells.Add(master);

            var result = DashboardLauncher.DryRun(d);
            Assert.NotNull(result.Error);
            Assert.Contains("Master", result.Error);
        }

        [Fact]
        public void ThreeCellDashboard_BadCellBlueprint_DiagnosticsContainCellPrefix()
        {
            var d = MakeThreeCellDashboard();
            // inject a bad feature into the "volume" cell
            d.Cells[1].Blueprint.Features.Add(new FeatureModel { TypeName = "NoSuchFeature_xyz" });

            var result = DashboardLauncher.DryRun(d);
            Assert.NotNull(result.Error);
            Assert.Contains("Cell 'volume'", result.Error);
        }

        // ── sample builder ────────────────────────────────────────────────────

        /// <summary>
        /// 三 cell 样本：主图（K线 shape）+ 量副 + MA 指标副。
        /// 蓝图故意保持最小：只含 DataSource + 一个 Feature，够 DryRun 过关即可。
        /// 真实 K线 schema 远比这复杂；D1.5 的目标是协议层端到端，不是像素正确性。
        /// </summary>
        private static Dashboard MakeThreeCellDashboard() => new Dashboard
        {
            HorizontalLeftPixel  = 60,
            HorizontalRightPixel = 0,
            Cells =
            {
                // [0] 主图 Master
                new DashboardCell
                {
                    Id          = "main",
                    Role        = DashboardCellRole.Master,
                    HeightRatio = 3,
                    Blueprint   = new ChartBlueprint
                    {
                        DataSource = new DataSourceModel { TypeName = nameof(TestDataSource) },
                        Features   =
                        {
                            new FeatureModel
                            {
                                TypeName     = nameof(LineSeriesFeature),
                                PortBindings = new Dictionary<string, object?> { ["DataPort"] = "main_price" },
                            },
                        },
                    },
                },

                // [1] 量副 Pane（通量 / volume bar）
                new DashboardCell
                {
                    Id          = "volume",
                    Role        = DashboardCellRole.Pane,
                    HeightRatio = 1,
                    Blueprint   = new ChartBlueprint
                    {
                        DataSource = new DataSourceModel { TypeName = nameof(TestDataSource) },
                        Features   =
                        {
                            new FeatureModel
                            {
                                TypeName     = nameof(BarSeriesFeature),
                                PortBindings = new Dictionary<string, object?> { ["DataPort"] = "vol_value" },
                            },
                        },
                    },
                },

                // [2] MA 指标副 Pane（简化为 LineSeriesFeature 代替真实 ComputeNode 指标）
                new DashboardCell
                {
                    Id          = "ma_indicator",
                    Role        = DashboardCellRole.Pane,
                    HeightRatio = 1,
                    Blueprint   = new ChartBlueprint
                    {
                        DataSource = new DataSourceModel { TypeName = nameof(TestDataSource) },
                        Features   =
                        {
                            new FeatureModel
                            {
                                TypeName     = nameof(LineSeriesFeature),
                                PortBindings = new Dictionary<string, object?> { ["DataPort"] = "ma_value" },
                            },
                        },
                    },
                },
            },
        };
    }
}
