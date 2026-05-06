using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Hevo.Charting.LowCode.Designer.GraphViewer.Wrappers;
using System.Linq;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §2 PortBindings 数组协议接入 CrosshairFeature.YTrackers:验证 CrosshairBlueprintFeatureBase
    /// 把 YTrackerPorts (DataPort&lt;RealRange&gt;[]) 暴露为蓝图可见的扇入数组端口,且跟 base.YTrackers
    /// 共存(用户显式塞 YTrackers 时,blueprint 路径不覆盖)。
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class CrosshairBlueprintFeatureTests
    {
        // CrosshairDoubleFeature 应暴露 YTrackerPorts 为扇入数组输入端口
        [Fact]
        public void NodeFactory_DetectsYTrackerPorts_AsArrayInputPort()
        {
            var node = NodeFactory.CreateNode(typeof(CrosshairDoubleFeature), new HevoPoint(0, 0));

            var port = node.InputPorts.FirstOrDefault(p => p.Id == nameof(CrosshairDoubleFeature.YTrackerPorts));
            Assert.NotNull(port);
            Assert.True(port!.IsInput);
            Assert.True(port.IsArray);
            Assert.EndsWith("[]", port.DataTypeName);
        }

        // CrosshairDateTimeFeature(closed-over-DateTime)走同样的基类,YTrackerPorts 也应可见
        [Fact]
        public void NodeFactory_DateTimeWrapper_AlsoDetectsYTrackerPorts()
        {
            var node = NodeFactory.CreateNode(typeof(CrosshairDateTimeFeature), new HevoPoint(0, 0));
            Assert.Contains(node.InputPorts, p => p.Id == nameof(CrosshairDateTimeFeature.YTrackerPorts));
        }

        // 抽象基类 CrosshairBlueprintFeatureBase<TX> 不应被 BuiltinRegistration 收录(它 abstract+泛型)
        // —— 蓝图 picker 里只看到 closed 子类。
        [Fact]
        public void Registry_DoesNotInclude_AbstractGenericBase()
        {
            // CrosshairDoubleFeature 必须可解析,base 类自己不可解析(picker 不把抽象类当候选)
            ComponentRegistry.Register<CrosshairDoubleFeature>();
            Assert.True(ComponentRegistry.IsRegistered(nameof(CrosshairDoubleFeature)));
            Assert.False(ComponentRegistry.IsRegistered("CrosshairBlueprintFeatureBase`1"));
        }

        // 默认空数组 —— 无任何 YTrackerPorts 设置时,PortBindings 上没有累赘字段
        [Fact]
        public void Default_YTrackerPorts_IsEmptyArray()
        {
            var feat = new CrosshairDoubleFeature();
            Assert.Empty(feat.YTrackerPorts);
            Assert.Empty(feat.YTrackerMetas);
            Assert.Empty(feat.YTrackerPlacements);
        }
    }
}
