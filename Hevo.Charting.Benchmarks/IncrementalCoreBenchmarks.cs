using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using BenchmarkDotNet.Attributes;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.Renderers;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Benchmarks
{
    // 增量渲染核心路径的微基准:判脏(SubmitSync)、Feature 投影(ProjectAll)、黑板事务、整帧随图层数的伸缩。
    // 跟 render-probe 互补:render-probe 看真实 K 线图的整帧,这里把单个环节拆出来,参数化图层数 / Feature 数,
    // 看开销是不是随规模线性增长、空闲帧是不是真的接近 0。
    //
    // ChartCell / ChartLayer 是 WPF 对象,benchmark 方法标 [STAThread],BDN 生成的入口会跑在 STA 线程上。
    //
    //   dotnet run -c Release -- --filter "*SubmitSync*" "*ProjectAll*" "*Blackboard*" "*FrameScaling*"

    /// <summary>
    /// RenderContext.SubmitSync 判脏。N 个已发现的图层,每个都读一个局部 Trait + 一个全局 Trait。
    /// <list type="bullet">
    /// <item>Idle:本帧没有任何提交 → Bag 级短路,每层 O(1)。</item>
    /// <item>OneLocal:只有 1 个图层的局部数据换了对象 → 该层引用比对判脏,其余短路。</item>
    /// <item>GlobalRepublish:全局 Bag 有提交但引用没变 → 每层都要走引用比对,但都不脏(判脏循环的满负荷成本)。</item>
    /// <item>GlobalChanged:全局 Trait 换了对象 → 每层都判脏。</item>
    /// </list>
    /// </summary>
    [MemoryDiagnoser]
    public class SubmitSyncBenchmarks
    {
        [Params(8, 32, 128)] public int Layers;
        [Params("Idle", "OneLocal", "GlobalRepublish", "GlobalChanged")] public string Scenario = "Idle";

        private readonly VisualDataBag _global = new();
        private readonly ConditionalWeakTable<IChartLayer, VisualDataBag> _locals = new();
        private List<IChartLayer> _layers = new();
        private BenchGlobalTrait _globalTrait = new(0);
        private int _n;

        [GlobalSetup]
        public void Setup()
        {
            _layers = new List<IChartLayer>();
            for (int i = 0; i < Layers; i++) _layers.Add(new BenchLayer($"L{i}"));

            // 首帧:给每层发局部数据唤醒(Strategy A),跑一次 Update 让依赖追踪器记下引用
            var ctx = new RenderContext(_global, _locals);
            ctx.Shared().PublishData(_globalTrait);
            foreach (var l in _layers) ctx.For(l).PublishData(new BenchTrait(0));
            ctx.SubmitSync(_layers);
            using (var frame = ctx.PrepareTasks(_layers))
                foreach (var t in frame.Tasks) t.Layer.Update(t.DataSnapshot);
            foreach (var l in _layers) ((ChartLayer)l).PostRender();
            ctx.Dispose();
        }

        [Benchmark, STAThread]
        public int SubmitSync()
        {
            var ctx = new RenderContext(_global, _locals);
            switch (Scenario)
            {
                case "OneLocal": ctx.For(_layers[0]).PublishData(new BenchTrait(++_n)); break;
                case "GlobalRepublish": ctx.Shared().PublishData(_globalTrait); break;
                case "GlobalChanged": ctx.Shared().PublishData(_globalTrait = new BenchGlobalTrait(++_n)); break;
            }
            ctx.SubmitSync(_layers);

            // 只清标记不重录:依赖追踪器保留上次 Update 时的引用,下一轮的判脏结论跟这一轮相同
            int dirty = 0;
            for (int i = 0; i < _layers.Count; i++)
            {
                var cl = (ChartLayer)_layers[i];
                if (cl.IsDirty) { dirty++; cl.PostRender(); }
            }
            ctx.Dispose();
            return dirty;
        }
    }

    /// <summary>
    /// ReactiveSchema.ProjectAll。N 个 Feature,各自 UsePort 一个独立端口(读取即订阅)。
    /// <list type="bullet">
    /// <item>Idle:没有端口变化、环境纪元没变 → 整帧短路。</item>
    /// <item>OnePort:1 个端口变了 → 1 个 Feature 重算(但仍要按 Phase 序遍历 N 个判脏名单)。</item>
    /// <item>AllPorts:一个事务里 N 个端口都变 → N 个 Feature 重算。</item>
    /// <item>FullPass:拨环境纪元(SizeChanged 同款)→ 全部重算。</item>
    /// </list>
    /// 端口写入(黑板事务 + 订阅通知)算在测量里,单独的写入成本见 <see cref="BlackboardTransactionBenchmarks"/>。
    /// </summary>
    [MemoryDiagnoser]
    public class ProjectAllBenchmarks
    {
        [Params(8, 32, 128)] public int Features;
        [Params("Idle", "OnePort", "AllPorts", "FullPass")] public string Scenario = "Idle";

        private BenchHarness _h = null!;
        private double _v;

        [GlobalSetup]
        public void Setup() => _h = new BenchHarness(Features, withLayers: false);

        [Benchmark, STAThread]
        public void ProjectAll()
        {
            switch (Scenario)
            {
                case "OnePort": _h.Write(1, ++_v); break;
                case "AllPorts": _h.Write(_h.Ports.Count, ++_v); break;
                case "FullPass": _h.Schema.InvalidateEnvironment(); break;
            }
            using (var ctx = _h.Cell.CreateContext()) _h.Schema.ProjectAll(ctx);
            _h.Cell.DiscardPendingUpdates();
        }
    }

    /// <summary>
    /// 整帧(排队事务 → ProjectAll → SubmitSync → 图层录制 → 交给 WPF)随图层数的伸缩。
    /// 每个 Feature 带一个图层,图层画 1 个矩形。OneDirty 是增量的典型帧(只有 1 层重录),
    /// AllDirty 是全部重录(对照组),Idle 是静止帧。
    /// </summary>
    [MemoryDiagnoser]
    public class FrameScalingBenchmarks
    {
        [Params(8, 32, 128)] public int Layers;
        [Params("Idle", "OneDirty", "AllDirty")] public string Scenario = "Idle";

        private BenchHarness _h = null!;
        private double _v;

        [GlobalSetup]
        public void Setup()
        {
            _h = new BenchHarness(Layers, withLayers: true);
            _h.Schema.InvalidateEnvironment();
            _h.Cell.RunFrameNow(PlotMode.Sync);
        }

        [Benchmark, STAThread]
        public int Frame()
        {
            switch (Scenario)
            {
                case "OneDirty": _h.Write(1, ++_v); break;
                case "AllDirty": _h.Write(_h.Ports.Count, ++_v); break;
            }
            return _h.Cell.RunFrameNow(PlotMode.Sync);
        }
    }

    /// <summary>
    /// DataBlackboard 事务:一个事务写 K 个端口。每个端口有 1 个 Feature 订阅(跟 ReactiveSchema 一样经
    /// OnPortUpdated → SubscriptionRegistry 标脏),事务后弹出脏名单。
    /// Unchanged 是值防抖命中(写同值,不通知),Changed 是真实写入。
    /// </summary>
    [MemoryDiagnoser]
    public class BlackboardTransactionBenchmarks
    {
        [Params(1, 8, 32)] public int PortsPerTransaction;
        [Params("Unchanged", "Changed")] public string Scenario = "Changed";

        private readonly DataBlackboard _board = new();
        private readonly SubscriptionRegistry _registry = new();
        private readonly HashSet<Feature> _dirty = new();
        private DataPort<double>[] _ports = Array.Empty<DataPort<double>>();
        private double _v;

        [GlobalSetup]
        public void Setup()
        {
            _ports = new DataPort<double>[PortsPerTransaction];
            for (int i = 0; i < _ports.Length; i++)
            {
                _ports[i] = new DataPort<double>($"B{i}");
                _registry.Subscribe(_ports[i], new BenchFeature(_ports[i], withLayer: false));
            }
            _board.OnPortUpdated += p => _registry.NotifyPortUpdated(p);
        }

        [GlobalCleanup]
        public void Cleanup() => _board.Dispose();

        [Benchmark]
        public int Transaction()
        {
            double v = Scenario == "Changed" ? ++_v : _v;
            using (_board.BeginTransaction())
            {
                for (int i = 0; i < _ports.Length; i++) _board.WriteIfChanged(_ports[i], v);
            }
            _dirty.Clear();
            _registry.PopDirtyFeatures(_dirty);
            return _dirty.Count;
        }

        [Benchmark]
        public double ReadUnderLock()
        {
            double sum = 0;
            using (_board.AcquireReadLock())
            {
                for (int i = 0; i < _ports.Length; i++) sum += _board.Read(_ports[i]);
            }
            return sum;
        }
    }

    // ── 共用探针 ──────────────────────────────────────────────────────────────

    internal sealed record BenchTrait(double Value);
    internal sealed record BenchGlobalTrait(int Value);

    /// <summary>读一个局部 Trait + 一个全局 Trait,画 1 个矩形。</summary>
    internal sealed class BenchLayer : ChartLayer
    {
        private static readonly IHevoBrush Brush = new HevoSolidBrush(Colors.SteelBlue);

        public BenchLayer(string name)
        {
            Name = name;
            Level = ChartLayerType.Main;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink drawSink, WidgetBuffer widgetSink)
        {
            var t = data.Get<BenchTrait>();
            data.Get<BenchGlobalTrait>();
            float v = (float)((t?.Value ?? 0) % 100);
            drawSink.DrawRectangle(Brush, null, new HevoRect(v, 0, 10, 10));
        }
    }

    /// <summary>UsePort 一个端口;端口变了就把新值发布到自己的图层(如果有)。</summary>
    internal sealed class BenchFeature : Feature
    {
        private readonly DataPort<double> _port;
        private readonly bool _withLayer;
        private BenchLayer? _layer;

        public BenchFeature(DataPort<double> port, bool withLayer)
        {
            _port = port;
            _withLayer = withLayer;
        }

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            if (!_withLayer) return;
            _layer = new BenchLayer(_port.Id);
            AttachLayer(_layer);
        }

        protected override void OnProject(FeatureContext ctx)
        {
            var (v, changed) = ctx.UsePort(_port);
            if (changed && _layer != null) ctx.For(_layer).PublishData(new BenchTrait(v));
        }
    }

    internal sealed class BenchSchema : ReactiveSchema
    {
        private readonly Feature[] _features;
        public WorkflowTrigger<DataBlackboard> Trigger { get; } = new();
        public BenchSchema(Feature[] features) => _features = features;
        protected override void DefineDataFlow(ChartCell chart) => Trigger.BindTo(chart);
        protected override void DefineFeatures(IFeatureContext canvas)
        {
            foreach (var f in _features) canvas.Add(f);
        }
    }

    /// <summary>真 ChartCell + N 个 BenchFeature,不进可视树、不挂 CompositionTarget,帧由基准方法显式驱动。</summary>
    internal sealed class BenchHarness
    {
        public ChartCell Cell { get; } = new();
        public BenchSchema Schema { get; }
        public DataBlackboard Board { get; } = new();
        public List<DataPort<double>> Ports { get; } = new();

        public BenchHarness(int features, bool withLayers)
        {
            var list = new Feature[features];
            for (int i = 0; i < features; i++)
            {
                var port = new DataPort<double>($"P{i}");
                Ports.Add(port);
                list[i] = new BenchFeature(port, withLayers);
            }
            Schema = new BenchSchema(list);
            Cell.Template = Schema;
            using (var ctx = Cell.CreateContext()) Schema.ComposeAll(Cell, ctx);
            Schema.Trigger.Push(Board);
            Write(Ports.Count, 0);

            // 首帧全量:让每个 Feature UsePort 一次完成订阅
            Schema.InvalidateEnvironment();
            using (var ctx = Cell.CreateContext()) Schema.ProjectAll(ctx);
            Cell.DiscardPendingUpdates();
        }

        /// <summary>一个事务里给前 count 个端口写入同一个值。</summary>
        public void Write(int count, double value)
        {
            using (Board.BeginTransaction())
                for (int i = 0; i < count; i++) Board.WriteIfChanged(Ports[i], value);
        }
    }
}
