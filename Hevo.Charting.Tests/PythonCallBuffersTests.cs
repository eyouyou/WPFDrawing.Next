using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 真 Python handler 在调用点里跑:结果数值跟逐次 new 的旧路径一致;输出来自调用点池;
    /// 输入固定缓冲在长度变短 / 变长(扩容)时视图长度正确;handler 把输入存起来下次再用不会崩(内容是新的);
    /// handler 抛异常后调用点还能继续用。
    /// </summary>
    [Collection(nameof(RealPythonCollection))]
    public sealed class PythonCallBuffersTests
    {
        private readonly RealPythonFixture _fx;
        public PythonCallBuffersTests(RealPythonFixture fx) { _fx = fx; }

        private Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>> Load(string name)
        {
            File.WriteAllText(Path.Combine(_fx.IndicatorsDir, "call_buffers_probe.py"), """
                from hevo_indicators import register
                import numpy as np

                _saved = []

                @register('cb_cumsum', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
                def cb_cumsum(arr):
                    return np.cumsum(np.asarray(arr, dtype=np.float64))

                @register('cb_keep_input', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
                def cb_keep_input(arr):
                    prev = _saved[-1] if _saved else None
                    _saved.append(arr)               # 违反约定:把输入存起来
                    head = float(prev[0]) if prev is not None else -1.0
                    return np.array([head, float(len(arr)), float(arr[-1])], dtype=np.float64)

                @register('cb_fail_on_negative', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
                def cb_fail_on_negative(arr):
                    a = np.asarray(arr)
                    if a[0] < 0:
                        raise ValueError('negative')
                    return a * 2.0
                """);
            _fx.Registry!.AutoDiscoverDirectory(_fx.IndicatorsDir);
            return (Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>)_fx.Registry.TryGet(name)!;
        }

        private static ReadOnlyMemory<double> InCallSite(ColumnCallBuffers b, Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>> fn,
            double[] input, object port)
        {
            using var reader = ColumnReaders.Enter(releasable: true);   // 跟 WatchAsync 回调一样(走固定输入缓冲路径)
            using var call = b.BeginCall();
            var r = fn(input);
            call.Written(port, r);
            return r;
        }

        [Fact]
        public void Results_MatchUnpooledPath_AcrossLengthChanges()
        {
            if (!_fx.Available) return;
            var fn = Load("cb_cumsum");
            var b = new ColumnCallBuffers();
            var port = new object();
            foreach (int len in new[] { 10, 5, 2000, 20_000, 7, 20_000, 30_000 })
            {
                var input = Enumerable.Range(1, len).Select(i => i * 0.5).ToArray();
                double[] expected = fn(input).ToArray();                  // 调用点外:旧路径
                var pooled = InCallSite(b, fn, input, port);
                Assert.Equal(expected, pooled.ToArray());
                Assert.True(MemoryMarshal.TryGetArray(pooled, out var seg) && b.PublishedForTest.Contains(seg.Array!),
                    $"长度 {len} 的结果没来自调用点池");
            }
        }

        [Fact]
        public void HandlerKeepingInput_DoesNotCrash_AndSeesNewContent()
        {
            if (!_fx.Available) return;
            var fn = Load("cb_keep_input");
            var b = new ColumnCallBuffers();
            var port = new object();
            InCallSite(b, fn, new double[] { 1, 2, 3 }, port);
            var r = InCallSite(b, fn, new double[] { 9, 8 }, port).ToArray();
            Assert.Equal(new[] { 9.0, 2.0, 8.0 }, r);                     // 存起来的旧视图读到的是本次写进去的内容
            // 扩容:旧固定缓冲被换掉,旧视图仍持有它(不崩)
            var big = Enumerable.Range(0, 50_000).Select(i => (double)i).ToArray();
            var r2 = InCallSite(b, fn, big, port).ToArray();
            Assert.Equal(new[] { 9.0, 50_000.0, 49_999.0 }, r2);
            GC.Collect(); GC.WaitForPendingFinalizers();
            // 变得很短:固定缓冲按 ArrayGrowth 收缩成新的一块,存住的旧视图还指着 5 万那块(内容不变、内存还活着)
            var r3 = InCallSite(b, fn, new double[] { 4 }, port).ToArray();
            Assert.Equal(new[] { 0.0, 1.0, 4.0 }, r3);
        }

        [Fact]
        public void HandlerThrowing_LeavesCallSiteUsable()
        {
            if (!_fx.Available) return;
            var fn = Load("cb_fail_on_negative");
            var b = new ColumnCallBuffers();
            var port = new object();
            Assert.ThrowsAny<Exception>(() => InCallSite(b, fn, new double[] { -1, 2 }, port));
            Assert.Null(ColumnCallBuffers.Current);
            Assert.Equal(new[] { 2.0, 4.0 }, InCallSite(b, fn, new double[] { 1, 2 }, port).ToArray());
            Assert.Equal(0, ColumnReaders.CanReleaseForLongCall ? 1 : 0);   // 读者登记都已离场
        }
    }
}
