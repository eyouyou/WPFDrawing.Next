using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;

namespace Hevo.Charting.Layers
{
    /// <summary>
    /// 主绘图区装饰特质：负责传递背景色与边框样式
    /// </summary>
    public record PlotAreaDecorTrait(IHevoBrush? BackgroundBrush, LineStyle? BorderStyle) : IVisualTrait;
    public partial class PlotAreaDecorLayer : ChartLayer
    {
        public PlotAreaDecorLayer()
        {
            Name = "PlotAreaDecorLayer";
            Mode = RenderMode.Hardware;
            Level = ChartLayerType.Background; // 💥 必须在最底层打底
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var decor = data.Get<PlotAreaDecorTrait>();
            var plotArea = data.Get<PlotAreaTrait>();

            if (decor == null || plotArea == null || plotArea.Area.IsEmpty) return;

            if (decor.BorderStyle != null)
            {
                double t = decor.BorderStyle.LinePen.Thickness;
                double halfT = t / 2.0;

                // 💥 避免边框被外围 Clip 裁掉一半，需向内收缩半个线宽 (保持纯数学运算)
                HevoRect strokeArea = new HevoRect(
                                    (float)(plotArea.Area.Left + halfT),
                                    (float)(plotArea.Area.Top + halfT),
                                    (float)Math.Max(0, plotArea.Area.Width - t),
                                    (float)Math.Max(0, plotArea.Area.Height - t));

                // 撑开伞，实现边框锋利
                using (draw.PushPixelSnapping((float)t))
                {
                    draw.DrawRectangle(decor.BackgroundBrush, decor.BorderStyle.LinePen, strokeArea);
                }
            }
            else if (decor.BackgroundBrush != null)
            {
                // 无边框纯背景，厚度为 0
                using (draw.PushPixelSnapping(0))
                {
                    draw.DrawRectangle(decor.BackgroundBrush, null, plotArea.Area);
                }
            }
        }
    }
}
