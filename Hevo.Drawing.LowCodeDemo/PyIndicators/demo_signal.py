"""Smoke test 用信号 handler:输入末值 > 100 → 下一笔 mock 单。"""
from hevo_indicators import register, trade

@register('place_demo_order', signature='(ReadOnlyMemory[double]) -> None')
def place_demo_order(prices):
    if prices is None or len(prices) == 0:
        return
    last = float(prices[-1])
    if last <= 100:
        return
    if trade is None:
        raise RuntimeError('trade backend 未注入 — UseTradeService 没调')
    ack = trade.place_order(
        symbol='DEMO',
        direction='buy',
        order_type='market',
        quantity=10,
        limit_price=last,
        client_order_id='demo-{0:.0f}'.format(last),
    )
