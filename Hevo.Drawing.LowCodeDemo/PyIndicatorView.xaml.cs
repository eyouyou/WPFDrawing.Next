using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hevo.Charting.PythonNet;

namespace Hevo.Drawing.LowCodeDemo
{
    /// <summary>
    /// §D2.5.E2E Python 嵌入指标 + 交易桥的 smoke test 面板。
    /// 两个动作:
    /// <list type="number">
    ///   <item>① 启 PythonNetRuntime → 写一个 ma.py(@register MA20)→ AutoDiscover 注册 →
    ///         经 BlueprintHandlerRegistry 拿强类型委托 → 喂 1000 点正弦波 → 显示前后对比。</item>
    ///   <item>② 同上 + UseTradeService(MockTradeService) → Python 函数体内 trade.place_order(...)
    ///         → MockTradeService 收单 → 查账户持仓 + 余额验证。</item>
    /// </list>
    /// <para>
    /// 这两个 demo 均**不依赖**蓝图运行时(蓝图层 ComputeFeature / HandlerFeature 接入是另一条
    /// 路径,需要 ChartCell 跑起来)—— 这里 smoke test 只验证 Python 嵌入 / marshalling / trade 桥三件套主链路能跑通。
    /// </para>
    /// </summary>
    public partial class PyIndicatorView : UserControl
    {
        // PythonNetRuntime / PythonHandlerRegistry / MockTradeService / .py 文件部署
        // 全部由 DemoPythonHost 管(进程级共享,跟 LowCodeDemoView 共用同一份 Python 实例)。
        private PythonHandlerRegistry Registry => DemoPythonHost.Registry;

        // ── 实时推流状态 ──────────────────────────────────────────
        private DispatcherTimer? _rtTimer;
        private const int RtWindowSize = 240;        // 滑动窗口长度(MA20 / MACD26 都够暖机)
        private const int RtTickIntervalMs = 500;    // 每 500ms 一帧
        private readonly double[] _rtPrices = new double[RtWindowSize];
        private int _rtFilled;                       // 窗口已填充长度(< RtWindowSize 时还在暖机)
        private int _rtTickCount;
        private double _rtLastPrice = 100.0;
        private readonly Random _rtRng = new(20260507);
        private bool _rtBusy;                        // 防 tick 内重入

        public PyIndicatorView()
        {
            InitializeComponent();
            btnSmoke.Click += async (_, __) => await RunSmokeTest();
            btnTradeDemo.Click += async (_, __) => await RunTradeDemo();
            btnRealtimeStart.Click += async (_, __) => await StartRealtime();
            btnRealtimeStop.Click += (_, __) => StopRealtime();
            btnFullStrategy.Click += async (_, __) => await RunFullStrategyDemo();
            btnClear.Click += (_, __) => txtOutput.Clear();
        }

