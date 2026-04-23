using Hevo.Charting.Abstractions;
using Hevo.Charting.Renderers;
using System.Windows.Media;

namespace Hevo.Charting.Core
{
    public record LineStyle(HevoPen LinePen, bool IsSmooth = false) : IVisualTrait
    {
        // 💥 1. 核心工厂方法：接收最高抽象的 IHevoBrush
        public static LineStyle Create(IHevoBrush brush, double thickness = 1.0, bool isSmooth = false)
        {
            return new LineStyle(new HevoPen(brush, thickness), isSmooth);
        }

        // 语法糖：向下兼容 Color 调用
        public static LineStyle Create(Color color, double thickness = 1.0, bool isSmooth = false)
        {
            return new LineStyle(new HevoPen(new HevoSolidBrush(color), thickness), isSmooth);
        }

        // 语法糖：直接从资源键生成
        public static LineStyle FromResource(string resourceKey, double thickness = 1.0, bool isSmooth = false)
        {
            return new LineStyle(new HevoPen(new HevoResourceBrush(resourceKey), thickness), isSmooth);
        }

        public static readonly LineStyle Default = Create(Colors.Blue, 1.0, false);
    }

    public static class LineLayerExtensions
    {
        // 💥 2. 核心扩展方法：参数升级为 IHevoBrush
        public static VisualProxy<T> WithLine<T>(this VisualProxy<T> proxy, IHevoBrush? brush = null, double? thickness = null, bool? isSmooth = null)
            where T : IChartLayer, IConsumes<LineStyle>
        {
            return proxy.UpdateData<LineStyle>(old =>
            {
                var baseStyle = old ?? LineStyle.Default;

                HevoPen newPen = baseStyle.LinePen;

                // 如果外部传入了新画刷或新线宽，才需要重新构建 HevoPen
                if (brush != null || thickness.HasValue)
                {
                    // 💥 终极避坑：安全的多态获取！绝不用强转去拿 Color！
                    // 旧画刷可能是 Solid 也可能是 Resource，不管它是什么，直接复用它的 IHevoBrush 接口！
                    var b = brush ?? baseStyle.LinePen.Brush;
                    var t = thickness ?? baseStyle.LinePen.Thickness;
                    newPen = new HevoPen(b, t);
                }

                return baseStyle with
                {
                    LinePen = newPen,
                    IsSmooth = isSmooth ?? baseStyle.IsSmooth
                };
            });
        }

        // 💥 3. 语法糖扩展方法：保护外部业务层之前写死的 Color 代码不用大改
        public static VisualProxy<T> WithLine<T>(this VisualProxy<T> proxy, Color color, double? thickness = null, bool? isSmooth = null)
            where T : IChartLayer, IConsumes<LineStyle>
        {
            return proxy.WithLine(new HevoSolidBrush(color), thickness, isSmooth);
        }
    }

    public partial class LineLayer : ChartLayer
    {
        // 跨帧复用的工作缓冲(WPF/Skia 渲染均在同一 UI tick 内同步消费 front buffer,
        // 下一帧 OnUpdate 回到本方法时,上一帧引用已被 ChartCell.PostRender + back/front swap 后的 Clear 释放。)
        private readonly List<HevoPoint> _screenPoints = new(2048);
        private readonly List<HevoPoint> _trimmedPoints = new(2048);
        private readonly List<HevoPoint> _smoothPoints = new(8192);

        public LineLayer()
        {
            Name = "LineSeries";
            Mode = RenderMode.Hardware;
            Level = ChartLayerType.Main;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var doubleSeries = data.Get<DoubleSeriesDataTrait>();

            var style = data.Get<LineStyle>() ?? LineStyle.Default;
            var axis = data.Get<ScaleStrategyTrait>();
            var plotAreaTrait = data.Get<PlotAreaTrait>();
            var xAxisTrait = data.Get<XAxisTrait>();
            var yAxisTrait = data.Get<YAxisTrait>();

            if (doubleSeries == null || plotAreaTrait == null || xAxisTrait == null || yAxisTrait == null || axis == null) return;

            HevoRect area = plotAreaTrait.Area;
            RealRange rangeX = xAxisTrait.Viewport;
            RealRange rangeY = yAxisTrait.Viewport;

            if (area.Width <= 0 || area.Height <= 0 || rangeX.Span <= 0 || rangeY.Span <= 0) return;

            int count = doubleSeries!.FieldValues[0].Length;

            // 采样口径与 UniversalAutoScaleFeature 对齐:Floor/Ceiling 在非整数 rangeX 时天然具备 ±1 视觉 padding。
            int startIndex = Math.Clamp((int)Math.Floor(rangeX.Min), 0, count - 1);
            int endIndex = Math.Clamp((int)Math.Ceiling(rangeX.Max), 0, count - 1);

            if (startIndex > endIndex) return;

            _screenPoints.Clear();
            _trimmedPoints.Clear();

            ReadOnlySpan<double> doubleSpan = doubleSeries!.FieldValues[0].Span;

            for (int i = startIndex; i <= endIndex; i++)
            {
                double val = doubleSpan[i];
                if (double.IsNaN(val)) continue;

                HevoPoint p = CoordinateExtensions.ProjectToScreen(area, rangeX, rangeY, axis, i, val);

                if (!float.IsNaN(p.X) && !float.IsInfinity(p.X) && !float.IsNaN(p.Y) && !float.IsInfinity(p.Y))
                {
                    _screenPoints.Add(p);
                }
            }

            if (_screenPoints.Count < 2) return;

            // 数学精确裁剪:首尾段按 plotArea 边界线性插值,跨边界段按原斜率收束到精确像素。
            TrimPolylineToPlotArea(_screenPoints, area, _trimmedPoints);
            if (_trimmedPoints.Count < 2) return;

            if (style.IsSmooth)
            {
                _smoothPoints.Clear();
                SplineAlgorithm.GetCatmullRomSpline(_trimmedPoints, 0.5, 8, _smoothPoints);
                draw.DrawPolyline(style.LinePen, _smoothPoints);
            }
            else
            {
                draw.DrawPolyline(style.LinePen, _trimmedPoints);
            }
        }

