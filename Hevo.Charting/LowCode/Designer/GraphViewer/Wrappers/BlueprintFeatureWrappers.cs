using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using System;
using System.Reflection;
using System.Windows.Media;

namespace Hevo.Charting.LowCode.Designer.GraphViewer.Wrappers
{
    // ==========================================
    //  泛型 → double / 构造参数 → init 属性 的低代码 wrapper。
    //  低代码.md §6 列出的两类边界情况:
    //    • 泛型 Feature (CrosshairFeature<TX> / TooltipWidgetFeature<TX>):
    //        BuiltinRegistration 会过滤掉泛型定义,但派一个 closed 子类(typeof TX = double) 即可登记。
    //    • 含构造参数的 Feature (AxisFeature(ITickProvider, name)):
    //        派一个无参子类,在 ctor 里默认构造 TickProvider,
    //        把"用户应该可配的字段(format / placement)"提升为 init 属性,OnCompose 反射回填 init-only 默认。
    //  这些 wrapper 由 BuiltinRegistration 自动收录,picker 里就直接能选。
    // ==========================================

    /// <summary>
    /// <see cref="CrosshairFeature{TX}"/> 的蓝图友好基类:把 <see cref="CrosshairFeature{TX}.YTrackers"/>
    /// (<see cref="CrosshairYTrackInfo"/> record-struct 数组,夹带 <c>DataPort</c>)拍扁成三个并行数组,
    /// 让 InputBindings 数组协议直接焊端口、Properties 注 metas/placements。
    /// <para>
    /// 蓝图侧:
    /// </para>
    /// <code>
    /// "InputBindings": {
    ///   "HitStatePort":   "interaction_hit",
    ///   "XAxisDataPort":  "candle_time",
    ///   "YTrackerPorts":  ["yrange_main", "yrange_volume"]      // 数组协议,扇入 N 个 RangePort
    /// },
    /// "Properties": {
    ///   "YTrackerMetas":      [ {Name:"价格"}, {Name:"量"} ],
    ///   "YTrackerPlacements": [ "Right", "Left" ]
    /// }
    /// </code>
    /// <para>
    /// <b>语义</b>:OnCompose 时,若用户没显式给 <see cref="CrosshairFeature{TX}.YTrackers"/>
    /// (默认空数组)且 YTrackerPorts 非空,就按下标 zip 三个并行数组重组成 YTrackers 反射写回 base ——
    /// 跟 <see cref="CandleBlueprintFeature"/> 把 5 个扁平端口装回 <c>CandlePorts</c> record 同 pattern。
    /// 用户显式塞了 YTrackers(传统 DSL 用法)就不覆盖,跟现有路径并行兼容。
    /// </para>
    /// </summary>
    public abstract class CrosshairBlueprintFeatureBase<TX> : CrosshairFeature<TX>
    {
        /// <summary>多 Y 轴扇入端口数组(等价于 <c>YTrackers.Select(t =&gt; t.Port)</c>)。蓝图用 PortBindings 数组协议焊 N 个 id。</summary>
        public DataPort<RealRange>[] YTrackerPorts { get; init; } = Array.Empty<DataPort<RealRange>>();

        /// <summary>跟 <see cref="YTrackerPorts"/> 等长的 Meta 配置;短了用 null 兜底,长了被截。</summary>
        public FieldMeta?[] YTrackerMetas { get; init; } = Array.Empty<FieldMeta?>();

        /// <summary>跟 <see cref="YTrackerPorts"/> 等长的 Placement 配置;短了默认 <see cref="AxisPlacement.Left"/>。</summary>
        public AxisPlacement[] YTrackerPlacements { get; init; } = Array.Empty<AxisPlacement>();

