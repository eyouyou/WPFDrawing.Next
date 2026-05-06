using Hevo.Charting.Core;
using Hevo.Charting.LowCode.Designer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// HandlerKey&lt;T&gt; + Register/Get 强类型路径回归 (协议增强 §K-2)。
    /// 关键不变式:
    /// 1. Register&lt;T&gt; 编译期强制签名匹配 (单测里只能验证 happy path,签名错配是 CS1503 编译错,无法 runtime 测)
    /// 2. Get&lt;T&gt; 返回强类型委托,不需要 cast
    /// 3. 跟 AutoDiscover 共存 — 同 registry 两条路径互不干扰
    /// 4. WellKnownHandlers 的几个常用类别能拿到正确类型
    /// </summary>
    public sealed class HandlerKeyStrongTypedTests
    {
        public sealed class Module
        {
            public int FetchCalls { get; private set; }
            public int FormatterCalls { get; private set; }

            public Task<bool> OnFetch(VersionToken tick, CancellationToken ct)
            {
                FetchCalls++;
                return Task.FromResult(true);
            }

            public string FormatLabel(int idx)
            {
                FormatterCalls++;
                return $"#{idx}";
            }
        }

        [Fact]
        public void Register_StrongTyped_RoundTripsWithGet()
        {
            var m = new Module();
            // 💥 注意:method group 每次写都是新包装的 Func 实例(.NET 行为),所以必须 cache 一份,
            //    用 Assert.Same 才有意义。两次裸 m.OnFetch 是 Equals 但不 Same。
            Func<VersionToken, CancellationToken, Task<bool>> handler = m.OnFetch;
            var key = WellKnownHandlers.Trigger.Fetch("on_fetch");

            var registry = new BlueprintHandlerRegistry().Register(key, handler);
            var fetched = registry.Get(key);

            Assert.NotNull(fetched);
            Assert.Same(handler, fetched);
        }

        [Fact]
        public async Task Register_StrongTyped_DelegateInvokesOriginalMethod()
        {
            var m = new Module();
            var key = WellKnownHandlers.Trigger.Fetch("on_fetch");

            var registry = new BlueprintHandlerRegistry().Register(key, m.OnFetch);
            var fetched = registry.Get(key);

            await fetched!(default, CancellationToken.None);
            Assert.Equal(1, m.FetchCalls);
        }

        [Fact]
        public void Get_StrongTyped_UnregisteredReturnsNull()
        {
            var registry = new BlueprintHandlerRegistry();
            var fetched = registry.Get(WellKnownHandlers.Trigger.Fetch("never_registered"));
            Assert.Null(fetched);
        }

        [Fact]
        public void Get_StrongTyped_TypeMismatchReturnsNull()
        {
            // 同 name 注册成 fetch handler,但用 formatter key 取 → 类型不兼容,返回 null。
            // 这是 BlueprintHandlerRegistry.TryGet(name) as TDelegate 的天然行为,
            // HandlerKey 的强类型只在 *注册* 端起作用,查询端只是把 cast 后的类型暴露给 IDE。
            var m = new Module();
            var registry = new BlueprintHandlerRegistry()
                .Register(WellKnownHandlers.Trigger.Fetch("shared_name"), m.OnFetch);

            var fmt = registry.Get(WellKnownHandlers.Crosshair.FutureXLabel("shared_name"));
            Assert.Null(fmt);
        }

        [Fact]
        public void StrongTyped_CoexistsWith_AutoDiscover()
        {
            var m = new Module();
            // AutoDiscover 这次扫不到任何东西(Module 没贴 [BlueprintHandler]),用空模块测共存即可。
            var registry = new BlueprintHandlerRegistry()
                .AutoDiscover(m)                                                           // 弱类型路径(空模块,无 entry)
                .Register(WellKnownHandlers.Trigger.Fetch("on_fetch"), m.OnFetch)          // 强类型路径
                .Register(WellKnownHandlers.Crosshair.FutureXLabel("fmt"), m.FormatLabel); // 强类型路径

            Assert.True(registry.Contains("on_fetch"));
            Assert.True(registry.Contains("fmt"));
            Assert.NotNull(registry.Get(WellKnownHandlers.Trigger.Fetch("on_fetch")));
            Assert.NotNull(registry.Get(WellKnownHandlers.Crosshair.FutureXLabel("fmt")));
        }

        [Fact]
        public void HandlerKey_ImplicitFromString_Works()
        {
            // string → HandlerKey<T> 隐式转换让 ad-hoc 调用更轻
            var m = new Module();
            var registry = new BlueprintHandlerRegistry()
                .Register<Func<int, string>>("ad_hoc", m.FormatLabel);

            Assert.True(registry.Contains("ad_hoc"));
            var fmt = registry.Get<Func<int, string>>("ad_hoc");
            Assert.NotNull(fmt);
            Assert.Equal("#42", fmt!(42));
        }

        [Fact]
        public void TryGetFetch_StillWorks_WithStrongTypedRegistration()
        {
            // 既存的 TryGetFetch 路径 (DynamicChartSchema.WireTrigger 用) 不被强类型路径破坏
            var m = new Module();
            var registry = new BlueprintHandlerRegistry()
                .Register(WellKnownHandlers.Trigger.Fetch("on_fetch"), m.OnFetch);

            var fetch = registry.TryGetFetch("on_fetch");
            Assert.NotNull(fetch);
        }
    }
}
