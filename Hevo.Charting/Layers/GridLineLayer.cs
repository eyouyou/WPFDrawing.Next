using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using System.Windows.Media;

namespace Hevo.Charting.Layers
{
    public enum GridOrientation { Horizontal, Vertical }

    /// <summary>
    /// 网格线特质：定义网格的方向和线条样式。
    /// <para>
    /// 默认所有 grid 走 <see cref="LineStyle"/> 的同一支笔；如需"0% 横线加粗"等强调，
    /// 由 <see cref="ITickStylePolicy.GetOverrideStyle"/> 在 per-tick 层面返回 LineStyle 即可，
    /// 不再靠 BaseLineStyle 二元方案 + 自动 +1 thickness 魔法。
    /// </para>
    /// </summary>
    public record GridStyleTrait(GridOrientation Orientation, LineStyle LineStyle) : IVisualTrait
    {
        // 💥 极简创建：直接传颜色和粗细，内部自动包装 LineStyle
        public static GridStyleTrait Create(GridOrientation orientation, Color color, double thickness = 1.0)
        {
            var line = LineStyle.Create(color, thickness, useSpline: false);
            return new GridStyleTrait(orientation, line);
        }

        public static GridStyleTrait Create(GridOrientation orientation, LineStyle style)
        {
            return new GridStyleTrait(orientation, style);
        }

        public static GridStyleTrait FromResource(GridOrientation orientation, string resourceKey, double thickness = 1.0)
        {
            var line = LineStyle.FromResource(resourceKey, thickness, useSpline: false);
            return new GridStyleTrait(orientation, line);
        }

        // 💥 预设：常用的浅灰色虚线网格
        public static GridStyleTrait DefaultHorizontal
            => Create(GridOrientation.Horizontal, Colors.LightGray);

        public static GridStyleTrait DefaultVertical
            => Create(GridOrientation.Vertical, Colors.LightGray);
    }

    public partial class GridLineLayer : ChartLayer
    {
        public GridLineLayer(string name)
        {
            Name = name;
            // 修复历史: SkiaSharp.Views.WPF 在某些 fullscreen 尺寸下,thick stroke / fill rect 都会在
            // 对特定 X 列的子集行产生 ±1 列错位(WPF DrawingRenderer 路径无此 bug,跨 stroke / fill / path
            // 路径全数复现)。详见 07.Grid 宽度不均排查记录.md。Grid 是低频 Background 层,WPF 性能足够。
            Mode = RenderMode.Software;
            Level = ChartLayerType.Background; // ✨ 核心：网格必须在最底层，被 K 线遮挡
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var tickTrait = data.Get<AxisTickDataTrait>();
            var style = data.Get<GridStyleTrait>();
            var plotArea = data.Get<PlotAreaTrait>();

            if (tickTrait == null || style == null || plotArea == null || tickTrait.Count == 0)
                return;

            HevoRect area = plotArea.Area;
            var defaultPen = style.LineStyle.LinePen;

            // 💥 纯 O(N) 极速渲染：利用已经算好的 Ratio
            // 每根 tick 自带 OverrideStyle (per-tick pen) ，无则走 defaultPen——比"baseline 二元 + 自动 +1"灵活
            for (int i = 0; i < tickTrait.Count; i++)
            {
                ref var tick = ref tickTrait.Ticks[i];

                // 💥 与 AxisLayer 对齐：Ratio 越界的 tick 不能画网格线到 plotArea 外。
                //    NiceNumberYTickStrategy 为了"优美对齐"会向外多生成刻度，这里必须守门。
                if (tick.Ratio < 0 || tick.Ratio > 1) continue;

                var activePen = tick.OverrideStyle?.LinePen ?? defaultPen;

                if (style.Orientation == GridOrientation.Horizontal)
                {
                    float py = (float)(area.Bottom - tick.Ratio * area.Height);
                    draw.DrawLine(activePen, new HevoPoint(area.Left, py), new HevoPoint(area.Right, py));
                }
                else
                {
                    float px = (float)(area.Left + tick.Ratio * area.Width);
                    draw.DrawLine(activePen, new HevoPoint(px, area.Top), new HevoPoint(px, area.Bottom));
                }
            }
        }
    }
}
