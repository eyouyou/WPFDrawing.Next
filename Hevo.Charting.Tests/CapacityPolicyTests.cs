using System;
using System.Collections.Generic;
using System.Threading;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>CapacityPolicy 扩缩容规则的边界,以及它在数据源快照 / 列缓冲环 / 调用点池上的效果。</summary>
    public sealed class CapacityPolicyTests
    {
        private static readonly CapacityPolicyOptions D = CapacityPolicyOptions.Default;

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 1 + 1024)]               // 小:余量取下限 MinReserve
        [InlineData(8192, 8192 + 1024)]         // 1/8 正好等于下限
        [InlineData(20000, 20000 + 2500)]       // 中:按 1/8
        [InlineData(524288, 524288 + 65536)]    // 1/8 正好等于上限
        [InlineData(2000000, 2000000 + 65536)]  // 大:余量封顶,线性增长
        public void Capacity_UsesClampedHeadroom(int required, int expected)
            => Assert.Equal(expected, CapacityPolicy.Capacity(required));

        [Fact]
        public void Headroom_IsAtMostOneEighth_AboveBlockSize_AndNeverAboveCap()
        {
            for (int n = 1; n < 5_000_000; n = n * 3 / 2 + 1)
            {
                int h = CapacityPolicy.Headroom(n);
                Assert.InRange(h, 1, D.MaxReserve);
                if (n >= D.MinReserve / D.ReserveRatio) Assert.True(h <= n / 8);
            }
        }

        [Fact]
        public void Resize_KeepsCapacity_WhileItFits_GrowsWhenShort_ShrinksBelowHalf()
        {
            int cap = CapacityPolicy.Capacity(20000);                     // 22500
            Assert.Equal(cap, CapacityPolicy.Resize(cap, 20001));         // 余量内不换
            Assert.Equal(cap, CapacityPolicy.Resize(cap, 22500));
            Assert.Equal(CapacityPolicy.Capacity(22501), CapacityPolicy.Resize(cap, 22501));
            Assert.Equal(cap, CapacityPolicy.Resize(cap, 11251));         // 一半以上不缩
            Assert.Equal(CapacityPolicy.Capacity(11000), CapacityPolicy.Resize(cap, 11000)); // 不到一半缩
            Assert.Equal(cap, CapacityPolicy.Resize(cap, 0));             // 0 不归调用方管(数据源自己清空)
        }

        [Fact]
        public void Shrink_NeverProducesLargerArray()
        {
            Assert.False(CapacityPolicy.ShouldShrink(1500, 10));          // 还在复用范围(10 + 2×1024)内:小数组不来回换
            Assert.True(CapacityPolicy.ShouldShrink(3000, 10));
            Assert.False(CapacityPolicy.ShouldShrink(1500, 800));         // 没到一半
            Assert.False(CapacityPolicy.ShouldShrink(1027, 2));           // 1027 → 1026 这种不缩
        }

        [Fact]
        public void Fits_RejectsTooSmall_AndWayTooLarge()
        {
            Assert.False(CapacityPolicy.Fits(19999, 20000));
            Assert.True(CapacityPolicy.Fits(20000, 20000));
            Assert.True(CapacityPolicy.Fits(25000, 20000));               // 20000 + 2×2500
            Assert.False(CapacityPolicy.Fits(25001, 20000));
            Assert.False(CapacityPolicy.Fits(2_000_000, 20000));          // 不拿超大旧缓冲装短列
        }

        // ---- 数据源快照 ----

        public readonly record struct Bar(double V);

        private sealed class GrowingSource : BufferedDataSource<GrowingSource, Bar>
        {
            // 不写 LogicalLength:基类默认就是最近一次推送的条数
            protected override bool ReserveSnapshotCapacity => true;
            public double LastViaItems() { lock (_lock) return SnapshotItems[^1].V; }
            public int ItemsLength() { lock (_lock) return SnapshotItems.Length; }
            public int Capacity => _readSnapshot.Length;
            public void Set(int n) { lock (_lock) { _buffer.Clear(); for (int i = 0; i < n; i++) _buffer.Add(new Bar(i)); Publish(); } }
            public void Append() { lock (_lock) { _buffer.Add(new Bar(_buffer.Count)); Publish(); } }
        }

        private sealed class LegacySource : BufferedDataSource<LegacySource, Bar>
        {
            public override int LogicalLength => _readSnapshot.Length;   // 旧写法(hevo.drawing 的数据源都是这样)
            public void Set(int n) { lock (_lock) { _buffer.Clear(); for (int i = 0; i < n; i++) _buffer.Add(new Bar(i)); Publish(); } }
            public void Append() { lock (_lock) { _buffer.Add(new Bar(_buffer.Count)); Publish(); } }
        }

        [Fact]
        public void OptedInSnapshot_AppendsWithoutReallocatingEveryBar()
        {
            var src = new GrowingSource();
            var published = new List<DataSnapshot<Bar>>();
            using var sub = src.Stream.Subscribe(s => published.Add(s));
            src.Set(20000);
            int reallocs = 0, cap = src.Capacity;
            for (int i = 0; i < 2000; i++)
            {
                src.Append();
                if (src.Capacity != cap) { reallocs++; cap = src.Capacity; }
            }
            Assert.True(reallocs <= 1, $"追加 2000 根换了 {reallocs} 次展示柜");
            Assert.Equal(22000, src.LogicalLength);
            var last = published[^1];
            Assert.Equal(22000, last.Count);
            Assert.Equal(21999, last.Items[last.Count - 1].V);
            Assert.InRange(src.Capacity, 22000, 22000 + 2 * CapacityPolicy.Headroom(22000));
        }

        [Fact]
        public void OptedInSnapshot_ShrinksWhenFarSmaller_AndEmptiesOnZero()
        {
            var src = new GrowingSource();
            src.Set(20000);
            src.Set(500);
            Assert.Equal(CapacityPolicy.Capacity(500), src.Capacity);
            Assert.Equal(500, src.LogicalLength);
            src.Set(0);
            Assert.Equal(0, src.Capacity);
            Assert.Equal(0, src.GetSnapshot().Count);
        }

        private sealed class DefaultLengthSource : BufferedDataSource<DefaultLengthSource, Bar>
        {
            public void Set(int n) { lock (_lock) { _buffer.Clear(); for (int i = 0; i < n; i++) _buffer.Add(new Bar(i)); Publish(); } }
        }

        [Fact]
        public void DefaultLogicalLength_IsPublishedCount_WithOrWithoutReserve()
        {
            var plain = new DefaultLengthSource();
            plain.Set(10); Assert.Equal(10, plain.LogicalLength);
            plain.Set(3); Assert.Equal(3, plain.LogicalLength);
            var reserved = new GrowingSource();
            reserved.Set(20000);
            Assert.True(reserved.Capacity > 20000);
            Assert.Equal(20000, reserved.LogicalLength);
            Assert.Equal(20000, reserved.ItemsLength());        // SnapshotItems 按条数切好
            Assert.Equal(19999, reserved.LastViaItems());
        }

        private sealed class ForgotToMigrateSource : BufferedDataSource<ForgotToMigrateSource, Bar>
        {
            public override int LogicalLength => _readSnapshot.Length;   // 开了预留空位却还按数组长度报
            protected override bool ReserveSnapshotCapacity => true;
            public void Set(int n) { lock (_lock) { _buffer.Clear(); for (int i = 0; i < n; i++) _buffer.Add(new Bar(i)); Publish(); } }
        }

        private sealed class CaptureListener : System.Diagnostics.TraceListener
        {
            public readonly List<string> Lines = new();
            public override void Write(string? message) { lock (Lines) Lines.Add(message ?? ""); }
            public override void WriteLine(string? message) { lock (Lines) Lines.Add(message ?? ""); }
        }

