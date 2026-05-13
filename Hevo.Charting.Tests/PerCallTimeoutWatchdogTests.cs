using System;
using System.Diagnostics;
using System.Threading;
using Hevo.Charting.PythonNet;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §D2.5.5 / §D2.8 PerCallTimeoutWatchdog 单元测试 —— 纯 C# 逻辑,不依赖 Python 解释器,
    /// xUnit 默认并行 collection 跑(快)。
    /// </summary>
    public sealed class PerCallTimeoutWatchdogTests
    {
        // ── Func<T> 路径 ───────────────────────────────────────────────────

        [Fact]
        public void Func_TimeoutZero_RunsInline_NoTask()
        {
            // timeoutMs <= 0 = 关超时,直接 inline 跑(不走 Task.Run,无 thread 切换)。
            var threadIdInside = -1;
            int callerTid = Environment.CurrentManagedThreadId;
            var result = PerCallTimeoutWatchdog.RunWithTimeout(0, () =>
            {
                threadIdInside = Environment.CurrentManagedThreadId;
                return 42;
            });
            Assert.Equal(42, result);
            Assert.Equal(callerTid, threadIdInside);   // inline = same thread
        }

        [Fact]
        public void Func_FastWork_ReturnsResult()
        {
            var result = PerCallTimeoutWatchdog.RunWithTimeout(1000, () =>
            {
                Thread.Sleep(50);   // 远小于 1000ms timeout
                return "ok";
            });
            Assert.Equal("ok", result);
        }

        [Fact]
        public void Func_SlowerThanTimeout_ThrowsTimeoutException()
        {
            var sw = Stopwatch.StartNew();
            var ex = Assert.Throws<TimeoutException>(() =>
                PerCallTimeoutWatchdog.RunWithTimeout(100, () =>
                {
                    Thread.Sleep(2000);   // 远超 100ms
                    return 0;
                }));
            sw.Stop();
            // 调用方应在接近 100ms 时拿到 TimeoutException,不该等满 2000ms
            Assert.True(sw.ElapsedMilliseconds < 1500,
                $"Watchdog 应早早抛出 TimeoutException,实际等了 {sw.ElapsedMilliseconds}ms");
            Assert.Contains("超时", ex.Message);
        }

        [Fact]
        public void Func_InnerThrowsPythonDiagnostics_PropagatesAsIs()
        {
            // PerCallTimeoutWatchdog 不该把 PythonDiagnosticsException 包成 TimeoutException 或 AggregateException ——
            // 调用方应直接收到原始异常(traceback 才不会丢)。
            var ex = Assert.Throws<PythonDiagnosticsException>(() =>
                PerCallTimeoutWatchdog.RunWithTimeout(1000, () =>
                {
                    throw new PythonDiagnosticsException(
                        "test error",
                        pythonExceptionType: "ValueError",
                        pythonTraceback: "Traceback ...",
                        sourceFilePath: "test.py");
                    #pragma warning disable CS0162
                    return 0;
                    #pragma warning restore CS0162
                }));
            Assert.Equal("ValueError", ex.PythonExceptionType);
            Assert.Equal("Traceback ...", ex.PythonTraceback);
        }

        // ── Action 路径 ─────────────────────────────────────────────────────

        [Fact]
        public void Action_TimeoutZero_RunsInline()
        {
            bool ran = false;
            PerCallTimeoutWatchdog.RunWithTimeout(0, () => { ran = true; });
            Assert.True(ran);
        }

        [Fact]
        public void Action_SlowerThanTimeout_Throws()
        {
            var sw = Stopwatch.StartNew();
            Assert.Throws<TimeoutException>(() =>
                PerCallTimeoutWatchdog.RunWithTimeout(100, () => Thread.Sleep(2000)));
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 1500);
        }

        [Fact]
        public void Action_InnerThrows_UnwrapsAggregate()
        {
            // Action 路径 task.Wait 完成后手动 unwrap AggregateException.InnerException
            var ex = Assert.Throws<InvalidOperationException>(() =>
                PerCallTimeoutWatchdog.RunWithTimeout(1000, () =>
                {
                    throw new InvalidOperationException("business logic broken");
                }));
            Assert.Equal("business logic broken", ex.Message);
        }

        // ── 边界:timeoutMs = 0 / 负数 = 不启 watchdog ──────────────────────

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-1000)]
        public void Func_NonPositiveTimeout_NoWatchdog(int timeoutMs)
        {
            // <=0 直接 inline,不走 Task.Run。
            var sw = Stopwatch.StartNew();
            var result = PerCallTimeoutWatchdog.RunWithTimeout(timeoutMs, () =>
            {
                Thread.Sleep(50);
                return 1;
            });
            sw.Stop();
            Assert.Equal(1, result);
            // inline 应该差不多就是 50ms,Task.Run 路径会多几 ms 调度税
            Assert.True(sw.ElapsedMilliseconds < 200);
        }
    }
}
