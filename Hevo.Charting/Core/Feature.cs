using Hevo.Charting.Abstractions;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// Feature 通用基类。
    /// <para>
    /// 把所有 port-reactive 通用机制都吃下:layer 挂载 / IDisposable 托管 / IPausable / Phase 排序 /
    /// IsSingleton / Schema 反向引用 / 嵌套子 feature / 帧渲染 Project / FeatureContext 派发等。
    /// 跟 chart 业务"耦合"的只有概念命名(IRenderFlow / DataBlackboard 字面带数据流意味),不是模型耦合 ——
    /// graph editor 的 feature 一样会用 <c>flow.Watch(...)</c> 监听 GraphState 端口、走 OnProject 推 trait,
    /// 整套机制是 chart 跟 graph 的最大公约数。
    /// </para>
    /// <para>
    /// 派生分支:
    /// <list type="bullet">
    ///   <item><see cref="ChartFeature"/> —— chart 业务特化:多注入 <see cref="ChartFeature.Viewport"/>(1D 量程概念)。</item>
    ///   <item>(Phase 3.1 起)graph editor 自家 feature —— 直接继承 Feature,享用 flow/board/Watch 但不要 Viewport。</item>
    /// </list>
    /// </para>
    /// </summary>
    public abstract class Feature : ChartAspect, IDisposableHost, IPausable
    {
        public string InstanceId { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        // 本 feature 在 chart 上挂的图层。Decompose 时统一 RemoveUnmanagedLayer + 清空。
        private readonly List<IChartLayer> _managedLayers = new();

        // 💥 内存泄漏终结者:专门存放与图表生命周期绑定的 Rx 句柄。Decompose 时全部 Dispose。
        private readonly List<IDisposable> _disposables = new();

        // 帧投影上下文:cross-frame 复用,_portTokens 跨帧存活以驱动脏检查机制。
        private readonly FeatureContext _context = new();

        public virtual FeaturePhase Phase => FeaturePhase.Series;

        /// <summary>
        /// true = 同类型已存在时,<see cref="ReactiveSchema.Add"/> 自动把旧实例 Remove 后再加新的,
        /// 保证 canvas 上至多一个该类型实例。典型:<see cref="Features.GridLayoutFeature"/> /
        /// <see cref="Features.ViewportManagerFeature"/> 这种"全局布局/状态管家",一个图只该有一个。
        /// 默认 false:多实例合理(LineSeries / Axis / Crosshair 这类可同图多份)。
        /// </summary>
        public virtual bool IsSingleton => false;

        // 宿主 ChartCell / Schema —— 真 nullable,attach 周期内非 null,Decompose 后 reset null
        // (不再用 `null!` 撒谎)。
        // 业务 / framework 内 access 走下面的 throwing getter,access 未 attach 时 throw 而非 NRE,诊断清晰。
        private ChartCell? _chart;
        private ReactiveSchema? _schema;

        /// <summary>当前 attach 的 ChartCell;未 attach 抛 InvalidOperation。</summary>
        internal ChartCell Chart => _chart
            ?? throw new InvalidOperationException(
                $"{GetType().Name}.Chart 在未 attach 时被访问。Feature 在 OnAttached 之前 / Decompose 之后访问 Chart 是协议错误。");

        /// <summary>当前 attach 的 schema;未 attach 抛 InvalidOperation。</summary>
        internal ReactiveSchema Schema => _schema
            ?? throw new InvalidOperationException(
                $"{GetType().Name}.Schema 在未 attach 时被访问。Feature 在 OnAttached 之前 / Decompose 之后访问 Schema 是协议错误。");

        internal DataBlackboard? CurrentBoard => Schema.CurrentBoard;

        /// <summary>
        /// 由 <see cref="ReactiveSchema.Add"/>(chart 就位时)或 <see cref="ReactiveSchema.InitializeRegistry"/>
        /// (补 attach ctor 阶段 add 的)调用,设置宿主引用并触达 <see cref="OnAttached"/> 钩子。
        /// **每个 attach 周期严格一次** —— 重复调 throw。Decompose 后 attach 字段 reset 成 null,
        /// 同一 feature 实例在 Transact remove+add 等热插拔场景被复用时,下次 Add 是新周期开始,正常 attach。
        /// </summary>
        internal void AttachToHost(ChartCell chart, ReactiveSchema schema)
        {
            if (_chart != null)
                throw new InvalidOperationException(
                    $"{GetType().Name}.AttachToHost 被重复调 —— 当前 attach 周期内 Feature 实例不该再次 attach。" +
                    "正常路径:ReactiveSchema.Add 时 chart 就位立刻 attach;Decompose 后字段 reset null,可再次 attach。");
            _chart = chart;
            _schema = schema;
            OnAttached();
        }

        /// <summary>
        /// 宿主引用就位后的早期初始化钩子(尚未跑 ComposeCore / OnCompose)。
        /// ChartFeature 在此从 ViewportManagerFeature.Ports 注入 Viewport;
        /// 未来 graph editor 的 Feature 子类可在此订阅 GraphSchema.StateChanged 等。
        /// 默认 no-op。
        /// </summary>
        protected virtual void OnAttached() { }

        // ==========================================
        // 💥 IPausable 生命周期(与 ReactiveSchema 对称)
        // 95% 的 Feature(LineSeriesFeature / BarSeriesFeature / AxisFeature 等纯投影器)继承基类 no-op
        // 行为即可。有内部定时器 / WPF 订阅要屏蔽的(ChartInteractionFeature 等)重载 OnSuspend / OnResume。
        // Schema 级冻结会级联调用每个 Feature 的 Suspend。
        // ==========================================
        public bool IsActive { get; private set; } = true;

        public void Suspend()
        {
            if (!IsActive) return;
            IsActive = false;
            OnSuspend();
            for (int i = 0; i < _disposables.Count; i++)
                if (_disposables[i] is IPausable p) p.Suspend();
        }

        public void Resume()
        {
            if (IsActive) return;
            IsActive = true;
            for (int i = 0; i < _disposables.Count; i++)
                if (_disposables[i] is IPausable p) p.Resume();
            OnResume();
        }

        /// <summary>
        /// 子类重载以冻结 Feature 级内部状态(WPF 事件订阅屏蔽、定时器取消等)。
        /// 基类会自动处理 <c>_disposables</c> 中的 <see cref="IPausable"/>,无需重复。
        /// </summary>
        protected virtual void OnSuspend() { }

        /// <summary>子类重载以解冻 Feature 级内部状态。</summary>
        protected virtual void OnResume() { }

        // ==========================================
        // IDisposableHost 实现
        // ==========================================
        /// <summary>暴露给 OwnedBy 的生命周期注册接口。</summary>
        public void RegisterDisposable(IDisposable disposable)
        {
            if (disposable != null && !_disposables.Contains(disposable))
            {
                _disposables.Add(disposable);
            }
        }

        /// <summary>反查托管资源——跟 <see cref="ReactiveSchema.Owned{T}"/> 对称。</summary>
        protected IEnumerable<T> Owned<T>() where T : class
        {
            for (int i = 0; i < _disposables.Count; i++)
                if (_disposables[i] is T t) yield return t;
        }

        // ==========================================
        // 视觉层挂载
        // ==========================================
        /// <summary>
        /// 把 layer 挂到 chart 上 + 录入 _managedLayers,Decompose 时统一摘除。
        /// 同一 instanceId 后缀避免多 feature 实例共名冲突(WPF logical-tree 名字唯一)。
        /// </summary>
        protected VisualProxy<TLayer> AttachLayer<TLayer>(TLayer layer) where TLayer : IChartLayer
        {
            if (!layer.Name.EndsWith(InstanceId))
            {
                layer.Name = $"{layer.Name}_{InstanceId}";
            }
            if (!_managedLayers.Contains(layer)) _managedLayers.Add(layer);
            return Chart.AddUnmanagedLayer(layer);
        }

        // ==========================================
        // 装配 / 帧渲染
        // ==========================================
        // ChartAspect.Compose 被封死:Feature 通过 InternalCompose 走自家装配路径(吃 flow)。
        public sealed override void Compose(ChartCell chart, RenderContext ctx) { /* 封禁原虚方法 */ }

        /// <summary>
        /// Feature 装配入口。<see cref="ReactiveSchema.BuildAndActivatePipeline"/> 按 Phase 序逐个调本方法,
        /// 把 mainFlow 灌进每个 Feature。Feature 在 OnCompose 里调 <c>flow.Watch(...)</c> 订阅 port 变化。
        /// </summary>
        internal IRenderFlow<DataBlackboard> InternalCompose(ChartCell chart, RenderContext ctx, ReactiveSchema schema, IRenderFlow<DataBlackboard> flow)
        {
            // AttachToHost 已由 ReactiveSchema.Add(chart 就位时)/ InitializeRegistry(补 ctor 阶段 add)
            // 调用,本方法只负责 ComposeCore + OnCompose。Chart/Schema 字段在此之前必非 null。
            ComposeCore(chart, ctx);
            using var composeScope = FeatureComposeScope.Enter(flow);
            OnCompose(chart, ctx, flow);
            return FeatureComposeScope.Current!.Build();
        }

        /// <summary>
        /// Compose 早期钩子(在 OnAttached 之后、子类专属 OnCompose 之前)。
        /// 跟 mainFlow / DataBlackboard 解耦,只接 chart + ctx。
        /// 典型:派生类提前 PublishData 一些不依赖数据流的固定 trait。
        /// </summary>
        protected virtual void ComposeCore(ChartCell chart, RenderContext ctx) { }

        /// <summary>
        /// 子类装配主入口。在此调 <c>flow.Watch(ports, callback)</c> 订阅 port 变化、<c>AttachLayer</c> 挂图层、
        /// <c>ctx.Shared().PublishData(...)</c> 发 trait 等。
        /// </summary>
        protected abstract void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow);

        /// <summary>
        /// 子类帧渲染入口(脏 = 当帧执行)。在此调 <c>ctx.UsePort(...)</c> 拉数据 + 防抖、
        /// <c>ctx.For(layer).PublishData(...)</c> 推 trait 给图层。
        /// </summary>
        protected abstract void OnProject(FeatureContext ctx);

        /// <summary>
        /// 帧渲染入口。<see cref="ReactiveSchema.ProjectAll"/> 按 Phase 序遍历脏 Feature,逐个调本方法。
        /// FeatureContext 跨帧复用(_portTokens / _hookPocket 持续存活以保脏检查正确)。
        /// </summary>
        public void Project(RenderContext ctx, DataBlackboard board, SubscriptionRegistry registry, bool isFullPass)
        {
#if DEBUG
            using var scope = DevTools.TopologyTracer.EnterScope(this);
            // 拿到挂在 schema 上的 tracer(若拓扑面板没开,Get 返回 null,后续插桩走 null 短路)
            var tracer = DevTools.TracerRegistry.Get(Schema);
            long start = tracer != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
#endif

            // 修复 H1:复用持久化的 _context 实例,不再每帧 new FeatureContext。
            //
            // 原代码的错误:每帧 new FeatureContext 会使 _portTokens 字典归零,
            // 导致 UsePort 里 TryGetValue 永远返回 false,changed 永远为 true,
            // 脏检查机制彻底失效,每帧触发全量重绘。
            //
            // 正确做法:只刷新帧级可变的调度字段,让 _portTokens 跨帧存活。
            // BeginProject 已负责重置 _hookCursor(Hook 游标),无需担心重入问题。
            _context._registry = registry;
            _context._currentFeature = this;
            _context._isFullPass = isFullPass;

            _context.BeginProject(ctx, board);
            OnProject(_context);
            _context.EndProject();

#if DEBUG
            // 把这一帧 OnProject 耗时喂回 tracer,UI 显示 "x.x ms" perf 徽章用。
            if (tracer != null)
            {
                long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
                tracer.RecordFeatureCost(this, elapsed);
            }
#endif
        }

        protected IWorkflow<(TEvent Event, DataBlackboard Board)> WithBoard<TEvent>(IWorkflow<TEvent> source)
        {
            return new WorkflowEngine<(TEvent Event, DataBlackboard Board)>((onNext, onError) =>
            {
                return source.Subscribe(
                    e =>
                    {
                        var board = CurrentBoard;
                        if (board == null) return;
                        onNext((e, board));
                    },
                    onError);
            });
        }

        // ==========================================
        // 嵌套子 feature
        // ==========================================
        /// <summary>
        /// Compose-time 把动态生成的 child feature 当一等公民加进 schema(典型用例:PyPlotFeature
        /// 把 Python 声明的 N 个 series feature 装进自己之下,每个 series 跟独立蓝图节点行为完全一致)。
        /// <para>
        /// 本方法只能在父 feature 的 <see cref="OnCompose"/> 里调,且必须把父收到的 <c>chart / ctx / flow</c>
        /// 原封不动透传 —— 子才能接进主管线。Schema 内部 internal API 通过 protected helper 暴露,
        /// 子类不需要 InternalsVisibleTo。
        /// </para>
        /// <para>
        /// 跟 <c>Schema.Transact</c> 的边界:Transact 是 runtime 热插拔事务,用在 OnCompose 阶段会
        /// 语义错位(_latestBoard 还没就绪、Aspect 链不该重入)。两个单一职责 method 各管一段生命周期。
        /// </para>
        /// </summary>
        protected void AddChildFeatureDuringCompose(
            Feature child,
            ChartCell chart,
            RenderContext ctx,
            IRenderFlow<DataBlackboard> flow)
            => Schema.AddDuringCompose(child, chart, ctx, flow);

        /// <summary>
        /// 与 <see cref="AddChildFeatureDuringCompose"/> 配套的反向操作:
        /// 父被卸载时把子也从 schema 摘掉。典型注册到 <see cref="RegisterDisposable"/> 的清理钩子里 ——
        /// 通过捕获 <c>this</c> 的 lambda 调本方法,父 Decompose 时自动级联。
        /// </summary>
        protected void RemoveChildFeature(Feature child) => Schema.Remove(child);

        // ==========================================
        // Decompose
        // ==========================================
        public sealed override void Decompose(ChartCell chart, RenderContext ctx)
        {
            // 1. 清理图层
            foreach (var layer in _managedLayers)
            {
                chart.RemoveUnmanagedLayer(layer);
            }
            _managedLayers.Clear();

            // 2. 💥 彻底斩断流式订阅,防止内存泄漏!
            foreach (var d in _disposables)
            {
                d.Dispose();
            }
            _disposables.Clear();

            // 3. 修复 H1 配套:清空跨帧状态。Transact 热插拔后再装配的同一实例必须以"全脏"姿态进入首帧。
            _context.Reset();

            // 4. 子类清理特化跨帧状态(纯钩子,基类已处理 layers + disposables + _context)。
            OnDecompose();

            // 5. 💥 reset attach 状态(Phase 5,2026-05):AttachToHost 严格一次性是 per attach 周期,
            // Decompose 结束 = 周期结束。同一 feature 实例在 Transact remove+add 等热插拔场景被复用时,
            // 下次 Add → AttachToHost 是新周期开始。诚实 reset null —— 后续若误访问 Chart/Schema getter 直接 throw。
            _chart = null;
            _schema = null;
        }

        /// <summary>
        /// 子类重载补 Decompose 的特化清理。基类已统一清 layers + disposables + _context.Reset(),
        /// 子类一般不需要重写;有特殊跨帧资源(如 graph feature 内部缓存的 GraphState 快照)才重写。
        /// </summary>
        protected virtual void OnDecompose() { }
    }
}
