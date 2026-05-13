"""复杂多指标:RSI + MACD + Bollinger Bands + 综合打分。
分析师场景模板 —— 用 numpy 写主体,签名声明给 .NET 侧推断委托类型。
"""

import numpy as np
from hevo_indicators import register


# ──────── 内部工具 ──────────────────────────────────────────────────────

def _ema(arr: np.ndarray, length: int) -> np.ndarray:
    n = arr.size
    out = np.empty(n, dtype=np.float64)
    if n == 0:
        return out
    alpha = 2.0 / (length + 1.0)
    out[0] = arr[0]
    for i in range(1, n):
        out[i] = alpha * arr[i] + (1.0 - alpha) * out[i - 1]
    return out


def _rolling_std(arr: np.ndarray, length: int) -> np.ndarray:
    n = arr.size
    out = np.full(n, np.nan, dtype=np.float64)
    if n < length:
        return out
    cs1 = np.cumsum(arr, dtype=np.float64)
    cs2 = np.cumsum(arr * arr, dtype=np.float64)
    sum1 = np.empty(n - length + 1)
    sum2 = np.empty(n - length + 1)
    sum1[0] = cs1[length - 1]
    sum2[0] = cs2[length - 1]
    sum1[1:] = cs1[length:] - cs1[:n - length]
    sum2[1:] = cs2[length:] - cs2[:n - length]
    mean = sum1 / length
    var = np.maximum(sum2 / length - mean * mean, 0.0)
    out[length - 1:] = np.sqrt(var)
    return out


def _rolling_mean(arr: np.ndarray, length: int) -> np.ndarray:
    n = arr.size
    out = np.full(n, np.nan, dtype=np.float64)
    if n < length:
        return out
    cs = np.cumsum(arr, dtype=np.float64)
    out[length - 1] = cs[length - 1] / length
    out[length:] = (cs[length:] - cs[:n - length]) / length
    return out


# ──────── ① RSI(14)Wilder 平滑 ────────────────────────────────────────

@register('rsi_14', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
def rsi_14(close):
    arr = np.asarray(close, dtype=np.float64)
    n = arr.size
    out = np.full(n, np.nan, dtype=np.float64)
    if n < 15:
        return out

    delta = np.diff(arr)
    gain = np.where(delta > 0, delta, 0.0)
    loss = np.where(delta < 0, -delta, 0.0)

    avg_g = np.empty(n - 1, dtype=np.float64)
    avg_l = np.empty(n - 1, dtype=np.float64)
    avg_g[:14] = gain[:14].mean()
    avg_l[:14] = loss[:14].mean()
    for i in range(14, n - 1):
        avg_g[i] = (avg_g[i - 1] * 13 + gain[i]) / 14.0
        avg_l[i] = (avg_l[i - 1] * 13 + loss[i]) / 14.0

    denom = np.where(avg_l[13:] == 0.0, 1e-12, avg_l[13:])
    rs = avg_g[13:] / denom
    out[14:] = 100.0 - 100.0 / (1.0 + rs)
    return out


# ──────── ② MACD histogram ─────────────────────────────────────────────

@register('macd_hist', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
def macd_hist(close):
    arr = np.asarray(close, dtype=np.float64)
    n = arr.size
    if n < 26:
        return np.full(n, np.nan, dtype=np.float64)
    ema12 = _ema(arr, 12)
    ema26 = _ema(arr, 26)
    macd = ema12 - ema26
    signal = _ema(macd, 9)
    hist = macd - signal
    hist[:26] = np.nan
    return hist


# ──────── ③ Bollinger Bands ────────────────────────────────────────────

@register('bollinger_upper', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
def bollinger_upper(close):
    arr = np.asarray(close, dtype=np.float64)
    return _rolling_mean(arr, 20) + 2.0 * _rolling_std(arr, 20)


@register('bollinger_lower', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
def bollinger_lower(close):
    arr = np.asarray(close, dtype=np.float64)
    return _rolling_mean(arr, 20) - 2.0 * _rolling_std(arr, 20)


# ──────── ④ 综合信号 ───────────────────────────────────────────────────

@register('combined_signal', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
def combined_signal(close):
    arr = np.asarray(close, dtype=np.float64)
    n = arr.size
    out = np.full(n, np.nan, dtype=np.float64)
    if n < 26:
        return out

    rsi = rsi_14(arr)
    hist = macd_hist(arr)
    upper = bollinger_upper(arr)
    lower = bollinger_lower(arr)

    rsi_v = np.where(rsi < 30.0, 1.0, np.where(rsi > 70.0, -1.0, 0.0))
    macd_v = np.where(hist > 0.0, 1.0, np.where(hist < 0.0, -1.0, 0.0))
    bb_v = np.where(arr > upper, -1.0,
            np.where(arr < lower, 1.0, 0.0))

    valid = (~np.isnan(rsi)) & (~np.isnan(hist)) & (~np.isnan(upper))
    out[valid] = (rsi_v[valid] + macd_v[valid] + bb_v[valid]) / 3.0
    return out
