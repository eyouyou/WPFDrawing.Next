using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Renderers;
using System.Windows.Media;

namespace Hevo.Charting.Layers
{
    /// <summary>
    /// 数学方程线渲染层 —— 消费 <see cref="MathLineSpecTrait"/> + <see cref="PlotAreaTrait"/>,
    /// (x, y) 坐标系投影成两端点像素 → <see cref="IDrawingSink.DrawLine"/> + 可选数学公式 label。
    /// </summary>
    public sealed class MathLineLayer : ChartLayer
    {
        public MathLineLayer()
        {
            Name = "MathLine";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Main;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var spec = data.Get<MathLineSpecTrait>();
            var plot = data.Get<PlotAreaTrait>();
            if (spec == null || plot == null) return;

            var area = plot.Area;
            if (area.Width <= 0 || area.Height <= 0) return;
            if (!spec.XRange.IsValid || !spec.YRange.IsValid) return;

            double xSpan = spec.XRange.Span;
            double ySpan = spec.YRange.Span;
            if (xSpan <= 0 || ySpan <= 0) return;

            // (x, y) → pixel 投影,跟 ScatterPlotLayer 同源
            HevoPoint Project(MathLinePoint p) => new(
                (float)(area.Left + (p.X - spec.XRange.Min) / xSpan * area.Width),
                (float)(area.Top + (1 - (p.Y - spec.YRange.Min) / ySpan) * area.Height));

            var sp1 = Project(spec.P1);
            var sp2 = Project(spec.P2);

            // 画线 —— layer 自动 clip 到 PlotArea,端点 y 越界由 sink 内部处理
            draw.DrawLine(spec.Style.LinePen, sp1, sp2);

            // 画 label
            if (!string.IsNullOrEmpty(spec.LabelText))
            {
                HevoPoint anchor = spec.Placement switch
                {
                    MathLineLabelPlacement.LineStart => sp1,
                    MathLineLabelPlacement.LineMid   => new HevoPoint((sp1.X + sp2.X) / 2f, (sp1.Y + sp2.Y) / 2f),
                    _ /* LineEnd */                  => sp2,
                };

                // LineEnd:文本右对齐,锚点稍左上偏移,避免文本越出 plotArea 右边
                // LineStart:文本左对齐,锚点稍右上偏移
                // LineMid:居中
                TextAlignX alignX;
                float dx;
                switch (spec.Placement)
                {
                    case MathLineLabelPlacement.LineEnd:
                        alignX = TextAlignX.Right; dx = -6f; break;
                    case MathLineLabelPlacement.LineStart:
                        alignX = TextAlignX.Left; dx = 6f; break;
                    default:
                        alignX = TextAlignX.Center; dx = 0f; break;
                }
                var labelPos = new HevoPoint(anchor.X + dx, anchor.Y - 4f);

                var textBrush = spec.Style.LinePen.Brush ?? new HevoSolidBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
                draw.DrawText(spec.LabelText, new HevoTypeface(spec.LabelFontFamily),
                    textBrush, (float)spec.LabelFontSize, labelPos,
                    alignX, TextAlignY.Bottom);
            }
        }
    }
}
