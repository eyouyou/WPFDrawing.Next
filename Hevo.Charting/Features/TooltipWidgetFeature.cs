using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using System.Windows;
using System.Windows.Media;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// 非泛型基类——仅用作类型识别(典型:<see cref="IFeatureContext.Remove{TFeature}"/>
    /// 不接受泛型抽象,所以装饰器要批量摘 tooltip 必须有这个基类作为 anchor)。
    /// </summary>
    public abstract class TooltipWidgetFeature : ChartFeature { }

    /// <summary>
    /// 💥 终极 0-GC 悬浮信息窗特征 (数据与坐标彻底解耦，拥抱 FieldMeta 与 HevoPoint)。
    /// <para>
    /// <b>典型用法</b>:跟 Crosshair 共享同一个 hitPort 即可联动:
    /// <code>
    /// interactions.Add(new TooltipWidgetFeature&lt;DateTime&gt;
    /// {
    ///     HitStatePort   = hitPort,
    ///     XAxisDataPort  = ports.Time,
    ///     XMeta          = FieldMeta.Literal("时间", brush, "yyyy-MM-dd HH:mm"),
    ///     PositionMode   = TooltipPositionMode.Auto,
    /// });
    /// </code>
    /// </para>
    /// <para>
    /// <b>数据流</b>:HitStatePort + XAxisDataPort → 拼第一行;遍历所有 Layer 的 MetaTrait + DoubleSeriesDataTrait → 拼后续行 → 下发 TooltipWidgetTrait → TooltipWidgetLayer 排版渲染。
    /// </para>
    /// </summary>
    public class TooltipWidgetFeature<TX> : TooltipWidgetFeature
    {
        public override FeaturePhase Phase => FeaturePhase.Interaction;

        /// <summary>命中状态端口:由 <see cref="ChartInteractionFeature"/> 写入,Tooltip 据此决定显隐与定位。</summary>
        public DataPort<PointerHitState?> HitStatePort { get; init; } = null!;

        /// <summary>X 轴原始数据端口(时间/索引值),用于 Tooltip 第一行的 X 字段显示。</summary>
        public DataPort<ReadOnlyMemory<TX>> XAxisDataPort { get; init; } = null!;

        /// <summary>悬浮窗停靠模式。Auto = 智能避让(默认右下,撞边自动翻转);其他值锁死方向。</summary>
        public TooltipPositionMode PositionMode { get; init; } = TooltipPositionMode.Auto;

        /// <summary>悬浮窗与十字光标交点的像素偏移,默认右下 12px。</summary>
        public HevoPoint Offset { get; init; } = new HevoPoint(12f, 12f);

        /// <summary>X 字段元数据(名/格式/画刷)。null 时退化到"Time" + ToString + 白色。</summary>
        public FieldMeta? XMeta { get; init; }

        // 注:之前的 NamesDataPort + _effectiveNamesPort 哑端口 sentinel 已下架(2026-05)——
        // 通用 Tooltip 不应该硬编码"证券名称"业务字段后门。现在"名称"跟其他指标一样走通用
        // MetaTrait + StringSeriesDataTrait 协议,业务侧用 NameSeriesFeature(挂 MetadataLayer)
        // 发布字符串列,Tooltip 在 ActiveLayers 循环里自动拼行。

        // Tooltip 渲染层(Interaction 级)。
        private readonly TooltipWidgetLayer _layer = new();
        // 默认半透明深色底,业务可在 OnCompose 替换。
        private readonly IHevoBrush _defaultBg = new HevoSolidBrush(Color.FromArgb(230, 30, 30, 30));
        // 空行 trait 的单例,隐藏 Tooltip 时复用以避免每帧分配。
        private static readonly TooltipRow[] _emptyRows = Array.Empty<TooltipRow>();
        // 行缓冲(64 行硬上限)。每帧从下标 0 累积,最后用 AsMemory(0, count) 切出活跃区。
        private readonly TooltipRow[] _rowBuffer = new TooltipRow[64];

        // 注:之前为防"联动 dashboard 下 SharedHit 镜像导致副图 tooltip 跟着弹"加过
        // _isMouseOverChart + MouseEnter/Leave 订阅 hack(2026-05 下架)。该场景在 framework
        // 现行规约下已不存在——LinkedPaneSchemaContext.Decorate 会 Remove<TooltipWidgetFeature>
        // 强制副图无 tooltip(见 SchemaContext.cs:139)。若未来业务侧手搓多 cell 装配绕开此规约
        // 且都挂 tooltip,需补 PointerHitState.SourceCellId 协议(改 ChartCell / DashboardLauncher
        // 装配链),而非在 Feature 端绕回 mouse event 侧信道。

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            AttachLayer(_layer);
        }

        protected override void OnProject(FeatureContext ctx)
        {
            // 铁律：无条件解包(HEVO003:UsePort 顺序/次数必须跨帧一致,不能在 if 里调)
            var (hitState, _) = ctx.UsePort(HitStatePort);
            var (xData, _) = ctx.UsePort(XAxisDataPort);

            // 越界隐藏。联动 dashboard 下副图不应弹 tooltip 这件事由 LinkedPaneSchemaContext.Decorate
            // 在装配阶段 Remove<TooltipWidgetFeature> 保证,Feature 端不再二次判定。
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
                IHevoBrush xBrush = XMeta?.GetDefaultBrush() ?? new HevoSolidBrush(Colors.White);

                // 完美格式化
                string xStr = val is IFormattable f ? f.FormatValue(xFormat, xProvider) : val?.ToString() ?? "";

                _rowBuffer[rowCount++] = new TooltipRow(xName, xStr, xBrush);
            }

            // 捞取其它指标数据 —— double 系列(LineSeries / Bar / Candle...)走 DoubleSeriesDataTrait,
            // 字符串元信息(证券名称 / 行业 等)走 StringSeriesDataTrait,两路并存。
            // ActiveLayers 顺序 = 行顺序 —— 业务侧蓝图想让"名称" 在第一行,就把 NameSeriesFeature
            // 节点放在其他 series feature 之前(framework 不做特权排序)。
            foreach (var layer in Chart.ActiveLayers)
            {
                var proxy = ctx.For(layer);
                var sMeta = proxy.Read<MetaTrait>();
                if (sMeta == null) continue;

                var doubleData = proxy.Read<DoubleSeriesDataTrait>();
                var stringData = proxy.Read<StringSeriesDataTrait>();
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
                    else if (stringData != null && i < stringData.FieldValues.Length &&
                             state.LocalIndex >= 0 && state.LocalIndex < stringData.FieldValues[i].Length)
                    {
                        string sVal = stringData.FieldValues[i].Span[state.LocalIndex];
                        if (!string.IsNullOrEmpty(sVal))
                        {
                            formattedString = sVal; // string 字段原样输出,format 留作未来扩展
                            hasValue = true;
                        }
                    }

                    if (hasValue)
                    {
                        IHevoBrush finalColor = indexResolver != null
                                ? indexResolver.Resolver.ResolveByIndex(i, state.LocalIndex)
                                : fieldMeta.GetDefaultBrush();

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
