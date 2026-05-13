using System;
using System.Linq;
using Hevo.Drawing.LowCodeDemo.Backtest;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §回测 引擎单元回归 —— 锁定 BB(20, 2.0) 长仓策略的几条核心不变式。
    /// <para>
    /// 跟 <c>Hevo.Drawing.LowCodeDemo/Backtest/BacktestEngine.cs</c> 共享同一份源码
    /// (csproj 用 Compile Include + Link 编进两个 assembly,各编各的)。
    /// </para>
    /// </summary>
    public sealed class BacktestEngineTests
    {
        /// <summary>方便构造测试输入的 helper:fixed timestep 1 分钟,opens/closes 等长。</summary>
        private static BacktestInput MakeInput(double[] closes, double[]? opens = null)
        {
            opens ??= closes;  // 默认 open == close(单 tick bar),不影响 BB 计算
            var times = new DateTime[closes.Length];
            var t0 = new DateTime(2026, 5, 13, 9, 30, 0);
            for (int i = 0; i < closes.Length; i++) times[i] = t0.AddMinutes(i);
            return new BacktestInput(times, opens, closes);
        }

        [Fact]
        public void Run_FlatPriceSeries_GeneratesZeroTrades()
        {
            // 全是 100 的常数序列:σ = 0,upper == lower == mean = 100,price 永远不会突破 → 零交易。
            // 防呆:engine 不应该在零方差边界条件下 NaN / Infinity 出错。
            var closes = Enumerable.Repeat(100.0, 50).ToArray();
            var result = BacktestEngine.Run(MakeInput(closes), new BacktestOptions());
            Assert.Empty(result.Trades);
            Assert.Equal(0, result.Stats.TotalTrades);
            Assert.Equal(result.Stats.InitialCapital, result.Stats.FinalEquity);
            Assert.Equal(0.0, result.Stats.TotalPnL);
        }

        [Fact]
        public void Run_TooFewBars_GeneratesZeroTrades()
        {
            // bar 数 < BollingerLength 时,BB 全 NaN,策略 step 跳过 NaN → 零交易,不抛。
            var closes = new double[] { 100, 101, 102, 103, 104 };
            var result = BacktestEngine.Run(MakeInput(closes), new BacktestOptions(BollingerLength: 20));
            Assert.Empty(result.Trades);
            Assert.Equal(5, result.Stats.BarCount);
        }

        [Fact]
        public void Run_BreakoutThenRevert_RecordsOneRoundTripTrade()
        {
            // 构造一个能触发恰好一次"突破上轨买、跌破下轨平"的序列:
            //   - 前 20 根:在 100 附近震荡(让 BB 收敛)
            //   - 第 20 根:跳到 120(突破上轨)→ 第 21 根 open 处买
            //   - 之后回落到 80(跌破下轨)→ 平仓
            var rng = new Random(42);
            var closes = new double[60];
            var opens  = new double[60];
            for (int i = 0; i < 20; i++)
            {
                closes[i] = 100.0 + (rng.NextDouble() - 0.5) * 0.4;  // 极窄震荡
                opens[i]  = closes[i];
            }
            // 第 20 根突破:这一根的 close 突破上轨,真正下单在第 21 根 open(我们指定 110)。
            closes[20] = 120.0;
            opens[20]  = 100.0;
            opens[21]  = 110.0; closes[21] = 112.0;
            // 第 22~30 根继续涨,然后跌破下轨
            for (int i = 22; i < 30; i++) { opens[i] = closes[i] = 112.0 + (i - 22) * 0.5; }
            // 跌破构造:close[30] = 80(远低于 lower);open[31] = 90 用作 fill price。
            closes[30] = 80.0;
            opens[30]  = 100.0;
            opens[31]  = 90.0;  closes[31] = 91.0;
            for (int i = 32; i < 60; i++) { opens[i] = closes[i] = 91.0; }

            var result = BacktestEngine.Run(MakeInput(closes, opens),
                new BacktestOptions(PositionSize: 100));

            Assert.Single(result.Trades);
            var trade = result.Trades[0];
            Assert.Equal(110.0, trade.EntryPrice, precision: 6);   // open[21]
            Assert.Equal(90.0,  trade.ExitPrice,  precision: 6);   // open[31]
            Assert.Equal(100.0, trade.Quantity,   precision: 6);
            Assert.Equal((90.0 - 110.0) * 100.0, trade.PnL, precision: 6);  // -2000
            Assert.True(trade.PnL < 0);
            Assert.Equal(1, result.Stats.TotalTrades);
            Assert.Equal(0, result.Stats.Winners);
            Assert.Equal(1, result.Stats.Losers);
        }

        [Fact]
        public void Run_OpenPositionAtEndOfSeries_ForceLiquidatesAtLastClose()
        {
            // 突破上轨后 close 没再跌破下轨 —— 末根 engine 强制按 close 平仓,trade 列表得记上这一笔,
            // 否则 stats 漏算"目前还浮盈/亏的持仓",FinalEquity ≠ cash + 持仓 mtm。
            var closes = new double[40];
            var opens  = new double[40];
            for (int i = 0; i < 20; i++) { opens[i] = closes[i] = 100.0; }
            closes[20] = 130.0; opens[20] = 100.0;
            // 第 21~39 根都在 130 之上,永远不会破下轨
            for (int i = 21; i < 40; i++) { opens[i] = closes[i] = 130.0 + (i - 21) * 0.1; }

            var result = BacktestEngine.Run(MakeInput(closes, opens),
                new BacktestOptions(PositionSize: 100));

            Assert.Single(result.Trades);
            var trade = result.Trades[0];
            Assert.Equal(130.0, trade.EntryPrice, precision: 6);       // open[21]
            Assert.Equal(closes[39], trade.ExitPrice, precision: 6);   // 强平用 close[39]
            Assert.Equal(trade.ExitTime.Date, trade.EntryTime.Date);   // 同一天的快照
            Assert.True(trade.PnL > 0);
        }

        [Fact]
        public void Run_FillPriceUsesNextBarOpen_NotSignalBarClose()
        {
            // 关键不变式:信号在 close[i] 评估,fill 用 open[i+1]。这条挡住未来函数。
            // 构造:close[20] 巨大突破,但 open[21] 跟它差很多,验证 entry 用 open[21] 不是 close[20]。
            var closes = new double[40];
            var opens  = new double[40];
            for (int i = 0; i < 20; i++) { opens[i] = closes[i] = 100.0; }
            closes[20] = 150.0;     // 突破上轨
            opens[20]  = 100.0;
            opens[21]  = 105.0;     // ← 下一根 open,故意跟 close[20] 差 45 块,看 engine 用谁
            closes[21] = 106.0;
            for (int i = 22; i < 40; i++) { opens[i] = closes[i] = 106.0; }

            var result = BacktestEngine.Run(MakeInput(closes, opens),
                new BacktestOptions(PositionSize: 100));

            Assert.True(result.Trades.Count >= 1);
            Assert.Equal(105.0, result.Trades[0].EntryPrice, precision: 6);  // 必须是 open[21],不是 close[20]
        }

        [Fact]
        public void Run_FinalEquityEqualsInitialCapitalPlusTotalPnL()
        {
            // 不变式:权益 conservation —— FinalEquity = InitialCapital + Σ trade.PnL。
            // 这条挡住"漏算最末根 mark-to-market"或"trade.PnL 计算错引入双倍计费"。
            var rng = new Random(123);
            var closes = new double[200];
            var opens  = new double[200];
            double last = 100.0;
            for (int i = 0; i < 200; i++)
            {
                double r = (rng.NextDouble() - 0.5) * 4.0;
                last += r;
                opens[i]  = last;
                closes[i] = last + (rng.NextDouble() - 0.5) * 0.5;
            }

            var result = BacktestEngine.Run(MakeInput(closes, opens), new BacktestOptions());

            double expected = result.Stats.InitialCapital + result.Trades.Sum(t => t.PnL);
            Assert.Equal(expected, result.Stats.FinalEquity, precision: 4);
        }

        [Fact]
        public void Run_StatsTotalReturnPercent_MatchesPnLOverCapital()
        {
            var rng = new Random(7);
            var closes = Enumerable.Range(0, 100).Select(i => 100.0 + rng.NextDouble() * 10).ToArray();
            var result = BacktestEngine.Run(MakeInput(closes), new BacktestOptions(InitialCapital: 50_000));
            double expected = result.Stats.TotalPnL / 50_000.0 * 100.0;
            Assert.Equal(expected, result.Stats.TotalReturnPercent, precision: 6);
        }

        [Fact]
        public void Run_EquityCurve_HasOnePointPerBar()
        {
            var closes = Enumerable.Range(0, 80).Select(i => 100.0 + i).ToArray();
            var result = BacktestEngine.Run(MakeInput(closes), new BacktestOptions());
            Assert.Equal(80, result.EquityCurve.Count);
            Assert.Equal(closes.Length, result.Stats.BarCount);
        }

        [Fact]
        public void Run_RejectsMismatchedArrayLengths()
        {
            var input = new BacktestInput(
                Times:  new[] { DateTime.Today, DateTime.Today.AddMinutes(1) },
                Opens:  new[] { 100.0, 101.0, 102.0 },   // 故意多一个
                Closes: new[] { 100.0, 101.0 });
            Assert.Throws<ArgumentException>(() =>
                BacktestEngine.Run(input, new BacktestOptions()));
        }

        [Fact]
        public void Run_RejectsBadOptions()
        {
            var input = MakeInput(new[] { 100.0, 101.0 });
            Assert.Throws<ArgumentException>(() => BacktestEngine.Run(input, new BacktestOptions(BollingerLength: 1)));
            Assert.Throws<ArgumentException>(() => BacktestEngine.Run(input, new BacktestOptions(BollingerK: 0)));
            Assert.Throws<ArgumentException>(() => BacktestEngine.Run(input, new BacktestOptions(InitialCapital: 0)));
            Assert.Throws<ArgumentException>(() => BacktestEngine.Run(input, new BacktestOptions(PositionSize: -1)));
        }
    }
}
