using System;
using System.Threading;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using Hevo.Charting.Renderers;
using Hevo.Charting.WorkFlow;
using Xunit;
using static Hevo.Charting.Tests.IngestorColumnConsistencyTests;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 其余摄入器的锁外读一致性(同 <see cref="IngestorColumnConsistencyTests"/> 的框架):
    /// FastStateMap、WindowMap、SequenceTransform、TimeAxisCoordinator。后台线程不停 Publish(一次 Publish 内所有元素同一代号),
    /// UI 线程跑帧(图层录制扫列),ComputeFeature 在线程池扫列;读到的列必须全是同一代号。
    /// WindowMap 前 period 个是 NaN,扫描从 period 开始;TimeAxisCoordinator 的时间列按"代号 + 下标"编码,一并检查。
    /// </summary>
    public sealed class IngestorColumnConsistencyMoreTests
    {
        private const int Period = 5;

        private sealed class IdentityTransform : ISequenceTransform
        {
            public void Transform(ReadOnlySpan<double> source, Span<double> target) => source.CopyTo(target);
        }

        public readonly record struct TimedItem(DateTime Time, double Gen) : ITimePoint;

        public sealed class TimedSource : BufferedDataSource<TimedSource, TimedItem>
        {
            private int _count;
            public override int LogicalLength => Volatile.Read(ref _count);

            public void PublishGen(double gen, int length)
            {
                lock (_lock)
                {
                    _buffer.Clear();
                    for (int i = 0; i < length; i++) _buffer.Add(new TimedItem(TimeOf(gen, i), gen));
                    Volatile.Write(ref _count, length);
                    Publish();
                }
            }
        }

        private static readonly DateTime Origin = new(2000, 1, 1);
        private static DateTime TimeOf(double gen, int i) => Origin.AddTicks((long)gen * 1_000_000 + i);

        /// <summary>按 DateTime 列扫:time[i] 必须等于 time[0] + i 个 tick(同一代)。</summary>
        private sealed class TimeColumnTrait
        {
            public int Scans, Bad;
            public string? First;

            public void Scan(ReadOnlySpan<DateTime> col, string who)
            {
                if (col.Length == 0) return;
                Interlocked.Increment(ref Scans);
                long t0 = col[0].Ticks;
                for (int i = 0; i < col.Length; i++)
                {
                    long t = col[i].Ticks;
                    if (t != t0 + i || t0 <= Origin.Ticks)
                    {
                        Interlocked.Increment(ref Bad);
                        Interlocked.CompareExchange(ref First, $"{who}: 第 {i} 个 {t - Origin.Ticks},第 0 个 {t0 - Origin.Ticks},长度 {col.Length}", null);
                        return;
                    }
                }
            }
        }

        private sealed class RaceSchema2 : ChartReactiveSchema
        {
            private readonly Action<ChartCell> _flow;
            private readonly Feature[] _features;
            public RaceSchema2(Action<ChartCell> flow, Feature[] features) { _flow = flow; _features = features; }
            protected override void DefineDataFlow(ChartCell chart) => _flow(chart);
            protected override void DefineFeatures(IFeatureContext canvas) { foreach (var f in _features) canvas.Add(f); }
        }

        private static ComputeFeature ScanCompute(DataPort<ReadOnlyMemory<double>> input, ScanStats stats, string name, int skip)
            => new()
            {
                InputPort = input,
                OutputPort = new DataPort<ReadOnlyMemory<double>>($"RC2_Out_{name}"),
                Compute = (Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>)(col =>
                {
                    stats.Scan(col.Span.Slice(Math.Min(skip, col.Length)), name);
                    return new double[] { col.Length };
                }),
            };

        private sealed class SkipScanFeature : Feature
        {
            private readonly DataPort<ReadOnlyMemory<double>> _port;
            private readonly int _skip;
            private readonly ScanLayer _layer;
            public SkipScanFeature(DataPort<ReadOnlyMemory<double>> port, ScanStats stats, int skip) { _port = port; _skip = skip; _layer = new ScanLayer(stats); }
            protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) => AttachLayer(_layer);
            protected override void OnProject(FeatureContext ctx)
            {
                var (col, changed) = ctx.UsePort(_port);
                if (changed) ctx.For(_layer).PublishData(new ColumnTrait(col.Slice(Math.Min(_skip, col.Length))));
            }
        }

        private sealed class TimeScanFeature : Feature
        {
            private readonly DataPort<ReadOnlyMemory<DateTime>> _port;
            private readonly TimeLayer _layer;
            public TimeScanFeature(DataPort<ReadOnlyMemory<DateTime>> port, TimeColumnTrait stats) { _port = port; _layer = new TimeLayer(stats); }
            protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) => AttachLayer(_layer);
            protected override void OnProject(FeatureContext ctx)
            {
                var (col, changed) = ctx.UsePort(_port);
                if (changed) ctx.For(_layer).PublishData(new TimeTrait(col));
            }

            internal sealed record TimeTrait(ReadOnlyMemory<DateTime> Col);

            private sealed class TimeLayer : ChartLayer
            {
                private readonly TimeColumnTrait _stats;
                public TimeLayer(TimeColumnTrait stats) { _stats = stats; Name = "time-scan"; Level = ChartLayerType.Main; }
                protected override void OnUpdate(IVisualData data, IDrawingSink drawSink, WidgetBuffer widgetSink)
                {
                    if (data.Get<TimeTrait>() is { } t) _stats.Scan(t.Col.Span, "图层录制/时间");
                }
            }
        }

        /// <summary>跑 1.5 秒:后台 publish(gen, 长度 1000/1500 交替),UI 线程连续跑帧。</summary>
        private static void Race(Func<RaceSchema2> create, Action<double, int> publish)
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var schema = create();
                var cell = new ChartCell { Template = schema };
                using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);
                publish(1, 1500);
                Thread.Sleep(50); // TimeAxisCoordinator 经脉冲异步合并
                schema.InvalidateEnvironment();
                cell.RunFrameNow(PlotMode.Sync);

                bool stop = false;
                var writer = new Thread(() =>
                {
                    double gen = 2;
                    while (!Volatile.Read(ref stop)) { publish(gen, (int)gen % 2 == 0 ? 1000 : 1500); gen++; }
                }) { IsBackground = true };
                writer.Start();
                var until = DateTime.UtcNow.AddMilliseconds(1500);
                while (DateTime.UtcNow < until) cell.RunFrameNow(PlotMode.Sync);
                Volatile.Write(ref stop, true);
                writer.Join();
                Thread.Sleep(200);
            });
        }

        private static void AssertClean(params (ScanStats Stats, string Name)[] all)
        {
            var report = string.Join(";", Array.ConvertAll(all, x => $"{x.Name} {x.Stats.Bad}/{x.Stats.Scans}"));
            foreach (var (s, name) in all)
            {
                Assert.True(s.Scans > 0, $"{name} 读者没跑起来:" + report);
                Assert.True(s.Bad == 0, $"{name} 读到不完整的列。{report}。例:{s.First}");
            }
        }

        [Fact]
        public void FastStateMap_Columns_AreNeverHalfWritten()
        {
            var source = new GenSource();
            var port = new DataPort<ReadOnlyMemory<double>>("RC2_State");
            ScanStats layer = new(), compute = new();
            Func<RaceSchema2> schema = () => new RaceSchema2(
                chart => source.Pipe().LinkStream(cfg => cfg.Map(port, 1.0, (x, _, k) => x.Gen * k)).BindTo(chart),
                new Feature[] { new SkipScanFeature(port, layer, 0), ScanCompute(port, compute, "state", 0) });
            Race(schema, source.PublishGen);
            AssertClean((layer, "图层/FastStateMap"), (compute, "Compute/FastStateMap"));
        }

        [Fact]
        public void WindowMap_Columns_AreNeverHalfWritten()
        {
            var source = new GenSource();
            var port = new DataPort<ReadOnlyMemory<double>>("RC2_Window");
            ScanStats layer = new(), compute = new();
            Func<RaceSchema2> schema = () => new RaceSchema2(
                chart => source.Pipe().LinkStream(cfg => cfg.MapWindow(port, Period, (x, _) => x.Gen, (cur, _) => cur)).BindTo(chart),
                new Feature[] { new SkipScanFeature(port, layer, Period), ScanCompute(port, compute, "window", Period) });
            Race(schema, source.PublishGen);
            AssertClean((layer, "图层/WindowMap"), (compute, "Compute/WindowMap"));
        }

        [Fact]
        public void SequenceTransform_Columns_AreNeverHalfWritten()
        {
            var source = new GenSource();
            var port = new DataPort<ReadOnlyMemory<double>>("RC2_Transform");
            ScanStats layer = new(), compute = new();
            Func<RaceSchema2> schema = () => new RaceSchema2(
                chart => source.Pipe().LinkStream(cfg => cfg.ApplyTransform(port, (x, _) => x.Gen, new IdentityTransform())).BindTo(chart),
                new Feature[] { new SkipScanFeature(port, layer, 0), ScanCompute(port, compute, "transform", 0) });
            Race(schema, source.PublishGen);
            AssertClean((layer, "图层/SequenceTransform"), (compute, "Compute/SequenceTransform"));
        }

        [Fact]
        public void TimeAxisCoordinator_Columns_AreNeverHalfWritten()
        {
            var source = new TimedSource();
            var timePort = new DataPort<ReadOnlyMemory<DateTime>>("RC2_Time");
            var valuePort = new DataPort<ReadOnlyMemory<double>>("RC2_TimeValue");
            ScanStats layer = new(), compute = new();
            var time = new TimeColumnTrait();
            Func<RaceSchema2> schema = () => new RaceSchema2(
                chart => new TimeAxisCoordinator(timePort).AddSource(source, (Func<TimedItem, double>)(x => x.Gen), valuePort).BindTo(chart),
                new Feature[] { new SkipScanFeature(valuePort, layer, 0), ScanCompute(valuePort, compute, "coord", 0), new TimeScanFeature(timePort, time) });
            Race(schema, source.PublishGen);
            AssertClean((layer, "图层/TimeAxisCoordinator"), (compute, "Compute/TimeAxisCoordinator"));
            Assert.True(time.Scans > 0, "时间列读者没跑起来");
            Assert.True(time.Bad == 0, $"时间列读到不完整的数据 {time.Bad}/{time.Scans}。例:{time.First}");
        }
    }
}
