"""§D2.X 完整策略 demo —— 布林带突破 + 多渠道通知 + 自动下单。

一个 .py 文件同时演示:
  ① @indicator + @register 画图(BB 上中下三线 + 买卖箭头 + BUY/SELL 文字)
  ② trade.place_order 下单(突破上轨买,破下轨卖)
  ③ send_email / send_dingtalk / send_webhook / send_sms / log_alert 多渠道通知

PyBusinessBridge.RegisterBuiltin 把 5 个通知 builtin 注入 __main__ 模块(不是 Python 自带的
builtins 模块)。此 .py 作为独立模块被 import 时,__main__ 跟本模块 globals 是两个 namespace,
不会自动看见名字。下方 _call_main 每次 late-binding 去 __main__ 取 —— 同时解决「模块 import 时
builtin 尚未注入」的时序问题,顺序鲁棒。
"""

import numpy as np
import __main__
from hevo_indicators import register, indicator, ta, trade


# PyBusinessBridge.RegisterBuiltin 把 5 个 builtin 注入 __main__ 模块。
# 此 .py 作为独立模块被 import 时,__main__ 跟本模块 globals 是两个 namespace,不会自动看见名字。
# 解法:每次触发时 late-binding 去 __main__ 取 —— 同时解决「模块 import 时 builtin 尚未注入」的时序问题。
def _call_main(name: str, payload) -> None:
    fn = getattr(__main__, name, None)
    if fn is None:
        return
    try:
        fn(payload)
    except Exception:
        pass  # builtin 内部异常不应该让策略主流程炸


# 模块级状态 —— 持久化已触发的信号位,避免每 tick 重发副作用(下单 / 邮件等)。
# 用 (bar_index, 'buy'|'sell') 元组做 key。MockKLineDataSource.Reset() 后 close 数组从头开始,
# n 突然变小 → 检测到 reset → 清空已触发集合(等价于换了新 timeline,过去的不算数)。
_bb_already_fired = set()
_bb_last_n = 0


@indicator('bb_breakout', overlay=True, series=[
    ('upper',        'line',          '#EF5350', 1.5),
    ('middle',       'line',          '#FFB74D', 2.0),
    ('lower',        'line',          '#EF5350', 1.5),
    ('signals_buy',  'arrow_markers', '#66BB6A', 0.0),
    ('signals_sell', 'arrow_markers', '#EF5350', 0.0),
    ('labels_buy',   'text_markers',  '#66BB6A', 0.0),
    ('labels_sell',  'text_markers',  '#EF5350', 0.0),
])
@register('bb_breakout', signature='(ReadOnlyMemory[double]) -> object')
def bb_breakout(close):
    global _bb_already_fired, _bb_last_n
    arr = np.asarray(close, dtype=np.float64)
    n = arr.size

    # 检测 timeline reset(MockKLineDataSource.Reset 后 OnFetchAsync 重新 seed,n 大幅缩水)
    if n < _bb_last_n:
        _bb_already_fired = set()
    _bb_last_n = n

    upper  = ta.bb_upper(arr,  length=20, k=2.0)
    middle = ta.bb_middle(arr, length=20)
    lower  = ta.bb_lower(arr,  length=20, k=2.0)

    # 全历史扫描:任何破上/下轨的 bar 都登记箭头 + 文字标签,持久化显示在图上,
    # 不会因为 viewport 滚动就消失。
    #
    # 副作用(下单 / 邮件 / 钉钉 等)只在「末根 + 真正首次发现」时 fire ——
    # 历史 bar 的突破事件早就过去了,反向触发邮件下单无业务意义。
    # last_idx = n - 1;每次 tick 只 fire 这一根的新事件,
    # _bb_already_fired 兜底防同一根多次触发(re-render 时 indicator 可能重入)。
    signals_buy = []
    signals_sell = []
    labels_buy = []
    labels_sell = []
    last_idx = n - 1
    for i in range(20, n):
        if np.isnan(upper[i]) or np.isnan(lower[i]):
            continue
        price = float(arr[i])

        if price > upper[i]:
            # 箭头朝下,从上方指向 K 线 —— 绿色买入 + BUY 文字在箭头上方
            signals_buy.append({
                'logical_x': float(i),
                'logical_y': price,
                'direction': 'down',
                'color':     '#66BB6A',
                'size':      14.0,
            })
            labels_buy.append({
                'logical_x': float(i),
                'logical_y': price,
                'text':      'BUY',
                'color':     '#66BB6A',
                'font_size': 11.0,
                'anchor':    'above',
            })
            key = (i, 'buy')
            if i == last_idx and key not in _bb_already_fired:
                _bb_already_fired.add(key)
                _fire_buy(price, float(upper[i]))

        elif price < lower[i]:
            # 箭头朝上,从下方指向 K 线 —— 红色卖出 + SELL 文字在箭头下方
            signals_sell.append({
                'logical_x': float(i),
                'logical_y': price,
                'direction': 'up',
                'color':     '#EF5350',
                'size':      14.0,
            })
            labels_sell.append({
                'logical_x': float(i),
                'logical_y': price,
                'text':      'SELL',
                'color':     '#EF5350',
                'font_size': 11.0,
                'anchor':    'below',
            })
            key = (i, 'sell')
            if i == last_idx and key not in _bb_already_fired:
                _bb_already_fired.add(key)
                _fire_sell(price, float(lower[i]))

    return {
        'upper':        upper,
        'middle':       middle,
        'lower':        lower,
        'signals_buy':  signals_buy,
        'signals_sell': signals_sell,
        'labels_buy':   labels_buy,
        'labels_sell':  labels_sell,
    }


