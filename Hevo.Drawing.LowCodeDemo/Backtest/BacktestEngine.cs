using System;
using System.Collections.Generic;

namespace Hevo.Drawing.LowCodeDemo.Backtest
{
    /// <summary>
    /// §回测 引擎 —— 跟 <c>PyIndicators/bb_breakout_strategy.py</c> 等价的离线策略评估器,纯 BCL。
    ///
    /// <para><b>策略语义</b>(长仓 only,跟 Python 端 fire_buy / fire_sell 一致):</para>
    /// <list type="number">
    ///   <item>每根 bar 收盘后,用最近 N 根 close 计算 Bollinger Bands (mean, mean±k·σ)。</item>
    ///   <item>当前空仓 && <c>close[i] &gt; upper[i]</c> → 在 <b>第 i+1 根开盘</b>买入 PositionSize 股。</item>
    ///   <item>当前持仓 && <c>close[i] &lt; lower[i]</c> → 在 <b>第 i+1 根开盘</b>全部平仓。</item>
    ///   <item>回测结束仍持仓:按 <b>最后一根 close</b> 强平,记 1 笔 trade,fill price 用 close 而非 open(已经没有下一根了)。</item>
    /// </list>
    ///
    /// <para><b>设计取舍</b>:</para>
    /// <list type="bullet">
    ///   <item><b>不用 numpy / Python</b> —— 纯 C# 单遍 rolling sum/var,O(N) 计算 BB,Sharpe / drawdown 也都顺手做了。
    ///         Demo 1000 根 bar 跑 &lt;1ms,UI 线程同步调用零卡顿。</item>
    ///   <item><b>未来函数防护</b>:信号在 close[i] 触发,fill 价用 open[i+1] —— 哪怕用户改成 limit / market 不同模型,
    ///         接 i+1 这条不变量也保证不会用到"当根 bar 已知未来"的价格。</item>
    ///   <item><b>不复利</b>:固定 PositionSize,不按权益增长加仓。Demo 演示意图清晰;严肃回测换 ATR sizing。</item>
    ///   <item><b>无手续费</b>:中国 A股 / 美股 commission 模型差异巨大,demo 用零滑点 / 零佣金把策略 alpha 纯粹化。
    ///         接真实回测时,在 _RecordTrade 里减一笔 commission 即可。</item>
    /// </list>
    /// </summary>
    public static class BacktestEngine
    {
        public static BacktestResult Run(BacktestInput input, BacktestOptions options)
        {
            if (input is null) throw new ArgumentNullException(nameof(input));
            if (options is null) throw new ArgumentNullException(nameof(options));

            int n = input.Closes.Count;
            if (input.Opens.Count != n || input.Times.Count != n)
                throw new ArgumentException(
                    $"BacktestInput 三组数组长度不一致:Times={input.Times.Count} Opens={input.Opens.Count} Closes={input.Closes.Count}",
                    nameof(input));
            if (options.BollingerLength < 2)
                throw new ArgumentException($"BollingerLength 必须 ≥ 2,实际 {options.BollingerLength}", nameof(options));
            if (options.BollingerK <= 0)
                throw new ArgumentException($"BollingerK 必须 > 0,实际 {options.BollingerK}", nameof(options));
            if (options.InitialCapital <= 0)
                throw new ArgumentException($"InitialCapital 必须 > 0,实际 {options.InitialCapital}", nameof(options));
            if (options.PositionSize <= 0)
                throw new ArgumentException($"PositionSize 必须 > 0,实际 {options.PositionSize}", nameof(options));

            var closes = input.Closes;
            var opens  = input.Opens;
            var times  = input.Times;

            // ── 1) 单遍 rolling 算 BB(N, k) ─────────────────────────────────────
            // 不用 numpy,O(N) 滑动窗口:每个新 bar 加入、最老 bar 退出,sum / sum² 各 O(1)。
            // 前 N-1 根 upper / lower 为 NaN(暖机期),策略 step 主动跳过 NaN。
            var upper = new double[n];
            var lower = new double[n];
            for (int i = 0; i < n; i++) { upper[i] = double.NaN; lower[i] = double.NaN; }

            int L = options.BollingerLength;
            double k = options.BollingerK;
            if (n >= L)
            {
                double sum = 0.0, sum2 = 0.0;
                for (int i = 0; i < L; i++) { sum += closes[i]; sum2 += closes[i] * closes[i]; }
                FillBand(upper, lower, L - 1, sum, sum2, L, k);
                for (int i = L; i < n; i++)
                {
                    double exiting = closes[i - L];
                    double entering = closes[i];
                    sum  += entering - exiting;
                    sum2 += entering * entering - exiting * exiting;
                    FillBand(upper, lower, i, sum, sum2, L, k);
                }
            }

            // ── 2) 走 bar,产生 trades + equity curve ────────────────────────────
            var trades = new List<Trade>();
            var equity = new List<EquityPoint>(n);

            double cash = options.InitialCapital;
            double pos  = 0.0;
            double entryPrice = 0.0;
            DateTime entryTime = default;

            for (int i = 0; i < n; i++)
            {
                // 当根 bar 收盘后的 mark-to-market 权益
                double mtm = cash + pos * closes[i];
                equity.Add(new EquityPoint(times[i], mtm));

                // 信号在 close[i] 评估,但 fill 价格用 open[i+1] —— 没有下一根就不能下单
                if (i + 1 >= n) continue;
                if (double.IsNaN(upper[i]) || double.IsNaN(lower[i])) continue;

                double price = closes[i];
                if (pos == 0.0 && price > upper[i])
                {
                    double fill = opens[i + 1];
                    pos = options.PositionSize;
                    cash -= pos * fill;
                    entryPrice = fill;
                    entryTime  = times[i + 1];
                }
                else if (pos > 0.0 && price < lower[i])
                {
                    double fill = opens[i + 1];
                    double pnl  = pos * (fill - entryPrice);
                    cash += pos * fill;
                    trades.Add(new Trade(
                        EntryTime:   entryTime,
                        EntryPrice:  entryPrice,
                        ExitTime:    times[i + 1],
                        ExitPrice:   fill,
                        Quantity:    pos,
                        PnL:         pnl,
                        PnLPercent:  (fill / entryPrice - 1.0) * 100.0));
                    pos = 0.0;
                    entryPrice = 0.0;
                    entryTime  = default;
                }
            }

            // 末根仍持仓 —— 按最后一根 close 强平,trade 也照样记一笔(否则 stats 漏算这段持仓 PnL)。
            if (pos > 0.0 && n > 0)
            {
                double last = closes[n - 1];
                double pnl  = pos * (last - entryPrice);
                cash += pos * last;
                trades.Add(new Trade(
                    EntryTime:   entryTime,
                    EntryPrice:  entryPrice,
                    ExitTime:    times[n - 1],
                    ExitPrice:   last,
                    Quantity:    pos,
                    PnL:         pnl,
                    PnLPercent:  (last / entryPrice - 1.0) * 100.0));
                pos = 0.0;
            }

            // ── 3) Stats:汇总 + 最大回撤 + 简化夏普 ──────────────────────────────
            double finalEquity = cash;     // pos 已归零
            double totalPnL    = finalEquity - options.InitialCapital;
            double totalRet    = totalPnL / options.InitialCapital * 100.0;

            int wins = 0, losses = 0;
            foreach (var t in trades)
            {
                if (t.PnL > 0) wins++;
                else if (t.PnL < 0) losses++;
            }
            double winRate = trades.Count > 0 ? wins * 100.0 / trades.Count : 0.0;

            // 最大回撤 —— 走 equity curve,维护 running peak,求最大 (peak - eq)/peak。
            double maxDD = 0.0;
            double peak  = options.InitialCapital;
            foreach (var ec in equity)
            {
                if (ec.Equity > peak) peak = ec.Equity;
                if (peak > 0)
                {
                    double dd = (peak - ec.Equity) / peak * 100.0;
                    if (dd > maxDD) maxDD = dd;
                }
            }

            // 简化 Sharpe:bar-to-bar 简单收益率的 mean / std,按 1-min bar (~240/day, 252 day)年化。
            // 不是严肃量化生产用的 Sharpe(没 risk-free,没 log return,样本独立性假设违反),
            // 仅供 demo 看一眼"波动调整后的收益"。
            double sharpe = 0.0;
            if (equity.Count >= 2)
            {
                double sumR = 0.0;
                int cnt = 0;
                var rets = new double[equity.Count - 1];
                for (int i = 1; i < equity.Count; i++)
                {
                    double prev = equity[i - 1].Equity;
                    if (prev <= 0) continue;
                    double r = (equity[i].Equity - prev) / prev;
                    rets[cnt++] = r;
                    sumR += r;
                }
                if (cnt > 1)
                {
                    double mean = sumR / cnt;
                    double sq = 0.0;
                    for (int i = 0; i < cnt; i++) { double d = rets[i] - mean; sq += d * d; }
                    double std = Math.Sqrt(sq / cnt);
                    if (std > 1e-12)
                    {
                        // ann factor ≈ √(252 * 240) ≈ √60480 ≈ 245.9。Demo 用近似。
                        sharpe = mean / std * Math.Sqrt(252.0 * 240.0);
                    }
                }
            }

            var stats = new BacktestStats(
                InitialCapital:     options.InitialCapital,
                FinalEquity:        finalEquity,
                TotalPnL:           totalPnL,
                TotalReturnPercent: totalRet,
                TotalTrades:        trades.Count,
                Winners:            wins,
                Losers:             losses,
                WinRatePercent:     winRate,
                MaxDrawdownPercent: maxDD,
                Sharpe:             sharpe,
                BarCount:           n);

            return new BacktestResult(stats, trades, equity);
        }

        // 把 sum / sum² → mean / std → upper / lower 算出来写进数组指定位置。
        // 拆出小函数避免主循环里那段算式复读两遍。
        private static void FillBand(double[] upper, double[] lower, int idx, double sum, double sum2, int len, double k)
        {
            double mean = sum / len;
            double varv = sum2 / len - mean * mean;
            if (varv < 0) varv = 0;
            double std = Math.Sqrt(varv);
            upper[idx] = mean + k * std;
            lower[idx] = mean - k * std;
        }
    }
}
