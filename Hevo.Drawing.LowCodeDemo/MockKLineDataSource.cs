using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Hevo.Charting;
using Hevo.Charting.LowCode;

namespace Hevo.Drawing.LowCodeDemo
{
    /// <summary>
    /// Mock OHLCV 数据点 —— 一个 K 线 bar(open/high/low/close + volume + 时间戳)。
    /// [PortGroup] + [Port] 由低代码层反射枚举字段,自动生成 ROM&lt;DateTime&gt; / ROM&lt;double&gt; 端口。
    /// </summary>
    [PortGroup("MockKLinePorts")]
    public readonly struct MockKLineBar
    {
        [Port] public DateTime Time   { get; init; }
        [Port] public double   Open   { get; init; }
        [Port] public double   High   { get; init; }
        [Port] public double   Low    { get; init; }
        [Port] public double   Close  { get; init; }
        [Port] public double   Volume { get; init; }
    }

    /// <summary>
    /// 流式 mock K 线数据源 —— TradingView 风味"边画边跑":
    /// <list type="number">
    ///   <item><b>seed</b>:OnFetchAsync 落 30 根暖机数据(BB(20) 需要 20 暖机,30 给余量)</item>
    ///   <item><b>tick</b>:进程级 DispatcherTimer 每 150ms 追加 1 根新 bar,所有活实例同步收到</item>
    ///   <item><b>cap</b>:总 bar 数 1000 封顶后 timer 自停(避免无限增长 + side-effect 风暴)</item>
    /// </list>
    ///
    /// <para>
    /// <b>多 cell 同源</b>:dashboard 3 个 cell 各 new 一份 MockKLineDataSource 实例。每个实例独立维护
    /// 自己的 ReactiveDataSource buffer(框架要求),但 bar 内容由静态 _sharedBars 提供 ——
    /// fetch 时拷贝快照,tick 时同步 append。保证三个 cell 的 X 轴帧帧对齐,不出现"主图到第 50 根、
    /// 副图还在第 49 根"的视觉错位。
    /// </para>
    ///
    /// <para>
    /// <b>Viewport trailing</b>:dashboard JSON 把 ViewportManagerFeature.Alignment 设 RightEdge —
    /// LogicalLength 涨 N→N+1 时,只要用户没主动往左拖,viewport ActiveRange 自动 +1 跟住右沿。
    /// 跟 TradingView "实时 K 线"一模一样。
    /// </para>
    ///
    /// <para>
    /// <b>Side-effect 速率</b>:bb_breakout 每 tick 重算全 buffer,看末根是否破轨。突破事件率
    /// = 5% 尖峰 ÷ 突破成功率,实际 demo 跑下来大约每秒 1-2 次买/卖触发,通知中心人眼可读不刷屏。
    /// </para>
    /// </summary>
    public class MockKLineDataSource : ReactiveDataSource<MockKLineDataSource, string, MockKLineBar>
    {
        // ── 进程级共享状态:K 线时间线、生成器、推送 timer、订阅者表 ─────────────

        private const int SeedBars = 30;
        private const int MaxBars  = 1000;
        private const int TickMs   = 150;

        private static readonly object _sharedLock = new();
        private static readonly List<MockKLineBar> _sharedBars = new(capacity: MaxBars);
        private static readonly List<WeakReference<MockKLineDataSource>> _liveInstances = new();
        private static DispatcherTimer? _sharedTimer;
        private static Random _sharedRng = new(20260512);
        private static double _sharedLastClose = 100.0;
        private static readonly DateTime _t0 = DateTime.Today.AddDays(-1).AddHours(9).AddMinutes(30);

        // ── 实例状态 ──────────────────────────────────────────────────────────

        private int _localCount;
        public override int LogicalLength => _localCount;

        /// <summary>
        /// 把进程级共享 timeline 重置回零状态 —— 下一次 OnFetchAsync 会重新 seed + 重启 timer。
        /// 业务侧典型用法:每次"运行 Dashboard"前调一次,实现 TradingView 那种"开窗就从头跑一遍"体验。
        /// 已开着的 cell 不会立刻看到效果(它们 buffer 里还是旧数据);效果在新 DS 实例首次 fetch 时显现。
        /// </summary>
        public static void Reset()
        {
            lock (_sharedLock)
            {
                _sharedTimer?.Stop();
                _sharedTimer = null;
                _sharedBars.Clear();
                _sharedRng = new Random(20260512);
                _sharedLastClose = 100.0;
                // _liveInstances 留着 —— WeakReference 会自动清死的;留着活的(老 cell)避免误删
            }
        }

        public MockKLineDataSource()
        {
            lock (_sharedLock) _liveInstances.Add(new WeakReference<MockKLineDataSource>(this));
        }

        protected override Task<int> OnFetchAsync(string? context, CancellationToken token)
        {
            EnsureSharedFeedStarted();

            // 拿到当前已生成的 bar 快照,各实例独立 fill 自己的 buffer
            MockKLineBar[] snap;
            lock (_sharedLock) snap = _sharedBars.ToArray();

            // 必须在 UpdateBuffer 之前更新 _localCount —— UpdateBuffer 内部立即 Publish,
            // 下游 ViewportManager 读 LogicalLength 决定 ActiveRange,延后赋值会出现"buffer 已有 30 根
            // 但 LogicalLength 还是 0"的瞬间不一致(虽然过一帧自愈,初始 IsLoading flip 时可能误判 empty)。
            _localCount = snap.Length;
            UpdateBuffer(buf => { buf.Clear(); buf.AddRange(snap); });
            return Task.FromResult(_localCount);
        }

