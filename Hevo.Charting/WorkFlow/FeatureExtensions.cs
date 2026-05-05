using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.WorkFlow
{
    internal sealed class FeatureComposeScope
    {
        private static readonly AsyncLocal<FeatureComposeScope?> CurrentSlot = new();

        private readonly IRenderFlow<DataBlackboard> _baseFlow;
        private readonly List<Func<IRenderFlow<DataBlackboard>, IRenderFlow<DataBlackboard>>> _transforms = new();

        private FeatureComposeScope(IRenderFlow<DataBlackboard> baseFlow)
        {
            _baseFlow = baseFlow;
        }

        public static FeatureComposeScope? Current => CurrentSlot.Value;

        public static IDisposable Enter(IRenderFlow<DataBlackboard> baseFlow)
        {
            var previous = CurrentSlot.Value;
            CurrentSlot.Value = new FeatureComposeScope(baseFlow);
            return new DisposableAction(() => CurrentSlot.Value = previous);
        }

        public void AddTransform(Func<IRenderFlow<DataBlackboard>, IRenderFlow<DataBlackboard>> transform)
        {
            _transforms.Add(transform);
        }

        public IRenderFlow<DataBlackboard> Build()
        {
            var flow = _baseFlow;
            foreach (var transform in _transforms)
            {
                flow = transform(flow);
            }
            return flow;
        }
    }

    public static class FeatureExtensions
    {
        // ==========================================
        // ⚡ 算子 1：同步 Watch (排版、切片专用)
        // 语义：在当前线程立刻执行，绝不切后台！
        // ==========================================
        public static IWorkflow<DataBlackboard> Watch(
            this IWorkflow<DataBlackboard> flow,
            object[] listenPorts,
            Action<DataBlackboard> sideEffect)
        {
#if DEBUG
            // 拓扑追踪闭环:跟 WatchAsync 对称,把 sideEffect 包在 EnterScope(owner) 里。
            // 否则 sync Watch 回调里的 board.Read(...) 因为 _currentCaller.Value == null
            // 不会被 RecordRead 记录,导致 TopologyInspector 漏掉这条 feature → port 的连线。
            object owner = DevTools.TopologyTracer.SetupContext.Value ?? "UnknownOwner";
            Action<DataBlackboard> tracedAction = b =>
            {
                using (DevTools.TopologyTracer.EnterScope(owner))
                {
                    sideEffect(b);
                }
            };
            return AttachWatchCore(flow, listenPorts, tracedAction);
#else
            return AttachWatchCore(flow, listenPorts, sideEffect);
#endif
        }

        // ==========================================
        // 🚀 算子 2：异步 WatchAsync (重度指标专用)
        // 语义：自动分发到后台线程池，由开发者自己加锁保护数据！
        // ==========================================
        public static IWorkflow<DataBlackboard> WatchAsync(
            this IWorkflow<DataBlackboard> flow,
            object[] listenPorts,
            Action<DataBlackboard> sideEffect)
        {
#if DEBUG
            object owner = DevTools.TopologyTracer.SetupContext.Value ?? "UnknownOwner";
#endif
            Action<DataBlackboard> backgroundAction = b =>
            {
                Core.HevoDispatcher.FireAndForget(() =>
                {
#if DEBUG
                    using (DevTools.TopologyTracer.EnterScope(owner))
#endif
                    {
                        sideEffect(b);
                    }
                });
            };

            return AttachWatchCore(flow, listenPorts, backgroundAction);
        }

        /// <summary>
        /// IRenderFlow 的安全包装层，提供链式调用
        /// </summary>
        public static IRenderFlow<DataBlackboard> Watch(
            this IRenderFlow<DataBlackboard> flow,
            object[] listenPorts,
            Action<DataBlackboard> sideEffect)
        {
            IWorkflow<DataBlackboard> safeBaseFlow = ((IWorkflow<DataBlackboard>)flow).Watch(listenPorts, sideEffect);
            var wrapped = flow.Wrap(safeBaseFlow);
            FeatureComposeScope.Current?.AddTransform(current => current.Wrap(((IWorkflow<DataBlackboard>)current).Watch(listenPorts, sideEffect)));
            return wrapped;
        }

        /// <summary>
        /// IRenderFlow 的安全包装层，提供链式调用
        /// </summary>
        public static IRenderFlow<DataBlackboard> WatchAsync(
            this IRenderFlow<DataBlackboard> flow,
            object[] listenPorts,
            Action<DataBlackboard> sideEffect)
        {
            IWorkflow<DataBlackboard> safeBaseFlow = ((IWorkflow<DataBlackboard>)flow).WatchAsync(listenPorts, sideEffect);
            var wrapped = flow.Wrap(safeBaseFlow);
            FeatureComposeScope.Current?.AddTransform(current => current.Wrap(((IWorkflow<DataBlackboard>)current).WatchAsync(listenPorts, sideEffect)));
            return wrapped;
        }

        // ==========================================
        // 🧠 终极 DRY 内核：只处理订阅、防抖与结算！
        // ==========================================
        private static IWorkflow<DataBlackboard> AttachWatchCore(
            IWorkflow<DataBlackboard> flow,
            object[] listenPorts,
            Action<DataBlackboard> executeAction)
        {
            DataBlackboard? currentBoard = null;
            Action<DataBlackboard>? dirtyMarker = null;
            Action<DataBlackboard>? commitHandler = null;
            bool hasInitialized = false;
            object switchLock = new object();

            var hookedFlow = flow.Do(board =>
            {
                bool shouldRunInit = false;

                lock (switchLock)
                {
                    if (currentBoard != board)
                    {
                        if (currentBoard != null)
                        {
                            if (dirtyMarker != null) foreach (var port in listenPorts) currentBoard.Unsubscribe(port, dirtyMarker);
                            if (commitHandler != null) currentBoard.OnTransactionCommitted -= commitHandler;
                        }

                        currentBoard = board;
                        bool isDirty = false;
                        object watchLock = new object();

                        dirtyMarker = _ => { lock (watchLock) isDirty = true; };
                        commitHandler = b =>
                        {
                            // 修复 M5：OnTransactionCommitted 的 event `-=` 是原子的，但 DataBlackboard
                            // 端 `Invoke` 先把 delegate 读到局部再调用，`-=` 若发生在这两步之间，已排队的
                            // 调用依旧会落到本 handler，闭包里的 executeAction 可能访问已销毁资源。
                            // 在 switchLock 内复查 currentBoard：DoOnDispose 会先置 null / 切换 board，
                            // 不匹配即安全退出。
                            lock (switchLock) { if (currentBoard != b) return; }

                            bool shouldRun = false;
                            lock (watchLock) { if (isDirty) { shouldRun = true; isDirty = false; } }
                            if (shouldRun) executeAction(b);
                        };

                        foreach (var port in listenPorts) currentBoard.Subscribe(port, dirtyMarker);
                        currentBoard.OnTransactionCommitted += commitHandler;
                    }

                    if (!hasInitialized)
                    {
                        hasInitialized = true;
                        shouldRunInit = true;
                    }
                }

                if (shouldRunInit) executeAction(board);
            });

            return hookedFlow.DoOnDispose(() =>
            {
                lock (switchLock)
                {
                    if (currentBoard != null)
                    {
                        if (dirtyMarker != null) foreach (var port in listenPorts) currentBoard.Unsubscribe(port, dirtyMarker);
                        if (commitHandler != null) currentBoard.OnTransactionCommitted -= commitHandler;
                        currentBoard = null;
                        dirtyMarker = null;
                        commitHandler = null;
                    }
                }
            });
        }

        public static IRenderFlow<DataBlackboard> CombineLatest(
            this IRenderFlow<DataBlackboard> flow,
            object[] requiredPorts,
            Action<DataBlackboard> sideEffect)
        {
            IWorkflow<DataBlackboard> baseFlow = ((IWorkflow<DataBlackboard>)flow).WatchAsync(requiredPorts, b =>
            {
                for (int i = 0; i < requiredPorts.Length; i++)
                {
                    if (b.GetVersion(requiredPorts[i]) == default) return;
                }
                sideEffect(b);
            });

            var wrapped = flow.Wrap(baseFlow);
            FeatureComposeScope.Current?.AddTransform(current => current.CombineLatest(requiredPorts, sideEffect));
            return wrapped;
        }

        public static IRenderFlow<DataBlackboard> Merge(
            this IRenderFlow<DataBlackboard> source,
            IRenderFlow<DataBlackboard> other)
        {
            IWorkflow<DataBlackboard> combined = new WorkflowEngine<DataBlackboard>((next, error) =>
            {
                var sub1 = source.Subscribe(next, error);
                var sub2 = other.Subscribe(next, error);
                // Phase 11 / §H.10：CompositeDisposable 多上游 fan-out IPausable
                return new CompositeDisposable(sub1, sub2);
            });
            var wrapped = source.Wrap(combined);
            FeatureComposeScope.Current?.AddTransform(current => current.Merge(other));
            return wrapped;
        }

        public static IRenderFlow<DataBlackboard> Join(
            this IRenderFlow<DataBlackboard> source,
            params object[] requiredPorts)
        {
            IWorkflow<DataBlackboard> filtered = ((IWorkflow<DataBlackboard>)source).Where(b =>
            {
                foreach (var port in requiredPorts)
                {
                    if (b.GetVersion(port).Equals(default(VersionToken)))
                        return false;
                }
                return true;
            });
            var wrapped = source.Wrap(filtered);
            FeatureComposeScope.Current?.AddTransform(current => current.Join(requiredPorts));
            return wrapped;
        }


        public static IWorkflow<TResult> WithLatestFrom<TSource, TOther, TResult>(
            this IWorkflow<TSource> source,
            IWorkflow<TOther> other,
            Func<TSource, TOther, TResult> selector)
        {
            return new WorkflowEngine<TResult>((onNext, onError) =>
            {
                object gate = new object();
                TOther? latestOther = default;
                bool hasOther = false;

                var otherSub = other.Subscribe(
                    val =>
                    {
                        lock (gate)
                        {
                            latestOther = val;
                            hasOther = true;
                        }
                    },
                    onError
                );

                var sourceSub = source.Subscribe(
                    val =>
                    {
                        TResult result;
                        lock (gate)
                        {
                            if (!hasOther) return;
                            result = selector(val, latestOther!);
                        }
                        onNext(result);
                    },
                    onError
                );

                return new CompositeDisposable(otherSub, sourceSub);
            });
        }

        /// <summary>
        /// 把当前 IDisposable 的完整生命周期托管给 host（schema / feature / session）。
        /// 含义不止 dispose——host 在 Suspend / Resume 时也会级联通知（IPausable），
        /// 业务子类（如 AutoRefreshSchema）还会顺手做 IRefreshable 级联。
        /// </summary>
        public static T OwnedBy<T>(this T subscription, IDisposableHost host) where T : class, IDisposable
        {
            if (subscription is null) throw new ArgumentNullException(nameof(subscription));
            host.RegisterDisposable(subscription);
            return subscription;
        }
    }
}
