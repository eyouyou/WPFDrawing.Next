using System;
using System.Threading;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// FastSourceMapIngestor / FastStateMapIngestor(AutoMap 生成的就是这类 Map)的通知语义:
    /// 内容没变不通知(tick 时 Time / 历史列不变,不能叫醒依赖它们的 Feature);长度不变但内容变了要通知
    /// (以前原地覆盖 + 按 ROM 判等会漏掉这种更新)。
    /// </summary>
    public sealed class MapIngestorNotificationTests
    {
        public readonly record struct Row(double A, double B);

        public sealed class RowSource : BufferedDataSource<RowSource, Row>
        {
            private int _count;
            public override int LogicalLength => Volatile.Read(ref _count);

            public void PublishRows(params Row[] rows)
            {
                lock (_lock)
                {
                    _buffer.Clear();
                    _buffer.AddRange(rows);
                    Volatile.Write(ref _count, rows.Length);
                    Publish();
                }
            }
        }

        private sealed class Rig
        {
            public RowSource Source { get; } = new();
            public DataPort<ReadOnlyMemory<double>> ColA { get; } = new("MN_A");   // FastSourceMapIngestor
            public DataPort<ReadOnlyMemory<double>> ColB { get; } = new("MN_B");   // FastStateMapIngestor
            public DataBlackboard Board = null!;
            public int NotifyA, NotifyB;

            public Rig()
            {
                Source.Pipe()
                    .LinkStream(cfg => cfg
                        .Map(ColA, (x, _) => x.A)
                        .Map(ColB, 10.0, (x, _, k) => x.B * k))
                    .Seal()
                    .Subscribe(b =>
                    {
                        if (Board != null) return;
                        Board = b;
                        b.Subscribe(ColA, _ => NotifyA++);
                        b.Subscribe(ColB, _ => NotifyB++);
                    });
            }

            public double[] Read(DataPort<ReadOnlyMemory<double>> port)
            {
                using (Board.AcquireReadLock()) return Board.Read(port).ToArray();
            }
        }

        [Fact]
        public void SameContent_DoesNotNotify()
        {
            var rig = new Rig();
            rig.Source.PublishRows(new Row(1, 2), new Row(3, 4));
            int a0 = rig.NotifyA, b0 = rig.NotifyB;

            rig.Source.PublishRows(new Row(1, 2), new Row(3, 4));
            rig.Source.PublishRows(new Row(1, 2), new Row(3, 4));

            Assert.Equal(a0, rig.NotifyA);
            Assert.Equal(b0, rig.NotifyB);
            Assert.Equal(new[] { 1.0, 3.0 }, rig.Read(rig.ColA));
            Assert.Equal(new[] { 20.0, 40.0 }, rig.Read(rig.ColB));
        }

        [Fact]
        public void SameLengthChangedContent_Notifies()
        {
            var rig = new Rig();
            rig.Source.PublishRows(new Row(1, 2), new Row(3, 4));
            int a0 = rig.NotifyA, b0 = rig.NotifyB;

            rig.Source.PublishRows(new Row(1, 2), new Row(5, 4));   // 只有 A 列最后一个变了

            Assert.Equal(a0 + 1, rig.NotifyA);
            Assert.Equal(b0, rig.NotifyB);                           // B 列内容没变
            Assert.Equal(new[] { 1.0, 5.0 }, rig.Read(rig.ColA));
        }

        [Fact]
        public void LengthChange_Notifies()
        {
            var rig = new Rig();
            rig.Source.PublishRows(new Row(1, 2));
            int a0 = rig.NotifyA, b0 = rig.NotifyB;

            rig.Source.PublishRows(new Row(1, 2), new Row(3, 4));

            Assert.Equal(a0 + 1, rig.NotifyA);
            Assert.Equal(b0 + 1, rig.NotifyB);
        }

        [Fact]
        public void NaNColumns_AreComparedByValue()
        {
            var rig = new Rig();
            rig.Source.PublishRows(new Row(double.NaN, 1), new Row(2, 1));
            int a0 = rig.NotifyA;
            rig.Source.PublishRows(new Row(double.NaN, 1), new Row(2, 1));
            Assert.Equal(a0, rig.NotifyA);   // NaN 跟 NaN 视为相同,不通知
        }
    }
}
