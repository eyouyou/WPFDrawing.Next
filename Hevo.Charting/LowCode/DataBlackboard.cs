using Hevo.Charting.Core;
using System.Collections.Concurrent;

namespace Hevo.Charting.LowCode
{
    /// <summary>
    /// 💥 现代高性能状态黑板 (精确制导 + 事务合并 + 0-GC 存储 + 无限纪元)
    /// 💥 [读写分离最终版] 完美支持并发读、独占写、以及牛逼的“读锁原地升级写锁”！
    /// </summary>
    public class DataBlackboard : IDisposable
    {
        public bool IsDisposed { get; private set; }
        private readonly TypedBucketMap _memory = new();
        private readonly Dictionary<object, Action<DataBlackboard>?> _subscriptions = new();
        private readonly StateClock _boardClock = new();
        private readonly ConcurrentDictionary<object, VersionToken> _portTokens = new();

        // ==========================================
        // 🛡️ 核心装甲升级：工业级读写分离锁
        // 💥 必须开启 SupportsRecursion！这是支持 BeginTransaction 嵌套、以及 Upgrade 锁升级的物理基础！
        // ==========================================
        private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.SupportsRecursion);

        private int _transactionDepth = 0;
        private readonly HashSet<object> _dirtyPorts = new();

        // 修复 H3：预分配快照缓冲，替代 EndTransaction 里每次 ToList() 的堆分配。
        // _portsSnapshot 在首次事务提交后增长到足够容量，之后复用，热路径零分配。
        private readonly List<object> _portsSnapshot = new();

        public event Action<object>? OnPortUpdated;
        public event Action<DataBlackboard>? OnTransactionCommitted;

        public VersionToken GetVersion(object port)
        {
            return _portTokens.TryGetValue(port, out var token) ? token : default;
        }

        // ==========================================
        // 💥 给业务层暴露的三大神器 (支持 using 语法)
        // 返回 readonly struct + duck-typed Dispose:每次 using 0 堆分配。
        // 旧实现是 class 包装,Interaction / Viewport / Paging hot path 每次 mouse / scroll 事件都 new 一个
        // scope 对象;改成 struct 后稳态零 GC。所有 caller 用 `using (...)` 形式,API 调用面不变。
        // ==========================================
        public ReadLockScope AcquireReadLock() { _rwLock.EnterReadLock(); return new ReadLockScope(this); }
        public WriteLockScope AcquireWriteLock() { _rwLock.EnterWriteLock(); return new WriteLockScope(this); }
        public UpgradeableReadLockScope AcquireUpgradeableReadLock() { _rwLock.EnterUpgradeableReadLock(); return new UpgradeableReadLockScope(this); }

        public readonly struct ReadLockScope : IDisposable
        {
            private readonly DataBlackboard _b;
            internal ReadLockScope(DataBlackboard b) { _b = b; }
            public void Dispose() => _b._rwLock.ExitReadLock();
        }

        public readonly struct WriteLockScope : IDisposable
        {
            private readonly DataBlackboard _b;
            internal WriteLockScope(DataBlackboard b) { _b = b; }
            public void Dispose() => _b._rwLock.ExitWriteLock();
        }

        public readonly struct UpgradeableReadLockScope : IDisposable
        {
            private readonly DataBlackboard _b;
            internal UpgradeableReadLockScope(DataBlackboard b) { _b = b; }
            public void Dispose() => _b._rwLock.ExitUpgradeableReadLock();
        }

        // ==========================================
        // 字典修改属于结构性变化，必须上排他写锁
        // ==========================================
        public void Subscribe(object port, Action<DataBlackboard> callback)
        {
            _rwLock.EnterWriteLock();
            try
            {
                if (_subscriptions.TryGetValue(port, out var existingAction)) _subscriptions[port] = existingAction + callback;
                else _subscriptions[port] = callback;
            }
            finally { _rwLock.ExitWriteLock(); }
        }

        public void Unsubscribe(object port, Action<DataBlackboard> callback)
        {
            _rwLock.EnterWriteLock();
            try
            {
                if (_subscriptions.TryGetValue(port, out var existingAction))
                {
                    var newAction = (Action<DataBlackboard>?)Delegate.Remove(existingAction, callback);
                    if (newAction == null) _subscriptions.Remove(port);
                    else _subscriptions[port] = newAction;
                }
            }
            finally { _rwLock.ExitWriteLock(); }
        }

