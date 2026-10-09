using System;
using System.Collections.Generic;
using System.Threading;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.Features;
using Hevo.Charting.WorkFlow;
using Xunit;
using static Hevo.Charting.Tests.IngestorColumnConsistencyTests;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// WatchAsync 的合并语义:同一订阅同时最多一个回调在跑;慢回调期间来的 N 次通知只额外跑一次,
    /// 且那一次读到最新值;回调抛异常后订阅还能继续工作;退订后不再跑待重跑的那次。
    /// 以前每次通知都丢一个线程池任务:慢回调下任务无限堆积,同一订阅并发执行、乱序提交。
    /// </summary>
    public sealed class WatchAsyncSingleFlightTests
    {
        private static void WaitIdle(Func<bool> done, int timeoutMs = 10_000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!done() && DateTime.UtcNow < until) Thread.Sleep(5);
            Thread.Sleep(50);
        }

        [Fact]
        public void SameSubscription_NeverRunsConcurrently()
        {
            int running = 0, maxRunning = 0, runs = 0;
            var gate = new FeatureExtensions.SingleFlight(_ =>
            {
                int now = Interlocked.Increment(ref running);
                int seen;
                while ((seen = Volatile.Read(ref maxRunning)) < now && Interlocked.CompareExchange(ref maxRunning, now, seen) != seen) { }
                Thread.Sleep(5);
                Interlocked.Decrement(ref running);
                Interlocked.Increment(ref runs);
            });

            var threads = new Thread[4];
            for (int t = 0; t < threads.Length; t++)
            {
                threads[t] = new Thread(() => { for (int i = 0; i < 200; i++) { gate.Notify(null!); Thread.SpinWait(500); } });
                threads[t].Start();
            }
            foreach (var t in threads) t.Join();
            WaitIdle(() => Volatile.Read(ref running) == 0);

            Assert.Equal(1, maxRunning);
            Assert.True(runs >= 1 && runs < 800, $"800 次通知跑了 {runs} 次");
        }

        [Fact]
        public void BurstDuringSlowCall_RunsExactlyOnceMore_WithLatestValue()
        {
            int value = 0, runs = 0, lastSeen = -1;
            using var inFirst = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var gate = new FeatureExtensions.SingleFlight(_ =>
            {
                int v = Volatile.Read(ref value); // 回调自己读"黑板"最新值
                if (Interlocked.Increment(ref runs) == 1) { inFirst.Set(); release.Wait(5000); }
                Volatile.Write(ref lastSeen, v);
            });

            gate.Notify(null!);
            Assert.True(inFirst.Wait(5000));
            for (int i = 1; i <= 50; i++) { Volatile.Write(ref value, i); gate.Notify(null!); }
            release.Set();
            WaitIdle(() => Volatile.Read(ref runs) >= 2);

            Assert.Equal(2, runs);
            Assert.Equal(50, lastSeen);
        }

        [Fact]
        public void ThrowingCallback_DoesNotWedgeSubscription()
        {
            int runs = 0;
            var gate = new FeatureExtensions.SingleFlight(_ =>
            {
                if (Interlocked.Increment(ref runs) == 1) throw new InvalidOperationException("boom");
            });

            gate.Notify(null!);
            WaitIdle(() => Volatile.Read(ref runs) >= 1);
            gate.Notify(null!);
            WaitIdle(() => Volatile.Read(ref runs) >= 2);

            Assert.Equal(2, runs);
        }

        [Fact]
        public void Disposed_SkipsPendingRerun()
        {
            int runs = 0;
            using var inFirst = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var gate = new FeatureExtensions.SingleFlight(_ =>
            {
                if (Interlocked.Increment(ref runs) == 1) { inFirst.Set(); release.Wait(5000); }
            });

            gate.Notify(null!);
            Assert.True(inFirst.Wait(5000));
            gate.Notify(null!);   // 待重跑
            gate.Dispose();       // 退订
            release.Set();
            WaitIdle(() => false, 300);

            Assert.Equal(1, runs);
        }

        /// <summary>
        /// 端到端:真 ComputeFeature,handler 随机慢(0–15 ms),后台不停 Publish 递增代号。
        /// 提交出去的结果按代号单调不减(不会旧结果覆盖新结果),最后一次提交是最后一代。
        /// </summary>
        [Fact]
        public void ComputeFeature_SlowHandler_CommitsInOrder_AndEndsOnLatest()
        {
            var committed = new List<double>();
            double finalOut = double.NaN;
            int lastGen = 0;
            IncrementalTestKit.RunOnSta(() =>
            {
                var rnd = new ThreadLocal<Random>(() => new Random(Environment.CurrentManagedThreadId));
                var outPort = new DataPort<ReadOnlyMemory<double>>("SF_Out");
                var schema = new RaceSchema(s => new Feature[]
                {
                    new ComputeFeature
                    {
                        InputPort = s.Mapped,
                        OutputPort = outPort,
                        Compute = (Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>)(col =>
                        {
                            double g = col.Span[0];
                            Thread.Sleep(rnd.Value!.Next(0, 15));
                            lock (committed) committed.Add(g); // 合并后同一订阅串行:返回顺序 = 提交顺序
                            return new[] { g };
                        }),
                    },
                });
                var cell = new ChartCell { Template = schema };
                using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);

                for (int gen = 1; gen <= 300; gen++) { schema.Source.PublishGen(gen, 100); Thread.Sleep(1); }
                lastGen = 300;
                WaitIdle(() => { lock (committed) return committed.Count > 0 && committed[^1] == 300; });
                lock (committed) finalOut = committed.Count > 0 ? committed[^1] : double.NaN;
            });

            lock (committed)
            {
                for (int i = 1; i < committed.Count; i++)
                    Assert.True(committed[i] >= committed[i - 1], $"第 {i} 次提交 {committed[i]} 比上一次 {committed[i - 1]} 旧");
                Assert.True(committed.Count < 300, $"300 次推送算了 {committed.Count} 次,没有合并");
            }
            Assert.Equal(lastGen, finalOut);
        }
    }
}