        // 单遍正向扫描:input 按 X 单调,左穿入补交点,右穿出补交点,内点直接拷贝。
        // 0-GC:全部追加写入 output,无 RemoveRange/Insert 中部数组移位。
        private static void TrimPolylineToPlotArea(List<HevoPoint> input, HevoRect area, List<HevoPoint> output)
        {
            int n = input.Count;
            if (n == 0) return;

            int i = 0;
            while (i < n && input[i].X < area.Left) i++;
            if (i >= n) return;

            if (i > 0)
            {
                var outside = input[i - 1];
                var inside = input[i];
                float dx = inside.X - outside.X;
                float entryY = dx == 0 ? inside.Y : outside.Y + (area.Left - outside.X) / dx * (inside.Y - outside.Y);
                output.Add(new HevoPoint(area.Left, entryY));
            }

            while (i < n && input[i].X <= area.Right)
            {
                output.Add(input[i]);
                i++;
            }

            if (i > 0 && i < n)
            {
                var inside = input[i - 1];
                var outside = input[i];
                float dx = outside.X - inside.X;
                float exitY = dx == 0 ? inside.Y : inside.Y + (area.Right - inside.X) / dx * (outside.Y - inside.Y);
                output.Add(new HevoPoint(area.Right, exitY));
            }
        }
    }

    public static class SplineAlgorithm
    {
        /// <summary>
        /// 0-GC 版:输出写入调用方提供的 result 缓冲。alpha 当前未使用(预留接口)。
        /// </summary>
        public static void GetCatmullRomSpline(List<HevoPoint> points, double alpha, int detail, List<HevoPoint> result)
        {
            if (points.Count < 2)
            {
                for (int i = 0; i < points.Count; i++) result.Add(points[i]);
                return;
            }

            var p0 = points[0];
            var pLast = points[points.Count - 1];

            for (int i = 0; i < points.Count - 1; i++)
            {
                HevoPoint p_1 = (i == 0) ? p0 : points[i - 1];
                HevoPoint p_0 = points[i];
                HevoPoint p_1_next = points[i + 1];
                HevoPoint p_2_next = (i + 2 < points.Count) ? points[i + 2] : pLast;

                for (int j = 0; j < detail; j++)
                {
                    double t = (double)j / detail;
                    result.Add(Interpolate(p_1, p_0, p_1_next, p_2_next, t));
                }
            }

            result.Add(pLast);
        }

        // Catmull-Rom 公式 (底层运算依然保持 double 稳健性，仅在出口强制降维)
        private static HevoPoint Interpolate(HevoPoint p0, HevoPoint p1, HevoPoint p2, HevoPoint p3, double t)
        {
            double t2 = t * t;
            double t3 = t2 * t;

            double x = 0.5 * ((2.0 * p1.X) +
                (-p0.X + p2.X) * t +
                (2.0 * p0.X - 5.0 * p1.X + 4.0 * p2.X - p3.X) * t2 +
                (-p0.X + 3.0 * p1.X - 3.0 * p2.X + p3.X) * t3);

            double y = 0.5 * ((2.0 * p1.Y) +
                (-p0.Y + p2.Y) * t +
                (2.0 * p0.Y - 5.0 * p1.Y + 4.0 * p2.Y - p3.Y) * t2 +
                (-p0.Y + 3.0 * p1.Y - 3.0 * p2.Y + p3.Y) * t3);

            return new HevoPoint((float)x, (float)y);
        }
    }
}
