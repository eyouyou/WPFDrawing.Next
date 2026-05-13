using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hevo.Trade.Mock
{
    /// <summary>
    /// <see cref="ITradeService"/> 的内存撮合实装 —— 用于回测 / e2e 测试 / 离线开发,
    /// **不连任何真实 broker**。下单立即"成交"(MARKET)或挂在内存里(LIMIT),
    /// 状态推送通过 <see cref="OrderUpdates"/> 走 <see cref="System.IObservable{T}"/>。
    ///
    /// <para>
    /// 设计目标:让 §D3.13 全本地形态在没有真实 broker SDK 的情况下也能跑通端到端 demo。
    /// 真实生产用 <c>Hevo.Trade.{CTP / Tora / IB / XTP}</c> 或 <c>Hevo.Trade.Remote</c>。
    /// </para>
    ///
    /// <para>
    /// <b>幂等保证</b>:同 <see cref="OrderRequest.ClientOrderId"/> 重复调
    /// <see cref="PlaceOrderAsync"/> 返回同一个 BrokerOrderId,不会重复下单。
    /// </para>
    /// </summary>
    public sealed class MockTradeService : ITradeService
    {
        private readonly ConcurrentDictionary<string, MockOrder> _orders = new();      // brokerOrderId → order
        private readonly ConcurrentDictionary<string, string>     _clientToBroker = new();   // clientOrderId → brokerOrderId
        private readonly ConcurrentDictionary<string, double> _positions = new();      // symbol → qty
        private readonly OrderUpdateSubject _subject = new();
        private TradeServiceOptions _options = new();
        private double _balance = 1_000_000;     // mock 初始 100w
        private long _seq;
        private bool _initialized;

        public bool IsConnected => _initialized;

        public IObservable<OrderUpdate> OrderUpdates => _subject;

        public void Initialize(TradeServiceOptions options)
        {
            _options = options ?? new TradeServiceOptions();
            _initialized = true;
        }

        public void Shutdown()
        {
            _initialized = false;
            _subject.OnCompleted();
        }

        public Task<OrderAck> PlaceOrderAsync(OrderRequest req, CancellationToken ct = default)
        {
            EnsureInitialized();

            // 幂等:同 ClientOrderId 已下过 → 返回原 ack。
            if (!string.IsNullOrEmpty(req.ClientOrderId)
                && _clientToBroker.TryGetValue(req.ClientOrderId, out var existingBroker))
            {
                return Task.FromResult(new OrderAck(true, existingBroker, null));
            }

            // 简单 pre-check:Market 单需粗略检查"够不够买" / "够不够卖"。
            if (_options.EnablePreCheck && req.Type == OrderType.Market)
            {
                if (req.Direction == Direction.Buy && req.Quantity * req.LimitPrice > _balance && req.LimitPrice > 0)
                {
                    return Task.FromResult(new OrderAck(false, "", "余额不足"));
                }
                if (req.Direction == Direction.Sell &&
                    (!_positions.TryGetValue(req.Symbol, out var pos) || pos < req.Quantity))
                {
                    return Task.FromResult(new OrderAck(false, "", $"{req.Symbol} 持仓不足"));
                }
            }

            var brokerOrderId = $"MOCK-{Interlocked.Increment(ref _seq):D8}";
            var order = new MockOrder
            {
                BrokerOrderId = brokerOrderId,
                Request = req,
                Status = OrderStatus.Pending,
            };
            _orders[brokerOrderId] = order;
            if (!string.IsNullOrEmpty(req.ClientOrderId)) _clientToBroker[req.ClientOrderId] = brokerOrderId;

            // Push Pending 状态;Market 单立即填单成 Filled,Limit 单留在内存待撤 / 推送(简化:仍 Pending)。
            _subject.OnNext(new OrderUpdate(brokerOrderId, OrderStatus.Pending, 0, 0, DateTime.UtcNow));

            if (req.Type == OrderType.Market)
            {
                FillOrderAtMarket(order);
            }

            return Task.FromResult(new OrderAck(true, brokerOrderId, null));
        }

        public Task<bool> CancelOrderAsync(string brokerOrderId, CancellationToken ct = default)
        {
            EnsureInitialized();
            if (!_orders.TryGetValue(brokerOrderId, out var order)) return Task.FromResult(false);
            if (order.Status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected)
                return Task.FromResult(false);
            order.Status = OrderStatus.Cancelled;
            _subject.OnNext(new OrderUpdate(brokerOrderId, OrderStatus.Cancelled,
                order.FilledQty, order.AvgPrice, DateTime.UtcNow));
            return Task.FromResult(true);
        }

        public Task<AccountSnapshot> QueryAccountAsync(CancellationToken ct = default)
        {
            EnsureInitialized();
            var positions = new List<Position>();
            foreach (var (symbol, qty) in _positions)
            {
                if (qty == 0) continue;
                positions.Add(new Position(symbol, qty, AvgCost: 0, MarketValue: 0));
            }
            return Task.FromResult(new AccountSnapshot(_balance, _balance, positions));
        }

        // ── 内部撮合 ────────────────────────────────────────────────────────────────

        private void FillOrderAtMarket(MockOrder order)
        {
            // mock 价格:LimitPrice(若提供) > 0,否则用 1 (待业务侧扩展接行情快照)
            var fillPrice = order.Request.LimitPrice > 0 ? order.Request.LimitPrice : 1.0;
            var qty = order.Request.Quantity;

            order.FilledQty = qty;
            order.AvgPrice = fillPrice;
            order.Status = OrderStatus.Filled;

            // 更新持仓与余额
            var sign = order.Request.Direction == Direction.Buy ? 1 : -1;
            _positions.AddOrUpdate(order.Request.Symbol,
                addValue:    sign * qty,
                updateValueFactory: (_, prev) => prev + sign * qty);
            _balance -= sign * qty * fillPrice;

            _subject.OnNext(new OrderUpdate(order.BrokerOrderId, OrderStatus.Filled,
                qty, fillPrice, DateTime.UtcNow));
        }

        private void EnsureInitialized()
        {
            if (!_initialized) throw new InvalidOperationException(
                "MockTradeService 未 Initialize。请先调 Initialize(options) 再下单。");
        }

        // ── 内部类型 ────────────────────────────────────────────────────────────────

        private sealed class MockOrder
        {
            public string BrokerOrderId { get; init; } = "";
            public OrderRequest Request { get; init; } = null!;
            public OrderStatus Status { get; set; }
            public double FilledQty { get; set; }
            public double AvgPrice { get; set; }
        }

        // 极简 Subject —— 不引 System.Reactive,自带轻量 Observable。生产场景换 Subject<T>。
        private sealed class OrderUpdateSubject : IObservable<OrderUpdate>
        {
            private readonly object _lock = new();
            private readonly List<IObserver<OrderUpdate>> _observers = new();
            private bool _completed;

            public IDisposable Subscribe(IObserver<OrderUpdate> observer)
            {
                lock (_lock)
                {
                    if (_completed) { observer.OnCompleted(); return new Empty(); }
                    _observers.Add(observer);
                }
                return new Subscription(this, observer);
            }

            public void OnNext(OrderUpdate u)
            {
                IObserver<OrderUpdate>[] snap;
                lock (_lock)
                {
                    if (_completed) return;
                    snap = _observers.ToArray();
                }
                foreach (var o in snap)
                {
                    try { o.OnNext(u); } catch { /* 隔离观察者异常 */ }
                }
            }

            public void OnCompleted()
            {
                IObserver<OrderUpdate>[] snap;
                lock (_lock)
                {
                    _completed = true;
                    snap = _observers.ToArray();
                    _observers.Clear();
                }
                foreach (var o in snap)
                {
                    try { o.OnCompleted(); } catch { /* swallow */ }
                }
            }

            private void Remove(IObserver<OrderUpdate> observer)
            {
                lock (_lock) { _observers.Remove(observer); }
            }

            private sealed class Subscription : IDisposable
            {
                private OrderUpdateSubject? _subject;
                private IObserver<OrderUpdate>? _observer;
                public Subscription(OrderUpdateSubject s, IObserver<OrderUpdate> o) { _subject = s; _observer = o; }
                public void Dispose()
                {
                    var s = _subject; var o = _observer;
                    _subject = null; _observer = null;
                    if (s != null && o != null) s.Remove(o);
                }
            }

            private sealed class Empty : IDisposable { public void Dispose() { } }
        }
    }
}
