using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using Hevo.Charting.Renderers;
using Hevo.Charting.WorkFlow;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 数据源 → pipe 摄入器(LinkStream Map)写进黑板的列,被锁外读者读到时必须完整:
    /// 一次 Publish 里所有元素填同一个代号,读者(图层录制、ComputeFeature handler)读到的列应该全是同一个代号、
    /// 不混默认值、不混别的代号。摄入器在写锁内原地复用同一块数组(先 Fill 默认值再重填),
    /// 读者只在读锁内取 ROM 引用、出锁后才读元素 —— 后台推送时会读到写了一半的列;
    /// 扩容时旧数组 Return 给 ArrayPool,还拿着它的读者会读到别人租走后写进去的数据。
    /// 测试只用现有公开机制(真 ChartCell 帧 + 真 ComputeFeature),修复前后不改测试。
    /// </summary>
    public sealed class IngestorColumnConsistencyTests
    {
        public readonly record struct GenItem(double Gen);

        public sealed class GenSource : BufferedDataSource<GenSource, GenItem>
        {
            private int _count;
            public override int LogicalLength => Volatile.Read(ref _count);

            public void PublishGen(double gen, int length)
            {
                lock (_lock)
                {
                    _buffer.Clear();
                    for (int i = 0; i < length; i++) _buffer.Add(new GenItem(gen));
                    Volatile.Write(ref _count, length);
                    Publish();
                }
            }
        }

        /// <summary>扫一遍列:全部等于第 0 个元素且不为 0(0 = ScatterIngestor 的 Fill 默认值)。</summary>
        public sealed class ScanStats
        {
            public int Scans, Bad;
            public string? First;

            public void Scan(ReadOnlySpan<double> col, string who)
            {
                if (col.Length == 0) return;
                Interlocked.Increment(ref Scans);
                double g = col[0];
                for (int i = 0; i < col.Length; i++)
                {
                    double v = col[i]; // 只读一次:报告里的值就是判定用的值
                    if (v != g || g <= 0)
                    {
                        Interlocked.Increment(ref Bad);
                        Interlocked.CompareExchange(ref First, $"{who}: 第 {i} 个元素 {v},第 0 个 {g},长度 {col.Length}", null);
                        return;
                    }
                }
            }
        }

        internal sealed record ColumnTrait(ReadOnlyMemory<double> Col);

        /// <summary>在图层录制(帧内、锁外)时扫列。</summary>
        internal sealed class ScanLayer : ChartLayer
        {
            private readonly ScanStats _stats;
            public ScanLayer(ScanStats stats) { _stats = stats; Name = "scan"; Level = ChartLayerType.Main; }

            protected override void OnUpdate(IVisualData data, IDrawingSink drawSink, WidgetBuffer widgetSink)
            {
                if (data.Get<ColumnTrait>() is { } t) _stats.Scan(t.Col.Span, "图层录制");
            }
        }

        internal sealed class ScanFeature : Feature
        {
            private readonly DataPort<ReadOnlyMemory<double>> _port;
            private readonly ScanLayer _layer;
            public ScanFeature(DataPort<ReadOnlyMemory<double>> port, ScanStats stats) { _port = port; _layer = new ScanLayer(stats); }
            protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) => AttachLayer(_layer);
            protected override void OnProject(FeatureContext ctx)
            {
                var (col, changed) = ctx.UsePort(_port);
                if (changed) ctx.For(_layer).PublishData(new ColumnTrait(col));
            }
        }

        internal sealed class RaceSchema : ChartReactiveSchema
        {
            public GenSource Source { get; } = new();
            public DataPort<ReadOnlyMemory<double>> Scatter { get; } = new("RC_Scatter");   // ScatterIngestor:Fill 默认值再重填
            public DataPort<ReadOnlyMemory<double>> Mapped { get; } = new("RC_Mapped");     // FastSourceMapIngestor:原地覆盖
            private readonly Feature[] _features;
            public RaceSchema(Func<RaceSchema, Feature[]> features) => _features = features(this);

            protected override void DefineDataFlow(ChartCell chart)
            {
                Source.Pipe()
                    .LinkStream(cfg => cfg
                        .Map(Scatter, x => x.Gen)
                        .Map(Mapped, (x, _) => x.Gen))
                    .BindTo(chart);
            }

            protected override void DefineFeatures(IFeatureContext canvas)
            {
                foreach (var f in _features) canvas.Add(f);
            }
        }

        private static ComputeFeature ScanCompute(DataPort<ReadOnlyMemory<double>> input, ScanStats stats, string name,
            Action<ReadOnlyMemory<double>>? beforeScan = null)
            => new()
            {
                InputPort = input,
                OutputPort = new DataPort<ReadOnlyMemory<double>>($"RC_Out_{name}"),
                Compute = (Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>)(col =>
                {
                    beforeScan?.Invoke(col);
                    stats.Scan(col.Span, name);
                    return new double[] { col.Length };
                }),
            };

        /// <summary>
        /// 后台线程不停 Publish(长度 1000 / 1500 交替),UI 线程不停跑帧(图层录制扫列),
        /// 同时 ComputeFeature 在线程池扫输入列。两种摄入器(Scatter / FastSourceMap)各自检查。
        /// </summary>
        [Fact]
        public void Columns_ReadOutsideLock_AreNeverHalfWritten()
        {
            var layerScatter = new ScanStats();
            var layerMapped = new ScanStats();
            var computeScatter = new ScanStats();
            var computeMapped = new ScanStats();
            IncrementalTestKit.RunOnSta(() =>
            {
                var schema = new RaceSchema(s => new Feature[]
                {
                    new ScanFeature(s.Scatter, layerScatter),
                    new ScanFeature(s.Mapped, layerMapped),
                    ScanCompute(s.Scatter, computeScatter, "compute-scatter"),
                    ScanCompute(s.Mapped, computeMapped, "compute-mapped"),
                });
                var cell = new ChartCell { Template = schema };
                using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);
                schema.Source.PublishGen(1, 1500);
                schema.InvalidateEnvironment(); // 首帧全量:Feature 第一次 UsePort 完成订阅
                cell.RunFrameNow(PlotMode.Sync);

                bool stop = false;
                var writer = new Thread(() =>
                {
                    double gen = 2;
                    while (!Volatile.Read(ref stop)) { schema.Source.PublishGen(gen, (int)gen % 2 == 0 ? 1000 : 1500); gen++; }
                }) { IsBackground = true };
                writer.Start();
                var until = DateTime.UtcNow.AddMilliseconds(1500);
                while (DateTime.UtcNow < until) cell.RunFrameNow(PlotMode.Sync);
                Volatile.Write(ref stop, true);
                writer.Join();
                Thread.Sleep(200); // 让在途的 ComputeFeature 跑完
            });

            var all = new[] { (layerScatter, "图层/Scatter"), (layerMapped, "图层/Map"), (computeScatter, "Compute/Scatter"), (computeMapped, "Compute/Map") };
            var report = string.Join(";", Array.ConvertAll(all, x => $"{x.Item2} {x.Item1.Bad}/{x.Item1.Scans}"));
            Assert.True(layerScatter.Scans > 0 && computeScatter.Scans > 0, "读者没跑起来:" + report);
            foreach (var (s, name) in all)
                Assert.True(s.Bad == 0, $"{name} 读到不完整的列。{report}。例:{s.First}");
        }

        /// <summary>
        /// 确定性复现"扩容后旧数组还给 ArrayPool":handler 拿到长度 1000 的列、扫之前停住;
        /// 主线程把列扩到 3000(摄入器把旧数组 Return 给 ArrayPool),再从 ArrayPool 租同尺寸数组写成 -7;
        /// handler 继续扫,读到的必须仍是原来那一代。
        /// </summary>
        [Fact]
        public void Column_HeldByReader_IsNotHandedToSomeoneElseOnGrowth()
        {
            var stats = new ScanStats();
            using var inHandler = new ManualResetEventSlim(false);
            using var proceed = new ManualResetEventSlim(false);
            int gate = 0;
            IncrementalTestKit.RunOnSta(() =>
            {
                var schema = new RaceSchema(s => new Feature[]
                {
                    ScanCompute(s.Scatter, stats, "compute-scatter", col =>
                    {
                        if (col.Length != 1000 || Interlocked.Exchange(ref gate, 1) != 0) return;
                        inHandler.Set();
                        proceed.Wait(TimeSpan.FromSeconds(10));
                    }),
                });
                var cell = new ChartCell { Template = schema };
                using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);

                schema.Source.PublishGen(5, 1000);
                Assert.True(inHandler.Wait(TimeSpan.FromSeconds(10)), "handler 没被触发");

                schema.Source.PublishGen(6, 3000);          // 扩容:旧数组以前会被 Return 给 ArrayPool(本线程的 TLS 缓存)
                var stolen = ArrayPool<double>.Shared.Rent(1000);
                Array.Fill(stolen, -7);                       // 别人租走后写入
                proceed.Set();
                Thread.Sleep(300);
                ArrayPool<double>.Shared.Return(stolen);
            });
            Assert.True(stats.Scans > 0, "handler 没扫到列");
            Assert.True(stats.Bad == 0, $"持有中的列被别人改写:{stats.First}(共 {stats.Bad}/{stats.Scans})");
        }
    }
}
