using Hevo.Charting.Core;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting
{
    /// <summary>
    /// 💥 顶层数据源基类：只负责低 GC 快照与管线构建 (双缓冲读写分离版)
    /// </summary>
    public abstract class DataSource<TSource, TItem> : IDisposable, IMappingBase, Hevo.Charting.Abstractions.IPausable
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


    public interface IMappingBase
    {
        int LogicalLength { get; }
    }

    /// <summary>
    /// 连续模式: 专门服务于连续数据 (如 K 线)
    /// </summary>
    public interface IContinuousMapping : IMappingBase { }

    /// <summary>
    /// 散列模式
    /// [内核契约] 结构化坑位映射能力
    /// 任何具备规律性坑位的数据源（如股票分时、分类数据）实现此接口。
    /// 内核只认 Index，绝不沾染 Time 等业务属性。
    /// </summary>
    public interface IStructuredMapping<TItem> : IMappingBase
    {
        int MapToIndex(TItem item);
    }

    /// <summary>
    /// 💥 视口驱动型泛型数据源 (终极异步契约版)
    /// 【绝对纯净】：没有任何 Enum，没有任何 DateTime，只做排队调度。
    /// 【异步穿透】：支持 TaskCompletionSource，完美桥接响应式流与 async/await！
    /// </summary>
    public abstract class ReactiveDataSource<TSource, TContext, TRequest, TResponse, TItem> : DataSource<TSource, TItem>
        where TSource : ReactiveDataSource<TSource, TContext, TRequest, TResponse, TItem>
    {
        // ==========================================
        // 💥 核武器：请求信封 (包装原始请求与生命周期令牌)
        // ==========================================
        protected readonly struct RequestEnvelope
        {
            public TRequest Request { get; }
            public TaskCompletionSource<int>? Tcs { get; } // 💥 升级：携带有返回值的支票，用于兑现真实回包数量！
            public CancellationToken Token { get; } // 💥 携带独立生命周期令牌

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

        protected ReactiveDataSource()
        {
            // 💥 核心管线：请求流入 -> 异步抓取 -> 拦截丧尸 -> 线程安全合并 -> 解锁 UI
            _pipelineSub = _requestBus
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
                        if (res.Error != null)
                        {
                            res.Env.Tcs?.TrySetException(res.Error);
                            return;
                        }

                        // ==========================================
                        // 💥 终极防火墙：丧尸数据拦截器！
                        // ==========================================
                        if (res.Env.Token.IsCancellationRequested)
                        {
                            System.Diagnostics.Debug.WriteLine("🚨 [DataSource] 拦截到超时丧尸数据，已在合并前安全销毁！");
                            res.Env.Tcs?.TrySetCanceled(res.Env.Token);
                            return;
                        }

                        int fetchedCount = 0;

                        // 只有通过了防火墙的纯洁数据，才允许加锁合并！
                        lock (_lock)
                        {
                            // 💥 从子类获取真实合并结果与网络回包数量！
                            var mergeResult = OnMerge(_buffer, res.Response, res.Env.Request);
                            fetchedCount = mergeResult.FetchedCount;

                            if (mergeResult.IsDirty) Publish();
                        }

                        // 💥 完美收官：数据已就位，兑现支票，并把真实数量交给 UI 引擎！
                        res.Env.Tcs?.TrySetResult(fetchedCount);
                    }
                    catch (Exception mergeEx)
                    {
                        res.Env.Tcs?.TrySetException(mergeEx);
                    }
                });
        }

        public void SwitchContext(TContext context, TRequest initialRequest)
        {
            Context = context;
            lock (_lock) { _buffer.Clear(); Publish(); }
            _requestBus.Push(new RequestEnvelope(initialRequest, null, CancellationToken.None));
        }

        /// <summary>
        /// 💥 无缝异步切换上下文 (The Seamless Switcher)
        /// 不会立刻清空旧数据，而是等待新数据到达后，通过 OnMerge 原子级覆盖！彻底消灭 UI 闪烁！
        /// </summary>
        public async Task<int> SwitchContextAsync(TContext context, TRequest initialRequest, CancellationToken token = default)
        {
            // 1. 切换上下文令牌
            Context = context;

            // 💥 绝对红线：这里千万不要调用 _buffer.Clear() 和 Publish()！
            // 让旧数据继续留在黑板上供 UI 渲染，避免出现“空窗期闪烁”。
            // (旧数据会在几十毫秒后，被安全送达的 OnMerge 连根替换)

            // 2. 复用 RequestAsync 的核心发车逻辑
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = token.CanBeCanceled ? token.Register(() => tcs.TrySetCanceled(token)) : default;

            _requestBus.Push(new RequestEnvelope(initialRequest, tcs, token));

            // 3. 挂起等待，直到管线处理完毕并返回数量
            return await tcs.Task;
        }

        public void Request(TRequest request)
        {
            if (Context != null)
                _requestBus.Push(new RequestEnvelope(request, null, CancellationToken.None));
        }

        /// <summary>
        /// 💥 史诗级进化接口：支持 CancellationToken 的彻底异步取消，并返回拉取数量！
        /// </summary>
        public async Task<int> RequestAsync(TRequest request, CancellationToken token = default)
        {
            if (Context == null) return 0;

            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            // 💥 神级防泄漏修补：await using 保证任务不论成功、失败还是超时，
            // 只要跳出此方法，立刻销毁注册链，绝不污染全局 Token！
            await using var registration = token.CanBeCanceled ? token.Register(() => tcs.TrySetCanceled(token)) : default;

            _requestBus.Push(new RequestEnvelope(request, tcs, token));

            // 💥 挂起等待下游流处理完毕，并接收返回的 FetchedCount
            return await tcs.Task;
        }

        protected void MergeDirectly(TResponse response, TRequest mockRequest)
        {
            lock (_lock)
            {
                var mergeResult = OnMerge(_buffer, response, mockRequest);
                if (mergeResult.IsDirty) Publish();
            }
        }

        protected abstract Task<TResponse> OnFetchAsync(TContext? context, TRequest request, CancellationToken token);

        // 💥 升级签名：强迫子类汇报是否产生重绘 (IsDirty) 以及真实的拉取数量 (FetchedCount)
        protected abstract (bool IsDirty, int FetchedCount) OnMerge(List<TItem> buffer, TResponse response, TRequest request);

        // ==========================================
        // 💥 Phase 11 / §H：在基类闸门之上，追加请求管线的冷冻 + 上下文刷新
        //    Suspend：切断 FetchLatest 管线，防止丧尸回包；
        //    Resume ：重建管线 + 按当前 Context 补一次增量请求（子类可重载 OnBuildRefreshRequest）。
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
            if (_pipelineSub == null)
            {
                // 重建与构造时同构的 FetchLatest 管线
                _pipelineSub = _requestBus
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
                            if (res.Env.Token.IsCancellationRequested) { res.Env.Tcs?.TrySetCanceled(res.Env.Token); return; }

                            int fetchedCount = 0;
                            lock (_lock)
                            {
                                var mergeResult = OnMerge(_buffer, res.Response, res.Env.Request);
                                fetchedCount = mergeResult.FetchedCount;
                                if (mergeResult.IsDirty) Publish();
                            }
                            res.Env.Tcs?.TrySetResult(fetchedCount);
                        }
                        catch (Exception mergeEx) { res.Env.Tcs?.TrySetException(mergeEx); }
                    });
            }
            base.Resume();

            // 补一次刷新请求：Resume 瞬间保证数据新鲜度。子类可通过 OnBuildRefreshRequest 定制（默认不发）。
            var refreshReq = OnBuildRefreshRequest();
            if (Context != null && refreshReq != null)
                _requestBus.Push(new RequestEnvelope(refreshReq, null, CancellationToken.None));
        }

        /// <summary>
        /// 子类重载以在 Resume 时补一次定制刷新请求（如 KLine 拉最新 5 根、TimeShare 重订阅推送）。
        /// 默认返回 null，表示不发补刷。
        /// </summary>
        protected virtual TRequest? OnBuildRefreshRequest() => default;

        public override void Dispose()
        {
            _pipelineSub?.Dispose();
            base.Dispose();
        }
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
