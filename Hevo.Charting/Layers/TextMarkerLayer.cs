using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;

namespace Hevo.Charting.Layers
{
    /// <summary>
    /// 文字 marker 图层 —— 把 <see cref="TextSpecTrait"/> 里的 <see cref="TextMarker"/> 列表按主图 axes
    /// 投影,在每个 LogicalX/LogicalY 处画文字标签(典型 K 线买卖点边上的 "BUY" / "SELL" / 价格批注)。
    ///
    /// <para>
    /// <b>DomainMode</b>:本 layer 走 <c>"inherit"</c> 模式 —— 跟主图共用 XAxisTrait / YAxisTrait /
    /// ScaleStrategyTrait,跟 <see cref="ArrowMarkerLayer"/> / <see cref="ScatterPlotLayer"/> 同款投影路径。
    /// </para>
    ///
    /// <para>
    /// <b>Anchor 语义</b>:相对逻辑点位置,5 个值
    /// <c>"above"</c>(默认,文本底中对齐逻辑点上方,留 6px 间隔避免压住 K 线)/
    /// <c>"below"</c>(顶中对齐下方,留 6px)/
    /// <c>"center"</c>(中心对齐逻辑点)/
    /// <c>"left"</c>(右中对齐逻辑点左侧)/
    /// <c>"right"</c>(左中对齐逻辑点右侧)。
    /// </para>
    /// </summary>
    public sealed class TextMarkerLayer : ChartLayer,
        IConsumes<PlotAreaTrait>,
        IConsumes<XAxisTrait>,
        IConsumes<YAxisTrait>,
        IConsumes<ScaleStrategyTrait>,
        IConsumes<TextSpecTrait>
    {
        // 默认字体 —— 跟 AxisFeature 的默认 typeface 对齐,文本风格统一。
        private static readonly HevoTypeface DefaultTypeface = new("Microsoft YaHei");

        // 逻辑点到文本锚点的像素 offset。above/below 留 6px 让文字不贴 K 线 wick。
        private const float AnchorOffset = 6f;

        public string LayerName
        {
            get => Name;
            set => Name = value;
        }

        public TextMarkerLayer()
        {
            Name = "TextMarker";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Main;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var plot  = data.Get<PlotAreaTrait>();
            var xAxis = data.Get<XAxisTrait>();
            var yAxis = data.Get<YAxisTrait>();
            var scale = data.Get<ScaleStrategyTrait>();
            var spec  = data.Get<TextSpecTrait>();
            if (plot == null || xAxis == null || yAxis == null || scale == null || spec == null) return;

            var area = plot.Area;
            if (area.Width <= 0 || area.Height <= 0) return;

            var span = spec.Markers.Span;
            for (int i = 0; i < span.Length; i++)
            {
                ref readonly var m = ref span[i];
                if (string.IsNullOrEmpty(m.Text)) continue;

                var p = CoordinateExtensions.ProjectToScreen(area, xAxis.Viewport, yAxis.Viewport, scale, m.LogicalX, m.LogicalY);
                if (float.IsNaN(p.X) || float.IsNaN(p.Y)) continue;
                if (p.X < area.Left || p.X > area.Right) continue;

                var brush = PlotBrushCache.Resolve(m.Color);
                var (anchorPt, alignX, alignY) = ResolveAnchor(p, m.Anchor);

                draw.DrawText(
                    new HevoLiteralString(m.Text),
                    DefaultTypeface,
                    brush,
                    m.FontSize > 0 ? m.FontSize : 11f,
                    anchorPt,
                    alignX,
                    alignY);
            }
        }

        private static (HevoPoint Pos, TextAlignX X, TextAlignY Y) ResolveAnchor(HevoPoint logical, string anchor)
        {
            switch ((anchor ?? "above").ToLowerInvariant())
            {
                case "below":
                    return (new HevoPoint(logical.X, logical.Y + AnchorOffset), TextAlignX.Center, TextAlignY.Top);
                case "center":
                    return (logical, TextAlignX.Center, TextAlignY.Center);
                case "left":
                    return (new HevoPoint(logical.X - AnchorOffset, logical.Y), TextAlignX.Right, TextAlignY.Center);
                case "right":
                    return (new HevoPoint(logical.X + AnchorOffset, logical.Y), TextAlignX.Left, TextAlignY.Center);
                case "above":
                default:
                    return (new HevoPoint(logical.X, logical.Y - AnchorOffset), TextAlignX.Center, TextAlignY.Bottom);
            }
        }
    }
}
