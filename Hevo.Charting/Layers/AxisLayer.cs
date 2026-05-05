using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using System.Windows.Media;

namespace Hevo.Charting.Layers
{
    /// <summary>
    /// 刻度策略载体。record 自动 Equals,策略参数真变才触发 UseMemo 失效。
    /// 数据流:Schema 装配时构造 → AxisFeature 在 OnProject 通过 _tickProvider 取出 → 喂给 ITickStrategy.Calculate 算 tick。
    /// </summary>
    public record TickStrategyTrait(ITickStrategy Strategy) : IVisualTrait
    {
        public static TickStrategyTrait Create(ITickStrategy strategy) => new(strategy);
    }

    /// <summary>坐标轴量程 trait(纯数学边界,无物理坐标)。供下游 Tick 算法或自定义 Layer 直接读取。</summary>
    public record AxisRangeTrait(RealRange Range) : IVisualTrait
    {
        public static AxisRangeTrait Create(double min, double max) => new(new RealRange(min, max));
        public static readonly AxisRangeTrait Empty = new(RealRange.Empty);
    }

    /// <summary>
    /// 已算好的刻度切片(Tick 数组 + 有效个数)。
    /// 数据流:AxisFeature.OnProject 用 UseMemo 算出 → PublishData 给 AxisLayer / GridLineLayer。
    /// 数组允许复用以维持 0-GC,Count 才是真实长度。
    /// </summary>
    public record AxisTickDataTrait(TickModel[] Ticks, int Count) : IVisualTrait
    {
        public static AxisTickDataTrait Empty() => new(Array.Empty<TickModel>(), 0);
    }

    /// <summary>坐标轴方位枚举。Top/Bottom = 横轴;Left/Right = 纵轴。</summary>
    public enum AxisPlacement { Top, Bottom, Left, Right }

    /// <summary>
    /// 坐标轴视觉样式 trait(无 Handle / 无 Port,纯视图)。
    /// AxisFeature.OnProject 会按 plotArea 算出 AbsoluteAnchor 后 with 出新实例下发图层。
    /// </summary>
    /// <param name="Placement">物理方位,决定文本对齐与刻度线方向。</param>
    /// <param name="TextBrush">默认 tick 文字画刷;per-tick 的 OverrideTextBrush 会覆盖此值。</param>
    /// <param name="FontSize">字号(像素)。</param>
    /// <param name="Typeface">字体。</param>
    /// <param name="BaseLineStyle">轴基线画笔;null = 不画基线。</param>
    /// <param name="TickLabelOffset">刻度文本与基线的距离(像素)。</param>
    /// <param name="TickMarkLength">刻度短线的物理长度(像素)。</param>
    /// <param name="CustomPhysicalAnchor">自定义贴轴坐标(用于"图表中心悬浮轴")。null = 自动贴 plotArea 边缘。</param>
    /// <param name="AbsoluteAnchor">运行时填写的最终物理坐标(由 Feature with 出来)。Layer 直接吃这个画轴。</param>
    public record AxisStyleTrait(
            AxisPlacement Placement,
            IHevoBrush TextBrush,
            double FontSize,
            HevoTypeface Typeface,
            LineStyle? BaseLineStyle = null,
            double TickLabelOffset = 5.0,
            double TickMarkLength = 4.0,
            double? CustomPhysicalAnchor = null,
            double AbsoluteAnchor = 0.0
        ) : IVisualTrait
    {
        public static AxisStyleTrait Create(
            AxisPlacement placement,
            Color textColor,
            LineStyle? baseLineStyle = null,
            double fontSize = 11.0,
            double labelOffset = 5.0,
            string fontFamily = "Microsoft YaHei",
            double? customPhysicalAnchor = null)
        {
            return new AxisStyleTrait(placement, new HevoSolidBrush(textColor), fontSize, new HevoTypeface(fontFamily), baseLineStyle, labelOffset, 4.0, customPhysicalAnchor);
        }
    }

    /// <summary>轴布局位置 trait(目前主要给十字光标 / Tooltip 反查"轴贴在哪根线"用)。AbsoluteAnchor 与 AxisStyleTrait 同义。</summary>
    public record AxisLayoutTrait(double AbsoluteAnchor) : IVisualTrait;

