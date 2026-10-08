using System.Collections.Generic;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// Feature 侧"读取即依赖":<see cref="FeatureContext.UsePort{T}"/> 首次读取时隐式订阅,之后按版本令牌判脏。
    /// 直接驱动 FeatureContext(不经过 schema),把 UsePort 的语义钉死:
    /// 只在端口版本变化时返回 IsChanged,订阅只登记一次,FullPass 强制全部视为变脏。
    /// </summary>
    public sealed class UsePortDirtyTrackingTests : System.IDisposable
    {
        private readonly DataPort<double> _a = new("A");
        private readonly DataPort<double> _b = new("B");
        private readonly DataBlackboard _board = new();
        private readonly SubscriptionRegistry _registry = new();
        private readonly ProbeFeature _feature = new("f");
        private readonly FeatureContext _ctx = new();

        public UsePortDirtyTrackingTests()
        {
            _ctx._registry = _registry;
            _ctx._currentFeature = _feature;
        }

        public void Dispose() => _board.Dispose();

        // 模拟一次 Feature.Project:只刷新帧级字段,_portTokens 跨帧保留(跟 Feature.Project 一致)。
        private (double Value, bool IsChanged) Use(DataPort<double> port, bool fullPass = false)
        {
            _ctx._isFullPass = fullPass;
            _ctx.BeginProject(IncrementalTestKit.NewRenderContext(), _board);
            using (_board.AcquireReadLock()) return _ctx.UsePort(port);
        }

        private bool PopDirty()
        {
            var set = new HashSet<Feature>();
            return _registry.PopDirtyFeatures(set) && set.Contains(_feature);
        }

        [Fact]
        public void FirstRead_IsChanged_AndSubscribesFeature()
        {
            _board.Write(_a, 1.0);

            var (value, changed) = Use(_a);

            Assert.Equal(1.0, value);
            Assert.True(changed);
            Assert.True(_registry.NotifyPortUpdated(_a)); // 已登记为 _a 的订阅者
            Assert.True(PopDirty());
        }

        [Fact]
        public void RepeatedRead_WithoutWrite_IsNotChanged()
        {
            _board.Write(_a, 1.0);
            Use(_a);

            Assert.False(Use(_a).IsChanged);
            Assert.False(Use(_a).IsChanged);
        }

        [Fact]
        public void WriteSameValue_IsNotChanged_NewValue_IsChangedExactlyOnce()
        {
            _board.Write(_a, 1.0);
            Use(_a);

            _board.Write(_a, 1.0); // WriteIfChanged 值防抖,令牌不动
            Assert.False(Use(_a).IsChanged);

            _board.Write(_a, 2.0);
            var (value, changed) = Use(_a);
            Assert.Equal(2.0, value);
            Assert.True(changed);
            Assert.False(Use(_a).IsChanged);
        }

        [Fact]
        public void ForceWriteSameValue_IsChanged()
        {
            _board.Write(_a, 1.0);
            Use(_a);

            using (_board.BeginTransaction()) _board.ForceWrite(_a, 1.0);

            Assert.True(Use(_a).IsChanged);
        }

        [Fact]
        public void UnrelatedPortWrite_DoesNotAffectIsChanged()
        {
            _board.Write(_a, 1.0);
            Use(_a);

            _board.Write(_b, 5.0);

            Assert.False(Use(_a).IsChanged);
        }

        [Fact]
        public void FullPass_ForcesIsChanged_WithoutVersionChange()
        {
            _board.Write(_a, 1.0);
            Use(_a);

            Assert.True(Use(_a, fullPass: true).IsChanged);
            Assert.False(Use(_a).IsChanged); // FullPass 不污染令牌,下一帧恢复增量
        }

        [Fact]
        public void PortNeverWritten_FirstReadIsChanged_ThenStable()
        {
            var (value, changed) = Use(_a);
            Assert.Equal(default, value);
            Assert.True(changed);
            Assert.False(Use(_a).IsChanged);

            // 首次写入后照常判脏
            _board.Write(_a, 3.0);
            Assert.True(Use(_a).IsChanged);
        }

        [Fact]
        public void Subscription_IsRegisteredOnlyOnFirstSight_ResetReEnablesIt()
        {
            _board.Write(_a, 1.0);
            Use(_a);

            // 订阅被外部摘除(Transact 移除 Feature 时 Registry.UnsubscribeAll)后,
            // 只要 _portTokens 还认识这个端口,UsePort 不会重新订阅 —— 订阅只在"首次见到端口"时登记。
            _registry.UnsubscribeAll(_feature);
            Use(_a);
            Assert.False(_registry.NotifyPortUpdated(_a));

            // Decompose 走 FeatureContext.Reset 清空令牌,重装后首帧重新订阅
            _ctx.Reset();
            _ctx._registry = _registry;
            _ctx._currentFeature = _feature;
            Assert.True(Use(_a).IsChanged);
            Assert.True(_registry.NotifyPortUpdated(_a));
        }

        [Fact]
        public void KnownBoundary_ConditionalUsePort_MissesSubscriptionUntilBranchRuns()
        {
            // ⚠ 已知边界(不是期望行为,是对现状的记录):
            // 订阅在"首次读取"时登记。UsePort 放进条件分支、且分支还没走到过时,该端口的写入唤不醒 Feature。
            // 这就是 HEVO003 要求 UsePort 顶层无条件调用的真正原因(跟调用顺序无关)。
            // 图层侧 VisualDependencyTracker 每帧重建依赖,没有这个边界,见 LayerDependencyTrackingTests。
            // 如果将来改成"每帧重建订阅",这个测试应当翻转并改名。
            bool readB = false;
            void ProjectFrame()
            {
                _ctx._isFullPass = false;
                _ctx.BeginProject(IncrementalTestKit.NewRenderContext(), _board);
                using (_board.AcquireReadLock())
                {
                    _ctx.UsePort(_a);
                    if (readB) _ctx.UsePort(_b);
                }
            }

            _board.OnPortUpdated += p => _registry.NotifyPortUpdated(p);
            ProjectFrame();
            PopDirty();

            // B 变了,但 Feature 从没读过 B → 没订阅 → 不会被标脏(漏重绘)
            _board.Write(_b, 1.0);
            Assert.False(PopDirty());

            // 直到 A 变化让 Feature 重跑、并且走进了读 B 的分支,B 才被订阅
            _board.Write(_a, 1.0);
            Assert.True(PopDirty());
            readB = true;
            ProjectFrame();

            _board.Write(_b, 2.0);
            Assert.True(PopDirty());
        }
    }
}
