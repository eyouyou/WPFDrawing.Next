using System;
using System.Threading;
using System.Threading.Tasks;
using Hevo.Trade;

namespace Hevo.Drawing.LowCodeDemo
{
    /// <summary>
    /// <see cref="ITradeService"/> 装饰器 —— 在每次 <see cref="PlaceOrderAsync"/> 成功 ack 后,
    /// 把 (BrokerOrderId → OrderRequest) 记到 <see cref="MockNotifier"/> 的元数据缓存,
    /// 让随后到达的 <see cref="OrderUpdate"/>(只带 BrokerOrderId / FilledQty / AvgPrice / Status)
    /// 能反查出 symbol / direction / qty,从而在通知中心呈现完整可读的订单事件。
    ///
    /// <para>
    /// 透传所有其他接口(Cancel / QueryAccount / OrderUpdates / IsConnected) —— 对 Python facade 无感。
    /// Python 端 <c>trade.place_order(...)</c> 拿到的还是原 <see cref="OrderAck"/>。
    /// </para>
    /// </summary>
    internal sealed class RecordingTradeService : ITradeService
    {
        private readonly ITradeService _inner;

        public RecordingTradeService(ITradeService inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public void Initialize(TradeServiceOptions options) => _inner.Initialize(options);
        public void Shutdown() => _inner.Shutdown();
        public bool IsConnected => _inner.IsConnected;
        public IObservable<OrderUpdate> OrderUpdates => _inner.OrderUpdates;

        public async Task<OrderAck> PlaceOrderAsync(OrderRequest req, CancellationToken ct = default)
        {
            var ack = await _inner.PlaceOrderAsync(req, ct).ConfigureAwait(false);
            if (ack.Success && !string.IsNullOrEmpty(ack.BrokerOrderId))
            {
                MockNotifier.RecordOrderRequest(ack.BrokerOrderId, req);
            }
            return ack;
        }

        public Task<bool> CancelOrderAsync(string brokerOrderId, CancellationToken ct = default)
            => _inner.CancelOrderAsync(brokerOrderId, ct);

        public Task<AccountSnapshot> QueryAccountAsync(CancellationToken ct = default)
            => _inner.QueryAccountAsync(ct);
    }
}
