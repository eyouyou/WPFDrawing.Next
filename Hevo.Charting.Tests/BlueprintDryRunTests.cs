using Hevo.Charting.Features;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §5 蓝图 dry-run 早期诊断回归。
    /// 每条用例构造"刚刚好坏"的蓝图,断言相应诊断 code 出现。
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class BlueprintDryRunTests
    {
        [Fact]
        public void DryRun_NullBlueprint_ReturnsError()
        {
            var r = BlueprintLauncher.DryRun(null!);
            Assert.NotNull(r.Error);
        }

        [Fact]
        public void DryRun_MissingDataSource_ReturnsError()
        {
            var bp = new ChartBlueprint();
            var r = BlueprintLauncher.DryRun(bp);
            Assert.NotNull(r.Error);
            Assert.Contains("DataSource", r.Error);
        }

        [Fact]
        public void DryRun_UnregisteredFeatureType_ProducesErrorDiagnostic()
        {
            var bp = MakeMinimalBlueprint();
            bp.Features.Add(new FeatureModel { TypeName = "TotallyMadeUpFeature_DoesNotExist" });

            var r = BlueprintLauncher.DryRun(bp);
            // DryRun 不一定中断 (Launcher 仍想跑下去),但应有 Error 级别诊断。
            Assert.Contains(r.Diagnostics, d =>
                d.Severity == BlueprintDiagnosticSeverity.Error &&
                d.Code == "BP_UNREGISTERED_TYPE" &&
                d.FeatureTypeName == "TotallyMadeUpFeature_DoesNotExist");
        }

        [Fact]
        public void DryRun_OutputPortMissingBinding_ProducesWarning()
        {
            // UniversalAutoScaleFeature.YRangePort 是 [PortDirection(Output)],蓝图必须焊接它否则
            // 下游 Watch 拿到 null → NRE → 黑屏 (低代码.md §8.1 历史坑)。dry-run 必须报警告。
            var bp = MakeMinimalBlueprint();
            bp.Features.Add(new FeatureModel
            {
                TypeName = nameof(UniversalAutoScaleFeature),
                InputBindings = new Dictionary<string, object?>
                {
                    // 故意只焊 ValuePorts 不焊 YRangePort
                    [nameof(UniversalAutoScaleFeature.ValuePorts)] = "RealTime_Price",
                },
            });

            var r = BlueprintLauncher.DryRun(bp);
            Assert.Contains(r.Diagnostics, d =>
                d.Severity == BlueprintDiagnosticSeverity.Warning &&
                d.Code == "BP_OUTPUT_PORT_UNBOUND" &&
                d.FeatureTypeName == nameof(UniversalAutoScaleFeature) &&
                d.PortName == nameof(UniversalAutoScaleFeature.YRangePort));
        }

        [Fact]
        public void DryRun_PortTypeMismatch_ProducesWarning()
        {
            // 同一个 globalId 既被作为 LineSeriesFeature.YRangePort: DataPort<RealRange> 注册,
            // 又被作为 LineSeriesFeature.DataPort: DataPort<ReadOnlyMemory<double>> 注册 → 类型冲突。
            var bp = MakeMinimalBlueprint();
            bp.Features.Add(new FeatureModel
            {
                TypeName = nameof(LineSeriesFeature),
                InputBindings = new Dictionary<string, object?>
                {
                    [nameof(LineSeriesFeature.YRangePort)] = "shared_id",
                },
            });
            bp.Features.Add(new FeatureModel
            {
                TypeName = nameof(LineSeriesFeature),
                InputBindings = new Dictionary<string, object?>
                {
                    [nameof(LineSeriesFeature.DataPort)]   = "shared_id",  // 类型跟上面冲突
                },
            });

            var r = BlueprintLauncher.DryRun(bp);
            Assert.Contains(r.Diagnostics, d =>
                d.Severity == BlueprintDiagnosticSeverity.Warning &&
                d.Code == "BP_PORT_TYPE_MISMATCH" &&
                d.PortName == "shared_id");
        }

        [Fact]
        public void DryRun_ValidMinimalBlueprint_NoDiagnostics()
        {
            // 最小有效蓝图:只有 DataSource 节点 + 内置 ChartInteractionFeature (其 PointerHitPort 是 Output,
            // 在 PortBindings 里焊一个就行)。不应产生任何警告。
            var bp = MakeMinimalBlueprint();
            bp.Features.Add(new FeatureModel
            {
                TypeName = nameof(ChartInteractionFeature),
                OutputBindings = new Dictionary<string, object?>
                {
                    [nameof(ChartInteractionFeature.PointerHitPort)] = "hit_state",
                },
            });

            var r = BlueprintLauncher.DryRun(bp);
            Assert.Null(r.Error);
            // 允许有 Trait 警告等 noise,但不应有 BP_PORT_TYPE_MISMATCH / BP_OUTPUT_PORT_UNBOUND。
            Assert.DoesNotContain(r.Diagnostics, d =>
                d.Code == "BP_PORT_TYPE_MISMATCH" || d.Code == "BP_OUTPUT_PORT_UNBOUND");
        }

        private static ChartBlueprint MakeMinimalBlueprint()
        {
            // SinWaveDataSource 仅在 LowCodeDemo 注册;测试要么在框架层选一个 BuiltinRegistration
            // 已登记的 DataSource (但 Hevo.Charting 自己没有内置 ds)。
            // 折中:用 GraphViewerBootstrap 后已登记的 ChartInteractionFeature 等内置 feature 测,
            // DataSource 用一个能找到的。简单起见:借测试自定义 DataSourceModel.TypeName,
            // 让 DryRun 在 ComponentRegistry.Resolve 阶段早 Error。但本类的非"unregistered"用例需要
            // 一个真正能 Resolve 的 DataSource。用 TestDataSource 注册到 ComponentRegistry。
            EnsureTestDataSourceRegistered();
            return new ChartBlueprint
            {
                DataSources = { new DataSourceModel { Id = "primary", TypeName = nameof(TestDataSource) } },
            };
        }

        private static bool _testDsRegistered;
        private static void EnsureTestDataSourceRegistered()
        {
            if (_testDsRegistered) return;
            _testDsRegistered = true;
            ComponentRegistry.Register<TestDataSource>();
        }
    }
}
