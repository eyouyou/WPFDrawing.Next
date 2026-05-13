using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using Hevo.Drawing.LowCodeDemo.Backtest;

namespace Hevo.Drawing.LowCodeDemo
{
    /// <summary>
    /// §回测 UI —— 把 <see cref="MockKLineDataSource.SnapshotBars"/> 当前 timeline 喂给
    /// <see cref="BacktestEngine"/>,顶部 6 张卡片汇总 stats,下面 DataGrid 列每笔交易。
    /// <para>
    /// 同步执行:1000 根 bar 跑 &lt;1ms,UI 线程直接调,不开 Task.Run(也就不用 marshal 回 UI 线程)。
    /// 真要跑万根级别再换 Task.Run + Dispatcher.Invoke 渲染。
    /// </para>
    /// <para>
    /// <b>使用流程</b>:用户先在「Dashboard 编辑器」tab 点「运行」让 K 线 timeline 累几根 bar,
    /// 然后切到本 tab 点「运行回测」—— 拿到的是同一份 timeline 的离线策略评估,跟
    /// dashboard strategy cell 上 BUY/SELL 箭头视觉对应得上(都用 BB(20, 2.0))。
    /// </para>
    /// </summary>
    public partial class BacktestView : UserControl
    {
        // DataGrid 绑这条 ObservableCollection;每次 Run 清空 + 填充。
        private readonly ObservableCollection<TradeRow> _rows = new();

        public BacktestView()
        {
            InitializeComponent();
            grid.ItemsSource = _rows;

            btnRun.Click += (_, __) => RunOnce();

            // 进 tab 第一时间不跑 —— K 线 timeline 可能还空,等用户先去 Dashboard tab 攒数据。
            // 但顺便刷一次状态行,提示当前已有多少 bar。
            Loaded += (_, __) => RefreshBarStatus();
        }

        private void RefreshBarStatus()
        {
            int n = MockKLineDataSource.SnapshotBars().Length;
            txtStatus.Text = n == 0
                ? "当前 timeline 为空 —— 先到「Dashboard 编辑器」tab 点「运行」让 K 线累几根 bar,再回来跑回测。"
                : $"当前 timeline:{n} 根 bar,可以「运行回测」。";
        }

