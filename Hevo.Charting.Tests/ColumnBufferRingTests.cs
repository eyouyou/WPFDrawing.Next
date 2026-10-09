using System;
using Hevo.Charting.LowCode;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 摄入器列缓冲轮换 + 列读者纪元:没有读者时复用退役缓冲(稳态不分配);在缓冲退役之前进场的读者没离场前,
    /// 这块缓冲不会被再交出去;已发布的那块永远不会被交出去。
    /// ColumnReaders 是全进程状态,本组测试不跟其它测试并行跑(别的测试登记的读者会挡住复用)。
    /// </summary>
    [Collection(nameof(ColumnReadersCollection))]
    public sealed class ColumnBufferRingTests
    {
        private static double[] PublishOnce(ColumnBufferRing<double> ring, int len)
        {
            var a = ring.Rent(len);
            ring.Published(a);
            return a;
        }

        [Fact]
        public void Rent_NeverReturnsPublishedBuffer()
        {
            var ring = new ColumnBufferRing<double>();
            var published = PublishOnce(ring, 100);
            for (int i = 0; i < 20; i++)
            {
                var next = ring.Rent(100);
                Assert.NotSame(published, next);
                ring.Published(next);
                published = next;
            }
        }

        [Fact]
        public void NoReaders_RetiredBufferIsReused()
        {
            var ring = new ColumnBufferRing<double>();
            var seen = new System.Collections.Generic.HashSet<double[]>(ReferenceEqualityComparer.Instance);
            int allocations = 0;
            for (int i = 0; i < 100; i++)
            {
                var a = ring.Rent(100);
                if (seen.Add(a)) allocations++;
                ring.Published(a);
            }
            // 没有读者时,稳态只在发布的那块 + 一块可写的之间轮换:100 次发布最多分配个位数块
            Assert.True(allocations <= 4, $"100 次发布分配了 {allocations} 块,退役缓冲没被复用");
        }

        [Fact]
        public void ReaderEnteredBeforeRetire_BlocksReuseUntilExit()
        {
            var ring = new ColumnBufferRing<double>();
            var held = PublishOnce(ring, 100);
            var reader = ColumnReaders.Enter();       // 读者在 held 退役之前进场(可能已拿到 held)
            PublishOnce(ring, 100);                   // held 退役
            for (int i = 0; i < 10; i++)
            {
                var a = ring.Rent(100);
                Assert.NotSame(held, a);              // 读者没离场,held 不能再被写
                ring.Published(a);
            }
            reader.Dispose();
        }

        [Fact]
        public void TooSmallRetiredBuffers_AreNotReused()
        {
            var ring = new ColumnBufferRing<double>();
            PublishOnce(ring, 10);
            PublishOnce(ring, 10);
            var big = ring.Rent(1000);
            Assert.True(big.Length >= 1000);
        }
    }

    [CollectionDefinition(nameof(ColumnReadersCollection), DisableParallelization = true)]
    public sealed class ColumnReadersCollection { }
}