        /// <summary>
        /// §D2.X 完整策略 demo —— bb_breakout_strategy.py 端到端跑通:
        /// 1. 生成 250 根合成价格(GBM + 偶发尖峰,确保会触发上下轨突破)
        /// 2. 滑动窗口逐 tick 调 bb_breakout(模拟实时推流)
        /// 3. 每次 tick 末值破上/下轨时,Python 内部:
        ///    - trade.place_order(...) → MockTradeService 接单
        ///    - send_email / send_dingtalk / send_webhook / send_sms / log_alert → MockNotifier 收单
        /// 4. 跑完打印账户快照 + 通知总数,让用户在"通知中心" tab 看到累计 N 条邮件 / 钉钉等
        /// </summary>
        private async Task RunFullStrategyDemo()
        {
            txtStatus.Text = "[FullStrategy] DemoPythonHost.EnsureInitialized...";
            try
            {
                DemoPythonHost.EnsureInitialized();

                var del = Registry.TryGet("bb_breakout")
                    as Func<ReadOnlyMemory<double>, object?>;
                if (del == null)
                {
                    AppendLine("[FullStrategy] ❌ handler 'bb_breakout' 未注册或类型不匹配。");
                    AppendLine("  → 检查 bb_breakout_strategy.py 是否落盘到 IndicatorsDir 且未在 .hevo_disabled.txt 内。");
                    txtStatus.Text = "[FullStrategy] 失败";
                    return;
                }

                int notifBefore = MockNotifier.History.Count;
                var snapBefore = await DemoPythonHost.Trade.QueryAccountAsync();

                AppendLine("[FullStrategy] ▶ 生成 250 根合成价格(GBM + 5% 概率尖峰)...");
                var bars = GenerateSyntheticPrices(seed: 20260512, count: 250, sigma: 0.55);

                // 滑动窗口:从第 21 根开始(BB(20) 暖机),每 tick 喂完整历史给 bb_breakout
                AppendLine("[FullStrategy] ▶ 滑动窗口推送(tick=21..249)...");
                int triggers = 0;
                for (int n = 21; n <= bars.Length; n++)
                {
                    var window = new ReadOnlyMemory<double>(bars, 0, n);
                    // bb_breakout 返回 dict<string, ROM<double>>,这里不消费结果(仅看副作用)
                    int notifBeforeTick = MockNotifier.History.Count;
                    await Task.Run(() => del(window));
                    if (MockNotifier.History.Count > notifBeforeTick) triggers++;
                }

                var snapAfter = await DemoPythonHost.Trade.QueryAccountAsync();
                int notifAfter = MockNotifier.History.Count;
                int notifDelta = notifAfter - notifBefore;

                AppendLine("[FullStrategy] ✅ 完成");
                AppendLine($"  价格区间: [{bars.Min():F3} .. {bars.Max():F3}],均值 {bars.Average():F3}");
                AppendLine($"  触发 tick 数: {triggers}(每次触发 5-6 条通知 + 1 笔订单)");
                AppendLine($"  通知总数 △: {notifDelta}(本次新增,本次前已有 {notifBefore})");
                AppendLine($"  账户余额: {snapBefore.Balance:F2} → {snapAfter.Balance:F2}");
                AppendLine($"  持仓数: {snapBefore.Positions.Count} → {snapAfter.Positions.Count}");
                foreach (var p in snapAfter.Positions)
                {
                    AppendLine($"    - {p.Symbol}: qty={p.Qty}, avg_cost={p.AvgCost:F3}, mv={p.MarketValue:F2}");
                }
                AppendLine("  → 切到 \"通知中心\" tab 看历史通知列表(按渠道过滤)");

                txtStatus.Text = $"[FullStrategy] 成功 — {triggers} 次触发 / {notifDelta} 条新通知 / {snapAfter.Positions.Count} 个持仓";
            }
            catch (PythonDiagnosticsException pex)
            {
                txtStatus.Text = "[FullStrategy] 失败 (Python 异常)";
                AppendLine($"[FullStrategy] ❌ Python {pex.PythonExceptionType}: {pex.Message}");
                AppendLine(pex.PythonTraceback);
            }
            catch (Exception ex)
            {
                txtStatus.Text = "[FullStrategy] 失败 (.NET 异常)";
                AppendLine($"[FullStrategy] ❌ .NET {ex.GetType().Name}: {ex.Message}");
                AppendLine(ex.StackTrace ?? "");
            }
        }

        /// <summary>合成价格序列(GBM 主体 + 5% 概率 ±0.8-2.5% 尖峰)—— 保证 BB(20,2σ) 会被频繁突破。</summary>
        private static double[] GenerateSyntheticPrices(int seed, int count, double sigma)
        {
            var rng = new Random(seed);
            var bars = new double[count];
            double last = 100.0;
            const double dt = 1.0 / 240.0;
            const double meanRev = 0.003;
            for (int i = 0; i < count; i++)
            {
                double drift = -meanRev * (Math.Log(last) - Math.Log(100.0));
                double u1 = 1.0 - rng.NextDouble();
                double u2 = 1.0 - rng.NextDouble();
                double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                double next = last * Math.Exp(drift * dt + sigma * Math.Sqrt(dt) * z);
                if (rng.NextDouble() < 0.05)
                {
                    double spike = (0.008 + rng.NextDouble() * 0.017) * (rng.NextDouble() < 0.5 ? -1 : 1);
                    next *= 1.0 + spike;
                }
                bars[i] = next;
                last = next;
            }
            return bars;
        }

