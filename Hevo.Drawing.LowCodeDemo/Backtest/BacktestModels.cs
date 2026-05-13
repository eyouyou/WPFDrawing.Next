using System;
using System.Collections.Generic;

namespace Hevo.Drawing.LowCodeDemo.Backtest
{
    /// <summary>
    /// §回测 输入 ── 从任何 OHLC 时间序列(MockKLineDataSource / 真实行情 / CSV 都行)拼出来即可。
    /// 三组数组必须等长且按时间升序;不做内部校验,违反时 <see cref="BacktestEngine.Run"/> fail-fast。
    /// </summary>
    /// <param name="Times">每根 bar 的时间戳(用于 Trade.EntryTime / ExitTime + EquityCurve.X 显示)。</param>
    /// <param name="Opens">每根 bar 的开盘价(策略信号在 close[i] 触发,真正 fill 价用 open[i+1] —— 避免未来函数)。</param>
    /// <param name="Closes">每根 bar 的收盘价(BB 计算 + 信号判定 + 持仓 mark-to-market 用)。</param>
    public sealed record BacktestInput(
        IReadOnlyList<DateTime> Times,
        IReadOnlyList<double>   Opens,
        IReadOnlyList<double>   Closes);

    /// <summary>
    /// §回测 参数 —— 业务用户可调 4 个旋钮。默认值跟
    /// <c>PyIndicators/bb_breakout_strategy.py</c> 内置策略 100% 对齐
    /// (length=20, k=2.0, qty=100),保证 demo "图上箭头 ↔ 回测交易" 视觉对应得上。
    /// </summary>
    /// <param name="InitialCapital">起始资金(美元 / 元随便)。绝对值仅影响 equity 曲线刻度,百分比指标无关。</param>
    /// <param name="PositionSize">每笔下单手数 / 股数。简化模型:固定仓位、不复利、不 size-by-volatility。</param>
    /// <param name="BollingerLength">BB 移动平均长度。<paramref name="Closes"/> 数量 &lt; 该值时全程不产生信号。</param>
    /// <param name="BollingerK">BB 上下轨 σ 倍数。典型 1.5 / 2.0 / 2.5。</param>
    public sealed record BacktestOptions(
        double InitialCapital  = 100_000.0,
        double PositionSize    = 100.0,
        int    BollingerLength = 20,
        double BollingerK      = 2.0);

    /// <summary>单笔已平仓交易记录 —— 入场出场配对。开放头寸(回测结束仍持仓)按末根收盘强平,也落这条。</summary>
    public sealed record Trade(
        DateTime EntryTime,
        double   EntryPrice,
        DateTime ExitTime,
        double   ExitPrice,
        double   Quantity,
        double   PnL,
        double   PnLPercent);

    /// <summary>
    /// 权益曲线一个点 —— 每根 bar 末打一个(cash + 持仓 mark-to-market)。
    /// <para>
    /// 用 sealed record class 而非 record struct,迁就 demo csproj 的 <c>LangVersion=9.0</c>
    /// (record struct 是 C# 10+)。每条 alloc ~32B,1000 根 bar ~32KB,demo 规模零压力。
    /// </para>
    /// </summary>
    public sealed record EquityPoint(DateTime Time, double Equity);

    /// <summary>
    /// 一次回测全部统计汇总 —— UI 顶部那条横幅就读这俩字段集。
    /// 所有百分比字段单位 = "%"(已 ×100),不是小数,UI 直接 F2 显示即可。
    /// </summary>
    public sealed record BacktestStats(
        double InitialCapital,
        double FinalEquity,
        double TotalPnL,
        double TotalReturnPercent,
        int    TotalTrades,
        int    Winners,
        int    Losers,
        double WinRatePercent,
        double MaxDrawdownPercent,
        double Sharpe,
        int    BarCount);

    /// <summary>一次回测的最终产物 —— 喂给 <c>BacktestView</c> 三个区域分别渲染。</summary>
    public sealed record BacktestResult(
        BacktestStats Stats,
        IReadOnlyList<Trade> Trades,
        IReadOnlyList<EquityPoint> EquityCurve);
}
