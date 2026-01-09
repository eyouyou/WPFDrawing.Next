namespace Hevo.Charting
{
    /// <summary>
    /// 数据源适配器：将命令式的数据推送 (Push) 转换为响应式的工作流 (Workflow)
    /// </summary>
    /// <typeparam name="T">传输的数据类型 (如 List<KLineItem>)</typeparam>
    public class WorkflowTrigger<T> : IWorkflow<T>, Hevo.Charting.Abstractions.IPausable
    {
        // 修复 M2：声明 volatile，确保跨线程读取时始终拿到最新引用，
        // 防止 JIT 将其缓存到寄存器导致 Push 线程看到过期的委托链。
        // 完整的线程安全由下方的 CAS 循环保证（同 .NET event 的编译器生成逻辑）。
        private volatile Action<T>? _subscribers;
        private volatile Action<Exception>? _errorSubscribers;

        // Phase 11 / §H：暂停/恢复闸门。暂停期间 Push 静默丢弃——上游触发仍在，
        // 订阅者不会被惊扰；Resume 由数据源补一次 GetSnapshot() 让 UI 回到最新。
        private volatile bool _isActive = true;
        public bool IsActive => _isActive;
        public void Suspend() => _isActive = false;
        public void Resume() => _isActive = true;

        // 1. 实现接口：线程安全地添加订阅者
        public IDisposable Subscribe(Action<T> onNext, Action<Exception>? onError = null)
        {
            // CAS 循环：读取当前值 → 构造新委托链 → 原子替换（仅当当前值未被其他线程修改时）
            // 若 CAS 失败说明有并发修改，自旋重试，直到成功为止。
            Action<T>? current, updated;
            do
            {
                current = _subscribers;
                updated = current + onNext;
            } while (Interlocked.CompareExchange(ref _subscribers, updated, current) != current);

            if (onError != null)
            {
                Action<Exception>? currentError, updatedError;
                do
                {
                    currentError = _errorSubscribers;
                    updatedError = currentError + onError;
                } while (Interlocked.CompareExchange(ref _errorSubscribers, updatedError, currentError) != currentError);
            }

            return new DisposableAction(() => Remove(onNext, onError));
        }

        /// <summary>
        /// 触发方法 (OnNext)
        /// </summary>
        /// <param name="data"></param>
        public void Push(T data)
        {
            // Phase 11：暂停闸门，冻结期丢弃所有 Push（Resume 由数据源补一帧快照）
            if (!_isActive) return;

            // volatile 读 + null 条件运算符会将引用拷贝到局部变量，
            // 即使之后 _subscribers 被其他线程修改，本次 Invoke 依然在一致的快照上执行。
            _subscribers?.Invoke(data);
        }

        /// <summary>
        /// (OnError)：推送异常信号
        /// </summary>
        /// <param name="ex"></param>
        public void PushError(Exception ex)
        {
            // volatile 读获取快照，安全 Invoke
            _errorSubscribers?.Invoke(ex);
        }

        // 线程安全地移除订阅者（与 Subscribe 对称的 CAS 循环）
        private void Remove(Action<T> onNext, Action<Exception>? onError)
        {
            Action<T>? currentNext, updatedNext;
            do
            {
                currentNext = _subscribers;
                updatedNext = (Action<T>?)Delegate.Remove(currentNext, onNext);
            } while (Interlocked.CompareExchange(ref _subscribers, updatedNext, currentNext) != currentNext);

            // 💥 CAS 摘除错误流通道
            if (onError != null)
            {
                Action<Exception>? currentError, updatedError;
                do
                {
                    currentError = _errorSubscribers;
                    updatedError = (Action<Exception>?)Delegate.Remove(currentError, onError);
                } while (Interlocked.CompareExchange(ref _errorSubscribers, updatedError, currentError) != currentError);
            }
        }
    }
}
