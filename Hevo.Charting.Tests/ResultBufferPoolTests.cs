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
    /// 调用点输出缓冲池:发布在任何端口上的缓冲不会被再交出去;在它退役之前进场的读者没离场前也不会;
    /// 租了没发布的立刻可再用;稳态下几乎不分配;同一块写到两个端口时两个都换掉才退役。
    /// ColumnReadScope 是全进程状态,跟 ColumnBufferPoolTests 同一组、不并行。
    /// </summary>
    [Collection(nameof(ColumnReadScopeCollection))]
    public sealed class ResultBufferPoolTests
    {
        private static readonly object PortA = new(), PortB = new();

        private static double[] CallOnce(ResultBufferPool buffers, int len, params object[] ports)
        {
            using var call = buffers.BeginCall();
            Assert.Same(buffers, ResultBufferPool.Current);
            var arr = buffers.Rent(len);
            foreach (var p in ports) call.Written(p, new ReadOnlyMemory<double>(arr, 0, len));
            return arr;
        }

        [Fact]
        public void Rent_NeverReturnsBufferPublishedOnAnyPort()
        {
            var b = new ResultBufferPool();
            var onA = CallOnce(b, 100, PortA);
            for (int i = 0; i < 20; i++)
            {
                var onB = CallOnce(b, 100, PortB);   // A 一直没换:它那块绝不能交出去
                Assert.NotSame(onA, onB);
            }
            Assert.Null(ResultBufferPool.Current);
        }

        [Fact]
        public void SteadyState_ReusesRetiredBuffers()
        {
            var b = new ResultBufferPool();
            var seen = new HashSet<double[]>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < 100; i++) seen.Add(CallOnce(b, 20_000, PortA));
            Assert.True(seen.Count <= 4, $"100 次发布分配了 {seen.Count} 块");
        }

        [Fact]
        public void ReaderEnteredBeforeRetire_BlocksReuse()
        {
            var b = new ResultBufferPool();
            var held = CallOnce(b, 100, PortA);
            var reader = ColumnReadScope.Begin();       // 可能已从端口拿到 held
            try
            {
                CallOnce(b, 100, PortA);              // held 被换下
                for (int i = 0; i < 10; i++) Assert.NotSame(held, CallOnce(b, 100, PortA));
            }
            finally { reader.Dispose(); }
        }

        [Fact]
        public void RentedButNotWritten_IsReusableImmediately()
        {
            var b = new ResultBufferPool();
            double[] unused;
            using (b.BeginCall()) unused = b.Rent(100);  // handler 返回了没写进端口的列
            var reader = ColumnReadScope.Begin();
            try { Assert.Same(unused, CallOnce(b, 100, PortA)); } // 没人见过它,读者在场也能立刻再用
            finally { reader.Dispose(); }
        }

        [Fact]
        public void SameBufferOnTwoPorts_RetiresOnlyWhenBothReplaced()
        {
            var b = new ResultBufferPool();
            var shared = CallOnce(b, 100, PortA, PortB);
            for (int i = 0; i < 5; i++) Assert.NotSame(shared, CallOnce(b, 100, PortA)); // B 还挂着 shared
            Assert.Contains(shared, b.PublishedForTest);
        }

        [Fact]
        public void ForeignArray_ReplacesPublishedAndIsNotTracked()
        {
            var b = new ResultBufferPool();
            var pooled = CallOnce(b, 100, PortA);
            var foreign = new double[100];
            using (var call = b.BeginCall()) call.Written(PortA, foreign);   // C# handler 自己 new 的
            Assert.Empty(b.PublishedForTest);
            Assert.Same(pooled, CallOnce(b, 100, PortA));                    // 没有读者:被换下的 pooled 可再用
        }
    }
}
