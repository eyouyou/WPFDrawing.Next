using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using System.Windows.Media;

namespace Hevo.Charting.Layers
{
    public enum GridOrientation { Horizontal, Vertical }

    /// <summary>
    /// 网格线特质：定义网格的方向和线条样式
    /// </summary>
    public record GridStyleTrait(GridOrientation Orientation, LineStyle LineStyle) : IVisualTrait
    {
        // 💥 极简创建：直接传颜色和粗细，内部自动包装 LineStyle
        public static GridStyleTrait Create(GridOrientation orientation, Color color, double thickness = 0.5)
        {
            var line = LineStyle.Create(color, thickness, isSmooth: false);
            return new GridStyleTrait(orientation, line);
        }

        public static GridStyleTrait Create(GridOrientation orientation, LineStyle style)
        {
            return new GridStyleTrait(orientation, style);
        }

        public static GridStyleTrait FromResource(GridOrientation orientation, string resourceKey, double thickness = 0.5)
        {
            // 内部调用 LineStyle.FromResource，并强制 isSmooth 为 false (网格线不需要平滑)
            var line = LineStyle.FromResource(resourceKey, thickness, isSmooth: false);
            return new GridStyleTrait(orientation, line);
        }

        // 💥 预设：常用的浅灰色虚线网格
        public static GridStyleTrait DefaultHorizontal
            => Create(GridOrientation.Horizontal, Colors.LightGray);

        public static GridStyleTrait DefaultVertical
            => Create(GridOrientation.Vertical, Colors.LightGray);
    }

    public partial class GridLineLayer<TDomain> : ChartLayer
    {
        public GridLineLayer(string name)
        {
            Name = name;
            Mode = RenderMode.Hardware; // 纯线条，完全可以交给 Skia/硬件加速
            Level = ChartLayerType.Background; // ✨ 核心：网格必须在最底层，被 K 线遮挡
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var tickTrait = data.Get<AxisTickDataTrait<TDomain>>();
            var style = data.Get<GridStyleTrait>();
            var plotArea = data.Get<PlotAreaTrait>();

            if (tickTrait == null || style == null || plotArea == null || tickTrait.Count == 0)
                return;

            HevoRect area = plotArea.Area;
            var pen = style.LineStyle.LinePen;

            // 💥 纯 O(N) 极速渲染：利用已经算好的 Ratio
            using (draw.PushPixelSnapping((float)pen.Thickness))
            {
                for (int i = 0; i < tickTrait.Count; i++)
                {
                    ref var tick = ref tickTrait.Ticks[i];
                    if (tick.IsBaseLine) continue;

                    if (style.Orientation == GridOrientation.Horizontal)
                    {
                        float py = (float)(area.Bottom - tick.Ratio * area.Height);
                        draw.DrawLine(pen, new HevoPoint(area.Left, py), new HevoPoint(area.Right, py)); // 告别 PixelSnapper！
                    }
                    else
                    {
                        float px = (float)(area.Left + tick.Ratio * area.Width);
                        draw.DrawLine(pen, new HevoPoint(px, area.Top), new HevoPoint(px, area.Bottom)); // 告别 PixelSnapper！
                    }
                }
            }
        }
    }
}
