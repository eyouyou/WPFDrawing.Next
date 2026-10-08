using System.Collections.Generic;
using System.Linq;
using Hevo.Charting.Core;
using Hevo.Charting.DevTools;
using Hevo.Charting.LowCode;
using Xunit;
using static Hevo.Charting.Tests.IncrementalTestKit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 真 ChartCell + ReactiveSchema 的整链路:ProjectAll 只重算脏 Feature、按 FeaturePhase 排序,
    /// 端口写入经 SubscriptionRegistry 最终只让对应图层重录。
    /// </summary>
    public sealed class ProjectAllIncrementalTests
    {
        private static ProbeFeature Logged(string name, FeaturePhase phase, List<string> log, DataPort<double>? port = null)
            => new(name, phase, log)
            {
                OnProjectAction = port is null ? null : (_, ctx) => ctx.UsePort(port),
            };

        [Fact]
        public void FullPass_ProjectsFeaturesInPhaseOrder_RegardlessOfAddOrder() => RunOnSta(() =>
        {
            var log = new List<string>();
            var h = new SchemaHarness(
                Logged("interaction", FeaturePhase.Interaction, log),
                Logged("series", FeaturePhase.Series, log),
                Logged("prelayout", FeaturePhase.PreLayout, log),
                Logged("scale", FeaturePhase.Scale, log),
                Logged("layout", FeaturePhase.Layout, log));

            h.InvalidateEnvironment();
            h.ProjectOnly();

            Assert.Equal(new[] { "prelayout", "layout", "scale", "series", "interaction" }, log);
        });

        [Fact]
        public void SameFrame_LaterPhaseReadsValuePublishedByEarlierPhase() => RunOnSta(() =>
        {
            // 同帧"上游写、下游读"靠阶段顺序保证:Scale 阶段发布的 Trait,Interaction 阶段读到的是本帧新值。
            // (生产中的例子:AxisFeature 写轴锚点,CrosshairFeature 读。)
            int frame = 0;
            var seenByInteraction = new List<int>();

            var reader = new ProbeFeature("reader", FeaturePhase.Interaction)
            {
                OnProjectAction = (_, ctx) => seenByInteraction.Add(ctx.Shared().Read<ProbeAnchorTrait>()?.Frame ?? -1),
            };
            var writer = new ProbeFeature("writer", FeaturePhase.Scale)
            {
                OnProjectAction = (_, ctx) => ctx.Shared().PublishData(new ProbeAnchorTrait(frame)),
            };
            // 故意先 Add reader,确认顺序来自 Phase 而不是 Add 顺序
            var h = new SchemaHarness(reader, writer);

            for (frame = 1; frame <= 3; frame++)
            {
                h.InvalidateEnvironment();
                h.Frame();
            }

            Assert.Equal(new[] { 1, 2, 3 }, seenByInteraction);
        });

        [Fact]
        public void FirstProjectAll_AfterCompose_IsNotFullPass_UntilEnvironmentInvalidated() => RunOnSta(() =>
        {
            // 记录现状:StateClock 初值 = default 令牌,刚装配完 _renderedToken 也是 default,
            // 所以首帧全量依赖 SizeChanged → InvalidateEnvironment。
            var log = new List<string>();
            var h = new SchemaHarness(Logged("f", FeaturePhase.Series, log));

            h.ProjectOnly();
            Assert.Empty(log);

            h.InvalidateEnvironment();
            h.ProjectOnly();
            Assert.Equal(new[] { "f" }, log);
        });

        [Fact]
        public void PortWrite_ProjectsOnlySubscribedFeature_IdleFrameProjectsNothing() => RunOnSta(() =>
        {
            var pa = new DataPort<double>("A");
            var pb = new DataPort<double>("B");
            var log = new List<string>();
            var h = new SchemaHarness(
                Logged("a", FeaturePhase.Series, log, pa),
                Logged("b", FeaturePhase.Series, log, pb));

            h.InvalidateEnvironment();
            h.ProjectOnly(); // 首帧 FullPass:两个 Feature 都跑,顺带完成隐式订阅
            log.Clear();

            h.Board.Write(pa, 1.0);
            h.ProjectOnly();
            Assert.Equal(new[] { "a" }, log);

            log.Clear();
            h.ProjectOnly();
            Assert.Empty(log);

            // 同一事务写两个端口:各跑一次,不重复
            using (h.Board.BeginTransaction())
            {
                h.Board.WriteIfChanged(pa, 2.0);
                h.Board.WriteIfChanged(pb, 2.0);
                h.Board.WriteIfChanged(pa, 3.0);
            }
            h.ProjectOnly();
            Assert.Equal(new[] { "a", "b" }, log.OrderBy(x => x));
        });

        [Fact]
        public void PortWrite_EndToEnd_RedrawsOnlyTheLayerFedByThatPort() => RunOnSta(() =>
        {
            var pa = new DataPort<double>("A");
            var pb = new DataPort<double>("B");
            var layerA = new ProbeLayer("A");
            var layerB = new ProbeLayer("B");

            var h = new SchemaHarness(FeedsLayer("a", pa, layerA), FeedsLayer("b", pb, layerB));
            h.InvalidateEnvironment();
            h.Frame();
            Assert.Equal(1, layerA.UpdateCount);
            Assert.Equal(1, layerB.UpdateCount);

            h.Board.Write(pa, 1.0);
            h.Frame();
            Assert.Equal(2, layerA.UpdateCount);
            Assert.Equal(1, layerB.UpdateCount);

            // 写入同值:WriteIfChanged 防抖,Feature 不被标脏,图层也不动
            h.Board.Write(pa, 1.0);
            h.Frame();
            Assert.Equal(2, layerA.UpdateCount);

            // 空闲帧
            h.Frame();
            Assert.Equal(2, layerA.UpdateCount);
            Assert.Equal(1, layerB.UpdateCount);
        });

        // Feature 读端口,值变了才给自己的图层发新 Trait
        internal static ProbeFeature FeedsLayer(string name, DataPort<double> port, ProbeLayer layer) => new(name)
        {
            OnComposeAction = f => f.Attach(layer),
            OnProjectAction = (_, ctx) =>
            {
                var (v, changed) = ctx.UsePort(port);
                if (changed) ctx.For(layer).PublishData(new ProbeTraitA((int)v));
            },
        };
    }

    /// <summary>
    /// 对照组开关本身的测试(测量工具依赖它们的语义)。
    /// 开关是静态的,会影响同进程内并行跑的其它渲染测试,所以这组单独串行。
    /// </summary>
    [CollectionDefinition(nameof(IncrementalProbeCollection), DisableParallelization = true)]
    public sealed class IncrementalProbeCollection { }

    [Collection(nameof(IncrementalProbeCollection))]
    public sealed class IncrementalRenderProbeTests
    {
        [Fact]
        public void ProbeDefaults_AreAllOff()
        {
            Assert.False(IncrementalRenderProbe.ForceFullPass);
            Assert.False(IncrementalRenderProbe.BypassBagShortCircuit);
            Assert.False(IncrementalRenderProbe.ForceLayerRedraw);
        }

        [Fact]
        public void ForceFullPass_ProjectsEveryFeatureEveryFrame() => RunOnSta(() =>
        {
            var pa = new DataPort<double>("A");
            var log = new List<string>();
            var h = new SchemaHarness(
                new ProbeFeature("a", FeaturePhase.Series, log) { OnProjectAction = (_, ctx) => ctx.UsePort(pa) },
                new ProbeFeature("idle", FeaturePhase.Series, log));
            h.InvalidateEnvironment();
            h.ProjectOnly();
            log.Clear();

            try
            {
                IncrementalRenderProbe.ForceFullPass = true;
                h.ProjectOnly(); // 没有任何写入
                Assert.Equal(new[] { "a", "idle" }, log.OrderBy(x => x));
            }
            finally { IncrementalRenderProbe.Reset(); }

            log.Clear();
            h.ProjectOnly();
            Assert.Empty(log);
        });

        [Fact]
        public void ForceLayerRedraw_RedrawsUntouchedLayers_BypassShortCircuitDoesNot() => RunOnSta(() =>
        {
            var pa = new DataPort<double>("A");
            var pb = new DataPort<double>("B");
            var layerA = new ProbeLayer("A");
            var layerB = new ProbeLayer("B");
            var h = new SchemaHarness(
                ProjectAllIncrementalTests.FeedsLayer("a", pa, layerA),
                ProjectAllIncrementalTests.FeedsLayer("b", pb, layerB));
            h.InvalidateEnvironment();
            h.Frame();

            try
            {
                // 去掉 Bag 短路只是多做引用比对,结果不变:B 不重绘
                IncrementalRenderProbe.BypassBagShortCircuit = true;
                h.Board.Write(pa, 1.0);
                h.Frame();
                Assert.Equal(2, layerA.UpdateCount);
                Assert.Equal(1, layerB.UpdateCount);

                // 关掉图层侧判脏:B 也被重录
                IncrementalRenderProbe.ForceLayerRedraw = true;
                h.Board.Write(pa, 2.0);
                h.Frame();
                Assert.Equal(3, layerA.UpdateCount);
                Assert.Equal(2, layerB.UpdateCount);
            }
            finally { IncrementalRenderProbe.Reset(); }
        });
    }
}
