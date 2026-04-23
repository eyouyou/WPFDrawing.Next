using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using System.Windows.Media;

namespace Hevo.Charting.Layers
{
    /// <summary>
    /// 万能策略载体。record 自动 Equals，策略参数真变才触发下游更新。
    /// </summary>
    public record TickStrategyTrait(ITickStrategy Strategy) : IVisualTrait
    {
        public static TickStrategyTrait Create(ITickStrategy strategy) => new(strategy);
    }

    public record AxisRangeTrait(RealRange Range) : IVisualTrait
    {
        public static AxisRangeTrait Create(double min, double max) => new(new RealRange(min, max));
        public static readonly AxisRangeTrait Empty = new(RealRange.Empty);
    }

    public record AxisTickDataTrait(TickModel[] Ticks, int Count) : IVisualTrait
    {
        public static AxisTickDataTrait Empty() => new(Array.Empty<TickModel>(), 0);
    }

    public enum AxisPlacement { Top, Bottom, Left, Right }

    // ==========================================
    // 💥 坐标轴纯视觉 Trait
    // 注意这里没有 Handle，也没有 Port，保持了对视图层的绝对纯净！
    // ==========================================
    public record AxisStyleTrait(
            AxisPlacement Placement,
            IHevoBrush TextBrush,
            double FontSize,
            HevoTypeface Typeface,
            LineStyle? BaseLineStyle = null,
            double TickLabelOffset = 5.0,
            double TickMarkLength = 4.0,
            double? CustomPhysicalAnchor = null,

            // 💥 供 Feature 运行时 with 修改的属性！
            double AbsoluteAnchor = 0.0
        ) : IVisualTrait
    {
        public static AxisStyleTrait Create(
            AxisPlacement placement,
            Color textColor,
            LineStyle? baseLineStyle = null,
            double fontSize = 11.0,
            double labelOffset = 5.0,
            string fontFamily = "Microsoft YaHei",
            double? customPhysicalAnchor = null)
        {
            return new AxisStyleTrait(placement, new HevoSolidBrush(textColor), fontSize, new HevoTypeface(fontFamily), baseLineStyle, labelOffset, 4.0, customPhysicalAnchor);
        }
    }

    public record AxisLayoutTrait(double AbsoluteAnchor) : IVisualTrait;

    public partial class AxisLayer : ChartLayer
    {
        public AxisLayer(string name)
        {
            Name = name;
            Mode = RenderMode.Software;
            Level = ChartLayerType.Background;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var tickTrait = data.Get<AxisTickDataTrait>();
            var axisTrait = data.Get<AxisStyleTrait>();
            var plotArea = data.Get<PlotAreaTrait>();

            // 💥 取消接收 AxisScaleTrait，无需二次解包！
            if (tickTrait == null || axisTrait == null || plotArea == null || tickTrait.Count == 0) return;

            // 💥 更换为全新的 HevoRect
            HevoRect area = plotArea.Area;

            double anchorX = axisTrait.CustomPhysicalAnchor ?? (axisTrait.Placement == AxisPlacement.Right ? area.Right : area.Left);
            double anchorY = axisTrait.CustomPhysicalAnchor ?? (axisTrait.Placement == AxisPlacement.Top ? area.Top : area.Bottom);

            HevoPen? tickPen = axisTrait.BaseLineStyle?.LinePen;

            for (int i = 0; i < tickTrait.Count; i++)
            {
                ref var tick = ref tickTrait.Ticks[i];

                // 💥 直接拦截：判断 Feature 传过来的最终洗礼版 Ratio 是否越界
                if (tick.Ratio < 0 || tick.Ratio > 1) continue;

                // ==========================================
                // 💥 极致纯洁的视图渲染：0反推，0逻辑！
                // tick.Ratio 已经是绝对正确的物理屏幕百分比，直接乘！
                // ==========================================
                double px = area.Left + tick.Ratio * area.Width;
                double py = area.Bottom - tick.Ratio * area.Height;

                if (tick.IsBaseLine && tickPen != null)
                {
                    using (draw.PushPixelSnapping((float)tickPen.Thickness))
                    {
                        if (axisTrait.Placement == AxisPlacement.Left || axisTrait.Placement == AxisPlacement.Right)
                            draw.DrawLine(tickPen, new HevoPoint((float)area.Left, (float)py), new HevoPoint((float)area.Right, (float)py));
                        else
                            draw.DrawLine(tickPen, new HevoPoint((float)px, (float)area.Top), new HevoPoint((float)px, (float)area.Bottom));
                    }
                }

                var textBrush = tick.OverrideTextBrush ?? (tick.IsBaseLine && tickPen != null ? tickPen.Brush : axisTrait.TextBrush);
                double offset = axisTrait.TickLabelOffset;
                double tLen = axisTrait.TickMarkLength;

                // 💥 文本渲染，坐标和大小一律安全降维至 float
                switch (axisTrait.Placement)
                {
                    case AxisPlacement.Bottom:
                        if (tickPen != null) draw.DrawLine(tickPen, new HevoPoint((float)px, (float)anchorY), new HevoPoint((float)px, (float)(anchorY + tLen)));
                        draw.DrawText(tick.Label, axisTrait.Typeface, textBrush, (float)axisTrait.FontSize, new HevoPoint((float)px, (float)(anchorY + offset)), TextAlignX.Center, TextAlignY.Top);
                        break;
                    case AxisPlacement.Top:
                        if (tickPen != null) draw.DrawLine(tickPen, new HevoPoint((float)px, (float)anchorY), new HevoPoint((float)px, (float)(anchorY - tLen)));
                        draw.DrawText(tick.Label, axisTrait.Typeface, textBrush, (float)axisTrait.FontSize, new HevoPoint((float)px, (float)(anchorY - offset)), TextAlignX.Center, TextAlignY.Bottom);
                        break;
                    case AxisPlacement.Left:
                        if (tickPen != null) draw.DrawLine(tickPen, new HevoPoint((float)anchorX, (float)py), new HevoPoint((float)(anchorX - tLen), (float)py));
                        draw.DrawText(tick.Label, axisTrait.Typeface, textBrush, (float)axisTrait.FontSize, new HevoPoint((float)(anchorX - offset), (float)py), TextAlignX.Right, TextAlignY.Center);
                        break;
                    case AxisPlacement.Right:
                        if (tickPen != null) draw.DrawLine(tickPen, new HevoPoint((float)anchorX, (float)py), new HevoPoint((float)(anchorX + tLen), (float)py));
                        draw.DrawText(tick.Label, axisTrait.Typeface, textBrush, (float)axisTrait.FontSize, new HevoPoint((float)(anchorX + offset), (float)py), TextAlignX.Left, TextAlignY.Center);
                        break;
                }
            }
        }
    }
}
