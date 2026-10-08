using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// SubscriptionRegistry:端口 → Feature 花名册 + 脏名单。ProjectAll 每帧靠 PopDirtyFeatures 决定重算谁。
    /// </summary>
    public sealed class SubscriptionRegistryTests
    {
        private readonly DataPort<double> _a = new("A");
        private readonly DataPort<double> _b = new("B");

        [Fact]
        public void Notify_WithoutSubscribers_ReturnsFalse_AndNothingDirty()
        {
            var registry = new SubscriptionRegistry();
            Assert.False(registry.NotifyPortUpdated(_a));
            Assert.False(registry.PopDirtyFeatures(new HashSet<Feature>()));
        }

        [Fact]
        public void Notify_DirtiesOnlySubscribersOfThatPort()
        {
            var registry = new SubscriptionRegistry();
            var fa = new ProbeFeature("a");
            var fb = new ProbeFeature("b");
            registry.Subscribe(_a, fa);
            registry.Subscribe(_b, fb);

            Assert.True(registry.NotifyPortUpdated(_a));

            var dirty = new HashSet<Feature>();
            Assert.True(registry.PopDirtyFeatures(dirty));
            Assert.Equal(new Feature[] { fa }, dirty.ToArray());
        }

        [Fact]
        public void Pop_ClearsDirtyList_SecondPopReturnsFalse()
        {
            var registry = new SubscriptionRegistry();
            var f = new ProbeFeature("f");
            registry.Subscribe(_a, f);
            registry.NotifyPortUpdated(_a);

            Assert.True(registry.PopDirtyFeatures(new HashSet<Feature>()));
            Assert.False(registry.PopDirtyFeatures(new HashSet<Feature>()));
        }

        [Fact]
        public void Pop_FillsTarget_WithoutClearingIt()
        {
            // 填充模式:清空 target 是调用方(ReactiveSchema._dirtySetBuffer)的责任
            var registry = new SubscriptionRegistry();
            var existing = new ProbeFeature("existing");
            var f = new ProbeFeature("f");
            registry.Subscribe(_a, f);
            registry.NotifyPortUpdated(_a);

            var target = new HashSet<Feature> { existing };
            registry.PopDirtyFeatures(target);

            Assert.Contains(existing, target);
            Assert.Contains(f, target);
        }

        [Fact]
        public void DuplicateSubscribe_AndMultiplePortsPerFeature_YieldSingleDirtyEntry()
        {
            var registry = new SubscriptionRegistry();
            var f = new ProbeFeature("f");
            registry.Subscribe(_a, f);
            registry.Subscribe(_a, f);
            registry.Subscribe(_b, f);

            registry.NotifyPortUpdated(_a);
            registry.NotifyPortUpdated(_b);
            registry.NotifyPortUpdated(_a);

            var dirty = new HashSet<Feature>();
            registry.PopDirtyFeatures(dirty);
            Assert.Single(dirty);
        }

        [Fact]
        public void UnsubscribeAll_RemovesPendingDirtyAndFutureNotifications()
        {
            var registry = new SubscriptionRegistry();
            var f = new ProbeFeature("f");
            registry.Subscribe(_a, f);
            registry.NotifyPortUpdated(_a);

            registry.UnsubscribeAll(f);

            Assert.False(registry.PopDirtyFeatures(new HashSet<Feature>()));
            Assert.False(registry.NotifyPortUpdated(_a));
        }

        [Fact]
        public void ConcurrentNotifyAndPop_LosesNoDirtyFeature()
        {
            // NotifyPortUpdated 可能来自后台写线程,PopDirtyFeatures 在 UI 线程;两者同锁,标脏不能丢。
            const int writers = 8;
            const int notifiesPerWriter = 2000;
            var registry = new SubscriptionRegistry();
            var ports = Enumerable.Range(0, writers).Select(i => new DataPort<double>($"P{i}")).ToArray();
            var features = Enumerable.Range(0, writers).Select(i => new ProbeFeature($"F{i}")).ToArray();
            for (int i = 0; i < writers; i++) registry.Subscribe(ports[i], features[i]);

            var seen = new HashSet<Feature>();
            var buffer = new HashSet<Feature>();
            using var done = new CountdownEvent(writers);

            var tasks = Enumerable.Range(0, writers).Select(i => Task.Run(() =>
            {
                for (int n = 0; n < notifiesPerWriter; n++) registry.NotifyPortUpdated(ports[i]);
                done.Signal();
            })).ToArray();

            while (!done.IsSet)
            {
                buffer.Clear();
                if (registry.PopDirtyFeatures(buffer)) seen.UnionWith(buffer);
            }
            Task.WaitAll(tasks);
            buffer.Clear();
            if (registry.PopDirtyFeatures(buffer)) seen.UnionWith(buffer);

            Assert.Equal(writers, seen.Count);
        }
    }
}