        // ==========================================
        // 📖 极速裸读：没有任何性能损耗！
        // ==========================================
        public T Read<T>(DataPort<T> port)
        {
#if DEBUG
            DevTools.TracerRegistry.Get(this)?.RecordRead(port);

            // 💥 引擎级防弹衣：你敢不带套（不加锁）就来读黑板？
            // 强制约束业务层：必须在 ReadLock 或 WriteLock 范围内调用 Read！
            if (!_rwLock.IsReadLockHeld && !_rwLock.IsWriteLockHeld && !_rwLock.IsUpgradeableReadLockHeld)
            {
                throw new InvalidOperationException($"[黑板越权访问] 读引脚 {port.Id} 失败！必须先调用 AcquireReadLock() 开启快照边界！");
            }
#endif
            if (_memory.TryGetValue<DataPort<T>, T>(port, out var val)) return val;
            return default!;
        }

        // ==========================================
        // ✍️ 极速裸写：把升级和加锁的权利还给外层！
        // ==========================================
        public void WriteIfChanged<T>(DataPort<T> port, T value)
        {
            // 1. 极速查脏
            if (_memory.TryGetValue<DataPort<T>, T>(port, out var oldVal))
            {
                if (EqualityComparer<T>.Default.Equals(oldVal, value)) return;
            }
            ForceWrite(port, value);
        }

        public void ForceWrite<T>(DataPort<T> port, T value)
        {
#if DEBUG
            DevTools.TracerRegistry.Get(this)?.RecordWrite(port);

            // 💥 引擎级防弹衣：必须持有写锁才能修改数据！
            if (!_rwLock.IsWriteLockHeld)
            {
                throw new InvalidOperationException($"[黑板越权篡改] 写引脚 {port.Id} 失败！必须先调用 BeginTransaction() 或 AcquireWriteLock()！");
            }
#endif
            // 2. 0-GC 裸写物理桶
            _memory.Set<DataPort<T>, T>(port, value);

            // 3. 拨动时钟(融合 Advance+Snapshot,单次 Interlocked.Increment 取新值,省一次 barrier)
            _portTokens[port] = _boardClock.AdvanceAndSnapshot();

            // 4. 路由拦截 (此时必在 WriteLock 的保护伞下)
            if (_transactionDepth > 0)
            {
                _dirtyPorts.Add(port);
            }
            else
            {
                NotifyPort(port); // 非事务裸写，依然在业务层的写锁控制下
                OnTransactionCommitted?.Invoke(this);
            }
        }

        private void NotifyPort(object port)
        {
            if (_subscriptions.TryGetValue(port, out var action) && action != null)
            {
                action.Invoke(this);
            }
            OnPortUpdated?.Invoke(port);
        }

        /// <summary>
        /// 开启一个黑板写入事务，期间所有的 Write 不会触发 Watch 回调
        /// </summary>
        public IDisposable BeginTransaction()
        {
            _rwLock.EnterWriteLock(); // 💥 显式上排他写锁，确保事务期间绝对无人能读写
            _transactionDepth++;
            return new TransactionScope(this);
        }

        // 修改 EndTransaction：
        internal void EndTransaction()
        {
            try
            {
                _transactionDepth--;
                if (_transactionDepth == 0)
                {
                    if (_dirtyPorts.Count > 0)
                    {
                        // 修复 H3：用预分配的 _portsSnapshot 替代 ToList()，热路径零堆分配。
                        // _portsSnapshot 充当临时快照缓冲：
                        //   - _dirtyPorts 可能在 NotifyPort 的回调中被重新写入（递归事务），
                        //     因此必须先快照、再清空、再通知，不能边遍历边清空。
                        //   - _portsSnapshot 复用跨事务增长的内部数组，无 GC 压力。
                        _portsSnapshot.Clear();
                        foreach (var p in _dirtyPorts) _portsSnapshot.Add(p);
                        _dirtyPorts.Clear();
                        foreach (var port in _portsSnapshot) NotifyPort(port);
                    }
                    // 💥 事务所有引脚通知完毕，触发最终结算！
                    OnTransactionCommitted?.Invoke(this);
                }
            }
            finally { _rwLock.ExitWriteLock(); } // 💥 释放排他写锁
        }

        private class TransactionScope : IDisposable
        {
            private readonly DataBlackboard _board;
            public TransactionScope(DataBlackboard board) => _board = board;
            public void Dispose() => _board.EndTransaction();
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            // 彻底释放读写锁底层句柄
            _rwLock.Dispose();

