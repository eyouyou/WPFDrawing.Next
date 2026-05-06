using BenchmarkDotNet.Attributes;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;

namespace Hevo.Charting.Benchmarks
{
    /// <summary>
    /// 端到端 benchmark:用接近真实业务规模的蓝图测整条加载链。
    /// 关注两个数:
    /// 1. CreateNode_50Features — §1 的端口元数据缓存的实际收益。
    /// 2. DryRun_50Features — §5 dry-run 在大蓝图上的开销。Dry-run 跑两次以上看缓存命中后的稳定性。
    /// </summary>
    [MemoryDiagnoser]
    public class BlueprintEndToEndBenchmarks
    {
        private readonly Type[] _types;
        private readonly ChartBlueprint _blueprint;

        public BlueprintEndToEndBenchmarks()
        {
            GraphViewerBootstrap.Initialize();
            ComponentRegistry.Register<BenchmarkDataSource>();

            // 50 节点规模 = 接近 TimeShare 业务蓝图。重复用同一组 Feature 类型,
            // 反映实际场景里"几十个节点 + 不超过十种 Feature 类型"的特征。
            _types = new[]
            {
                typeof(LineSeriesFeature),
                typeof(AxisFeature),
                typeof(GridLayoutFeature),
                typeof(PlotAreaDecorFeature),
                typeof(UniversalAutoScaleFeature),
                typeof(ChartInteractionFeature),
                typeof(BarSeriesFeature),
                typeof(ViewportManagerFeature),
                typeof(UniversalHeaderFeature),
                typeof(DataPagingFeature),
            };

            _blueprint = BuildLargeBlueprint(featureCount: 50);
        }

        // ============================================================
        // §1 端口元数据缓存:50 次 CreateNode 的总开销
        // ============================================================
        // 缓存命中后,反射 GetProperties 一次都不用,仅做新 Node 实例的 Guid + dict 构造。

        [Benchmark]
        public int CreateNode_50Features()
        {
            int total = 0;
            for (int i = 0; i < 50; i++)
            {
                var t = _types[i % _types.Length];
                var node = NodeFactory.CreateNode(t, default);
                total += node.InputPorts.Count + node.OutputPorts.Count;
            }
            return total;
        }

        // ============================================================
        // §5 DryRun 在 50 Feature 蓝图上的开销
        // ============================================================

        [Benchmark]
        public int DryRun_50Features()
        {
            var r = BlueprintLauncher.DryRun(_blueprint);
            return r.Diagnostics.Count;
        }

        private ChartBlueprint BuildLargeBlueprint(int featureCount)
        {
            var bp = new ChartBlueprint
            {
                DataSource = new DataSourceModel
                {
                    TypeName = nameof(BenchmarkDataSource),
                    VectorMappings = new Dictionary<string, string>
                    {
                        ["Value"] = "ds_Value",
                    },
                },
            };
            for (int i = 0; i < featureCount; i++)
            {
                var t = _types[i % _types.Length];
                bp.Features.Add(new FeatureModel
                {
                    TypeName = t.Name,
                    PortBindings = new Dictionary<string, object?>(),
                });
            }
            return bp;
        }
    }

    public class BenchmarkDataItem
    {
        public double Value { get; set; }
    }

    public class BenchmarkDataSource : DataSource<BenchmarkDataSource, BenchmarkDataItem>
    {
        public override int LogicalLength => 0;
    }
}
