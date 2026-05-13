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

        // 接口字段无多态 Converter 时,Properties 里残留的 JsonElement 必须 silent skip,
        // 不污染 Preset 路径克隆出来的默认实例。模拟 ScaleStrategyTrait + IScale 场景:
        // .draft 里把 ScaleStrategyTrait.Default.DomainScale 序列化进 Properties,但 IScale 没注册
        // 多态 Converter,JsonSerializer.Deserialize<IScale> 会抛 NotSupportedException。
        // CoerceValue 早路短路返 SkipInjection → setter 跳过 → DomainScale 默认值保留。
        public interface IDummyScale { string Tag { get; } }
        public sealed record DummyEdge : IDummyScale
        {
            public string Tag => "edge";
            public static readonly DummyEdge Instance = new();
        }
        public record TraitWithInterface(IDummyScale Scale) : IVisualTrait
        {
            public static readonly TraitWithInterface Default = new(DummyEdge.Instance);
        }

        [Fact]
        public void Materialize_PresetWithStaleInterfaceProperty_KeepsPresetDefault()
        {
            // 模拟 .draft 把 IDummyScale 字段值跟 Preset 一起序列化下来 → 反序列化时 Properties
            // 里是个 JsonElement object,目标类型 IDummyScale 是个没注册 Converter 的接口。
            // 旧行为:JsonSerializer.Deserialize<IDummyScale> 抛 NotSupportedException(用户看到的 bug)。
            // 新行为:CoerceValue 返 SkipInjection → InjectProperties 跳过赋值 → preset 克隆的默认值保留。
            using var doc = System.Text.Json.JsonDocument.Parse("{\"Tag\":\"edge\"}");
            var stale = doc.RootElement.Clone();
            var def = new StyleModel
            {
                TraitTypeName = nameof(TraitWithInterface),
                Preset = "Default",
                Properties = new Dictionary<string, object?> { ["Scale"] = stale },
            };
            var inst = SmartActivator.MaterializeTrait(typeof(TraitWithInterface), def) as TraitWithInterface;
            Assert.NotNull(inst);
            Assert.Same(DummyEdge.Instance, inst!.Scale);
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

        // ============================================================
        // §F 防注入护栏:setter 非 public 的属性,蓝图 Properties 字典即便挟带同名 key 也禁止写入。
        // 模拟 ChartFeature.Viewport(internal set)被低代码反序列化路径误覆盖的坑。
        // ============================================================
        public sealed class GuardedTarget
        {
            public string Public { get; set; } = "init";
            public string Internal { get; internal set; } = "init";
            public string Private { get; private set; } = "init";
            public string InitOnly { get; init; } = "init"; // init = public setter + modreq → 必须仍可注入
        }

        [Fact]
        public void InjectProperties_SkipsNonPublicSetters_AllowsPublicAndInit()
        {
            var target = new GuardedTarget();
            var props = new Dictionary<string, object?>
            {
                ["Public"]   = "written",
                ["Internal"] = "written",
                ["Private"]  = "written",
                ["InitOnly"] = "written",
            };
            SmartActivator.InjectProperties(target, props);

            Assert.Equal("written", target.Public);
            Assert.Equal("init",    target.Internal); // internal set 被护栏挡住
            Assert.Equal("init",    target.Private);  // private set 被护栏挡住
            Assert.Equal("written", target.InitOnly); // init 是 public 修饰的 setter,允许
        }

        // ============================================================
        // CoerceValue §JsonElement 重构回归 —— 锁住"JsonElement 拆包前置 + 各种 ValueKind"
        // 跟目标类型(enum / int / double / bool / nullable)的注入行为。
        //
        // 历史坑:旧版 SafeChangeType cascade 顺序里 IsEnum / Nullable 等步骤优先匹配但不认 JsonElement,
        // 走到 Enum.ToObject(JsonElement) 等不兼容 API → 抛 → InjectProperties 吞掉 → 属性默认值。
        // 重构后:JsonElement 一步拆包,后续路径只面对 native,这些边界用例必须全部通过。
        // ============================================================

        public enum SampleEnum { Alpha = 0, Beta = 1, Gamma = 2 }

        public sealed class PrimitiveTarget
        {
            public SampleEnum EnumField  { get; init; } = SampleEnum.Alpha;
            public int        IntField   { get; init; }
            public float      FloatField { get; init; }
            public double     DblField   { get; init; }
            public bool       BoolField  { get; init; }
            public int?       NullableInt { get; init; } = -1;
            public string?    StrField   { get; init; }
        }

        // 解析一段 JSON、把第 0 个 property 的 JsonElement 喂给 InjectProperties —— 精确复现
        // BlueprintRunner ResolveInstances 路径上"Properties 字典里是 JsonElement"的形态。
        private static PrimitiveTarget InjectFromJson(string fieldName, string jsonValue)
        {
            using var doc = System.Text.Json.JsonDocument.Parse($"{{\"v\":{jsonValue}}}");
            var el = doc.RootElement.GetProperty("v").Clone();
            var target = new PrimitiveTarget();
            SmartActivator.InjectProperties(target, new Dictionary<string, object?> { [fieldName] = el });
            return target;
        }

        [Fact]
        public void CoerceValue_JsonElementString_ToEnum_ParsesByName()
        {
            // MergeMode 踩坑同款形态:JSON 字符串 enum 字面量必须能注入到 enum 属性。
            var t = InjectFromJson("EnumField", "\"Beta\"");
            Assert.Equal(SampleEnum.Beta, t.EnumField);
        }

        [Fact]
        public void CoerceValue_JsonElementNumber_ToEnum_ParsesByOrdinal()
        {
            // 旧版双坑:step 3 IsEnum 吃了 JsonElement(数字)→ Enum.ToObject(Type, JsonElement) 抛。
            // 重构后:JsonElement 拆包 → long → step 4 Enum.ToObject(Type, long) 走通。
            var t = InjectFromJson("EnumField", "2");
            Assert.Equal(SampleEnum.Gamma, t.EnumField);
        }

        [Fact]
        public void CoerceValue_JsonElementNumber_ToInt_AndDouble_AndFloat()
        {
            Assert.Equal(42,        InjectFromJson("IntField",   "42").IntField);
            Assert.Equal(3.14,      InjectFromJson("DblField",   "3.14").DblField, 4);
            Assert.Equal(2.5f,      InjectFromJson("FloatField", "2.5").FloatField);
        }

        [Fact]
        public void CoerceValue_JsonElementBool_ToBool()
        {
            Assert.True(InjectFromJson("BoolField", "true").BoolField);
            Assert.False(InjectFromJson("BoolField", "false").BoolField);
        }

        [Fact]
        public void CoerceValue_JsonElementNumber_ToNullableInt()
        {
            // Nullable<T> 拆壳走 step 1 → 后续步骤当 int 处理。JsonElement Number 拆 long → ChangeType to int。
            var t = InjectFromJson("NullableInt", "99");
            Assert.Equal(99, t.NullableInt);
        }

        [Fact]
        public void CoerceValue_JsonElementNull_ToNullableInt_SetsNull()
        {
            var t = InjectFromJson("NullableInt", "null");
            Assert.Null(t.NullableInt);
        }

        [Fact]
        public void CoerceValue_NativeString_ToEnum_StillWorks()
        {
            // 程序化路径(蓝图测试 / 业务直接塞 Properties)走 native string,不经 JsonElement。
            // 重构后这条路径必须保持原来行为。
            var target = new PrimitiveTarget();
            SmartActivator.InjectProperties(target, new Dictionary<string, object?> { ["EnumField"] = "Gamma" });
            Assert.Equal(SampleEnum.Gamma, target.EnumField);
        }

        // ============================================================
        // §Fail-fast 契约 —— 验证 InjectProperties 不再 silent-swallow,配置错在装配期就翻车。
        // 历史背景:旧版外层 try-catch 把所有注入异常吞成 console warning,导致蓝图 typo / 类型错配
        // 全部表现为"属性默认值保留",bug 极难定位(典型:MergeMode="WhenAll" 跑成 WhenAny)。
        // ============================================================

        [Fact]
        public void InjectProperties_UnknownKey_Throws()
        {
            // typo / 字段已删除 / 改名 —— 装配期立刻翻车,不让运行时神秘默认值
            var target = new PrimitiveTarget();
            var props = new Dictionary<string, object?> { ["NotARealField"] = 42 };
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SmartActivator.InjectProperties(target, props));
            Assert.Contains("NotARealField", ex.Message);
            Assert.Contains(nameof(PrimitiveTarget), ex.Message);
        }

        [Fact]
        public void InjectProperties_NullToNonNullableValueType_Throws()
        {
            // 蓝图 JSON 显式 "IntField": null —— 旧版静默吞 + 默认值;新版 fail-fast 抛
            var target = new PrimitiveTarget();
            var props = new Dictionary<string, object?> { ["IntField"] = null };
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SmartActivator.InjectProperties(target, props));
            Assert.Contains("null", ex.Message);
            Assert.Contains("Int32", ex.Message);
        }

        [Fact]
        public void InjectProperties_TypeMismatch_Throws()
        {
            // "abc" 不是合法 int —— TypeConverter 抛 FormatException(或 Convert.ChangeType 兜底抛 InvalidCast)。
            // 不管哪种,fail-fast 都把异常上传出去给调用方,而不是吞了留默认值。
            var target = new PrimitiveTarget();
            var props = new Dictionary<string, object?> { ["IntField"] = "abc" };
            Assert.ThrowsAny<Exception>(() => SmartActivator.InjectProperties(target, props));
        }

        [Fact]
        public void InjectProperties_ReadOnlyProperty_Throws()
        {
            // 蓝图配只读字段 = 配置错,fail-fast 抛(跟"属性不存在"同语义,都属于"蓝图作者写错了")
            var target = new ReadOnlyTarget();
            var props = new Dictionary<string, object?> { ["ReadOnly"] = "x" };
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SmartActivator.InjectProperties(target, props));
            Assert.Contains("只读", ex.Message);
        }

        public sealed class ReadOnlyTarget
        {
            public string ReadOnly { get; } = "init";
        }
    }
}
