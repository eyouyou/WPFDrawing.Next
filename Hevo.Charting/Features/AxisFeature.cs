using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;

namespace Hevo.Charting
{
    /// <summary>
    /// 💥 坐标轴句柄：用于在独立的图表组件（如光标和坐标轴）之间建立强类型的物理空间关联。
    /// 核心目的：彻底消灭易错的魔法字符串 (Magic Strings)，将校验工作移交给 C# 编译器！
    /// </summary>
    public sealed class AxisHandle
    {
        // 内部自动生成唯一暗号，外部无需关心
        internal string Id { get; } = Guid.NewGuid().ToString("N");
    }
}

namespace Hevo.Charting.Features
{
    /// <summary>
    /// 💥 智能空间注册表：全图表的“隐形地图”，存放在全局 Shared 上下文中。
    /// 大脑(Feature)计算完坐标后在此登记，其他大脑可随时 O(1) 提取。
    /// 支持两种匹配模式：
    /// 1. 按 Handle 精确打击 (多轴高玩必备)
    /// 2. 按 Placement 模糊备忘 (单轴小白兜底)
    /// </summary>
    public class AxisLayoutRegistryTrait : IVisualTrait
    {
        // 1. 精确匹配字典：记录拥有专属 Handle 的轴的物理绝对坐标
        public Dictionary<AxisHandle, double> VerticalAnchors { get; } = new();
        public Dictionary<AxisHandle, double> HorizontalAnchors { get; } = new();

        // 2. 模糊匹配备忘录：记录各个方向上（左/右/上/下），最新绘制的轴的坐标
        public Dictionary<AxisPlacement, double> LastKnownAnchors { get; } = new();
    }

    // 💥 明确的数学语义：自变量(Domain) 还是 因变量(Value)
    public enum ScaleMapping { Domain, Value }

    /// <summary>
    /// 万能坐标轴特征（终极正交版）：物理方位、数学映射、作用域彻底解耦
    /// </summary>
    public class AxisFeature<TDomain> : ChartFeature
    {
        public AxisHandle Handle { get; init; } = new AxisHandle();
        public override FeaturePhase Phase => FeaturePhase.Scale;
        public DataPort<RealRange> RangePort { get; init; } = null!;
        public ScaleMapping Mapping { get; init; } = ScaleMapping.Domain;
        public bool IsShared { get; init; } = false;

        // 🚀 完美复原：Feature 只需要持有这个对象，不需要把它压扁！
        public AxisStyleTrait AxisStyle { get; init; } = null!;
        public GridStyleTrait? GridStyle { get; init; }

        private bool IsHorizontal => AxisStyle.Placement == AxisPlacement.Top || AxisStyle.Placement == AxisPlacement.Bottom;
        private readonly ITickProvider<TDomain> _tickProvider;
        private readonly AxisLayer<TDomain> _axisLayer;
        private readonly GridLineLayer<TDomain> _gridLayer;

        public AxisFeature(ITickProvider<TDomain> tickProvider, string name = "Axis")
        {
            _tickProvider = tickProvider ?? throw new ArgumentNullException(nameof(tickProvider));
            _axisLayer = new AxisLayer<TDomain>($"{name}_Layer");
            _gridLayer = new GridLineLayer<TDomain>($"{name}_Grid");
        }

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            AttachLayer(_gridLayer);
            AttachLayer(_axisLayer);
        }

