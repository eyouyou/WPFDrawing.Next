using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using System.Windows;
using System.Windows.Media;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// 💥 终极 0-GC 悬浮信息窗特征 (数据与坐标彻底解耦，拥抱 FieldMeta 与 HevoPoint)
    /// </summary>
    public class TooltipWidgetFeature<TX> : ChartFeature
    {
        public override FeaturePhase Phase => FeaturePhase.Interaction;

        public DataPort<PointerHitState?> HitStatePort { get; init; } = null!;
        public DataPort<ReadOnlyMemory<TX>> XAxisDataPort { get; init; } = null!;

        public TooltipPositionMode PositionMode { get; init; } = TooltipPositionMode.Auto;

        // 💥 换装轻量级的纯 float 偏移量
        public HevoPoint Offset { get; init; } = new HevoPoint(12f, 12f);

        // 💥 完美大一统：用 FieldMeta 替代之前所有零散的 Name, Format, Brush 属性！
        public FieldMeta? XMeta { get; init; }

        private readonly TooltipWidgetLayer _layer = new();
        private readonly IHevoBrush _defaultBg = new HevoSolidBrush(Color.FromArgb(230, 30, 30, 30));
        private static readonly TooltipRow[] _emptyRows = Array.Empty<TooltipRow>();
        private readonly TooltipRow[] _rowBuffer = new TooltipRow[64];

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            AttachLayer(_layer);
        }

        protected override void OnProject(FeatureContext ctx)
        {
            // 铁律：无条件解包
            var (hitState, _) = ctx.UsePort(HitStatePort);
            var (xData, _) = ctx.UsePort(XAxisDataPort);

            // 越界隐藏
            if (hitState == null || hitState.Value.IsOutOfBounds)
            {
                PublishEmpty(ctx);
                return;
            }

            var state = hitState.Value;
            int rowCount = 0;

            // ==========================================
            // 💥 智能联动：通过 XMeta 提取大一统配置，带完美兜底
            // ==========================================
            if (state.LocalIndex >= 0 && state.LocalIndex < xData.Length)
            {
                TX val = xData.Span[state.LocalIndex];

                // 从 Meta 中拆解所需部件，如果没有 Meta，给予安全兜底
                IHevoString xName = XMeta?.Name ?? new HevoLiteralString("Time");
                string xFormat = XMeta?.Format ?? "G";
                IHevoFormatter? xProvider = XMeta?.Provider;
                IHevoBrush xBrush = XMeta?.GetConstantBrush() ?? new HevoSolidBrush(Colors.White);

                // 完美格式化
                string xStr = val is IFormattable f ? f.FormatValue(xFormat, xProvider) : val?.ToString() ?? "";

                _rowBuffer[rowCount++] = new TooltipRow(xName, xStr, xBrush);
            }

            // 捞取其它指标数据
            foreach (var layer in Chart.ActiveLayers)
            {
                var proxy = ctx.For(layer);
                var sMeta = proxy.Read<MetaTrait>();
                if (sMeta == null) continue;

                var doubleData = proxy.Read<DoubleSeriesDataTrait>();
                var indexResolver = proxy.Read<IndexBrushResolverTrait>();

                for (int i = 0; i < sMeta.Fields.Length; i++)
                {
                    if (rowCount >= _rowBuffer.Length) break;
                    var fieldMeta = sMeta.Fields[i];

                    bool hasValue = false;
                    string formattedString = string.Empty; // 💥 直接暂存格式化后的字符串，而不是暂存数字

                    if (doubleData != null && i < doubleData.FieldValues.Length &&
                             state.LocalIndex >= 0 && state.LocalIndex < doubleData.FieldValues[i].Length)
                    {
                        double dVal = doubleData.FieldValues[i].Span[state.LocalIndex];
                        formattedString = dVal.FormatValue(fieldMeta.Format, fieldMeta.Provider);
                        hasValue = true;
                    }

                    if (hasValue)
                    {
                        IHevoBrush finalColor = indexResolver != null
                                ? indexResolver.Resolver.ResolveByIndex(i, state.LocalIndex)
                                : fieldMeta.GetConstantBrush();

                        // 💥 直接塞入已经格式化好的字符串！
                        _rowBuffer[rowCount++] = new TooltipRow(fieldMeta.Name, formattedString, finalColor);
                    }
                }
            }

            // 发布图层数据
            if (rowCount > 0)
            {
                HevoRect safeArea = ctx.PlotArea; // 💥 直接使用你的纯净版 HevoRect！

                ctx.For(_layer).PublishData(new TooltipWidgetTrait(
                    AnchorPos: new HevoPoint((float)state.CenterX, state.MousePos.Y), // 安全降维
                    Rows: _rowBuffer.AsMemory(0, rowCount),
                    Background: _defaultBg,
                    CornerRadius: 6.0,
                    IsVisible: true,
                    PositionMode: PositionMode,
                    Offset: Offset,
                    PlotArea: safeArea
                ));
            }
            else
            {
                PublishEmpty(ctx);
            }
        }

        private void PublishEmpty(FeatureContext ctx) =>
            ctx.For(_layer).PublishData(new TooltipWidgetTrait(default, _emptyRows.AsMemory(), _defaultBg, 6.0, false, PositionMode, Offset, default));
    }

    // ==========================================
    // 💥 悬浮窗方位配置
    // ==========================================
    public enum TooltipPositionMode
    {
        Auto,           // 智能避让 (默认右下，碰壁自动翻转)
        TopLeft,        // 左上
        TopRight,       // 右上
        BottomLeft,     // 左下
        BottomRight     // 右下
    }

    public static class TooltipPositionCalculator
    {
        /// <summary>
        /// 💥 智能避让算法：计算 Tooltip 的最终左上角坐标
        /// </summary>
        /// <param name="anchor">锚点 (通常是十字光标交叉点)</param>
        /// <param name="size">Tooltip 的真实物理尺寸 (由渲染器测量得出)</param>
        /// <param name="plotArea">图表的安全绘图区域</param>
        /// <param name="mode">用户配置的停靠模式</param>
        /// <param name="offset">与光标的间距偏移量</param>
        public static Point Calc(Point anchor, Size size, Rect plotArea, TooltipPositionMode mode, Point offset)
        {
            double x = anchor.X;
            double y = anchor.Y;

            // 智能模式：动态计算剩余空间，决定最终模式
            if (mode == TooltipPositionMode.Auto)
            {
                bool fitRight = (x + offset.X + size.Width) <= plotArea.Right;
                bool fitBottom = (y + offset.Y + size.Height) <= plotArea.Bottom;

                // 优先停靠右下角，右边放不下就放左边，下面放不下就放上面
                mode = fitRight ?
                       (fitBottom ? TooltipPositionMode.BottomRight : TooltipPositionMode.TopRight) :
                       (fitBottom ? TooltipPositionMode.BottomLeft : TooltipPositionMode.TopLeft);
            }

            // 根据最终模式，计算出左上角绘制起点
            return mode switch
            {
                TooltipPositionMode.TopLeft => new Point(x - size.Width - offset.X, y - size.Height - offset.Y),
                TooltipPositionMode.TopRight => new Point(x + offset.X, y - size.Height - offset.Y),
                TooltipPositionMode.BottomLeft => new Point(x - size.Width - offset.X, y + offset.Y),
                TooltipPositionMode.BottomRight => new Point(x + offset.X, y + offset.Y),
                _ => new Point(x + offset.X, y + offset.Y)
            };
        }
    }
}
