"""§D2.6 Pine 风味 plot DSL demo —— SMA 三连发(20 / 60 / 120)。

@indicator 装饰器:声明 series 元数据(渲染规格,蓝图侧 PlotFeature 据此构造 layers)
@register   装饰器:声明 .NET 委托名(蓝图侧 ResolveHandlerReferences 翻成 Indicator 委托)

两个装饰器堆叠,装饰同一个函数 —— 一个 .py 文件就够分析师交付一个完整指标(算 + 渲染)。
"""

from hevo_indicators import register, indicator, ta


@indicator('pine_triple_ma', overlay=True, series=[
    ('sma20',  'line', '#EF5350', 2.5),
    ('sma60',  'line', '#CE93D8', 2.5),
    ('sma120', 'line', '#26C6DA', 2.5),
])
@register('pine_triple_ma', signature='(ReadOnlyMemory[double]) -> object')
def pine_triple_ma(close):
    return {
        'sma20':  ta.sma(close, length=20),
        'sma60':  ta.sma(close, length=60),
        'sma120': ta.sma(close, length=120),
    }


@indicator('pine_bb_dual', overlay=True, series=[
    ('upper',  'line', '#EF5350', 1.5),
    ('middle', 'line', '#FFB74D', 2.0),
    ('lower',  'line', '#EF5350', 1.5),
])
@register('pine_bb_dual', signature='(ReadOnlyMemory[double]) -> object')
def pine_bb_dual(close):
    return {
        'upper':  ta.bb_upper(close,  length=20, k=2.0),
        'middle': ta.bb_middle(close, length=20),
        'lower':  ta.bb_lower(close,  length=20, k=2.0),
    }


# 混合 line + bar 渲染:MACD 线 + 信号线 + 柱状直方图
# bar 渲染验证 PlotFeature 的 BarLayer 路径(line 路径 pine_triple_ma 已经验证)
@indicator('pine_macd', overlay=False, series=[
    ('macd',   'line', '#FFB74D', 2.0),
    ('signal', 'line', '#4FC3F7', 2.0),
    ('hist',   'bar',  '#66BB6A', 0.6),
])
@register('pine_macd', signature='(ReadOnlyMemory[double]) -> object')
def pine_macd(close):
    return {
        'macd':   ta.macd_line(close,   fast=12, slow=26),
        'signal': ta.macd_signal(close, fast=12, slow=26, signal=9),
        'hist':   ta.macd_hist(close,   fast=12, slow=26, signal=9),
    }
