using Hevo.Charting.Abstractions;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Core
{
    public interface IFeatureProjector
    {
        // 它的唯一职责：把所有 Feature 挨个 Project 一遍
        void ProjectAll(RenderContext ctx);

        // 💥 优雅契约：向外部暴露一个高度语义化的“环境失效”方法
        void InvalidateEnvironment();
    }

    public interface IFeatureContext
    {
        /// <summary>
        /// ➕ 挂载特征 (原 Use)
        /// 将指定的视觉/交互特征添加到当前图纸的生命周期中。
        /// </summary>
        IFeatureContext Add(ChartFeature feature);

        /// <summary>
        /// ➖ 泛型精准摘除
        /// 查找到指定类型的特征（支持 Lambda 过滤），并将其连根拔起（包含 UI 图层与后台事件监听）。
        /// </summary>
        IFeatureContext Remove<TFeature>(Func<TFeature, bool>? predicate = null) where TFeature : ChartFeature;

        /// <summary>
        /// ➖ 实例摘除
        /// 直接按对象实例将特征连根拔起。
        /// </summary>
        void Remove(ChartFeature feature);

        /// <summary>
        /// 🌱 播种全局只读配置
        /// 允许在装配特征时，顺手向全局环境播种只读的视觉特征配置。
        /// </summary>
        IFeatureContext Seed<T>(T trait) where T : class, IVisualTrait;

        /// <summary>
        /// 🛡️ UI 渲染事务 (UI Transaction)
        /// [架构语义]：开启一个不可分割的 UI 变更事务舱。
        /// 在闭包内执行任意数量的 Add / Remove 操作，引擎将在闭包结束后，
        /// 进行智能 Diff 比对，并以 0-GC 的方式执行一次原子化的图表重绘！
        /// 绝不会出现“加了坐标轴但线还没画出来”的画面撕裂现象。
        /// </summary>
        void Transact(Action<IFeatureContext> action);

        /// <summary>
        /// 🔍 探查特征
        /// 检查当前图纸环境中是否已经挂载了指定类型的特征。
        /// 架构意义：用于实现底层组件库的“静默依赖注入”与“防重复挂载”逻辑。
        /// </summary>
        /// <typeparam name="TFeature">要检查的特征类型</typeparam>
        /// <returns>如果已存在该类型的特征则返回 true，否则返回 false</returns>
        bool HasFeature<TFeature>(Func<TFeature, bool>? predicate = null) where TFeature : ChartFeature;
    }

    // ==========================================
    // 💥 响应式图纸：接入状态时钟与 UI 事务热插拔机制
    // [核心哲学]：所有视觉特征 (Feature) 的增删改查必须具备原子性。
    // ==========================================
    public abstract class ReactiveSchema : ChartSchema, IFeatureContext, IFeatureProjector, IDisposableHost, IDataFlowHost, Hevo.Charting.Abstractions.IPausable
    {
        private volatile DataBlackboard? _latestBoard;
        public DataBlackboard? CurrentBoard => _latestBoard;
        private ChartCell? _attachedChart;

        // 💥 L6 / §B.2.6：顶层视口基类上提。
        // 原先 21 个业务 Schema 都要 `private readonly ViewportPorts VP = new();`，纯样板。
        // 现在由基类统一持有，子类直接用 this.Viewport；Feature 由 Add(...) 自动注入。
        // 需要二套独立视口（罕见的双 X 轴）时，Schema 可自行 `new ViewportPorts()` 并在
        // Feature 初始化块里显式传入，自动注入逻辑会尊重外部设置。
        protected ViewportPorts Viewport { get; } = new();

        private readonly List<ChartFeature> _features = new();
        private ChartFeature[] _orderedFeatures = Array.Empty<ChartFeature>();
        internal List<ChartFeature> Features => _features; // 只读暴露，禁止外部修改顺序

        public SubscriptionRegistry Registry { get; private set; } = null!;

        // 修复 H3：预分配脏 Feature 集合缓冲，替代 ProjectAll 里每帧的 new HashSet<>() 分配。
        // PopDirtyFeatures 填充模式直接写入此缓冲，调用方负责 Clear。
        private readonly HashSet<ChartFeature> _dirtySetBuffer = new();

        // ==========================================
        // 💥 状态时钟体系
        // ==========================================
        // 1. 驱动源：物理环境的时钟 (代表外部环境，如窗口尺寸、皮肤主题的变化频率)
        private readonly StateClock _environmentClock = new();

        // 2. 追赶者：渲染管线最后一次成功绘制时的环境令牌记录
        // 初始状态下为空，确保第一帧一定会触发全量环境对齐
        private VersionToken _renderedToken = default;

        private readonly List<IDisposable> _disposables = new();
        private PipelineDispatcher? _schemaDispatcher;

        // ==========================================
        // 💥 引擎运行状态与动态事务容器
        // ==========================================
        private bool _isEngineRunning = false;
        private bool _isTransacting = false; // 事务重入锁：防止内部 Add/Remove 触发死循环

        // 💥 内存泄漏防线升级：使用字典精准映射 Feature 与其内部的 Rx 订阅！
        // 确保 Remove 操作时，对应的后台数据管线会被立刻掐断！
        private readonly Dictionary<ChartFeature, IDisposable> _dynamicSubs = new();

        public ReactiveSchema()
        {
#if DEBUG
            // 💥 重点：一出生就给自己贴上这张“隐形符”
            // 外部看我还是一个干干净净的 Schema，但其实我已经带了追踪器
            var tracer = new DevTools.TopologyTracer();
            DevTools.TracerRegistry.Attach(this, tracer);
#endif
        }

        public void RegisterDisposable(IDisposable disposable)
        {
            _disposables.Add(disposable);
        }

        /// <summary>
        /// 💥 链式托管（Phase 10 / plan §G.3）：把外部 new 的 IDisposable 资源交给 Schema 生命周期。
        /// 语义等同 RegisterDisposable，但返回 resource 本身以支持 `_src = this.Own(new Foo())` 的初始化写法。
        /// 所有 Schema 子类都可用（不局限 <see cref="DynamicSchema"/>）。
        /// </summary>
        public T Own<T>(T resource) where T : class, IDisposable
        {
            RegisterDisposable(resource);
            return resource;
        }

        // ==========================================
        // 💥 Phase 11 / §H：IPausable 生命周期
        // 设计要点：
        //   1. `_disposables.OfType<IPausable>()` 自动捞所有托管的数据源/触发器，子类 0 改动
        //      （业务代码现在 `ds.DisposeWith(this)` 或 `this.Own(ds)` 就够了）。
        //   2. Schema 级定时器（如 KLine 心跳 Interval 订阅）不直接是 IPausable，由子类
        //      重载 `OnSuspend() / OnResume()` 自行管理（比如取消/重建 CancellationTokenSource）。
        //   3. Resume 末尾强制 InvalidateEnvironment 触发一次全量重绘，保证首帧看到最新数据。
        // ==========================================
        public bool IsActive { get; private set; } = true;

        public virtual void Suspend()
        {
            if (!IsActive) return;
            IsActive = false;

            // 1. 子类先收拾自己的特殊状态（心跳、Tooltip 定时器等）
            OnSuspend();

            // 2. 基类统一处理：遍历所有托管的 IPausable（DataSource / 内部 WorkflowTrigger 等）
            for (int i = 0; i < _disposables.Count; i++)
            {
                if (_disposables[i] is Hevo.Charting.Abstractions.IPausable p) p.Suspend();
            }

            // 3. 级联到 Feature 层（ChartInteractionFeature 等可屏蔽 WPF 订阅 / 内部定时器）
            for (int i = 0; i < _orderedFeatures.Length; i++)
            {
                _orderedFeatures[i].Suspend();
            }
        }

        public virtual void Resume()
        {
            if (IsActive) return;
            IsActive = true;

            // 1. 先恢复 Feature 层（WPF 订阅重新就位，等下面数据到位后立即消费）
            for (int i = 0; i < _orderedFeatures.Length; i++)
            {
                _orderedFeatures[i].Resume();
            }

            // 2. 恢复所有数据源/触发器（触发器 Resume 时会补一次 GetSnapshot 让 UI 回到最新）
            for (int i = 0; i < _disposables.Count; i++)
            {
                if (_disposables[i] is Hevo.Charting.Abstractions.IPausable p) p.Resume();
            }

            // 3. 再交给子类恢复自己的特殊状态
            OnResume();

            // 4. 最后拨动环境时钟，让所有 Feature 在下一帧按新数据全量重算
            InvalidateEnvironment();
        }

        /// <summary>
        /// 子类重载以冻结 schema 级特殊状态（心跳定时器、外部 HitTest 订阅等）。
        /// 基类会自动处理所有托管的 <see cref="Abstractions.IPausable"/>（DataSource / WorkflowTrigger 等），无需重复。
        /// </summary>
        protected virtual void OnSuspend() { }

        /// <summary>
        /// 子类重载以解冻 schema 级特殊状态。通常用于重建在 OnSuspend 里 Cancel 掉的 CancellationTokenSource。
        /// </summary>
        protected virtual void OnResume() { }

        public void AttachDataFlow(DataFlowBinding binding)
        {
            (_schemaDispatcher ??= CreatePipelineDispatcher()).AttachDataFlow(binding);
        }

        // ==========================================
        // 🛡️ 核心：UI 渲染事务 (UI Transaction) 与智能 Diff
        // ==========================================
        /// <summary>
        /// [架构语义]：开启一个不可分割的 UI 变更事务舱。
        /// 在闭包内执行任意数量的 Add / Remove 操作，引擎将在闭包结束后，
        /// 进行智能 Diff 比对，并以 0-GC 的方式执行一次原子化的图表重绘！
        /// </summary>
        public void Transact(Action<IFeatureContext> action)
        {
            // 1. 如果引擎还没启动，直接执行闭包即可，等 ComposeAll 自然装配
            if (!_isEngineRunning)
            {
                action(this);
                return;
            }

            // 防重入：如果在事务内部又调用了事务，直接放行
            if (_isTransacting)
            {
                action(this);
                return;
            }

            _isTransacting = true;
            try
            {
                // 2. 记录变动前的集合，用于计算智能 Diff
                var oldFeatures = _features.ToList();

                // 3. 执行业务闭包 (内部调用的 Add/Remove 仅会修改 _features 列表)
                action(this);

                // 4. 计算 Diff (找出被剔除的和新进来的)
                var removedFeatures = oldFeatures.Except(_features).ToList();
                var addedFeatures = _features.Except(oldFeatures).ToList();

                if (removedFeatures.Count == 0 && addedFeatures.Count == 0) return;

                using var ctx = _attachedChart!.CreateContext();

                // 5. 【外科手术切除】：连根拔起被删掉的 Feature
                foreach (var f in removedFeatures)
                {
                    // 自动执行 Chart.RemoveLayer() 擦除屏幕旧画面，并销毁内部旁路监听
                    f.Decompose(_attachedChart, ctx);
                    // 从精确制导字典中彻底抹除，不再被 Tick 唤醒
                    Registry?.UnsubscribeAll(f);

                    // 💥 极致防漏：精准掐断该 Feature 专属的后台 Rx 脉冲订阅！
                    if (_dynamicSubs.Remove(f, out var sub))
                    {
                        sub.Dispose();
                    }
                }

                // 6. 【拓扑重排】：重新排序并缝合视觉树 (WPF UI 层合并)
                _orderedFeatures = _features.OrderBy(f => f.Phase).ToArray();
                ComposeVisualLayers(_attachedChart, ctx);

                // 7. 【动态输血】：为新增的 Feature 建立底层 Rx 数据流监听
                if (addedFeatures.Count > 0)
                {
                    // 💥 修复：使用你框架原生支持的 WorkflowTrigger，100% 兼容！
                    var dummyTrigger = new WorkflowTrigger<DataBlackboard>();

                    // 利用 BindTo 将普通 Workflow 包装成具备生命周期拦截能力的 IRenderFlow
                    var dummyFlow = dummyTrigger.BindTo(_attachedChart);

                    foreach (var f in addedFeatures)
                    {
                        // 建立 Feature 内部的 Rx 链条
                        var subFlow = f.InternalCompose(_attachedChart, ctx, this, dummyFlow);

                        // 订阅激活，并记录订阅句柄到字典中
                        var sub = subFlow.Subscribe(_ => { }, ex => System.Diagnostics.Debug.WriteLine($"[动态加载异常] {ex}"));
                        _dynamicSubs[f] = sub;
                    }

                    // 💥 瞬间激活：把当前的黑板状态“推”给这些新来的 Feature
                    // 让他们瞬间完成数据映射（Project），不用等下一秒的心跳！
                    if (_latestBoard != null)
                    {
                        dummyTrigger.Push(_latestBoard);
                    }
                }

                // 8. 【原子化重绘】：拨动环境时钟，通知底层引擎画面已过期，立即刷新！
                InvalidateEnvironment();
                _attachedChart.RequestUpdate(_ => { });
            }
            finally
            {
                // 事务结束，释放锁
                _isTransacting = false;
            }
        }

        // ==========================================
        // ➕ 挂载 / ➖ 摘除 (契约实现)
        // ==========================================
        public IFeatureContext Add(ChartFeature feature)
        {
            if (_features.Contains(feature)) return this;

            // 💥 L6 / §B.2.6：自动注入顶层 Viewport。
            // 外部显式设置了自定义 VP（罕见的双 X 轴场景）时不覆盖，尊重业务意图。
            if (feature.Viewport is null) feature.Viewport = this.Viewport;

            // 💥 防御机制：如果引擎已运行，且未在事务中，自动包裹一层隐式微型事务！
            if (_isEngineRunning && !_isTransacting)
            {
                Transact(c => c.Add(feature));
                return this;
            }

            _features.Add(feature);
            return this;
        }

        /// <summary>
        /// 💥 泛型精准切除：支持 Lambda 过滤的定点清除
        /// </summary>
        public IFeatureContext Remove<TFeature>(Func<TFeature, bool>? predicate = null) where TFeature : ChartFeature
        {
            // 1. 先找出所有该类型的 Feature
            IEnumerable<TFeature> targets = _features.OfType<TFeature>();

            // 2. 如果传了条件，就进行精准过滤！
            if (predicate != null)
            {
                targets = targets.Where(predicate);
            }

            // 3. ToList() 固化集合，防止在 foreach 中执行 Remove 导致集合被修改而报错
            foreach (var target in targets.ToList())
            {
                Remove(target); // 复用底层的单体摘除逻辑
            }

            return this;
        }

        public void Remove(ChartFeature feature)
        {
            if (feature == null || !_features.Contains(feature)) return;

            // 💥 防御机制：裸调 Remove 自动包裹隐式微型事务
            if (_isEngineRunning && !_isTransacting)
            {
                Transact(c => c.Remove(feature));
                return;
            }

            // 仅仅从列表中移除。真正的物理切除 (Decompose/UnsubscribeAll) 
            // 将由 Transact 的 Diff 算法在事务结束时统一执行！
            _features.Remove(feature);
        }

        public bool HasFeature<TFeature>(Func<TFeature, bool>? predicate = null) where TFeature : ChartFeature
        {
            // 1. 先找出所有该类型的 Feature
            IEnumerable<TFeature> targets = _features.OfType<TFeature>();

            // 2. 如果传了条件，就进行精准过滤！
            if (predicate != null)
            {
                targets = targets.Where(predicate);
            }

            return targets.Any();
        }

        public IFeatureContext Seed<T>(T trait) where T : class, IVisualTrait
        {
            _attachedChart?.Seed(trait);
            return this;
        }

        public DataPort<long> EnvironmentEpochPort { get; } = new("Global_EnvironmentEpoch");

        // 修复 L2：子类通过覆盖此属性自定义图表边距，不再需要 canvas.Use(new GridLayoutFeature{...})
        protected virtual GridLayoutConfig DefaultLayout => GridLayoutConfig.Default;

        /// <summary>
        /// 触发环境级失效 (如 SizeChanged 发生时由 ChartCell 调用)
        /// </summary>
        public void InvalidateEnvironment()
        {
            // 拨动环境时钟，产生新的纪元
            _environmentClock.Advance();
            // 2. 💥 新增逻辑：脉冲后台黑板！(触发所有监听环境的 WatchAsync)
            if (_latestBoard != null)
            {
                using (_latestBoard.AcquireWriteLock())
                {
                    // 利用 Ticks 保证每次写入都是不相等的新值
                    _latestBoard.WriteIfChanged(EnvironmentEpochPort, DateTime.Now.Ticks);
                }
            }
        }

        public void ProjectAll(RenderContext ctx)
        {
            // 防空拦截：管线尚未就绪
            if (_latestBoard == null || Registry == null) return;

            // 1. 抓取当前物理环境的最新纪元快照
            VersionToken targetToken = _environmentClock.Snapshot();

            // 2. 令牌对齐检查
            bool isEnvironmentSync = _renderedToken != targetToken;

            // 3. 修复 H3：填充模式弹出，_dirtySetBuffer 跨帧复用，零堆分配。
            //    先清空缓冲，再由 PopDirtyFeatures 直接填入脏 Feature 集合。
            _dirtySetBuffer.Clear();
            bool hasDirty = Registry.PopDirtyFeatures(_dirtySetBuffer);

            // 4. 0-GC 短路：环境未变化且无脏 Feature，跳过整帧渲染
            if (!isEnvironmentSync && !hasDirty) return;

            // ==========================================
            // 💥 终极拔高：渲染管线统一打伞！(一帧一锁)
            // ==========================================
            // 在这一瞬间，我们冻结整个黑板的时间！
            // 保证在这把伞下，所有 Feature 画出来的这几千根 K 线、均线、指标，
            // 绝对处于同一个物理时空，绝不会发生高低点倒挂的”时空撕裂”！
            using (_latestBoard.AcquireReadLock())
            {
                foreach (var feature in _orderedFeatures)
                {
                    // 触发条件：环境纪元不同步 (FullPass) 或 脏名单命中
                    if (isEnvironmentSync || _dirtySetBuffer.Contains(feature))
                    {
                        // 💥 业务特征（Feature）在内部愉快地裸奔调用 Read()
                        // 因为我们在外层拿了读锁，底层的防弹衣校验将完美通过！
                        feature.Project(ctx, _latestBoard, Registry, isEnvironmentSync);
                    }
                }
            }
            // 💥 帧渲染结束！大伞收起，释放读锁。
            // 后台被短暂卡住几毫秒的 DataPipe 写操作，此刻会像开闸放水一样瞬间涌入！

            // 5. 打扫战场，世界线收束
            _renderedToken = targetToken;
        }

        protected abstract void DefineFeatures(IFeatureContext canvas);

        // 💥 源头水管定义：子类用 `.MergeInto(this)` 登记数据流（Phase 12 / §I 推荐）。
        // 仍兼容老式 `.BindTo(chart)` —— Ambient 作用域会自动捕获为 _pendingMainFlow。
        // 与 DefineFeatures 对齐为 void，允许子类多语句装配（加载任务、订阅、心跳等）。
        protected abstract void DefineDataFlow(ChartCell chart);

        // ==========================================
        // BindTo 兼容 Ambient（全量迁移到 MergeInto 后可删）
        // ==========================================
        [ThreadStatic]
        private static ReactiveSchema? _composingSchema;
        internal static ReactiveSchema? CurrentComposing => _composingSchema;

        private IRenderFlow<DataBlackboard>? _pendingMainFlow;

        internal void RegisterMainFlow(IRenderFlow<DataBlackboard> flow)
        {
            if (flow is null) throw new ArgumentNullException(nameof(flow));
            if (_pendingMainFlow is not null)
                throw new InvalidOperationException(
                    $"{GetType().Name}.DefineDataFlow 重复调用 BindTo(chart)——一个 Schema 只能有一个主数据流");
            _pendingMainFlow = flow;
        }

        // 💥 完美重写底层的 ComposeAll 钩子
        // 修复 M4：将 100 行的上帝方法拆分为 4 个职责单一的私有方法。
        public sealed override void ComposeAll(ChartCell chart, RenderContext ctx)
        {
            InitializeRegistry(chart);
            ComposeVisualLayers(chart, ctx);
            BuildAndActivatePipeline(chart, ctx);

            _isEngineRunning = true; // 💥 标记引擎点火成功！
#if DEBUG
            InjectDebugContextMenu(chart);
#endif
        }

        // 职责 1：初始化注册表，收集并排序所有 Feature
        private void InitializeRegistry(ChartCell chart)
        {
            _attachedChart = chart;
            Registry = new SubscriptionRegistry();
#if DEBUG
            var myTracer = DevTools.TracerRegistry.Get(this);
            if (myTracer != null) DevTools.TracerRegistry.Attach(Registry, myTracer);
#endif
            // 注入内置布局特征 (保持在此处，绝不污染构造函数！)
            var layout = DefaultLayout;

            this.Environment().SetupLayout(left: layout.Left, top: layout.Top, right: layout.Right, bottom: layout.Bottom);
            DefineFeatures(this);
            _orderedFeatures = _features.OrderBy(f => f.Phase).ToArray();
        }

        // 职责 2：将 Feature 缝合为 WPF 视觉层，并向下传递生命周期
        private void ComposeVisualLayers(ChartCell chart, RenderContext ctx)
        {
            // 将每个 Feature (ChartAspect 子类) 合并到 Aspect 链，使 WPF Decorate() 能带上所有 Feature 的 UI
            this.Aspect = ChartAspect.Empty;
            foreach (var feature in _orderedFeatures)
            {
                this.Aspect += feature;
            }

            // 维持旧时代契约：内部会调用 Aspect.Compose()，保证生命周期事件完整向下传递
            base.ComposeAll(chart, ctx);
        }

        private PipelineDispatcher CreatePipelineDispatcher()
        {
            return new PipelineDispatcher(this, RequestDataFlowPulse);
        }

        internal void RequestDataFlowPulse()
        {
            var chart = _attachedChart;
            var board = _latestBoard;
            if (chart == null || board == null) return;

            _schemaDispatcher?.Execute(board);
            chart.RequestUpdate(_ => { });
        }

        // Phase 12 / §I：MergeInto 路径下用作 Feature.flow 的合成 pulse trigger
        private WorkflowTrigger<DataBlackboard>? _featurePulse;

        // 职责 3：构建 Rx 数据流管线并扣动扳机激活
        //
        // Phase 12 / §I：双模式兼容
        //   - 【BindTo 路径（旧）】：子类 `.BindTo(chart)` 产生 IRenderFlow，走 Ambient 登记到 _pendingMainFlow
        //     （Ambient 机制见 WorkflowWatchExtensions.BindTo；本类只读 _pendingMainFlow）
        //   - 【MergeInto 路径（新）】：子类 `.MergeInto(this)` 把 binding 登记到 _schemaDispatcher；
        //     Schema 自持 `_latestBoard`，用内部 WorkflowTrigger 合成 Feature.flow，Push 一次触发 initial setup
        private void BuildAndActivatePipeline(ChartCell chart, RenderContext ctx)
        {
            _schemaDispatcher = CreatePipelineDispatcher();

            // Phase 12：Schema 自持黑板（MergeInto 路径下 bindings 的 ProcessTo(...) 要有目标）
            _latestBoard ??= new DataBlackboard();
            _latestBoard.OnPortUpdated -= Blackboard_OnPortUpdated;
            _latestBoard.OnPortUpdated += Blackboard_OnPortUpdated;

            // 3a. 业务 DefineDataFlow —— bindings 走 MergeInto 登记到 dispatcher；或 BindTo 走 Ambient
            _pendingMainFlow = null;
            var prevComposing = _composingSchema;
            _composingSchema = this;
            try
            {
#if DEBUG
                using (DevTools.TopologyTracer.EnterSetupScope(DevTools.TopologyTracer.PIPE_ID))
#endif
                {
                    DefineDataFlow(chart);
                }
            }
            finally
            {
                _composingSchema = prevComposing;
            }

            // 3b. 准备 Feature.flow
            IRenderFlow<DataBlackboard> featureFlow;
            bool isBindToMode = _pendingMainFlow != null;
            if (isBindToMode)
            {
                // 【BindTo 兼容路径】老模型：mainFlow 驱动每帧 Feature.Do
                var mainFlow = _pendingMainFlow!;
                _pendingMainFlow = null;
                var dataPlotWorkflow = mainFlow.Do(board =>
                {
                    // BindTo 路径下 board 可能来自 DataPipeBuilder._persistentBoard；迁转 OnPortUpdated 挂载
                    if (!ReferenceEquals(_latestBoard, board))
                    {
                        if (_latestBoard != null) _latestBoard.OnPortUpdated -= Blackboard_OnPortUpdated;
                        _latestBoard = board;
                        board.OnPortUpdated += Blackboard_OnPortUpdated;
                    }
                    _schemaDispatcher?.Execute(board);
                });
                featureFlow = mainFlow.Wrap(dataPlotWorkflow);
            }
            else
            {
                // 【MergeInto 新路径】Feature.flow 是合成的 WorkflowTrigger
                // 只需 initial Push 一次触发 Feature.Watch 的 OnTransactionCommitted 挂载；
                // 后续数据更新走 bindings → PipelineDispatcher.Execute → ProcessTo → Transaction 自然 fire Watch
                _featurePulse = new WorkflowTrigger<DataBlackboard>();
                featureFlow = _featurePulse.BindTo(chart);
            }

            // 3c. Feature 依 Phase 顺序 compose
            foreach (var feature in _orderedFeatures)
            {
#if DEBUG
                using (DevTools.TopologyTracer.EnterSetupScope(feature))
#endif
                {
                    using var composeScope = FeatureComposeScope.Enter(featureFlow);
                    var composedFlow = feature.InternalCompose(chart, ctx, this, featureFlow);
                    featureFlow = !ReferenceEquals(composedFlow, featureFlow)
                        ? composedFlow
                        : FeatureComposeScope.Current?.Build() ?? composedFlow;
                }
            }

            // 3d. 激活
            featureFlow.Plot(board =>
            {
                _latestBoard = board;
#if DEBUG
                var tracer = DevTools.TracerRegistry.Get(this);
                if (tracer != null)
                {
                    DevTools.TracerRegistry.Attach(board, tracer);
                    DevTools.TracerRegistry.Attach(Registry, tracer);
                }
#endif
            });

            if (!isBindToMode)
            {
                // MergeInto 路径：先跑一次 dispatcher 把现存数据注入 _latestBoard，再 Push 给 featureFlow 让 Feature.Watch 挂钩
                _schemaDispatcher?.Execute(_latestBoard!);
                _featurePulse!.Push(_latestBoard!);
            }
        }

#if DEBUG
        // 职责 4（仅 Debug）：注入拓扑监控器入口及子类自定义菜单项
        private void InjectDebugContextMenu(ChartCell chart)
        {
            if (chart.ContextMenu == null) chart.ContextMenu = new System.Windows.Controls.ContextMenu();
            var monitorItem = new System.Windows.Controls.MenuItem { Header = "🚀 开启拓扑水管监控器" };
            monitorItem.Click += (s, e) => ShowTopologyMonitor(chart);
            chart.ContextMenu.Items.Add(monitorItem);
            InjectDebugMenus(chart);
        }

        private void ShowTopologyMonitor(ChartCell chart)
        {
            var win = new System.Windows.Window
            {
                Title = "🌊 Hevo 拓扑流向监控仪",
                Content = new DevTools.TopologyInspectorControl(chart),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(10, 10, 15)),
                Width = 1000,
                Height = 750,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                Owner = System.Windows.Window.GetWindow(chart)
            };
            win.Show();
        }

        // 💥 3. 留给子类 (如 KLineSchema) 的钩子
        protected virtual void InjectDebugMenus(ChartCell chart) { }
#endif

        /// <summary>
        /// 💥 终极生命周期清理：图表卸载时，必须把所有 Feature 和事件钩子连根拔起！
        /// 修复了切换 Template 或关闭窗口时，底层黑板依然持有 UI 事件导致内存泄漏的致命 Bug。
        /// </summary>
        public override void DecomposeAll(ChartCell chart, RenderContext ctx)
        {
            _isEngineRunning = false;

            foreach (var d in _disposables) d?.Dispose();
            _disposables.Clear();

            // 💥 全局卸载时，清空所有字典里的流，防止泄漏
            foreach (var kvp in _dynamicSubs) kvp.Value?.Dispose();
            _dynamicSubs.Clear();

            _schemaDispatcher?.Dispose();
            _schemaDispatcher = null;

            // 1. 斩断与底层物理黑板的全局监听 (防止极其严重的内存泄漏！)
            if (_latestBoard != null)
            {
                _latestBoard.OnPortUpdated -= Blackboard_OnPortUpdated;
            }

            // 2. 逆向遍历并销毁所有 Feature (先装配的后销毁，严格符合洋葱模型生命周期)
            for (int i = _orderedFeatures.Length - 1; i >= 0; i--)
            {
                _orderedFeatures[i].Decompose(chart, ctx);
            }

            // 3. 清空注册表与脏名单
            Registry = null!;
            _features.Clear();
            _orderedFeatures = Array.Empty<ChartFeature>();

            // 4. 解除物理容器强引用
            _latestBoard = null;
            _attachedChart = null;

            // 5. 调用基类清理
            base.DecomposeAll(chart, ctx);
        }

        /// <summary>
        /// 当黑板上的具体引脚发生写入且数据真实改变时触发
        /// </summary>
        private void Blackboard_OnPortUpdated(object port)
        {
            var chart = _attachedChart;
            var registry = Registry;

            if (chart == null || registry == null) return;

            // 查字典，如果这个引脚被任何 Feature 订阅了，就把对应的 Feature 扔进脏名单
            if (registry.NotifyPortUpdated(port))
            {
                // 申请下一帧物理重绘
                chart.RequestUpdate(_ => { });
            }
        }

    }
}
