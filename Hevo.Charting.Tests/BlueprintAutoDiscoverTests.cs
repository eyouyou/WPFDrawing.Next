using Hevo.Charting.Core;
using Hevo.Charting.LowCode.Designer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// AutoDiscover + [BlueprintHandler] 回归。
    /// 关键不变式:
    /// 1. 标了 attribute 的方法被自动注册,委托类型按签名推断 (Action / Func)
    /// 2. 注册名缺省 = 方法名;attribute Name 参数可覆盖
    /// 3. private 方法也能扫到 (业务 handler 模块从 Schema 内迁过来时常常是 private)
    /// 4. 没标的方法不动
    /// 5. 实例 + 静态两条路径都通
    /// </summary>
    public sealed class BlueprintAutoDiscoverTests
    {
        public sealed class TestModule
        {
            public int CallCount { get; private set; }

            // 默认用方法名 "OnTick" 注册
            [BlueprintHandler]
            public Task<bool> OnTick(VersionToken tick, CancellationToken ct)
            {
                CallCount++;
                return Task.FromResult(true);
            }

            // 显式重命名 → "paging"
            [BlueprintHandler("paging")]
            private Task<bool> OnRequirePagingAsync(int anchor, int count) => Task.FromResult(true);

            [BlueprintHandler("formatter")]
            public string FormatLabel(int idx) => $"idx={idx}";

            [BlueprintHandler("voider")]
            public void DoSideEffect(string s) { /* return void → Action<string> */ }

            // 没标 attribute,不应被 AutoDiscover 扫到
            public Task<bool> NotAHandler() => Task.FromResult(false);
        }

        public static class StaticTestModule
        {
            [BlueprintHandler("static_fmt")]
            public static string Format(int a, int b) => $"{a}+{b}";
        }

        [Fact]
        public void AutoDiscover_RegistersAllAttributedMethods()
        {
            var module = new TestModule();
            var registry = new BlueprintHandlerRegistry().AutoDiscover(module);

            Assert.True(registry.Contains(nameof(TestModule.OnTick)));
            Assert.True(registry.Contains("paging"));
            Assert.True(registry.Contains("formatter"));
            Assert.True(registry.Contains("voider"));
        }

        [Fact]
        public void AutoDiscover_SkipsMethodsWithoutAttribute()
        {
            var module = new TestModule();
            var registry = new BlueprintHandlerRegistry().AutoDiscover(module);
            Assert.False(registry.Contains(nameof(TestModule.NotAHandler)));
        }

        [Fact]
        public void AutoDiscover_RegistersFetchCompatibleDelegate()
        {
            // OnTick 签名 (VersionToken, CancellationToken) → Task<bool>,
            // 跟 BlueprintHandlerRegistry.TryGetFetch 期望的类型一致。
            var module = new TestModule();
            var registry = new BlueprintHandlerRegistry().AutoDiscover(module);

            var fetch = registry.TryGetFetch(nameof(TestModule.OnTick));
            Assert.NotNull(fetch);
        }

        [Fact]
        public async Task AutoDiscover_DelegateInvokesOriginalMethod()
        {
            var module = new TestModule();
            var registry = new BlueprintHandlerRegistry().AutoDiscover(module);

            var fetch = registry.TryGetFetch(nameof(TestModule.OnTick));
            await fetch!(default, CancellationToken.None);
            Assert.Equal(1, module.CallCount);
        }

        [Fact]
        public void AutoDiscover_PreservesDelegateType()
        {
            var module = new TestModule();
            var registry = new BlueprintHandlerRegistry().AutoDiscover(module);

            // FormatLabel 应是 Func<int, string>
            var fmt = registry.TryGet("formatter") as Func<int, string>;
            Assert.NotNull(fmt);
            Assert.Equal("idx=42", fmt!(42));

            // DoSideEffect 应是 Action<string>
            var voider = registry.TryGet("voider") as Action<string>;
            Assert.NotNull(voider);
        }

        [Fact]
        public void AutoDiscoverStatic_RegistersStaticMethods()
        {
            var registry = new BlueprintHandlerRegistry().AutoDiscoverStatic(typeof(StaticTestModule));
            var fmt = registry.TryGet("static_fmt") as Func<int, int, string>;
            Assert.NotNull(fmt);
            Assert.Equal("3+5", fmt!(3, 5));
        }

        [Fact]
        public void AutoDiscover_FluentChain_AllowsComposingMultipleModules()
        {
            // 多业务 handler 模块组合 (K线 + 指标共享一个 registry)
            var ds = new TestModule();
            var staticReg = new BlueprintHandlerRegistry()
                .AutoDiscover(ds)
                .AutoDiscoverStatic(typeof(StaticTestModule));

            Assert.True(staticReg.Contains(nameof(TestModule.OnTick)));
            Assert.True(staticReg.Contains("static_fmt"));
        }
    }
}