        // 反射缓存:YTrackers 是 CrosshairFeature<TX> 上的 init 属性,SetValue 穿透 init 修饰符。
        // 注意 PropertyInfo 必须从已闭合的泛型基类取,否则 SetValue 在 net5+ 反射上无法落地泛型字段。
        private static readonly PropertyInfo _yTrackersProperty =
            typeof(CrosshairFeature<TX>).GetProperty(nameof(YTrackers))!;

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            // 用户没塞 YTrackers 但塞了 YTrackerPorts → 按下标 zip 重组,反射写回 base.YTrackers。
            // 跟 CandleBlueprintFeature 的扁平端口 → CandlePorts record 同思路。
            if (YTrackerPorts.Length > 0 && (YTrackers == null || YTrackers.Length == 0))
            {
                var trackers = new CrosshairYTrackInfo[YTrackerPorts.Length];
                for (int i = 0; i < YTrackerPorts.Length; i++)
                {
                    trackers[i] = new CrosshairYTrackInfo(
                        Port: YTrackerPorts[i],
                        Meta: i < YTrackerMetas.Length ? YTrackerMetas[i] : null,
                        Placement: i < YTrackerPlacements.Length ? YTrackerPlacements[i] : AxisPlacement.Left);
                }
                _yTrackersProperty.SetValue(this, trackers);
            }
            base.OnCompose(chart, ctx, flow);
        }
    }

    /// <summary>
    /// <see cref="CrosshairFeature{TX}"/> 的 closed-over-double 子类,蓝图可见。
    /// X 轴原始数据通常是时间索引(双精度浮点表达,允许小数索引),故 double 是最通用的 TX。
    /// 继承 <see cref="CrosshairBlueprintFeatureBase{TX}"/> 拿到 YTrackerPorts/Metas/Placements 三件套。
    /// </summary>
    public class CrosshairDoubleFeature : CrosshairBlueprintFeatureBase<double> { }

    /// <summary>
    /// <see cref="TooltipWidgetFeature{TX}"/> 的 closed-over-double 子类,蓝图可见。
    /// </summary>
    public class TooltipDoubleWidgetFeature : TooltipWidgetFeature<double> { }

    /// <summary>
    /// <see cref="CrosshairFeature{TX}"/> 的 closed-over-DateTime 子类,蓝图可见。
    /// K 线 / 分时 这种 X 轴是时间戳的业务图用这个版本 ——
    /// XAxisDataPort 类型是 <c>DataPort&lt;ReadOnlyMemory&lt;DateTime&gt;&gt;</c>,
    /// 跟数据源的 Times 列直接对接,不绕 double 索引。
    /// 继承 <see cref="CrosshairBlueprintFeatureBase{TX}"/> 拿到 YTrackerPorts/Metas/Placements 三件套。
    /// </summary>
    public class CrosshairDateTimeFeature : CrosshairBlueprintFeatureBase<DateTime> { }

    /// <summary>
    /// <see cref="TooltipWidgetFeature{TX}"/> 的 closed-over-DateTime 子类,蓝图可见。
    /// 跟 <see cref="CrosshairDateTimeFeature"/> 配对,典型 K 线 / 分时 X 轴用。
    /// </summary>
    public class TooltipDateTimeWidgetFeature : TooltipWidgetFeature<DateTime> { }

    /// <summary>
    /// <see cref="CandleFeature"/> 的"扁平 5 端口"蓝图友好版。
    /// <para>
    /// CandleFeature.Ports 是 <see cref="CandlePorts"/> record (5 个 DataPort 的全家桶),
    /// 而蓝图 InputBindings 协议只支持单 DataPort / DataPort[] 数组,无法直接焊一个 record-of-DataPort。
    /// 本 wrapper 把 5 个端口拍扁成独立 init 属性,蓝图侧:
    /// </para>
    /// <code>
    /// "InputBindings": {
    ///   "Time":  "candle_time",
    ///   "Open":  "candle_open",
    ///   "High":  "candle_high",
    ///   "Low":   "candle_low",
    ///   "Close": "candle_close",
    ///   "YRangePort": "candle_yrange"
    /// }
    /// </code>
    /// <para>
    /// <see cref="OnCompose"/> 时把 5 个扁平端口重组成 <see cref="CandlePorts"/> 通过反射写回 base.Ports,
    /// CandleFeature 的渲染逻辑零改动。
    /// </para>
    /// </summary>
    public class CandleBlueprintFeature : CandleFeature
    {
        public DataPort<ReadOnlyMemory<DateTime>> Time  { get; init; } = null!;
        public DataPort<ReadOnlyMemory<double>>   Open  { get; init; } = null!;
        public DataPort<ReadOnlyMemory<double>>   High  { get; init; } = null!;
        public DataPort<ReadOnlyMemory<double>>   Low   { get; init; } = null!;
        public DataPort<ReadOnlyMemory<double>>   Close { get; init; } = null!;

        // 反射缓存:Ports 是 CandleFeature 上的 init 属性,SetValue 穿透 init 修饰符
        // (.NET 5+ 反射 SetValue 不强制 init,跟 SmartActivator 写 init port 同理)。
        private static readonly System.Reflection.PropertyInfo _portsProperty =
            typeof(CandleFeature).GetProperty(nameof(Ports))!;

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            // 蓝图反射注入完 5 个扁平端口后,把它们打包成 CandlePorts 写回 base.Ports;
            // CandleFeature.OnCompose 里读 Ports 用于 OnProject 时的列消费。
            if (Ports == null!)
            {
                _portsProperty.SetValue(this, new CandlePorts(Time, Open, High, Low, Close));
            }
            base.OnCompose(chart, ctx, flow);
        }
    }

    // ⚠️ AxisLowCodeFeature 已删除 ——
    //    AxisFeature 自身已经支持公共无参 ctor + Format / Name / TickProvider / Placement init 属性,
    //    蓝图直接用框架原 AxisFeature 即可(Y 轴的典型场景)。
    //    但 X 轴的 tick 计算 (DomainTickProvider<TX> / ScheduleTickProvider) 需要"运行时上下文":
    //      - DomainTickProvider<TX>:吃 DataPort<ReadOnlyMemory<TX>> + ViewportPorts —— 都是端口/对象引用
    //      - ScheduleTickProvider:吃 Func<int> + Func<int, DateTime> 两个闭包 —— 业务侧才有
    //    这两类在蓝图 JSON 里表达不出来,需要 wrapper:把"运行时上下文"以 init 属性 / 协议 §K 的
    //    Delegate handler 引用形式暴露出来,OnCompose 物化时反射写回 base.TickProvider。

    /// <summary>
    /// X 轴 schedule-driven 版蓝图友好 wrapper —— 跟 <see cref="Hevo.Charting.FeatureCanvasExtensions.AddScheduleDomainAxis"/>
    /// (扩展方法 DSL) 等价。tick 位置和 label 都从 <see cref="LogicalLengthProvider"/> + <see cref="IndexToTimeProvider"/>
    /// 推导,跟实际数据数组解耦 —— 适合分时图 / 强弱评级这类"数据只到达一半也要画完整时段 tick"的业务。
    /// <para>
    /// 蓝图侧:
    /// </para>
    /// <code>
    /// new FeatureModel
    /// {
    ///   TypeName = "ScheduleDomainAxisFeature",
    ///   Properties = {
    ///     ["Mapping"] = "Domain", ["IsShared"] = true,
    ///     ["LogicalLengthProvider"] = "ts_logical_length",   // §K handler 引用
    ///     ["IndexToTimeProvider"]   = "ts_index_to_time",
    ///     ["ScheduleLabelFormat"]   = "HH:mm",
    ///     ["AxisStyle"] = AxisStyleTrait.Create(AxisPlacement.Bottom, ...),
    ///     ["GridStyle"] = GridStyleTrait.Create(GridOrientation.Vertical, ...),
    ///   },
    ///   PortBindings = { ["RangePort"] = "VP_ActiveRange" },
    /// };
    /// </code>
    /// </summary>
    public class ScheduleDomainAxisFeature : AxisFeature
    {
        /// <summary>
        /// §K Delegate handler 引用 —— 业务侧用 <c>[BlueprintHandler("ts_logical_length")] public int GetLogicalLength()</c>
        /// 暴露,Func&lt;int&gt; 由 BlueprintHandlerRegistry 反射推断生成。空时降级到 base.TickProvider 配置。
        /// </summary>
        public Func<int>? LogicalLengthProvider { get; init; }

        /// <summary>
        /// §K Delegate handler 引用 —— 业务侧用 <c>[BlueprintHandler("ts_index_to_time")] public DateTime IndexToTime(int idx)</c>
        /// 暴露。空时降级到 base.TickProvider 配置。
        /// </summary>
        public Func<int, DateTime>? IndexToTimeProvider { get; init; }

        /// <summary>label 格式串 (传给 ScheduleTickProvider.Format,默认 "HH:mm")。</summary>
        public string ScheduleLabelFormat { get; init; } = "HH:mm";

        // 反射写穿 init-only TickProvider — 跟 CandleBlueprintFeature 写 Ports 同 pattern。
        private static readonly PropertyInfo _tickProviderProperty =
            typeof(AxisFeature).GetProperty(nameof(TickProvider))!;

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            // 蓝图侧没显式给 TickProvider 时,凭两个 handler 拼一个 ScheduleTickProvider。
            // 显式 TickProvider 已注入(高级业务自定义)就不覆盖。两个 handler 任一缺失静默回落到
            // base 默认的 NumericTickProvider —— 跟 AxisFeature.TickProvider==null 路径一致,
            // 不抛异常,DryRun 阶段已经报过 BP_HANDLER_NOT_REGISTERED warning。
            if (TickProvider == null && LogicalLengthProvider != null && IndexToTimeProvider != null)
            {
                var sp = new ScheduleTickProvider(LogicalLengthProvider, IndexToTimeProvider, ScheduleLabelFormat);
                _tickProviderProperty.SetValue(this, sp);
            }
            base.OnCompose(chart, ctx, flow);
        }
    }

    /// <summary>
    /// X 轴 data-driven 版蓝图友好 wrapper(closed over DateTime)—— 跟
    /// <see cref="Hevo.Charting.FeatureCanvasExtensions.AddDomainAxis"/> (扩展方法 DSL) 等价。
    /// tick 位置和 label 从实际数据 <see cref="DomainDataPort"/> 取(K 线典型场景:索引 → 真实数据时间)。
    /// <para>
    /// <b>跟 <see cref="ScheduleDomainAxisFeature"/> 的对比</b>:
    /// 本 wrapper 不需要 handler,只要把 <c>candle_time</c> 端口接进 <see cref="DomainDataPort"/> 即可,
    /// ViewportPorts 通过 ChartFeature.Viewport 自动注入。
    /// </para>
    /// </summary>
    public class DomainAxisDateTimeFeature : AxisFeature
    {
        /// <summary>X 轴的原始时间数据列(典型 K 线 candle_time 端口)。</summary>
        public DataPort<ReadOnlyMemory<DateTime>> DomainDataPort { get; init; } = null!;

        /// <summary>label 格式串 (传给 DomainTickProvider.Format,默认 "yy-MM-dd HH:mm")。</summary>
        public string DomainLabelFormat { get; init; } = "yy-MM-dd HH:mm";

        private static readonly PropertyInfo _tickProviderProperty =
            typeof(AxisFeature).GetProperty(nameof(TickProvider))!;

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            // ChartFeature.Viewport 由 ReactiveSchema.Add 自动填充,装配阶段已就绪。
            // DomainTickProvider 无 strategyFactory 时内部默认走 BeautyTickStrategy。
            if (TickProvider == null && DomainDataPort != null! && this.Viewport != null)
            {
                var dtp = new DomainTickProvider<DateTime>(DomainDataPort, this.Viewport, DomainLabelFormat);
                _tickProviderProperty.SetValue(this, dtp);
            }
            base.OnCompose(chart, ctx, flow);
        }
    }

    // ⚠️ AutoScaleLowCodeFeature 已删除 ——
    //    DynamicChartSchema.DefineFeatures 现在直接支持 DataPort<T>[] 数组属性的 CSV 多绑,
    //    UniversalAutoScaleFeature.ValuePorts 在 GraphViewer 里就是一根标了 [] 的扇入端口,
    //    可同时接受多根连线。代价 = ChartBlueprint.cs 里 ~30 行通用解析逻辑,
    //    收益 = 任何 DataPort<T>[] 属性免 wrapper、自动可视化连线。

    // 💡 Link 系列 wrappers (跟传统 .Inject / .ForwardTo / .ProjectExtent / .LinkStream DSL 一一对应)
    //    单独搬到 LinkWrappers.cs,在画布上以专门的"桥接 feature"形式呈现,等价 DSL 一行 = 蓝图一根连线。

    // ⚠️ GridLayoutLowCode / PlotAreaDecorLowCode 已删除 ——
    //    编辑器 (NodeEditorWindow) 现在直接认 ChartLength / IHevoBrush 类型,
    //    不需要为这种"纯 shape massage"的 feature 写 wrapper。
}
