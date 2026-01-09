using Hevo.Charting.Abstractions;
using Hevo.Charting.Renderers;
using System.Windows.Media;

namespace Hevo.Charting.Core
{
    public record CandleData(
            int StartIndex,
            ReadOnlyMemory<double> Opens,
            ReadOnlyMemory<double> Highs,
            ReadOnlyMemory<double> Lows,
            ReadOnlyMemory<double> Closes) : IVisualTrait;

    public record CandleStyle(
        IHevoBrush UpBrush,
        IHevoBrush DownBrush,
        HevoPen WickPen,
        double BodyPadding = 0.2
    ) : IVisualTrait
    {
        public static CandleStyle CreateSolid(
            Color upColor,
            Color downColor,
            Color wickColor,
            double wickWidth = 1.0,
            double bodyPadding = 0.2)
        {
            return new CandleStyle(
                UpBrush: new HevoSolidBrush(upColor),
                DownBrush: new HevoSolidBrush(downColor),
                WickPen: new HevoPen(new HevoSolidBrush(wickColor), wickWidth),
                BodyPadding: bodyPadding
            );
        }

        public static readonly CandleStyle Default = CreateSolid(
            upColor: Color.FromRgb(234, 71, 109),
            downColor: Color.FromRgb(38, 166, 154),
            wickColor: Color.FromRgb(128, 128, 128)
        );
    }

    public partial class CandleLayer : ChartLayer
    {
        public CandleLayer()
        {
            Name = "CandleStick";
            Mode = RenderMode.Hardware;
            Level = ChartLayerType.Main;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var candleData = data.Get<CandleData>();
            var plotAreaTrait = data.Get<PlotAreaTrait>();
            var xAxisTrait = data.Get<XAxisTrait>();
            var yAxisTrait = data.Get<YAxisTrait>();

            // 默认兜底，但绝不会干扰其数学计算纯洁性
            var axis = data.Get<ScaleStrategyTrait>();
            var style = data.Get<CandleStyle>() ?? CandleStyle.Default;

            if (candleData == null || plotAreaTrait == null || xAxisTrait == null || yAxisTrait == null || axis == null) return;

            HevoRect plotArea = plotAreaTrait.Area;
            RealRange xRange = xAxisTrait.Viewport;
            RealRange yRange = yAxisTrait.Viewport;

            if (!xRange.IsValid || !yRange.IsValid || plotArea.Width <= 0 || plotArea.Height <= 0) return;

            var opens = candleData.Opens.Span;
            var highs = candleData.Highs.Span;
            var lows = candleData.Lows.Span;
            var closes = candleData.Closes.Span;

            int dataLength = opens.Length;
            if (dataLength == 0) return;

            int baseIndex = candleData.StartIndex;

            // 💥 纯 UI 层的平滑视口剔除 (Culling)，两端各缓冲 2 根，防止拖拽闪烁
            int viewStart = (int)Math.Floor(xRange.Min) - 2;
            int viewEnd = (int)Math.Ceiling(xRange.Max) + 2;

            int logicalStart = Math.Max(viewStart, baseIndex);
            int logicalEnd = Math.Min(viewEnd, baseIndex + dataLength - 1);

            if (logicalStart > logicalEnd) return;

            int capacity = logicalEnd - logicalStart + 1;

            // 💥 换装轻量级集合
            var upRects = new List<HevoRect>(capacity);
            var downRects = new List<HevoRect>(capacity);
            var wicks = new List<HevoPoint>(capacity * 2);

            // ==========================================
            // 💥 极致多态：利用 IScale 相邻索引差值，推算绝对物理宽度！
            // ==========================================
            double unitNormDelta = axis.DomainScale.Normalize(1, xRange) - axis.DomainScale.Normalize(0, xRange);
            double ppuX = plotArea.Width * Math.Abs(unitNormDelta);

            // 扣除业务所需的间隙留白 (BodyPadding)
            double bodyWidth = Math.Max(1.0, ppuX * (1.0 - style.BodyPadding));
            double halfBody = bodyWidth / 2.0;

            for (int logicalIndex = logicalStart; logicalIndex <= logicalEnd; logicalIndex++)
            {
                int arrayIndex = logicalIndex - baseIndex;

                double o = opens[arrayIndex];
                if (o <= 0) continue;
                double h = highs[arrayIndex];
                double l = lows[arrayIndex];
                double c = closes[arrayIndex];

                // 💥 绝对中心点：Scale 去操心居中偏移，Layer 只管画！
                double xNorm = axis.DomainScale.Normalize(logicalIndex, xRange);
                double xCenter = plotArea.Left + (xNorm * plotArea.Width);

                double leftEdge = xCenter - halfBody;
                double rightEdge = xCenter + halfBody;

                // 边界剔除
                if (leftEdge < plotArea.Left || rightEdge > plotArea.Right) continue;

                double yOpen = CoordinateExtensions.ProjectValueToScreen(plotArea, yRange, axis, o);
                double yHigh = CoordinateExtensions.ProjectValueToScreen(plotArea, yRange, axis, h);
                double yLow = CoordinateExtensions.ProjectValueToScreen(plotArea, yRange, axis, l);
                double yClose = CoordinateExtensions.ProjectValueToScreen(plotArea, yRange, axis, c);

                wicks.Add(new HevoPoint((float)xCenter, (float)yHigh));
                wicks.Add(new HevoPoint((float)xCenter, (float)yLow));

                double top = Math.Min(yOpen, yClose);
                double bottom = Math.Max(yOpen, yClose);
                double height = Math.Max(1.0, bottom - top);

                HevoRect rect = new HevoRect((float)leftEdge, (float)top, (float)(rightEdge - leftEdge), (float)height);

                if (c >= o) upRects.Add(rect);
                else downRects.Add(rect);
            }

            // 绘制与抗锯齿处理
            using (draw.PushClip(plotArea))
            {
                if (wicks.Count > 0)
                {
                    using (draw.PushPixelSnapping((float)style.WickPen.Thickness))
                    {
                        draw.DrawLineSegments(style.WickPen, wicks);
                    }
                }

                if (upRects.Count > 0 || downRects.Count > 0)
                {
                    using (draw.PushPixelSnapping(0.0f))
                    {
                        if (upRects.Count > 0) draw.DrawRectangles(style.UpBrush, null, upRects);
                        if (downRects.Count > 0) draw.DrawRectangles(style.DownBrush, null, downRects);
                    }
                }
            }
        }
    }
}
