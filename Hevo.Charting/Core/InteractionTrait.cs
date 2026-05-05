using Hevo.Charting.Abstractions;
using Hevo.Charting.Layers;

namespace Hevo.Charting.Core
{
    /// <summary>万能标签契约:十字光标的 X/Y 标签共用此结构。</summary>
    /// <param name="Text">显示文本(已格式化好)。</param>
    /// <param name="BackgroundBrush">标签底色,通常取 CrosshairStyleTrait.LabelBrush。</param>
    /// <param name="Placement">所属轴方位,决定文本对齐方向(左轴右对齐,右轴左对齐)。</param>
    /// <param name="CustomPhysicalAnchor">CrosshairFeature 通过 AxisLayoutRegistryTrait 雷达扫描到的轴绝对像素。null 时画工退化到 plotArea 边缘兜底。</param>
    public record AxisLabel(
            string Text,
            IHevoBrush BackgroundBrush,
            AxisPlacement Placement = AxisPlacement.Left,
            double? CustomPhysicalAnchor = null
        );

    /// <summary>Tooltip 行数据。Name 是多语言可解析的 IHevoString;Value 是已格式化的实时字符串。</summary>
    public record TooltipRow(IHevoString Name, string Value, IHevoBrush? ValueBrush = null);

    /// <summary>
    /// 十字光标传递给渲染层的交互特质——纯渲染数据,layer 无须了解上游业务。
    /// </summary>
    /// <param name="IsActive">是否绘制十字光标</param>
    /// <param name="HighlightX">垂直线/X 标签/交点的横坐标(active 时一定有值)</param>
    /// <param name="HighlightY">
    /// 水平线/Y 标签的纵坐标。null = 上游告知本帧无意义的 Y(典型场景:联动 dashboard 中
    /// 镜像 hit 来自邻居 cell),layer 直接跳过水平方向绘制。
    /// </param>
    /// <param name="LabelX">X 轴标签</param>
    /// <param name="YLabels">Y 轴标签集合</param>
    /// <param name="Dots">数据交点</param>
    public record InteractionTrait(
        bool IsActive,
        float HighlightX,
        float? HighlightY,
        AxisLabel? LabelX,
        IReadOnlyList<AxisLabel>? YLabels,
        IReadOnlyList<CrosshairDotInfo>? Dots
    ) : IVisualTrait;
}
