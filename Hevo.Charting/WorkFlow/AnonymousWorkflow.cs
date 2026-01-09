namespace Hevo.Charting
{
    /// <summary>
    /// 💥 匿名工作流适配器
    /// 将基于委托的订阅逻辑 (Func) 动态包装为标准的 <see cref="IWorkflow{T}"/> 接口。
    /// 适用于快速创建无需维护复杂状态的轻量级数据流。
    /// </summary>
    /// <typeparam name="T">数据流承载的元素类型</typeparam>
    internal class AnonymousWorkflow<T> : IWorkflow<T>
    {
        private readonly Func<Action<T>, Action<Exception>?, IDisposable> _subscribe;

        /// <summary>
        /// 初始化匿名工作流
        /// </summary>
        /// <param name="subscribe">真实的订阅执行函数，负责连接上游数据源并返回注销句柄</param>
        public AnonymousWorkflow(Func<Action<T>, Action<Exception>?, IDisposable> subscribe)
        {
            _subscribe = subscribe;
        }

        /// <summary>
        /// 💥 执行订阅：直接将下游的观察者委托透传给内部装配好的订阅逻辑。
        /// </summary>
        /// <param name="onNext">接收正常数据的回调</param>
        /// <param name="onError">接收异常的错误回调</param>
        /// <returns>用于切断该条流式管道的注销令牌</returns>
        public IDisposable Subscribe(Action<T> onNext, Action<Exception>? onError = null)
        {
            return _subscribe(onNext, onError);
        }
    }
}
