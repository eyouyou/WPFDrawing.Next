using System.Collections.Generic;
using System.Windows.Media;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// PlotFeature 统一 series 协议单测(纯 mock,无真 Python)。
    /// 覆盖:
    /// 1. <see cref="PlotFeature.ParseScatterSpec"/> / <see cref="PlotFeature.ParseArrowSpec"/> 把
    ///    list[Dictionary&lt;string,object?&gt;] 解析成 ROM&lt;ScatterPoint/ArrowMarker&gt;
    /// 2. BlueprintLauncher.DryRun 对 PlotFeature.Series 数组的 per-spec 校验
    ///    (BP_PLOT_KIND_UNKNOWN / BP_PLOT_DOMAIN_MISMATCH)
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class PlotFeatureSeriesTests
    {
        // ── 1. spec 解析 ───────────────────────────────────────────────────────

        [Fact]
        public void ParseScatterSpec_FromListOfDict_ExtractsRenderFields()
        {
            // hover_text / click_payload 字段存在但被忽略(framework 不绑业务 metadata,业务侧按 Index 自查)
            var raw = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["x"]             = 0.05,
                    ["y"]             = 8.5,
                    ["size"]          = 4.5,
                    ["color"]         = "#FF4488CC",
                    ["hover_text"]    = "ignored",
                    ["click_payload"] = new Dictionary<string, object?> { ["k"] = 1 },
                },
                new Dictionary<string, object?>
                {
                    ["x"]             = -0.03,
                    ["y"]             = 6.0,
                    ["size"]          = 2.0,
                    ["color"]         = "#FF",
                },
            };

            var spec = PlotFeature.ParseScatterSpec(raw);
            Assert.Equal(2, spec.Length);
            var span = spec.Span;

            Assert.Equal(0.05f, span[0].X, 5);
            Assert.Equal(8.5f, span[0].Y, 5);
            Assert.Equal(4.5f, span[0].Size, 5);
            Assert.Equal("#FF4488CC", span[0].Color);
            Assert.Equal(-0.03f, span[1].X, 5);
        }

        [Fact]
        public void ParseScatterSpec_NonDictElements_AreSkipped()
        {
            var raw = new object?[]
            {
                "noise",
                null,
                new Dictionary<string, object?>
                {
                    ["x"] = 1.0, ["y"] = 2.0, ["size"] = 3.0, ["color"] = "#FFFFFF", ["hover_text"] = "ok",
                },
            };
            var spec = PlotFeature.ParseScatterSpec(raw);
            Assert.Single(spec.ToArray());
            Assert.Equal(1.0f, spec.Span[0].X, 5);
        }

        [Fact]
        public void ParseScatterSpec_EmptyArray_ReturnsEmpty()
        {
            Assert.Equal(0, PlotFeature.ParseScatterSpec(System.Array.Empty<object?>()).Length);
        }

        [Fact]
        public void ParseArrowSpec_FromListOfDict_ExtractsRenderFields()
        {
            var raw = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["logical_x"] = 12.0,
                    ["logical_y"] = 105.5,
                    ["direction"] = "down",
                    ["color"]     = "#4CAF50",
                    ["size"]      = 8.0,
                },
            };
            var spec = PlotFeature.ParseArrowSpec(raw);
            Assert.Single(spec.ToArray());
            var m = spec.Span[0];
            Assert.Equal(12.0, m.LogicalX, 5);
            Assert.Equal(105.5, m.LogicalY, 5);
            Assert.Equal("down", m.Direction);
            Assert.Equal("#4CAF50", m.Color);
            Assert.Equal(8.0f, m.Size, 5);
        }

        // ── 2. DryRun PlotFeature.Series per-spec 校验 ──────────────────────────

        [Fact]
        public void DryRun_PlotKindUnknown_ProducesError()
        {
            var bp = MakePlotBlueprint(
                new PlotSeriesSpec(Name: "demo", Kind: "scatterz", Color: Colors.White, Width: 0));
            var r = BlueprintLauncher.DryRun(bp);
            Assert.Contains(r.Diagnostics, d =>
                d.Severity == BlueprintDiagnosticSeverity.Error &&
                d.Code == "BP_PLOT_KIND_UNKNOWN" &&
                d.FeatureTypeName == nameof(PlotFeature));
        }

        [Fact]
        public void DryRun_ArrowMarkersWithDomain_ProducesMismatchWarning()
        {
            // arrow_markers 永远 inherit,不应有 X/YDomain
            var bp = MakePlotBlueprint(
                new PlotSeriesSpec(
                    Name: "demo",
                    Kind: "arrow_markers",
                    Color: Colors.LimeGreen,
                    Width: 0,
                    XDomain: new RealRange(0, 1),
                    YDomain: new RealRange(0, 1)));
            var r = BlueprintLauncher.DryRun(bp);
            Assert.Contains(r.Diagnostics, d =>
                d.Severity == BlueprintDiagnosticSeverity.Warning &&
                d.Code == "BP_PLOT_DOMAIN_MISMATCH");
        }

        [Fact]
        public void DryRun_ValidScatterWithDomain_NoPlotIssues()
        {
            var bp = MakePlotBlueprint(
                new PlotSeriesSpec(
                    Name: "demo",
                    Kind: "scatter",
                    Color: Colors.White,
                    Width: 0,
                    XDomain: new RealRange(-1, 1),
                    YDomain: new RealRange(0, 10)));
            var r = BlueprintLauncher.DryRun(bp);
            Assert.DoesNotContain(r.Diagnostics, d => d.Code.StartsWith("BP_PLOT_"));
        }

        [Fact]
        public void DryRun_PlotEmptySeries_NoChecks()
        {
            // Series 字段不存在 / 空 → 跳过 per-spec 校验(IndicatorMetadataRegistry 路径,也合法)
            BlueprintTestFixture.EnsureTestDataSourceRegistered();
            var feature = new FeatureModel
            {
                TypeName = nameof(PlotFeature),
                Properties =
                {
                    ["IndicatorName"] = "some_series_indicator",
                },
            };
            var bp = new ChartBlueprint
            {
                DataSources = { new DataSourceModel { Id = "primary", TypeName = nameof(TestDataSource) } },
                Features = { feature },
            };
            var r = BlueprintLauncher.DryRun(bp);
            Assert.DoesNotContain(r.Diagnostics, d => d.Code.StartsWith("BP_PLOT_"));
        }

        [Fact]
        public void DryRun_MixedSeriesKinds_OnlyInvalidFlagged()
        {
            var bp = MakePlotBlueprint(
                new PlotSeriesSpec(Name: "close",  Kind: "line",          Color: Colors.SteelBlue, Width: 1),
                new PlotSeriesSpec(Name: "buys",   Kind: "arrow_markers", Color: Colors.LimeGreen, Width: 0),
                new PlotSeriesSpec(Name: "scatter1", Kind: "scatter",     Color: Colors.White,    Width: 0),
                new PlotSeriesSpec(Name: "garbage", Kind: "weirdo",       Color: Colors.Black,    Width: 0));
            var r = BlueprintLauncher.DryRun(bp);
            Assert.Contains(r.Diagnostics, d =>
                d.Code == "BP_PLOT_KIND_UNKNOWN" &&
                d.PortName == "Series[3].Kind");
            // 前 3 个不应触发 BP_PLOT_KIND_UNKNOWN
            int unknownCount = 0;
            foreach (var d in r.Diagnostics)
                if (d.Code == "BP_PLOT_KIND_UNKNOWN") unknownCount++;
            Assert.Equal(1, unknownCount);
        }

        // ── helpers ────────────────────────────────────────────────────────────

        private static ChartBlueprint MakePlotBlueprint(params PlotSeriesSpec[] series)
        {
            BlueprintTestFixture.EnsureTestDataSourceRegistered();
            var feature = new FeatureModel
            {
                TypeName = nameof(PlotFeature),
                Properties =
                {
                    ["IndicatorName"] = "demo_indicator",
                    ["Series"]        = series,
                },
            };
            return new ChartBlueprint
            {
                DataSources = { new DataSourceModel { Id = "primary", TypeName = nameof(TestDataSource) } },
                Features = { feature },
            };
        }
    }
}
