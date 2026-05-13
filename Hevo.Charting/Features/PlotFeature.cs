using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// 一个 series 的渲染规格 —— <see cref="PlotFeature"/> 据此构造对应数量 / 类型的子 SeriesFeature。
    /// 典型来源:Python 端 <c>@indicator(series=[...])</c> 装饰器(经
    /// <see cref="IndicatorMetadataRegistry"/>),也可由 C# 直接构造数组喂蓝图 Properties。
    /// </summary>
    /// <param name="Name">序列名,跟 <see cref="PlotFeature.Indicator"/> 函数返回 dict 的 key 对齐。</param>
    /// <param name="Kind">渲染类型: <c>line</c> / <c>bar</c> / <c>scatter</c> / <c>arrow_markers</c>。</param>
    /// <param name="Color">画笔颜色(line/bar 用;scatter/arrow 单点 spec 自带 color,本字段忽略)。</param>
    /// <param name="Width">线宽(line)或柱体相对宽度(bar,典型 0.6)。scatter/arrow 忽略。</param>
    /// <param name="XDomain">scatter 显式 X 轴范围 —— null = inherit 主图 shared XAxisTrait;arrow_markers 永远 inherit,本字段忽略。</param>
    /// <param name="YDomain">scatter 显式 Y 轴范围 —— null = inherit;同上。</param>
    public sealed record PlotSeriesSpec(
        string Name,
        string Kind,
        Color Color,
        double Width,
        RealRange? XDomain = null,
        RealRange? YDomain = null);

    /// <summary>
    /// 全局 indicator metadata 表 —— <see cref="PlotFeature"/> 据此查 series specs。
    ///
    /// <para>
    /// <b>两种填充路径(都可用,业务侧自选)</b>:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>显式 Register</b>:C# 业务直接 <see cref="Register"/> 一组 <see cref="PlotSeriesSpec"/>(无 Python 路径 / 测试 / mock 场景)</item>
    ///   <item><b>Source 回调</b>:<see cref="UseSource"/> 注一个 lazy fetcher。
    ///   PythonNet 子项目在 <c>PythonNetRuntime.Initialize</c> 时把"从 Python @indicator 装饰器拉数据"作为
    ///   source 注进来,Get 命中缓存优先,miss 时调 source 一次再缓存。</item>
    /// </list>
    ///
    /// <para>主项目 <c>Hevo.Charting</c> 对 Python 零感知 —— 不开 PythonNet 子项目时 source 是 null,
    /// Get 走纯字典查找,业务依赖 Register 显式塞数据。</para>
    /// </summary>
    public static class IndicatorMetadataRegistry
    {
        private static readonly object _lock = new();
        private static readonly Dictionary<string, PlotSeriesSpec[]> _cache = new();
        private static Func<string, PlotSeriesSpec[]?>? _source;

        /// <summary>
        /// 注册 lazy fetcher。重复调用以最后一个为准,缓存清空。null 解除注册。
        /// PythonNetRuntime.Initialize 内部自动调,业务侧通常不用直接动。
        /// </summary>
        public static void UseSource(Func<string, PlotSeriesSpec[]?>? source)
        {
            lock (_lock)
            {
                _source = source;
                _cache.Clear();
            }
        }

        /// <summary>显式登记 specs。同名后注册覆盖前。</summary>
        public static void Register(string name, PlotSeriesSpec[] specs)
        {
            if (string.IsNullOrEmpty(name) || specs == null) return;
            lock (_lock) _cache[name] = specs;
        }

        /// <summary>
        /// 拉取 indicator <paramref name="name"/> 的 series 元数据。
        /// 缓存命中直接返;否则调 source(若注册过)拉一次缓存。找不到返回空数组(不返 null)。
        /// </summary>
        public static PlotSeriesSpec[] Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return Array.Empty<PlotSeriesSpec>();
            lock (_lock)
            {
                if (_cache.TryGetValue(name, out var hit)) return hit;
                var fetched = _source?.Invoke(name);
                if (fetched != null && fetched.Length > 0)
                {
                    _cache[name] = fetched;
                    return fetched;
                }
                return Array.Empty<PlotSeriesSpec>();
            }
        }

        /// <summary>清缓存(典型场景:Python 热重载后 indicator metadata 变化)。</summary>
        public static void Invalidate()
        {
            lock (_lock) _cache.Clear();
        }
    }

    /// <summary>
    /// 多 series 自渲染节点 —— 一个委托返回 dict,按 spec 配置展开成 N 个嵌套子 Feature
    /// (<see cref="LineSeriesFeature"/> / <see cref="BarSeriesFeature"/> / <see cref="ScatterPlotFeature"/> /
    /// <see cref="ArrowMarkerFeature"/>),自动获能 Tooltip / Crosshair / DynamicHeader 等下游协议。
    ///
    /// <para>
    /// <b>统一 series 协议</b>:scatter / arrow_markers 跟 line / bar 平级,都是 <see cref="PlotSeriesSpec"/> 数组里的一项。
    /// Python 端 indicator 函数返回 <c>dict[name → value]</c>,value 类型按 <see cref="PlotSeriesSpec.Kind"/> 决定:
    /// </para>
    /// <list type="bullet">
    ///   <item>line / bar:    <c>ROM&lt;double&gt;</c>(列式数值序列)</item>
    ///   <item>scatter:      <c>list[ScatterPoint]</c>(行式点 spec,每点独立 size/color/payload)</item>
    ///   <item>arrow_markers: <c>list[ArrowMarker]</c>(行式箭头 spec,K 线买点等场景)</item>
    /// </list>
    /// <para>
    /// 单一 spec 的旧 <c>@register_plot</c> 直接返 list(不裹 dict)兼容:Watch 回调判定 raw 形态,top-level list 时
    /// 自动包成 <c>{specs[0].Name: raw}</c> —— 让历史 Python 代码无须改。
    /// </para>
    ///
    /// <para>
    /// <b>典型用法(Pine 风味,Python @indicator 混搭 4 种 kind)</b>:
    /// </para>
    /// <code>
    /// @indicator('strat_overlay', overlay=True, series=[
    ///     ('close',  'line',          '#888', 1),
    ///     ('buys',   'arrow_markers', '#0F0', 8),
    ///     ('sells',  'arrow_markers', '#F00', 8),
    /// ])
    /// def strat(close):
    ///     return {
    ///         'close': close,
    ///         'buys':  [ArrowMarker(...) for b in find_buys(close)],
    ///         'sells': [ArrowMarker(...) for s in find_sells(close)],
    ///     }
    /// </code>
    ///
    /// <para>
    /// <b>对比 <see cref="ComputeFeature"/></b>:
    /// </para>
    /// <list type="bullet">
    ///   <item>ComputeFeature: 1 input → 1 output(纯计算节点,渲染依赖外部 LineSeries)</item>
    ///   <item>PlotFeature:    1 input → 内部 N 子 feature(自管渲染) + 通过 <see cref="SeriesOutputs"/> 字典对外 fan-out
    ///         每条 line/bar 子序列一根独立 bar-aligned 端口,接 <c>AutoScale.ValuePorts</c> / 其他指标输入都行</item>
    /// </list>
    /// </summary>
    public sealed class PlotFeature : ChartFeature
    {
        public override FeaturePhase Phase => FeaturePhase.Series;

        /// <summary>
        /// 单输入端口(典型:close 收盘价 / 任何 ROM&lt;double&gt; 主流)。
        /// 跟 <see cref="Inputs"/> 互斥:Inputs.Count > 0 时走多输入路径。
        /// </summary>
        public DataPort<ReadOnlyMemory<double>> InputPort { get; init; } = null!;

        /// <summary>
        /// §D2.X 多输入端口字典(典型 Stochastic 的 high/low/close)。
        /// PortBindings <c>"Inputs.{name}"</c> nested key 焊接到这里。
        /// </summary>
        public Dictionary<string, DataPort<ReadOnlyMemory<double>>> Inputs { get; init; } = new();

        /// <summary>§D2.X Indicator 委托的形参顺序。多输入路径必填。</summary>
        public string[] InputOrder { get; init; } = Array.Empty<string>();

        /// <summary>Y 轴量程 —— 跟 LineSeriesFeature / BarSeriesFeature 同样的协议。</summary>
        public DataPort<RealRange> YRangePort { get; init; } = null!;

        /// <summary>
        /// 每条 line/bar series 一根独立输出端口(<c>SeriesOutputs[spec.Name]</c>)。
        /// JSON 用 nested key 绑定 —— <c>"SeriesOutputs.upper": "s_BB_Upper"</c> —— 任意下游(<see cref="UniversalAutoScaleFeature.ValuePorts"/>
        /// fan-in 数组 / 另一个 <c>ComputeFeature.InputPort</c> / 链式指标等)拿到的都是 bar-aligned 长 N 的纯数据,
        /// 语义跟单一 <see cref="LineSeriesFeature.DataPort"/> 完全一致。
        /// <para>
        /// 旧 <c>MergedOutputPort</c>(端到端拼接 N 段成扁平 k·N buffer)已删除 —— 拼接 buffer 跟 AutoScale 的
        /// "按 viewport bar-index 切片" 假设冲突,只能扫到第一段(典型 bug:BB 下轨掉出画布)。
        /// </para>
        /// <para>
        /// scatter / arrow_markers / text_markers 不在本字典里 —— 它们的数据类型不是 <c>ROM&lt;double&gt;</c>,
        /// 且 scatter 走自己的 layer-local XAxis/YAxis,arrow/text 寄生主图 axes,不需要独立 Y 量程贡献。
        /// </para>
        /// <para>
        /// 没绑的 series 不影响内部渲染 —— OnCompose 会为其合成一根私有端口,child LineSeries/BarSeries 仍正常工作,
        /// 只是不向外暴露,下游无从订阅(典型坑见低代码.md §8 + DryRun 警告 <c>BP_OUTPUT_NOT_SCALED</c>)。
        /// </para>
        /// </summary>
        [PortDirection(PortDirection.Output)]
        public Dictionary<string, DataPort<ReadOnlyMemory<double>>> SeriesOutputs { get; init; } = new();

        /// <summary><c>@indicator</c> 装饰器声明的 indicator 名,用于查 series specs。</summary>
        public string IndicatorName { get; init; } = "";

        /// <summary>
        /// 计算委托(返回 dict {series_name → value};或单 spec 时直接返 list[dict] 兼容旧 @register_plot)。
        /// 蓝图加载阶段 ResolveHandlerReferences 把 string 翻成实际委托。
        /// </summary>
        public Delegate? Indicator { get; init; }

        /// <summary>
        /// Series specs。优先级:Properties 显式传 > <see cref="IndicatorMetadataRegistry"/> resolve。
        /// 用 Properties 传可以离线使用(metadata 拉不到时业务侧手填)。
        /// </summary>
        public PlotSeriesSpec[] Series { get; init; } = Array.Empty<PlotSeriesSpec>();

        /// <summary>
        /// scatter / arrow_markers 子 feature 的 hover 命中容差(像素²)。默认 16²。
        /// 透传到 <see cref="ScatterPlotFeature.HoverHitToleranceSq"/> / <see cref="ArrowMarkerFeature.HoverHitToleranceSq"/>。
        /// </summary>
        public double HoverHitToleranceSq { get; init; } = 16.0 * 16.0;

        /// <summary>
        /// 可选 hit-state 输出端口(scatter / arrow_markers child 的 hover 命中状态)。透传给 child Feature 的
        /// <see cref="ElementPlotFeatureBase{TSpec, TLayer}.HitStatePort"/>;<see cref="TooltipWidgetFeature{TX}"/> 接此端口画 tooltip。
        /// 限制:多个 element-mode spec 共享同一根端口,适合单 scatter / 单 arrow 主流场景。
        /// </summary>
        [PortDirection(PortDirection.Output)]
        public DataPort<PointerHitState?>? HitStatePort { get; init; }

        // ── 事件透传 ────────────────────────────────────────────
        // ScatterPlotFeature / ArrowMarkerFeature 等 child element-mode feature 的鼠标事件
        // bubble 上来,蓝图层的 Events 路由(URI 风味)能直接挂在 PlotFeature 节点上。
        // EventArgs 类型用 object —— child 可能是不同 TSpec(ScatterPoint / ArrowMarker)的
        // PlotElementEvent<TSpec>,装箱后 URI 模板反射 {Index} / {Element.X} 仍能工作(record struct
        // 字段反射可见)。
        public event EventHandler<object>? ElementDoubleClicked;
        public event EventHandler<object>? ElementHovered;

        // ── runtime 状态 ─────────────────────────────────────────
        private PlotSeriesSpec[] _resolvedSpecs = Array.Empty<PlotSeriesSpec>();

        // 子 feature 数组(LineSeriesFeature / BarSeriesFeature / ScatterPlotFeature / ArrowMarkerFeature 任意混搭)
        private ChartFeature[] _children = Array.Empty<ChartFeature>();

        // 每个 spec 配一根合成 DataPort,类型按 spec.Kind:
        //   line/bar       → DataPort<ROM<double>>
        //   scatter        → DataPort<ROM<ScatterPoint>>
        //   arrow_markers  → DataPort<ROM<ArrowMarker>>
        // 装到 object[] 里保留运行期类型,Watch 回调按 spec.Kind 强转写入。
        private object[] _childPorts = Array.Empty<object>();

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            // 1. 解析 specs:Series 显式 > metadata registry
            _resolvedSpecs = Series.Length > 0
                ? Series
                : IndicatorMetadataRegistry.Get(IndicatorName);
            if (_resolvedSpecs.Length == 0)
            {
                // 蓝图作者填了 IndicatorName 但元数据 registry 找不到(Python @indicator 没扫到 / 名字拼错)——
                // 跟 Indicator 委托缺失同源,但走 IndicatorMetadataRegistry 这条 fallback 路径。
                // 不 throw(可能是 IndicatorName/Series 都为空的占位 feature,正常),只 trace + 报。
                if (!string.IsNullOrEmpty(IndicatorName))
                {
                    System.Diagnostics.Trace.TraceWarning(
                        $"[PlotFeature] Indicator '{IndicatorName}' 找不到 series 元数据,跳过渲染。" +
                        "Series 字段没显式给,IndicatorMetadataRegistry 也没记 —— Python @indicator 元数据扫描漏了 / 名字拼错。");
#if DEBUG
                    var fid = $"F_{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)}";
                    Hevo.Charting.DevTools.TracerRegistry.Get(chart.Template)?.RecordKeyEvent(
                        Hevo.Charting.DevTools.TopologyTracer.EventKind.FeatureShortCircuit, fid,
                        $"PlotFeature: indicator '{IndicatorName}' series 元数据缺");
#endif
                }
                return;
            }

            // 蓝图作者指定了 IndicatorName(意图调 Python plot handler)但 Indicator 委托没解析上来 ——
            // 配置错误,fail fast。99% 是 ResolveHandlerReferences 阶段 handler 在 BlueprintHandlerRegistry
            // 查不到(Python init 失败 / handler 名拼错 / 业务侧没 RegisterPythonFunction)。
            // 静默早退会让 Watch 每帧空跑,用户开十次蓝图也不知道为啥不画 —— 直接抛,问题立刻摆到屏前。
            if (Indicator == null && !string.IsNullOrEmpty(IndicatorName))
            {
                throw new InvalidOperationException(
                    $"PlotFeature.Indicator 委托未解析(IndicatorName='{IndicatorName}')。" +
                    "蓝图引用了 plot handler 但 BlueprintHandlerRegistry 里查不到。排查:" +
                    "1) 上游 ResolveHandlerReferences 是否报过 'handler 未注册' / 类型不兼容警告;" +
                    "2) 业务侧 RegisterPythonFunction / RegisterDelegate 是否成功(看 host init log);" +
                    "3) IndicatorName 是否拼写正确。");
            }

            // 2. 给每个 spec 配一根合成端口 + 一个零改动的 series feature 实例。
            _children = new ChartFeature[_resolvedSpecs.Length];
            _childPorts = new object[_resolvedSpecs.Length];

            for (int i = 0; i < _resolvedSpecs.Length; i++)
            {
                var spec = _resolvedSpecs[i];
                var meta = FieldMeta.Literal(spec.Name, new HevoSolidBrush(spec.Color), "G");

                // 给每根 child port 显式起名 = "Plot.{IndicatorName}.{spec.Name}"。
                // 否则 DataPort<T> ctor 走 [CallerMemberName] 兜底,DisplayName 落成方法名 "OnCompose",
                // Inspector 拓扑画布的引脚列就会冒出 N 个名叫 OnCompose 的端口节点(每个 spec 一根)。
                string portName = $"Plot.{IndicatorName}.{spec.Name}";

                ChartFeature child;
                object port;
                switch ((spec.Kind ?? "line").Trim().ToLowerInvariant())
                {
                    case "bar":
                    {
                        // line/bar:JSON 在 SeriesOutputs[spec.Name] 焊接过就用绑定端口,下游可直接订阅;
                        // 没绑就合成一根私有端口,渲染照常,只是不向外暴露。
                        var p = ResolveLineBarPort(spec.Name, portName);
                        port = p;
                        child = new BarSeriesFeature
                        {
                            DataPort = p,
                            YRangePort = this.YRangePort,
                            Meta = meta,
                            BarWidthRatio = spec.Width,
                        };
                        break;
                    }
                    case "scatter":
                    {
                        var p = new DataPort<ReadOnlyMemory<ScatterPoint>>(portName);
                        port = p;
                        child = new ScatterPlotFeature
                        {
                            Spec = p,
                            Name = spec.Name,
                            LayerName = $"Plot.{IndicatorName}.{spec.Name}",
                            XDomain = spec.XDomain,
                            YDomain = spec.YDomain,
                            HoverHitToleranceSq = HoverHitToleranceSq,
                            HitStatePort = this.HitStatePort,   // 透传 → child hover 写本端口
                        };
                        break;
                    }
                    case "arrow_markers":
                    {
                        var p = new DataPort<ReadOnlyMemory<ArrowMarker>>(portName);
                        port = p;
                        child = new ArrowMarkerFeature
                        {
                            Spec = p,
                            Name = spec.Name,
                            LayerName = $"Plot.{IndicatorName}.{spec.Name}",
                            HoverHitToleranceSq = HoverHitToleranceSq,
                            HitStatePort = this.HitStatePort,   // 透传 → child hover 写本端口
                            YRangePort = this.YRangePort,       // 透传 → child publish 到 self layer-local YAxisTrait
                        };
                        break;
                    }
                    case "text_markers":
                    {
                        var p = new DataPort<ReadOnlyMemory<TextMarker>>(portName);
                        port = p;
                        child = new TextMarkerFeature
                        {
                            Spec = p,
                            Name = spec.Name,
                            LayerName = $"Plot.{IndicatorName}.{spec.Name}",
                            HoverHitToleranceSq = HoverHitToleranceSq,
                            HitStatePort = this.HitStatePort,
                            YRangePort = this.YRangePort,
                        };
                        break;
                    }
                    case "line":
                    default:
                    {
                        var p = ResolveLineBarPort(spec.Name, portName);
                        port = p;
                        child = new LineSeriesFeature
                        {
                            DataPort = p,
                            YRangePort = this.YRangePort,
                            LayerName = $"Plot.{IndicatorName}.{spec.Name}",
                            Meta = meta,
                            Style = LineStyle.Create(meta.GetDefaultBrush(), spec.Width, useSpline: false),
                        };
                        break;
                    }
                }

                _childPorts[i] = port;
                _children[i] = child;
            }

            for (int i = 0; i < _children.Length; i++)
            {
                this.AddChildFeatureDuringCompose(_children[i], chart, ctx, flow);
            }

            // 事件透传:子 element-mode feature(ScatterPlotFeature / ArrowMarkerFeature)的
            // ElementDoubleClicked / ElementHovered 反射挂 forward,bubble 到本 PlotFeature 上,
            // 蓝图 Events 路由能用 PlotFeature 节点 → URI dispatch。
            for (int i = 0; i < _children.Length; i++)
            {
                ForwardChildElementEvent(_children[i], nameof(ElementDoubleClicked), raw => ElementDoubleClicked?.Invoke(this, raw));
                ForwardChildElementEvent(_children[i], nameof(ElementHovered),       raw => ElementHovered?.Invoke(this, raw));
            }

            RegisterDisposable(new PlotChildLifetime(_children, chart, ctx, this.RemoveChildFeature));

            // 3. Watch input port(s):每帧调 Indicator,按 spec.Kind 拆 dict 到对应类型的 child 端口。
            //    多输入(§D2.X)走 Inputs+InputOrder + DynamicInvoke;单输入走 InputPort 强类型直调。
            DataPort<ReadOnlyMemory<double>>[]? orderedPorts = null;
            object[] watchKeys;
            if (Inputs.Count > 0)
            {
                if (InputOrder == null || InputOrder.Length == 0)
                {
                    Console.WriteLine("[PlotFeature] Inputs 字典非空但 InputOrder 未声明,跳过装配。");
                    return;
                }
                orderedPorts = new DataPort<ReadOnlyMemory<double>>[InputOrder.Length];
                for (int i = 0; i < InputOrder.Length; i++)
                {
                    if (!Inputs.TryGetValue(InputOrder[i], out var p) || p == null)
                    {
                        Console.WriteLine($"[PlotFeature] InputOrder['{InputOrder[i]}'] 未在 Inputs 字典中焊接,跳过装配。");
                        return;
                    }
                    orderedPorts[i] = p;
                }
                watchKeys = new object[orderedPorts.Length];
                for (int i = 0; i < orderedPorts.Length; i++) watchKeys[i] = orderedPorts[i];
            }
            else
            {
                if (InputPort == null) return;
                watchKeys = new object[] { InputPort };
            }

            // 协议规范 §B (00.Hevo.Charting 架构协议) 要求:Indicator 调用属于"耗时指标计算",
            // 必须走 WatchAsync 后台线程池 + Compute-then-Commit 三段式锁:
            //   ① 瞬间读锁捞 inputs → ② 锁外裸奔算指标 → ③ 瞬间写锁提交 outputs
            // 之前用 flow.Watch 整段持 UpgradeableReadLock 跑 Indicator.DynamicInvoke,
            // 5000 点 scatter / Python handler 会把 board write lock 锁 100-300ms,
            // WPF 60Hz Render tick 跟其他 reader 全卡死。
            //
            // 注意:argsBuf 从 lambda 外的共享改成 lambda 内 local —— WatchAsync 后台多 task 可能并发,
            // 共享 buffer 会撞写。每帧 new 一个小 object[] 开销可忽略。
            flow.WatchAsync(watchKeys, board =>
            {
                if (Indicator == null) return;

                // ① 读锁捞 inputs (拷贝出 ReadOnlyMemory<double> 句柄,锁外可继续读底层 array)
                object? raw;
                try
                {
                    if (orderedPorts != null)
                    {
                        var argsBuf = new object?[orderedPorts.Length];
                        using (board.AcquireReadLock())
                        {
                            for (int i = 0; i < orderedPorts.Length; i++)
                            {
                                var v = board.Read(orderedPorts[i]);
                                if (v.Length == 0) return;
                                argsBuf[i] = v;
                            }
                        }
                        // ② 锁外算 Indicator (Python handler 100-300ms 也不锁 board)
                        raw = Indicator is Func<object?[], object?> typedMulti
                            ? typedMulti(argsBuf)
                            : Indicator.DynamicInvoke(argsBuf);
                    }
                    else
                    {
                        ReadOnlyMemory<double> input;
                        using (board.AcquireReadLock())
                        {
                            input = board.Read(InputPort);
                            if (input.Length == 0) return;
                        }
                        raw = Indicator is Func<ReadOnlyMemory<double>, object?> typed ? typed(input) : Indicator.DynamicInvoke(input);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PlotFeature] handler 调用异常: {(ex.InnerException ?? ex).Message}");
                    return;
                }
                if (raw == null) return;

                // 解包 handler 输出 —— dict / list[dict] 兼容路径,纯 CPU 内存操作,锁外做。
                IDictionary<string, object?>? dict = raw as IDictionary<string, object?>;
                if (dict == null && raw is object?[] arr && _resolvedSpecs.Length == 1)
                {
                    dict = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [_resolvedSpecs[0].Name] = arr,
                    };
                }
                if (dict == null) return;

                // ③ 写锁提交 outputs (μs 级,不影响 UI 渲染)
                //    每条 line/bar series 各自写到自己的 _childPorts[i] —— 如果 JSON 焊到 SeriesOutputs.{name},
                //    这里写的就是绑定端口本身,下游(AutoScale.ValuePorts / 其他 ComputeFeature 等)直接订阅生效。
                using (board.AcquireWriteLock())
                {
                    for (int i = 0; i < _resolvedSpecs.Length; i++)
                    {
                        if (!dict.TryGetValue(_resolvedSpecs[i].Name, out var value) || value == null) continue;
                        WriteValueByKind(board, _childPorts[i], _resolvedSpecs[i].Kind, value);
                    }
                }
            });
        }

        protected override void OnProject(FeatureContext ctx)
        {
            // 故意为空 —— 完全非蓝图模式 / first-class schema 子节点架构下:
            // 数据流:Watch 回调写 _childPorts → board.WriteIfChanged 触发 NotifyPortUpdated
            //         → registry 把订阅过这些 port 的子 feature 标脏 → schema.ProjectAll 自动 project 它们
            // YRange / Viewport 变也走同一通路(子 feature 的 UsePort 直接登记到 schema registry)。
        }

        // 反射给 child 挂事件 forward delegate。child 事件类型形如 EventHandler<PlotElementEvent<TSpec>>,
        // 用 Expression.Lambda 编一个匹配签名的 delegate,内部把 args 装箱后调 forwarder。
        // PlotElementEvent<TSpec> 是 record struct,装箱后反射 Index / Element 字段仍可见,URI 模板 {Index} 可用。
        private static void ForwardChildElementEvent(object child, string eventName, Action<object> forwarder)
        {
            var evtInfo = child.GetType().GetEvent(eventName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (evtInfo == null) return;
            var handlerType = evtInfo.EventHandlerType;
            if (handlerType == null) return;

            var invoke = handlerType.GetMethod("Invoke")!;
            var pars = invoke.GetParameters();
            var paramExprs = new System.Linq.Expressions.ParameterExpression[pars.Length];
            for (int i = 0; i < pars.Length; i++)
                paramExprs[i] = System.Linq.Expressions.Expression.Parameter(pars[i].ParameterType, pars[i].Name);

            int argsIndex = pars.Length >= 2 ? 1 : pars.Length - 1;
            var argsExpr = System.Linq.Expressions.Expression.Convert(paramExprs[argsIndex], typeof(object));

            var fwdInvoke = typeof(Action<object>).GetMethod("Invoke")!;
            var body = System.Linq.Expressions.Expression.Call(
                System.Linq.Expressions.Expression.Constant(forwarder),
                fwdInvoke,
                argsExpr);

            var del = System.Linq.Expressions.Expression.Lambda(handlerType, body, paramExprs).Compile();
            evtInfo.AddEventHandler(child, del);
        }

        // ── 按 spec.Kind 写值到对应类型 port ─────────────────────────────────
        // line/bar:port = DataPort<ROM<double>>
        // scatter: port = DataPort<ROM<ScatterPoint>>,value 期望 object?[](Python list of dict)
        // arrow:   port = DataPort<ROM<ArrowMarker>>
        private static void WriteValueByKind(DataBlackboard board, object port, string kind, object value)
        {
            switch ((kind ?? "line").Trim().ToLowerInvariant())
            {
                case "bar":
                case "line":
                {
                    if (port is not DataPort<ReadOnlyMemory<double>> p) return;
                    ReadOnlyMemory<double> rom = value switch
                    {
                        ReadOnlyMemory<double> m => m,
                        double[] a              => a,
                        _                       => ReadOnlyMemory<double>.Empty,
                    };
                    if (rom.Length > 0) board.WriteIfChanged(p, rom);
                    break;
                }
                case "scatter":
                {
                    if (port is not DataPort<ReadOnlyMemory<ScatterPoint>> p) return;
                    var spec = ParseScatterSpec(value);
                    board.WriteIfChanged(p, spec);
                    break;
                }
                case "arrow_markers":
                {
                    if (port is not DataPort<ReadOnlyMemory<ArrowMarker>> p) return;
                    var spec = ParseArrowSpec(value);
                    board.WriteIfChanged(p, spec);
                    break;
                }
                case "text_markers":
                {
                    if (port is not DataPort<ReadOnlyMemory<TextMarker>> p) return;
                    var spec = ParseTextSpec(value);
                    board.WriteIfChanged(p, spec);
                    break;
                }
            }
        }

        // line/bar 端口解析:JSON 在 SeriesOutputs[seriesName] 焊接过就用绑定端口,Watch 写到这根 port
        // 立即触发 AutoScale.ValuePorts / 任何下游订阅;没绑就合成私有端口,渲染照常,不向外暴露。
        private DataPort<ReadOnlyMemory<double>> ResolveLineBarPort(string seriesName, string fallbackName)
        {
            if (SeriesOutputs != null && SeriesOutputs.TryGetValue(seriesName, out var bound) && bound != null)
                return bound;
            return new DataPort<ReadOnlyMemory<double>>(fallbackName);
        }

        // ── element spec 解析 ──────────────────────────────────────────────────────
        // 期望 raw = object?[](Python list 经 UnboxBestEffort 后的形态;C# 调用方塞 object?[] 也走这路径),
        // 元素 = IDictionary<string, object?>(Python dict / C# 直接构造均可)。

        internal static ReadOnlyMemory<ScatterPoint> ParseScatterSpec(object raw)
        {
            // 路径 ①:SoA(Struct of Arrays)—— Python 端推荐输出形态。
            //   { 'x': ndarray<float64>, 'y': ndarray<float64>, 'size': ndarray<float64>,
            //     'color_argb': ndarray<int32> packed 0xAARRGGBB(优先) | 'color': list<str>(向后兼容) }
            //
            // 关键:color_argb 走 ndarray int32 zero-copy,**完全消灭 5000 string × 3 次 InvalidCastException 噪声**。
            // 老 'color' list<str> 路径保留兼容,但每个 string 走 UnboxBestEffort try-cast 链(慢 + first-chance 刷屏),
            // 新 Python handler 推荐用 color_argb。
            if (raw is IDictionary<string, object?> soa
                && soa.TryGetValue("x", out var xRaw)
                && soa.TryGetValue("y", out var yRaw))
            {
                var xs = AsDoubleSpan(xRaw);
                var ys = AsDoubleSpan(yRaw);
                int n = Math.Min(xs.Length, ys.Length);
                if (n == 0) return ReadOnlyMemory<ScatterPoint>.Empty;

                ReadOnlySpan<double> sizes = soa.TryGetValue("size", out var szRaw) ? AsDoubleSpan(szRaw) : default;

                // 优先 packed ARGB int32 数组(zero-copy + 零 first-chance);找不到 fall back 到老 string list。
                ReadOnlySpan<int> colorArgb = soa.TryGetValue("color_argb", out var caRaw) ? AsIntSpan(caRaw) : default;
                object?[]? colorStrs = colorArgb.Length == 0 && soa.TryGetValue("color", out var clRaw) ? clRaw as object?[] : null;

                var output = new ScatterPoint[n];
                for (int i = 0; i < n; i++)
                {
                    float sz = i < sizes.Length ? (float)sizes[i] : 4.0f;
                    string cl;
                    if (i < colorArgb.Length)
                    {
                        // packed 0xAARRGGBB → "#AARRGGBB" — C# 端 8 字符 hex,直接 ToString("X8") 简洁。
                        cl = "#" + colorArgb[i].ToString("X8");
                    }
                    else
                    {
                        cl = (colorStrs != null && i < colorStrs.Length && colorStrs[i] is string s) ? s : "#FFFFFFFF";
                    }
                    output[i] = new ScatterPoint((float)xs[i], (float)ys[i], sz, cl);
                }
                return output;
            }

            // 路径 ②:AoS(Array of Structs)—— 旧 Python 端形态 list[dict],保留向后兼容。
            //   marshal 慢(5000 dict × 4 key 解包),新代码请改 SoA。
            if (raw is not object?[] arr) return ReadOnlyMemory<ScatterPoint>.Empty;
            if (arr.Length == 0) return ReadOnlyMemory<ScatterPoint>.Empty;

            var aosOutput = new ScatterPoint[arr.Length];
            int written = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] is not IDictionary<string, object?> d) continue;
                var x     = (float)ReadDouble(d, "x");
                var y     = (float)ReadDouble(d, "y");
                var size  = (float)ReadDouble(d, "size", 4.0);
                var color = ReadString(d, "color", "#FFFFFFFF");
                aosOutput[written++] = new ScatterPoint(x, y, size, color);
            }
            if (written == arr.Length) return aosOutput;
            var slim = new ScatterPoint[written];
            Array.Copy(aosOutput, slim, written);
            return slim;
        }

        // SoA 路径捞 double 列:接受 ReadOnlyMemory<double> / double[] / 其他 IEnumerable<double>。
        // Python.NET 把 numpy<float64> 解成 ReadOnlyMemory<double>,直接 .Span 拿连续内存。
        private static ReadOnlySpan<double> AsDoubleSpan(object? raw) => raw switch
        {
            ReadOnlyMemory<double> m => m.Span,
            double[] a               => a.AsSpan(),
            _                        => default,
        };

        // SoA 路径捞 int 列:numpy<int32> → ReadOnlyMemory<int> zero-copy。用于 color_argb packed ARGB。
        private static ReadOnlySpan<int> AsIntSpan(object? raw) => raw switch
        {
            ReadOnlyMemory<int> m => m.Span,
            int[] a               => a.AsSpan(),
            _                     => default,
        };

        internal static ReadOnlyMemory<ArrowMarker> ParseArrowSpec(object raw)
        {
            if (raw is not object?[] arr) return ReadOnlyMemory<ArrowMarker>.Empty;
            if (arr.Length == 0) return ReadOnlyMemory<ArrowMarker>.Empty;

            var output = new ArrowMarker[arr.Length];
            int written = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] is not IDictionary<string, object?> d) continue;
                var lx    = ReadDouble(d, "logical_x");
                var ly    = ReadDouble(d, "logical_y");
                var dir   = ReadString(d, "direction", "down");
                var color = ReadString(d, "color", "#4CAF50");
                var size  = (float)ReadDouble(d, "size", 8.0);
                output[written++] = new ArrowMarker(lx, ly, dir, color, size);
            }
            if (written == arr.Length) return output;
            var slim = new ArrowMarker[written];
            Array.Copy(output, slim, written);
            return slim;
        }

        internal static ReadOnlyMemory<TextMarker> ParseTextSpec(object raw)
        {
            if (raw is not object?[] arr) return ReadOnlyMemory<TextMarker>.Empty;
            if (arr.Length == 0) return ReadOnlyMemory<TextMarker>.Empty;

            var output = new TextMarker[arr.Length];
            int written = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] is not IDictionary<string, object?> d) continue;
                var lx     = ReadDouble(d, "logical_x");
                var ly     = ReadDouble(d, "logical_y");
                var text   = ReadString(d, "text",   "");
                var color  = ReadString(d, "color",  "#E0E6EC");
                var fsize  = (float)ReadDouble(d, "font_size", 11.0);
                var anchor = ReadString(d, "anchor", "above");
                output[written++] = new TextMarker(lx, ly, text, color, fsize, anchor);
            }
            if (written == arr.Length) return output;
            var slim = new TextMarker[written];
            Array.Copy(output, slim, written);
            return slim;
        }

        private static double ReadDouble(IDictionary<string, object?> d, string key, double fallback = 0.0)
        {
            if (!d.TryGetValue(key, out var v) || v == null) return fallback;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static string ReadString(IDictionary<string, object?> d, string key, string fallback)
        {
            if (!d.TryGetValue(key, out var v) || v == null) return fallback;
            return v as string ?? v.ToString() ?? fallback;
        }
    }

    /// <summary>
    /// PlotFeature 被单独移除(schema 还活着)的热插拔清理。把子 feature 也从 schema 摘掉。
    /// 全 schema teardown 场景不依赖本类 —— schema.Decompose 走 _orderedFeatures 反向遍历调
    /// 每个 feature 的 Decompose,子 feature 在 _orderedFeatures 里就会被自然清理。
    /// </summary>
    internal sealed class PlotChildLifetime : IDisposable
    {
        private readonly ChartFeature[] _children;
        private readonly ChartCell _chart;
        private readonly RenderContext _ctx;
        private readonly Action<ChartFeature> _unregister;
        private bool _disposed;

        public PlotChildLifetime(ChartFeature[] children, ChartCell chart, RenderContext ctx, Action<ChartFeature> unregister)
        {
            _children = children;
            _chart = chart;
            _ctx = ctx;
            _unregister = unregister;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (int i = 0; i < _children.Length; i++)
            {
                try
                {
                    _unregister(_children[i]);
                    _children[i].Decompose(_chart, _ctx);
                }
                catch
                {
                    // 子 cleanup 失败不应阻塞其他子的清理
                }
            }
        }
    }
}
