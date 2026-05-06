using Hevo.Charting.Features;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §10 蓝图 round-trip 一致性回归。
    /// 不变式:bp → GraphState → bp 应保持语义等价(忽略 AutoLayout 重排的 Node 位置)。
    /// 这是 §7 (JsonConverter) / §8 (PortBindings 数组化) 改造前必须有的安全网。
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class BlueprintRoundTripTests
    {
        public BlueprintRoundTripTests()
        {
            // TestDataSource 不在 BuiltinRegistration 名单里,显式登记一次 (重复登记安全)。
            ComponentRegistry.Register<TestDataSource>();
        }

        [Fact]
        public void RoundTrip_PreservesDataSourceTypeName()
        {
            var bp = MakeFixtureBlueprint();
            var state = GraphDeserializer.FromBlueprint(bp);
            var bp2 = GraphSerializer.ToBlueprint(state);

            Assert.NotNull(bp2.DataSource);
            Assert.Equal(bp.DataSource!.TypeName, bp2.DataSource!.TypeName);
        }

        [Fact]
        public void RoundTrip_PreservesMappingKeys()
        {
            // GraphSerializer 序列化时按 Node.Id 重生全局 portId(低代码.md §7.4),
            // 所以 mapping 的 value 不字面保留 —— 但 key 集合(DataSource 上的字段名)必须一致。
            // 拓扑等价检查放到 RoundTrip_PreservesPortBindingsConnectivity 里。
            var bp = MakeFixtureBlueprint();
            var state = GraphDeserializer.FromBlueprint(bp);
            var bp2 = GraphSerializer.ToBlueprint(state);

            Assert.Equal(
                bp.DataSource!.ScalarMappings.Keys.OrderBy(x => x),
                bp2.DataSource!.ScalarMappings.Keys.OrderBy(x => x));
            Assert.Equal(
                bp.DataSource.VectorMappings.Keys.OrderBy(x => x),
                bp2.DataSource.VectorMappings.Keys.OrderBy(x => x));
        }

        [Fact]
        public void RoundTrip_PreservesFeatureSet()
        {
            var bp = MakeFixtureBlueprint();
            var state = GraphDeserializer.FromBlueprint(bp);
            var bp2 = GraphSerializer.ToBlueprint(state);

            // Feature 集应一一对应 (顺序由 AutoLayout 决定,这里只比 multiset)。
            var names1 = bp.Features.Select(f => f.TypeName).OrderBy(n => n).ToArray();
            var names2 = bp2.Features.Select(f => f.TypeName).OrderBy(n => n).ToArray();
            Assert.Equal(names1, names2);
        }

        [Fact]
        public void RoundTrip_PreservesPortBindingsConnectivity()
        {
            // 关键不变式:每个 Feature 的 PortBindings *键集*必须一致(否则焊接缺端);
            // 且 fixture 里 LineSeriesFeature.DataPort 焊到 DataSource.Value 这条线 ——
            // round-trip 后 LineSeries 的 DataPort 值必须仍然 == DataSource.VectorMappings["Value"]
            // (拓扑等价检查),即便字面 portId 已重生。
            var bp = MakeFixtureBlueprint();
            var state = GraphDeserializer.FromBlueprint(bp);
            var bp2 = GraphSerializer.ToBlueprint(state);

            foreach (var f1 in bp.Features)
            {
                var f2 = bp2.Features.FirstOrDefault(f => f.TypeName == f1.TypeName);
                Assert.NotNull(f2);
                foreach (var kv in f1.PortBindings)
                {
                    Assert.True(f2!.PortBindings.ContainsKey(kv.Key),
                        $"Feature {f1.TypeName} 端口 {kv.Key} round-trip 后丢失");
                }
            }

            // 拓扑等价:LineSeriesFeature.DataPort 焊接的 globalId
            //          == DataSource.VectorMappings["Value"]
            // PortBindings value 现在是 object? — 用 PortBindingValue.ExtractSingle 标准化对比。
            var line2 = bp2.Features.First(f => f.TypeName == nameof(LineSeriesFeature));
            var actualPortId = line2.PortBindings[nameof(LineSeriesFeature.DataPort)]?.ToString();
            Assert.Equal(
                bp2.DataSource!.VectorMappings["Value"],
                actualPortId);
        }

        [Fact]
        public void PortBindings_ReadsArrayForm_NewFormat()
        {
            // §8 新格式:扇入端口写 List<string>,反序列化时被识别为多 globalId。
            // 用 UniversalAutoScale.ValuePorts (DataPort<ReadOnlyMemory<double>>[]) 做扇入端口测试。
            var bp = new ChartBlueprint
            {
                DataSource = new DataSourceModel
                {
                    TypeName = nameof(TestDataSource),
                    VectorMappings = new Dictionary<string, string>
                    {
                        ["Value"] = "ds_Value",
                    },
                },
                Features = new List<FeatureModel>
                {
                    new FeatureModel
                    {
                        TypeName = nameof(UniversalAutoScaleFeature),
                        PortBindings = new Dictionary<string, object?>
                        {
                            // 数组形态焊扇入 —— 单元素也用列表表达
                            [nameof(UniversalAutoScaleFeature.ValuePorts)] = new List<string> { "ds_Value" },
                        },
                    },
                },
            };

            var state = GraphDeserializer.FromBlueprint(bp);
            // 扇入边应该被还原:ValuePorts ← ds_Value
            Assert.Contains(state.Edges, e =>
                e.ToPortId == nameof(UniversalAutoScaleFeature.ValuePorts) &&
                e.FromPortId == "Value");
        }

        [Fact]
        public void PortBindings_ReadsCsvForm_LegacyCompat()
        {
            // §8 兼容:扇入端口的 <v1 CSV 字符串形态仍能被 ExtractList 识别拆出多 globalId。
            // 这套兼容路径是为了让旧蓝图 JSON 不需要数据迁移就能加载。
            var bp = new ChartBlueprint
            {
                DataSource = new DataSourceModel
                {
                    TypeName = nameof(TestDataSource),
                    VectorMappings = new Dictionary<string, string>
                    {
                        ["Value"] = "ds_Value",
                        ["Time"]  = "ds_Time",
                    },
                },
                Features = new List<FeatureModel>
                {
                    new FeatureModel
                    {
                        TypeName = nameof(UniversalAutoScaleFeature),
                        PortBindings = new Dictionary<string, object?>
                        {
                            // 老 CSV 形态
                            [nameof(UniversalAutoScaleFeature.ValuePorts)] = "ds_Value,ds_Time",
                        },
                    },
                },
            };

            var state = GraphDeserializer.FromBlueprint(bp);
            // 两根扇入边都应被还原
            Assert.Equal(2, state.Edges.Count(e =>
                e.ToPortId == nameof(UniversalAutoScaleFeature.ValuePorts)));
        }

        [Fact]
        public void RoundTrip_EmptyFeaturesList_StillSerializesDataSource()
        {
            var bp = new ChartBlueprint
            {
                DataSource = new DataSourceModel { TypeName = nameof(TestDataSource) },
            };

            var state = GraphDeserializer.FromBlueprint(bp);
            var bp2 = GraphSerializer.ToBlueprint(state);

            Assert.NotNull(bp2.DataSource);
            Assert.Equal(nameof(TestDataSource), bp2.DataSource!.TypeName);
            Assert.Empty(bp2.Features);
        }

        private static ChartBlueprint MakeFixtureBlueprint()
        {
            return new ChartBlueprint
            {
                DataSource = new DataSourceModel
                {
                    TypeName = nameof(TestDataSource),
                    ScalarMappings = new Dictionary<string, string>
                    {
                        // TestDataSource.LogicalLength 是 int,作 scalar 可被反射出来
                        ["LogicalLength"] = "VP_LogicalLength",
                    },
                    VectorMappings = new Dictionary<string, string>
                    {
                        ["Value"] = "ds_Value",
                        ["Time"]  = "ds_Time",
                    },
                },
                Features = new List<FeatureModel>
                {
                    new FeatureModel
                    {
                        TypeName = nameof(ChartInteractionFeature),
                        PortBindings = new Dictionary<string, object?>
                        {
                            [nameof(ChartInteractionFeature.PointerHitPort)] = "hit_state",
                        },
                    },
                    new FeatureModel
                    {
                        TypeName = nameof(LineSeriesFeature),
                        // 只焊 DataPort —— YRangePort 在真实蓝图里来自 UniversalAutoScaleFeature 的 Output;
                        // round-trip 测试用最小依赖,避免引入额外节点干扰。
                        PortBindings = new Dictionary<string, object?>
                        {
                            [nameof(LineSeriesFeature.DataPort)] = "ds_Value",
                        },
                    },
                },
            };
        }
    }
}