        /// <summary>幂等启动 —— 首个 fetch 时建立 seed + 启动 tick timer,后续 fetch 走快照即可。</summary>
        private static void EnsureSharedFeedStarted()
        {
            lock (_sharedLock)
            {
                if (_sharedTimer != null) return;

                // Seed:生成 30 根暖机数据(BB(20) 要 20 根,留 10 余量让 BB 一开局就有值)
                for (int i = 0; i < SeedBars; i++)
                {
                    var bar = GenerateOneBarInLock();
                    _sharedBars.Add(bar);
                }

                // 必须显式绑 UI Dispatcher —— OnFetchAsync 可能在 background 线程触发,
                // 那线程没 Dispatcher。DispatcherTimer ctor 不传 Dispatcher 会用 CurrentDispatcher 抓
                // 当前线程的(background 线程上是 null),timer 永不 Tick。
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null) return;  // 非 WPF host(单测等),不启 timer

                _sharedTimer = new DispatcherTimer(
                    TimeSpan.FromMilliseconds(TickMs),
                    DispatcherPriority.Background,
                    (_, __) => OnSharedTick(),
                    dispatcher);
                _sharedTimer.Start();
            }
        }

        /// <summary>timer tick —— 在 UI 线程跑:① 生成 1 根新 bar ② 广播给所有活实例 append + Publish。</summary>
        private static void OnSharedTick()
        {
            MockKLineBar newBar;
            MockKLineDataSource[] subscribers;

            lock (_sharedLock)
            {
                if (_sharedBars.Count >= MaxBars)
                {
                    // 到顶,停止推送 —— 历史保留,用户仍可滚动查看
                    _sharedTimer?.Stop();
                    _sharedTimer = null;
                    return;
                }

                newBar = GenerateOneBarInLock();
                _sharedBars.Add(newBar);

                // 快照活实例 + 顺手清死引用
                var alive = new List<MockKLineDataSource>(_liveInstances.Count);
                for (int i = _liveInstances.Count - 1; i >= 0; i--)
                {
                    if (_liveInstances[i].TryGetTarget(out var ds)) alive.Add(ds);
                    else _liveInstances.RemoveAt(i);
                }
                subscribers = alive.ToArray();
            }

            // UpdateBuffer 在 _sharedLock 外调 —— 内部自己有 ReactiveDataSource 的 buffer lock,
            // 嵌套两层锁顺序敏感(死锁风险)。
            foreach (var ds in subscribers)
            {
                try
                {
                    // _localCount 必须先增 —— 跟 OnFetchAsync 同理,UpdateBuffer 内立即 Publish,
                    // ViewportManager.OnLogicalLengthChanged 才能正确看到 N→N+1。
                    ds._localCount++;
                    ds.UpdateBuffer(buf => buf.Add(newBar));
                    // UpdateBuffer 内已自动 Publish() → 下游 LineSeries / Candle / bb_breakout 全部刷新
                }
                catch
                {
                    // 实例已 dispose / cell 关闭 → 静默,下一次 tick 会被 WeakReference 清掉
                }
            }
        }

        /// <summary>调用前请持有 _sharedLock。GBM 主体 + 5% 尖峰 + volume 跟 |return| 共振。</summary>
        private static MockKLineBar GenerateOneBarInLock()
        {
            const double dt = 1.0 / 240.0;
            const double sigma = 0.55;
            const double meanRev = 0.003;

            double drift = -meanRev * (Math.Log(_sharedLastClose) - Math.Log(100.0));
            double u1 = 1.0 - _sharedRng.NextDouble();
            double u2 = 1.0 - _sharedRng.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            double rawReturn = drift * dt + sigma * Math.Sqrt(dt) * z;

            if (_sharedRng.NextDouble() < 0.05)
            {
                double spike = (0.008 + _sharedRng.NextDouble() * 0.017) * (_sharedRng.NextDouble() < 0.5 ? -1 : 1);
                rawReturn += spike;
            }

            double close = _sharedLastClose * Math.Exp(rawReturn);
            double open = _sharedLastClose;
            double bodyHi = Math.Max(open, close);
            double bodyLo = Math.Min(open, close);
            double wick = Math.Abs(close - open) * (0.3 + _sharedRng.NextDouble() * 0.7) + _sharedLastClose * 0.0005;
            double high = bodyHi + wick * _sharedRng.NextDouble();
            double low  = bodyLo - wick * _sharedRng.NextDouble();
            double volume = 3000 + Math.Abs(rawReturn) * 1_500_000 + _sharedRng.NextDouble() * 2000;

            var bar = new MockKLineBar
            {
                Time   = _t0.AddMinutes(_sharedBars.Count),
                Open   = open,
                High   = high,
                Low    = low,
                Close  = close,
                Volume = volume,
            };
            _sharedLastClose = close;
            return bar;
        }
    }
}
