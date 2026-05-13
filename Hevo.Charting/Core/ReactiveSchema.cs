using Hevo.Charting.Abstractions;
using Hevo.Charting.Features;
using Hevo.Charting.Linked;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 渲染管线对 Schema 的最小依赖契约。<see cref="ChartCell.ExecutePipeline"/> 每帧只需要这两个方法。
    /// 把 Schema 与 Feature 集合解耦,允许未来出现非 Reactive 的 Schema 变体。
    /// </summary>
    public interface IFeatureProjector
    {
        /// <summary>把所有 Feature 按 Phase 顺序 Project 一遍(读黑板 → 算 trait → 下发图层)。</summary>
        void ProjectAll(RenderContext ctx);

        /// <summary>声明环境(尺寸 / 主题 / 数据上下文)已失效,下一帧所有 Feature 走 FullPass 重算。典型由 ChartCell.SizeChanged / TabResume 触发。</summary>
        void InvalidateEnvironment();
    }

    public interface IFeatureContext
    {
        /// <summary>
        /// ➕ 挂载特征 (原 Use)
        /// 将指定的视觉/交互特征添加到当前图纸的生命周期中。
        /// </summary>
        IFeatureContext Add(Feature feature);

        /// <summary>
        /// ➖ 泛型精准摘除
        /// 查找到指定类型的特征（支持 Lambda 过滤），并将其连根拔起（包含 UI 图层与后台事件监听）。
        /// </summary>
        IFeatureContext Remove<TFeature>(Func<TFeature, bool>? predicate = null) where TFeature : Feature;

        /// <summary>
        /// ➖ 实例摘除
        /// 直接按对象实例将特征连根拔起。
        /// </summary>
        void Remove(Feature feature);

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
        bool HasFeature<TFeature>(Func<TFeature, bool>? predicate = null) where TFeature : Feature;

        /// <summary>
        /// 🎯 抓取特征实例
        /// 找到第一个匹配的 feature 实例并返回——典型:装饰器(<see cref="Linked.SchemaContext.Decorate"/>)
        /// 拿到现有 feature 直接 mutate 字段(避免 Remove + Add 丢失原配置)。
        /// </summary>
        TFeature? Find<TFeature>(Func<TFeature, bool>? predicate = null) where TFeature : Feature;

        /// <summary>
        /// 🎯 强制抓取特征实例 —— 找不到抛 InvalidOperation 而非返回 null。
        /// 用于"我确信装配阶段已 Add 该 feature"的 framework / 业务侧场景,调用面零 <c>!</c> 后缀。
        /// </summary>
        TFeature Require<TFeature>(Func<TFeature, bool>? predicate = null) where TFeature : Feature;
    }

    /// <summary>
    /// 响应式图纸基类。
    /// <para>
    /// <b>子类只需实现两件事</b>:
    /// <list type="bullet">
    ///   <item><see cref="DefineDataFlow"/> —— 组装数据管线,末端 .BindTo(chart) 注册主流。</item>
    ///   <item><see cref="DefineFeatures"/> —— 用 IFeatureContext 装配视觉/交互 Feature。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>核心机制</b>:
    /// 1) 状态时钟(StateClock):环境变化拨动 _environmentClock,ProjectAll 检测到与 _renderedToken 不同步 → FullPass 全重算;
    /// 2) 脏 Feature 集(SubscriptionRegistry):port 写入命中订阅者后入脏名单,只重算这部分;
    /// 3) 事务舱(Transact):运行时 Add/Remove Feature 走原子 Diff,杜绝画面撕裂;
    /// 4) IPausable 生命周期:挂载在 ChartCell.Loaded/Unloaded/IsVisibleChanged 上自动 Suspend/Resume,Tab 切换零开销。
    /// </para>
    /// </summary>
    public abstract class ReactiveSchema : ChartSchema, IFeatureContext, IFeatureProjector, IDisposableHost, Hevo.Charting.Abstractions.IPausable
    {
        // 主数据流当前推送的最新 board 引用。volatile 保证 cross-thread 读到的不是部分初始化值。
        private volatile DataBlackboard? _latestBoard;

        /// <summary>当前主数据流的最新黑板(只读暴露,典型用于 dashboard 镜像桥)。null = 管线尚未推送过任何数据。</summary>
        public DataBlackboard? CurrentBoard => _latestBoard;

        // 宿主 ChartCell 的反向引用。OnApplyTemplate → ComposeAll 时绑定;DecomposeAll 时清空。
        private ChartCell? _attachedChart;

        // viewport 已经从 schema 移到 ChartCell attached property。
        // ReactiveSchema 不再有 Viewport sugar / GetEffectiveViewport —— schema 是装配规则,viewport 是 cell 运行时状态。
        // 业务 schema body 显式走 `Chart!.Viewport.X`(下面的 protected Chart accessor + ChartCell.Viewport)。
        // 兄弟 ChartFeature 在 OnAttached 时直接读 Chart.Viewport,跟 schema 解耦。

        /// <summary>
        /// 本 schema 当前 attach 到的 ChartCell。non-nullable throwing getter ——
        /// 装配 / 运行期(InitializeRegistry → BuildAndActivatePipeline → ProjectAll / 菜单 click handler)
        /// 总是有值;DecomposeAll 之后或还没 attach 时调用本 getter 抛 InvalidOperation 而非 NRE,
        /// 错误时机清晰可追溯,业务侧 access 不需要 `Chart!` 的 null-forgiving 后缀。
        /// </summary>
        protected ChartCell Chart => _attachedChart
            ?? throw new InvalidOperationException(
                $"{GetType().Name}.Chart 在 schema 未 attach 到 ChartCell 时被访问 —— " +
                "通常意味着在 DecomposeAll 之后或 ComposeAll 之前调用。" +
                "schema body 内访问 viewport / chart 上下文必须发生在装配后(DefineFeatures / DefineDataFlow / OnResume / 菜单 handler 等)。");

        /// <summary>本 schema 的指针 hit 端口。独立=自建;dashboard 注入时=共享。</summary>
        protected DataPort<PointerHitState?> HitPort => _context.HitPort;

        /// <summary>
        /// 本 schema 可见的 dashboard 级共享端口。Standalone 返空。
        /// 派生类(DynamicChartSchema 等)在 DefineFeatures 时遍历此表注入 _portRegistry,
        /// 让 feature 的 PortBinding 引用 "dashboard:{name}" 拿到 LinkedChartContext 的同一 instance。
        /// </summary>
        protected IReadOnlyDictionary<string, IDataPort> SharedPorts => _context.SharedPorts;

        /// <summary>
        /// dashboard 共享的 viewport ports。Standalone 返 null(各 schema 用自家 local viewport);
        /// Linked 返 <see cref="LinkedChartContext.SharedViewport"/>。
        /// 派生类在 DefineFeatures 时若非 null 则用此实例注册 <c>cell:viewport.*</c>,
        /// 否则用 <c>this.Require&lt;ViewportPortsFeature&gt;().Ports</c> 兜底。
        /// </summary>
        protected ViewportPorts? SharedViewport => _context.SharedViewport;

        private SchemaContext _context = SchemaContext.Standalone();

        // 装配期累积的所有 Feature(顺序 = Add 顺序,真正执行顺序由下面的 _orderedFeatures 决定)。
        private readonly List<Feature> _features = new();
        // 按 FeaturePhase 排序后的执行序列。每次 Add/Remove 透过 Transact 重排,稳态零分配。
        private Feature[] _orderedFeatures = Array.Empty<Feature>();
        internal List<Feature> Features => _features; // 只读暴露,禁止外部修改顺序

        /// <summary>
        /// 业务侧只读快照 —— 拿到 schema 装配后所有 Feature 实例(按 Add 顺序)。
        /// 典型用法:蓝图反射装配后,业务侧
        /// <c>schema.ListFeatures().OfType&lt;PyPlotFeature&gt;().FirstOrDefault(f =&gt; f.PlotName == "...")</c>
        /// 拿到 Feature 引用订阅 C# 事件。返回新数组,后续 _features 变更不影响调用方持有的 snapshot。
        /// </summary>
        public IReadOnlyList<Feature> ListFeatures() => _features.ToArray();

        /// <summary>订阅注册表:port → Feature 集合 + 脏队列。Feature 在 OnCompose 中通过 ctx 隐式登记,ProjectAll 弹脏。</summary>
        public SubscriptionRegistry Registry { get; private set; } = null!;

        // 修复 H3：预分配脏 Feature 集合缓冲，替代 ProjectAll 里每帧的 new HashSet<>() 分配。
        // PopDirtyFeatures 填充模式直接写入此缓冲，调用方负责 Clear。
        private readonly HashSet<Feature> _dirtySetBuffer = new();

        // ==========================================
        // 💥 状态时钟体系
        // ==========================================
        // 1. 驱动源：物理环境的时钟 (代表外部环境，如窗口尺寸、皮肤主题的变化频率)
        private readonly StateClock _environmentClock = new();

        // 2. 追赶者：渲染管线最后一次成功绘制时的环境令牌记录
        // 初始状态下为空，确保第一帧一定会触发全量环境对齐
        private VersionToken _renderedToken = default;

        private readonly List<IDisposable> _disposables = new();

        // ==========================================
        // 💥 引擎运行状态与动态事务容器
        // ==========================================
        private bool _isEngineRunning = false;
        private bool _isTransacting = false; // 事务重入锁：防止内部 Add/Remove 触发死循环

        // 💥 内存泄漏防线升级：使用字典精准映射 Feature 与其内部的 Rx 订阅！
        // 确保 Remove 操作时，对应的后台数据管线会被立刻掐断！
        private readonly Dictionary<Feature, IDisposable> _dynamicSubs = new();

        public ReactiveSchema()
        {
#if DEBUG
            // 💥 重点：一出生就给自己贴上这张“隐形符”
            // 外部看我还是一个干干净净的 Schema，但其实我已经带了追踪器
            var tracer = new DevTools.TopologyTracer();
            DevTools.TracerRegistry.Attach(this, tracer);
#endif
        }

        // ==========================================
        // 💥 BoardActivated 事件(2026-05):dashboard 镜像桥的钩子
        //     当 schema 的主数据流首次推送(或重新刷新)新 board 时触发,
        //     dashboard 在此 attach 端口镜像桥,让多 cell 共享 hit/viewport 状态。
        // ==========================================
        public event Action<DataBlackboard>? BoardActivated;

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

        /// <summary>
        /// 💥 反查托管资源——跟 <see cref="Own{T}(T)"/> 对偶（一边添加，一边查询）。
        /// 子类可在 OnResume 等钩子里用 <c>foreach (var r in Owned&lt;IRefreshable&gt;())</c> 做按接口级联，
        /// 比如 AutoRefreshSchema 用它实现 Resume 时自动刷新。
        /// </summary>
        protected IEnumerable<T> Owned<T>() where T : class
        {
            for (int i = 0; i < _disposables.Count; i++)
                if (_disposables[i] is T t) yield return t;
        }

        // ==========================================
        // 💥 Phase 11 / §H：IPausable 生命周期
        // 设计要点：
        //   1. `_disposables.OfType<IPausable>()` 自动捞所有托管的数据源/触发器，子类 0 改动
        //      （业务代码现在 `ds.OwnedBy(this)` 或 `this.Own(ds)` 就够了）。
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

        // ==========================================
        // 🛡️ 核心：UI 渲染事务 (UI Transaction) 与智能 Diff
        // ==========================================
        /// <summary>
        /// [架构语义]：开启一个不可分割的 UI 变更事务舱。
        /// 在闭包内执行任意数量的 Add / Remove 操作，引擎将在闭包结束后，
        /// 进行智能 Diff 比对，并以 0-GC 的方式执行一次原子化的图表重绘！
        /// </summary>
        /// <summary>
        /// 父 feature 在自己 OnCompose 里把动态生成的 child feature 当一等公民加进 schema 的
        /// **专用入口**(典型用例:PyPlotFeature 把 Python 声明的 N 个 series feature 装进自己之下,
        /// 每个 series 跟独立蓝图节点行为完全一致)。
        ///
        /// <para>
        /// 跟 <see cref="Transact"/> 的边界:Transact 是 runtime 热插拔事务(diff/dummyTrigger/push 全套),
        /// 用在 <c>OnCompose</c> 阶段会语义错位 —— 那个阶段 _latestBoard 还没就绪、Aspect 链不该重入。
        /// 拆成两个单一职责 method,各管一段生命周期。
        /// </para>
        ///
        /// <para>
        /// 调用约束:仅允许在父 feature 的 <c>OnCompose</c> 里调,且必须把父收到的 chart/ctx/flow
        /// 原封不动透传 —— 子才能接进主管线。
        /// </para>
        /// </summary>
        internal void AddDuringCompose(
            Feature feature,
            ChartCell chart,
            RenderContext ctx,
            IRenderFlow<DataBlackboard> flow)
        {
            if (_features.Contains(feature)) return;
            _features.Add(feature);
            // 重建 _orderedFeatures,让 ProjectAll 后续能直接迭代到子。
            // 父的 outer step-3c foreach 按旧快照走,不会重复访问新子 —— 我们下面立刻手动 attach + InternalCompose。
            _orderedFeatures = _features.OrderBy(f => f.Phase).ToArray();
            // 显式 AttachToHost(InternalCompose 已不再调,Add 路径只对 ReactiveSchema.Add 走 attach;
            // AddDuringCompose 是 framework 内部 add 入口,需要自己负责 attach)。
            feature.AttachToHost(chart, this);
            // 用主 featureFlow 调子的 InternalCompose:子接进主管线,不走 Transact 的隔离 dummyTrigger。
            feature.InternalCompose(chart, ctx, this, flow);
        }

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
        public IFeatureContext Add(Feature feature)
        {
            if (_features.Contains(feature)) return this;

            // 💥 单例语义:GridLayoutFeature / ViewportManagerFeature 这种同类型只该有一个,
            // 在此自动替换。以前靠 SetupLayout / SetupViewport 扩展方法手动 Remove<T>()
            // 维护(GridLayoutFeature.cs 注释里那条"致命防线"),低代码反射路径绕过扩展方法 →
            // 双重添加 → 历史"白屏" bug 复发。把单例语义抬到 Add 这一层,凡 IsSingleton=true
            // 的 feature 进来就先把同类型旧实例清掉,所有调用方一视同仁。
            if (feature.IsSingleton)
            {
                var existing = _features.FirstOrDefault(f => f.GetType() == feature.GetType());
                if (existing != null) Remove<Feature>(f => ReferenceEquals(f, existing));
            }

            // 💥 防御机制：如果引擎已运行，且未在事务中，自动包裹一层隐式微型事务！
            if (_isEngineRunning && !_isTransacting)
            {
                Transact(c => c.Add(feature));
                return this;
            }

            _features.Add(feature);

            // 💥 Add 时 chart 若已就位,立刻 AttachToHost:
            // PortsFeature 等"早挂"feature 在 SetupViewport helper 一调完就完成 SetAttached,
            // 让后续业务 helper 链(SetupUniversalHeader 等)在 DefineFeatures 内能拿到 Viewport ports。
            // chart 还没就位的 case(罕见:业务 schema 在 ctor 内 .Add)由 InitializeRegistry 第一行后补 attach。
            // InternalCompose 不再 attach,AttachToHost 严格一次性。
            if (_attachedChart != null) feature.AttachToHost(_attachedChart, this);

            return this;
        }

        /// <summary>
        /// 💥 泛型精准切除：支持 Lambda 过滤的定点清除
        /// </summary>
        public IFeatureContext Remove<TFeature>(Func<TFeature, bool>? predicate = null) where TFeature : Feature
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

        public void Remove(Feature feature)
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

        public bool HasFeature<TFeature>(Func<TFeature, bool>? predicate = null) where TFeature : Feature
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

        public TFeature? Find<TFeature>(Func<TFeature, bool>? predicate = null) where TFeature : Feature
        {
            IEnumerable<TFeature> targets = _features.OfType<TFeature>();
            if (predicate != null) targets = targets.Where(predicate);
            return targets.FirstOrDefault();
        }

        public TFeature Require<TFeature>(Func<TFeature, bool>? predicate = null) where TFeature : Feature
            => Find<TFeature>(predicate)
                ?? throw new InvalidOperationException(
                    $"{GetType().Name}.Require<{typeof(TFeature).Name}>:_features 内没匹配实例。" +
                    "调用方假定该 feature 在装配阶段已 Add(典型:框架 ensure / 业务 SetupViewport helper / 蓝图自助 ensure)。");

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
            //
            // 💥 竞态防御:Template 热替换(BlueprintRunner.Run 重跑 → cell.Template = newSchema)场景下,
            //    旧 Schema 的 _latestBoard 在 DecomposeAll → ChartSession.Dispose → pipe.Dispose 链路里
            //    被 Dispose,_rwLock 已 Dispose;同时 ChartCell.SizeChanged 排队的 RequestUpdate 在下一帧
            //    OnCompositionTargetRendering 里跑,调 Template.InvalidateEnvironment()。如果它拿到的还是
            //    旧 Schema 的引用(WPF DependencyProperty 异步路径下偶发)→ AcquireWriteLock 抛
            //    ObjectDisposedException。检 IsDisposed + 双重 catch 兜底,失效 schema 静默跳过。
            var board = _latestBoard;
            if (board == null || board.IsDisposed) return;
            try
            {
                using (board.AcquireWriteLock())
                {
                    // 利用 Ticks 保证每次写入都是不相等的新值
                    board.WriteIfChanged(EnvironmentEpochPort, DateTime.Now.Ticks);
                }
            }
            catch (ObjectDisposedException)
            {
                // board.IsDisposed 检查跟 AcquireWriteLock 之间的极小窗口内被 Decompose 抢先
                // (典型:Decompose 跑在 UI 线程同步路径,SizeChanged 排队的 update 跑在下一帧 ——
                //  按理不会撞,但保险起见 catch 一层)
            }
        }

        /// <summary>
        /// 每帧渲染主循环:① 检测环境时钟是否同步(决定是否 FullPass);② 弹脏 Feature 集;③ 任意脏数据则在 board 读锁下按 Phase 顺序 Project 全部脏 Feature。
        /// 单帧锁定保证所有 Feature 看到的是同一时刻黑板快照,杜绝高低点倒挂等"时空撕裂"。
        /// </summary>
        public void ProjectAll(RenderContext ctx)
        {
            // 防空拦截:管线尚未就绪 / 旧 board 已 Dispose(Template 热替换竞态,见 InvalidateEnvironment 同款注释)
            if (_latestBoard == null || _latestBoard.IsDisposed || Registry == null) return;

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

        /// <summary>
        /// 子类在此组装所有视觉/交互 Feature。一帧只调一次,生命周期 = ChartCell.OnApplyTemplate。
        /// 默认 no-op:GraphSchema 等"无 chart features"派生可不重写(Phase 3)。
        /// </summary>
        /// <param name="canvas">提供 Add/Remove/Find 等门面 API,直接 this 实例。</param>
        protected virtual void DefineFeatures(IFeatureContext canvas) { }

        /// <summary>
        /// chart-specific 中间层(典型 <see cref="ChartReactiveSchema"/>)在此 ensure 自家"基础 features"
        /// (Phase 5,2026-05),典型:<c>this.Add(new ViewportPortsFeature())</c>。
        /// <para>
        /// 时序:<see cref="InitializeRegistry"/> set _attachedChart 之后、SetupLayout 之前调本 hook。
        /// 那时 _attachedChart 已就位 → Add 走早 attach 路径 → ensure 的 feature 立刻 OnAttached;
        /// 后续业务 DefineFeatures 内任意顺序 helper 都拿到 attached state(典型:viewport ports)。
        /// </para>
        /// <para>
        /// ReactiveSchema 通用层默认 no-op —— framework 不知 viewport;chart-specific 装配由
        /// <see cref="ChartReactiveSchema"/> override 负责。graph editor 等直接继承 ReactiveSchema 的 schema
        /// 不享受这个 hook,无副作用。
        /// </para>
        /// </summary>
        protected virtual void EnsureBaseFeatures() { }

        /// <summary>
        /// 子类在此组装数据管线,末端必须调 .BindTo(chart) 注册为主数据流。
        /// Ambient 作用域(_composingSchema 线程槽)自动捕获到 _pendingMainFlow,BuildAndActivatePipeline 接续驱动 Feature 帧。
        /// 允许多语句装配(创建数据源 / 订阅 WebSocket / 启动心跳定时器等)。
        /// 默认 no-op:GraphSchema 等"无主数据流"派生可不重写(Phase 3),但这种 schema 必须 override
        /// <see cref="ComposeAll"/> 跳过 BuildAndActivatePipeline,否则会因 _pendingMainFlow 为空抛 InvalidOperation。
        /// </summary>
        protected virtual void DefineDataFlow(ChartCell chart) { }

        // ==========================================
        // BindTo Ambient 机制：让 .BindTo(chart) 自动登记为主数据流
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
        // 修复 M4:将 100 行的上帝方法拆分为 4 个职责单一的私有方法。
        public override void ComposeAll(ChartCell chart, RenderContext ctx)
        {
#if DEBUG
            // 诊断:同一 schema 实例 ComposeAll 被调多次 → 累积 _features / 重订阅 → board lifecycle 错乱。
            // 配合 ChartCell.OnApplyTemplate 的栈日志一起看,定位多触发的源头。
            System.Diagnostics.Debug.WriteLine(
                $"[ReactiveSchema.ComposeAll] schema=#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)}({GetType().Name}) " +
                $"cell=#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(chart)} " +
                $"_features.Count(before)={_features.Count} _isEngineRunning={_isEngineRunning}");
#endif
            InitializeRegistry(chart);
            ComposeVisualLayers(chart, ctx);
            BuildAndActivatePipeline(chart, ctx);

            _isEngineRunning = true; // 💥 标记引擎点火成功！

#if DEBUG
            InjectDebugContextMenu(chart);
#endif
        }

        // 职责 1：初始化注册表，收集并排序所有 Feature
        // protected virtual:中间层(典型 ChartReactiveSchema)可 override 加 chart-specific ensure;
        // GraphSchema 等也用此 step 复用。
        protected virtual void InitializeRegistry(ChartCell chart)
        {
            _attachedChart = chart;
            // dashboard 在 ChartCell 上注入 LinkedMaster/LinkedPane 时覆盖默认 Standalone。
            if (SchemaContext.GetAttached(chart) is { } injected) _context = injected;
            Registry = new SubscriptionRegistry();
#if DEBUG
            var myTracer = DevTools.TracerRegistry.Get(this);
            if (myTracer != null) DevTools.TracerRegistry.Attach(Registry, myTracer);
#endif

            // 💥 补 attach ctor 阶段 add 的 features —— 罕见 case(业务 schema 在 ctor 内 .Add(...)),
            // 那时 _attachedChart=null 跳过了 Add 的早 attach 路径。InitializeRegistry 第一行刚 set chart,
            // 这里集中补一遍。后续 SetupLayout / DefineFeatures 阶段 add 的 features 都走 Add 立刻 attach。
            for (int i = 0; i < _features.Count; i++)
                _features[i].AttachToHost(chart, this);

            // 💥 chart-specific 中间层(典型 ChartReactiveSchema)hook:在 SetupLayout / DefineFeatures
            // 之前 ensure 自家"基础 features"(典型 chart 的 ViewportPortsFeature)。
            // ReactiveSchema 默认 no-op —— framework 通用层不知 viewport,任何 chart-specific 装配由派生层负责。
            EnsureBaseFeatures();

            // 注入内置布局特征 (保持在此处，绝不污染构造函数！)
            var layout = DefaultLayout;

            this.Environment().SetupLayout(left: layout.Left, top: layout.Top, right: layout.Right, bottom: layout.Bottom);
            DefineFeatures(this);
            // 💥 装饰钩子:dashboard 注入的 SchemaContext 在此摘除独占 feature(视口管家 / tooltip)
            // 并 mutate ViewportPortsFeature.Ports 为 SharedViewport(若业务侧 SetupViewport 已 add 它)。
            _context.Decorate(this);

            _orderedFeatures = _features.OrderBy(f => f.Phase).ToArray();
        }

        // 职责 2：将 Feature 缝合为 WPF 视觉层，并向下传递生命周期
        // protected(Phase 3):GraphSchema 等派生 override ComposeAll 时复用此 step。
        protected void ComposeVisualLayers(ChartCell chart, RenderContext ctx)
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

        // 职责 3：构建 Rx 数据流管线并扣动扳机激活
        //
        // 子类在 DefineDataFlow 里组装 `.BindTo(chart)`，Ambient 捕获到 _pendingMainFlow；
        // 本方法把它 mainFlow.Do 截住，每帧更新 _latestBoard + OnPortUpdated 挂载，然后传给 Feature 链。
        private void BuildAndActivatePipeline(ChartCell chart, RenderContext ctx)
        {
#if DEBUG
            // 时间线锚:用户右键开 inspector 一眼看到"compose 何时开始、何时结束 / 中间各 feature 何时
            // 接上"。tracer 在 schema ctor 已 attach,这里只往 KeyEvents 里塞标记。
            var traceForCompose = DevTools.TracerRegistry.Get(this);
            traceForCompose?.RecordKeyEvent(DevTools.TopologyTracer.EventKind.ComposeStart, GetType().Name);
#endif

            // 3a. 业务 DefineDataFlow —— BindTo 走 Ambient 登记到 _pendingMainFlow
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

            if (_pendingMainFlow == null)
                throw new InvalidOperationException(
                    $"{GetType().Name}.DefineDataFlow 必须调用 .BindTo(chart) 注册主数据流");

            // 3b. 主数据流挂钩:每次推送时对齐 _latestBoard + OnPortUpdated 监听
            //
            // ⚠ tracer attach 必须在这里(Do callback)做,而不是后面 featureFlow.Plot 里 ——
            // Do() 跑在 features' Watch subscribers 之前,把 tracer 提前 attach 到 board,
            // 第一次 publish 时 features' RecordRead/Write 才能拿到 tracer 记录链路。
            // 之前 tracer attach 在 Plot callback(最后一个 subscriber),Watch 先跑时
            // TracerRegistry.Get(board) 返 null → SinWaveDataSource 这种"只 push 一次"的源
            // 整个 first publish 的所有读写都不进 LinkHits → topology inspector 看不到 writer。
            var mainFlow = _pendingMainFlow;
            _pendingMainFlow = null;
            var dataPlotWorkflow = mainFlow.Do(board =>
            {
                if (!ReferenceEquals(_latestBoard, board))
                {
                    if (_latestBoard != null) _latestBoard.OnPortUpdated -= Blackboard_OnPortUpdated;
                    _latestBoard = board;
                    board.OnPortUpdated += Blackboard_OnPortUpdated;
#if DEBUG
                    var trace = DevTools.TracerRegistry.Get(this);
                    if (trace != null) DevTools.TracerRegistry.Attach(board, trace);
#endif

                    // 💥 通知外部观察者(典型:LinkedKLineDashboard 的镜像桥),board 已切换
                    BoardActivated?.Invoke(board);
                }
            });
            IRenderFlow<DataBlackboard> featureFlow = mainFlow.Wrap(dataPlotWorkflow);

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
#if DEBUG
                // 单个 feature 接进来 = 时间线一笔。Phase 顺序一目了然。
                traceForCompose?.RecordKeyEvent(
                    DevTools.TopologyTracer.EventKind.FeatureComposed,
                    $"F_{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(feature)}",
                    feature.GetType().Name);
#endif
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

#if DEBUG
            traceForCompose?.RecordKeyEvent(DevTools.TopologyTracer.EventKind.ComposeEnd, GetType().Name,
                $"{_orderedFeatures.Length} features");
#endif
        }

#if DEBUG
        // 当前 schema 注入的 ContextMenu 项 —— DecomposeAll 成对摘除,避免 reload 蓝图后菜单项累加
        // (旧 schema 走 OnTemplateChanged → DecomposeAll,但 chart.ContextMenu 跨 schema 复用,
        //  不主动 Remove 自家加的项 → "应用蓝图"按钮每点一次菜单 +1 项)
        private readonly List<System.Windows.Controls.MenuItem> _injectedDebugMenuItems = new();

        // 职责 4（仅 Debug）：注入拓扑监控器入口及子类自定义菜单项
        private void InjectDebugContextMenu(ChartCell chart)
        {
            if (chart.ContextMenu == null) chart.ContextMenu = new System.Windows.Controls.ContextMenu();
            var monitorItem = new System.Windows.Controls.MenuItem { Header = "🚀 开启拓扑水管监控器" };
            monitorItem.Click += (s, e) => ShowTopologyMonitor(chart);
            chart.ContextMenu.Items.Add(monitorItem);
            _injectedDebugMenuItems.Add(monitorItem);

            // 子类 override 可能直接往 chart.ContextMenu 加项(如 KLineSchema 的"物理内存迷你图")——
            // snapshot count 前后差,把增量也纳入清理范围,子类不必改签名。
            int before = chart.ContextMenu.Items.Count;
            InjectDebugMenus(chart);
            for (int i = before; i < chart.ContextMenu.Items.Count; i++)
            {
                if (chart.ContextMenu.Items[i] is System.Windows.Controls.MenuItem mi)
                    _injectedDebugMenuItems.Add(mi);
            }
        }

        private void RemoveInjectedDebugMenuItems(ChartCell chart)
        {
            if (_injectedDebugMenuItems.Count == 0) return;
            var menu = chart.ContextMenu;
            if (menu != null)
            {
                foreach (var mi in _injectedDebugMenuItems) menu.Items.Remove(mi);
            }
            _injectedDebugMenuItems.Clear();
        }

        private void ShowTopologyMonitor(ChartCell chart)
        {
            var win = new System.Windows.Window
            {
                Title = "🌊 Hevo 拓扑流向监控仪 + 时间线",
                Content = new DevTools.TopologyInspectorControl(chart),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(10, 10, 15)),
                Width = 1100,
                // 加了 TimelineHeight=180 这块,顺手把窗口 950 → 950 不够紧;给到 950 + 180 = 1130 差不多,
                // 用户真不够还能拉。
                Height = 930,
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
#if DEBUG
            System.Diagnostics.Debug.WriteLine(
                $"[ReactiveSchema.DecomposeAll] schema=#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)}({GetType().Name}) " +
                $"cell=#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(chart)} " +
                $"_features.Count={_features.Count} _latestBoard={(_latestBoard == null ? "null" : "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_latestBoard))}");
#endif
            _isEngineRunning = false;

#if DEBUG
            RemoveInjectedDebugMenuItems(chart);
#endif

            foreach (var d in _disposables) d?.Dispose();
            _disposables.Clear();

            // 💥 全局卸载时，清空所有字典里的流，防止泄漏
            foreach (var kvp in _dynamicSubs) kvp.Value?.Dispose();
            _dynamicSubs.Clear();

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
            _orderedFeatures = Array.Empty<Feature>();

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
