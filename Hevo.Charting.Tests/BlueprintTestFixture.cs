using Hevo.Charting.LowCode.Designer.GraphViewer;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 全局一次性 bootstrap —— GraphViewerBootstrap.Initialize 内部 _initialized 单例保护,
    /// 多个 IClassFixture 测试类共用没问题。同时把测试自定义 trait/feature 注册到 ComponentRegistry。
    /// 用 ICollectionFixture (xUnit 协议),TestCollection 标在每个测试类上即可。
    /// </summary>
    public sealed class BlueprintTestFixture
    {
        public BlueprintTestFixture()
        {
            // 触发框架 + GraphViewer 内置 feature 登记;反射缓存预热也在这里跑过一遍。
            GraphViewerBootstrap.Initialize();
            EnsureTestDataSourceRegistered();
        }

        // 测试自定义 DataSource 的全局登记入口 —— 跨测试类共享同一份注册。
        // 多次 Register 是 idempotent(ComponentRegistry 内部以 alias 字典覆盖),无副作用。
        private static bool _testDsRegistered;
        public static void EnsureTestDataSourceRegistered()
        {
            if (_testDsRegistered) return;
            _testDsRegistered = true;
            Hevo.Charting.LowCode.Designer.ComponentRegistry.Register<TestDataSource>();
        }
    }

    [CollectionDefinition(nameof(BlueprintCollection))]
    public sealed class BlueprintCollection : ICollectionFixture<BlueprintTestFixture>
    {
        // 标记类,xUnit 协议要求。无方法。
    }
}
