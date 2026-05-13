using System;
using System.IO;
using System.Linq;
using Hevo.Charting.PythonNet;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §D2.X 多输入指标真 Python 端到端测试 —— 用 <see cref="RealPythonFixture"/> 拉起进程级嵌入解释器,
    /// 写 .py、AutoDiscover、调用、对账数值。覆盖整链路:
    /// <list type="bullet">
    ///   <item><see cref="PythonRegisterScanner"/> 抓 <c>inputs=[...]</c> 字面量</item>
    ///   <item><see cref="PythonHandlerRegistry.RegisterModule"/> 把 inputs 元数据塞进 <see cref="BlueprintHandlerRegistry"/></item>
    ///   <item><see cref="PythonTypeMapper.ResolveDelegateType"/> 推断多 arity Func 类型</item>
    ///   <item><c>PythonInvokerShim.MakeDelegate&lt;TDel&gt;</c> 生成强类型委托(Expression 编译,无 Emit)</item>
    ///   <item><c>PythonNetModule.Invoke</c> + <c>PythonMarshaller</c> 跨边界 ROM&lt;double&gt; ↔ ndarray 拷贝</item>
    ///   <item>hevo_indicators.ta.atr 多输入指标(_ta_multi.py 新加)算出正确值</item>
    /// </list>
    ///
    /// <para>
    /// <b>跳过策略</b>:本机找不到 Python312/python312.dll → 测试 skip(开发机有,CI 默认无)。
    /// 真要在 CI 上跑需准备 embedded Python(参 scripts/setup-python.ps1)。
    /// </para>
    /// </summary>
    [Collection(nameof(RealPythonCollection))]
    public sealed class MultiInputE2ETests
    {
        private readonly RealPythonFixture _fx;

        public MultiInputE2ETests(RealPythonFixture fx)
        {
            _fx = fx;
        }

        // ── Sanity:fixture 真的拉起来了 Python 吗? ────────────────────────
        [Fact]
        public void Fixture_Sanity_HasRegistry()
        {
            // 这条不 skip,Available=false 时直接 fail 暴露环境问题
            Assert.True(_fx.Available, $"Python fixture 未就绪: {_fx.SkipReason ?? "(无原因)"}");
            Assert.NotNull(_fx.Registry);
        }

        // ── 多输入 ATR 整链路 ─────────────────────────────────────────────

        [Fact]
        public void Atr14_FullChain_RegisterScanInvoke_ProducesCorrectValues()
        {
            if (!_fx.Available) return;   // 跳过 — Python 不可用
            var registry = _fx.Registry!;

            // 1. 落 .py 到沙箱目录
            var pyFile = Path.Combine(_fx.IndicatorsDir, "atr_demo.py");
            File.WriteAllText(pyFile, """
                from hevo_indicators import register, ta

                @register('atr_14',
                          signature='(ReadOnlyMemory[double], ReadOnlyMemory[double], ReadOnlyMemory[double]) -> ReadOnlyMemory[double]',
                          inputs=['high','low','close'])
                def atr_14(high, low, close):
                    return ta.atr(high, low, close, length=14)
                """);

            // 2. AutoDiscover 走 Scanner + RegisterModule 全套
            registry.AutoDiscoverDirectory(_fx.IndicatorsDir);

            // 3. 元数据落库:inputs=[...] 应被 BlueprintHandlerRegistry.GetInputNames 拿到
            var inputs = registry.GetInputNames("atr_14");
            Assert.NotNull(inputs);
            Assert.Equal(new[] { "high", "low", "close" }, inputs!);

            // 4. 委托类型应是 Func<ROM,ROM,ROM,ROM>(3 输入)
            var del = registry.TryGet("atr_14");
            Assert.NotNull(del);
            Assert.IsType<Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>, ReadOnlyMemory<double>, ReadOnlyMemory<double>>>(del);

            // 5. 实际调用 —— 单调上涨 OHLC 序列,ATR 应稳态在 (H-L) 附近
            int n = 30;
            var high  = new double[n];
            var low   = new double[n];
            var close = new double[n];
            for (int i = 0; i < n; i++)
            {
                high[i]  = i + 1.5;
                low[i]   = i + 0.5;
                close[i] = i + 1.0;
            }

            var typed = (Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>, ReadOnlyMemory<double>, ReadOnlyMemory<double>>)del!;
            var result = typed(high, low, close);

            // 6. 数值断言
            Assert.Equal(n, result.Length);
            // 暖机不足:头 length-1 元素 NaN(length=14 → 前 13 个 NaN)
            for (int i = 0; i < 13; i++)
            {
                Assert.True(double.IsNaN(result.Span[i]), $"index {i} 应为 NaN(暖机不足)");
            }
            // 暖机后:每根 bar 的 H-L = 1.0,prev_C 接连 → True Range 稳态接近 1.0,ATR 稳态接近 1.0
            for (int i = 14; i < n; i++)
            {
                Assert.False(double.IsNaN(result.Span[i]), $"index {i} 不该为 NaN(已过暖机)");
                Assert.InRange(result.Span[i], 0.5, 2.0);   // ATR 稳态 ~1.0 + 偏差兜底
            }
        }

        // ── 多输入 OBV(2 输入)—— 验证不同 arity ─────────────────────────

        [Fact]
        public void Obv_TwoInput_FullChain()
        {
            if (!_fx.Available) return;
            var registry = _fx.Registry!;

            var pyFile = Path.Combine(_fx.IndicatorsDir, "obv_demo.py");
            File.WriteAllText(pyFile, """
                from hevo_indicators import register, ta

                @register('obv_test',
                          signature='(ReadOnlyMemory[double], ReadOnlyMemory[double]) -> ReadOnlyMemory[double]',
                          inputs=['close', 'volume'])
                def obv_test(close, volume):
                    return ta.obv(close, volume)
                """);

            registry.AutoDiscoverDirectory(_fx.IndicatorsDir);

            var del = registry.TryGet("obv_test");
            Assert.NotNull(del);
            Assert.IsType<Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>, ReadOnlyMemory<double>>>(del);

            // 单调上涨 close + 固定成交量 → OBV 累加 = (n-1) × volume
            int n = 10;
            var close  = Enumerable.Range(1, n).Select(i => (double)i).ToArray();
            var volume = Enumerable.Repeat(1000.0, n).ToArray();

            var typed = (Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>, ReadOnlyMemory<double>>)del!;
            var result = typed(close, volume);

            Assert.Equal(n, result.Length);
            Assert.Equal(0.0, result.Span[0]);   // OBV[0] = 0 by convention
            Assert.Equal((n - 1) * 1000.0, result.Span[n - 1], 1e-9);
        }

        // ── 单输入兼容:旧 .py(无 inputs= kwarg)依然能跑 ──────────────────

        [Fact]
        public void SingleInputHandler_NoInputsKwarg_StillWorks()
        {
            if (!_fx.Available) return;
            var registry = _fx.Registry!;

            var pyFile = Path.Combine(_fx.IndicatorsDir, "ma_demo.py");
            File.WriteAllText(pyFile, """
                from hevo_indicators import register, ta

                @register('ma_close_5', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
                def ma_close_5(close):
                    return ta.sma(close, length=5)
                """);

            registry.AutoDiscoverDirectory(_fx.IndicatorsDir);

            // 单输入 handler:GetInputNames 返 null
            Assert.Null(registry.GetInputNames("ma_close_5"));

            var del = registry.TryGet("ma_close_5");
            Assert.IsType<Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>>(del);

            var typed = (Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>)del!;
            var input = new double[] { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0 };
            var output = typed(input);
            Assert.Equal(10, output.Length);
            // SMA(5) 在 i=4 = mean(1..5) = 3
            Assert.Equal(3.0, output.Span[4], 1e-9);
            // SMA(5) 在 i=9 = mean(6..10) = 8
            Assert.Equal(8.0, output.Span[9], 1e-9);
        }

        // ── EnumerateAllHandlers 枚举验证(给 picker UX 用)─────────────────

        [Fact]
        public void EnumerateAllHandlers_MixesSingleAndMulti()
        {
            if (!_fx.Available) return;
            var registry = _fx.Registry!;

            File.WriteAllText(Path.Combine(_fx.IndicatorsDir, "mixed.py"), """
                from hevo_indicators import register, ta

                @register('single_ma', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
                def single_ma(close): return ta.sma(close, 10)

                @register('multi_atr',
                          signature='(ReadOnlyMemory[double], ReadOnlyMemory[double], ReadOnlyMemory[double]) -> ReadOnlyMemory[double]',
                          inputs=['high','low','close'])
                def multi_atr(h, l, c): return ta.atr(h, l, c, 14)
                """);
            registry.AutoDiscoverDirectory(_fx.IndicatorsDir);

            var all = registry.EnumerateAllHandlers().ToList();
            var single = all.Where(h => h.Inputs is null or { Count: 0 }).ToList();
            var multi  = all.Where(h => h.Inputs is { Count: > 0 }).ToList();

            Assert.Contains(single, h => h.Name == "single_ma");
            Assert.Contains(multi,  h => h.Name == "multi_atr");
            Assert.Equal(3, multi.First(h => h.Name == "multi_atr").Inputs!.Count);
        }
    }
}
