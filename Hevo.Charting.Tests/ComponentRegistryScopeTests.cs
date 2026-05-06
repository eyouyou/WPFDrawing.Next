using Hevo.Charting.Features;
using Hevo.Charting.LowCode.Designer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §11 ComponentRegistry 作用域隔离 —— alias prefix 方案验收测试。
    /// <para>
    /// 关键场景:多业务线复用蓝图时,不同业务自定义 Feature 撞名(典型:业务 A 跟业务 B 都叫 LineSeriesFeature)
    /// 互相覆盖。这里验证:
    /// </para>
    /// <list type="bullet">
    ///   <item>带 prefix 的 RegisterAssemblyOf 把 alias 存成 "prefix:TypeName"</item>
    ///   <item>Resolve("prefix:TypeName") 精确命中</item>
    ///   <item>Resolve("anyprefix:KnownName") 找不到精确项时回退到无前缀注册(兼容旧蓝图)</item>
    ///   <item>不同业务两个 prefix 各自互不干扰,即便指向同一类型也独立寻址</item>
    /// </list>
    /// <para>
    /// 测试不调用 ComponentRegistry.Reset() —— ComponentRegistry 是全局静态字典,Reset 会破坏其他用例
    /// (BlueprintTestFixture 已在 collection ctor 阶段注册大量框架 builtin)。各 Fact 用唯一 prefix 隔离即可。
    /// </para>
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class ComponentRegistryScopeTests
    {
        [Fact]
        public void RegisterAssemblyOf_WithPrefix_RegistersAsPrefixedAlias()
        {
            BuiltinRegistration.RegisterAssemblyOf<TestDataSource>("scope_a");

            Assert.True(ComponentRegistry.IsRegistered("scope_a:TestDataSource"));
            Assert.Equal(typeof(TestDataSource), ComponentRegistry.Resolve("scope_a:TestDataSource"));
        }

        [Fact]
        public void RegisterAssemblyOf_TwoDifferentPrefixes_BothResolvable()
        {
            // 模拟两个业务线分别给同一程序集挂自己的 prefix
            BuiltinRegistration.RegisterAssemblyOf<TestDataSource>("scope_b1");
            BuiltinRegistration.RegisterAssemblyOf<TestDataSource>("scope_b2");

            Assert.True(ComponentRegistry.IsRegistered("scope_b1:TestDataSource"));
            Assert.True(ComponentRegistry.IsRegistered("scope_b2:TestDataSource"));
            Assert.Equal(typeof(TestDataSource), ComponentRegistry.Resolve("scope_b1:TestDataSource"));
            Assert.Equal(typeof(TestDataSource), ComponentRegistry.Resolve("scope_b2:TestDataSource"));
        }

        [Fact]
        public void Resolve_PrefixedQuery_FallsBackToUnprefixedRegistration()
        {
            // BlueprintTestFixture 已经把 LineSeriesFeature 以无前缀名 ("LineSeriesFeature") 登记。
            // 旧蓝图查 "LineSeriesFeature" 命中,新蓝图查 "anyprefix:LineSeriesFeature" 也应回退命中
            // —— 跨进程兼容关键:加 prefix 不破坏旧蓝图,旧无前缀注册可被任何 prefix 查询命中。
            var resolved = ComponentRegistry.Resolve("any_unknown_prefix:LineSeriesFeature");
            Assert.Equal(typeof(LineSeriesFeature), resolved);
        }

        [Fact]
        public void IsRegistered_PrefixedQuery_FallsBackToUnprefixedRegistration()
        {
            // IsRegistered 跟 Resolve 同语义,DryRun 等预检场景靠这条
            Assert.True(ComponentRegistry.IsRegistered("any_unknown_prefix:LineSeriesFeature"));
        }

        [Fact]
        public void Resolve_UnknownTypeWithoutPrefix_Throws()
        {
            // 边界:无 prefix 的未知名直接抛(原行为不变)
            Assert.Throws<System.Exception>(() => ComponentRegistry.Resolve("definitely_unknown_type_xyz"));
        }

        [Fact]
        public void Resolve_PrefixedQueryWithUnknownTypeName_Throws()
        {
            // 边界:有 prefix 但去掉 prefix 后仍然找不到 → 抛
            Assert.Throws<System.Exception>(() =>
                ComponentRegistry.Resolve("any_prefix:definitely_unknown_type_xyz"));
        }

        [Fact]
        public void IsRegistered_LeadingColonWithKnownName_FallsBackToUnprefixed()
        {
            // 边界:":LineSeriesFeature" 视为 prefix="" + name="LineSeriesFeature",
            // TryStripPrefix 仍把尾段拿出来回退查询 —— 保持跟 "anyprefix:Name" 一致的宽松匹配。
            Assert.True(ComponentRegistry.IsRegistered(":LineSeriesFeature"));
        }
    }
}
