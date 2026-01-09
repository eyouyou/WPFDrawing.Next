using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;

namespace Hevo.Charting.Layers
{
    public record BarDataTrait(ReadOnlyMemory<double> Values, IHevoBrush Brush, double WidthRatio = 0.6) : IVisualTrait;
    public record BarPaletteTrait(IBrushResolver<double>? Resolver) : IVisualTrait;

    public partial class BarLayer : ChartLayer
    {
        // 💥 更换为 HevoRect
        private readonly Dictionary<IHevoBrush, List<HevoRect>> _batchedRects = new();

        public BarLayer()
        {
            Name = "BarLayer";
            Mode = RenderMode.Hardware;
            Level = ChartLayerType.Main;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var bData = data.Get<BarDataTrait>();
            var plotArea = data.Get<PlotAreaTrait>();
            var xAxis = data.Get<XAxisTrait>();
            var yAxis = data.Get<YAxisTrait>();
            var scale = data.Get<ScaleStrategyTrait>();

            // 尝试获取画刷解析器 (如果没有配置动态调色盘，则退化为普通画刷)
            var resolver = data.Get<BarPaletteTrait>()?.Resolver;

            if (bData == null || bData.Values.IsEmpty || plotArea == null || xAxis == null || yAxis == null || scale == null) return;
            if (plotArea.Area.IsEmpty || xAxis.Viewport.Span <= 0 || yAxis.Viewport.Span <= 0) return;

            var span = bData.Values.Span;
            int count = span.Length;

            int startIndex = Math.Clamp((int)Math.Floor(xAxis.Viewport.Min), 0, count - 1);
            int endIndex = Math.Clamp((int)Math.Ceiling(xAxis.Viewport.Max), 0, count - 1);
            if (startIndex > endIndex) return;

            HevoRect area = plotArea.Area;

            // 清空批处理缓冲，但不释放内存
            foreach (var list in _batchedRects.Values) list.Clear();

            double zeroNorm = scale.ValueScale.Normalize(0, yAxis.Viewport);
            double zeroPy = area.Bottom - (area.Height * zeroNorm);
            double clampedZeroPy = Math.Clamp(zeroPy, area.Top, area.Bottom);

            double unitNormDelta = scale.DomainScale.Normalize(1, xAxis.Viewport) - scale.DomainScale.Normalize(0, xAxis.Viewport);
            double unitPxWidth = area.Width * Math.Abs(unitNormDelta);
            double pxHalfWidth = unitPxWidth * bData.WidthRatio * 0.5;

            for (int i = startIndex; i <= endIndex; i++)
            {
                double val = span[i];
                if (double.IsNaN(val)) continue;

                double xNorm = scale.DomainScale.Normalize(i, xAxis.Viewport);
                double pxCenter = area.Left + (area.Width * xNorm);

                double valNorm = scale.ValueScale.Normalize(val, yAxis.Viewport);
                double valPy = area.Bottom - (area.Height * valNorm);
                double clampedValPy = Math.Clamp(valPy, area.Top, area.Bottom);

                if (clampedZeroPy == clampedValPy) continue;

                double pxLeft = pxCenter - pxHalfWidth;
                double pxRight = pxCenter + pxHalfWidth;

                double top = Math.Min(clampedZeroPy, clampedValPy);
                double bottom = Math.Max(clampedZeroPy, clampedValPy);

                if (bottom - top < 1.0) top = bottom - 1.0;

                // 💥 核心逻辑：获取当前柱子的真实画刷
                IHevoBrush actualBrush = resolver != null ? resolver.Resolve(in val) : bData.Brush;

                // 按画刷将 Rect 分发到对应的批次中
                if (!_batchedRects.TryGetValue(actualBrush, out var rectList))
                {
                    rectList = new List<HevoRect>();
                    _batchedRects[actualBrush] = rectList;
                }
                // 💥 降维：在最后一环安全转为 float，录入 0-GC 的缓冲队列中！
                rectList.Add(new HevoRect((float)pxLeft, (float)top, (float)(pxRight - pxLeft), (float)(bottom - top)));
            }

            // 💥 批量提交给渲染器 (相同颜色的柱子一次性 Draw 完，性能无敌)
            using (draw.PushPixelSnapping(0.0f))
            {
                foreach (var kvp in _batchedRects)
                {
                    if (kvp.Value.Count > 0)
                    {
                        draw.DrawRectangles(kvp.Key, null, kvp.Value);
                    }
                }
            }
        }
    }
}
