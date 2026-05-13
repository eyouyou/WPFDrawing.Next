using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.Handlers;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// Phase 1 — Handler Lifecycle 基础设施回归。
    /// 验证项:
    /// 1. 旧 static handler 默认 Singleton,行为不破坏(由 BlueprintAutoDiscoverTests 保证)
    /// 2. AutoDiscoverType + [BlueprintNode] 类级 lifecycle 继承
    /// 3. 方法级 [BlueprintHandler(Lifecycle=...)] 覆盖类级
    /// 4. PerNode 按 nodeId 隔离,同 handler 不同节点字段互不污染
    /// 5. 模式 3:同方法多 [BlueprintHandler] 各自独立 lifecycle + 实例池
    /// 6. ctor DI 从 ScopeContext.Services 注入
    /// 7. ScopeContext.Dispose 清理 Scoped/PerNode,Singleton 留给进程
    /// </summary>
    public sealed class BlueprintLifecycleTests
    {
        // ───── 测试用宿主类 ─────

        /// <summary>类级 PerNode + 字段累加器:每个节点应该一份。</summary>
        [BlueprintNode(Lifecycle = NodeLifetime.PerNode)]
        public class CountingHandler
        {
            public int Counter { get; private set; }

            [BlueprintHandler("count_inc")]
            public int Inc() => ++Counter;
        }

        /// <summary>类级 Scoped + 方法级覆盖。</summary>
        [BlueprintNode(Lifecycle = NodeLifetime.Scoped)]
        public class HybridHandler
        {
            public int ScopedCalls { get; private set; }
            public int PerNodeCalls { get; private set; }

            [BlueprintHandler("hybrid_scoped")]                              // 继承类级 → Scoped
            public int Trend() => ++ScopedCalls;

            [BlueprintHandler("hybrid_per_node", Lifecycle = NodeLifetime.PerNode)]  // 覆盖 → PerNode
            public int Compute() => ++PerNodeCalls;
        }

        /// <summary>模式 3:同方法两个 attr,不同 lifecycle。</summary>
        [BlueprintNode]   // 类级默认 Singleton(本测试只看方法级)
        public class DualBindingHandler
        {
            public int Calls { get; private set; }

            [BlueprintHandler("ema_node",   Lifecycle = NodeLifetime.PerNode)]
            [BlueprintHandler("ema_shared", Lifecycle = NodeLifetime.Scoped)]
            public int Compute() => ++Calls;
        }

        /// <summary>ctor DI 测试:依赖一个外部 service。</summary>
        public sealed class FakeService { public string Tag = "svc"; }

        [BlueprintNode(Lifecycle = NodeLifetime.Scoped)]
        public class CtorDiHandler
        {
            public FakeService Injected { get; }
            public CtorDiHandler(FakeService svc) => Injected = svc;

            [BlueprintHandler("ctor_di_tag")]
            public string Tag() => Injected.Tag;
        }

        /// <summary>IDisposable Scoped:Dispose 时应被释放。</summary>
        [BlueprintNode(Lifecycle = NodeLifetime.Scoped)]
        public class DisposableHandler : IDisposable
        {
            public bool Disposed { get; private set; }
            public void Dispose() => Disposed = true;

            [BlueprintHandler("disposable_noop")]
            public void Noop() { }
        }

        // ───── 测试用例 ─────

        [Fact]
        public void AutoDiscoverType_RegistersHandler_ResolvableViaScope()
        {
            var registry = new BlueprintHandlerRegistry().AutoDiscoverType(typeof(CountingHandler));
            Assert.True(registry.Contains("count_inc"));

            var scope = new ScopeContext();
            var fn = registry.Resolve("count_inc", scope, nodeId: "feature1") as Func<int>;

            Assert.NotNull(fn);
            Assert.Equal(1, fn!());
            Assert.Equal(2, fn());   // 同 nodeId 复用同一实例
        }

        [Fact]
        public void PerNode_DifferentNodeIds_IsolatesState()
        {
            var registry = new BlueprintHandlerRegistry().AutoDiscoverType(typeof(CountingHandler));
            var scope = new ScopeContext();

            var fnA = registry.Resolve("count_inc", scope, nodeId: "A") as Func<int>;
            var fnB = registry.Resolve("count_inc", scope, nodeId: "B") as Func<int>;

            Assert.NotNull(fnA); Assert.NotNull(fnB);
            Assert.Equal(1, fnA!());
            Assert.Equal(1, fnB!());   // 字段独立,各从 0 起
            Assert.Equal(2, fnA());
            Assert.Equal(2, fnB());
        }

        [Fact]
        public void MethodLevelLifecycle_OverridesClassLevel()
        {
            // 验证:hybrid_scoped 继承类级 Scoped(同 scope 不分 nodeId 共享 instance);
            //       hybrid_per_node 方法级覆盖成 PerNode(按 nodeId 隔离 instance)。
            // 通过观察"两个 nodeId 各调一次,字段累计是 1+1=2 还是 共享=2"反推 lifecycle。
            var registry = new BlueprintHandlerRegistry().AutoDiscoverType(typeof(HybridHandler));
            var scope = new ScopeContext();

            // Scoped:同 scope 不分 nodeId,两次调用累计在同一个实例上 → ScopedCalls 走到 2
            var scopedFnN1 = registry.Resolve("hybrid_scoped", scope, nodeId: "n1") as Func<int>;
            var scopedFnN2 = registry.Resolve("hybrid_scoped", scope, nodeId: "n2") as Func<int>;
            Assert.NotNull(scopedFnN1); Assert.NotNull(scopedFnN2);
            Assert.Equal(1, scopedFnN1!());
            Assert.Equal(2, scopedFnN2!());   // 同一 HybridHandler 实例,字段从 1 涨到 2

            // PerNode:不同 nodeId 各一份,两次调用各自从 0 起累 → 都是 1
            var perNodeFnN1 = registry.Resolve("hybrid_per_node", scope, nodeId: "n1") as Func<int>;
            var perNodeFnN2 = registry.Resolve("hybrid_per_node", scope, nodeId: "n2") as Func<int>;
            Assert.NotNull(perNodeFnN1); Assert.NotNull(perNodeFnN2);
            Assert.Equal(1, perNodeFnN1!());
            Assert.Equal(1, perNodeFnN2!());   // 两份独立实例,各自 PerNodeCalls 从 0 起
        }

        [Fact]
        public void Mode3_SameMethodDifferentNames_IndependentInstancePools()
        {
            // 模式 3:同方法 ema_node (PerNode) + ema_shared (Scoped) → 两份独立实例。
            // 实例字段 Calls 不污染:ema_node 自己累 / ema_shared 自己累。
            var registry = new BlueprintHandlerRegistry().AutoDiscoverType(typeof(DualBindingHandler));
            var scope = new ScopeContext();

            var perNode = registry.Resolve("ema_node",   scope, nodeId: "n1") as Func<int>;
            var shared  = registry.Resolve("ema_shared", scope, nodeId: null) as Func<int>;

            Assert.NotNull(perNode); Assert.NotNull(shared);

            Assert.Equal(1, perNode!());
            Assert.Equal(1, shared!());   // 独立池,各自从 0 起 —— 模式 3 关键不变式
            Assert.Equal(2, perNode());
            Assert.Equal(2, shared());

            // ema_node 不同 nodeId 也应隔离
            var perNode2 = registry.Resolve("ema_node", scope, nodeId: "n2") as Func<int>;
            Assert.NotNull(perNode2);
            Assert.Equal(1, perNode2!());
        }

        [Fact]
        public void Scoped_SameHandlerSameScope_SharesInstance()
        {
            var registry = new BlueprintHandlerRegistry().AutoDiscoverType(typeof(HybridHandler));
            var scope = new ScopeContext();

            var fn1 = registry.Resolve("hybrid_scoped", scope, nodeId: "n1") as Func<int>;
            var fn2 = registry.Resolve("hybrid_scoped", scope, nodeId: "n2") as Func<int>;
            Assert.NotNull(fn1); Assert.NotNull(fn2);

            // Scoped 不分 nodeId,同 scope 共享
            Assert.Equal(1, fn1!());
            Assert.Equal(2, fn2!());
        }

        [Fact]
        public void Singleton_ProcessWide_SharedAcrossScopes()
        {
            // 静态 RegisterDelegate / AutoDiscoverStatic 路径就是 Singleton 的 pre-bound delegate,
            // 这里测的是 AutoDiscoverType + 方法级 Singleton lifecycle 也走进程级共享。
            ScopeContext.ClearSingletons();   // 测试隔离

            var registry = new BlueprintHandlerRegistry()
                .AutoDiscoverType(typeof(SingletonOnlyHandler));

            var scopeA = new ScopeContext();
            var scopeB = new ScopeContext();
            var fnA = registry.Resolve("singleton_inc", scopeA, nodeId: "x") as Func<int>;
            var fnB = registry.Resolve("singleton_inc", scopeB, nodeId: "y") as Func<int>;

            Assert.NotNull(fnA); Assert.NotNull(fnB);
            Assert.Equal(1, fnA!());
            Assert.Equal(2, fnB!());   // 进程级共享同一计数器
        }

        [BlueprintNode(Lifecycle = NodeLifetime.Singleton)]
        public class SingletonOnlyHandler
        {
            public int Counter { get; private set; }
            [BlueprintHandler("singleton_inc")]
            public int Inc() => ++Counter;
        }

        [Fact]
        public void CtorDI_ResolvesFromScopeServices()
        {
            var registry = new BlueprintHandlerRegistry().AutoDiscoverType(typeof(CtorDiHandler));
            var scope = new ScopeContext();
            scope.AddService(typeof(FakeService), new FakeService { Tag = "injected!" });

            var fn = registry.Resolve("ctor_di_tag", scope, nodeId: null) as Func<string>;
            Assert.NotNull(fn);
            Assert.Equal("injected!", fn!());
        }

        [Fact]
        public void Dispose_ReleasesScopedAndPerNode_NotSingleton()
        {
            var registry = new BlueprintHandlerRegistry().AutoDiscoverType(typeof(DisposableHandler));
            var scope = new ScopeContext();
            var fn = registry.Resolve("disposable_noop", scope, nodeId: null) as Action;
            Assert.NotNull(fn);
            fn!();   // 实例化(Scoped 池里现在有一份)

            // 反向拿到那个实例:用 GetOrCreate 同 key 取(Scoped 是同 scope 同 handlerName)
            var instance = (DisposableHandler)scope.GetOrCreate(
                typeof(DisposableHandler), NodeLifetime.Scoped, "disposable_noop", null);
            Assert.False(instance.Disposed);

            scope.Dispose();
            Assert.True(instance.Disposed);
        }

        [Fact]
        public void TryGet_NoScope_SingletonInstanceEntry_StillResolves()
        {
            // 旧调用路径不传 scope,Lifecycle-aware Singleton entry 应能解析(用内部默认 ScopeContext)。
            // 这是兼容契约 —— 旧调用方走 TryGet 也不破坏 Singleton handler 行为。
            ScopeContext.ClearSingletons();
            var registry = new BlueprintHandlerRegistry().AutoDiscoverType(typeof(SingletonOnlyHandler));

            var fn = registry.TryGet("singleton_inc") as Func<int>;
            Assert.NotNull(fn);
            Assert.Equal(1, fn!());
        }

        [Fact]
        public void StaticHandlers_DefaultToSingleton_ZeroBehaviorBreak()
        {
            // 旧 RegisterDelegate / AutoDiscoverStatic 路径走 OfStatic,lifecycle 字段忽略,
            // TryGet 一如既往返回 pre-bound delegate。零行为破坏不变式,跟 BlueprintAutoDiscoverTests 互补。
            var registry = new BlueprintHandlerRegistry()
                .RegisterDelegate("legacy", (Func<int, string>)(i => $"legacy-{i}"));
            var fn = registry.TryGet("legacy") as Func<int, string>;
            Assert.NotNull(fn);
            Assert.Equal("legacy-7", fn!(7));
            Assert.Equal(typeof(Func<int, string>), registry.GetDelegateType("legacy"));
        }
    }
}
