using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 💥 引擎专属时空穿梭机：用最优雅的 await 实现无闭包的线程切换！
    /// </summary>
    public static class HevoThread
    {
        // 💥 瞬移到后台线程池！
        public static ThreadPoolSwitchAwaiter SwitchToBackground() => new ThreadPoolSwitchAwaiter();

        // 💥 瞬移回主线程！(传入图表所在的 Dispatcher)
        public static DispatcherSwitchAwaiter SwitchToUI(Dispatcher dispatcher) => new DispatcherSwitchAwaiter(dispatcher);

        // ==========================================
        // 核心实现：欺骗编译器，接管 await 状态机
        // ==========================================
        public struct ThreadPoolSwitchAwaiter : INotifyCompletion
        {
            public ThreadPoolSwitchAwaiter GetAwaiter() => this;

            // 永远返回 false，强迫 await 挂起当前方法，交出控制权
            public bool IsCompleted => false;

            public void GetResult() { } // 不返回任何值

            // 💥 当 await 挂起后，让状态机在 ThreadPool 里恢复执行！
            public void OnCompleted(Action continuation) => ThreadPool.QueueUserWorkItem(_ => continuation());
        }

        public struct DispatcherSwitchAwaiter : INotifyCompletion
        {
            private readonly Dispatcher _dispatcher;
            public DispatcherSwitchAwaiter(Dispatcher dispatcher) => _dispatcher = dispatcher;
            public DispatcherSwitchAwaiter GetAwaiter() => this;

            // 如果已经在主线程了，直接继续执行，0 损耗！
            public bool IsCompleted => _dispatcher.CheckAccess();

            public void GetResult() { }

            // 如果在后台线程，则把剩下的代码扔回主线程执行！
            public void OnCompleted(Action continuation) => _dispatcher.InvokeAsync(continuation);
        }
    }

    /// <summary>
    /// 💥 引擎专属工作线程分发器
    /// 核心能力：异步防崩溃护盾 + 时空堆栈缝合 + 极简同步调试开关
    /// </summary>
    public static class HevoDispatcher
    {
#if DEBUG
        // ==========================================
        // 💥 终极调试开关 (Debug模式专属)
        // 当你遇到诡异 Bug 需要看完美的原生 VS 调用堆栈时，将其设为 true。
        // 此时所有的 WatchAsync 将退化为主线程同步执行！指哪打哪！
        // ==========================================
        public static bool ForceSyncForDebugging { get; set; } = false;
#endif

        public static void FireAndForget(Action action, string ownerName = "Unknown")
        {
#if DEBUG
            // 1. 调试模式：纯同步执行，保留完美堆栈
            if (ForceSyncForDebugging)
            {
                action();
                return;
            }

            // 2. 抓取“时空照片”：记录是谁（主线程的哪个方法）触发了这次异步投递
            // skipFrames: 1 表示跳过当前的 FireAndForget 方法，直接抓业务调用方
            string enqueueStackTrace = new StackTrace(1, true).ToString();
#endif
            // 3. 正常并发模式：扔进线程池
            Task.Run(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
#if DEBUG
                    // 💥 核心黑科技：时空缝合！
                    string msg = $@"
🚨 [HevoDispatcher 异步计算崩溃]
归属方: {ownerName}
--------------------------------------------------
[1. 因果溯源 (是谁/在哪个环节触发了这次重算)]
{enqueueStackTrace}
--------------------------------------------------
[2. 真实崩溃现场 (具体报错代码行)]
{ex.Message}
{ex.StackTrace}";

                    // 打印到 VS 输出窗口，或者接入你的日志中心
                    Debug.WriteLine(msg);

                    // 强烈建议在 Debug 下依然把异常抛出，让开发者立刻感知！
                    // throw new Exception(msg, ex); 
#else
                    // Release 模式：极致轻量，安全吞掉异常，保证金融终端不闪退！
                    Debug.WriteLine($"[HevoDispatcher Error] {ownerName}: {ex.Message}");
#endif
                }
            });
        }
    }
}