def _fire_buy(price: float, upper_band: float) -> None:
    cid = 'bb-buy-{0:.2f}'.format(price)
    # ① 下单 —— mock TradeService 返 OrderAck(BrokerOrderId / Success)
    broker_id = ''
    success = False
    if trade is not None:
        try:
            ack = trade.place_order(symbol='DEMO', direction='buy',
                                    order_type='market', quantity=100,
                                    limit_price=price, client_order_id=cid)
            broker_id = str(getattr(ack, 'BrokerOrderId', '') or '')
            success = bool(getattr(ack, 'Success', False))
        except Exception as ex:
            _call_main('log_alert', {'level':'ERROR','kind':'order','msg':'place_order failed: {0}'.format(ex)})

    # ② 邮件
    _call_main('send_email', {
        'subject': '[BB突破] 买入 DEMO @ {0:.2f}'.format(price),
        'body':    '布林上轨={0:.3f},现价{1:.3f} 突破。已下市价单 100 股,broker_id={2},success={3}'.format(
                       upper_band, price, broker_id, success),
        'to':      'analyst@team.com',
    })
    # ③ 钉钉
    _call_main('send_dingtalk', {
        'title': 'BB 突破买入',
        'text':  '**DEMO** 突破上轨 @ **{0:.2f}** (上轨 {1:.2f})'.format(price, upper_band),
        'at_mobiles': ['13800138000'],
    })
    # ④ Webhook(业务系统接 PagerDuty / Slack / 自建告警网关)
    _call_main('send_webhook', {
        'url':  'https://hooks.internal/strategy/bb',
        'json': {'symbol':'DEMO','action':'buy','price':price,'upper':upper_band,'client_order_id':cid},
    })
    # ⑤ 落本地告警日志(UI 通知中心展示)
    _call_main('log_alert', {'level':'INFO','kind':'signal','msg':'BB 上轨突破 @ {0:.2f},挂单 cid={1}'.format(price, cid)})


def _fire_sell(price: float, lower_band: float) -> None:
    cid = 'bb-sell-{0:.2f}'.format(price)
    broker_id = ''
    success = False
    if trade is not None:
        try:
            ack = trade.place_order(symbol='DEMO', direction='sell',
                                    order_type='market', quantity=100,
                                    limit_price=price, client_order_id=cid)
            broker_id = str(getattr(ack, 'BrokerOrderId', '') or '')
            success = bool(getattr(ack, 'Success', False))
        except Exception as ex:
            _call_main('log_alert', {'level':'ERROR','kind':'order','msg':'place_order failed: {0}'.format(ex)})

    _call_main('send_email', {
        'subject': '[BB突破] 卖出 DEMO @ {0:.2f}'.format(price),
        'body':    '布林下轨={0:.3f},现价{1:.3f} 跌破。已下市价单 -100 股,broker_id={2},success={3}'.format(
                       lower_band, price, broker_id, success),
        'to':      'analyst@team.com',
    })
    _call_main('send_dingtalk', {
        'title': 'BB 跌破卖出',
        'text':  '**DEMO** 跌破下轨 @ **{0:.2f}** (下轨 {1:.2f})'.format(price, lower_band),
        'at_mobiles': ['13800138000'],
    })
    _call_main('send_sms', {
        'phone': '13800138000',
        'text':  '[策略] DEMO 跌破下轨 @ {0:.2f},已平仓 100 股'.format(price),
    })
    _call_main('send_webhook', {
        'url':  'https://hooks.internal/strategy/bb',
        'json': {'symbol':'DEMO','action':'sell','price':price,'lower':lower_band,'client_order_id':cid},
    })
    _call_main('log_alert', {'level':'WARN','kind':'signal','msg':'BB 下轨跌破 @ {0:.2f},挂单 cid={1}'.format(price, cid)})