        private async Task RunSmokeTest()
        {
            txtStatus.Text = "[Smoke] DemoPythonHost.EnsureInitialized...";
            try
            {
                DemoPythonHost.EnsureInitialized();

                // 构造一段正弦波数据,作为 bollinger_upper 的输入
                var input = Enumerable.Range(0, 100)
                    .Select(i => 100.0 + 5.0 * Math.Sin(i * 0.2))
                    .ToArray();

                txtStatus.Text = "[Smoke] Resolve handler + 调用...";

                // 取 BlueprintHandlerRegistry 的弱类型 Delegate(强类型 HandlerKey 在 §K-2,这里不绕)
                var del = Registry.TryGet("bollinger_upper") as Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>;
                if (del == null)
                {
                    AppendLine("[Smoke] ❌ handler 'bollinger_upper' 未注册或类型不匹配。");
                    txtStatus.Text = "[Smoke] 失败";
                    return;
                }

                ReadOnlyMemory<double> output;
                output = await Task.Run(() => del(input));

                AppendLine("[Smoke] ✅ Python 调用成功");
                AppendLine($"  input  长度 = {input.Length},前 10 项 = {Format(input.AsSpan().Slice(0, 10))}");
                AppendLine($"  output 长度 = {output.Length},前 30 项 = {Format(output.Span.Slice(0, Math.Min(30, output.Length)))}");
                AppendLine($"  (前 19 项应为 NaN —— Bollinger length=20 头部 length-1 项空窗)");

                txtStatus.Text = "[Smoke] 成功";
            }
            catch (PythonDiagnosticsException pex)
            {
                txtStatus.Text = "[Smoke] 失败 (Python 异常)";
                AppendLine($"[Smoke] ❌ Python {pex.PythonExceptionType}: {pex.Message}");
                AppendLine(pex.PythonTraceback);
            }
            catch (Exception ex)
            {
                txtStatus.Text = "[Smoke] 失败 (.NET 异常)";
                AppendLine($"[Smoke] ❌ .NET {ex.GetType().Name}: {ex.Message}");
                if (ex is DllNotFoundException || ex.Message.Contains("python", StringComparison.OrdinalIgnoreCase))
                {
                    AppendLine("");
                    AppendLine("提示:Python DLL 找不到。请:");
                    AppendLine("  1) 装 Python 3.8-3.12 + 'pip install numpy'");
                    AppendLine("  2) 设环境变量 PYTHONNET_PYDLL=C:\\path\\to\\python311.dll");
                    AppendLine("  3) 重启进程");
                }
                AppendLine(ex.StackTrace ?? "");
            }
        }

        private async Task RunTradeDemo()
        {
            txtStatus.Text = "[Trade] DemoPythonHost.EnsureInitialized...";
            try
            {
                DemoPythonHost.EnsureInitialized();

                txtStatus.Text = "[Trade] Resolve handler + 调用 place_demo_order...";

                var del = Registry.TryGet("place_demo_order") as Action<ReadOnlyMemory<double>>;
                if (del == null)
                {
                    AppendLine("[Trade] ❌ handler 'place_demo_order' 未注册或类型不匹配。");
                    txtStatus.Text = "[Trade] 失败";
                    return;
                }

                // 给 handler 一段触发条件 —— Python 函数会用最后一个值 > 100 作为下单 trigger
                var input = new double[] { 99, 98, 100, 101, 105 };
                await Task.Run(() => del(input));

                txtStatus.Text = "[Trade] 查账户...";
                var snap = await DemoPythonHost.Trade.QueryAccountAsync();
                AppendLine("[Trade] ✅ Python → trade.place_order 完成");
                AppendLine($"  账户余额: {snap.Balance:F2}");
                AppendLine($"  持仓数: {snap.Positions.Count}");
                foreach (var p in snap.Positions)
                {
                    AppendLine($"    - {p.Symbol}: qty={p.Qty}");
                }
                txtStatus.Text = "[Trade] 成功";
            }
            catch (PythonDiagnosticsException pex)
            {
                txtStatus.Text = "[Trade] 失败 (Python 异常)";
                AppendLine($"[Trade] ❌ Python {pex.PythonExceptionType}: {pex.Message}");
                AppendLine(pex.PythonTraceback);
            }
            catch (Exception ex)
            {
                txtStatus.Text = "[Trade] 失败 (.NET 异常)";
                AppendLine($"[Trade] ❌ .NET {ex.GetType().Name}: {ex.Message}");
                AppendLine(ex.StackTrace ?? "");
            }
        }

