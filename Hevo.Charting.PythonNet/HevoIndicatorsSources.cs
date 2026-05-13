namespace Hevo.Charting.PythonNet
{
    /// <summary>
    /// §D2.5.6 hevo_indicators Python 端运行时包的源码 —— 直接以 raw string literal 内嵌,
    /// 避开 .NET embedded-resource 命名编码(<c>__init__.py</c> 这种以 <c>_</c> 开头文件名在 manifest 里命名规则不稳)。
    /// 启动时 <see cref="HevoIndicatorsBootstrap.EnsureInstalled"/> 把这些字符串写到本地 TEMP 目录下的
    /// <c>hevo_indicators/</c> 包,并把父目录加进 sys.path。
    ///
    /// <para>
    /// <b>包结构</b>(参 §D2.5.6 / §D2.X 设计):
    /// </para>
    /// <list type="bullet">
    ///   <item><c>__init__.py</c> —— @register / @indicator 装饰器 + 模块级 _hevo_handlers 字典 + trade 全局占位</item>
    ///   <item><c>_trade.py</c> —— _TradeFacade 类:把 ITradeService 包成 snake_case 同步 Python API</item>
    ///   <item><c>_common.py</c> —— 公共工具:numpy 数组/滚动窗 utility + <c>@as_arrays</c> / <c>@pta_polyfill</c> 装饰器 + 共享 pandas_ta singleton</item>
    ///   <item><c>_ta_ma.py</c> —— 移动平均族(SMA / EMA / WMA / DEMA / TEMA)</item>
    ///   <item><c>_ta_momentum.py</c> —— 动量族(RSI / ROC / MOM / MACD 三件套)</item>
    ///   <item><c>_ta_volatility.py</c> —— 波动率族(stdev / Bollinger Bands)</item>
    ///   <item><c>_ta_multi.py</c> —— §D2.X 多输入指标(true_range / ATR / MFI / VWAP / OBV)</item>
    ///   <item><c>ta.py</c> —— 公共 API hub,从各族子模块 re-export,旧代码 <c>from hevo_indicators import ta; ta.sma(...)</c> 不变</item>
    /// </list>
    ///
    /// <para>
    /// <b>为什么拆</b>:旧版 ta.py 单文件 200 多行 18 函数,每个函数都重复
    /// <c>np.asarray(close, dtype=np.float64)</c> + <c>if _pta is not None: try: ... except: pass</c> 样板。
    /// 抽 <c>@as_arrays</c> / <c>@pta_polyfill</c> 装饰器后函数体只剩纯算法,新增指标(ATR/MFI/VWAP)信噪比明显上来。
    /// </para>
    /// </summary>
    internal static class HevoIndicatorsSources
    {
        // (filename, content) 列表,顺序无关 —— Bootstrap 写入时按文件名落盘。
        public static readonly (string Name, string Content)[] Files = new[]
        {
            ("__init__.py",       InitPy),
            ("_trade.py",         TradePy),
            ("_common.py",        CommonPy),
            ("_ta_ma.py",         TaMaPy),
            ("_ta_momentum.py",   TaMomentumPy),
            ("_ta_volatility.py", TaVolatilityPy),
            ("_ta_multi.py",      TaMultiPy),
            ("ta.py",             TaPy),
        };

        private const string InitPy = """
            # Hevo low-code Python indicator runtime.
            #
            # 蓝图侧通过 @register 装饰器把 Python 函数登记为 handler;C# 端 PythonRegisterScanner
            # 做静态正则扫描 + PythonHandlerRegistry 从函数签名串构造 .NET 委托。
            #
            # trade facade 由 C# PythonNetRuntime.UseTradeService(...) 启动时注入(_setup_trade 路径);
            # 未注入时 trade is None,函数体内访问 trade.place_order(...) 抛 AttributeError。

            import inspect
            from typing import Callable, Dict, List, Optional, Tuple

            # 模块级 handler 表 —— 用 dict 而非 list:同名后 register 覆盖前(reload 友好)。
            # 形态:name -> (function_name, signature_or_None, inputs_or_None, source_file_or_empty)
            # inputs:      多输入指标(§D2.X)的形参名列表,例 ['high','low','close']。单输入留 None。
            # source_file: fn 所在 .py 文件绝对路径,C# 端 EmbeddedPythonHost.LoadPythonAssetsFromAssembly
            #              用 inspect.getfile(fn) 拿 → 用来反查 RegisterPythonFunction(filePath) 必填参数;
            #              拿不到(动态构造的 lambda / interactive 等)记空串,C# 端走 regex fallback。
            _hevo_handlers: Dict[str, Tuple[str, Optional[str], Optional[List[str]], str]] = {}


            def register(name: str, *, signature: Optional[str] = None,
                         inputs: Optional[List[str]] = None,
                         incremental: bool = False) -> Callable:
                # 把函数登记为蓝图 handler。
                #
                # name:        蓝图 JSON 引用 handler 用的名字。同名后注册覆盖前(热重载友好)。
                # signature:   类型签名串,例 "(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]"。
                #              PythonTypeMapper(C# 端)据此推断 .NET 委托类型,
                #              缺省 = 蓝图层无法推断委托类型,handler 被跳过 + DryRun BP_PYHANDLER_NO_SIGNATURE。
                # inputs:      §D2.X 多输入指标 — 函数形参名列表(顺序须跟 signature 形参顺序一致)。
                #              例 inputs=['high','low','close'] 配 def atr(high, low, close): ...
                #              C# 端 ComputeFeature/HandlerFeature/PlotFeature 据此把 PortBindings 里
                #              "Inputs.high" / "Inputs.low" / "Inputs.close" 三根分别接到对应形参。
                #              单输入留 None,走旧 InputPort 兼容路径。
                # incremental: §D2.6.4 增量协议标记 —— Python 侧不做特殊处理(只为接住 kwarg 不抛 TypeError);
                #              C# 端 PythonRegisterScanner regex 自己识别这个 kwarg 设 IsIncremental=true。
                inputs_norm = list(inputs) if inputs else None
                def deco(fn: Callable) -> Callable:
                    try:
                        source_file = inspect.getfile(fn)
                    except (TypeError, OSError):
                        source_file = ""
                    _hevo_handlers[name] = (fn.__name__, signature, inputs_norm, source_file)
                    return fn
                return deco


            # Trade facade 占位 —— C# 启动时 _setup_trade 注入实际实例(_trade.py 的 _TradeFacade)。
            # 业务侧 Python 函数 `from hevo_indicators import trade` 拿到的就是这个 module attr。
            trade = None


            def _setup_trade(svc) -> None:
                # C# PythonNetRuntime.UseTradeService 调用入口 —— 把 .NET ITradeService 实例
                # 包成 _TradeFacade 注入 module 全局 trade。
                global trade
                from ._trade import _TradeFacade
                trade = _TradeFacade(svc)


            # §D2.6 Pine 风味:@indicator 装饰器 + series 元数据 ──────────────────
            # 蓝图侧 PlotFeature 用 IndicatorMetadataRegistry 读这个表,
            # 据此构造 N 个 LineSeries / BarSeries 子序列(N 由 series 列表长度决定)。
            _hevo_indicators: Dict[str, dict] = {}


            def indicator(name: str, *, overlay: bool = True, series=None) -> Callable:
                # 声明一个完整指标(Pine 风味)。
                #
                # name:    蓝图引用 IndicatorName 用的名字。同名后注册覆盖前(reload 友好)。
                # overlay: True = 叠在主图(典型 SMA / Bollinger);False = 独立副图(典型 MACD / RSI)。
                # series:  序列声明列表,元素形如 (series_name, kind, color, width):
                #            series_name: 函数返回 dict 的 key
                #            kind:        'line' / 'bar' / 'scatter'(后续可扩展 'area' / 'cloud')
                #            color:       '#RRGGBB' 或 '#AARRGGBB',可选(缺省 '#888888')
                #            width:       线宽(line / scatter)或柱宽(bar),可选(缺省 1.5)
                #
                # 装饰的函数签名:(close: ndarray) -> dict[str, ndarray],key 必须跟 series_name 对齐。
                # 长度短于输入的序列会被自动右对齐 NaN 填充(交给 PlotFeature 处理)。
                series = list(series or [])
                norm_series = []
                for s in series:
                    if not isinstance(s, (tuple, list)) or len(s) < 2:
                        raise ValueError('@indicator series 元素需要 (name, kind, [color], [width]),收到: ' + repr(s))
                    sname = str(s[0])
                    skind = str(s[1])
                    scolor = str(s[2]) if len(s) > 2 else '#888888'
                    swidth = float(s[3]) if len(s) > 3 else 1.5
                    if skind not in ('line', 'bar', 'scatter', 'arrow_markers', 'text_markers'):
                        raise ValueError('@indicator series kind 不支持: ' + repr(skind) + ' (line/bar/scatter/arrow_markers/text_markers)')
                    norm_series.append({'name': sname, 'kind': skind, 'color': scolor, 'width': swidth})

                def deco(fn: Callable) -> Callable:
                    _hevo_indicators[name] = {
                        'name':    name,
                        'fn':      fn.__name__,
                        'overlay': bool(overlay),
                        'series':  norm_series,
                    }
                    return fn
                return deco


            def get_indicator_meta(name: str):
                # C# 端 IndicatorMetadataRegistry 用此函数 introspect 已登记的 indicator metadata。
                # 返回 dict({name, fn, overlay, series}) 或 None
                return _hevo_indicators.get(name)


            def list_indicators():
                # C# 端 enumerate 全部 indicator metadata 用,返回 list[dict]
                return list(_hevo_indicators.values())


            # §D2.6 plot DSL 占位 —— 当前 no-op。如果未来切到 Pine 全风格 (Option A,真 plot 调用而非 dict 返回),
            # 这里实装成 thread-local list:plot 调用按顺序 append,C# 调用结束后读取并清空。
            def plot(*args, **kwargs) -> None:
                pass


            def hsl_color(pct: float, alpha: float = 1.0) -> str:
                # 业务便利:把 % 涨跌幅 + alpha 强度转 '#AARRGGBB' 字符串。
                # 跟旧 ScatterCloudLayer 同公式 —— 涨红跌蓝,饱和度按 abs(pct)/0.05 线性,亮度 0.55。
                pct = max(-0.10, min(0.10, float(pct)))
                alpha = max(0.0, min(1.0, float(alpha)))
                sat = min(abs(pct) / 0.05, 1.0)
                hue = 240.0 if pct < 0 else 0.0
                lightness = 0.55
                c = (1.0 - abs(2.0 * lightness - 1.0)) * sat
                hp = hue / 60.0
                x = c * (1.0 - abs(hp % 2.0 - 1.0))
                if   hp < 1: r1, g1, b1 = c, x, 0.0
                elif hp < 2: r1, g1, b1 = x, c, 0.0
                elif hp < 3: r1, g1, b1 = 0.0, c, x
                elif hp < 4: r1, g1, b1 = 0.0, x, c
                elif hp < 5: r1, g1, b1 = x, 0.0, c
                else:        r1, g1, b1 = c, 0.0, x
                m = lightness - c / 2.0
                rr = int(max(0.0, min(1.0, r1 + m)) * 255)
                gg = int(max(0.0, min(1.0, g1 + m)) * 255)
                bb = int(max(0.0, min(1.0, b1 + m)) * 255)
                aa = int(alpha * 255)
                return '#{0:02X}{1:02X}{2:02X}{3:02X}'.format(aa, rr, gg, bb)
            """;

        private const string TradePy = """
            # Trade facade —— ITradeService 的 snake_case Python 风格包装。
            #
            # 由 PythonNetRuntime.UseTradeService(...) 启动时注入到 hevo_indicators.trade。
            # Python 端 `from hevo_indicators import trade` 直接调下单 / 撤单 / 查账户。
            #
            # 注:.NET PlaceOrderAsync(...) 返回 Task[OrderAck],这里用 .Result 同步等(Python 风格)。
            # 高频热路径不要走这个 facade —— 阻塞主流程;low-frequency 信号下单(典型 1-100Hz)够用。


            class _TradeFacade:
                def __init__(self, service):
                    # service 是 .NET ITradeService 实例(经 pythonnet 反射桥接)
                    self._svc = service

                def place_order(self, *, symbol: str, direction: str, quantity: float,
                                order_type: str = 'market', limit_price: float = 0.0,
                                client_order_id: str) -> object:
                    # 下单,同步阻塞等 broker ack。
                    # 字符串 -> .NET enum 通过 pythonnet 反射桥接的 .NET 名字空间做转换
                    from Hevo.Trade import OrderRequest, Direction, OrderType
                    d = Direction.Buy if str(direction).lower() == 'buy' else Direction.Sell
                    t = OrderType.Limit if str(order_type).lower() == 'limit' else OrderType.Market
                    req = OrderRequest(symbol, d, t, float(quantity), float(limit_price), client_order_id)
                    return self._svc.PlaceOrderAsync(req).Result

                def cancel_order(self, broker_order_id: str) -> bool:
                    return self._svc.CancelOrderAsync(broker_order_id).Result

                def query_account(self) -> object:
                    return self._svc.QueryAccountAsync().Result

                @property
                def is_connected(self) -> bool:
                    return bool(self._svc.IsConnected)

                def __repr__(self) -> str:
                    return f'<_TradeFacade connected={self.is_connected}>'
            """;

        private const string CommonPy = """
            # hevo_indicators 内部公共工具 —— numpy 数组工具 + 装饰器 + pandas_ta singleton。
            #
            # 设计目的:把所有 ta_* 子模块里重复的样板(np.asarray 转换 / pandas_ta 优先 fallback 模式)
            # 抽到这里,各指标函数体只剩纯算法。
            #
            # - _nan_array / _rolling_sum / _rolling_std_pop:numpy 数组工具,被多个指标共享
            # - @as_arrays(n):前 n 个位置参数自动 np.asarray(.., dtype=float64),消除每个函数头的重复转换
            # - @pta_polyfill(name):优先调 pandas_ta.{name}(...).values,失败回退到原函数体
            # - _pta:pandas_ta singleton import,失败为 None(纯 numpy fallback 路径仍可用)

            import functools
            import numpy as np

            try:
                import pandas_ta as _pta  # type: ignore
            except ImportError:
                _pta = None


            # ── 工具:NaN 数组 / 滚动求和 / 滚动标准差 ─────────────────────────

            def _nan_array(n):
                # 长度 n 的全 NaN float64 数组
                out = np.empty(n, dtype=np.float64)
                out[:] = np.nan
                return out


            def _rolling_sum(arr, length):
                # 滚动求和(SMA × length),头 length-1 元素填 NaN(暖机不足)
                n = arr.size
                out = _nan_array(n)
                if n < length:
                    return out
                cs = np.cumsum(arr, dtype=np.float64)
                out[length - 1] = cs[length - 1]
                out[length:] = cs[length:] - cs[:n - length]
                return out


            def _rolling_std_pop(arr, length):
                # 总体标准差(ddof=0)的滚动版本,头 length-1 元素 NaN
                n = arr.size
                out = _nan_array(n)
                if n < length:
                    return out
                cs1 = np.cumsum(arr, dtype=np.float64)
                cs2 = np.cumsum(arr * arr, dtype=np.float64)
                s1 = np.empty(n - length + 1)
                s2 = np.empty(n - length + 1)
                s1[0] = cs1[length - 1]
                s2[0] = cs2[length - 1]
                s1[1:] = cs1[length:] - cs1[:n - length]
                s2[1:] = cs2[length:] - cs2[:n - length]
                mean = s1 / length
                var = np.maximum(s2 / length - mean * mean, 0.0)
                out[length - 1:] = np.sqrt(var)
                return out


            # ── 装饰器:类型转换 + pandas_ta 优先 fallback ─────────────────────

            def as_arrays(n=1):
                # 把前 n 个位置参数转 np.asarray(.., dtype=float64)。
                # n=1: 单输入指标(sma / rsi);n=2: P/V 量价(obv);n=3: OHLC(atr);n=4: OHLCV(mfi/vwap)。
                #
                # 实参少于 n 时不做转换,让原函数自己抛 TypeError(参数缺失)—— 装饰器不掩盖错误。
                def deco(fn):
                    @functools.wraps(fn)
                    def wrapper(*args, **kwargs):
                        if len(args) < n:
                            return fn(*args, **kwargs)
                        converted = tuple(np.asarray(args[i], dtype=np.float64) for i in range(n))
                        return fn(*converted, *args[n:], **kwargs)
                    return wrapper
                return deco


            def pta_polyfill(name, n=1):
                # 优先用 pandas_ta.{name}(*args, **kwargs).values 实现;pta 不在 / 抛异常 → 调原函数。
                # n: 跟 @as_arrays 同义 —— 转给 pta 时位置参数前 n 个保持原顺序。
                #
                # 跟 @as_arrays 配合用法:
                #     @as_arrays(1)
                #     @pta_polyfill('sma')
                #     def sma(close, length=20): ...
                # 装饰器栈:caller -> as_arrays_wrapper -> pta_polyfill_wrapper -> sma_body
                # pta 收到的是已经转好的 ndarray,跟原函数体一样的参数,行为对齐。
                def deco(fn):
                    @functools.wraps(fn)
                    def wrapper(*args, **kwargs):
                        if _pta is not None:
                            pta_fn = getattr(_pta, name, None)
                            if pta_fn is not None:
                                try:
                                    result = pta_fn(*args, **kwargs)
                                    # pandas_ta 返回 Series / DataFrame,统一取 .values 拿 numpy
                                    if hasattr(result, 'values'):
                                        return result.values
                                    return result
                                except Exception:
                                    pass
                        return fn(*args, **kwargs)
                    return wrapper
                return deco
            """;

        private const string TaMaPy = """
            # 移动平均族:SMA / EMA / WMA / DEMA / TEMA。

            import numpy as np
            from ._common import _pta, _rolling_sum, _nan_array, as_arrays, pta_polyfill


            @as_arrays(1)
            @pta_polyfill('sma')
            def sma(close, length: int = 20):
                # Simple Moving Average
                if length <= 1: return close.copy()
                return _rolling_sum(close, length) / length


            @as_arrays(1)
            @pta_polyfill('ema')
            def ema(close, length: int = 20):
                # Exponential Moving Average,首值 = close[0]
                n = close.size
                if n == 0: return close.copy()
                alpha = 2.0 / (length + 1.0)
                out = np.empty(n, dtype=np.float64)
                out[0] = close[0]
                for i in range(1, n):
                    out[i] = alpha * close[i] + (1.0 - alpha) * out[i - 1]
                return out


            @as_arrays(1)
            def wma(close, length: int = 20):
                # Weighted Moving Average — 权重 1..length 线性递增
                n = close.size
                out = _nan_array(n)
                if n < length: return out
                weights = np.arange(1, length + 1, dtype=np.float64)
                norm = weights.sum()
                for i in range(length - 1, n):
                    out[i] = float((close[i - length + 1:i + 1] * weights).sum()) / norm
                return out


            def dema(close, length: int = 20):
                # Double EMA = 2*EMA - EMA(EMA)
                e1 = ema(close, length)
                e2 = ema(e1, length)
                return 2.0 * e1 - e2


            def tema(close, length: int = 20):
                # Triple EMA = 3*EMA - 3*EMA(EMA) + EMA(EMA(EMA))
                e1 = ema(close, length)
                e2 = ema(e1, length)
                e3 = ema(e2, length)
                return 3.0 * e1 - 3.0 * e2 + e3
            """;

        private const string TaMomentumPy = """
            # 动量族:RSI / ROC / MOM / MACD 三件套。

            import numpy as np
            from ._common import _nan_array, as_arrays, pta_polyfill
            from ._ta_ma import ema


            @as_arrays(1)
            @pta_polyfill('rsi')
            def rsi(close, length: int = 14):
                # Wilder's RSI,头 length 项 NaN
                n = close.size
                out = _nan_array(n)
                if n < length + 1: return out

                delta = np.diff(close)
                gain = np.where(delta > 0, delta, 0.0)
                loss = np.where(delta < 0, -delta, 0.0)
                avg_g = np.empty(n - 1, dtype=np.float64)
                avg_l = np.empty(n - 1, dtype=np.float64)
                avg_g[:length] = gain[:length].mean()
                avg_l[:length] = loss[:length].mean()
                for i in range(length, n - 1):
                    avg_g[i] = (avg_g[i - 1] * (length - 1) + gain[i]) / length
                    avg_l[i] = (avg_l[i - 1] * (length - 1) + loss[i]) / length
                denom = np.where(avg_l[length - 1:] == 0.0, 1e-12, avg_l[length - 1:])
                rs = avg_g[length - 1:] / denom
                out[length:] = 100.0 - 100.0 / (1.0 + rs)
                return out


            @as_arrays(1)
            def roc(close, length: int = 10):
                # Rate of Change(百分比):(close - close[t-length]) / close[t-length] * 100
                n = close.size
                out = _nan_array(n)
                if n <= length: return out
                prev = close[:n - length]
                curr = close[length:]
                safe = np.where(prev == 0.0, 1e-12, prev)
                out[length:] = (curr - prev) / safe * 100.0
                return out


            @as_arrays(1)
            def mom(close, length: int = 10):
                # Momentum(差):close - close[t-length]
                n = close.size
                out = _nan_array(n)
                if n <= length: return out
                out[length:] = close[length:] - close[:n - length]
                return out


            def macd_line(close, fast: int = 12, slow: int = 26):
                # MACD line = EMA(fast) - EMA(slow);前 max(fast,slow) 项可信度低,这里不强制 NaN
                return ema(close, fast) - ema(close, slow)


            def macd_signal(close, fast: int = 12, slow: int = 26, signal: int = 9):
                # Signal line = EMA(MACD, signal)
                return ema(macd_line(close, fast, slow), signal)


            def macd_hist(close, fast: int = 12, slow: int = 26, signal: int = 9):
                # Histogram = MACD - Signal;前 (slow + signal) 项标 NaN
                line = macd_line(close, fast, slow)
                sig = ema(line, signal)
                hist = line - sig
                warm = slow + signal
                if hist.size > warm: hist[:warm] = np.nan
                return hist
            """;

        private const string TaVolatilityPy = """
            # 波动率族:stdev / Bollinger Bands。

            import numpy as np
            from ._common import _rolling_std_pop, as_arrays
            from ._ta_ma import sma


            @as_arrays(1)
            def stdev(close, length: int = 20):
                # 滚动总体标准差(ddof=0)
                return _rolling_std_pop(close, length)


            def bb_middle(close, length: int = 20):
                # Bollinger 中轨 = SMA
                return sma(close, length)


            def bb_upper(close, length: int = 20, k: float = 2.0):
                # Bollinger 上轨 = SMA + k * stdev
                return sma(close, length) + k * stdev(close, length)


            def bb_lower(close, length: int = 20, k: float = 2.0):
                # Bollinger 下轨 = SMA - k * stdev
                return sma(close, length) - k * stdev(close, length)


            @as_arrays(1)
            def bb_pct(close, length: int = 20, k: float = 2.0):
                # Bollinger %B = (close - lower) / (upper - lower);0=贴下轨,1=贴上轨,>1=破上轨
                up = bb_upper(close, length, k)
                lo = bb_lower(close, length, k)
                width = up - lo
                safe = np.where(width == 0.0, 1e-12, width)
                return (close - lo) / safe


            def bb_width(close, length: int = 20, k: float = 2.0):
                # Bollinger 带宽 = (upper - lower) / middle;越大波动越剧烈
                mid = bb_middle(close, length)
                up = bb_upper(close, length, k)
                lo = bb_lower(close, length, k)
                safe = np.where(mid == 0.0, 1e-12, mid)
                return (up - lo) / safe
            """;

        private const string TaMultiPy = """
            # §D2.X 多输入指标:true_range / ATR / MFI / VWAP / OBV。
            #
            # 这些指标接多根 ROM<double> 输入流(典型 OHLC 或 OHLCV)。Python 侧用法:
            #     @register('atr_14',
            #               signature='(ReadOnlyMemory[double], ReadOnlyMemory[double], ReadOnlyMemory[double]) -> ReadOnlyMemory[double]',
            #               inputs=['high','low','close'])
            #     def atr_14(high, low, close):
            #         from hevo_indicators import ta
            #         return ta.atr(high, low, close, length=14)
            #
            # 蓝图侧用 ComputeFeature.Inputs dict + InputOrder 数组焊接,详见低代码.md §D2.X。

            import numpy as np
            from ._common import _nan_array, as_arrays, pta_polyfill


            @as_arrays(3)
            def true_range(high, low, close):
                # True Range = max(H-L, |H-prev_C|, |L-prev_C|)。首元素 = H[0] - L[0](无 prev_C)。
                n = close.size
                if n == 0: return close.copy()
                tr = np.empty(n, dtype=np.float64)
                tr[0] = high[0] - low[0]
                if n > 1:
                    prev_c = close[:-1]
                    h1 = high[1:] - low[1:]
                    h2 = np.abs(high[1:] - prev_c)
                    h3 = np.abs(low[1:] - prev_c)
                    tr[1:] = np.maximum(np.maximum(h1, h2), h3)
                return tr


            @as_arrays(3)
            @pta_polyfill('atr', n=3)
            def atr(high, low, close, length: int = 14):
                # Average True Range — Wilder smoothing of True Range。
                # 头 length-1 元素 NaN(暖机不足)。
                tr = true_range(high, low, close)
                n = tr.size
                out = _nan_array(n)
                if n < length: return out
                # Wilder: 首值 = 前 length 个 TR 均值,后续 EWMA(alpha=1/length)
                out[length - 1] = tr[:length].mean()
                for i in range(length, n):
                    out[i] = (out[i - 1] * (length - 1) + tr[i]) / length
                return out


            @as_arrays(4)
            @pta_polyfill('mfi', n=4)
            def mfi(high, low, close, volume, length: int = 14):
                # Money Flow Index — RSI-like 动量基于价量。
                # typical price = (H+L+C)/3,raw money flow = TP * V,按 TP 方向分正负 money flow,
                # MFI = 100 - 100/(1 + posMF/negMF) 在 length 滚动窗内。头 length 项 NaN。
                n = close.size
                out = _nan_array(n)
                if n < length + 1: return out

                tp = (high + low + close) / 3.0
                mf = tp * volume
                delta = np.diff(tp)
                pos_mf = np.where(delta > 0, mf[1:], 0.0)
                neg_mf = np.where(delta < 0, mf[1:], 0.0)
                pos_sum = np.empty(n - length, dtype=np.float64)
                neg_sum = np.empty(n - length, dtype=np.float64)
                cs_pos = np.cumsum(pos_mf, dtype=np.float64)
                cs_neg = np.cumsum(neg_mf, dtype=np.float64)
                pos_sum[0] = cs_pos[length - 1]
                neg_sum[0] = cs_neg[length - 1]
                if n > length + 1:
                    pos_sum[1:] = cs_pos[length:] - cs_pos[:n - length - 1]
                    neg_sum[1:] = cs_neg[length:] - cs_neg[:n - length - 1]
                safe = np.where(neg_sum == 0.0, 1e-12, neg_sum)
                ratio = pos_sum / safe
                out[length:] = 100.0 - 100.0 / (1.0 + ratio)
                return out


            @as_arrays(4)
            def vwap(high, low, close, volume):
                # Volume Weighted Average Price(累计 session 形态)。
                # VWAP[t] = cumsum(typical * volume) / cumsum(volume),typical = (H+L+C)/3。
                # 元素 0 = typical[0]。零成交量段以前一 VWAP 为兜底(safe div)。
                n = close.size
                if n == 0: return close.copy()
                tp = (high + low + close) / 3.0
                cum_pv = np.cumsum(tp * volume, dtype=np.float64)
                cum_v = np.cumsum(volume, dtype=np.float64)
                safe = np.where(cum_v == 0.0, 1e-12, cum_v)
                return cum_pv / safe


            @as_arrays(2)
            def obv(close, volume):
                # On Balance Volume — 累计带符号成交量。
                # OBV[0] = 0;OBV[i] = OBV[i-1] + sign(close[i] - close[i-1]) * volume[i]。
                n = close.size
                if n == 0: return close.copy()
                out = np.empty(n, dtype=np.float64)
                out[0] = 0.0
                delta = np.diff(close)
                sign = np.where(delta > 0, 1.0, np.where(delta < 0, -1.0, 0.0))
                out[1:] = np.cumsum(sign * volume[1:])
                return out
            """;

        private const string TaPy = """
            # ta —— 公共技术指标 API hub,从各族子模块 re-export。
            #
            # 设计目的:旧代码 `from hevo_indicators import ta; ta.sma(close, 20)` 不变,
            # 内部按指标族拆到独立子模块(_ta_ma / _ta_momentum / _ta_volatility / _ta_multi),
            # 让加新指标 / 调单族算法都不用改这个 200 行的大文件。

            from ._ta_ma         import sma, ema, wma, dema, tema
            from ._ta_momentum   import rsi, roc, mom, macd_line, macd_signal, macd_hist
            from ._ta_volatility import stdev, bb_middle, bb_upper, bb_lower, bb_pct, bb_width
            from ._ta_multi      import true_range, atr, mfi, vwap, obv

            __all__ = [
                # MA family
                'sma', 'ema', 'wma', 'dema', 'tema',
                # Momentum
                'rsi', 'roc', 'mom', 'macd_line', 'macd_signal', 'macd_hist',
                # Volatility
                'stdev', 'bb_middle', 'bb_upper', 'bb_lower', 'bb_pct', 'bb_width',
                # Multi-input (§D2.X)
                'true_range', 'atr', 'mfi', 'vwap', 'obv',
            ]
            """;
    }
}
