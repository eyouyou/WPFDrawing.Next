using System;
using System.IO;
using System.Threading;
using Hevo.Charting.LowCode;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 可让出的列读者:WatchAsync 回调登记为可让出,长计算(Python handler)期间让出登记,不挡全进程缓冲复用;
    /// 帧 / 同步 Watch 登记不可让出。断言直接看自己占的槽位,不受并行测试里其它读者的影响。
    /// </summary>
    public sealed class ColumnReadScopeReleaseTests
    {
        [Fact]
        public void Releasable_ParksSlotAndReentersWithFreshEpoch()
        {
            using var scope = ColumnReadScope.Begin(releasable: true);
            int slot = scope.SlotIndex;
            long entered = ColumnReadScope.ReadSlot(slot);
            Assert.True(ColumnReadScope.CanReleaseForLongCall);

            using (var released = ColumnReadScope.ReleaseForLongCall())
            {
                Assert.True(released.IsReleased);
                Assert.Equal(long.MaxValue, ColumnReadScope.ReadSlot(slot));   // 让出中:不挡任何回收,槽位仍归本线程
                ColumnReadScope.Retire();                                      // 期间全局纪元前进
            }
            Assert.True(ColumnReadScope.ReadSlot(slot) > entered);              // 回来按新纪元重新登记
        }

        [Fact]
        public void NonReleasable_OrNested_CannotRelease()
        {
            using (var frame = ColumnReadScope.Begin())
            {
                Assert.False(ColumnReadScope.CanReleaseForLongCall);
                using var r = ColumnReadScope.ReleaseForLongCall();
                Assert.False(r.IsReleased);
                Assert.NotEqual(long.MaxValue, ColumnReadScope.ReadSlot(frame.SlotIndex));

                using (ColumnReadScope.Begin(releasable: true))
                    Assert.False(ColumnReadScope.CanReleaseForLongCall);           // 外层是帧,整体不可让出
            }
            Assert.False(ColumnReadScope.CanReleaseForLongCall);                // 没有登记时也不可让出
        }
    }

    /// <summary>真 Python handler:在可让出的读者里调用时,Python 运行期间槽位处于让出状态,且入参内容完整传到 Python。</summary>
    [Collection(nameof(RealPythonCollection))]
    public sealed class PythonHandlerReleaseTests
    {
        private readonly RealPythonFixture _fx;
        public PythonHandlerReleaseTests(RealPythonFixture fx) { _fx = fx; }

        [Fact]
        public void PythonCall_InsideReleasableReader_ReleasesSlotDuringCall()
        {
            if (!_fx.Available) return;
            File.WriteAllText(Path.Combine(_fx.IndicatorsDir, "release_probe.py"), """
                from hevo_indicators import register
                import numpy as np
                import time

                @register('release_probe_sum', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
                def release_probe_sum(arr):
                    time.sleep(0.3)
                    return np.array([float(np.asarray(arr).sum())], dtype=np.float64)
                """);
            _fx.Registry!.AutoDiscoverDirectory(_fx.IndicatorsDir);
            var fn = (Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>)_fx.Registry.TryGet("release_probe_sum")!;

            int slot = -1;
            long during = 0, entered = 0;
            double result = 0;
            var input = new double[] { 1, 2, 3, 4 };
            using var started = new ManualResetEventSlim(false);
            var worker = new Thread(() =>
            {
                using var scope = ColumnReadScope.Begin(releasable: true);   // 跟 WatchAsync 回调一样
                slot = scope.SlotIndex;
                entered = ColumnReadScope.ReadSlot(slot);
                started.Set();
                result = fn(input).Span[0];
            });
            worker.Start();
            started.Wait();
            Thread.Sleep(150);                                             // Python 正在 sleep
            during = ColumnReadScope.ReadSlot(slot);
            worker.Join();

            Assert.Equal(long.MaxValue, during);
            Assert.Equal(10, result);
        }
    }
}
