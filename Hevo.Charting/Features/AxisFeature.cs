using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
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
    /// 💥 智能空间注册表：全图表的"隐形地图"，存放在全局 Shared 上下文中。
    /// 大脑(Feature)计算完坐标后在此登记，其他大脑可随时 O(1) 提取。
    /// 支持两种匹配模式：
    /// 1. 按 Handle 精确打击 (多轴高玩必备)
    /// 2. 按 Placement 模糊备忘 (单轴小白兜底)
    /// </summary>
    public class AxisLayoutRegistryTrait : IVisualTrait
    {
        /// <summary>纵轴精确表:Handle → 该轴的物理 X 像素。Crosshair Y 标签按 Handle 锁定时走这里。</summary>
        public Dictionary<AxisHandle, double> VerticalAnchors { get; } = new();

        /// <summary>横轴精确表:Handle → 该轴的物理 Y 像素。横向多轴时按 Handle 精确定位用。</summary>
        public Dictionary<AxisHandle, double> HorizontalAnchors { get; } = new();

        /// <summary>方位模糊表:Placement → 该方向最新绘制轴的坐标。单轴/未传 Handle 的下游降级查这里。</summary>
        public Dictionary<AxisPlacement, double> LastKnownAnchors { get; } = new();
    }

    /// <summary>映射方向枚举:Domain = 自变量(横轴/索引/时间);Value = 因变量(纵轴/价格/数值)。</summary>
    public enum ScaleMapping { Domain, Value }

    /// <summary>
    /// 万能坐标轴特征（终极正交版）：物理方位、数学映射、作用域彻底解耦。
    /// 逻辑域严格跟 RealRange / IScale 的 double 契约对齐，不再携带假泛型。
    /// <para>
    /// <b>典型用法</b>(更友好的 DSL 见 <see cref="FeatureCanvasExtensions"/>):
    /// <code>
    /// new AxisFeature(new TradeTimeTickStrategy(...))
    /// {
    ///     RangePort = Viewport.ActiveRange,
    ///     Mapping   = ScaleMapping.Domain,
    ///     IsShared  = true,
    ///     AxisStyle = AxisStyleTrait.Create(AxisPlacement.Bottom, Colors.Gray),
    /// }
    /// </code>
    /// </para>
    /// <para>
    /// <b>数据流</b>:RangePort + ScaleStrategyTrait → UseMemo 算 tick → AxisLayer / GridLineLayer 渲染;
    /// 同帧把本轴的 AbsoluteAnchor 写入 Shared 的 <see cref="AxisLayoutRegistryTrait"/>,供 CrosshairFeature 反查 Y 标签贴轴坐标。
    /// </para>
    /// </summary>
    public class AxisFeature : ChartFeature
    {
        /// <summary>本轴的强类型句柄。Crosshair 等下游用 Handle 在 <see cref="AxisLayoutRegistryTrait"/> 里精确锁定本轴的物理 X，替代易错的"按 Placement 模糊匹配"。</summary>
        public AxisHandle Handle { get; init; } = new AxisHandle();

        public override FeaturePhase Phase => FeaturePhase.Scale;

        /// <summary>本轴消费的逻辑量程端口。横轴一般接 Viewport.ActiveRange,纵轴一般接 AutoScale 算出的 YRangePort。</summary>
        public DataPort<RealRange> RangePort { get; init; } = null!;

        /// <summary>当前轴属于自变量(Domain)还是因变量(Value),决定 ProjectStrategyTrait 里挑哪一支 IScale 做归一化。</summary>
        public ScaleMapping Mapping { get; init; } = ScaleMapping.Domain;

        /// <summary>true = 把本轴量程写回 Shared 黑板的 X 轴 trait,供联动 dashboard 共享;false = 仅写本图层的 Y 轴 trait。</summary>
        public bool IsShared { get; init; } = false;

        /// <summary>视觉样式 trait(Placement / 字体 / 刻度长度等)。Feature 持有原对象、按需 with 出 AbsoluteAnchor 后下发图层。
        /// 蓝图侧由 <see cref="LowCode.Designer.GraphViewer.NodeEditorWindow"/> 当作"配置组"层层钻入编辑。</summary>
        public AxisStyleTrait AxisStyle { get; init; } = null!;

        /// <summary>可选网格线样式。null = 不画网格;非空时与本轴 tick 同源,挂在 GridLineLayer。</summary>
        public GridStyleTrait? GridStyle { get; init; }

        /// <summary>💥 双轴镜像广播槽:本轴每帧把 tick 比例写入此实例,供副轴的 <see cref="MirrorRatioTickStrategy"/> 读取。Schema 持有同一实例。</summary>
        public MirrorTickAnchor? BroadcastTicksTo { get; init; }

        /// <summary>💥 双轴镜像订阅槽:副轴登记此实例,其 Version 加入 UseMemo 依赖,主轴 tick 变化时副轴失效重算。</summary>
        public MirrorTickAnchor? MirrorTicksFrom { get; init; }

        private bool IsHorizontal => AxisStyle.Placement == AxisPlacement.Top || AxisStyle.Placement == AxisPlacement.Bottom;

        /// <summary>
        /// tick label 数值格式串 ("G" / "F2" / "0.00%" 等)。仅在 <see cref="TickProvider"/> 未显式注入时被
        /// 默认的 <see cref="NumericTickProvider"/> 消费;若业务自定义了 TickProvider,Format 不再生效。
        /// </summary>
        public string Format { get; init; } = "G";

        /// <summary>
        /// 调试 / 多实例区分用的轴名称,影响内部 AxisLayer / GridLineLayer 的层名后缀。
        /// </summary>
        public string Name { get; init; } = "Axis";

        /// <summary>
        /// tick 算法实现。null = OnCompose 时按 <see cref="Format"/> 合一份 <see cref="NumericTickProvider"/>。
        /// 业务侧需要 DomainTickProvider / TradeTimeTickStrategy 等时显式注入 (二者不能 JSON 表达,只能代码侧 new)。
        /// </summary>
        public ITickProvider? TickProvider { get; init; }

        // 实际生效的 tick 算法。OnCompose 时确定,之后只读。
        private ITickProvider _effectiveTickProvider = null!;

        // 文字 / 刻度短线 / 基线渲染层(Background 级)。OnCompose 时根据 Name 创建。
        private AxisLayer _axisLayer = null!;
        // 网格线渲染层(Background 级,在 _axisLayer 下方铺开,被 K 线压住)。
        private GridLineLayer _gridLayer = null!;

        /// <summary>蓝图 / 反射友好的无参构造。<see cref="TickProvider"/> 默认走 <see cref="NumericTickProvider"/>(<see cref="Format"/>)。</summary>
        public AxisFeature() { }

        /// <summary>显式 ctor:跟低代码无关,纯业务老 API 的兼容入口。
        /// 等价 <c>new AxisFeature { TickProvider = ..., Name = ... }</c>。</summary>
        public AxisFeature(ITickProvider tickProvider, string name = "Axis")
        {
            TickProvider = tickProvider ?? throw new ArgumentNullException(nameof(tickProvider));
            Name = name;
        }

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            // tick 算法 / 渲染层都延迟到 OnCompose 才物化:此时 init 属性已注入完毕。
            _effectiveTickProvider = TickProvider ?? new NumericTickProvider(Format, null, null);
            _axisLayer = new AxisLayer($"{Name}_Layer");
            _gridLayer = new GridLineLayer($"{Name}_Grid");
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
            var strategy = _effectiveTickProvider.GetStrategy(ctx);
            var stylePolicy = _effectiveTickProvider.GetStyle(ctx);

            // ==========================================
            // 💥 终极修复 2：纯净的依赖元组
            // 此时 deps 里全是值类型 (range, length)、引用地址不变的静态类 (scale) 
            // 以及只 new 过一次的长生命周期对象 (strategy, stylePolicy)。
            // 因此，这段核心计算逻辑不仅 0-GC，而且绝不会触发嵌套 Hook 熔断！
            // ==========================================
            // 💥 镜像版本号塞进依赖：主轴 tick 变化 → MirrorTickAnchor.Version 自增 → 副轴 UseMemo 失效重算。
            // 主轴本身没有 mirror 时取 -1,不影响其自身缓存命中。
            int mirrorVersion = MirrorTicksFrom?.Version ?? -1;
            var (ticks, ticksChanged) = ctx.UseMemo(
                            (range, physicalLength, scale, strategy, stylePolicy, mirrorVersion),
                            static deps =>
                            {
                                // 1. 让策略去算出需要哪些刻度 (只取逻辑值 Value 和 文本格式)
                                var rawTicks = deps.strategy.Calculate(deps.range, deps.physicalLength).ApplyStyle(deps.stylePolicy).ToArray();

                                // 无论比例尺是线性的还是非线性的，永远、无条件地强制通过全局 Scale 进行二次映射！
                                // 保证坐标轴刻度与 K线、折线的物理坐标 100% 像素级对齐！
                                for (int i = 0; i < rawTicks.Length; i++)
                                {
                                    // Value 直接是 double，无需 Convert.ToDouble 的装箱与虚派发
                                    double realRatio = deps.scale.Normalize(rawTicks[i].Value, deps.range);
                                    rawTicks[i] = rawTicks[i] with { Ratio = realRatio };
                                }

                                return new AxisTickDataTrait(rawTicks, rawTicks.Length);
                            });

            // 💥 主轴广播：tick 变化时把最新比例写入共享槽,触发副轴重算。
            //    依赖 ticksChanged 节流,平稳帧零写入零 GC。
            if (ticksChanged && BroadcastTicksTo != null)
            {
                BroadcastTicksTo.Capture(new System.ReadOnlySpan<TickModel>(ticks.Ticks, 0, ticks.Count));
            }
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