    public partial class AxisLayer : ChartLayer
    {
        public AxisLayer(string name)
        {
            Name = name;
            Mode = RenderMode.Software;
            Level = ChartLayerType.Background;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var tickTrait = data.Get<AxisTickDataTrait>();
            var axisTrait = data.Get<AxisStyleTrait>();
            var plotArea = data.Get<PlotAreaTrait>();

            // 💥 取消接收 AxisScaleTrait，无需二次解包！
            if (tickTrait == null || axisTrait == null || plotArea == null || tickTrait.Count == 0) return;

            // 💥 更换为全新的 HevoRect
            HevoRect area = plotArea.Area;

            double anchorX = axisTrait.CustomPhysicalAnchor ?? (axisTrait.Placement == AxisPlacement.Right ? area.Right : area.Left);
            double anchorY = axisTrait.CustomPhysicalAnchor ?? (axisTrait.Placement == AxisPlacement.Top ? area.Top : area.Bottom);

            HevoPen? tickPen = axisTrait.BaseLineStyle?.LinePen;

            for (int i = 0; i < tickTrait.Count; i++)
            {
                ref var tick = ref tickTrait.Ticks[i];

                // 💥 直接拦截：判断 Feature 传过来的最终洗礼版 Ratio 是否越界
                if (tick.Ratio < 0 || tick.Ratio > 1) continue;

                // ==========================================
                // 💥 极致纯洁的视图渲染：0反推，0逻辑！
                // tick.Ratio 已经是绝对正确的物理屏幕百分比，直接乘！
                // ==========================================
                double px = area.Left + tick.Ratio * area.Width;
                double py = area.Bottom - tick.Ratio * area.Height;

                // 💥 baseline 横线由 GridLineLayer（硬件 Background）渲染，确保落在线序列下方。
                //    label 默认与普通 tick 同色（axisTrait.TextBrush）；要高亮 baseline 走 ITickStylePolicy 的 OverrideTextBrush。
                var textBrush = tick.OverrideTextBrush ?? axisTrait.TextBrush;
                double offset = axisTrait.TickLabelOffset;
                double tLen = axisTrait.TickMarkLength;

                // 💥 文本渲染，坐标和大小一律安全降维至 float
                switch (axisTrait.Placement)
                {
                    case AxisPlacement.Bottom:
                        if (tickPen != null) draw.DrawLine(tickPen, new HevoPoint((float)px, (float)anchorY), new HevoPoint((float)px, (float)(anchorY + tLen)));
                        draw.DrawText(tick.Label, axisTrait.Typeface, textBrush, (float)axisTrait.FontSize, new HevoPoint((float)px, (float)(anchorY + offset)), TextAlignX.Center, TextAlignY.Top);
                        break;
                    case AxisPlacement.Top:
                        if (tickPen != null) draw.DrawLine(tickPen, new HevoPoint((float)px, (float)anchorY), new HevoPoint((float)px, (float)(anchorY - tLen)));
                        draw.DrawText(tick.Label, axisTrait.Typeface, textBrush, (float)axisTrait.FontSize, new HevoPoint((float)px, (float)(anchorY - offset)), TextAlignX.Center, TextAlignY.Bottom);
                        break;
                    case AxisPlacement.Left:
                        if (tickPen != null) draw.DrawLine(tickPen, new HevoPoint((float)anchorX, (float)py), new HevoPoint((float)(anchorX - tLen), (float)py));
                        draw.DrawText(tick.Label, axisTrait.Typeface, textBrush, (float)axisTrait.FontSize, new HevoPoint((float)(anchorX - offset), (float)py), TextAlignX.Right, TextAlignY.Center);
                        break;
                    case AxisPlacement.Right:
                        if (tickPen != null) draw.DrawLine(tickPen, new HevoPoint((float)anchorX, (float)py), new HevoPoint((float)(anchorX + tLen), (float)py));
                        draw.DrawText(tick.Label, axisTrait.Typeface, textBrush, (float)axisTrait.FontSize, new HevoPoint((float)(anchorX + offset), (float)py), TextAlignX.Left, TextAlignY.Center);
                        break;
                }
            }
        }
    }
}
