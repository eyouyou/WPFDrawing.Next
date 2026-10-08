using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hevo.Charting.LowCode;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Xunit;
using TypeInfoCache = Hevo.Charting.LowCode.Designer.GraphViewer.BlueprintLauncher.DryRunTypeInfo;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// DryRun 端口元数据缓存(按类型)跟 PortMetadataRegistry 方向登记的一致性:
    /// 登记后缓存必须失效;建表途中有人登记,过期条目也不能被继续使用;并发读写下最终看到的都是最新方向。
    /// 每个用例用自己专属的探针类型,避免全局登记表污染别的测试。
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class DryRunTypeInfoCacheTests
    {
        private sealed class SeqProbe { public DataPort<double>? A { get; set; } public DataPort<double>? B { get; set; } }
        private sealed class RaceProbe { public DataPort<double>? A { get; set; } public DataPort<double>? B { get; set; } }
        private sealed class StressProbe { public DataPort<double>? A { get; set; } public DataPort<double>? B { get; set; } }

        private static PortDirection Dir(Type t, string name) =>
            TypeInfoCache.Get(t).DataPorts.Single(p => p.Property.Name == name).Direction;

        [Fact]
        public void RegisterOutputs_AfterCached_InvalidatesEntry()
        {
            var t = typeof(SeqProbe);
            Assert.Equal(PortDirection.Input, Dir(t, "B"));
            var first = TypeInfoCache.Get(t);
            Assert.Same(first, TypeInfoCache.Get(t)); // 版本没变 → 命中缓存

            PortMetadataRegistry.RegisterOutputs(t, "B");

            Assert.NotSame(first, TypeInfoCache.Get(t));
            Assert.Equal(PortDirection.Output, Dir(t, "B"));
            Assert.Equal(PortDirection.Input, Dir(t, "A"));
        }

        [Fact]
        public void RegisterDuringBuild_StaleEntryIsNotServed()
        {
            // 复现竞态(两个线程,用钩子把交错固定下来):
            //   A 建表,读完旧方向(B=Input)还没写入缓存 → 此时方向被登记(B=Output)
            //   → 读者 C 发现版本变了开始重建,但还没写入 → A 先把旧表写进缓存 → C 再写入。
            // 修复前的"版本变了就清空整表 + GetOrAdd"实现里,C 的 GetOrAdd 会拿到 A 写进去的旧表,
            // 而全局版本已经是新的,旧表从此一直被当成最新的用。
            var t = typeof(RaceProbe);
            using var cBuilding = new ManualResetEventSlim(false);
            using var aStored = new ManualResetEventSlim(false);
            int calls = 0;
            Task? reader = null;

            TypeInfoCache.BuildScannedForTest = type =>
            {
                if (type != t) return;
                switch (Interlocked.Increment(ref calls))
                {
                    case 1: // A:旧方向已读完
                        PortMetadataRegistry.RegisterOutputs(t, "B");
                        reader = Task.Run(() => TypeInfoCache.Get(t)); // C
                        Assert.True(cBuilding.Wait(TimeSpan.FromSeconds(10)));
                        break;
                    case 2: // C:新方向已读完,等 A 先写入
                        cBuilding.Set();
                        Assert.True(aStored.Wait(TimeSpan.FromSeconds(10)));
                        break;
                }
            };
            try
            {
                var fromA = TypeInfoCache.Get(t);
                Assert.Equal(PortDirection.Input, fromA.DataPorts.Single(p => p.Property.Name == "B").Direction);
                aStored.Set();
                Assert.True(reader!.Wait(TimeSpan.FromSeconds(10)));
            }
            finally
            {
                aStored.Set();
                TypeInfoCache.BuildScannedForTest = null;
            }

            Assert.Equal(PortDirection.Output, Dir(t, "B")); // 之后的读取不能再拿到 A 写进去的旧表
        }

        [Fact]
        public void ConcurrentReadsAndRegistration_EndUpWithLatestDirection()
        {
            var t = typeof(StressProbe);
            using var start = new ManualResetEventSlim(false);
            var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                start.Wait();
                for (int i = 0; i < 2000; i++) TypeInfoCache.Get(t);
            })).ToArray();

            start.Set();
            Thread.Sleep(1);
            PortMetadataRegistry.RegisterOutputs(t, "A");
            Task.WaitAll(readers);

            Assert.Equal(PortDirection.Output, Dir(t, "A"));
            Assert.Equal(PortDirection.Input, Dir(t, "B"));
        }
    }
}
