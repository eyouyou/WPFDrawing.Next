using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// 💥 泛型单线特征 
    /// 架构亮点：绝对内聚！自己管理 Layer 生命期，对外只暴露极简 API。
    /// </summary>
    public class LineSeriesFeature : ChartFeature
    {
        public override FeaturePhase Phase => FeaturePhase.Series;

        public DataPort<ReadOnlyMemory<double>> DataPort { get; init; } = null!;

        // 💥 Y 轴量程引脚：如果是多线共用 Y 轴，这个引脚将由专门的 Y 轴特征计算后流入
        public DataPort<RealRange> YRangePort { get; init; } = null!;

        // Viewport 由 ChartFeature 基类统一持有，由 ReactiveSchema.Add 自动注入（L6 / §B.2.6）。

        // 💥 物理图层由特征完全私有化！Schema 无权也无需干涉！
        private readonly LineLayer _layer = new();

        public string LayerName { get; init; } = "LineLayer";
        public LineStyle? Style { get; init; }

        // 💥 极简元数据注入：只接收单列的 FieldMeta 结构体！
        public FieldMeta Meta { get; init; }

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            _layer.Name = LayerName;

            // 💥 特征自己向图表引擎注册图层！彻底解放 Schema！
            var proxy = AttachLayer(_layer);

            var finalStyle = Style ?? LineStyle.Create(Meta.GetConstantBrush(), thickness: 1.0, isSmooth: false);
            proxy.LineStyle(finalStyle);

            // 💥 自动协议升维：将极简的 FieldMeta 包装为底层引擎所需的 MetaTrait
            proxy.PublishData(new MetaTrait(WpfRenderRegistry.ResolveString(Meta.Name), Meta));

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
