using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §1 端口元数据缓存的回归测试。
    /// 关键不变式:同一 Type 多次 CreateNode,结果里的 Port[] 是同一引用 (cache hit),
    /// 反射只跑了第一次。
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class NodePortCacheTests
    {
        [Fact]
        public void CreateNode_SameType_ReusesPortArrays()
        {
            var n1 = NodeFactory.CreateNode(typeof(LineSeriesFeature), new HevoPoint(0, 0));
            var n2 = NodeFactory.CreateNode(typeof(LineSeriesFeature), new HevoPoint(100, 100));

            // Port[] 引用应一致 —— ScanFeaturePorts 走缓存,不重复反射。
            Assert.Same(n1.InputPorts, n2.InputPorts);
            Assert.Same(n1.OutputPorts, n2.OutputPorts);
        }

        [Fact]
        public void CreateNode_DifferentTypes_HaveSeparatedShapes()
        {
            var line = NodeFactory.CreateNode(typeof(LineSeriesFeature), new HevoPoint(0, 0));
            var axis = NodeFactory.CreateNode(typeof(AxisFeature), new HevoPoint(0, 0));

            // 两个类型的 Port shape 必须是独立的对象,缓存不能串台。
            Assert.NotSame(line.InputPorts, axis.InputPorts);
        }

        [Fact]
        public void Classify_SameType_ReturnsCachedKind()
        {
            var k1 = NodeFactory.Classify(typeof(LineSeriesFeature));
            var k2 = NodeFactory.Classify(typeof(LineSeriesFeature));
            Assert.Equal(NodeFactory.Kind.Feature, k1);
            Assert.Equal(k1, k2);
        }

        [Fact]
        public void CreateNode_PerCallProperties_AreIndependent()
        {
            // Properties 字典是节点状态,每个 Node 实例必须有自己的可写字典 ——
            // 否则 NodeEditorWindow 改 A 的 PaddingRatio 会污染 B。
            var n1 = NodeFactory.CreateNode(typeof(LineSeriesFeature), new HevoPoint(0, 0));
            var n2 = NodeFactory.CreateNode(typeof(LineSeriesFeature), new HevoPoint(0, 0));
            Assert.NotSame(n1.Properties, n2.Properties);
        }
    }
}
