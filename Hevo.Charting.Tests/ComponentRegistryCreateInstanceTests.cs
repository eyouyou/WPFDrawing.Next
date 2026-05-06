using Hevo.Charting.Features;
using Hevo.Charting.LowCode.Designer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §2 编译委托缓存的回归测试。
    /// 关键不变式:CreateInstance 必须返回正确类型实例;反复调用得到不同实例 (newobj 每次新对象);
    /// 无 public 无参 ctor 的类型走 Activator 兜底不抛 ArgumentNullException。
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class ComponentRegistryCreateInstanceTests
    {
        [Fact]
        public void CreateInstance_ReturnsRequestedType()
        {
            var inst = ComponentRegistry.CreateInstance(typeof(LineSeriesFeature));
            Assert.IsType<LineSeriesFeature>(inst);
        }

        [Fact]
        public void CreateInstance_RepeatedCalls_ProduceDistinctInstances()
        {
            // 编译委托是 ctor() 包装,每次调用都 newobj —— 不能复用单例。
            var a = ComponentRegistry.CreateInstance(typeof(LineSeriesFeature));
            var b = ComponentRegistry.CreateInstance(typeof(LineSeriesFeature));
            Assert.NotSame(a, b);
        }

        [Fact]
        public void CreateInstance_NullArg_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ComponentRegistry.CreateInstance(null!));
        }
    }
}
