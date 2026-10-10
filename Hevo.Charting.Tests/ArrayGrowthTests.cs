using System;
using System.Collections.Generic;
using System.Threading;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>ArrayGrowth 扩缩容规则的边界,以及它在数据源快照 / 列缓冲环 / 调用点池上的效果。</summary>
    public sealed class ArrayGrowthTests
    {
        private static readonly ArrayGrowthOptions D = ArrayGrowthOptions.Default;

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 1 + 1024)]               // 小:余量取下限 BlockSize
        [InlineData(8192, 8192 + 1024)]         // 1/8 正好等于下限
        [InlineData(20000, 20000 + 2500)]       // 中:按 1/8
        [InlineData(524288, 524288 + 65536)]    // 1/8 正好等于上限
        [InlineData(2000000, 2000000 + 65536)]  // 大:余量封顶,线性增长
        public void Capacity_UsesClampedHeadroom(int required, int expected)
            => Assert.Equal(expected, ArrayGrowth.Capacity(required));

        [Fact]
        public void Headroom_IsAtMostOneEighth_AboveBlockSize_AndNeverAboveCap()
        {
            for (int n = 1; n < 5_000_000; n = n * 3 / 2 + 1)
            {
                int h = ArrayGrowth.Headroom(n);
                Assert.InRange(h, 1, D.LargeBufferMultiple);
                if (n >= D.BlockSize * D.GrowthDivisor) Assert.True(h <= n / 8);
            }
        }

        [Fact]
        public void Resize_KeepsCapacity_WhileItFits_GrowsWhenShort_ShrinksBelowHalf()
        {
            int cap = ArrayGrowth.Capacity(20000);                     // 22500
            Assert.Equal(cap, ArrayGrowth.Resize(cap, 20001));         // 余量内不换
            Assert.Equal(cap, ArrayGrowth.Resize(cap, 22500));
            Assert.Equal(ArrayGrowth.Capacity(22501), ArrayGrowth.Resize(cap, 22501));
            Assert.Equal(cap, ArrayGrowth.Resize(cap, 11251));         // 一半以上不缩
            Assert.Equal(ArrayGrowth.Capacity(11000), ArrayGrowth.Resize(cap, 11000)); // 不到一半缩
            Assert.Equal(cap, ArrayGrowth.Resize(cap, 0));             // 0 不归调用方管(数据源自己清空)
        }

        [Fact]
        public void Shrink_NeverProducesLargerArray()
        {
            Assert.False(ArrayGrowth.ShouldShrink(1500, 10));          // 还在复用范围(10 + 2×1024)内:小数组不来回换
            Assert.True(ArrayGrowth.ShouldShrink(3000, 10));
            Assert.False(ArrayGrowth.ShouldShrink(1500, 800));         // 没到一半
            Assert.False(ArrayGrowth.ShouldShrink(1027, 2));           // 1027 → 1026 这种不缩
        }

        [Fact]
        public void Fits_RejectsTooSmall_AndWayTooLarge()
        {
            Assert.False(ArrayGrowth.Fits(19999, 20000));
            Assert.True(ArrayGrowth.Fits(20000, 20000));
            Assert.True(ArrayGrowth.Fits(25000, 20000));               // 20000 + 2×2500
            Assert.False(ArrayGrowth.Fits(25001, 20000));
            Assert.False(ArrayGrowth.Fits(2_000_000, 20000));          // 不拿超大旧缓冲装短列
        }

        // ---- 数据源快照 ----

        public readonly record struct Bar(double V);

        private sealed class GrowingSource : BufferedDataSource<GrowingSource, Bar>
        {
            public override int LogicalLength => PublishedCount;   // 默认开余量,不用 override SnapshotGrowth
            public int Capacity => _readSnapshot.Length;
            public void Set(int n) { lock (_lock) { _buffer.Clear(); for (int i = 0; i < n; i++) _buffer.Add(new Bar(i)); Publish(); } }
            public void Append() { lock (_lock) { _buffer.Add(new Bar(_buffer.Count)); Publish(); } }
        }

        private sealed class LegacySource : BufferedDataSource<LegacySource, Bar>
        {
            public override int LogicalLength => _readSnapshot.Length;   // 旧写法(hevo.drawing 的数据源都是这样)
            protected override ArrayGrowthOptions? SnapshotGrowth => null; // 旧写法必须关掉余量
            public void Set(int n) { lock (_lock) { _buffer.Clear(); for (int i = 0; i < n; i++) _buffer.Add(new Bar(i)); Publish(); } }
            public void Append() { lock (_lock) { _buffer.Add(new Bar(_buffer.Count)); Publish(); } }
        }

        [Fact]
        public void DefaultSnapshot_AppendsWithoutReallocatingEveryBar()
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
            Assert.InRange(src.Capacity, 22000, 22000 + 2 * ArrayGrowth.Headroom(22000));
        }

        [Fact]
        public void DefaultSnapshot_ShrinksWhenFarSmaller_AndEmptiesOnZero()
        {
            var src = new GrowingSource();
            src.Set(20000);
            src.Set(500);
            Assert.Equal(ArrayGrowth.Capacity(500), src.Capacity);
            Assert.Equal(500, src.LogicalLength);
            src.Set(0);
            Assert.Equal(0, src.Capacity);
            Assert.Equal(0, src.GetSnapshot().Count);
        }

        private sealed class ForgotToMigrateSource : BufferedDataSource<ForgotToMigrateSource, Bar>
        {
            public override int LogicalLength => _readSnapshot.Length;   // 没改:默认留余量后报的是容量
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
        public void Debug_WarnsWhenLogicalLengthStillUsesArrayLength()
        {
            var listener = new CaptureListener();
            System.Diagnostics.Trace.Listeners.Add(listener);
            try { new ForgotToMigrateSource().Set(100); }
            finally { System.Diagnostics.Trace.Listeners.Remove(listener); }
            lock (listener.Lines)
                Assert.Contains(listener.Lines, l => l.Contains(nameof(ForgotToMigrateSource)) && l.Contains("PublishedCount"));
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

    /// <summary>列缓冲环 / 调用点池按 ArrayGrowth 选缓冲,并遵守空闲字节上限。全进程 ColumnReaders,不并行。</summary>
    [Collection(nameof(ColumnReadersCollection))]
    public sealed class ArrayGrowthPoolTests
    {
        [Fact]
        public void Ring_DoesNotReuseWayTooLargeBuffer_ForShortColumn()
        {
            var ring = new ColumnBufferRing<double>();
            var big = ring.Rent(1_000_000); ring.Published(big);
            var big2 = ring.Rent(1_000_000); ring.Published(big2);     // big 退役
            var small = ring.Rent(1000);
            Assert.NotSame(big, small);
            Assert.True(small.Length < 10_000);
        }

        [Fact]
        public void Ring_RespectsMaximumFreeBytes()
        {
            var opts = new ArrayGrowthOptions { MaximumFreeBytes = 1 };   // 只留最新一块
            var ring = new ColumnBufferRing<double>(opts);
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
            var opts = new ArrayGrowthOptions { MaximumFreeBytes = 1 };
            var cb = new ColumnCallBuffers(opts);
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
