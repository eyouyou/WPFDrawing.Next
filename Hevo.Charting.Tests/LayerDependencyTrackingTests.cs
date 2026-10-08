using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Xunit;
using static Hevo.Charting.Tests.IncrementalTestKit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 图层侧"读取即依赖":ChartLayer.Update 里经 TrackerDataProxy 读到的 Trait 引用被 VisualDependencyTracker 记下,
    /// 下一帧 RenderContext.SubmitSync 只比引用(ReferenceEquals)决定是否重录。
    /// 依赖集合每帧按实际读取重建,条件读取天然正确。
    /// </summary>
    public sealed class LayerDependencyTrackingTests
    {
        [Fact]
        public void SleepingLayer_WakesOnlyOnLocalData() => RunOnSta(() =>
        {
            var layer = new ProbeLayer();
            var driver = new LayerFrameDriver(layer);

            // 只有全局数据:未被发现的图层保持深度睡眠
            driver.Frame(ctx => ctx.Shared().PublishData(new ProbeTraitB(1)));
            Assert.Equal(0, layer.UpdateCount);

            // 专属局部数据到达 → 唤醒并首次采样
            driver.Frame(ctx => ctx.For(layer).PublishData(new ProbeTraitA(1)));
            Assert.Equal(1, layer.UpdateCount);
            Assert.True(layer.IsDiscovered);
        });

        [Fact]
        public void SameReference_Republished_DoesNotRedraw() => RunOnSta(() =>
        {
            var layer = new ProbeLayer();
            var driver = new LayerFrameDriver(layer);
            var trait = new ProbeTraitA(1);

            driver.Frame(ctx => ctx.For(layer).PublishData(trait));
            Assert.Equal(1, layer.UpdateCount);

            // 有局部草稿(不走 Bag 短路),但引用没变 → 引用指纹比对判定不脏
            driver.Frame(ctx => ctx.For(layer).PublishData(trait));
            driver.Frame(ctx => ctx.For(layer).PublishData(trait));
            Assert.Equal(1, layer.UpdateCount);
        });

        [Fact]
        public void NewReference_Redraws_EvenWithEqualValue() => RunOnSta(() =>
        {
            // 判脏只看引用,不看值:Trait 是不可变 record,"换对象 = 变了"。
            // 值相等的新对象也会重绘 —— 防抖责任在 Feature 侧(UsePort 的 IsChanged)。
            var layer = new ProbeLayer();
            var driver = new LayerFrameDriver(layer);

            driver.Frame(ctx => ctx.For(layer).PublishData(new ProbeTraitA(1)));
            driver.Frame(ctx => ctx.For(layer).PublishData(new ProbeTraitA(1)));
            Assert.Equal(2, layer.UpdateCount);

            driver.Frame(ctx => ctx.For(layer).PublishData(new ProbeTraitA(2)));
            Assert.Equal(3, layer.UpdateCount);
        });

        [Fact]
        public void IdleFrame_NoDrafts_RedrawsNothing() => RunOnSta(() =>
        {
            var layer = new ProbeLayer();
            var driver = new LayerFrameDriver(layer);
            driver.Frame(ctx => ctx.For(layer).PublishData(new ProbeTraitA(1)));

            driver.Frame(_ => { });
            driver.Frame(_ => { });

            Assert.Equal(1, layer.UpdateCount);
        });

        [Fact]
        public void GlobalTraitChange_RedrawsOnlyLayersThatReadIt() => RunOnSta(() =>
        {
            var readsGlobal = new ProbeLayer("global") { Reads = static d => { d.Get<ProbeTraitA>(); d.Get<ProbeTraitB>(); } };
            var localOnly = new ProbeLayer("local") { Reads = static d => d.Get<ProbeTraitA>() };
            var driver = new LayerFrameDriver(readsGlobal, localOnly);

            driver.Frame(ctx =>
            {
                ctx.Shared().PublishData(new ProbeTraitB(1));
                ctx.For(readsGlobal).PublishData(new ProbeTraitA(1));
                ctx.For(localOnly).PublishData(new ProbeTraitA(1));
            });
            Assert.Equal(1, readsGlobal.UpdateCount);
            Assert.Equal(1, localOnly.UpdateCount);

            // 全局 Trait 换对象:两个图层都过引用比对,只有读过它的那个重绘
            driver.Frame(ctx => ctx.Shared().PublishData(new ProbeTraitB(2)));
            Assert.Equal(2, readsGlobal.UpdateCount);
            Assert.Equal(1, localOnly.UpdateCount);
        });

        [Fact]
        public void LocalTraitShadowsGlobal_GlobalChangeIgnored() => RunOnSta(() =>
        {
            // 级联读取:局部 > 全局。局部已有同类型 Trait 时,全局那份变了也不影响这个图层。
            var layer = new ProbeLayer { Reads = static d => d.Get<ProbeTraitB>() };
            var driver = new LayerFrameDriver(layer);

            driver.Frame(ctx =>
            {
                ctx.Shared().PublishData(new ProbeTraitB(1));
                ctx.For(layer).PublishData(new ProbeTraitB(100));
            });
            Assert.Equal(1, layer.UpdateCount);

            driver.Frame(ctx => ctx.Shared().PublishData(new ProbeTraitB(2)));
            Assert.Equal(1, layer.UpdateCount);
        });

        [Fact]
        public void ConditionalRead_DependenciesFollowTheBranchTakenLastFrame() => RunOnSta(() =>
        {
            // 跟 Feature 侧 UsePort 的已知边界对照:图层侧每帧清空 TrackedRefs、按实际读取重建,
            // 分支切换后依赖立即跟着切换,不会漏也不会多。
            var layer = new ProbeLayer
            {
                Reads = static d =>
                {
                    if (d.Get<ProbeSwitchTrait>()?.ReadB == true) d.Get<ProbeTraitB>();
                    else d.Get<ProbeTraitA>();
                }
            };
            var driver = new LayerFrameDriver(layer);

            driver.Frame(ctx =>
            {
                ctx.For(layer).PublishData(new ProbeSwitchTrait(false));
                ctx.For(layer).PublishData(new ProbeTraitA(1));
                ctx.For(layer).PublishData(new ProbeTraitB(1));
            });
            Assert.Equal(1, layer.UpdateCount);

            // 走的是 A 分支:B 变化不重绘
            driver.Frame(ctx => ctx.For(layer).PublishData(new ProbeTraitB(2)));
            Assert.Equal(1, layer.UpdateCount);

            // 切到 B 分支
            driver.Frame(ctx => ctx.For(layer).PublishData(new ProbeSwitchTrait(true)));
            Assert.Equal(2, layer.UpdateCount);

            // 现在 A 变化不重绘,B 变化重绘
            driver.Frame(ctx => ctx.For(layer).PublishData(new ProbeTraitA(2)));
            Assert.Equal(2, layer.UpdateCount);
            driver.Frame(ctx => ctx.For(layer).PublishData(new ProbeTraitB(3)));
            Assert.Equal(3, layer.UpdateCount);
        });

        [Fact]
        public void TraitReadAsNull_IsTracked_AppearanceTriggersRedraw() => RunOnSta(() =>
        {
            // 读到 null 也是一次依赖:之后这个 Trait 第一次出现,引用从 null 变为对象 → 重绘。
            var layer = new ProbeLayer { Reads = static d => { d.Get<ProbeTraitA>(); d.Get<ProbeTraitB>(); } };
            var driver = new LayerFrameDriver(layer);

            driver.Frame(ctx => ctx.For(layer).PublishData(new ProbeTraitA(1)));
            Assert.Equal(1, layer.UpdateCount);

            driver.Frame(ctx => ctx.Shared().PublishData(new ProbeTraitB(1)));
            Assert.Equal(2, layer.UpdateCount);
        });
    }
}
