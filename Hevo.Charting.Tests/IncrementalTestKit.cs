using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Threading;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.Renderers;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 增量渲染机制测试共用的探针:可配置的 Feature / Layer、最小 schema、帧驱动 harness。
    /// 设计原则:不走 CompositionTarget / Dispatcher 泵,测试里手动调 ProjectAll / ExecutePipeline,
    /// 帧边界完全由测试控制,结果确定。
    /// </summary>
    internal static class IncrementalTestKit
    {
        /// <summary>WPF 对象(ChartCell / DrawingVisual)要 STA;xUnit 默认 MTA,这里起一根 STA 线程跑 body。</summary>
        public static void RunOnSta(Action action)
        {
            Exception? caught = null;
            var t = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { caught = ex; }
                finally { try { Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch { /* 线程收尾,忽略 */ } }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (caught != null) throw new Xunit.Sdk.XunitException(caught.ToString());
        }

        public static RenderContext NewRenderContext()
            => new(new VisualDataBag(), new ConditionalWeakTable<IChartLayer, VisualDataBag>());

        /// <summary>事务内写一个端口(DataBlackboard 要求写操作持写锁)。</summary>
        public static void Write<T>(this DataBlackboard board, DataPort<T> port, T value)
        {
            using (board.BeginTransaction()) board.WriteIfChanged(port, value);
        }
    }

    // ── 探针 Trait ──────────────────────────────────────────────────────────────
    internal sealed record ProbeTraitA(int Value);
    internal sealed record ProbeTraitB(int Value);
    internal sealed record ProbeSwitchTrait(bool ReadB);
    internal sealed record ProbeAnchorTrait(int Frame);

    /// <summary>
    /// 可配置的 Feature:Phase 由构造参数决定,OnProject / OnCompose 行为由委托注入,每次 Project 记一笔日志。
    /// </summary>
    internal sealed class ProbeFeature : Feature
    {
        private readonly FeaturePhase _phase;
        private readonly List<string>? _log;

        public string Name { get; }
        public int ProjectCount { get; private set; }
        public Action<ProbeFeature, FeatureContext>? OnProjectAction { get; init; }
        public Action<ProbeFeature>? OnComposeAction { get; init; }

        public ProbeFeature(string name, FeaturePhase phase = FeaturePhase.Series, List<string>? log = null)
        {
            Name = name;
            _phase = phase;
            _log = log;
        }

        public override FeaturePhase Phase => _phase;

        /// <summary>把 protected AttachLayer 暴露给测试在 Compose 委托里用。</summary>
        public void Attach<TLayer>(TLayer layer) where TLayer : IChartLayer => AttachLayer(layer);

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
            => OnComposeAction?.Invoke(this);

        protected override void OnProject(FeatureContext ctx)
        {
            ProjectCount++;
            _log?.Add(Name);
            OnProjectAction?.Invoke(this, ctx);
        }
    }

    /// <summary>
    /// 可配置的 Layer:OnUpdate 只做"读 Trait"(读什么由 <see cref="Reads"/> 决定)并计数,不画东西。
    /// 读取经过 TrackerDataProxy,因此会进入图层的 VisualDependencyTracker。
    /// </summary>
    internal sealed class ProbeLayer : ChartLayer
    {
        public int UpdateCount { get; private set; }
        public Action<IVisualData> Reads { get; init; } = static d => d.Get<ProbeTraitA>();

        public ProbeLayer(string name = "probe", ChartLayerType level = ChartLayerType.Main)
        {
            Name = name;
            Level = level;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink drawSink, WidgetBuffer widgetSink)
        {
            UpdateCount++;
            Reads(data);
        }
    }

    /// <summary>
    /// 只用 RenderContext 驱动图层的最小帧循环,复刻 ChartCell.ExecutePipeline + Invalidate 里跟判脏相关的部分:
    /// SubmitSync 判脏 → 脏图层 Update → PostRender 清脏标记。不需要 ChartCell。
    /// </summary>
    internal sealed class LayerFrameDriver
    {
        private readonly VisualDataBag _global = new();
        private readonly ConditionalWeakTable<IChartLayer, VisualDataBag> _locals = new();
        private readonly List<IChartLayer> _layers = new();

        public LayerFrameDriver(params IChartLayer[] layers) => _layers.AddRange(layers);

        /// <summary>开一帧:返回的 RenderContext 用来发布 Trait,随后调 <see cref="Commit"/>。</summary>
        public RenderContext Begin() => new(_global, _locals);

        public void Commit(RenderContext ctx)
        {
            ctx.SubmitSync(_layers);

            var dirty = new List<IChartLayer>();
            foreach (var l in _layers)
                if (l is ChartLayer cl && cl.IsDirty) dirty.Add(l);

            if (dirty.Count > 0)
            {
                using var frame = ctx.PrepareTasks(dirty);
                foreach (var t in frame.Tasks) t.Layer.Update(t.DataSnapshot);
            }

            foreach (var l in _layers)
                if (l is ChartLayer cl) cl.PostRender();
            ctx.Dispose();
        }

        public void Frame(Action<RenderContext> publish)
        {
            var ctx = Begin();
            publish(ctx);
            Commit(ctx);
        }
    }

    /// <summary>最小 ReactiveSchema:主数据流是一个手动 Push 的 WorkflowTrigger,Feature 由构造参数给定。</summary>
    internal sealed class ProbeSchema : ReactiveSchema
    {
        private readonly Feature[] _features;
        public WorkflowTrigger<DataBlackboard> Trigger { get; } = new();

        public ProbeSchema(params Feature[] features) => _features = features;

        protected override void DefineDataFlow(ChartCell chart) => Trigger.BindTo(chart);

        protected override void DefineFeatures(IFeatureContext canvas)
        {
            foreach (var f in _features) canvas.Add(f);
        }
    }

    /// <summary>
    /// 真 ChartCell + ProbeSchema 的帧驱动 harness(必须在 STA 线程里用)。
    /// 跟生产路径的唯一区别:不挂 CompositionTarget,由测试显式调 <see cref="Frame"/> / <see cref="ProjectOnly"/>。
    /// </summary>
    internal sealed class SchemaHarness
    {
        public ChartCell Cell { get; } = new();
        public ProbeSchema Schema { get; }
        public DataBlackboard Board { get; } = new();

        public SchemaHarness(params Feature[] features)
        {
            Schema = new ProbeSchema(features);
            // ExecutePipeline 只在 Template is IFeatureProjector 时才调 ProjectAll,所以必须挂上 Template。
            // 挂 Template 本身不装配(装配在 OnApplyTemplate 里,cell 不进可视树就不会触发),这里手动 ComposeAll 一次。
            Cell.Template = Schema;
            using (var ctx = Cell.CreateContext()) Schema.ComposeAll(Cell, ctx);
            Schema.Trigger.Push(Board);
        }

        /// <summary>
        /// 模拟 SizeChanged:拨环境纪元。注意 StateClock 初值 = default(VersionToken),
        /// 所以 schema 刚装配完的第一次 ProjectAll 并不是 FullPass,生产里靠 SizeChanged 触发首帧全量。
        /// </summary>
        public void InvalidateEnvironment() => Schema.InvalidateEnvironment();

        /// <summary>只跑 Feature 侧(ProjectAll),不碰图层。</summary>
        public void ProjectOnly()
        {
            using var ctx = Cell.CreateContext();
            Schema.ProjectAll(ctx);
        }

        /// <summary>跑完整一帧:ProjectAll → SubmitSync → Layer.Update → 上屏(RenderOpen)→ PostRender。</summary>
        public void Frame()
        {
            using var ctx = Cell.CreateContext();
            Cell.ExecutePipeline(ctx, PlotMode.Sync);
            Cell.Invalidate();
        }
    }
}
