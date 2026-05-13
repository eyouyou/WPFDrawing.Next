using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hevo.Trade
{
    /// <summary>
    /// 默认 <see cref="ITradeService"/> 实现 —— 任何下单调用立即抛 + 提示安装路径。
    /// 调用方不显式启用 broker = 蓝图能跑(信号继续算)但不真实下单。
    ///
    /// <para>
    /// 设计意图:让"框架可独立运行 / 业务按需启用 broker"成立。
    /// 跟 Hevo.Charting 的 NullPythonRuntime 同模式。
    /// </para>
    /// </summary>
    public sealed class NullTradeService : ITradeService
    {
        public static readonly NullTradeService Instance = new();

        public bool IsConnected => false;

        public IObservable<OrderUpdate> OrderUpdates { get; } = new EmptyObservable<OrderUpdate>();

        private static InvalidOperationException NotConfigured() => new(
            "Trade backend 未配置。要使用交易功能,请:\n" +
            "1) 引一个 broker 子项目(典型:Hevo.Trade.Mock 跑回测,或 Hevo.Trade.{CTP / Tora / IB / XTP} 接真实 broker);\n" +
            "2) new XxxTradeService() + Initialize(options) 启动连接;\n" +
            "3) 业务侧 BlueprintLauncher.LaunchEx(blueprint, ..., trade: yourTradeService) 注入。");

        public void Initialize(TradeServiceOptions options) { /* 幂等空操作 */ }

        public void Shutdown() { /* 幂等空操作,允许业务方不区分启用/未启用都 finally 调一次 */ }

        public Task<OrderAck> PlaceOrderAsync(OrderRequest req, CancellationToken ct = default)
            => throw NotConfigured();

        public Task<bool> CancelOrderAsync(string brokerOrderId, CancellationToken ct = default)
            => throw NotConfigured();

        public Task<AccountSnapshot> QueryAccountAsync(CancellationToken ct = default)
            => throw NotConfigured();

        // 空 IObservable —— 没有订单推送,Subscribe 立即 OnCompleted。
        private sealed class EmptyObservable<T> : IObservable<T>
        {
            public IDisposable Subscribe(IObserver<T> observer)
            {
                observer.OnCompleted();
                return EmptyDisposable.Instance;
            }
        }

        private sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