        // ── 私有 ────────────────────────────────────────────────────────────────

        private void AppendLine(string s)
        {
            txtOutput.AppendText(s + Environment.NewLine);
            txtOutput.ScrollToEnd();
        }

        private static string Format(ReadOnlySpan<double> span)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < span.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                if (double.IsNaN(span[i])) sb.Append("NaN");
                else sb.Append(span[i].ToString("F3"));
            }
            sb.Append(']');
            return sb.ToString();
        }

        // ── ③ 实时多指标推流 ────────────────────────────────────────────────────

        private async Task StartRealtime()
        {
            if (_rtTimer != null) return;
            txtStatus.Text = "[Realtime] DemoPythonHost.EnsureInitialized...";
            try
            {
                DemoPythonHost.EnsureInitialized();

                // §D2.4 DryRunImports 先把 .py 真实 import 错误浮出来(AutoDiscoverDirectory 把单文件
                // 失败 silent skip,直接看 TryGet null 看不出原因)。
                AppendLine("[Realtime] DryRunImports 检查...");
                var diags = Registry.DryRunImports(DemoPythonHost.IndicatorsDir);
                foreach (var d in diags)
                {
                    var fname = System.IO.Path.GetFileName(d.FilePath);
                    if (d.Success)
                    {
                        var hs = string.Join(", ", d.Handlers.Select(h => $"{h.Name}({h.Signature})"));
                        AppendLine($"  ✓ {fname}: {d.Handlers.Count} handlers — {hs}");
                    }
                    else
                    {
                        AppendLine($"  ✗ {fname}: {d.Error}");
                        if (!string.IsNullOrEmpty(d.PythonTraceback)) AppendLine(d.PythonTraceback);
                    }
                }

                // 校验 5 个 handler 都注册到了
                var rsi    = Registry.TryGet("rsi_14")           as Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>;
                var macd   = Registry.TryGet("macd_hist")        as Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>;
                var bbUp   = Registry.TryGet("bollinger_upper")  as Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>;
                var bbLo   = Registry.TryGet("bollinger_lower")  as Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>;
                var signal = Registry.TryGet("combined_signal")  as Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>;
                if (rsi == null || macd == null || bbUp == null || bbLo == null || signal == null)
                {
                    AppendLine("[Realtime] ❌ handler 注册不全:" +
                               $" rsi_14={(rsi==null?"NULL":"OK")}," +
                               $" macd_hist={(macd==null?"NULL":"OK")}," +
                               $" bollinger_upper={(bbUp==null?"NULL":"OK")}," +
                               $" bollinger_lower={(bbLo==null?"NULL":"OK")}," +
                               $" combined_signal={(signal==null?"NULL":"OK")}");
                    AppendLine("看上面 DryRunImports 输出找原因。");
                    txtStatus.Text = "[Realtime] 失败";
                    return;
                }

                // 暖机一次同步调用,避免首 tick 在 UI 线程上付 import / numpy 首次加载成本(典型 100ms+)
                AppendLine("[Realtime] 暖机 numpy / pandas_ta(首次 import 较慢)...");
                _rtFilled = 0;
                _rtTickCount = 0;
                _rtLastPrice = 100.0;
                FillSyntheticWindow(initialBars: 50);     // 先灌 50 根作为开局
                await Task.Run(() => signal(_rtPrices.AsMemory(0, _rtFilled)));
                AppendLine($"[Realtime] ✅ 暖机完成。窗口预填 {_rtFilled} 根,开始推流(间隔 {RtTickIntervalMs}ms)");

                btnRealtimeStart.IsEnabled = false;
                btnRealtimeStop.IsEnabled = true;

                _rtTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RtTickIntervalMs) };
                _rtTimer.Tick += async (_, __) => await OnRealtimeTick(rsi, macd, bbUp, bbLo, signal);
                _rtTimer.Start();
                txtStatus.Text = "[Realtime] 推流中...";
            }
            catch (PythonDiagnosticsException pex)
            {
                txtStatus.Text = "[Realtime] 失败 (Python 异常)";
                AppendLine($"[Realtime] ❌ Python {pex.PythonExceptionType}: {pex.Message}");
                AppendLine(pex.PythonTraceback);
            }
            catch (Exception ex)
            {
                txtStatus.Text = "[Realtime] 失败 (.NET 异常)";
                AppendLine($"[Realtime] ❌ .NET {ex.GetType().Name}: {ex.Message}");
                AppendLine(ex.StackTrace ?? "");
            }
        }

        private void StopRealtime()
        {
            _rtTimer?.Stop();
            _rtTimer = null;
            btnRealtimeStart.IsEnabled = true;
            btnRealtimeStop.IsEnabled = false;
            txtStatus.Text = $"[Realtime] 已停止(共推 {_rtTickCount} tick)";
            AppendLine($"[Realtime] ⏹  已停止,共 {_rtTickCount} tick");
        }

        private async Task OnRealtimeTick(
            Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>> rsi,
            Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>> macd,
            Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>> bbUp,
            Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>> bbLo,
            Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>> signal)
        {
            if (_rtBusy) return;       // 上一帧 Python 还没回来 → 跳过
            _rtBusy = true;
            try
            {
                AdvancePriceWalk();
                _rtTickCount++;
                int n = _rtFilled;
                var window = _rtPrices.AsMemory(0, n);
                double price = _rtPrices[n - 1];

                // 5 个 handler 全在 background 线程跑(各 Invoke 内部 acquire GIL),避免阻塞 UI
                var (rsiOut, macdOut, upOut, loOut, sigOut) = await Task.Run(() => (
                    rsi(window),
                    macd(window),
                    bbUp(window),
                    bbLo(window),
                    signal(window)));

                double rsiV  = LastFinite(rsiOut);
                double macdV = LastFinite(macdOut);
                double upV   = LastFinite(upOut);
                double loV   = LastFinite(loOut);
                double sigV  = LastFinite(sigOut);

                string rsiTag  = double.IsNaN(rsiV) ? "--"
                               : rsiV < 30 ? "超卖↑" : rsiV > 70 ? "超买↓" : "中性";
                string macdTag = double.IsNaN(macdV) ? "--" : macdV > 0 ? "多" : "空";
                string bbTag   = double.IsNaN(upV) ? "--"
                               : price > upV ? "破上轨" : price < loV ? "破下轨" : "区间内";
                string verdict = double.IsNaN(sigV) ? "--"
                               : sigV >  0.5 ? "🟢 强多"
                               : sigV >  0.0 ? "🟢 偏多"
                               : sigV >= 0.0 ? "—  中性"
                               : sigV > -0.5 ? "🔴 偏空"
                               :               "🔴 强空";

                txtLive.Text =
                    $"#{_rtTickCount:D4}   price={price:F3}   " +
                    $"RSI={Fmt(rsiV)} ({rsiTag})   " +
                    $"MACDh={Fmt(macdV)} ({macdTag})   " +
                    $"BB[{Fmt(loV)} .. {Fmt(upV)}] ({bbTag})   " +
                    $"score={Fmt(sigV)} {verdict}";

                txtLive.Foreground = double.IsNaN(sigV)
                    ? new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0x9C))
                    : sigV > 0.3  ? new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A))
                    : sigV < -0.3 ? new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50))
                                  : new SolidColorBrush(Color.FromRgb(0xC0, 0xC8, 0xD0));

                // 每 10 tick 落一行到日志区便于回看
                if (_rtTickCount % 10 == 0 || _rtTickCount <= 3)
                {
                    AppendLine($"#{_rtTickCount:D4} px={price:F3} rsi={Fmt(rsiV)} macd={Fmt(macdV)} bb=[{Fmt(loV)},{Fmt(upV)}] score={Fmt(sigV)}  {verdict}");
                }
            }
            catch (PythonDiagnosticsException pex)
            {
                AppendLine($"[Realtime tick #{_rtTickCount}] ❌ Python {pex.PythonExceptionType}: {pex.Message}");
                AppendLine(pex.PythonTraceback);
                StopRealtime();
            }
            catch (Exception ex)
            {
                AppendLine($"[Realtime tick #{_rtTickCount}] ❌ .NET {ex.GetType().Name}: {ex.Message}");
                StopRealtime();
            }
            finally
            {
                _rtBusy = false;
            }
        }

        /// <summary>
        /// GBM(几何布朗运动)+ 偶发 spike 的合成价格,带轻度均值回归确保围绕 100 波动:
        /// dlog(P) = -0.005·(log(P)-log(100))·dt + sigma·sqrt(dt)·N(0,1) + 偶发跳变
        /// </summary>
        private void AdvancePriceWalk()
        {
            const double dt = 1.0 / 240.0;            // 一年 240 个交易日基准
            const double sigma = 0.45;                // 年化波动率(略夸张以让信号变化明显)
            const double meanRev = 0.005;
            double drift = -meanRev * (Math.Log(_rtLastPrice) - Math.Log(100.0));
            double z = SampleNormal();
            double next = _rtLastPrice * Math.Exp(drift * dt + sigma * Math.Sqrt(dt) * z);

            // 5% 概率有一个尖峰(±0.4-1.5%)制造 RSI/MACD 翻转
            if (_rtRng.NextDouble() < 0.05)
            {
                double spike = (0.004 + _rtRng.NextDouble() * 0.011) * (_rtRng.NextDouble() < 0.5 ? -1 : 1);
                next *= 1.0 + spike;
            }
            _rtLastPrice = next;
            PushPrice(next);
        }

        private void FillSyntheticWindow(int initialBars)
        {
            for (int i = 0; i < initialBars; i++) AdvancePriceWalk();
        }

        private void PushPrice(double price)
        {
            if (_rtFilled < RtWindowSize)
            {
                _rtPrices[_rtFilled++] = price;
            }
            else
            {
                // 滑动:丢最旧,append 最新
                Array.Copy(_rtPrices, 1, _rtPrices, 0, RtWindowSize - 1);
                _rtPrices[RtWindowSize - 1] = price;
            }
        }

        private double SampleNormal()
        {
            // Box-Muller
            double u1 = 1.0 - _rtRng.NextDouble();
            double u2 = 1.0 - _rtRng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        private static double LastFinite(ReadOnlyMemory<double> mem)
        {
            var s = mem.Span;
            for (int i = s.Length - 1; i >= 0; i--)
            {
                double v = s[i];
                if (!double.IsNaN(v) && !double.IsInfinity(v)) return v;
            }
            return double.NaN;
        }

        private static string Fmt(double v) => double.IsNaN(v) ? "  NaN  " : v.ToString("F3");

        // Python sample 源已搬到 DemoPySources.cs(进程级共享 — Tab 1 GraphViewer 跟 Tab 3
        // PyIndicator 用同一份 .py 文件)。落盘由 DemoPythonHost.EnsureInitialized 处理。
    }
}
