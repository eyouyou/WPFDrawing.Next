using System.Collections.Generic;
using System.Linq;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// DryRun 按类型缓存端口元数据后,PortMetadataRegistry 的方向登记仍要生效:
    /// 第一次 DryRun 把类型缓存下来,之后再登记 Output,下一次 DryRun 必须按新方向报 BP_OUTPUT_PORT_UNBOUND。
    /// 用专属的探针 Feature,避免全局登记表污染别的测试。
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class DryRunTypeInfoCacheTests
    {
        public sealed class DirectionCacheProbeFeature : Feature
        {
            public DataPort<double>? A { get; set; }
            public DataPort<double>? B { get; set; }
            protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) { }
            protected override void OnProject(FeatureContext ctx) { }
        }

        private static List<string?> UnboundOutputs(ChartBlueprint bp) =>
            BlueprintLauncher.DryRun(bp).Diagnostics
                .Where(d => d.Code == "BP_OUTPUT_PORT_UNBOUND" && d.FeatureTypeName == nameof(DirectionCacheProbeFeature))
                .Select(d => d.PortName).ToList();

        [Fact]
        public void RegisterOutputs_AfterFirstDryRun_TakesEffect()
        {
            BlueprintTestFixture.EnsureTestDataSourceRegistered();
            ComponentRegistry.Register<DirectionCacheProbeFeature>();
            var bp = new ChartBlueprint
            {
                DataSources = { new DataSourceModel { Id = "primary", TypeName = nameof(TestDataSource) } },
                Features = { new FeatureModel { TypeName = nameof(DirectionCacheProbeFeature) } },
            };

            Assert.Empty(UnboundOutputs(bp)); // A / B 默认都是 Input,类型此时进了缓存

            PortMetadataRegistry.RegisterOutputs<DirectionCacheProbeFeature>(nameof(DirectionCacheProbeFeature.B));

            Assert.Equal(new[] { nameof(DirectionCacheProbeFeature.B) }, UnboundOutputs(bp));
        }
    }
}
