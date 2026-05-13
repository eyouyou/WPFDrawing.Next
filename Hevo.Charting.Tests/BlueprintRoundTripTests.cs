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

            Assert.Single(bp2.DataSources);
            Assert.Equal(bp.DataSources[0].TypeName, bp2.DataSources[0].TypeName);
        }

        [Fact]
        public void RoundTrip_PreservesMappingKeys()
        {
            // GraphSerializer 序列化时按 Node.Id 重生全局 portId(低代码.md §7.4),
            // 所以 mapping 的 value 不字面保留 —— 但 key 集合(DataSource 上的字段名)必须一致。
            // 节点化协议:单 PortBindings 字典统一表达,key 既包含 DS 自身 scalar 也包含 TItem 行字段。
            // 拓扑等价检查放到 RoundTrip_PreservesPortBindingsConnectivity 里。
            var bp = MakeFixtureBlueprint();
            var state = GraphDeserializer.FromBlueprint(bp);
            var bp2 = GraphSerializer.ToBlueprint(state);

            Assert.Equal(
                bp.DataSources[0].OutputBindings.Keys.OrderBy(x => x),
                bp2.DataSources[0].OutputBindings.Keys.OrderBy(x => x));
        }

        [Fact]
        public void RoundTrip_StripsLegacyScalarAndVectorMappingsFromDataSourceProperties()
        {
            // 历史 bug:GraphSerializer 过滤 ScalarMappings/VectorMappings 的条件写错了优先级
            // (`||` 跟 `&&` 摆错),VectorMappings 只在 value=null 时才跳,非 null 漏到 dsm.Properties。
            // 后续 InjectProperties 被 fail-fast 抓到("属性 'VectorMappings' 在 Composite`1 上不存在"用户报错)。
            //
            // 不变式:GraphState 里就算 Node.Properties 带了 ScalarMappings/VectorMappings(NodeFactory 总会塞这俩),
            // 序列化出去的 ChartBlueprint.DataSources[i].Properties 永远不能带这俩 key —— 它们是编辑器内部状态,
            // 不是 DataSource 的运行时属性。
            var bp = MakeFixtureBlueprint();
            var state = GraphDeserializer.FromBlueprint(bp);

            // 给 DS 节点的 Properties 显式塞两个非 null 字典(模拟 NodeFactory.CreateNode 出来的形态)。
            var dsNode = state.Nodes.First(n => n.Kind == NodeKind.DataSource);
            dsNode.Properties["ScalarMappings"] = new Dictionary<string, string> { ["Foo"] = "bar" };
            dsNode.Properties["VectorMappings"] = new Dictionary<string, string> { ["Baz"] = "qux" };

            var bp2 = GraphSerializer.ToBlueprint(state);
            var dsm = bp2.DataSources[0];

            Assert.False(dsm.Properties.ContainsKey("ScalarMappings"),
                "GraphSerializer 必须无条件过滤 ScalarMappings,否则会撞 fail-fast。");
            Assert.False(dsm.Properties.ContainsKey("VectorMappings"),
                "GraphSerializer 必须无条件过滤 VectorMappings,否则会撞 fail-fast。");
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
                var allKeys1 = f1.InputBindings.Keys.Concat(f1.OutputBindings.Keys);
                foreach (var key in allKeys1)
                {
                    Assert.True(f2!.InputBindings.ContainsKey(key) || f2!.OutputBindings.ContainsKey(key),
                        $"Feature {f1.TypeName} 端口 {key} round-trip 后丢失");
                }
            }

            // 拓扑等价:LineSeriesFeature.DataPort 焊接的 globalId == DataSource.OutputBindings["Value"]
            var line2 = bp2.Features.First(f => f.TypeName == nameof(LineSeriesFeature));
            var actualPortId = line2.InputBindings[nameof(LineSeriesFeature.DataPort)]?.ToString();
            Assert.Equal(
                bp2.DataSources[0].OutputBindings["Value"]?.ToString(),
                actualPortId);
        }

        [Fact]
        public void PortBindings_ReadsArrayForm_NewFormat()
        {
            // §8 新格式:扇入端口写 List<string>,反序列化时被识别为多 globalId。
            // 用 UniversalAutoScale.ValuePorts (DataPort<ReadOnlyMemory<double>>[]) 做扇入端口测试。
            var bp = new ChartBlueprint
            {
                DataSources = new List<DataSourceModel>
                {
                    new DataSourceModel
                    {
                        Id = "primary",
                        TypeName = nameof(TestDataSource),
                        OutputBindings = new Dictionary<string, object?>
                        {
                            ["Value"] = "ds_Value",
                        },
                    },
                },
                Features = new List<FeatureModel>
                {
                    new FeatureModel
                    {
                        TypeName = nameof(UniversalAutoScaleFeature),
                        InputBindings = new Dictionary<string, object?>
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
                DataSources = new List<DataSourceModel>
                {
                    new DataSourceModel
                    {
                        Id = "primary",
                        TypeName = nameof(TestDataSource),
                        OutputBindings = new Dictionary<string, object?>
                        {
                            ["Value"] = "ds_Value",
                            ["Time"]  = "ds_Time",
                        },
                    },
                },
                Features = new List<FeatureModel>
                {
                    new FeatureModel
                    {
                        TypeName = nameof(UniversalAutoScaleFeature),
                        InputBindings = new Dictionary<string, object?>
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
        public void RoundTrip_MultiInputCompute_PreservesNestedPortBindings()
        {
            // §D2.X 多输入指标:ComputeFeature.Inputs (Dictionary<string, DataPort<ROM<double>>>)
            // 在蓝图里通过 nested key "Inputs.high" / "Inputs.low" / "Inputs.close" 焊接,
            // round-trip 后这些 nested key + 对应 globalId(拓扑等价)必须保留。
            var bp = new ChartBlueprint
            {
                DataSources = new List<DataSourceModel>
                {
                    new DataSourceModel
                    {
                        Id = "primary",
                        TypeName = nameof(TestDataSource),
                        OutputBindings = new Dictionary<string, object?>
                        {
                            ["Value"] = "ds_high",
                            ["Time"]  = "ds_low",   // TestDataSource 没多列,借现成字段当 high/low/close 占位
                        },
                    },
                },
                Features = new List<FeatureModel>
                {
                    new FeatureModel
                    {
                        TypeName = nameof(ComputeFeature),
                        Properties = new Dictionary<string, object?>
                        {
                            ["InputOrder"] = new[] { "high", "low" },
                        },
                        InputBindings = new Dictionary<string, object?>
                        {
                            ["Inputs.high"] = "ds_high",
                            ["Inputs.low"]  = "ds_low",
                        },
                        OutputBindings = new Dictionary<string, object?>
                        {
                            [nameof(ComputeFeature.OutputPort)] = "indicator_out",
                        },
                    },
                },
            };

            var state = GraphDeserializer.FromBlueprint(bp);

            // 反序列化后 ComputeFeature 节点应有展开后的 child input ports
            var computeNode = state.Nodes.FirstOrDefault(n => n.TypeName == nameof(ComputeFeature));
            Assert.NotNull(computeNode);
            Assert.Contains(computeNode!.InputPorts, p => p.Id == "Inputs.high");
            Assert.Contains(computeNode!.InputPorts, p => p.Id == "Inputs.low");

            // 对应的边也应被还原(ds_high / ds_low → Inputs.high / Inputs.low)
            Assert.Contains(state.Edges, e => e.ToPortId == "Inputs.high");
            Assert.Contains(state.Edges, e => e.ToPortId == "Inputs.low");

            // round-trip 回来 InputBindings 必须含 nested keys
            var bp2 = GraphSerializer.ToBlueprint(state);
            var compute2 = bp2.Features.First(f => f.TypeName == nameof(ComputeFeature));
            Assert.True(compute2.InputBindings.ContainsKey("Inputs.high"));
            Assert.True(compute2.InputBindings.ContainsKey("Inputs.low"));
        }

        [Fact]
        public void DryRun_MultiInputMissingBinding_ReportsInputUnconnected()
        {
            // §D2.X DryRun 校验:InputOrder 声明 'close' 但 PortBindings 没 "Inputs.close" 焊接
            // → BP_PYHANDLER_INPUT_UNCONNECTED warning。
            var bp = new ChartBlueprint
            {
                DataSources = new List<DataSourceModel>
                {
                    new DataSourceModel
                    {
                        Id = "primary",
                        TypeName = nameof(TestDataSource),
                        OutputBindings = new Dictionary<string, object?> { ["Value"] = "ds_v" },
                    },
                },
                Features = new List<FeatureModel>
                {
                    new FeatureModel
                    {
                        TypeName = nameof(ComputeFeature),
                        Properties = new Dictionary<string, object?>
                        {
                            ["InputOrder"] = new[] { "high", "low", "close" },
                        },
                        InputBindings  = new Dictionary<string, object?> { ["Inputs.high"] = "ds_v" },  // low / close 故意缺失
                        OutputBindings = new Dictionary<string, object?> { [nameof(ComputeFeature.OutputPort)] = "out" },
                    },
                },
            };

            var result = BlueprintLauncher.DryRun(bp);
            Assert.Null(result.Error);
            Assert.Contains(result.Diagnostics, d =>
                d.Code == "BP_PYHANDLER_INPUT_UNCONNECTED" && d.PortName == "Inputs.low");
            Assert.Contains(result.Diagnostics, d =>
                d.Code == "BP_PYHANDLER_INPUT_UNCONNECTED" && d.PortName == "Inputs.close");
        }

        [Fact]
        public void NodeFactory_ResetInputSlots_StripsOldChildrenAndReplaces()
        {
            // §D2.X NodeEditor 重新配置多输入指标:把 atr (3 输入)节点切换成 mfi (4 输入),
            // 老 child input ports 应当被剥离,新的 4 根重新展开,InputOrder 同步刷新。
            var node = NodeFactory.CreateNode(typeof(ComputeFeature), new HevoPoint(0, 0));
            node = NodeFactory.ExpandInputSlots(node, "Inputs",
                new[] { "high", "low", "close" }, typeof(ReadOnlyMemory<double>));

            Assert.Equal(3, node.InputPorts.Count(p => p.Id.StartsWith("Inputs.")));
            Assert.Equal(new[] { "high", "low", "close" }, (string[])node.Properties["InputOrder"]!);

            var reset = NodeFactory.ResetInputSlots(node, "Inputs",
                new[] { "high", "low", "close", "volume" }, typeof(ReadOnlyMemory<double>));

            // 旧 3 根被摘掉,新 4 根上去,non-Inputs.* 的端口(InputPort 单输入兼容字段)保留
            var newChildren = reset.InputPorts.Where(p => p.Id.StartsWith("Inputs.")).Select(p => p.Id).ToArray();
            Assert.Equal(4, newChildren.Length);
            Assert.Contains("Inputs.volume", newChildren);
            Assert.Equal(new[] { "high", "low", "close", "volume" }, (string[])reset.Properties["InputOrder"]!);
        }

        [Fact]
        public void DryRun_MultiInputMissingInputOrder_Reports()
        {
            // PortBindings 含 "Inputs.x" 但 Properties 没 InputOrder → 装配会跳,警告抛出。
            var bp = new ChartBlueprint
            {
                DataSources = new List<DataSourceModel>
                {
                    new DataSourceModel
                    {
                        Id = "primary",
                        TypeName = nameof(TestDataSource),
                        OutputBindings = new Dictionary<string, object?> { ["Value"] = "ds_v" },
                    },
                },
                Features = new List<FeatureModel>
                {
                    new FeatureModel
                    {
                        TypeName = nameof(ComputeFeature),
                        // 无 InputOrder
                        InputBindings  = new Dictionary<string, object?> { ["Inputs.x"] = "ds_v" },
                        OutputBindings = new Dictionary<string, object?> { [nameof(ComputeFeature.OutputPort)] = "out" },
                    },
                },
            };

            var result = BlueprintLauncher.DryRun(bp);
            Assert.Contains(result.Diagnostics, d => d.Code == "BP_PYHANDLER_INPUT_ORDER_MISSING");
        }

        [Fact]
        public void RoundTrip_EmptyFeaturesList_StillSerializesDataSource()
        {
            var bp = new ChartBlueprint
            {
                DataSources = { new DataSourceModel { Id = "primary", TypeName = nameof(TestDataSource) } },
            };

            var state = GraphDeserializer.FromBlueprint(bp);
            var bp2 = GraphSerializer.ToBlueprint(state);

            Assert.Single(bp2.DataSources);
            Assert.Equal(nameof(TestDataSource), bp2.DataSources[0].TypeName);
            Assert.Empty(bp2.Features);
        }

        [Fact]
        public void RoundTrip_PreservesFeatureEvents()
        {
            // ScannerWindow 场景:PlotFeature.Events["ElementDoubleClicked"] → URI 路由,
            // 编辑器保存(GraphState 走 round-trip)后双击仍生效。
            var bp = new ChartBlueprint
            {
                DataSources = { new DataSourceModel { Id = "primary", TypeName = nameof(TestDataSource),
                    OutputBindings = new Dictionary<string, object?> { ["Value"] = "ds_v" } } },
                Features = new List<FeatureModel>
                {
                    new FeatureModel
                    {
                        TypeName = nameof(LineSeriesFeature),
                        InputBindings = new Dictionary<string, object?>
                        {
                            [nameof(LineSeriesFeature.DataPort)] = "ds_v",
                        },
                        Events = new Dictionary<string, string>
                        {
                            ["ElementDoubleClicked"] = "hevo://open/kline_detail?index={Index}",
                        },
                    },
                },
            };

            var state = GraphDeserializer.FromBlueprint(bp);
            var bp2 = GraphSerializer.ToBlueprint(state);

            var f2 = bp2.Features.First(f => f.TypeName == nameof(LineSeriesFeature));
            Assert.True(f2.Events.ContainsKey("ElementDoubleClicked"),
                "Events round-trip failed: GraphDeserializer/Serializer dropped Events on FeatureModel");
            Assert.Equal("hevo://open/kline_detail?index={Index}", f2.Events["ElementDoubleClicked"]);
        }

        [Fact]
        public void RoundTrip_KLineLikeSoADataSource_PreservesVectorPortBindings()
        {
            // KLineDetailWindow 真实场景:TItem(KLineDataBlock)的字段是 ReadOnlyMemory<X>(SoA 块式),
            // 不是 X 标量。GraphDeserializer 走 NodeFactory.ScanDataSourceOutputs 反射 TItem 字段,
            // 不识别 ROM<X> 的话 DS 节点没有 "Closes"/"Times"/... OutputPort → globalIdToOutput 漏注册 →
            // UniversalAutoScaleFeature.ValuePorts 引用的 globalId 反查不到 → 边丢失 → 运行期 ValuePorts 为 null
            // → OnCompose listenPorts.AddRange(null) 抛 ArgumentNullException(collection)。
            ComponentRegistry.Register<SoaTestDataSource>();
            ComponentRegistry.Register<Hevo.Charting.Features.UniversalAutoScaleFeature>();

            var bp = new ChartBlueprint
            {
                DataSources = { new DataSourceModel { Id = "primary", TypeName = nameof(SoaTestDataSource),
                    OutputBindings = new Dictionary<string, object?>
                    {
                        ["Closes"] = "6f0b04ea_Closes",
                    } } },
                Features = new List<FeatureModel>
                {
                    new FeatureModel
                    {
                        TypeName       = nameof(Hevo.Charting.Features.UniversalAutoScaleFeature),
                        OutputBindings = new Dictionary<string, object?> { ["YRangePort"] = "yr_out" },
                        InputBindings  = new Dictionary<string, object?> { ["ValuePorts"] = new List<string> { "6f0b04ea_Closes" } },
                    },
                },
            };

            var state = GraphDeserializer.FromBlueprint(bp);
            var bp2 = GraphSerializer.ToBlueprint(state);

            var autoScale = bp2.Features.First(f => f.TypeName == nameof(Hevo.Charting.Features.UniversalAutoScaleFeature));
            Assert.True(autoScale.InputBindings.ContainsKey("ValuePorts"),
                "SoA TItem(ReadOnlyMemory<X> 字段)反射不到 → DS 节点缺 OutputPort → ValuePorts 边丢失。");
            var list = PortBindingValue.ExtractList(autoScale.InputBindings["ValuePorts"]);
            Assert.NotEmpty(list);
        }

        [Fact]
        public void RoundTrip_PreservesArrayPortBindings()
        {
            // KLineDetailWindow 场景:UniversalAutoScaleFeature.ValuePorts 是 DataPort<T>[] 数组端口,
            // 蓝图 PortBindings 写 list ["6f0b04ea_Closes"]。round-trip 丢的话 ValuePorts 为 null,
            // OnCompose 里 listenPorts.AddRange(ValuePorts) → ArgumentNullException(collection)。
            ComponentRegistry.Register<Hevo.Charting.Features.UniversalAutoScaleFeature>();

            var bp = new ChartBlueprint
            {
                DataSources = { new DataSourceModel { Id = "primary", TypeName = nameof(TestDataSource),
                    OutputBindings = new Dictionary<string, object?> { ["Value"] = "ds_v" } } },
                Features = new List<FeatureModel>
                {
                    new FeatureModel
                    {
                        TypeName       = nameof(Hevo.Charting.Features.UniversalAutoScaleFeature),
                        OutputBindings = new Dictionary<string, object?> { ["YRangePort"] = "yr_out" },
                        InputBindings  = new Dictionary<string, object?> { ["ValuePorts"] = new List<string> { "ds_v" } },
                    },
                },
            };

            var state = GraphDeserializer.FromBlueprint(bp);
            var bp2 = GraphSerializer.ToBlueprint(state);

            var autoScale = bp2.Features.First(f => f.TypeName == nameof(Hevo.Charting.Features.UniversalAutoScaleFeature));
            Assert.True(autoScale.InputBindings.ContainsKey("ValuePorts"),
                "round-trip 后 ValuePorts 整个 binding 丢了 —— BlueprintRunner 拿不到 ValuePorts,默认 null,OnCompose 抛 ArgumentNullException(collection)。");
            var list = PortBindingValue.ExtractList(autoScale.InputBindings["ValuePorts"]);
            Assert.NotEmpty(list);
        }

        [Fact]
        public void RoundTrip_PreservesOutputBindingId_WhenConsumerOrderedBeforeProducer()
        {
            // 历史 bug(2026-05-12):GraphSerializer.ToBlueprint 的 outputPortGlobalId 表是 features
            // foreach 里边遍历边更新的,Consumer feature 在 list 中排 Producer 之前时,Consumer 的
            // InputPort 反查拿到的是 fallback "{nodeId}_{portId}",而非 Producer 的显式 OutputPort.BindingId。
            //
            // 具体踩点:default_kline_dashboard.json strategy cell 里 UniversalAutoScaleFeature(features[3])
            // 排在 PlotFeature(features[5])之前;AutoScale.ValuePorts 引用 PlotFeature.SeriesOutputs.upper —
            //   PlotFeature.PortBindings["SeriesOutputs.upper"] = "s_BB_Upper"   (显式 globalId)
            //   AutoScale.PortBindings["ValuePorts"]            = ["s_BB_Upper"]
            // round-trip 后理应保持 "s_BB_Upper",但旧逻辑下 AutoScale.ValuePorts 变成了
            // "{plotNodeId}_SeriesOutputs.upper",两端各拿不同 globalId,运行期 _portRegistry 注册成两根独立
            // port,AutoScale 读不到 PlotFeature 的 Watch 写入,bb_breakout 的 upper/middle/lower 不进 Y 量程。
            //
            // 本测试用 ComputeFeature(单 Output)+ AutoScale 复现,语义跟 PlotFeature.SeriesOutputs 等价
            // (单输出端口 vs nested dict 子端口,都走 OutputPort.BindingId 这条路)。
            ComponentRegistry.Register<Hevo.Charting.Features.UniversalAutoScaleFeature>();
            ComponentRegistry.Register<Hevo.Charting.Features.ComputeFeature>();

            var bp = new ChartBlueprint
            {
                DataSources = { new DataSourceModel { Id = "primary", TypeName = nameof(TestDataSource),
                    OutputBindings = new Dictionary<string, object?> { ["Value"] = "ds_v" } } },
                Features = new List<FeatureModel>
                {
                    // Consumer 排前面 —— 故意触发原 bug。
                    new FeatureModel
                    {
                        TypeName       = nameof(Hevo.Charting.Features.UniversalAutoScaleFeature),
                        OutputBindings = new Dictionary<string, object?> { ["YRangePort"] = "yr_out" },
                        InputBindings  = new Dictionary<string, object?> { ["ValuePorts"] = new List<string> { "my_signal" } },
                    },
                    // Producer 排后面,显式 OutputPort globalId "my_signal"。
                    new FeatureModel
                    {
                        TypeName       = nameof(Hevo.Charting.Features.ComputeFeature),
                        InputBindings  = new Dictionary<string, object?> { ["InputPort"]  = "ds_v" },
                        OutputBindings = new Dictionary<string, object?> { ["OutputPort"] = "my_signal" },
                    },
                },
            };

            var state = GraphDeserializer.FromBlueprint(bp);
            var bp2 = GraphSerializer.ToBlueprint(state);

            // ① Producer 端 round-trip 保持显式 globalId
            var compute2 = bp2.Features.First(f => f.TypeName == nameof(Hevo.Charting.Features.ComputeFeature));
            Assert.Equal("my_signal", PortBindingValue.ExtractSingle(compute2.OutputBindings["OutputPort"]));

            // ② Consumer 端的 fan-in array 必须引用同一个 globalId,不能被换成 "{producerNodeId}_OutputPort"。
            //    这是本 bug 的关键断言 —— 旧逻辑下这里会 fail。
            var autoScale2 = bp2.Features.First(f => f.TypeName == nameof(Hevo.Charting.Features.UniversalAutoScaleFeature));
            var valuePorts = PortBindingValue.ExtractList(autoScale2.InputBindings["ValuePorts"]);
            Assert.Contains("my_signal", valuePorts);
            Assert.DoesNotContain(valuePorts, id => id.EndsWith("_OutputPort", StringComparison.Ordinal) && id != "my_signal");
        }

        [Fact]
        public void RoundTrip_PreservesFeatureEvents_AfterJsonSerialization()
        {
            // 模拟 ScannerWindow 启动:JSON 资产 → ChartBlueprint(deserialize) →
            // GraphState(FromBlueprint) → ChartBlueprint(ToBlueprint) → 给 BlueprintRunner 用。
            // 任何一步丢失 Events 都会导致双击无路由 handler。
            var json = """
            {
              "DataSources": [
                { "Id": "primary", "TypeName": "TestDataSource",
                  "PortBindings": { "Value": "ds_v" } }
              ],
              "Features": [
                { "TypeName": "LineSeriesFeature",
                  "PortBindings": { "DataPort": "ds_v" },
                  "Events": { "ElementDoubleClicked": "hevo://open/kline_detail?index={Index}" } }
              ]
            }
            """;
            var bp = System.Text.Json.JsonSerializer.Deserialize<ChartBlueprint>(json,
                Hevo.Charting.LowCode.Designer.Converters.BlueprintJsonOptions.Default)!;
            Assert.True(bp.Features[0].Events.ContainsKey("ElementDoubleClicked"),
                "JSON deserialization 直接就丢了 Events 字段 —— FeatureModel.Events 字段没正确反序列化。");

            var state = GraphDeserializer.FromBlueprint(bp);
            var bp2 = GraphSerializer.ToBlueprint(state);

            var f2 = bp2.Features.First(f => f.TypeName == nameof(LineSeriesFeature));
            Assert.True(f2.Events.ContainsKey("ElementDoubleClicked"),
                "JSON → GraphState → JSON 后 Events 丢失。");
        }

        private static ChartBlueprint MakeFixtureBlueprint()
        {
            return new ChartBlueprint
            {
                DataSources = new List<DataSourceModel>
                {
                    new DataSourceModel
                    {
                        Id = "primary",
                        TypeName = nameof(TestDataSource),
                        OutputBindings = new Dictionary<string, object?>
                        {
                            ["LogicalLength"] = "VP_LogicalLength",
                            ["Value"] = "ds_Value",
                            ["Time"]  = "ds_Time",
                        },
                    },
                },
                Features = new List<FeatureModel>
                {
                    new FeatureModel
                    {
                        TypeName = nameof(ChartInteractionFeature),
                        OutputBindings = new Dictionary<string, object?>
                        {
                            [nameof(ChartInteractionFeature.PointerHitPort)] = "hit_state",
                        },
                    },
                    new FeatureModel
                    {
                        TypeName = nameof(LineSeriesFeature),
                        // 只焊 DataPort —— YRangePort 在真实蓝图里来自 UniversalAutoScaleFeature 的 Output;
                        // round-trip 测试用最小依赖,避免引入额外节点干扰。
                        InputBindings = new Dictionary<string, object?>
                        {
                            [nameof(LineSeriesFeature.DataPort)] = "ds_Value",
                        },
                    },
                },
            };
        }
    }
}