#if DEBUG
        [Fact]
        public void Debug_WarnsWhenReservedSourceStillReportsArrayLength()
        {
            var listener = new CaptureListener();
            System.Diagnostics.Trace.Listeners.Add(listener);
            try { new ForgotToMigrateSource().Set(100); }
            finally { System.Diagnostics.Trace.Listeners.Remove(listener); }
            lock (listener.Lines)
                Assert.Contains(listener.Lines, l => l.Contains(nameof(ForgotToMigrateSource)) && l.Contains("SnapshotCount"));
        }
#endif

        [Fact]
        public void LegacySnapshot_LengthAlwaysEqualsCount_IncludingShrink()
        {
            var src = new LegacySource();
            src.Set(100);
            Assert.Equal(100, src.LogicalLength);
            src.Append();
            Assert.Equal(101, src.LogicalLength);
            src.Set(50);                                               // 以前变短时数组还是 101,LogicalLength 报错
            Assert.Equal(50, src.LogicalLength);
            src.Set(0);
            Assert.Equal(0, src.LogicalLength);
        }
    }

    /// <summary>列缓冲环 / 调用点池按 CapacityPolicy 选缓冲,并遵守空闲字节上限。全进程 ColumnReadScope,不并行。</summary>
    [Collection(nameof(ColumnReadScopeCollection))]
    public sealed class CapacityPolicyPoolTests
    {
        [Fact]
        public void Ring_DoesNotReuseWayTooLargeBuffer_ForShortColumn()
        {
            var ring = new ColumnBufferPool<double>();
            var big = ring.Rent(1_000_000); ring.Published(big);
            var big2 = ring.Rent(1_000_000); ring.Published(big2);     // big 退役
            var small = ring.Rent(1000);
            Assert.NotSame(big, small);
            Assert.True(small.Length < 10_000);
        }

        [Fact]
        public void Ring_RespectsMaximumFreeBytes()
        {
            var opts = new CapacityPolicyOptions { MaxIdleBytes = 1 };   // 只留最新一块
            var ring = new ColumnBufferPool<double>(opts);
            var x = new double[1124];
            var y = new double[1124];
            ring.Unused(x);
            ring.Unused(y);                                              // 超上限:x 被挤掉(至少留最新一块)
            Assert.Same(y, ring.Rent(100));
            Assert.NotSame(x, ring.Rent(100));                           // 没有上限的话这里会拿到 x
        }

        [Fact]
        public void CallBuffers_RespectsMaximumFreeBytes_AndFits()
        {
            var opts = new CapacityPolicyOptions { MaxIdleBytes = 1 };
            var cb = new ResultBufferPool(opts);
            var port = new object();
            double[] Once(int n) { using var call = cb.BeginCall(); var a = cb.Rent(n); call.Written(port, new ReadOnlyMemory<double>(a, 0, n)); return a; }
            var a1 = Once(20000);
            var a2 = Once(20000);
            var a3 = Once(20000);
            Assert.Same(a1, a3);                                         // 稳态在两块之间轮换
            var tiny = Once(100);
            Assert.NotSame(a2, tiny);                                    // 20000 的旧缓冲不拿来装 100
            Assert.True(tiny.Length < 2000);
        }
    }
}
