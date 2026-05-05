using Hevo.Charting.Abstractions;
using Hevo.Charting.Renderers;
using System.Windows.Media;

namespace Hevo.Charting.Core
{
    /// <summary>线段样式 trait。</summary>
    /// <param name="LinePen">线条画笔(画刷 + 粗细 + 虚线模式)。</param>
    /// <param name="UseSpline">true = 走 Catmull-Rom 样条平滑;false = 直线段。仅控制几何插值,与像素对齐 / AA 无关。</param>
    /// <param name="SplineDetail">UseSpline=true 时每两个原始点间的细分段数,默认 8。
    /// 调小渲染更省(肉眼差异不大),调大更圆滑但渲染开销线性增长;UseSpline=false 时此参数无效。</param>
    public record LineStyle(HevoPen LinePen, bool UseSpline = false, int SplineDetail = 8) : IVisualTrait
    {
        // 💥 1. 核心工厂方法：接收最高抽象的 IHevoBrush
        // UseSpline 仅控制 LineLayer 的几何插值（Catmull-Rom 样条 vs 直线段），与像素对齐 / AA 都无关。
        // 像素对齐由渲染器内部 PixelSnap + fill rect 路径自动处理（§12 / §13）。
        public static LineStyle Create(IHevoBrush brush, double thickness = 1.0, bool useSpline = false)
        {
            return new LineStyle(new HevoPen(brush, thickness), useSpline);
        }

        // 语法糖：向下兼容 Color 调用
        public static LineStyle Create(Color color, double thickness = 1.0, bool useSpline = false)
        {
            return new LineStyle(new HevoPen(new HevoSolidBrush(color), thickness), useSpline);
        }

        // 语法糖：直接从资源键生成
        public static LineStyle FromResource(string resourceKey, double thickness = 1.0, bool useSpline = false)
        {
            return new LineStyle(new HevoPen(new HevoResourceBrush(resourceKey), thickness), useSpline);
        }

        public static readonly LineStyle Default = Create(Colors.Blue, 1.0, false);
    }

    public static class LineLayerExtensions
    {
        // 💥 2. 核心扩展方法：参数升级为 IHevoBrush
        public static VisualProxy<T> WithLine<T>(this VisualProxy<T> proxy, IHevoBrush? brush = null, double? thickness = null, bool? useSpline = null)
            where T : IChartLayer, IConsumes<LineStyle>
        {
            return proxy.UpdateData<LineStyle>(old =>
            {
                var baseStyle = old ?? LineStyle.Default;

                HevoPen newPen = baseStyle.LinePen;
                bool finalUseSpline = useSpline ?? baseStyle.UseSpline;

                // 仅 brush / thickness 变更才需要重建 pen（UseSpline 是 LineStyle 自身字段，与 pen 无关）。
                if (brush != null || thickness.HasValue)
                {
                    // 💥 终极避坑：安全的多态获取！绝不用强转去拿 Color！
                    // 旧画刷可能是 Solid 也可能是 Resource，不管它是什么，直接复用它的 IHevoBrush 接口！
                    var b = brush ?? baseStyle.LinePen.Brush;
                    var t = thickness ?? baseStyle.LinePen.Thickness;
                    newPen = baseStyle.LinePen with { Brush = b, Thickness = t };
                }

                return baseStyle with
                {
                    LinePen = newPen,
                    UseSpline = finalUseSpline
                };
            });
        }

        // 💥 3. 语法糖扩展方法：保护外部业务层之前写死的 Color 代码不用大改
        public static VisualProxy<T> WithLine<T>(this VisualProxy<T> proxy, Color color, double? thickness = null, bool? useSpline = null)
            where T : IChartLayer, IConsumes<LineStyle>
        {
            return proxy.WithLine(new HevoSolidBrush(color), thickness, useSpline);
        }
    }

    public partial class LineLayer : ChartLayer
    {
        // 跨帧复用的工作缓冲(WPF/Skia 渲染均在同一 UI tick 内同步消费 front buffer,
        // 下一帧 OnUpdate 回到本方法时,上一帧引用已被 ChartCell.PostRender + back/front swap 后的 Clear 释放。)
        // Capacity 是经验水位线:2048 ≈ 全屏可见 ~1500 根 K 线 + 头尾 trim 点的常态;
        // 8192 = spline 默认 detail=8 时 _trimmedPoints * 4~8 倍展开,留出余量避免运行时扩容拷贝。
        // 不暴露成配置:数值是"避免首次扩容"的优化下限,业务调小反而加重 GC、调大也不省内存。
        private readonly List<HevoPoint> _screenPoints = new(2048);
        private readonly List<HevoPoint> _trimmedPoints = new(2048);
        private readonly List<HevoPoint> _smoothPoints = new(8192);

        public LineLayer()
        {
            Name = "LineSeries";
            // [全 WPF 实验] 同 CandleLayer 注释,Skia 全屏特定尺寸 ±1 列错位临时全切 WPF。
            Mode = RenderMode.Software;
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

            int count = doubleSeries.FieldValues[0].Length;

            // 视口可见 domain 区间:用 Denormalize 拿到 plotArea 真实覆盖的边界(兼容 CategoryScale 的 ±Offset 与反向 Scale)。
            // permissive 采样:Floor/Ceiling 各往外扩一根,确保跨过 plotArea 边界的 chord 也能被 TrimPolylineToPlotArea 精确裁出。
            // 是否出现"边缘半段"是 Scale.SnapEdges + Interaction 决定的涌现行为,不在 Layer 这一层做硬编码取舍。
            double leftDomain = axis.DomainScale.Denormalize(0.0, rangeX);
            double rightDomain = axis.DomainScale.Denormalize(1.0, rangeX);
            double visibleMin = Math.Min(leftDomain, rightDomain);
            double visibleMax = Math.Max(leftDomain, rightDomain);
            int startIndex = Math.Clamp((int)Math.Floor(visibleMin), 0, count - 1);
            int endIndex = Math.Clamp((int)Math.Ceiling(visibleMax), 0, count - 1);

            if (startIndex > endIndex) return;

            _screenPoints.Clear();
            _trimmedPoints.Clear();

            ReadOnlySpan<double> doubleSpan = doubleSeries.FieldValues[0].Span;

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

            if (style.UseSpline)
            {
                _smoothPoints.Clear();
                SplineAlgorithm.GetCatmullRomSpline(_trimmedPoints, style.SplineDetail, _smoothPoints);
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
        /// 0-GC 版 Catmull-Rom 样条:每两个原始点之间细分 <paramref name="detail"/> 段,结果写入调用方提供的 <paramref name="result"/> 缓冲。
        /// 注:历史上有 alpha 参数(centripetal 形式),实现里从未生效已删除;若未来要做 centripetal 再单开一个重载。
        /// </summary>
        public static void GetCatmullRomSpline(List<HevoPoint> points, int detail, List<HevoPoint> result)
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
