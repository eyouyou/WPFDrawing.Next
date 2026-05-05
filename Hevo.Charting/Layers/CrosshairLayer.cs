using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using System.Windows.Media;

namespace Hevo.Charting.Layers
{
    public readonly struct CrosshairDotInfo
    {
        // 💥 改用跨平台极轻量级浮点向量
        public HevoPoint Position { get; }
        public IHevoBrush Brush { get; }

        public CrosshairDotInfo(HevoPoint position, IHevoBrush brush)
        {
            Position = position;
            Brush = brush;
        }
    }

    /// <summary>
    /// 💥 全局十字光标格式化契约
    /// </summary>
    public record CrosshairFormatterTrait(
        Func<double, string>? FormatX = null,
        Func<double, string>? FormatY = null
    ) : IVisualTrait;

    /// <summary>
    /// 💥 十字光标样式特征 (全副武装：彻底消灭 Layer 内的魔法配置)
    /// </summary>
    public record CrosshairStyleTrait(
        HevoPen LinePen,
        IHevoBrush TooltipBgBrush,
        HevoPen TooltipBorderPen,
        IHevoBrush TextBrush,
        IHevoBrush LabelBrush,

        // 💥 新增：把之前写死在 Layer 里的魔法配置全部提上来！
        IHevoTypeface Typeface,
        double FontSize = 12.0,
        double PaddingX = 6.0,
        double PaddingY = 2.0,
        double DotRadius = 4.0
    ) : IVisualTrait
    {
        // 极简工厂方法 (提供常用的快捷构造)
        public static CrosshairStyleTrait CreateSolid(
            Color lineColor, double lineThickness,
            Color tooltipBgColor, Color tooltipBorderColor,
            Color textColor, Color labelColor,
            string fontFamily = "Arial", double fontSize = 12.0) // 💥 暴露字体和字号
        {
            return new CrosshairStyleTrait(
                LinePen: new HevoPen(new HevoSolidBrush(lineColor), lineThickness, new double[] { 4, 4 }),
                TooltipBgBrush: new HevoSolidBrush(tooltipBgColor),
                TooltipBorderPen: new HevoPen(new HevoSolidBrush(tooltipBorderColor), 1.0),
                TextBrush: new HevoSolidBrush(textColor),
                LabelBrush: new HevoSolidBrush(labelColor),
                Typeface: new HevoTypeface(fontFamily, 400),
                FontSize: fontSize
            );
        }

        public static readonly CrosshairStyleTrait DefaultDark = CreateSolid(
            lineColor: Color.FromArgb(150, 150, 150, 150),
            lineThickness: 1.0,
            tooltipBgColor: Color.FromArgb(230, 20, 20, 20),
            tooltipBorderColor: Colors.Transparent,
            textColor: Colors.White,
            labelColor: Color.FromRgb(150, 150, 150)
        );

        public static readonly CrosshairStyleTrait DefaultLight = CreateSolid(
            lineColor: Color.FromArgb(150, 100, 100, 100),
            lineThickness: 1.0,
            tooltipBgColor: Color.FromArgb(230, 255, 255, 255),
            tooltipBorderColor: Colors.Transparent,
            textColor: Colors.Black,
            labelColor: Color.FromRgb(100, 100, 100)
        );
    }

    /// <summary>
    /// 十字光标渲染图层：与 AxisLayer 共享同样的边缘对齐逻辑
    /// </summary>
    public partial class CrosshairLayer : ChartLayer
    {
        public CrosshairLayer()
        {
            Name = "SharedCrosshair";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Interaction;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var plotTrait = data.Get<PlotAreaTrait>();
            var interaction = data.Get<InteractionTrait>();
            var style = data.Get<CrosshairStyleTrait>() ?? CrosshairStyleTrait.DefaultDark;

            if (plotTrait == null || plotTrait.Area.IsEmpty || interaction == null || !interaction.IsActive) return;

            HevoRect plot = plotTrait.Area;

            float clampedX = Math.Clamp(interaction.HighlightX, plot.Left, plot.Right);
            // HighlightY 为 null 表示上游本帧不提供 Y 焦点(水平方向无意义),layer 跳过水平线/Y 标签。
            float? clampedY = interaction.HighlightY is float y ? Math.Clamp(y, plot.Top, plot.Bottom) : null;

            using (draw.PushClip(plot))
            {
                draw.DrawLine(style.LinePen, new HevoPoint(clampedX, plot.Top), new HevoPoint(clampedX, plot.Bottom));
                if (clampedY is float cy)
                    draw.DrawLine(style.LinePen, new HevoPoint(plot.Left, cy), new HevoPoint(plot.Right, cy));
            }

            if (interaction.Dots != null && interaction.Dots.Count > 0)
            {
                foreach (var dot in interaction.Dots)
                {
                    if (dot.Position.Y < plot.Top || dot.Position.Y > plot.Bottom) continue;
                    draw.DrawEllipse(dot.Brush, style.TooltipBorderPen, dot.Position, (float)style.DotRadius, (float)style.DotRadius);
                }
            }

            if (interaction.LabelX != null)
            {
                draw.DrawText(interaction.LabelX.Text, style.Typeface, style.TextBrush, (float)style.FontSize,
                    new HevoPoint(clampedX, plot.Bottom), TextAlignX.Center, TextAlignY.Top, interaction.LabelX.BackgroundBrush, style.TooltipBorderPen, (float)style.PaddingX, (float)style.PaddingY);
            }

            if (clampedY is float labelY && interaction.YLabels != null && interaction.YLabels.Count > 0)
            {
                foreach (var lbl in interaction.YLabels)
                {
                    // 🚀 画工直接听命：如果有大脑配置的绝对坐标就死死吸附！
                    // 如果为 null，就用当前的 plot.Left / Right 兜底，完美对齐带 Padding 的画布边缘！
                    double drawX = lbl.CustomPhysicalAnchor ?? (lbl.Placement == AxisPlacement.Right ? plot.Right : plot.Left);

                    // 🚀 文本对齐严格按 Placement：左轴向左排版，右轴向右排版（CustomPhysicalAnchor 只决定贴在哪条轴上）
                    var alignX = lbl.Placement == AxisPlacement.Right ? TextAlignX.Left : TextAlignX.Right;

                    draw.DrawText(lbl.Text, style.Typeface, style.TextBrush, (float)style.FontSize,
                        new HevoPoint((float)drawX, labelY), alignX, TextAlignY.Center, lbl.BackgroundBrush, style.TooltipBorderPen, (float)style.PaddingX, (float)style.PaddingY);
                }
            }
        }
    }
}
