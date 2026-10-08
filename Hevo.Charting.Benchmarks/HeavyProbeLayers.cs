using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.Renderers;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Benchmarks
{
    internal sealed record HeavyTrait(long Frame);

    /// <summary>
    /// render-probe --heavy-layers=N --heavy-ms=X:人为加重的图层,每次录制忙等 X 毫秒(纯 CPU,不碰 WPF 对象,
    /// 可以安全进线程池),画 1 个矩形。用来测 PlotMode.Parallel 在"图层录制真的很重"时能不能并行出收益。
    /// </summary>
    internal sealed class HeavyProbeLayer : ChartLayer
    {
        private static readonly IHevoBrush Brush = new HevoSolidBrush(Colors.DimGray);
        private readonly double _ms;

        public HeavyProbeLayer(string name, double ms)
        {
            Name = name;
            _ms = ms;
            Level = ChartLayerType.Main;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink drawSink, WidgetBuffer widgetSink)
        {
            var t = data.Get<HeavyTrait>();
            long until = Stopwatch.GetTimestamp() + (long)(_ms * Stopwatch.Frequency / 1000.0);
            double acc = 0;
            while (Stopwatch.GetTimestamp() < until) acc += Math.Sqrt(acc + 1);
            drawSink.DrawRectangle(Brush, null, new HevoRect((float)((t?.Frame ?? 0) % 50 + acc % 1), 0, 4, 4));
        }
    }

    /// <summary>挂 N 个 <see cref="HeavyProbeLayer"/>;每次投影给每层发一个新 trait(全量模式下每帧投影 → 每层每帧重录)。</summary>
    internal sealed class HeavyLayersFeature : Feature
    {
        private readonly int _count;
        private readonly double _ms;
        private readonly List<HeavyProbeLayer> _layers = new();
        private long _frame;

        public HeavyLayersFeature(int count, double ms) { _count = count; _ms = ms; }

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            for (int i = 0; i < _count; i++)
            {
                var layer = new HeavyProbeLayer($"Heavy{i}", _ms);
                _layers.Add(layer);
                AttachLayer(layer);
            }
        }

        protected override void OnProject(FeatureContext ctx)
        {
            _frame++;
            foreach (var l in _layers) ctx.For(l).PublishData(new HeavyTrait(_frame));
        }
    }

    internal static class ParallelOverhead
    {
        /// <summary>Parallel.ForEach 跑 n 个空任务的单次耗时中位数(μs),量线程池调度本身的开销。</summary>
        public static double MedianMicros(int n, int reps = 2000)
        {
            var items = Enumerable.Range(0, n).ToArray();
            for (int i = 0; i < 200; i++) Parallel.ForEach(items, _ => { });
            var samples = new double[reps];
            for (int r = 0; r < reps; r++)
            {
                long t0 = Stopwatch.GetTimestamp();
                Parallel.ForEach(items, _ => { });
                samples[r] = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
            }
            Array.Sort(samples);
            return samples[reps / 2];
        }
    }
}
