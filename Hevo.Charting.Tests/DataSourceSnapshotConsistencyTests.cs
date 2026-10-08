using System;
using System.Threading;
using Hevo.Charting.Core;
using Hevo.Charting.WorkFlow;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// BufferedDataSource 的锁外读路径(GetSnapshot / RepublishLatest)在后台线程持续 Publish 时,
    /// 拿到的快照必须完整一致:Count 不超过数组长度、[0, Count) 内每个元素都来自同一次 Publish、
    /// 没有半写的 struct。旧实现直接返回会被原地覆盖的 _readSnapshot,并在锁外分两次读数组和 _buffer.Count。
    /// </summary>
    public sealed class DataSourceSnapshotConsistencyTests
    {
        /// <summary>4 个字段都由同一个代号派生,任何一个对不上就是读到了半写的元素或混了两次 Publish。</summary>
        public readonly record struct RaceItem(long Gen, long Neg, long Twice, long Plus1);

        public sealed class RaceSource : BufferedDataSource<RaceSource, RaceItem>
        {
            public override int LogicalLength => 0;

            public void PublishGeneration(long gen, int count)
            {
                lock (_lock)
                {
                    _buffer.Clear();
                    for (int i = 0; i < count; i++) _buffer.Add(new RaceItem(gen, -gen, gen * 2, gen + 1));
                    Publish();
                }
            }
        }

        private static string? Check(DataSnapshot<RaceItem> snap)
        {
            if (snap.Count > snap.Items.Length) return $"Count {snap.Count} > Items.Length {snap.Items.Length}";
            if (snap.Count == 0) return null;
            long gen = snap.Items[0].Gen;
            for (int i = 0; i < snap.Count; i++)
            {
                var x = snap.Items[i];
                if (x.Gen != gen || x.Neg != -gen || x.Twice != gen * 2 || x.Plus1 != gen + 1)
                    return $"第 {i} 个元素 {x} 跟第 0 个的代号 {gen} 不一致";
            }
            return null;
        }

        /// <summary>后台线程不停 Publish(长度在 1000 / 1500 之间交替,数组扩到 1500 后一直原地覆盖)。</summary>
        private static (Thread Thread, Func<bool> Stop) StartPublisher(RaceSource ds)
        {
            bool stop = false;
            var t = new Thread(() =>
            {
                long gen = 1;
                while (!Volatile.Read(ref stop)) ds.PublishGeneration(gen++, gen % 2 == 0 ? 1000 : 1500);
            }) { IsBackground = true };
            t.Start();
            return (t, () => { Volatile.Write(ref stop, true); t.Join(); return true; });
        }

        [Fact]
        public void GetSnapshot_UnderConcurrentPublish_IsConsistent()
        {
            var ds = new RaceSource();
            ds.PublishGeneration(0, 1500);
            var (_, stop) = StartPublisher(ds);
            string? firstBad = null;
            int bad = 0, total = 0;
            var until = DateTime.UtcNow.AddMilliseconds(500);
            while (DateTime.UtcNow < until)
            {
                total++;
                if (Check(ds.GetSnapshot()) is { } err) { bad++; firstBad ??= err; }
            }
            stop();
            Assert.True(bad == 0, $"{bad}/{total} 个快照不一致,例如:{firstBad}");
        }

        [Fact]
        public void RepublishLatest_UnderConcurrentPublish_PushesConsistentSnapshot()
        {
            var ds = new RaceSource();
            ds.PublishGeneration(0, 1500);
            int me = Environment.CurrentManagedThreadId;
            string? firstBad = null;
            int bad = 0, total = 0;
            // 订阅者在推送线程上同步执行;只检查本线程 RepublishLatest 推出来的那些(后台 Publish 的推送在锁内,本来就安全)
            using var sub = ds.Stream.Subscribe(snap =>
            {
                if (Environment.CurrentManagedThreadId != me) return;
                total++;
                if (Check(snap) is { } err) { bad++; firstBad ??= err; }
            });
            var (_, stop) = StartPublisher(ds);
            var until = DateTime.UtcNow.AddMilliseconds(500);
            while (DateTime.UtcNow < until) ds.RepublishLatest();
            stop();
            Assert.True(total > 0);
            Assert.True(bad == 0, $"{bad}/{total} 次补推不一致,例如:{firstBad}");
        }
    }
}
