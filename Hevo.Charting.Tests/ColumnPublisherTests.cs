using System;
using System.Collections.Generic;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using Hevo.Charting.LowCode.Designer;
using Xunit;

namespace Hevo.Charting.Tests
{
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