        protected override void OnProject(FeatureContext ctx)
        {
            var (range, rangeChanged) = ctx.UsePort(RangePort);
            var plotArea = ctx.PlotArea;
            var scaleTrait = ctx.Shared().Read<ScaleStrategyTrait>();

            if (!range.IsValid || plotArea.IsEmpty || AxisStyle == null || scaleTrait == null) return;

            // 优先从当前上下文 (Local Seed) 获取 Scale，防退化
            double physicalLength = IsHorizontal ? plotArea.Width : plotArea.Height;
            var scale = Mapping == ScaleMapping.Domain ? scaleTrait.DomainScale : scaleTrait.ValueScale;

            // 💥 0-GC 优化 1：彻底消灭隐式闭包
            var strategy = _tickProvider.GetStrategy(ctx);
            var stylePolicy = _tickProvider.GetStyle(ctx);

            // ==========================================
            // 💥 终极修复 2：纯净的依赖元组
            // 此时 deps 里全是值类型 (range, length)、引用地址不变的静态类 (scale) 
            // 以及只 new 过一次的长生命周期对象 (strategy, stylePolicy)。
            // 因此，这段核心计算逻辑不仅 0-GC，而且绝不会触发嵌套 Hook 熔断！
            // ==========================================
            var (ticks, ticksChanged) = ctx.UseMemo(
                            (range, physicalLength, scale, strategy, stylePolicy),
                            static deps =>
                            {
                                // 1. 让策略去算出需要哪些刻度 (只取逻辑值 Value 和 文本格式)
                                var rawTicks = deps.strategy.Calculate(deps.range, deps.physicalLength).ApplyStyle(deps.stylePolicy).ToArray();

                                // 无论比例尺是线性的还是非线性的，永远、无条件地强制通过全局 Scale 进行二次映射！
                                // 保证坐标轴刻度与 K线、折线的物理坐标 100% 像素级对齐！
                                for (int i = 0; i < rawTicks.Length; i++)
                                {
                                    double logicalValue = Convert.ToDouble(rawTicks[i].Value);
                                    double realRatio = deps.scale.Normalize(logicalValue, deps.range);
                                    rawTicks[i] = rawTicks[i] with { Ratio = realRatio };
                                }

                                return new AxisTickDataTrait<TDomain>(rawTicks, rawTicks.Length);
                            });
            // 💥 0-GC 优化 2：Style 的 with 克隆操作用 static 锁死
            var (finalStyleTrait, styleChanged) = ctx.UseMemo((plotArea, AxisStyle),
                static deps =>
                {
                    double anchor = 0.0;
                    switch (deps.AxisStyle.Placement)
                    {
                        case AxisPlacement.Bottom: anchor = deps.AxisStyle.CustomPhysicalAnchor ?? deps.plotArea.Bottom; break;
                        case AxisPlacement.Top: anchor = deps.AxisStyle.CustomPhysicalAnchor ?? deps.plotArea.Top; break;
                        case AxisPlacement.Left: anchor = deps.AxisStyle.CustomPhysicalAnchor ?? deps.plotArea.Left; break;
                        case AxisPlacement.Right: anchor = deps.AxisStyle.CustomPhysicalAnchor ?? deps.plotArea.Right; break;
                    }
                    return deps.AxisStyle with { AbsoluteAnchor = anchor };
                });

            // 💥 【已删除 AxisScaleTrait 缓存生成代码】图层已经瞎了，不需要知道了！

            var registry = ctx.Shared().Read<AxisLayoutRegistryTrait>();
            if (registry == null)
            {
                registry = new AxisLayoutRegistryTrait();
                ctx.Shared().PublishData(registry);
            }

            if (IsHorizontal)
            {
                registry.HorizontalAnchors[this.Handle] = finalStyleTrait.AbsoluteAnchor;
                registry.LastKnownAnchors[AxisStyle.Placement] = finalStyleTrait.AbsoluteAnchor;
            }
            else
            {
                registry.VerticalAnchors[this.Handle] = finalStyleTrait.AbsoluteAnchor;
                registry.LastKnownAnchors[AxisStyle.Placement] = finalStyleTrait.AbsoluteAnchor;
            }

            if (IsShared) ctx.Shared().UpdateSharedXAxis(range);
            else ctx.For(_axisLayer).UpdateYAxis(range);

            if (ticksChanged || rangeChanged || styleChanged)
            {
                ctx.For(_axisLayer).PublishData(finalStyleTrait);
                ctx.For(_axisLayer).PublishData(ticks);

                // 💥 【已删除 AxisScaleTrait 发货逻辑】

                if (GridStyle != null)
                {
                    ctx.For(_gridLayer).PublishData(GridStyle);
                    ctx.For(_gridLayer).PublishData(ticks);
                    // 💥 【已删除 GridLayer 的 AxisScaleTrait 发货逻辑】
                }
            }
        }
    }
}