            _subscriptions.Clear();
            _dirtyPorts.Clear();
            _portTokens.Clear();
            OnPortUpdated = null;
            OnTransactionCommitted = null;
        }
    }

    // ==========================================
    // 💥 3. 定向花名册订阅系统 (双缓冲安全版)
    // 单锁守护 _portSubscribers + _dirtyFeatures：
    //   - NotifyPortUpdated 是 ForceWrite 后的高频路径,原双锁嵌套(先 _portSubscribers
    //     再 _dirtyLock)每次进出两套 monitor,合并后省一半 lock 开销。
    //   - Subscribe / PopDirtyFeatures / UnsubscribeAll 几乎都在 UI 线程上,合锁后
    //     竞争面没变化。
    // ==========================================
    public class SubscriptionRegistry
    {
        private readonly object _gate = new object();
        private readonly HashSet<ChartFeature> _dirtyFeatures = new();

        // 记录引脚与 Feature 的订阅关系 (通常在 UI 线程建树时操作)
        private readonly Dictionary<object, HashSet<ChartFeature>> _portSubscribers = new();

        /// <summary>隐式订阅：将 Feature 登记为引脚的观察者</summary>
        public void Subscribe<T>(DataPort<T> port, ChartFeature feature)
        {
#if DEBUG
            DevTools.TracerRegistry.Get(this)?.RecordSubscribe(port, feature);
#endif

            // 简单加锁防止建树期的并发
            lock (_gate)
            {
                if (!_portSubscribers.TryGetValue(port, out var features))
                {
                    features = new HashSet<ChartFeature>();
                    _portSubscribers[port] = features;
                }
                features.Add(feature);
            }
        }

        /// <summary>
        /// 联动查脏：将所有订阅了该引脚的 Feature 打入冷宫 (脏列表)
        /// 💥 注意：此方法由黑板在后台线程触发！
        /// </summary>
        public bool NotifyPortUpdated(object port)
        {
            lock (_gate)
            {
                if (_portSubscribers.TryGetValue(port, out var features) && features.Count > 0)
                {
                    foreach (var f in features) _dirtyFeatures.Add(f);
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 💥 工业级引擎的核心交接法：填充模式弹出 (Fill-Pattern Pop)
        /// 修复 H3：原实现每次返回新 List，热路径每帧产生堆分配。
        /// 新 API 直接填充调用方预分配的 HashSet，零堆分配。
        /// 返回 true 代表有脏 Feature 已填入 target；false 代表脏名单为空。
        /// </summary>
        public bool PopDirtyFeatures(HashSet<ChartFeature> target)
        {
            lock (_gate)
            {
                if (_dirtyFeatures.Count == 0) return false;

                // 直接填充目标集合（调用方负责 Clear），避免任何中间 List/HashSet 分配
                foreach (var f in _dirtyFeatures) target.Add(f);
                _dirtyFeatures.Clear();
                return true;
            }
        }

        /// <summary>彻底抹除某个 Feature 的所有订阅记录</summary>
        public void UnsubscribeAll(ChartFeature feature)
        {
            lock (_gate)
            {
                _dirtyFeatures.Remove(feature);
                foreach (var subscribers in _portSubscribers.Values)
                {
                    subscribers.Remove(feature);
                }
            }
        }
    }

    /// <summary>
    /// 💥 统一数据加工管线 (具备自我清理能力的加工厂)
    /// 核心职责：持有所有摄入器规则，复用唯一的黑板，并在销毁时通知下属释放内存池！
    /// </summary>
    public class UniversalDataPipe<TItem> : IDisposable
    {
        private readonly List<IDataIngestor<TItem>> _ingestors = new();

        // 💥 跨帧复用的持久化黑板，避免每帧 new DataBlackboard
        private readonly DataBlackboard _persistentBoard = new();

        public void AddIngestor(IDataIngestor<TItem> ingestor) => _ingestors.Add(ingestor);

        /// <summary>
        /// 💥 核心加工马达：每当新行情到来，遍历执行加工规则
        /// </summary>
        public DataBlackboard Process(DataSnapshot<TItem> snapshot)
        {
            using (_persistentBoard.BeginTransaction())
            {
                // 按添加顺序执行，确保标量先写入，向量后写入
                for (int i = 0; i < _ingestors.Count; i++)
                {
                    // 💥 极简投喂：直接扔快照！脏判断由各个 Ingestor 自己负责
                    _ingestors[i].Process(snapshot, _persistentBoard);
                }
            }
            return _persistentBoard;
        }

        /// <summary>
        /// 定向输出管：允许双引擎管线在鼠标拖拽时，把最新的数据快照强行压入黑板！
        /// </summary>
        /// <param name="items"></param>
        /// <param name="board"></param>
        public void ProcessTo(DataSnapshot<TItem> snapshot, DataBlackboard board)
        {
            using (board.BeginTransaction())
            {
                for (int i = 0; i < _ingestors.Count; i++)
                {
                    _ingestors[i].Process(snapshot, board);
                }
            }
        }

        /// <summary>
        /// 💥 生命周期终点：必须被调用以释放 ArrayPool！
        /// </summary>
        public void Dispose()
        {
            _persistentBoard.Dispose();
            for (int i = 0; i < _ingestors.Count; i++)
            {
                if (_ingestors[i] is IDisposable d) d.Dispose();
            }
            _ingestors.Clear();
        }
    }
}