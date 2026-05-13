using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using System.Windows.Media;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// 字符串元信息 series —— 把一根 <c>ReadOnlyMemory&lt;string&gt;</c> 列(典型:证券名称)发布成
    /// <see cref="MetaTrait"/> + <see cref="StringSeriesDataTrait"/>,挂在自家 <see cref="MetadataLayer"/> 上,
    /// 供 Tooltip / Header 等下游通过 <see cref="ChartCell.ActiveLayers"/> 通用协议取用。
    ///
    /// <para>
    /// 设计动机:消除 <see cref="TooltipWidgetFeature{TX}"/> 之前的 <c>NamesDataPort</c> 反模式 ——
    /// 通用 Tooltip 不该硬编码"名称"业务字段。现在"名称"跟"涨跌幅 / MACD"等指标一视同仁,
    /// 走完全相同的 layer trait 协议,后来者(行业 / 地区代码 等)新增 0 framework 改动。
    /// </para>
    ///
    /// <para>
    /// <b>Tooltip 行顺序</b>:按 <see cref="ChartCell.ActiveLayers"/> 顺序拼行,而 ActiveLayers 顺序由
    /// Feature Compose 先后决定。要让"名称"出现在第一行,业务侧蓝图把 <see cref="NameSeriesFeature"/>
    /// 节点放在其他 Series Feature **之前**即可,framework 无须额外排序协议。
    /// </para>
    /// </summary>
    public class NameSeriesFeature : ChartFeature
    {
        public override FeaturePhase Phase => FeaturePhase.Series;

        /// <summary>字符串列数据源(每行索引对应一个 string 值)。</summary>
        public DataPort<ReadOnlyMemory<string>> DataPort { get; init; } = null!;

        // layer 完全私有 —— Schema 无须知道
        private readonly MetadataLayer _layer = new();

        /// <summary>底层 layer 名,调试 / 多实例区分用。</summary>
        public string LayerName { get; init; } = "NameMetadata";

        /// <summary>字段元数据(图例 / Tooltip 标签 + 画刷)。null 时由 OnCompose 用下面的标量字段合成。</summary>
        public FieldMeta? Meta { get; init; }

        // ─ 蓝图友好标量配置:Meta == null 时生效 ─

        /// <summary>蓝图配置:字段显示名(Tooltip 标签)。默认"名称"。</summary>
        public string? MetaName { get; init; } = "名称";

        /// <summary>蓝图配置:字段颜色,默认白色。</summary>
        public Color MetaColor { get; init; } = Colors.White;

        /// <summary>蓝图配置:字符串格式串,默认"G"(原样输出)。string 通常用不到,留着保持跟 double 字段对称。</summary>
        public string MetaFormat { get; init; } = "G";

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            _layer.Name = LayerName;
            var proxy = AttachLayer(_layer);

            var meta = Meta ?? FieldMeta.Literal(MetaName ?? LayerName, MetaColor, MetaFormat);
            proxy.PublishData(new MetaTrait(WpfRenderRegistry.ResolveString(meta.Name), meta));
        }

        protected override void OnProject(FeatureContext ctx)
        {
            var (data, _) = ctx.UsePort(DataPort);
            ctx.For(_layer).PublishData(new StringSeriesDataTrait(data));
        }
    }
}
