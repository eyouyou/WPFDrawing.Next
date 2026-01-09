using Hevo.Charting.Core;

namespace Hevo.Charting
{
    // =================================================================
    // 顶层接口：纯数据流 (Pure Data Tier)
    // =================================================================
    // 没有任何 UI 依赖，可以在 Console、Service、Backend 任意地方跑
    public interface IWorkflow<out T>
    {
        // 订阅流：当数据流转时触发 action
        // 返回的 IDisposable 用于取消本次订阅（内部会存入 Session）
        IDisposable Subscribe(Action<T> onNext, Action<Exception>? onError = null);
    }

    // =================================================================
    // 渲染流接口 (Render Tier)
    // =================================================================
    // 代表数据流已经“插”在某个图表控件上，但还没开始绘制
    // 注意：持有的是长周期的 ChartCell，而不是瞬态的 RenderContext
    public interface IRenderFlow<out T> : IWorkflow<T>
    {
        // 显式暴露 Context，避免强制转换
        ChartCell Chart { get; }
        // 这是一个标记接口，用于挂载扩展方法 (Step, Fetch, etc.)

        /// <summary>
        /// 将任何一个新的 IWorkflow 重新包装成当前 Chart 下的 IRenderFlow
        /// </summary>
        /// <typeparam name="TOut"></typeparam>
        /// <param name="nextWorkflow"></param>
        /// <returns></returns>
        IRenderFlow<TOut> Wrap<TOut>(IWorkflow<TOut> nextWorkflow);
    }
}
