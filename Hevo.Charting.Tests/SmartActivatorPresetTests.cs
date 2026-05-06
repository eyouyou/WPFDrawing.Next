using Hevo.Charting.Abstractions;
using Hevo.Charting.LowCode.Designer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §6 Preset+Properties 单例污染兜底回归。
    /// 用一个本地测试 record trait,带 public static Default —— 模拟 ScaleStrategyTrait 这类典型形态。
    /// </summary>
    public sealed class SmartActivatorPresetTests
    {
        public record TestTrait(string Name, double Value) : IVisualTrait
        {
            public static readonly TestTrait Default = new("default", 1.0);
        }

        [Fact]
        public void Materialize_PresetOnly_ReturnsExactPresetReference()
        {
            // 单纯 Preset 不带 Properties → 不需要 clone,直接返回静态单例引用 (零分配快路径)。
            var def = new StyleModel { TraitTypeName = nameof(TestTrait), Preset = "Default" };
            var inst = SmartActivator.MaterializeTrait(typeof(TestTrait), def);
            Assert.Same(TestTrait.Default, inst);
        }

        [Fact]
        public void Materialize_PresetWithProperties_ClonesAndDoesNotPolluteSingleton()
        {
            var defaultBefore = TestTrait.Default;
            // 关键不变式:跨调用 Default.Value 永远是初始值 1.0 ——
            // 旧代码没有 clone,InjectProperties 直接写穿到 public static 字段实例,会污染单例。
            Assert.Equal(1.0, defaultBefore.Value);
            Assert.Equal("default", defaultBefore.Name);

            var def = new StyleModel
            {
                TraitTypeName = nameof(TestTrait),
                Preset = "Default",
                Properties = new Dictionary<string, object?> { ["Value"] = 42.0 },
            };
            var inst = SmartActivator.MaterializeTrait(typeof(TestTrait), def) as TestTrait;
            Assert.NotNull(inst);
            Assert.Equal(42.0, inst!.Value);
            Assert.Equal("default", inst.Name);  // 未列在 Properties 的字段从 preset 浅拷贝过来

            // 💥 污染检查:Default 单例本身必须保持不变。
            Assert.Same(defaultBefore, TestTrait.Default);
            Assert.Equal(1.0, TestTrait.Default.Value);
            Assert.Equal("default", TestTrait.Default.Name);

            // 实例独立:返回的不是同一个对象。
            Assert.NotSame(TestTrait.Default, inst);
        }

        [Fact]
        public void Materialize_PresetWithProperties_RepeatedCalls_ProduceDistinctClones()
        {
            // 多次调用必须各得独立 clone —— 否则两份蓝图共享同一克隆实例又会有相同问题。
            var def = new StyleModel
            {
                TraitTypeName = nameof(TestTrait),
                Preset = "Default",
                Properties = new Dictionary<string, object?> { ["Value"] = 7.0 },
            };
            var a = SmartActivator.MaterializeTrait(typeof(TestTrait), def);
            var b = SmartActivator.MaterializeTrait(typeof(TestTrait), def);
            Assert.NotSame(a, b);
        }
    }
}
