using System;
using System.Collections.Generic;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using Hevo.Charting.LowCode.Designer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 零分配签名进蓝图:[BlueprintHandler] 静态方法写成 (ReadOnlySpan&lt;double&gt; × N, Span&lt;double&gt;) → void 时,
    /// 注册成 ComputeInto / ComputeInto2 / 3 / 4,输入名取形参名(不含最后的输出);多输入时输出长度 = 输入长度最小值。
    /// </summary>
    public sealed class ComputeIntoBlueprintTests
    {
        public static class SpanHandlers
        {
            [BlueprintHandler("span_double")]
            public static void Double(ReadOnlySpan<double> close, Span<double> result)
            {
                for (int i = 0; i < close.Length; i++) result[i] = close[i] * 2;
            }

            [BlueprintHandler("span_change_pct")]
            public static void ChangePct(ReadOnlySpan<double> latest, ReadOnlySpan<double> prev_close, Span<double> result)
            {
                for (int i = 0; i < result.Length; i++)
                    result[i] = prev_close[i] > 0 ? (latest[i] - prev_close[i]) / prev_close[i] : 0.0;
            }

            [BlueprintHandler("span_sum3")]
            public static void Sum3(ReadOnlySpan<double> a, ReadOnlySpan<double> b, ReadOnlySpan<double> c, Span<double> result)
            {
                for (int i = 0; i < result.Length; i++) result[i] = a[i] + b[i] + c[i];
            }
        }

        [Fact]
        public void SpanHandlers_RegisterAsComputeIntoFamily_WithInputNamesWithoutOutput()
        {
            var reg = new BlueprintHandlerRegistry().AutoDiscoverStatic(typeof(SpanHandlers));
            Assert.IsType<ComputeInto>(reg.TryGet("span_double"));
            Assert.IsType<ComputeInto2>(reg.TryGet("span_change_pct"));
            Assert.IsType<ComputeInto3>(reg.TryGet("span_sum3"));
            Assert.Equal(new[] { "latest", "prev_close" }, reg.GetInputNames("span_change_pct"));
            Assert.Equal(new[] { "a", "b", "c" }, reg.GetInputNames("span_sum3"));
            Assert.Equal(new[] { "close" }, reg.GetInputNames("span_double"));
        }

        [Fact]
        public void MultiInput_OutputLengthIsMinOfInputs()
        {
            var reg = new BlueprintHandlerRegistry().AutoDiscoverStatic(typeof(SpanHandlers));
            var fn = reg.TryGet("span_change_pct")!;
            var latest = new ReadOnlyMemory<double>(new double[] { 11, 22, 33, 44 });
            var prev = new ReadOnlyMemory<double>(new double[] { 10, 20, 30 });
            var r = ComputeFeature.InvokeComputeInto(fn, new object?[] { latest, prev }, default);
            Assert.Equal(3, r.Length);
            Assert.Equal(new[] { 0.1, 0.1, 0.1 }, Array.ConvertAll(r.ToArray(), v => Math.Round(v, 10)));
        }

        [Fact]
        public void WrongInputCount_Throws()
        {
            ComputeInto2 two = (a, b, o) => { };
            var one = new ReadOnlyMemory<double>(new double[] { 1 });
            Assert.Throws<InvalidOperationException>(() => ComputeFeature.InvokeComputeInto(two, new object?[] { one, one, one }, default));
        }
    }

    /// <summary>ColumnPublisher:发布的值正确、绝不把已发布的那块再交出去、稳态复用。全进程 ColumnReadScope,不并行。</summary>
    [Collection(nameof(ColumnReadScopeCollection))]
    public sealed class ColumnPublisherTests
    {
        [Fact]
        public void Publish_WritesPort_NeverRentsPublished_AndReuses()
        {
            using var board = new DataBlackboard();
            var port = new DataPort<ReadOnlyMemory<double>>("CP_Out");
            var pub = new ColumnPublisher<double>();
            var seen = new HashSet<double[]>(ReferenceEqualityComparer.Instance);
            double[]? published = null;
            for (int k = 1; k <= 50; k++)
            {
                var buf = pub.Rent(1000);
                Assert.NotSame(published, buf);
                for (int i = 0; i < 1000; i++) buf[i] = k;
                using (board.AcquireWriteLock()) pub.Publish(board, port, buf, 1000);
                published = buf;
                seen.Add(buf);
                using (board.AcquireReadLock())
                {
                    var col = board.Read(port);
                    Assert.Equal(1000, col.Length);
                    Assert.Equal(k, col.Span[999]);
                }
            }
            Assert.True(seen.Count <= 4, $"50 次发布用了 {seen.Count} 块");
        }

        [Fact]
        public void Discard_MakesBufferImmediatelyReusable()
        {
            var pub = new ColumnPublisher<double>();
            var a = pub.Rent(100);
            pub.Discard(a);
            using var reader = ColumnReadScope.Begin();
            Assert.Same(a, pub.Rent(100));
        }
    }
}
