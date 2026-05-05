using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;

namespace Hevo.Charting.Layers
{
    /// <summary>
    /// 主绘图区装饰 trait:传递底色与边框样式给 <see cref="PlotAreaDecorLayer"/>。
    /// 由 <see cref="Features.PlotAreaDecorFeature"/> 在 OnProject 阶段下发,两者任一非 null 即触发绘制。
    /// </summary>
    /// <param name="BackgroundBrush">底色画刷;null = 透明。</param>
    /// <param name="BorderStyle">边框样式;null = 不画边框。</param>
    public record PlotAreaDecorTrait(IHevoBrush? BackgroundBrush, LineStyle? BorderStyle) : IVisualTrait;
    public partial class PlotAreaDecorLayer : ChartLayer
    {
        public PlotAreaDecorLayer()
        {
            Name = "PlotAreaDecorLayer";
            // [全 WPF 实验] 同 CandleLayer 注释,Skia 全屏特定尺寸 ±1 列错位临时全切 WPF。
            Mode = RenderMode.Software;
            Level = ChartLayerType.Background; // 💥 必须在最底层打底
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var decor = data.Get<PlotAreaDecorTrait>();
            var plotArea = data.Get<PlotAreaTrait>();

            if (decor == null || plotArea == null || plotArea.Area.IsEmpty) return;

            // 像素对齐协议：halfT 内缩已下沉到 renderer 的 PixelSnap.InsideStroke。
            // 这里只声明几何（plotArea.Area）+ pen，渲染器内部保证边框锐利、不被外围 Clip 切半。
            if (decor.BorderStyle != null)
            {
                draw.DrawRectangle(decor.BackgroundBrush, decor.BorderStyle.LinePen, plotArea.Area);
            }
            else if (decor.BackgroundBrush != null)
            {
                draw.DrawRectangle(decor.BackgroundBrush, null, plotArea.Area);
            }
        }
    }
}
