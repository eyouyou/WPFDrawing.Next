using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;

namespace Hevo.Charting.Layers
{
    /// <summary>
    /// 纯元信息容器 layer —— 不画任何东西,只作为 trait 载体让 Tooltip / Header 等下游
    /// 通过 <see cref="ChartCell.ActiveLayers"/> 遍历拿到 <see cref="MetaTrait"/> +
    /// <see cref="StringSeriesDataTrait"/> 等元信息。
    ///
    /// <para>
    /// <b>典型用法</b>:业务侧需要把"证券名称 / 行业 / 地区代码" 之类的静态字符串字段送进 Tooltip,
    /// 用 <c>NameSeriesFeature</c>(或其他业务 Feature)把数据 publish 到本 layer,Tooltip 通过通用协议
    /// 取到,不需要给每种字段开专用端口字段(对比旧版 <c>TooltipWidgetFeature.NamesDataPort</c> 反模式)。
    /// </para>
    /// </summary>
    public sealed class MetadataLayer : ChartLayer
    {
        public MetadataLayer()
        {
            Name = "Metadata";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Main;
        }

        // 纯 trait 容器,无可视输出。trait 由上游 Feature 通过 ctx.For(this).PublishData 写入,
        // Tooltip / Header 等下游通过 IVisualData.Get<T> 读取 —— 不经过本方法。
        protected override void OnUpdate(IVisualData data, IDrawingSink drawSink, WidgetBuffer widgetSink)
        {
        }
    }
}
