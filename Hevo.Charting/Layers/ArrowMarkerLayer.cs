using System.Windows.Media;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;

namespace Hevo.Charting.Layers
{
    /// <summary>
    /// 箭头 marker 图层 —— 把 <see cref="ArrowSpecTrait"/> 里的 <see cref="ArrowMarker"/> 列表按主图 axes
    /// 投影,在每个 LogicalX/LogicalY 处画三角箭头(复用 BuyMarkerLayer 同款 stroke 三角)。
    /// 命中索引在屏幕侧画 hover_text tooltip。
    ///
    /// <para>
    /// <b>DomainMode</b>:本 layer 走 <c>"inherit"</c> 模式 —— 跟主图共用 XAxisTrait / YAxisTrait /
    /// ScaleStrategyTrait。K 线买点 / 卖点等场景天然契合。
    /// </para>
    /// </summary>
    public sealed class ArrowMarkerLayer : ChartLayer,
        IConsumes<PlotAreaTrait>,
        IConsumes<XAxisTrait>,
        IConsumes<YAxisTrait>,
        IConsumes<ScaleStrategyTrait>,
        IConsumes<ArrowSpecTrait>
    {
        public string LayerName
        {
            get => Name;
            set => Name = value;
        }

        public ArrowMarkerLayer()
        {
            Name = "ArrowMarker";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Main;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var plot = data.Get<PlotAreaTrait>();
            var xAxis = data.Get<XAxisTrait>();
            var yAxis = data.Get<YAxisTrait>();
            var scale = data.Get<ScaleStrategyTrait>();
            var spec = data.Get<ArrowSpecTrait>();
            if (plot == null || xAxis == null || yAxis == null || scale == null || spec == null) return;

            var area = plot.Area;
            if (area.Width <= 0 || area.Height <= 0) return;

            var span = spec.Markers.Span;
            for (int i = 0; i < span.Length; i++)
            {
                ref readonly var m = ref span[i];
                var p = CoordinateExtensions.ProjectToScreen(area, xAxis.Viewport, yAxis.Viewport, scale, m.LogicalX, m.LogicalY);
                if (float.IsNaN(p.X) || float.IsNaN(p.Y)) continue;
                if (p.X < area.Left || p.X > area.Right) continue;

                var brush = PlotBrushCache.Resolve(m.Color);
                DrawArrow(draw, p, m.Direction, m.Size, brush);
            }
        }

        // 三角箭头:跟 BuyMarkerLayer 同款 3 条线段闭合,stroke 厚度 ~2.5px。
        // direction 控制箭头朝向:down(尖朝下,顶点指向 logical 点上方) / up / left / right。
        private static void DrawArrow(IDrawingSink draw, HevoPoint anchor, string direction, float size, IHevoBrush brush)
        {
            float half = size;
            float offset = size * 1.5f;
            HevoPoint tip;
            HevoPoint baseLeft, baseRight;
            switch ((direction ?? "down").ToLowerInvariant())
            {
                case "up":
                    tip       = new HevoPoint(anchor.X, anchor.Y - offset);
                    baseLeft  = new HevoPoint(anchor.X - half, anchor.Y);
                    baseRight = new HevoPoint(anchor.X + half, anchor.Y);
                    break;
                case "left":
                    tip       = new HevoPoint(anchor.X - offset, anchor.Y);
                    baseLeft  = new HevoPoint(anchor.X, anchor.Y - half);
                    baseRight = new HevoPoint(anchor.X, anchor.Y + half);
                    break;
                case "right":
                    tip       = new HevoPoint(anchor.X + offset, anchor.Y);
                    baseLeft  = new HevoPoint(anchor.X, anchor.Y - half);
                    baseRight = new HevoPoint(anchor.X, anchor.Y + half);
                    break;
                case "down":
                default:
                    tip       = new HevoPoint(anchor.X, anchor.Y - offset * 0.5f);
                    baseLeft  = new HevoPoint(anchor.X - half, anchor.Y - offset - half);
                    baseRight = new HevoPoint(anchor.X + half, anchor.Y - offset - half);
                    break;
            }
            var pen = new HevoPen(brush, 2.5);
            draw.DrawLine(pen, baseLeft, baseRight);
            draw.DrawLine(pen, baseRight, tip);
            draw.DrawLine(pen, tip, baseLeft);
        }
    }
}
