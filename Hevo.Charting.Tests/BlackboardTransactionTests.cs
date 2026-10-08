using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// DataBlackboard 事务语义:事务内多次写入合并成一次通知、通知在释放写锁之前完成、
    /// WriteIfChanged 的值防抖、版本令牌只在真实写入时推进。
    /// 这些是"读取即依赖"增量链路最上游的保证 —— 通知次数直接决定下游 Feature 被标脏几次。
    /// </summary>
    public sealed class BlackboardTransactionTests
    {
        private readonly DataPort<double> _a = new("A");
        private readonly DataPort<double> _b = new("B");
        private readonly DataPort<double> _c = new("C");

        [Fact]
        public void Transaction_MultipleWritesToSamePort_NotifyOncePerPort_AndCommitOnce()
        {
            using var board = new DataBlackboard();
            var portHits = new Dictionary<object, int>();
            int commits = 0, subscriberHits = 0;
            board.OnPortUpdated += p => portHits[p] = portHits.GetValueOrDefault(p) + 1;
            board.OnTransactionCommitted += _ => commits++;
            board.Subscribe(_a, _ => subscriberHits++);

            using (board.BeginTransaction())
            {
                board.WriteIfChanged(_a, 1);
                board.WriteIfChanged(_a, 2);
                board.WriteIfChanged(_a, 3);
                board.WriteIfChanged(_b, 1);

                // 事务内:一条通知都不发
                Assert.Empty(portHits);
                Assert.Equal(0, commits);
            }

            Assert.Equal(1, portHits[_a]);
            Assert.Equal(1, portHits[_b]);
            Assert.Equal(2, portHits.Count);
            Assert.Equal(1, subscriberHits);
            Assert.Equal(1, commits);
            using (board.AcquireReadLock()) Assert.Equal(3, board.Read(_a));
        }

        [Fact]
        public void NestedTransaction_NotifiesOnlyWhenOutermostEnds()
        {
            using var board = new DataBlackboard();
            int portHits = 0, commits = 0;
            board.OnPortUpdated += _ => portHits++;
            board.OnTransactionCommitted += _ => commits++;

            using (board.BeginTransaction())
            {
                using (board.BeginTransaction())
                {
                    board.WriteIfChanged(_a, 1);
                }
                Assert.Equal(0, portHits);
                Assert.Equal(0, commits);
                board.WriteIfChanged(_a, 2);
            }

            Assert.Equal(1, portHits);
            Assert.Equal(1, commits);
        }

        [Fact]
        public void WriteIfChanged_SameValue_DoesNotBumpVersionOrNotify()
        {
            using var board = new DataBlackboard();
            board.Write(_a, 1.0);
            var v1 = board.GetVersion(_a);

            int portHits = 0;
            board.OnPortUpdated += _ => portHits++;
            board.Write(_a, 1.0);

            Assert.Equal(v1, board.GetVersion(_a));
            Assert.Equal(0, portHits);
        }

        [Fact]
        public void EffectiveWrite_BumpsVersion_ForceWriteBumpsEvenWithSameValue()
        {
            using var board = new DataBlackboard();
            Assert.Equal(default, board.GetVersion(_a)); // 从未写过 = default 令牌

            board.Write(_a, 1.0);
            var v1 = board.GetVersion(_a);
            Assert.NotEqual(default, v1);

            board.Write(_a, 2.0);
            var v2 = board.GetVersion(_a);
            Assert.NotEqual(v1, v2);

            // ForceWrite 不做值比较:同值也推进版本 → 下游 UsePort 会看到 IsChanged=true
            using (board.BeginTransaction()) board.ForceWrite(_a, 2.0);
            Assert.NotEqual(v2, board.GetVersion(_a));
        }

        [Fact]
        public void WriteOutsideTransaction_UnderWriteLock_NotifiesImmediately()
        {
            using var board = new DataBlackboard();
            int portHits = 0, commits = 0;
            board.OnPortUpdated += _ => portHits++;
            board.OnTransactionCommitted += _ => commits++;

            using (board.AcquireWriteLock())
            {
                board.WriteIfChanged(_a, 1);
                Assert.Equal(1, portHits);
                board.WriteIfChanged(_a, 2);
                Assert.Equal(2, portHits);
            }

            // 非事务写:每次有效写入各自 commit 一次,不合并
            Assert.Equal(2, commits);
        }

        [Fact]
        public void Notification_RunsBeforeWriteLockIsReleased()
        {
            // "读取与订阅之间被写入插队"不存在的前提:通知在写锁内完成,渲染侧拿不到读锁。
            using var board = new DataBlackboard();
            Task? reader = null;
            bool readerEnteredDuringNotify = true;

            board.OnTransactionCommitted += b =>
            {
                reader = Task.Run(() => { using (b.AcquireReadLock()) { } });
                // 读线程在写锁释放前必然阻塞;等 200ms 仍未完成即证明锁还被持有
                readerEnteredDuringNotify = reader.Wait(200);
            };

            using (board.BeginTransaction()) board.WriteIfChanged(_a, 1);

            Assert.False(readerEnteredDuringNotify);
            Assert.True(reader!.Wait(TimeSpan.FromSeconds(5)), "写锁释放后读线程应能进入");
        }

        [Fact]
        public void TransactionCommit_FeedsRegistry_FeatureDirtiedOnceForTwoPorts()
        {
            // 复刻 ReactiveSchema.Blackboard_OnPortUpdated 的接线:board 端口通知 → registry 标脏。
            using var board = new DataBlackboard();
            var registry = new SubscriptionRegistry();
            var feature = new ProbeFeature("f");
            registry.Subscribe(_a, feature);
            registry.Subscribe(_b, feature);
            board.OnPortUpdated += p => registry.NotifyPortUpdated(p);

            using (board.BeginTransaction())
            {
                board.WriteIfChanged(_a, 1);
                board.WriteIfChanged(_b, 1);
                board.WriteIfChanged(_a, 2);
            }

            var dirty = new HashSet<Feature>();
            Assert.True(registry.PopDirtyFeatures(dirty));
            Assert.Single(dirty);
            Assert.Contains(feature, dirty);
        }

        [Fact]
        public void WriteFromNotificationCallback_IsDeliveredWithoutCorruptingSnapshot()
        {
            // EndTransaction 先快照脏端口再通知:回调里再写别的端口不会撞上"集合被修改"。
            using var board = new DataBlackboard();
            var notified = new List<object>();
            board.OnPortUpdated += p =>
            {
                notified.Add(p);
                if (ReferenceEquals(p, _a)) board.WriteIfChanged(_c, 42);
            };

            using (board.BeginTransaction())
            {
                board.WriteIfChanged(_a, 1);
                board.WriteIfChanged(_b, 1);
            }

            Assert.Contains(_a, notified);
            Assert.Contains(_b, notified);
            Assert.Contains(_c, notified);
            using (board.AcquireReadLock()) Assert.Equal(42, board.Read(_c));
        }

#if DEBUG
        [Fact]
        public void Debug_ReadWithoutLock_Throws()
        {
            using var board = new DataBlackboard();
            Assert.Throws<InvalidOperationException>(() => board.Read(_a));
        }
#endif
    }
}
