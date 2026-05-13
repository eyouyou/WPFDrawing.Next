using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hevo.Charting.PythonNet
{
    /// <summary>
    /// §D2.5.5 Python handler 超时硬切。<see cref="PythonSandboxOptions.PerCallTimeoutMs"/> &gt; 0
    /// 时,<see cref="PythonNetModule.Invoke"/> 走本类包裹一次:超时把调用方放出来抛
    /// <see cref="TimeoutException"/>,Python 线程继续跑到自然结束。
    ///
    /// <para>
    /// <b>实装(MVP / best-effort)</b>:Task.Run + Task.Wait(timeoutMs)。
    /// </para>
    /// <list type="bullet">
    ///   <item>✅ 主调用方不阻塞 —— 超时后立刻拿到 TimeoutException,UI 不会卡。</item>
    ///   <item>⚠️ Python 线程不被真打断 —— 仍跑到函数自然结束(可能继续占 GIL,后续 Invoke 阻塞)。
    ///         设计文档 §D2.5.5 已明示此限制:numpy native ops 即使有
    ///         <c>PyThreadState_SetAsyncExc</c> 也不立刻生效(CPython 在字节码循环检查点才检查异步异常)。</item>
    ///   <item>⚠️ 不实装 <c>Runtime.PyThreadState_SetAsyncExc</c> 强中断 —— pythonnet 3.0.x
    ///         未公开此 API,反射调内部成员版本/平台脆弱;真要硬切走"重启 PythonEngine"路径,
    ///         不在 D2.5.5 scope。</item>
    /// </list>
    ///
    /// <para>
    /// <b>建议</b>:超时阈值给宽松点(典型 1000-5000ms),并相信"分析师不会写 30s 阻塞函数"。
    /// 真有长 numpy reduce 的指标改 chunk size,框架不强中断。
    /// </para>
    /// </summary>
    internal static class PerCallTimeoutWatchdog
    {
        /// <param name="timeoutMs"><=0 = 关超时(直接 inline 调 <paramref name="action"/>);&gt;0 = 启 Task.Run watchdog。</param>
        /// <param name="action">实际执行的工作单元。<b>调用方不应已持 GIL</b> —— GIL acquire 必须在 <paramref name="action"/> 内做(否则跨线程 nested acquire 死锁)。</param>
        public static T RunWithTimeout<T>(int timeoutMs, Func<T> action)
        {
            if (timeoutMs <= 0) return action();

            // Task.Run 把 action 调度到线程池,内部 acquire GIL 跑 Python 调用。
            var task = Task.Run(action);

            // ⚠️ Task.Wait(timeoutMs) 在 task **异常完成**时直接抛 AggregateException(不是返 false),
            // 必须 try/catch unwrap inner exception。否则 PythonDiagnosticsException 会被 Aggregate 包裹,
            // 调用方 `catch (PythonDiagnosticsException)` 拿不到。Wait 返回:
            //   - true  → task 成功完成
            //   - false → 超时
            //   - throw → task 异常完成
            bool completed;
            try
            {
                completed = task.Wait(timeoutMs);
            }
            catch (AggregateException agg)
            {
                throw agg.InnerException ?? agg;
            }
            if (completed) return task.Result;   // task 成功 → 取结果

            // 超时:caller 放出来,task 仍在跑(orphan)
            throw new TimeoutException(
                $"Python handler 超时 ({timeoutMs}ms)。" +
                "Python 线程可能仍在跑(numpy native ops 不可中断 —— CPython 设计限制,详见 §D2.5.5)。");
        }

        /// <summary>void 重载 —— 给 HandlerFeature 这种纯副作用路径用。</summary>
        public static void RunWithTimeout(int timeoutMs, Action action)
        {
            if (timeoutMs <= 0) { action(); return; }
            var task = Task.Run(action);
            // 同 Func 路径 ——  Wait 异常完成时抛 AggregateException,unwrap inner。
            bool completed;
            try
            {
                completed = task.Wait(timeoutMs);
            }
            catch (AggregateException agg)
            {
                throw agg.InnerException ?? agg;
            }
            if (completed) return;

            throw new TimeoutException(
                $"Python handler 超时 ({timeoutMs}ms)。" +
                "Python 线程可能仍在跑(numpy native ops 不可中断 —— CPython 设计限制,详见 §D2.5.5)。");
        }
    }
}
