using Hevo.Charting.Abstractions;
using Hevo.Charting.Layers;
using System.Windows;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 万能标签契约 (The Label Contract)
    /// </summary>
    /// <param name="Text"></param>
    /// <param name="BackgroundBrush"></param>
    /// <param name="Placement"></param>
    /// <param name="CustomPhysicalAnchor"></param>
    public record AxisLabel(
            string Text,
            IHevoBrush BackgroundBrush,
            AxisPlacement Placement = AxisPlacement.Left,

            // 💥 核心：雷达扫描到的确切物理像素将被注入到这里！
            // 如果有值，画工就会死死吸附在这个像素上 (支持图表中心悬浮轴)
            // 只有当为 null 时，画工(Layer)才会退化到去吸附带有 Padding 的图表边缘。
            double? CustomPhysicalAnchor = null
        );

    // Tooltip 行模型
    // 💥 Name 升级为 IHevoString，完美衔接多语言字典！Value 是实时数值，保留 string！
    public record TooltipRow(IHevoString Name, string Value, IHevoBrush? ValueBrush = null);

    /// <summary>
    /// 十字光标传递给渲染层的交互特质
    /// </summary>
    public record InteractionTrait(
        bool IsActive,
        HevoPoint HighlightPoint,
        AxisLabel? LabelX,
        IReadOnlyList<AxisLabel>? YLabels,
        IReadOnlyList<CrosshairDotInfo>? Dots
    ) : IVisualTrait;
}
