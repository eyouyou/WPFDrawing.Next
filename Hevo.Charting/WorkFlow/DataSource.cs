using Hevo.Charting.Core;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting
{
    /// <summary>
    /// 💥 顶层数据源基类：只负责低 GC 快照与管线构建 (双缓冲读写分离版)
    /// </summary>
    public abstract class DataSource<TSource, TItem> : IDisposable, Hevo.Charting.Abstractions.IPausable
        where TSource : DataSource<TSource, TItem>
    {
        protected readonly WorkflowTrigger<DataSnapshot<TItem>> _trigger = new();
        public IWorkflow<DataSnapshot<TItem>> Stream => _trigger;
        public abstract int LogicalLength { get; }

        // ==========================================
        // 💥 Phase 11 / §H：IPausable 生命周期（TabControl / 虚拟化 / IsVisibleChanged）
        //    基类负责闸门 + 触发器暂停；ReactiveDataSource 派生层追加请求管线的拆解与补刷。
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
            // 补一次快照：Resume 后 UI 立刻看到当前数据，不必等下一次 Publish
            _trigger.Push(GetSnapshot());
        }

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
        /// 💥 核心：将后厨案板的数据上菜到展示柜
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

                // 2. 物理拷贝：把后厨数据端到前台
                _buffer.CopyTo(_readSnapshot);

                // 3. 拨动时钟
                _dataClock.Advance();
                VersionToken newVersion = _dataClock.Snapshot();

                // 4. 💥 关键点：传出的是物理数组，但告诉外层有效长度是 _buffer.Count！
                _trigger.Push(new DataSnapshot<TItem>(_readSnapshot, _buffer.Count, newVersion));
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

        public virtual void Dispose() { }
    }


    /// <summary>
    /// 请求驱动型泛型数据源：承载"身份 + 多类请求分发 + 按请求类型定制合并策略"的完整三元组。
    /// 适用于需要在同一 Context 下发起多种 TRequest 的复杂流（典型代表：KLine 分页的 History / Range / Latest）；
    /// 90% 只需"身份 + 拉一次"的业务请改继承 3 参精简版 <see cref="ReactiveDataSource{TSource, TContext, TItem}"/>。
    /// </summary>
    public abstract class ReactiveDataSource<TSource, TContext, TRequest, TResponse, TItem> : DataSource<TSource, TItem>
        where TSource : ReactiveDataSource<TSource, TContext, TRequest, TResponse, TItem>
    {
        protected readonly struct RequestEnvelope
        {
            public TRequest Request { get; }
            public TaskCompletionSource<int>? Tcs { get; }   // null = fire-and-forget；非 null 用于 RequestAsync 回传条数
            public CancellationToken Token { get; }          // 调用方独立的生命周期令牌

            public RequestEnvelope(TRequest request, TaskCompletionSource<int>? tcs, CancellationToken token)
            {
                Request = request;
                Tcs = tcs;
                Token = token;
            }
        }

        private readonly WorkflowTrigger<RequestEnvelope> _requestBus = new();
        private IDisposable? _pipelineSub;

        public TContext? Context { get; private set; }

        protected ReactiveDataSource() => _pipelineSub = BuildPipeline();

        /// <summary>
        /// 管线装配：请求入队 → FetchLatest 调度 OnFetchAsync → 丧尸拦截 → 锁内 OnMerge → 回传 TCS。
        /// 构造期首建、Suspend/Resume 后重建都走这里，保证两条路径行为严格一致。
        /// </summary>
        private IDisposable BuildPipeline()
        {
            return _requestBus
                .FetchLatest(async (env, token) =>
                {
                    try
                    {
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, env.Token);
                        var response = await OnFetchAsync(Context, env.Request, linkedCts.Token);
                        return (Env: env, Response: response, Error: (Exception?)null);
                    }
                    catch (Exception ex)
                    {
                        return (Env: env, Response: default(TResponse)!, Error: ex);
                    }
                })
                .Subscribe(res =>
                {
                    try
                    {
                        if (res.Error != null) { res.Env.Tcs?.TrySetException(res.Error); return; }

                        // 💥 丧尸拦截：调用方已取消时，绝不能再触碰 buffer
                        if (res.Env.Token.IsCancellationRequested)
                        {
                            res.Env.Tcs?.TrySetCanceled(res.Env.Token);
                            return;
                        }

                        int fetchedCount;
                        lock (_lock)
                        {
                            var mergeResult = OnMerge(_buffer, res.Response, res.Env.Request);
                            fetchedCount = mergeResult.FetchedCount;
                            if (mergeResult.IsDirty) Publish();
                        }
                        res.Env.Tcs?.TrySetResult(fetchedCount);
                    }
                    catch (Exception mergeEx)
                    {
                        res.Env.Tcs?.TrySetException(mergeEx);
                    }
                });
        }

        /// <summary>
        /// 同步切换上下文：立即清空 buffer 并 Publish，适用于"宁可闪一下也要立刻拔掉旧数据"的场景。
        /// </summary>
        public void SwitchContext(TContext context, TRequest initialRequest)
        {
            Context = context;
            lock (_lock) { _buffer.Clear(); Publish(); }
            _requestBus.Push(new RequestEnvelope(initialRequest, null, CancellationToken.None));
        }

        /// <summary>
        /// 无缝异步切换上下文：不立即清 buffer，等新数据到达后由 OnMerge 原子替换，消灭 UI 空窗期闪烁。
        /// 💥 与 <see cref="SwitchContext"/> 的差异是刻意的，不要"顺手"加上 _buffer.Clear()。
        /// </summary>
        public async Task<int> SwitchContextAsync(TContext context, TRequest initialRequest, CancellationToken token = default)
        {
            Context = context;

            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = token.CanBeCanceled ? token.Register(() => tcs.TrySetCanceled(token)) : default;

            _requestBus.Push(new RequestEnvelope(initialRequest, tcs, token));
            return await tcs.Task;
        }

        /// <summary>Fire-and-forget 请求。Context 未设置时静默丢弃。</summary>
        public void Request(TRequest request)
        {
            if (Context != null)
                _requestBus.Push(new RequestEnvelope(request, null, CancellationToken.None));
        }

        /// <summary>可 await 可取消的请求；返回本次网络回包条数。Context 未设置时立即返回 0。</summary>
        public async Task<int> RequestAsync(TRequest request, CancellationToken token = default)
        {
            if (Context == null) return 0;

            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            // 💥 await using 保证任务无论成功/失败/超时跳出本方法时，回调注册链必被销毁，防止 Token 泄漏
            await using var registration = token.CanBeCanceled ? token.Register(() => tcs.TrySetCanceled(token)) : default;

            _requestBus.Push(new RequestEnvelope(request, tcs, token));
            return await tcs.Task;
        }

        protected abstract Task<TResponse> OnFetchAsync(TContext? context, TRequest request, CancellationToken token);
        protected abstract (bool IsDirty, int FetchedCount) OnMerge(List<TItem> buffer, TResponse response, TRequest request);

        // ==========================================
        // 💥 IPausable：Suspend 切断请求管线防止丧尸回包写入；Resume 重建管线并按需补一次刷新。
        // ==========================================
        public override void Suspend()
        {
            if (!IsActive) return;
            _pipelineSub?.Dispose();
            _pipelineSub = null;
            base.Suspend();
        }

        public override void Resume()
        {
            if (IsActive) return;
            _pipelineSub ??= BuildPipeline();
            base.Resume();

            var refreshReq = OnBuildRefreshRequest();
            if (Context != null && refreshReq != null)
                _requestBus.Push(new RequestEnvelope(refreshReq, null, CancellationToken.None));
        }

        /// <summary>
        /// 子类重载以在 Resume 时补一次定制刷新请求（如 KLine 拉最新 N 根）。默认返回 null 表示不发补刷。
        /// </summary>
        protected virtual TRequest? OnBuildRefreshRequest() => default;

        public override void Dispose()
        {
            _pipelineSub?.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// 空占位类型：当 ReactiveDataSource 不需要 TRequest/TResponse 语义时使用。
    /// </summary>
    public readonly struct Unit
    {
        public static readonly Unit Default = default;
    }

    // ==========================================
    // 💥 3 参精简基类：只承载"身份 (TContext) + 数据项 (TItem)"
    //    核心哲学：基类不替业务决定合并策略，只提供一个并发安全的 buffer 写入闸门。
    //    - 吞掉 TRequest / TResponse 两个几乎总被占位的泛型槽
    //    - 没有 OnMerge 家族：子类在 OnFetchAsync 里和推送回调里都用同一把 UpdateBuffer
    //    - fetch 与 push 完全对称、路径合一，不再存在"空快照抹掉推送数据"这类窗口
    // ==========================================
    public abstract class ReactiveDataSource<TSource, TContext, TItem>
        : ReactiveDataSource<TSource, TContext, Unit, int, TItem>
        where TSource : ReactiveDataSource<TSource, TContext, TItem>
    {
        // ----- sealed 适配 5 参管线：TResponse = int 只用来把本次拉到的条数交给 TCS -----
        protected sealed override Task<int> OnFetchAsync(TContext? context, Unit request, CancellationToken token)
            => OnFetchAsync(context, token);

        // buffer 已经在 OnFetchAsync 里通过 UpdateBuffer 写完并 Publish 过，这里一律报告"未再弄脏"
        protected sealed override (bool IsDirty, int FetchedCount) OnMerge(List<TItem> buffer, int response, Unit request)
            => (false, response);

        /// <summary>
        /// 子类唯一需要实现的方法：拉数据 + 在适当时机调用 <see cref="UpdateBuffer"/> 写入。
        /// 返回值是本次拉到的条数，会透传给 <c>LoadAsync</c> 的调用者。
        /// </summary>
        protected abstract Task<int> OnFetchAsync(TContext? context, CancellationToken token);

        /// <summary>
        /// 💥 唯一的 buffer 写入通道：加锁 → 执行 mutation → Publish。
        /// fetch 回调 和 push 回调 都调它，保证语义对称、并发安全。
        /// 调用即视为"产生了变更"，基类会无条件 Publish；若子类判断无需变更，直接不要调它。
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

        // ----- 门面 API：把"设置身份 + 首次拉取"合并为一次调用 -----
        public void Load(TContext context) => SwitchContext(context, Unit.Default);
        public Task<int> LoadAsync(TContext context, CancellationToken token = default)
            => SwitchContextAsync(context, Unit.Default, token);
    }

    public readonly record struct AlignedTuple<TVal1, TVal2>(DateTime Time, TVal1 Value1, TVal2 Value2);

    // ==========================================
    // 💥 虚拟数据源：专门用于接管多流合并后的数据中转
    // ==========================================
    public class VirtualDataSource<TItem> : DataSource<VirtualDataSource<TItem>, TItem>
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
