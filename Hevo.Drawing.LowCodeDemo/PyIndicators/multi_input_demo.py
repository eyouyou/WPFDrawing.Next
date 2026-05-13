"""§D2.X 多输入指标 demo —— ATR / MFI / VWAP / OBV。
分析师写多输入指标的标准模板:声明 inputs=[...],函数形参顺序对齐。
"""

from hevo_indicators import register, ta


@register('atr_14',
          signature='(ReadOnlyMemory[double], ReadOnlyMemory[double], ReadOnlyMemory[double]) -> ReadOnlyMemory[double]',
          inputs=['high', 'low', 'close'])
def atr_14(high, low, close):
    return ta.atr(high, low, close, length=14)


@register('mfi_14',
          signature='(ReadOnlyMemory[double], ReadOnlyMemory[double], ReadOnlyMemory[double], ReadOnlyMemory[double]) -> ReadOnlyMemory[double]',
          inputs=['high', 'low', 'close', 'volume'])
def mfi_14(high, low, close, volume):
    return ta.mfi(high, low, close, volume, length=14)


@register('vwap_session',
          signature='(ReadOnlyMemory[double], ReadOnlyMemory[double], ReadOnlyMemory[double], ReadOnlyMemory[double]) -> ReadOnlyMemory[double]',
          inputs=['high', 'low', 'close', 'volume'])
def vwap_session(high, low, close, volume):
    return ta.vwap(high, low, close, volume)


@register('obv',
          signature='(ReadOnlyMemory[double], ReadOnlyMemory[double]) -> ReadOnlyMemory[double]',
          inputs=['close', 'volume'])
def obv(close, volume):
    return ta.obv(close, volume)
