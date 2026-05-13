using System;
using System.Windows.Media;
using Hevo.Charting;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;

namespace Hevo.Drawing.LowCodeDemo.CodedKLine
{
    /// <summary>
    /// §对照实现 手写主图 schema —— K 线主图,跟
    /// <c>Assets/default_kline_dashboard.json</c> 的 <c>main</c> cell 同等表达力,但
    /// <b>纯 C# 直写</b>(继承 <see cref="ChartReactiveSchema"/>),不走蓝图 JSON / NodeFactory 反射路径,
    /// 作为低代码协议 vs. 手写 schema 的并排对照。
    ///
    /// <para><b>装配要点</b>:</para>
    /// <list type="number">
    ///   <item><b>DataSource</b>:<see cref="MockKLineDataSource"/> 进程级共享 timeline,<c>this.Own(...)</c> 托管;</item>
    ///   <item><b>Pipeline</b>:<c>_ds.Pipe().LinkStream(cfg => cfg.AutoMap(ports))</c> 6 个字段散射 + <c>.Compute(...)</c>
    ///         算 SMA(20) overlay 写 <c>_smaPort</c>;</item>
    ///   <item><b>Seed</b>:<see cref="ScaleStrategyTrait.CandleMode"/> —— <b>不能省</b>,对应 JSON 的
    ///         <c>"InitialTraits": [{ "TraitTypeName": "ScaleStrategyTrait", "Preset": "CandleMode" }]</c>;</item>
    ///   <item><b>Features</b>:Environment + Axes + Series(蜡烛 + SMA 折线)+ Interactions 全套。</item>
    /// </list>
    ///
    /// <para><b>⚠ scale 丢了 = 忘 Seed ScaleStrategyTrait 的典型症状</b></para>
    /// <para>
    /// 详见 <c>ChartBlueprint.cs:880-887</c> 的注释:<see cref="ScaleStrategyTrait"/> 没 seed 时,
    /// <c>ctx.Shared().Read&lt;ScaleStrategyTrait&gt;()</c> 返 null → AxisFeature / Layer 拿不到 IScale →
    /// 物理坐标投影直接退帧 → 黑屏 / scale 全错。框架对 GridLayout 兜底了默认实例,但 ScaleStrategyTrait
    /// 目前没有 framework-level 默认 seed,业务 schema 必须显式 <see cref="IFeatureContext.Seed{T}"/>。
    /// </para>
    /// </summary>
    public sealed class KLineMainSchema : ChartReactiveSchema, IDisposable
    {
        // ── 端口 ──────────────────────────────────────────────────────────────
        // 显式传 prefix 让 port id 形如 "M_Time" / "M_Open",跨 schema 区分;
        // 顺便绕开 "[CallerMemberName] 在 field init 里 resolve 成 ".ctor" 起脏 id" 的问题。
        // PortGenerator 已把生成的 PortGroup 改成 sealed class(C# struct 0-arg ctor 暗坑根治),
        // 这里就算写 new() 也能拿到正常初始化的 port 字段,但显式 prefix 让 dump 出来的端口名清楚。
        private readonly MockKLinePorts Ports = new("M_");
        // SMA(20) overlay 输出端口 —— Compute ingestor 算完写它,LineSeriesFeature 读它。
        private readonly DataPort<ReadOnlyMemory<double>> _smaPort   = new("M_SMA20");
        // Y 轴极值端口 —— UniversalAutoScale 综合 high/low/sma 算出来。
        private readonly DataPort<RealRange>             _yRangePort = new("M_YRange");

        // §DS 共享(canonical 同款)—— DataSource 由 View 持有 + 跨多个 schema 共享一份实例,
        // 所有 cell 的 pipe 订阅同一根 _trigger,View 调一次 SwitchContext 把所有 schema 一起喂上数据。
        // 不再 this.Own —— DS 生命周期归 View(类似 KLineIndicatorView),schema 只是消费者。
        private readonly MockKLineDataSource _ds;

