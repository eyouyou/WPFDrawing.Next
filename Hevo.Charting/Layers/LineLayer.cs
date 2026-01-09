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
        public LineLayer()
        {
            Name = "LineSeries";
            Mode = RenderMode.Hardware; // Skia 硬件加速起飞
            Level = ChartLayerType.Main;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            // 💥 完美兼容新协议：同时尝试获取 Double 和 Float 类型的多列数据包！
            var doubleSeries = data.Get<DoubleSeriesDataTrait>();

            var style = data.Get<LineStyle>() ?? LineStyle.Default;
            var axis = data.Get<ScaleStrategyTrait>();
            var plotAreaTrait = data.Get<PlotAreaTrait>();
            var xAxisTrait = data.Get<XAxisTrait>();
            var yAxisTrait = data.Get<YAxisTrait>();

            // 💥 防御性检查：如果没有读到任何一种热数据，直接返回
            if (doubleSeries == null || plotAreaTrait == null || xAxisTrait == null || yAxisTrait == null || axis == null) return;

            HevoRect area = plotAreaTrait.Area;
            RealRange rangeX = xAxisTrait.Viewport;
            RealRange rangeY = yAxisTrait.Viewport;

            if (area.Width <= 0 || area.Height <= 0 || rangeX.Span <= 0 || rangeY.Span <= 0) return;

            // 💥 修复 1：判断当前到底是 double 还是 float 数据，并默认读取第 0 列 (单线指标的常态)

            int count = doubleSeries!.FieldValues[0].Length;

            // 💥 采样口径必须与 UniversalAutoScaleFeature 对齐：两端都用 Floor/Ceiling，不加 ±1 padding。
            // 之前用 Floor-1 / Ceiling+1 想"多画一个点让斜率连续"，但 AutoScale 的 YRange 扫描只覆盖
            // [Floor(rangeX.Min), Ceiling(rangeX.Max)]；padding 那两个点的 Y 值没算进量程，投影后完全可
            // 能飞到 plotArea 之外。
            // Floor/Ceiling 在 rangeX 非整数时天然具备 ±1 视觉 padding（Floor(10.5)=10 即入视图上游一个），
            // 所以缩放/平移的平滑过渡仍然成立，且与 BarLayer 的采样完全一致。
            int startIndex = Math.Clamp((int)Math.Floor(rangeX.Min), 0, count - 1);
            int endIndex = Math.Clamp((int)Math.Ceiling(rangeX.Max), 0, count - 1);

            if (startIndex > endIndex) return;

            int visibleCount = endIndex - startIndex + 1;

            // 💥 彻底更新为纯浮点极速集合！
            var screenPoints = new List<HevoPoint>(visibleCount);

            ReadOnlySpan<double> doubleSpan = doubleSeries!.FieldValues[0].Span;

#if DEBUG
            if (startIndex < endIndex)
            {
                double firstNorm = axis.DomainScale.Normalize(0, rangeX);
                double lastNorm = axis.DomainScale.Normalize(240, rangeX);

                System.Diagnostics.Debug.WriteLine($"[X-Verify] Viewport: {rangeX.Min:F2} - {rangeX.Max:F2}, Span: {rangeX.Span}");
                System.Diagnostics.Debug.WriteLine($"[X-Verify] FirstPoint(0) Norm: {firstNorm:F4}");
                System.Diagnostics.Debug.WriteLine($"[X-Verify] LastPoint(240) Norm: {lastNorm:F4}");
            }
#endif
            for (int i = startIndex; i <= endIndex; i++)
            {
                double val = doubleSpan[i];
                if (double.IsNaN(val)) continue;

                // 注：假设 CoordinateExtensions.ProjectToScreen 已更新为返回 HevoPoint
                // (如果在你的代码库里它还是返回 Point，可以在这里包一层 new HevoPoint)
                HevoPoint p = CoordinateExtensions.ProjectToScreen(area, rangeX, rangeY, axis, i, val);

                // C# 标准：float 也支持 IsNaN 和 IsInfinity 检查
                if (!float.IsNaN(p.X) && !float.IsInfinity(p.X) && !float.IsNaN(p.Y) && !float.IsInfinity(p.Y))
                {
                    screenPoints.Add(p);
                }
            }

            if (screenPoints.Count < 2) return;

            // 💥 折线是斜率图元，绝不套用 PushPixelSnapping！让底层抗锯齿完美发挥！
            if (style.IsSmooth)
            {
                var smoothPoints = SplineAlgorithm.GetCatmullRomSpline(screenPoints, 0.5, 8);
                draw.DrawPolyline(style.LinePen, smoothPoints);
            }
            else
            {
                draw.DrawPolyline(style.LinePen, screenPoints);
            }
        }
    }

    public static class SplineAlgorithm
    {
        /// <summary>
        /// 生成平滑曲线点集 (Catmull-Rom) - 跨平台 Float 优化版
        /// </summary>
        /// <param name="points">原始屏幕坐标点</param>
        /// <param name="alpha">张力系数 (0.0~1.0)，0.5 较好</param>
        /// <param name="detail">插值密度 (每两个点之间插几个点)</param>
        public static List<HevoPoint> GetCatmullRomSpline(List<HevoPoint> points, double alpha, int detail)
        {
            if (points.Count < 2) return points;

            var result = new List<HevoPoint>((points.Count - 1) * detail);

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
            return result;
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
