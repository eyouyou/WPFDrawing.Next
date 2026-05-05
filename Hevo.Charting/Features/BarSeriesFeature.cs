using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using System.Windows.Media;

namespace Hevo.Charting.Features
{
    public static class BarSeriesFeatureExtensions
    {
        // Viewport 由 ReactiveSchema.Add 自动注入（L6 / §B.2.6），外部不再显式传 vp。
        public static SeriesBuilder AddBar(this SeriesBuilder builder, DataPort<ReadOnlyMemory<double>> dataPort, DataPort<RealRange> rangePort, FieldMeta meta, double widthRatio = 0.8)
        {
            builder.Canvas.Add(new BarSeriesFeature
            {
                DataPort = dataPort,
                YRangePort = rangePort,
                Meta = meta,
                BarWidthRatio = widthRatio
            });
            return builder;
        }
    }

    public class BarSeriesFeature : ChartFeature, IIndexBrushResolver
    {
        public override FeaturePhase Phase => FeaturePhase.Series;

        /// <summary>柱体高度数据源(每根索引对应一个 double 值)。Slicer 删除后世界索引 == 数组下标。</summary>
        public DataPort<ReadOnlyMemory<double>> DataPort { get; init; } = null!;

        /// <summary>Y 轴量程引脚:由 <see cref="UniversalAutoScaleFeature"/> 算好后流入,决定柱体在画布上的纵向归一化基准。</summary>
        public DataPort<RealRange> YRangePort { get; init; } = null!;

        // Viewport 由 ChartFeature 基类统一持有（L6 / §B.2.6）

        /// <summary>字段元数据:控制图例文本/颜色/数值格式。Resolver 还会被 Crosshair 圆点/Tooltip 复用。</summary>
        public FieldMeta Meta { get; init; } = FieldMeta.Literal("", new HevoSolidBrush(Colors.Red));

        /// <summary>柱宽相对单格距离的比例(0~1)。0.6 即柱体占满 60%、两侧各留 20% 空隙,不影响坐标对齐。</summary>
        public double BarWidthRatio { get; init; } = 0.6;

        private readonly BarLayer _layer = new();

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            AttachLayer(_layer).Meta(new MetaTrait("Bar", Meta));
        }

        private ReadOnlyMemory<double> _currentDataSpan;
        private IBrushResolver<double>? _currentResolver;
        private IHevoBrush _currentConstantBrush = null!;

        protected override void OnProject(FeatureContext ctx)
        {
            var (dataSpan, _) = ctx.UsePort(DataPort);

            // 💥 修复 2：把 Y 轴和 X 轴的数据从引脚里扒出来！
            var (yRange, _) = ctx.UsePort(YRangePort);
            var (xRange, _) = ctx.UsePort(Viewport.ActiveRange);

            _currentDataSpan = dataSpan;
            _currentConstantBrush = Meta.GetConstantBrush();
            _currentResolver = Meta.Resolver;

            var proxy = ctx.For(_layer);

            // 💥 修复 3：全量发货！缺一不可！
            proxy.PublishData(new BarDataTrait(dataSpan, _currentConstantBrush, BarWidthRatio));
            proxy.PublishData(new XAxisTrait(xRange));
            proxy.PublishData(new YAxisTrait(yRange));
            proxy.PublishData(new BarPaletteTrait(_currentResolver));
            // ==========================================
            // 🎁 附赠神级优化：兼容十字光标吸附！
            // 回想一下，你的 CrosshairFeature 是靠扫描 DoubleSeriesDataTrait 来寻找交点的。
            // 顺手把这个标准 Trait 推出去，你的十字光标瞬间就能吸附到柱状图上了！
            // ==========================================
            proxy.PublishData(new DoubleSeriesDataTrait(new[] { dataSpan }));

            // 颜色特征也推出去，让十字光标的交点圆圈知道自己该染什么色
            proxy.PublishData(new IndexBrushResolverTrait(this));
        }

        public IHevoBrush ResolveByIndex(int fieldIdx, int localIdx)
        {
            if (localIdx < 0 || localIdx >= _currentDataSpan.Length) return _currentConstantBrush;
            return _currentResolver != null
                ? _currentResolver.Resolve(_currentDataSpan.Span[localIdx])
                : _currentConstantBrush;
        }
    }
}
