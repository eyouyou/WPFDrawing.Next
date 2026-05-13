using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using Hevo.Charting.Renderers;
using System.Windows.Media;

namespace Hevo.Charting.Features
{
    /// <summary>label 锚点位置:线起点 / 中点 / 终点。</summary>
    public enum MathLineLabelPlacement { LineStart, LineMid, LineEnd }

    /// <summary>
    /// 数学方程线 feature —— 给定 (Slope, Intercept) 直线方程,按当前 X/Y 量程画 2 点直线,自带数学公式 label。
    /// <para>
    /// 跟 <see cref="LineSeriesFeature"/> 互补:LineSeriesFeature 按数组 index 投影 ROM&lt;double&gt;,适合时序;
    /// MathLineFeature 按 (x, y) 真实坐标系画任意直线,适合 scatter 散点云的"阈值参考线 / 基准线"场景。
    /// </para>
    /// <para>
    /// 端口:<see cref="XRangePort"/> / <see cref="YRangePort"/> 通常接 ConstantRangeFeature 同源端口,
    /// 跟 AxisFeature.RangePort 共用 globalId 保证坐标系对齐。
    /// </para>
    /// </summary>
    public sealed class MathLineFeature : ChartFeature
    {
        public override FeaturePhase Phase => FeaturePhase.Series;

        /// <summary>斜率。水平线 = 0,45° 对角 = 1。</summary>
        public double Slope { get; init; }

        /// <summary>Y 截距(x=0 时的 y 值)。水平线 y=8.70:Slope=0, Intercept=8.70。</summary>
        public double Intercept { get; init; }

        /// <summary>线样式(颜色 / 粗细 / 虚线)。默认 1px 白色实线。</summary>
        public LineStyle Style { get; init; } = LineStyle.Create(Colors.White, thickness: 1.0);

        /// <summary>label 文本,null/"" 不画 label。典型:"y = log₁₀(5e8) ≈ 8.70"。</summary>
        public string? LabelText { get; init; }

        /// <summary>label 锚点:LineEnd(右端,默认)/ LineMid / LineStart。</summary>
        public MathLineLabelPlacement LabelPlacement { get; init; } = MathLineLabelPlacement.LineEnd;

        /// <summary>label 字号(像素)。默认 11。</summary>
        public double LabelFontSize { get; init; } = 11.0;

        /// <summary>label 字体。默认微软雅黑。</summary>
        public string LabelFontFamily { get; init; } = "Microsoft YaHei";

        /// <summary>X 量程端口 —— 跟 AxisFeature.RangePort 共用 globalId。</summary>
        public DataPort<RealRange> XRangePort { get; init; } = null!;

        /// <summary>Y 量程端口 —— 同上。</summary>
        public DataPort<RealRange> YRangePort { get; init; } = null!;

        /// <summary>底层图层名,仅用于调试 / 多实例区分。</summary>
        public string LayerName { get; init; } = "MathLine";

        private readonly MathLineLayer _layer = new();

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            _layer.Name = LayerName;
            AttachLayer(_layer);
        }

        protected override void OnProject(FeatureContext ctx)
        {
            var (xRange, _) = ctx.UsePort(XRangePort);
            var (yRange, _) = ctx.UsePort(YRangePort);
            if (!xRange.IsValid || !yRange.IsValid) return;

            // 直线两端点 = (xMin, slope*xMin + intercept) 和 (xMax, slope*xMax + intercept)
            var p1 = new MathLinePoint(xRange.Min, Slope * xRange.Min + Intercept);
            var p2 = new MathLinePoint(xRange.Max, Slope * xRange.Max + Intercept);

            ctx.For(_layer).PublishData(new MathLineSpecTrait(
                p1, p2, Style, LabelText, LabelPlacement,
                LabelFontSize, LabelFontFamily, xRange, yRange));
        }
    }

    /// <summary>(x, y) 数学坐标点 —— MathLine 端点。</summary>
    public readonly record struct MathLinePoint(double X, double Y);

    /// <summary>
    /// MathLineLayer 消费的 trait —— 包含两端点 + 样式 + label 配置 + 当前 X/Y 量程(layer 用于像素投影)。
    /// </summary>
    public sealed record MathLineSpecTrait(
        MathLinePoint P1,
        MathLinePoint P2,
        LineStyle Style,
        string? LabelText,
        MathLineLabelPlacement Placement,
        double LabelFontSize,
        string LabelFontFamily,
        RealRange XRange,
        RealRange YRange) : IVisualTrait;
}
