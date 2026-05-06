using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using System.Linq;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §D1 Dashboard 蓝图协议 + DashboardLauncher 集成测试。
    /// 验证 dashboard 级 DryRun 校验(角色顺序 / Master 唯一 / Id 重复)+ per-cell 蓝图诊断聚合。
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class DashboardLauncherTests
    {
        public DashboardLauncherTests()
        {
            ComponentRegistry.Register<TestDataSource>();
        }

        // 空 dashboard / null cells / Cells 0 个 → Error
        [Fact]
        public void DryRun_NullDashboard_ReturnsError()
        {
            var result = DashboardLauncher.DryRun(null!);
            Assert.NotNull(result.Error);
            Assert.False(result.Launched);
        }

        [Fact]
        public void DryRun_EmptyCells_ReturnsError()
        {
            var dashboard = new Dashboard();
            var result = DashboardLauncher.DryRun(dashboard);
            Assert.NotNull(result.Error);
            Assert.Contains("至少需要一个 cell", result.Error);
        }

        // 第一个 cell 必须是 Master
        [Fact]
        public void DryRun_FirstCellNotMaster_ReturnsError()
        {
            var dashboard = new Dashboard
            {
                Cells = { MakeCell("pane1", DashboardCellRole.Pane), MakeCell("pane2", DashboardCellRole.Pane) },
            };
            var result = DashboardLauncher.DryRun(dashboard);
            Assert.NotNull(result.Error);
            Assert.Contains("必须是 Master", result.Error);
        }

        // Master 角色不能多于一个
        [Fact]
        public void DryRun_MultipleMasters_ReturnsError()
        {
            var dashboard = new Dashboard
            {
                Cells = { MakeCell("a", DashboardCellRole.Master), MakeCell("b", DashboardCellRole.Master) },
            };
            var result = DashboardLauncher.DryRun(dashboard);
            Assert.NotNull(result.Error);
            Assert.Contains("Master", result.Error);
        }

        // cell.Id 重复
        [Fact]
        public void DryRun_DuplicateCellIds_ReturnsError()
        {
            var dashboard = new Dashboard
            {
                Cells = { MakeCell("dup", DashboardCellRole.Master), MakeCell("dup", DashboardCellRole.Pane) },
            };
            var result = DashboardLauncher.DryRun(dashboard);
            Assert.NotNull(result.Error);
            Assert.Contains("重复", result.Error);
        }

        // cell.Id 必填
        [Fact]
        public void DryRun_EmptyCellId_ReturnsError()
        {
            var dashboard = new Dashboard
            {
                Cells = { MakeCell("", DashboardCellRole.Master) },
            };
            var result = DashboardLauncher.DryRun(dashboard);
            Assert.NotNull(result.Error);
            Assert.Contains("Id 为空", result.Error);
        }

        // HeightRatio <= 0 → 警告而非错(运行时退化但不阻止启动)
        [Fact]
        public void DryRun_NonPositiveHeightRatio_AddsWarning()
        {
            var c = MakeCell("main", DashboardCellRole.Master);
            c.HeightRatio = 0;
            var dashboard = new Dashboard { Cells = { c } };
            var result = DashboardLauncher.DryRun(dashboard);
            Assert.Null(result.Error);
            Assert.Contains(result.Diagnostics, d =>
                d.Code == "BP_DASHBOARD_BAD_HEIGHT" && d.FeatureTypeName == "Cell:main");
        }

        // K线-shaped: 1 master + 2 panes,蓝图都简单 valid → 0 error / 0 critical warning
        [Fact]
        public void DryRun_KLineShapedDashboard_Clean()
        {
            var dashboard = MakeKLineShapedDashboard();
            var result = DashboardLauncher.DryRun(dashboard);
            Assert.Null(result.Error);
        }

        // per-cell 诊断聚合:让某个 cell 引用未注册 Feature → BP_UNREGISTERED_TYPE 上报,FeatureTypeName 加 Cell 前缀
        [Fact]
        public void DryRun_BadCellBlueprint_AggregatesPerCellDiagnostic()
        {
            var dashboard = new Dashboard
            {
                Cells =
                {
                    MakeCell("main", DashboardCellRole.Master),
                    MakeCell("bad", DashboardCellRole.Pane, new ChartBlueprint
                    {
                        DataSource = new DataSourceModel { TypeName = nameof(TestDataSource) },
                        Features = { new FeatureModel { TypeName = "DefinitelyNotRegistered" } },
                    }),
                },
            };
            var result = DashboardLauncher.DryRun(dashboard);
            // BP_UNREGISTERED_TYPE 在 BlueprintLauncher.DryRun 里是 Error 严重度的诊断
            // → DashboardLauncher 把它升级成 result.Error 阻断启动,避免 silent skip 带来的黑屏
            Assert.NotNull(result.Error);
            Assert.Contains("Cell 'bad' 致命诊断", result.Error);
            Assert.Contains("BP_UNREGISTERED_TYPE", result.Error);
            // 诊断列表也应有 Cell 前缀的对应条目
            Assert.Contains(result.Diagnostics, d =>
                d.Code == "BP_UNREGISTERED_TYPE" && d.FeatureTypeName.Contains("Cell:bad"));
        }

        // ── 工具 ────────────────────────────────────────────────────────

        private static DashboardCell MakeCell(string id, DashboardCellRole role, ChartBlueprint? bp = null)
            => new DashboardCell
            {
                Id = id,
                Role = role,
                HeightRatio = role == DashboardCellRole.Master ? 3 : 1,
                Blueprint = bp ?? new ChartBlueprint
                {
                    DataSource = new DataSourceModel { TypeName = nameof(TestDataSource) },
                    Features =
                    {
                        new FeatureModel
                        {
                            TypeName = nameof(LineSeriesFeature),
                            PortBindings = new Dictionary<string, object?>
                            {
                                ["DataPort"]   = $"{id}_price",
                                ["YRangePort"] = $"{id}_yrange",
                            },
                        },
                    },
                },
            };

        private static Dashboard MakeKLineShapedDashboard() => new Dashboard
        {
            HorizontalLeftPixel = 60,
            HorizontalRightPixel = 0,
            Cells =
            {
                MakeCell("main",   DashboardCellRole.Master),
                MakeCell("volume", DashboardCellRole.Pane),
                MakeCell("macd",   DashboardCellRole.Pane),
            },
        };
    }
}
