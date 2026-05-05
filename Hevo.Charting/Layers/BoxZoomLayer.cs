using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using System.Windows.Media;

namespace Hevo.Charting.Layers
{
    /// <summary>
    /// 框选缩放可视化特质：mouse 拖拽阶段实时反映选框矩形。
    /// IsActive=false 时 layer 直接清屏(transient overlay)。
    /// </summary>
    /// <param name="IsActive">是否绘制选框</param>
    /// <param name="Rect">选框物理矩形(已规整为 left ≤ right, top ≤ bottom)</param>
    /// <param name="Border">边框样式;null = 默认浅灰 1px 虚线</param>
    /// <param name="Fill">填充;null = 默认半透明蓝</param>
    public record BoxZoomTrait(bool IsActive, HevoRect Rect, LineStyle? Border, IHevoBrush? Fill) : IVisualTrait
    {
        public static readonly BoxZoomTrait Inactive = new(false, HevoRect.Empty, null, null);

        // 默认样式:跟主流图表一致的"半透明蓝矩形 + 浅灰虚线边框"。
        // freeze-style 全局复用,0-GC 友好。
        internal static readonly LineStyle DefaultBorder =
            LineStyle.Create(new HevoSolidBrush(Color.FromArgb(180, 200, 200, 200)), 1.0);

        internal static readonly IHevoBrush DefaultFill =
            new HevoSolidBrush(Color.FromArgb(40, 80, 140, 220));
    }

    /// <summary>
    /// 框选缩放渲染层:与 CrosshairLayer 共栖在 Interaction 层级,只在 trait IsActive 时画一个矩形。
    /// </summary>
    public class BoxZoomLayer : ChartLayer
    {
        public BoxZoomLayer()
        {
            Name = "BoxZoom";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Interaction;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var box = data.Get<BoxZoomTrait>();
            var plotTrait = data.Get<PlotAreaTrait>();
            if (box == null || !box.IsActive || plotTrait == null || plotTrait.Area.IsEmpty) return;

            // 选框只在 plot 区内显示,夹紧到 plot 矩形(用户可能拖出边)
            var clamped = box.Rect.Intersect(plotTrait.Area);
            if (clamped.IsEmpty) return;

            var border = box.Border ?? BoxZoomTrait.DefaultBorder;
            var fill = box.Fill ?? BoxZoomTrait.DefaultFill;

            using (draw.PushClip(plotTrait.Area))
                draw.DrawRectangle(fill, border.LinePen, clamped);
        }
    }
}
