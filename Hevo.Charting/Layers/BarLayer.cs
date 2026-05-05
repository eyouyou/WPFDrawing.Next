using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;

namespace Hevo.Charting.Layers
{
    /// <summary>柱状图渲染数据包。</summary>
    /// <param name="Values">每根索引的高度值(允许 NaN 跳过)。</param>
    /// <param name="Brush">默认画刷;若同帧也下发了 <see cref="BarPaletteTrait"/> 则被其覆盖。</param>
    /// <param name="WidthRatio">柱宽相对单位距离的比例(0~1),默认 60%。</param>
    public record BarDataTrait(ReadOnlyMemory<double> Values, IHevoBrush Brush, double WidthRatio = 0.6) : IVisualTrait;

    /// <summary>柱状图动态调色板。Resolver != null 时按值/索引动态算色,适用红涨绿跌、阈值高亮等场景。</summary>
    public record BarPaletteTrait(IBrushResolver<double>? Resolver) : IVisualTrait;

    public partial class BarLayer : ChartLayer
    {
        // 💥 更换为 HevoRect
        private readonly Dictionary<IHevoBrush, List<HevoRect>> _batchedRects = new();

        public BarLayer()
        {
            Name = "BarLayer";
            // [全 WPF 实验] 同 CandleLayer 注释,Skia 全屏特定尺寸 ±1 列错位临时全切 WPF。
            Mode = RenderMode.Software;
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

            // 视口可见 domain 区间:用 Denormalize 拿到 plotArea 真实覆盖的边界(兼容 CategoryScale 的 ±Offset 与反向 Scale)。
            // 采样口径采用 permissive 策略——只要柱子的任何部分(中心 ± halfRatio)与可见区间相交,就纳入渲染,
            // 由 72-73 行的像素 clip 负责切掉溢出部分。是否出现"半根"是 Scale.SnapEdges + Interaction 决定的涌现行为,
            // 不在 Layer 这一层做硬编码取舍。
            double halfRatio = bData.WidthRatio * 0.5;
            double leftDomain = scale.DomainScale.Denormalize(0.0, xAxis.Viewport);
            double rightDomain = scale.DomainScale.Denormalize(1.0, xAxis.Viewport);
            double visibleMin = Math.Min(leftDomain, rightDomain);
            double visibleMax = Math.Max(leftDomain, rightDomain);
            int startIndex = Math.Clamp((int)Math.Ceiling(visibleMin - halfRatio), 0, count - 1);
            int endIndex = Math.Clamp((int)Math.Floor(visibleMax + halfRatio), 0, count - 1);
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

                // 💥 边缘柱子的半宽度可能溢出 plotArea，直接在数学坐标层面收回来，
                //    保证像素严格落在 plotArea 内部 —— 不依赖 Skia 剪裁。
                double pxLeft = Math.Max(pxCenter - pxHalfWidth, area.Left);
                double pxRight = Math.Min(pxCenter + pxHalfWidth, area.Right);
                if (pxRight <= pxLeft) continue;

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
            // 像素对齐由 renderer 在 DrawRectangles 分发时统一完成（fill 走原 rect、无需 snap）。
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