        private void RunOnce()
        {
            try
            {
                // ── 1) 参数解析 + 校验 ────────────────────────────────────────
                if (!TryParseOptions(out var options, out var parseErr))
                {
                    txtStatus.Text = $"❌ 参数解析失败:{parseErr}";
                    return;
                }

                // ── 2) 取当前 timeline 快照 ───────────────────────────────────
                var bars = MockKLineDataSource.SnapshotBars();
                if (bars.Length == 0)
                {
                    txtStatus.Text = "❌ Timeline 空 —— 请先到「Dashboard 编辑器」tab 点「运行」生成 K 线数据。";
                    return;
                }
                if (bars.Length < options.BollingerLength + 2)
                {
                    txtStatus.Text = $"❌ 数据太少:仅 {bars.Length} 根 bar,需要 ≥ {options.BollingerLength + 2} 根才能起策略。";
                    return;
                }

                // ── 3) 拆 OHLC + 跑 engine ────────────────────────────────────
                var times  = new DateTime[bars.Length];
                var opens  = new double[bars.Length];
                var closes = new double[bars.Length];
                for (int i = 0; i < bars.Length; i++)
                {
                    times[i]  = bars[i].Time;
                    opens[i]  = bars[i].Open;
                    closes[i] = bars[i].Close;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = BacktestEngine.Run(new BacktestInput(times, opens, closes), options);
                sw.Stop();

                // ── 4) 渲染:Stats 卡片 + 交易明细 ────────────────────────────
                RenderStats(result.Stats);
                _rows.Clear();
                for (int i = 0; i < result.Trades.Count; i++)
                {
                    _rows.Add(new TradeRow(i + 1, result.Trades[i]));
                }

                txtStatus.Text =
                    $"✅ 回测完成:{result.Stats.BarCount} 根 bar / {result.Stats.TotalTrades} 笔交易,引擎耗时 {sw.Elapsed.TotalMilliseconds:F2} ms。";
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"❌ 回测失败:{ex.GetType().Name}: {ex.Message}";
            }
        }

        private bool TryParseOptions(out BacktestOptions options, out string error)
        {
            options = default!;
            error = string.Empty;

            if (!double.TryParse(txtInitialCapital.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var cap) || cap <= 0)
            { error = "初始资金必须是正数"; return false; }
            if (!double.TryParse(txtPositionSize.Text,   NumberStyles.Float, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
            { error = "每笔手数必须是正数"; return false; }
            if (!int.TryParse(txtBBLength.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var L) || L < 2)
            { error = "BB 长度必须是 ≥ 2 的整数"; return false; }
            if (!double.TryParse(txtBBK.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var k) || k <= 0)
            { error = "BB σ 倍数必须是正数"; return false; }

            options = new BacktestOptions(
                InitialCapital:  cap,
                PositionSize:    qty,
                BollingerLength: L,
                BollingerK:      k);
            return true;
        }

        private void RenderStats(BacktestStats s)
        {
            var profitBrush = (SolidColorBrush)new BrushConverter().ConvertFrom("#FF66BB6A")!;
            var lossBrush   = (SolidColorBrush)new BrushConverter().ConvertFrom("#FFEF5350")!;
            var neutralBrush = (SolidColorBrush)new BrushConverter().ConvertFrom("#FFE0E6EC")!;

            lblTotalPnL.Text       = s.TotalPnL.ToString("+#,##0.00;-#,##0.00;0.00", CultureInfo.InvariantCulture);
            lblTotalPnL.Foreground = s.TotalPnL > 0 ? profitBrush : s.TotalPnL < 0 ? lossBrush : neutralBrush;

            lblTotalReturn.Text       = (s.TotalReturnPercent >= 0 ? "+" : string.Empty) + s.TotalReturnPercent.ToString("F2", CultureInfo.InvariantCulture) + "%";
            lblTotalReturn.Foreground = s.TotalReturnPercent > 0 ? profitBrush : s.TotalReturnPercent < 0 ? lossBrush : neutralBrush;

            lblTrades.Text       = $"{s.TotalTrades}  ({s.WinRatePercent:F1}% 胜)";
            lblTrades.Foreground = neutralBrush;

            lblMaxDD.Text       = $"-{s.MaxDrawdownPercent:F2}%";
            lblMaxDD.Foreground = s.MaxDrawdownPercent > 0 ? lossBrush : neutralBrush;

            lblSharpe.Text       = s.Sharpe.ToString("F2", CultureInfo.InvariantCulture);
            lblSharpe.Foreground = s.Sharpe > 0 ? profitBrush : s.Sharpe < 0 ? lossBrush : neutralBrush;

            lblFinalEquity.Text       = s.FinalEquity.ToString("#,##0.00", CultureInfo.InvariantCulture);
            lblFinalEquity.Foreground = s.FinalEquity > s.InitialCapital ? profitBrush :
                                         s.FinalEquity < s.InitialCapital ? lossBrush  : neutralBrush;
        }

        /// <summary>
        /// DataGrid 行的视图模型 —— 把 Trade(record)适配成带 # 序号 / 显示字符串 / 涨跌色刷的 binding source。
        /// </summary>
        public sealed class TradeRow
        {
            public int      Index      { get; }
            public DateTime EntryTime  { get; }
            public double   EntryPrice { get; }
            public DateTime ExitTime   { get; }
            public double   ExitPrice  { get; }
            public double   Quantity   { get; }
            public string   PnLDisplay        { get; }
            public string   PnLPercentDisplay { get; }
            public SolidColorBrush PnLColor   { get; }

            // 必须全限定 —— Hevo.Trade(ITradeService 那条)也是 namespace,简写 Trade 编译器会优先匹到 namespace,
            // 报 CS0118 "Trade 是命名空间,但此处被当做类型使用"。
            public TradeRow(int index, Backtest.Trade t)
            {
                Index      = index;
                EntryTime  = t.EntryTime;
                EntryPrice = t.EntryPrice;
                ExitTime   = t.ExitTime;
                ExitPrice  = t.ExitPrice;
                Quantity   = t.Quantity;
                PnLDisplay        = t.PnL.ToString("+#,##0.00;-#,##0.00;0.00", CultureInfo.InvariantCulture);
                PnLPercentDisplay = (t.PnLPercent >= 0 ? "+" : string.Empty) + t.PnLPercent.ToString("F2", CultureInfo.InvariantCulture) + "%";
                PnLColor = t.PnL > 0
                    ? (SolidColorBrush)new BrushConverter().ConvertFrom("#FF66BB6A")!
                    : t.PnL < 0
                        ? (SolidColorBrush)new BrushConverter().ConvertFrom("#FFEF5350")!
                        : (SolidColorBrush)new BrushConverter().ConvertFrom("#FFE0E6EC")!;
            }
        }
    }
}