        public KLineMainSchema(MockKLineDataSource dataSource)
        {
            _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        public void Dispose() { /* DS 不由本 schema 持有,无需在这里释放 */ }

        // canonical 同款:tab 切回时 schema.Resume 触达本钩子。让外部数据源把当前 snapshot 重推一次,
        // 本 cell 的 pipe 收到后写 port → Layer 重绘。不动数据源 IsActive,共享同 DS 的其它 schema
        // 不会被打扰。第一次进 tab IsActive=true,Resume 短路 → OnResume 不调,无副作用。
        protected override void OnResume() => _ds.RepublishLatest();

        // ── 数据流 ────────────────────────────────────────────────────────────
        protected override void DefineDataFlow(ChartCell chart)
        {
            _ds.Pipe()
                // ① 6 字段 → 6 端口(Time/Open/High/Low/Close/Volume)。
                .LinkStream(cfg => cfg.AutoMap(Ports))
                // ② Compute 节点 —— 数据帧内同步算 SMA(20),写 _smaPort。闭包必须 static(0-GC)。
                .Compute(
                    state: new SmaState(length: 20, smaPort: _smaPort, ds: _ds),
                    computeAction: static (board, source, st) => st.Recompute(board))
                // ③ ProjectExtent + BindTo:DS.LogicalLength → vp.LogicalLength,管线接入 chart。
                //    跟 canonical KLineSchema 同款:viewport 通过 ChartCell attached prop 拿,框架自动跟
                //    SchemaContext 装饰阶段 mutate 出来的 SharedViewport 对齐。
                .ProjectExtent(ViewportPorts.RequireAttached(Chart))
                .BindTo(chart);
        }

        // ── 视觉与交互 ────────────────────────────────────────────────────────
        protected override void DefineFeatures(IFeatureContext canvas)
        {
            // §核心 ⚠ Seed ScaleStrategyTrait —— 跟 JSON 蓝图的 InitialTraits 一一对应。
            // 没这行所有 AxisFeature / CandleLayer / LineLayer 都拿不到 DomainScale / ValueScale,
            // 物理坐标投影函数 (Project / Denormalize) 直接返回默认,scale 看起来"丢了"。
            // 这是手写 schema 跟 JSON 协议最容易踩的对照差异点 —— JSON 用户绝不会忘(蓝图 InitialTraits 是字面写出来的),
            // C# 用户极易忘(没编译期强制提示)。
            canvas.Seed<ScaleStrategyTrait>(ScaleStrategyTrait.CandleMode);

            var hitPort = HitPort;
            var timeMeta  = FieldMeta.Literal("时间",  Colors.White,                     "yyyy-MM-dd HH:mm");
            var priceMeta = FieldMeta.Literal("价",    Colors.LightGray,                 "F2");
            var smaMeta   = FieldMeta.Literal("SMA20", Color.FromRgb(0xFF, 0xB7, 0x4D),  "F2");
            var candleMetas = new CandleMetas(
                Open:  FieldMeta.Literal("开", Colors.Gray, "F2"),
                High:  FieldMeta.Literal("高", Colors.Gray, "F2"),
                Low:   FieldMeta.Literal("低", Colors.Gray, "F2"),
                Close: FieldMeta.Literal("收", Colors.Gray, "F2"));

            canvas
                .Environment(env => env
                    .SetupLayout(
                        left:   ChartLength.Pixel(60),
                        top:    ChartLength.Pixel(28),  // 给联动 Header 留 28px
                        right:  ChartLength.Pixel(0),
                        bottom: ChartLength.Pixel(24))
                    .SetupViewport(
                        minVisibleCount:     10,
                        alignment:           ViewportAlignment.RightEdge,
                        defaultVisibleCount: 120,
                        overscrollMin:       OverscrollPolicy.Hard,
                        overscrollMax:       OverscrollPolicy.Hard)
                    .SetupAutoScale(
                        yRangePort: _yRangePort,
                        valuePorts: new[] { Ports.HighPort, Ports.LowPort, _smaPort },
                        paddingRatio: 0.05)
                    // 万能联动头 —— OHLC + SMA20 自动出现在顶部一行。
                    .SetupUniversalHeader(hitPort: hitPort))
                .Axes(axes => axes
                    .AddDomainAxis(Ports.TimePort, ViewportPorts.RequireAttached(Chart), timeMeta)
                    .AddRangeAxis(_yRangePort, priceMeta, AxisPlacement.Right))
                .Series(series => series
                    .AddCandle(
                        rangePort: _yRangePort,
                        ports: new CandlePorts(Ports.TimePort, Ports.OpenPort, Ports.HighPort, Ports.LowPort, Ports.ClosePort),
                        groupName: "MainCandle",
                        metas: candleMetas)
                    .AddLine(
                        dataPort:  _smaPort,
                        rangePort: _yRangePort,
                        meta:      smaMeta,
                        thickness: 1.5))
                .Interactions(i => i.EnableStandard(
                    domainDataPort: Ports.TimePort,
                    domainMeta:     timeMeta,
                    options: new InteractionOptions<DateTime>
                    {
                        HitPort                = hitPort,
                        Modes                  = ChartInteractionMode.All,
                        TooltipXMeta           = timeMeta,
                        RequireClickToActivate = true,
                        DismissOnEscape        = true,
                    }));
        }

        // ── SMA(20) 计算状态 ─────────────────────────────────────────────────
        // 单独抽 class 让 .Compute<TState> 接 state —— Compute 闭包要求 static,
        // 不能 capture this。把要用的引用全塞 state 里。
        private sealed class SmaState
        {
            private readonly int _length;
            private readonly DataPort<ReadOnlyMemory<double>> _smaPort;
            private readonly MockKLineDataSource _ds;
            private double[] _buffer = Array.Empty<double>();

            public SmaState(int length, DataPort<ReadOnlyMemory<double>> smaPort, MockKLineDataSource ds)
            {
                _length = length;
                _smaPort = smaPort;
                _ds = ds;
            }

            public void Recompute(DataBlackboard board)
            {
                // Compute ingestor 在 LinkStream 之后跑同一帧,直接读 DS readonly snapshot
                // 拿 OHLC 原始数组(跟 board 上 ClosePort 的 span 同源,零额外开销)。
                // O(N) 滑动 sum → SMA,前 _length-1 根 NaN,LineSeries 自动断点。
                var snap = _ds.GetSnapshot();
                int n = snap.Count;
                if (_buffer.Length < n) _buffer = new double[Math.Max(n, 64)];

                if (n < _length)
                {
                    for (int i = 0; i < n; i++) _buffer[i] = double.NaN;
                    board.WriteIfChanged(_smaPort, _buffer.AsMemory(0, n));
                    return;
                }

                var span = snap.AsSpan();
                double sum = 0.0;
                for (int i = 0; i < _length; i++) sum += span[i].Close;
                for (int i = 0; i < _length - 1; i++) _buffer[i] = double.NaN;
                _buffer[_length - 1] = sum / _length;
                for (int i = _length; i < n; i++)
                {
                    sum += span[i].Close - span[i - _length].Close;
                    _buffer[i] = sum / _length;
                }
                board.WriteIfChanged(_smaPort, _buffer.AsMemory(0, n));
            }
        }
    }
}
