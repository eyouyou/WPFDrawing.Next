using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hevo.Trade
{
    /// <summary>
    /// 交易后端抽象 —— 把 broker SDK 的具体实现隔离在独立子项目里,
    /// 调用方只见接口跟 <see cref="NullTradeService"/> 默认 stub。
    ///
    /// <para>
    /// 三类实现(各独立 csproj):
    /// </para>
    /// <list type="bullet">
    ///   <item><b>LocalTradeService</b>(各 broker 一个独立子项目):
    ///         <c>Hevo.Trade.{CTP / Tora / IB / XTP / Mock}</c>。
    ///         直接调 broker 本地 SDK,典型单机部署用。</item>
    ///   <item><b>RemoteTradeService</b>(<c>Hevo.Trade.Remote</c>):
    ///         gRPC client → 后端 <c>TradeService</c> 转发,典型后端模式用。</item>
    ///   <item><b><see cref="NullTradeService"/></b>:默认实现,任何下单调用抛"未配置 backend"
    ///         + 安装指引。</item>
    /// </list>
    ///
    /// <para>
    /// <b>本程序集纯 BCL,不依赖 Hevo.Charting / WPF / 任何图表库</b> ——
    /// 命令行回测 / Web API / Console Trader / 图表都能直接引用。
    /// 跟图表的桥接(<c>TradeServiceTrait</c>)放 Hevo.Charting 主项目内。
    /// </para>
    /// </summary>
    public interface ITradeService
    {
        /// <summary>应用配置并连接 broker。多次调用幂等(已连接直接返回)。</summary>
        void Initialize(TradeServiceOptions options);

        /// <summary>断开连接 + 释放资源。重复调用幂等。</summary>
        void Shutdown();

        /// <summary>
        /// 下单。<paramref name="req"/> 含 ClientOrderId 用作客户端幂等键 ——
        /// 网络重试 / 进程崩溃恢复时同 ClientOrderId 不会被 broker 当成新单。
        /// </summary>
        Task<OrderAck> PlaceOrderAsync(OrderRequest req, CancellationToken ct = default);

        /// <summary>撤单。返回 broker 是否接受撤单请求(不代表已成交撤销,后续走 OrderUpdates 推送)。</summary>
        Task<bool> CancelOrderAsync(string brokerOrderId, CancellationToken ct = default);

        /// <summary>查询账户余额 + 持仓。</summary>
        Task<AccountSnapshot> QueryAccountAsync(CancellationToken ct = default);

        /// <summary>
        /// 订单状态推送流(broker 回报)。订阅式,推 PartialFill / Filled / Cancelled / Rejected 等更新。
        /// 实现:Local 走 broker SDK 的 callback,Remote 走 gRPC server-streaming。
        /// </summary>
        IObservable<OrderUpdate> OrderUpdates { get; }

        /// <summary>broker 是否连接就绪 —— UI 状态指示用。</summary>
        bool IsConnected { get; }
    }

    /// <summary>下单请求 DTO。跨 Local / Remote 实现共享同一份。</summary>
    public sealed record OrderRequest(
        string Symbol,
        Direction Direction,
        OrderType Type,
        double Quantity,
        double LimitPrice,
        string ClientOrderId);

    /// <summary>下单回执 —— broker 接单成功 / 失败的同步响应(后续填单走 OrderUpdates)。</summary>
    public sealed record OrderAck(
        bool Success,
        string BrokerOrderId,
        string? Error);

    /// <summary>订单状态推送 DTO。</summary>
    public sealed record OrderUpdate(
        string BrokerOrderId,
        OrderStatus Status,
        double FilledQty,
        double AvgPrice,
        DateTime Ts);

    /// <summary>账户快照(余额 + 持仓)。</summary>
    public sealed record AccountSnapshot(
        double Balance,
        double Equity,
        IReadOnlyList<Position> Positions);

    /// <summary>单个持仓。</summary>
    public sealed record Position(
        string Symbol,
        double Qty,
        double AvgCost,
        double MarketValue);

    /// <summary>买卖方向。</summary>
    public enum Direction
    {
        Buy = 0,
        Sell = 1,
    }

    /// <summary>订单类型。</summary>
    public enum OrderType
    {
        Market = 0,
        Limit = 1,
    }

    /// <summary>订单状态(broker 回报)。</summary>
    public enum OrderStatus
    {
        /// <summary>已挂单未成交。</summary>
        Pending = 0,
        /// <summary>部分成交。</summary>
        Partial = 1,
        /// <summary>全部成交。</summary>
        Filled = 2,
        /// <summary>已撤单。</summary>
        Cancelled = 3,
        /// <summary>broker 拒单(资金不足 / 超价 / 风控等)。</summary>
        Rejected = 4,
    }

    /// <summary>
    /// 启动 <see cref="ITradeService"/> 时的配置 —— broker 各家自由扩展(端口 / 账号 / 密钥 / 风控参数等)。
    /// 主项目只暴露通用字段,broker 子项目自己派生 record / 在 <see cref="ExtraProperties"/> 里塞自家字段。
    /// </summary>
    public sealed record TradeServiceOptions
    {
        /// <summary>broker 服务端 endpoint(local 走 SDK 内置 / remote 走 gRPC URL)。</summary>
        public string? BrokerEndpoint { get; init; }

        /// <summary>账号。</summary>
        public string? UserId { get; init; }

        /// <summary>密码 / token —— 调用方负责从 secret store 取,**不要硬编码**到蓝图 JSON。</summary>
        public string? Password { get; init; }

        /// <summary>broker 各家专属字段(典型 CTP 的 BrokerId / 投资者代码 / 流水号等)。</summary>
        public IReadOnlyDictionary<string, string>? ExtraProperties { get; init; }

        /// <summary>下单确认前的预校验(资金 / 持仓 / 限额)是否启用。生产推荐 true。</summary>
        public bool EnablePreCheck { get; init; } = true;

        /// <summary>单笔下单 broker SDK 调用超时(毫秒)。</summary>
        public int CallTimeoutMs { get; init; } = 5000;
    }
}
