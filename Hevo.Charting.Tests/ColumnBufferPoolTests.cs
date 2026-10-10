using System;
using Hevo.Charting.LowCode;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 摄入器列缓冲轮换 + 列读者纪元:没有读者时复用退役缓冲(稳态不分配);在缓冲退役之前进场的读者没离场前,
    /// 这块缓冲不会被再交出去;已发布的那块永远不会被交出去。
    /// ColumnReadScope 是全进程状态,本组测试不跟其它测试并行跑(别的测试登记的读者会挡住复用)。
    /// </summary>
    [Collection(nameof(ColumnReadScopeCollection))]
    public sealed class ColumnBufferPoolTests
    {
        private static double[] PublishOnce(ColumnBufferPool<double> ring, int len)
        {
            var a = ring.Rent(len);
            ring.Published(a);
            return a;
        }

        [Fact]
        public void Rent_NeverReturnsPublishedBuffer()
        {
            var ring = new ColumnBufferPool<double>();
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
            var ring = new ColumnBufferPool<double>();
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
            var ring = new ColumnBufferPool<double>();
            var held = PublishOnce(ring, 100);
            var reader = ColumnReadScope.Begin();       // 读者在 held 被换下之前进场(可能已拿到 held)
            PublishOnce(ring, 100);                   // held 被换下(退役纪元在下一次 Rent 时打)
            for (int i = 0; i < 10; i++)
            {
                var a = ring.Rent(100);
                Assert.NotSame(held, a);              // 读者没离场,held 不能再被写
                ring.Published(a);
            }
            reader.Dispose();
        }

        [Fact]
        public void ReaderEnteredAfterPublishBeforeNextRent_IsProtected()
        {
            // 模拟端口镜像:写者发布 B(A 被换下)后、下一次 Rent 前,另一块黑板上的读者进场并拿到了 A。
            // 退役纪元若在 Published 时就打,读者纪元比它新,A 会被立刻回收重写。
            var ring = new ColumnBufferPool<double>();
            var a = PublishOnce(ring, 100);
            PublishOnce(ring, 100);                   // A 被换下
            var reader = ColumnReadScope.Begin();       // 读者此后才进场(拿到的可能是 A)
            try
            {
                for (int i = 0; i < 10; i++)
                {
                    var x = ring.Rent(100);
                    Assert.NotSame(a, x);
                    ring.Published(x);
                }
            }
            finally { reader.Dispose(); }
        }

        [Fact]
        public void DefaultScopeDispose_IsNoOp()
        {
            default(ColumnReadScope.Scope).Dispose();
            default(ColumnReadScope.Scope).Dispose();
            // 以前 default 跟溢出凭据编码相同,Dispose 会把溢出计数减成负数 → 全进程永远不可回收
            Assert.True(ColumnReadScope.IsReclaimable(0));
        }

        [Fact]
        public void OverflowScope_BlocksReclaimUntilDisposed()
        {
            var scopes = new System.Collections.Generic.List<ColumnReadScope.Scope>();
            try
            {
                for (int i = 0; i < 128; i++) scopes.Add(ColumnReadScope.Begin());   // 占满全部槽位
                var overflow = ColumnReadScope.Begin();
                scopes.ForEach(x => x.Dispose());
                scopes.Clear();
                Assert.False(ColumnReadScope.IsReclaimable(0));   // 溢出读者在场:谁都不回收
                overflow.Dispose();
                Assert.True(ColumnReadScope.IsReclaimable(0));
            }
            finally
            {
                scopes.ForEach(x => x.Dispose());
            }
        }

        [Fact]
        public void TooSmallRetiredBuffers_AreNotReused()
        {
            var ring = new ColumnBufferPool<double>();
            PublishOnce(ring, 10);
            PublishOnce(ring, 10);
            var big = ring.Rent(1000);
            Assert.True(big.Length >= 1000);
        }
    }

    [CollectionDefinition(nameof(ColumnReadScopeCollection), DisableParallelization = true)]
    public sealed class ColumnReadScopeCollection { }
}
