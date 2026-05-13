using Hevo.Charting.Core;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting
{
    /// <summary>
    /// 💥 顶层数据源基类(单值正交版,2026-05 协议重构)。
    /// <para>
    /// 不预设"数据是 list of rows"语义 —— <typeparamref name="T"/> 直接是 publish unit:
    /// </para>
    /// <list type="bullet">
    ///   <item>列式 / 多 row 场景:走 <see cref="BufferedDataSource{TSource, TItem}"/>(T = <see cref="DataSnapshot{TItem}"/>)</item>
    ///   <item>单值场景(graph state / 单值 latest 等):直继承本类,T = state 类型本身</item>
    /// </list>
    /// <para>
    /// 责任清单:Stream 总线 + 当前值 + IPausable + IDisposable。Buffer / Snapshot / 蓝图 chart 联动 全部下沉到 <see cref="BufferedDataSource{TSource, TItem}"/>,
    /// 单值 DS 不背列式机制开销。
    /// </para>
    /// </summary>
    public abstract class DataSource<TSource, T> : IDisposable, Hevo.Charting.Abstractions.IPausable
        where TSource : DataSource<TSource, T>
    {
        // 💥 主总线 —— 所有 Publish 都进这里,subscribe 者按 T 接收。
        // 单值 DS:T 是 state record / DTO;列式 DS:T = DataSnapshot<TItem>。
        protected readonly WorkflowTrigger<T> _trigger = new();

        /// <summary>对外订阅入口。每次 <see cref="Publish"/> 推一次 T。</summary>
        public IWorkflow<T> Stream => _trigger;

        // 是否已经 publish 过 —— 跟 Resume 自动补推的逻辑挂钩(没 publish 过 = 没东西可补)。
        // _current 的引用 / 装箱判 default(T) 兜不住 nullable struct 跟 reference 共用,这里用显式 flag 最干净。
        private bool _hasPublished;
        private T? _current;

        /// <summary>
        /// 当前 publish 过的最新值。`null`(reference)/`default`(value)= 还没 publish 过。
        /// 同步快读 —— 不订阅、不分配 Task,跟 <see cref="Stream"/> 异步流互补。
        /// </summary>
        public T? Current => _current;

        /// <summary>
        /// 内部统一 publish 入口。子类(<see cref="BufferedDataSource{TSource, TItem}"/> 的 <c>Publish()</c> 无参版、
        /// 单值 DS 自定义 SetState 路径)都最终走这里,确保 _current 跟 _trigger 推送对齐。
        /// </summary>
        protected void Publish(T value)
        {
            _current = value;
            _hasPublished = true;
            _trigger.Push(value);
        }

        // ==========================================
        // 💥 IPausable 生命周期(TabControl / 虚拟化 / IsVisibleChanged)
        // ==========================================
        private volatile bool _isActive = true;
        public bool IsActive => _isActive;

        public virtual void Suspend()
        {
            if (!_isActive) return;
            _isActive = false;
            _trigger.Suspend();
        }

        public virtual void Resume()
        {
            if (_isActive) return;
            _isActive = true;
            _trigger.Resume();
            // 补一次快照:Resume 后 UI 立刻看到当前数据,不必等下一次 Publish。
            // 子类(列式 BufferedDataSource)可 override OnResumeRepublish 改成 fresh GetSnapshot()。
            OnResumeRepublish();
        }

        /// <summary>
        /// Resume 自动补推钩子。基类默认推 <see cref="Current"/>(若已 publish 过)。
        /// 列式 <see cref="BufferedDataSource{TSource, TItem}"/> override 推 <c>GetSnapshot()</c> ——
        /// 跟旧行为对齐(发出含当前 buffer 的新 DataSnapshot),让消费端拿到带新 VersionToken 的实例。
        /// </summary>
        protected virtual void OnResumeRepublish()
        {
            if (_hasPublished && _current != null) _trigger.Push(_current);
        }

        public virtual void Dispose() { }
    }


    /// <summary>
    /// 💥 列式 / 多 row 缓冲 DataSource —— "list of TItem" 语义的工坊基类(2026-05 重构,从 <see cref="DataSource{TSource,T}"/> 拆出)。
    /// <para>
    /// 责任:双缓冲读写分离(后厨 <c>_buffer</c> + 展柜 <c>_readSnapshot</c>) + 0-GC <see cref="DataSnapshot{TItem}"/> publish + <see cref="LogicalLength"/> +
    /// chart 蓝图集成(<see cref="IBlueprintRunnable"/> + <see cref="Pipe"/>)。
    /// </para>
    /// <para>
    /// 继承 <see cref="DataSource{TSource, T}"/> 时把 T 锁定为 <see cref="DataSnapshot{TItem}"/>,
    /// 自动获得 Stream / Current / IPausable / Dispose 这套通用机制。
    /// </para>
    /// </summary>
    public abstract class BufferedDataSource<TSource, TItem> : DataSource<TSource, DataSnapshot<TItem>>
        where TSource : BufferedDataSource<TSource, TItem>
    {
        public abstract int LogicalLength { get; }

        // ==========================================
        // 🔪 后厨案板 (写缓冲区)：专门应对复杂的网络数据合并与增删
        // ==========================================
        protected readonly List<TItem> _buffer = new();
        protected readonly object _lock = new();

        // ==========================================
        // 📺 前台展示柜 (读缓冲区)：物理连续数组，供 Span 极速读取
        // ==========================================
        protected volatile TItem[] _readSnapshot = Array.Empty<TItem>();

        // 💥 挂载物理数据时钟
        protected readonly StateClock _dataClock = new();

        public VersionToken CurrentVersion => _dataClock.Snapshot();

        public DataPipeBuilder<TSource, TItem> Pipe() => new((TSource)this, Stream);

        /// <summary>
        /// 💥 核心：将后厨案板的数据上菜到展示柜,并通过基类 <see cref="DataSource{TSource,T}.Publish"/> 推到 Stream。
        /// </summary>
        protected void Publish()
        {
            lock (_lock)
            {
                // 1. 数组扩容：如果展示柜不够放了，才换个大点的柜子 (避免频繁 GC)
                if (_readSnapshot.Length < _buffer.Count)
                {
                    _readSnapshot = new TItem[_buffer.Count];
                }
                else if (_buffer.Count == 0 && _readSnapshot.Length > 0)
                {
                    // 归零路径：SwitchContext 清 buffer 后必须把展示柜也清空，
                    // 否则 _readSnapshot[0] 仍持有上一上下文的引用，依赖它计算 LogicalLength
                    // 的子类（如 KLineDataSource）会把"归零信号"误报成"未变化"，下游 Watch 收不到。
                    _readSnapshot = Array.Empty<TItem>();
                }

                // 2. 物理拷贝：把后厨数据端到前台
                _buffer.CopyTo(_readSnapshot);

                // 3. 拨动时钟(融合 Advance+Snapshot,单次 Interlocked.Increment 取新值)
                VersionToken newVersion = _dataClock.AdvanceAndSnapshot();

                // 4. 💥 关键点：传出的是物理数组，但告诉外层有效长度是 _buffer.Count！
                base.Publish(new DataSnapshot<TItem>(_readSnapshot, _buffer.Count, newVersion));
            }
        }

        /// <summary>
        /// 💥 重算提取器：供 DataPipeBuilder.Reevaluate 直接调用的 0-GC 方法
        /// </summary>
        public DataSnapshot<TItem> GetSnapshot()
        {
            // 这里不需要加锁，因为 _readSnapshot 是指针传递，_buffer.Count 是简单值。
            // 传出去的是前台展示柜的引用，和实际有效长度
            return new DataSnapshot<TItem>(_readSnapshot, _buffer.Count, _dataClock.Snapshot());
        }

        /// <summary>
        /// 强制把当前 snapshot 重推给所有订阅者,但不改 IsActive 状态。
        /// 跟 Suspend/Resume 解耦——适合多 schema 共享数据源的场景:
        /// schema 在 OnResume 里调一次,自己的 chart pipe 收到 push → port 更新 → layer 重绘,
        /// 既能唤醒自己又不会影响其他正在使用同一数据源的 schema。
        /// </summary>
        public void RepublishLatest()
        {
            if (IsActive) _trigger.Push(GetSnapshot());
        }

        // 列式 DS Resume 时推一份 fresh GetSnapshot() —— 比基类默认补推 _current(可能是同一引用)更稳:
        // 拿到的 DataSnapshot 自带新 VersionToken,下游 Watch / WriteIfChanged 一定判脏。
        protected override void OnResumeRepublish()
        {
            _trigger.Push(GetSnapshot());
        }
    }


    /// <summary>
    /// 请求驱动型数据源 —— "身份(TContext) + 数据项(TItem)" 的反应式基类。
    /// <para>
    /// 单 shape 设计(2026-05 折叠):framework 只管"context 切换 + 单飞 fetch + 心跳 refresh"三件事;
    /// 业务级 typed 请求(KLine 多种 demand 类型 / SwitchContext 之外的分页操作)由子类用
    /// <see cref="RequestBus{TRequest}"/> 自行组合,framework 零侵入。
    /// </para>
    /// <para>
    /// 子类合约:
    /// </para>
    /// <list type="number">
    ///   <item>实现 <see cref="OnFetchAsync"/>,在适当位置调 <see cref="UpdateBuffer"/> 写 buffer + Publish</item>
    ///   <item>需要分页 / typed 请求时,private 持一个 <see cref="RequestBus{TRequest}"/>,handler 内同样调 <see cref="UpdateBuffer"/></item>
    ///   <item>不要 override Suspend/Resume —— framework 已接管 internal bus 的拆建</item>
    /// </list>
    /// </summary>
    public abstract class ReactiveDataSource<TSource, TContext, TItem> : BufferedDataSource<TSource, TItem>, Hevo.Charting.Abstractions.IRefreshable
        where TSource : ReactiveDataSource<TSource, TContext, TItem>
    {
        // 内部 bus —— 只跑"基于当前 Context 的单飞 fetch"。SwitchContext / Refresh 都走这里。
        // 业务级 typed 请求(KLine demand 多态)由子类自带 RequestBus<XxxDemand, ...>,跟这条不冲突。
        // TResult = int:本次 OnFetchAsync 的回包条数,透传给 LoadAsync / SwitchContextAsync 调用方。
        private readonly RequestBus<Unit, int> _ctxBus;

        public TContext? Context { get; private set; }

        protected ReactiveDataSource()
        {
            _ctxBus = new RequestBus<Unit, int>(async (_, token) =>
            {
                if (Context == null) return 0;
                return await OnFetchAsync(Context, token).ConfigureAwait(false);
            });
        }

        /// <summary>
        /// 子类唯一必填:基于当前 Context 拉数据,适当时机调 <see cref="UpdateBuffer"/> 写 buffer。
        /// 返回值是本次更新条数,透传给 <see cref="LoadAsync"/> 调用方;子类内部分页 bus 完成时也用此返回。
        /// Context = null 不会进到这里(framework 已 short-circuit)。
        /// </summary>
        protected abstract Task<int> OnFetchAsync(TContext context, CancellationToken token);

        /// <summary>
        /// 💥 唯一的 buffer 写入通道:加锁 → 执行 mutation → Publish。
        /// 子类的 OnFetchAsync 跟自带 RequestBus handler 都通过它写 buffer,语义对称、并发安全。
        /// 调用即视为"产生了变更",基类无条件 Publish;子类判断无需变更则不调用即可。
        /// </summary>
        protected void UpdateBuffer(Action<List<TItem>> mutation)
        {
            if (mutation == null) return;
            lock (_lock)
            {
                mutation(_buffer);
                Publish();
            }
        }

        // ----- 门面 API -----

        /// <summary>同步切换上下文:立即清 buffer + Publish + 异步发起首次 fetch。"宁可闪一下也要拔掉旧数据"场景用。</summary>
        public void SwitchContext(TContext context)
        {
            Context = context;
            lock (_lock) { _buffer.Clear(); Publish(); }
            _ctxBus.Push(Unit.Default);
        }

        /// <summary>
        /// 无缝异步切换上下文:不立即清 buffer,等新数据到达后由 <see cref="OnFetchAsync"/> 内 UpdateBuffer 原子替换,消灭 UI 空窗期闪烁。
        /// 返回本次 OnFetchAsync 报告的回包条数。
        /// </summary>
        public Task<int> SwitchContextAsync(TContext context, CancellationToken token = default)
        {
            Context = context;
            return _ctxBus.RequestAsync(Unit.Default, token);
        }

        /// <summary>语法糖:等价 <see cref="SwitchContext"/>,跟 chart 侧 KLineDataSource.Load 协议对齐。</summary>
        public void Load(TContext context) => SwitchContext(context);

        /// <summary>语法糖:等价 <see cref="SwitchContextAsync"/>,只是名字更贴"加载"语义。</summary>
        public Task<int> LoadAsync(TContext context, CancellationToken token = default)
            => SwitchContextAsync(context, token);

        /// <summary>
        /// Phase 2 类型擦除桥 —— 给 <see cref="LowCode.Designer.BlueprintCascadeWiring"/> 装配 Cascade 时用。
        /// 调用方拿到的是 <c>object</c>(driver 投影出的下游 TContext 实例),不知道泛型参数;
        /// 这里在派生类自己的泛型 body 内 <typeparamref name="TContext"/> 编译期可见,直 cast + 调强类型版本,反射零开销。
        /// </summary>
        public virtual Task SwitchContextErasedAsync(object context, CancellationToken token = default)
        {
            if (context is not TContext typed)
                throw new InvalidCastException(
                    $"SwitchContextErasedAsync:context 类型 {context?.GetType().FullName ?? "null"} 不匹配 " +
                    $"{GetType().Name} 的 TContext={typeof(TContext).FullName}。检查 Cascade ContextDriver 返回类型。");
            return SwitchContextAsync(typed, token);
        }

        /// <summary>
        /// IRefreshable 实现:基于当前 Context 重发一次 fetch(不切身份、不清 buffer)。
        /// Context 未设置时静默返回 0;返回本次 OnFetchAsync 报告的回包条数。
        /// </summary>
        public virtual Task<int> RefreshAsync(CancellationToken token = default)
        {
            if (Context == null) return Task.FromResult(0);
            return _ctxBus.RequestAsync(Unit.Default, token);
        }

        // 显式实现 IRefreshable.RefreshAsync 转发到强类型版本(Task<int> 是 Task 的子类,自动协变兼容)
        Task Hevo.Charting.Abstractions.IRefreshable.RefreshAsync(CancellationToken token) => RefreshAsync(token);

        // ==========================================
        // 💥 IPausable:framework 接管 _ctxBus 拆建,子类不必 override。
        //    子类自带的 RequestBus<TRequest> 若需要随 Suspend/Resume 联动,自己 override 这俩追加调用。
        // ==========================================
        public override void Suspend()
        {
            if (!IsActive) return;
            _ctxBus.Suspend();
            base.Suspend();
        }

        public override void Resume()
        {
            if (IsActive) return;
            _ctxBus.Resume();
            base.Resume();
        }

        public override void Dispose()
        {
            _ctxBus.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// 空占位类型 —— framework 内部 ctx bus 用,业务别直接接触。
    /// </summary>
    public readonly struct Unit
    {
        public static readonly Unit Default = default;
    }

    // 历史:5 参 ReactiveDataSource<TSource, TContext, TRequest, TResponse, TItem> 已折叠到上面单 shape;
    // KLine 等需要 typed paged 请求的 DS 改用 RequestBus<TRequest> 自行组合,详见 KLineDataSource 实现。

    public readonly record struct AlignedTuple<TVal1, TVal2>(DateTime Time, TVal1 Value1, TVal2 Value2);

    // ==========================================
    // 💥 虚拟数据源：专门用于接管多流合并后的数据中转
    // ==========================================
    public class VirtualDataSource<TItem> : BufferedDataSource<VirtualDataSource<TItem>, TItem>
    {
        public override int LogicalLength => _readSnapshot.Length;

        // 💥 保存上游 CombineLatest 产生的订阅句柄
        private IDisposable? _subscription;

        // 算子内部装配时，将句柄绑定进来
        internal void BindLifecycle(IDisposable sub) => _subscription = sub;

        public void Feed(List<TItem> data)
        {
            lock (_lock)
            {
                _buffer.Clear();
                if (data != null) _buffer.AddRange(data);
                Publish(); // 触发 0-GC 内存拷贝和纪元推进
            }
        }

        // 💥 当被 OnDispose 钩子调用时，精准拔掉管线的数据源头！
        public override void Dispose()
        {
            _subscription?.Dispose();
            base.Dispose();
        }
    }
}
