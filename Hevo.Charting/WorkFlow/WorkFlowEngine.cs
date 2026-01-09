using Hevo.Charting.Abstractions;

namespace Hevo.Charting
{
    /// <summary>
    /// 💥 暂停闸门（Phase 11 / §H.10）：共享给 WorkflowEngine.Subscribe 与其他需要按位拦截的算子。
    /// 采用 volatile 写 + 读保证线程可见性；CAS 级别的同步由调用方（通常是 WorkflowEngine 的 lockObj）负责。
    /// </summary>
    internal sealed class PauseGate : IPausable
    {
        private volatile bool _isActive = true;
        public bool IsActive => _isActive;
        public void Suspend() => _isActive = false;
        public void Resume() => _isActive = true;
    }

    /// <summary>
    /// 💥 可暂停订阅（Phase 11 / §H.10）：WorkflowEngine.Subscribe 的返回值统一升级。
    ///
    /// 两级 pause 保护：
    ///   1. 本地 gate：拦截 SafeNext/SafeError 调用，下游看不到任何事件；
    ///   2. upstream IPausable 透传：若上游订阅本身是 IPausable（比如根源的 IntervalSubscription），
    ///      Suspend 会一路传上去让真正的源头停下来（CPU/定时器都真停）。
    ///
    /// 任何基于 <see cref="WorkflowEngine{T}"/> 构造的算子（Select/Where/FetchExclusive/CombineLatest/...）
    /// 订阅后都自动产出此类型，业务层无需任何改动即可支持暂停。
    /// </summary>
    public sealed class PausableSubscription : IDisposable, IPausable
    {
        private readonly Action _disposeAction;
        private readonly PauseGate _gate;
        private readonly IPausable? _upstream;
        private bool _disposed;

        internal PausableSubscription(Action disposeAction, PauseGate gate, IPausable? upstream)
        {
            _disposeAction = disposeAction;
            _gate = gate;
            _upstream = upstream;
        }

        public bool IsActive => _gate.IsActive;

        public void Suspend()
        {
            _gate.Suspend();
            _upstream?.Suspend();   // 透传到上游（最终触达 IntervalSubscription 等时序源）
        }

        public void Resume()
        {
            _upstream?.Resume();    // 先恢复源头让事件流动起来
            _gate.Resume();         // 再开下游闸门
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _disposeAction();
        }
    }

    /// <summary>
    /// 基础工作流节点 通过这个把所有流程进行编排
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public sealed class WorkflowEngine<T> : IWorkflow<T>
    {
        // 上游的订阅逻辑
        private readonly Func<Action<T>, Action<Exception>, IDisposable> _subscribeLogic;

        public WorkflowEngine(Func<Action<T>, Action<Exception>, IDisposable> logic)
        {
            _subscribeLogic = logic;
        }

        public IDisposable Subscribe(Action<T> onNext, Action<Exception>? onError = null)
        {
            // 状态守卫：防止重复 Dispose 或并发问题
            bool isUnsubscribed = false;
            object lockObj = new object();
            var gate = new PauseGate();   // Phase 11：订阅级暂停闸门

            // 安全的回调包装：暂停期静默吞掉，下游完全感知不到
            void SafeNext(T val)
            {
                if (!gate.IsActive) return;
                lock (lockObj) { if (!isUnsubscribed) onNext(val); }
            }
            void SafeError(Exception ex)
            {
                if (!gate.IsActive) return;
                lock (lockObj) { if (!isUnsubscribed) onError?.Invoke(ex); }
            }

            try
            {
                // 执行上游逻辑，拿到上游的销毁句柄
                var upstreamDisposable = _subscribeLogic(SafeNext, SafeError);

                // 返回给下游的订阅：PausableSubscription 自动继承上游 IPausable（若有）
                return new PausableSubscription(
                    disposeAction: () =>
                    {
                        lock (lockObj)
                        {
                            if (isUnsubscribed) return;
                            isUnsubscribed = true;
                        }
                        upstreamDisposable?.Dispose();
                    },
                    gate: gate,
                    upstream: upstreamDisposable as IPausable);
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
                return DisposableAction.Empty;
            }
        }
    }

    // 简单的 IDisposable 包装
    public class DisposableAction : IDisposable
    {
        public static readonly DisposableAction Empty = new(() => { });
        private readonly Action _action;
        public DisposableAction(Action action) => _action = action;
        public void Dispose() => _action?.Invoke();
    }

    /// <summary>
    /// 💥 丢弃物容器 (弹夹)
    /// 内部装载多个 IDisposable，统一销毁！
    /// Phase 11 / §H.10：同时实现 IPausable，fan-out 到所有可暂停的子项——
    /// Merge/CombineLatest 等多上游算子内部用它合并订阅，自动获得多源同步暂停能力。
    /// </summary>
    public class CompositeDisposable : IDisposable, IPausable
    {
        private readonly List<IDisposable> _disposables = new();
        private bool _isDisposed;
        private volatile bool _isActive = true;

        public bool IsActive => _isActive;

        public CompositeDisposable(params IDisposable[] disposables)
        {
            _disposables.AddRange(disposables);
        }

        public void Add(IDisposable disposable)
        {
            lock (_disposables)
            {
                if (_isDisposed) disposable.Dispose();
                else _disposables.Add(disposable);
            }
        }

        public void Suspend()
        {
            _isActive = false;
            lock (_disposables)
                foreach (var d in _disposables)
                    if (d is IPausable p) p.Suspend();
        }

        public void Resume()
        {
            _isActive = true;
            lock (_disposables)
                foreach (var d in _disposables)
                    if (d is IPausable p) p.Resume();
        }

        public void Dispose()
        {
            lock (_disposables)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                foreach (var d in _disposables) d.Dispose();
                _disposables.Clear();
            }
        }
    }
}
