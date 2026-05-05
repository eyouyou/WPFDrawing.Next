using Hevo.Charting.Abstractions;
using Hevo.Charting.Renderers;
using System.Windows.Media;

namespace Hevo.Charting.Core
{
    /// <summary>蜡烛图渲染数据包(冷热分离的双 layer 共用此结构)。</summary>
    /// <param name="StartIndex">本切片首根 K 线的世界索引(动态层 = 末根索引;静态层 = 0)。</param>
    /// <param name="Opens">开盘价数组。</param>
    /// <param name="Highs">最高价数组。</param>
    /// <param name="Lows">最低价数组。</param>
    /// <param name="Closes">收盘价数组。</param>
    public record CandleData(
            int StartIndex,
            ReadOnlyMemory<double> Opens,
            ReadOnlyMemory<double> Highs,
            ReadOnlyMemory<double> Lows,
            ReadOnlyMemory<double> Closes) : IVisualTrait;

    /// <summary>蜡烛图样式。</summary>
    /// <param name="UpBrush">阳线(收 ≥ 开)柱体填充。A 股惯例为红色。</param>
    /// <param name="DownBrush">阴线(收 &lt; 开)柱体填充。A 股惯例为绿色。</param>
    /// <param name="WickPen">影线画笔(上下影线共用)。</param>
    /// <param name="BodyPadding">柱体相对单格距离的内缩比例,0.2 即两侧各留 10% 空隙。</param>
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
        // 跨帧复用,首次 OnUpdate 后 capacity 锁定在水位线,后续帧 0 分配。
        // 256 ≈ 单屏典型 K 线根数(120~250)的常态上限;512 = wick 走 LineSegments 每根 K 线 2 个端点。
        // 不暴露成配置:作用纯粹是"避免首次扩容拷贝",业务调小反加重 GC,调大也不省内存。
        private readonly List<HevoRect> _upRects = new(256);
        private readonly List<HevoRect> _downRects = new(256);
        private readonly List<HevoPoint> _wicks = new(512);

        public CandleLayer()
        {
            Name = "CandleStick";
            Mode = RenderMode.Software;
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

            _upRects.Clear();
            _downRects.Clear();
            _wicks.Clear();

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

                _wicks.Add(new HevoPoint((float)xCenter, (float)yHigh));
                _wicks.Add(new HevoPoint((float)xCenter, (float)yLow));

                double top = Math.Min(yOpen, yClose);
                double bottom = Math.Max(yOpen, yClose);
                double height = Math.Max(1.0, bottom - top);

                HevoRect rect = new HevoRect((float)leftEdge, (float)top, (float)(rightEdge - leftEdge), (float)height);

                if (c >= o) _upRects.Add(rect);
                else _downRects.Add(rect);
            }

            using (draw.PushClip(plotArea))
            {
                if (_wicks.Count > 0)
                {
                    draw.DrawLineSegments(style.WickPen, _wicks);
                }

                if (_upRects.Count > 0) draw.DrawRectangles(style.UpBrush, null, _upRects);
                if (_downRects.Count > 0) draw.DrawRectangles(style.DownBrush, null, _downRects);
            }
        }
    }
}
