using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using System.Windows.Media;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// 💥 泛型单线特征
    /// 架构亮点：绝对内聚！自己管理 Layer 生命期，对外只暴露极简 API。
    /// </summary>
    public class LineSeriesFeature : ChartFeature
    {
        public override FeaturePhase Phase => FeaturePhase.Series;

        /// <summary>线段值数据源(每根索引对应一个 double 值,允许 NaN 制造断点)。</summary>
        public DataPort<ReadOnlyMemory<double>> DataPort { get; init; } = null!;

        /// <summary>💥 Y 轴量程引脚:多线共用 Y 轴时,由统一的 <see cref="UniversalAutoScaleFeature"/> 算好后流入。</summary>
        public DataPort<RealRange> YRangePort { get; init; } = null!;

        // Viewport 由 ChartFeature 基类统一持有，由 ReactiveSchema.Add 自动注入（L6 / §B.2.6）。

        // 💥 物理图层由特征完全私有化！Schema 无权也无需干涉！
        private readonly LineLayer _layer = new();

        /// <summary>底层图层名,仅用于调试 / 多实例区分。</summary>
        public string LayerName { get; init; } = "LineLayer";

        /// <summary>线条样式(画笔/是否走样条插值)。null 时由 OnCompose 用 Meta 的常量画刷构造默认 1px 直线。</summary>
        public LineStyle? Style { get; init; }

        /// <summary>字段元数据:控制图例 / Header / Tooltip 的名字、颜色、格式。
        /// null 时 OnCompose 会用 <see cref="MetaName"/> / <see cref="MetaColor"/> / <see cref="MetaFormat"/> 合成,
        /// 让低代码 JSON 蓝图无需手写 FieldMeta 字面量也能跑起来。</summary>
        public FieldMeta? Meta { get; init; }

        // ==========================================
        // 蓝图友好的标量配置:Meta == null 时生效。
        // SmartActivator.SafeChangeType 走 TypeConverter 路径,Color 字段直接接受
        // "#RRGGBB" / "Red" 字符串,JSON 描述蓝图无需理解 FieldMeta 内部结构。
        // ==========================================

        /// <summary>蓝图配置:字段显示名(图例 / tooltip 标签)。null 时回退 <see cref="LayerName"/>。</summary>
        public string? MetaName { get; init; }

        /// <summary>蓝图配置:线条颜色,默认 SteelBlue。</summary>
        public Color MetaColor { get; init; } = Colors.SteelBlue;

        /// <summary>蓝图配置:数值显示格式串,默认 "G"。</summary>
        public string MetaFormat { get; init; } = "G";

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            _layer.Name = LayerName;

            // 💥 特征自己向图表引擎注册图层！彻底解放 Schema！
            var proxy = AttachLayer(_layer);

            // 业务侧若直接 new + 注入 Meta 走老路;蓝图侧未配 Meta 时按标量字段合成。
            var meta = Meta ?? FieldMeta.Literal(MetaName ?? LayerName, MetaColor, MetaFormat);

            var finalStyle = Style ?? LineStyle.Create(meta.GetConstantBrush(), thickness: 1.0, useSpline: false);
            proxy.LineStyle(finalStyle);

            // 💥 自动协议升维：将极简的 FieldMeta 包装为底层引擎所需的 MetaTrait
            proxy.PublishData(new MetaTrait(WpfRenderRegistry.ResolveString(meta.Name), meta));

        }

        protected override void OnProject(FeatureContext ctx)
        {
            // 必须在函数最顶层无条件执行 Hook！按固定顺序排列，不得条件化。
            var (dataCol, _) = ctx.UsePort(DataPort);
            var (yRange, _) = ctx.UsePort(YRangePort);
            var (xRange, _) = ctx.UsePort(Viewport.ActiveRange);

            // 如果数据未就绪，安全退出
            if (dataCol.Length == 0 || yRange.IsEmpty || !xRange.IsValid) return;

            var proxy = ctx.For(_layer);
            // 💥 发布 layer-local XAxisTrait：与 BarSeriesFeature 一致，绕开 shared 路径的 timing 依赖，
            // 保证 LineLayer.OnUpdate 永远读到与本帧 Crosshair/Axis 完全一致的 rangeX。
            proxy.PublishData(new XAxisTrait(xRange));
            proxy.UpdateYAxis(yRange);
            proxy.PublishData(new DoubleSeriesDataTrait(dataCol));
        }
    }
}
