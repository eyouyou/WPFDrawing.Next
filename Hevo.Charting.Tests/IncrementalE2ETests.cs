using System;
using System.IO;
using System.Linq;
using Hevo.Charting.PythonNet;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §D2.6.4 IIncrementalCompute 增量协议 e2e 测试 —— 真 Python 嵌入式跑 EMA / RSI 风味的
    /// stateful handler,验证 PyTuple → ValueTuple Unbox 整链路 + 跨帧 state 持久(state 跨帧由调用方
    /// 持有,模拟 IncrementalComputeFeature 内部 _state 字段语义)+ 首帧 bootstrap。
    ///
    /// <para>Python 不可用时 skip,跟 §D2.X 同款。</para>
    /// </summary>
    [Collection(nameof(RealPythonCollection))]
    public sealed class IncrementalE2ETests
    {
        private readonly RealPythonFixture _fx;

        public IncrementalE2ETests(RealPythonFixture fx) { _fx = fx; }

        // ── EMA 增量 handler 整链路 ──────────────────────────────────────

        [Fact]
        public void EmaIncremental_FullChain_ReturnsTuple()
        {
            if (!_fx.Available) return;
            var registry = _fx.Registry!;

            // 落 .py:EMA-5 增量,state = [prev_ema];首帧 prev_state.size==0 → 用 close[-1] bootstrap
            var pyFile = Path.Combine(_fx.IndicatorsDir, "ema_inc.py");
            File.WriteAllText(pyFile, """
                from hevo_indicators import register
                import numpy as np

                @register('ema_5_inc',
                          incremental=True,
                          signature='(ReadOnlyMemory[double], ReadOnlyMemory[double]) -> Tuple[ReadOnlyMemory[double], ReadOnlyMemory[double]]')
                def ema_5_inc(close, prev_state):
                    alpha = 2.0 / (5 + 1)
                    if prev_state.size == 0:
                        prev = close[-1]                     # bootstrap
                    else:
                        prev = prev_state[0]
                    new_ema = close[-1] * alpha + prev * (1 - alpha)
                    return np.array([new_ema], dtype=np.float64), np.array([new_ema], dtype=np.float64)
                """);

            registry.AutoDiscoverDirectory(_fx.IndicatorsDir);

            // 委托类型:Func<ROM, ROM, ValueTuple<ROM, ROM>>
            var del = registry.TryGet("ema_5_inc");
            Assert.NotNull(del);
            var typed = del as Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>,
                                    ValueTuple<ReadOnlyMemory<double>, ReadOnlyMemory<double>>>;
            Assert.NotNull(typed);

            // 第 1 帧:prev_state 空 → bootstrap,prev = close[-1] = 10.0,new_ema = 10.0
            var input1 = new double[] { 10.0 };
            var (out1, state1) = typed!(input1, ReadOnlyMemory<double>.Empty);
            Assert.Single(out1.ToArray());
            Assert.Single(state1.ToArray());
            Assert.Equal(10.0, out1.Span[0], 1e-9);
            Assert.Equal(10.0, state1.Span[0], 1e-9);

            // 第 2 帧:prev_state = [10.0],close[-1] = 12.0
            //   alpha = 1/3,new = 12 * 1/3 + 10 * 2/3 = 4 + 6.667 = 10.667
            var input2 = new double[] { 12.0 };
            var (out2, state2) = typed(input2, state1);
            double expected = 12.0 * (1.0 / 3.0) + 10.0 * (2.0 / 3.0);
            Assert.Equal(expected, out2.Span[0], 1e-9);
            Assert.Equal(expected, state2.Span[0], 1e-9);

            // 跨帧 state 自循环 5 次,值应单调收敛
            var feed = new[] { 13.0, 14.0, 15.0, 16.0, 17.0 };
            ReadOnlyMemory<double> prev = state2;
            double last = state2.Span[0];
            foreach (var v in feed)
            {
                var (o, s) = typed(new[] { v }, prev);
                Assert.True(s.Span[0] > last, $"close={v}: state 应 monotonic increasing,实际 {s.Span[0]} <= prev {last}");
                last = s.Span[0];
                prev = s;
            }
        }

        // ── 增量 handler 跟 numpy ground truth 对账 ─────────────────────

        [Fact]
        public void EmaIncremental_TenFrames_MatchesNumpyGroundTruth()
        {
            if (!_fx.Available) return;
            var registry = _fx.Registry!;

            var pyFile = Path.Combine(_fx.IndicatorsDir, "ema_gt.py");
            File.WriteAllText(pyFile, """
                from hevo_indicators import register
                import numpy as np

                @register('ema_3_inc',
                          incremental=True,
                          signature='(ReadOnlyMemory[double], ReadOnlyMemory[double]) -> Tuple[ReadOnlyMemory[double], ReadOnlyMemory[double]]')
                def ema_3_inc(close, prev_state):
                    alpha = 0.5    # 显式 alpha,length=3 时 alpha=2/(3+1)=0.5
                    if prev_state.size == 0:
                        prev = close[-1]
                    else:
                        prev = prev_state[0]
                    new_ema = close[-1] * alpha + prev * (1 - alpha)
                    return np.array([new_ema]), np.array([new_ema])
                """);

            registry.AutoDiscoverDirectory(_fx.IndicatorsDir);
            var typed = (Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>,
                              ValueTuple<ReadOnlyMemory<double>, ReadOnlyMemory<double>>>)
                        registry.TryGet("ema_3_inc")!;

            // 喂 10 帧 close,跟 C# 端独立算的 EMA(alpha=0.5) 对账
            var closes = new[] { 10.0, 12.0, 14.0, 13.0, 15.0, 16.0, 18.0, 20.0, 19.0, 21.0 };
            const double alpha = 0.5;

            ReadOnlyMemory<double> state = ReadOnlyMemory<double>.Empty;
            double gt = closes[0];   // ground truth bootstrap = closes[0]
            for (int i = 0; i < closes.Length; i++)
            {
                var (output, nextState) = typed(new[] { closes[i] }, state);

                // 第 0 帧:python prev_state.size==0 → prev = close[-1] = closes[0],
                //   new = closes[0]*0.5 + closes[0]*0.5 = closes[0]
                if (i > 0) gt = closes[i] * alpha + gt * (1 - alpha);

                Assert.Equal(gt, output.Span[0], 1e-9);
                state = nextState;
            }
        }

        // ── 多输入 stateful (§D2.6.4 + §D2.X 复用) ─────────────────────

        [Fact]
        public void MultiInputIncremental_AtrStateful_FullChain()
        {
            if (!_fx.Available) return;
            var registry = _fx.Registry!;

            // ATR-stateful:H/L/C 3 个数据输入 + 隐含 prev_state 形参 = signature 串里 4 个 ROM 参
            var pyFile = Path.Combine(_fx.IndicatorsDir, "atr_inc.py");
            File.WriteAllText(pyFile, """
                from hevo_indicators import register
                import numpy as np

                @register('atr_3_inc',
                          incremental=True,
                          inputs=['high','low','close'],
                          signature='(ReadOnlyMemory[double], ReadOnlyMemory[double], ReadOnlyMemory[double], ReadOnlyMemory[double]) -> Tuple[ReadOnlyMemory[double], ReadOnlyMemory[double]]')
                def atr_3_inc(high, low, close, prev_state):
                    # state 是 [prev_close, prev_atr] —— Wilder's smoothing
                    h, l, c = high[-1], low[-1], close[-1]
                    if prev_state.size == 0:
                        # bootstrap:首帧 TR = H-L,ATR = TR
                        tr = h - l
                        new_atr = tr
                        prev_close = c
                    else:
                        prev_close = prev_state[0]
                        prev_atr   = prev_state[1]
                        tr = max(h - l, abs(h - prev_close), abs(l - prev_close))
                        # Wilder smoothing(length=3): atr_t = (atr_(t-1)*(N-1) + tr) / N
                        new_atr = (prev_atr * 2 + tr) / 3
                        prev_close = c

                    return np.array([new_atr]), np.array([prev_close, new_atr])
                """);

            registry.AutoDiscoverDirectory(_fx.IndicatorsDir);

            // inputs 元数据应该被记
            Assert.Equal(new[] { "high", "low", "close" }, registry.GetInputNames("atr_3_inc")!);

            var del = registry.TryGet("atr_3_inc");
            Assert.NotNull(del);
            // 期望 Func<ROM, ROM, ROM, ROM, ValueTuple<ROM, ROM>>
            var typed = del as Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>,
                                    ReadOnlyMemory<double>, ReadOnlyMemory<double>,
                                    ValueTuple<ReadOnlyMemory<double>, ReadOnlyMemory<double>>>;
            Assert.NotNull(typed);

            // 喂 5 帧 H/L/C,验证 state 跨帧自循环 + ATR 单调收敛趋势
            var bars = new[]
            {
                (h: 100.0, l: 99.0,  c: 99.5),
                (h: 101.0, l: 100.0, c: 100.5),
                (h: 102.0, l: 101.0, c: 101.5),
                (h: 103.0, l: 102.0, c: 102.5),
                (h: 104.0, l: 103.0, c: 103.5),
            };

            ReadOnlyMemory<double> state = ReadOnlyMemory<double>.Empty;
            double[] atrSeries = new double[bars.Length];
            for (int i = 0; i < bars.Length; i++)
            {
                var b = bars[i];
                var (output, nextState) = typed!(
                    new[] { b.h }, new[] { b.l }, new[] { b.c }, state);

                Assert.Single(output.ToArray());
                atrSeries[i] = output.Span[0];
                state = nextState;
                Assert.Equal(2, state.Length);   // [prev_close, prev_atr]
            }

            // 单调上涨 + 每根 H-L=1.0 → ATR 应稳态接近 1.0
            // 暖机后(i>=1)所有 ATR ∈ (0.5, 2.0) 区间
            for (int i = 1; i < atrSeries.Length; i++)
            {
                Assert.InRange(atrSeries[i], 0.5, 2.0);
            }
        }

        // ── PyTuple 解包反向断言:返回非 tuple 应抛异常 ─────────────────

        [Fact]
        public void IncrementalHandler_HandlerReturnsNonTuple_InvalidCast()
        {
            if (!_fx.Available) return;
            var registry = _fx.Registry!;

            var pyFile = Path.Combine(_fx.IndicatorsDir, "bad_inc.py");
            File.WriteAllText(pyFile, """
                from hevo_indicators import register
                import numpy as np

                @register('bad_inc',
                          incremental=True,
                          signature='(ReadOnlyMemory[double], ReadOnlyMemory[double]) -> Tuple[ReadOnlyMemory[double], ReadOnlyMemory[double]]')
                def bad_inc(close, prev):
                    # 故意返回单 ndarray,不是 tuple —— 协议违规
                    return np.array([1.0])
                """);

            registry.AutoDiscoverDirectory(_fx.IndicatorsDir);

            var del = registry.TryGet("bad_inc");
            Assert.NotNull(del);
            var typed = (Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>,
                              ValueTuple<ReadOnlyMemory<double>, ReadOnlyMemory<double>>>)del!;

            // shim 的 ConstructValueTuple 应抛 InvalidCastException(消息含 "ValueTuple")
            var ex = Assert.Throws<InvalidCastException>(() =>
                typed(new[] { 1.0 }, ReadOnlyMemory<double>.Empty));
            Assert.Contains("ValueTuple", ex.Message);
        }
    }
}
