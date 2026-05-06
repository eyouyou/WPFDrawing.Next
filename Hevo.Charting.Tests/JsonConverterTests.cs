using System.Text.Json;
using System.Windows.Media;
using Hevo.Charting;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.Converters;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §7 蓝图 JsonConverter 回归。
    /// 关键不变式:Color / IHevoBrush 通过 BlueprintJsonOptions 序列化-反序列化得到等价值,
    /// 且写出的 JSON 形态稳定(便于 AI 生成 / diff 评审)。
    /// </summary>
    public sealed class JsonConverterTests
    {
        private static readonly JsonSerializerOptions Opts = BlueprintJsonOptions.Default;

        // ============================================================
        // ColorJsonConverter
        // ============================================================

        [Fact]
        public void Color_Opaque_WritesSixDigits()
        {
            var c = Color.FromRgb(0x14, 0x16, 0x1B);
            var json = JsonSerializer.Serialize(c, Opts);
            Assert.Equal("\"#14161B\"", json);
        }

        [Fact]
        public void Color_WithAlpha_WritesEightDigits()
        {
            var c = Color.FromArgb(0x55, 0xB0, 0xBE, 0xC5);
            var json = JsonSerializer.Serialize(c, Opts);
            Assert.Equal("\"#55B0BEC5\"", json);
        }

        [Theory]
        [InlineData("\"#14161B\"",   0xFF, 0x14, 0x16, 0x1B)]
        [InlineData("\"#55B0BEC5\"", 0x55, 0xB0, 0xBE, 0xC5)]
        [InlineData("\"14161B\"",    0xFF, 0x14, 0x16, 0x1B)]   // 无 # 前缀
        [InlineData("\"#abc\"",      0xFF, 0xAA, 0xBB, 0xCC)]   // 3 位简写
        [InlineData("\"#5abc\"",     0x55, 0xAA, 0xBB, 0xCC)]   // 4 位 #ARGB
        public void Color_Read_AcceptsAllValidForms(string json, byte a, byte r, byte g, byte b)
        {
            var c = JsonSerializer.Deserialize<Color>(json, Opts);
            Assert.Equal(Color.FromArgb(a, r, g, b), c);
        }

        [Fact]
        public void Color_RoundTrip_PreservesValue()
        {
            var orig = Color.FromArgb(0x80, 0x12, 0x34, 0x56);
            var json = JsonSerializer.Serialize(orig, Opts);
            var back = JsonSerializer.Deserialize<Color>(json, Opts);
            Assert.Equal(orig, back);
        }

        // ============================================================
        // HevoBrushJsonConverter
        // ============================================================

        [Fact]
        public void Brush_Solid_RoundTrip()
        {
            IHevoBrush orig = new HevoSolidBrush(Color.FromRgb(0x14, 0x16, 0x1B));
            var json = JsonSerializer.Serialize(orig, Opts);
            // schema 检查:必须含 kind + color 字段,key 名稳定
            Assert.Contains("\"kind\": \"solid\"", json);
            Assert.Contains("\"color\": \"#14161B\"", json);

            var back = JsonSerializer.Deserialize<IHevoBrush>(json, Opts);
            Assert.IsType<HevoSolidBrush>(back);
            Assert.Equal(orig, back);   // record 自动 by-value Equals
        }

        [Fact]
        public void Brush_Resource_RoundTrip()
        {
            IHevoBrush orig = new HevoResourceBrush("BrushKeys.L1");
            var json = JsonSerializer.Serialize(orig, Opts);
            Assert.Contains("\"kind\": \"resource\"", json);
            Assert.Contains("\"key\": \"BrushKeys.L1\"", json);

            var back = JsonSerializer.Deserialize<IHevoBrush>(json, Opts);
            Assert.IsType<HevoResourceBrush>(back);
            Assert.Equal(orig, back);
        }

        [Fact]
        public void Brush_UnknownKind_Throws()
        {
            var json = "{ \"kind\": \"galaxy_explosion\" }";
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IHevoBrush>(json, Opts));
        }

        // ============================================================
        // SmartActivator: Properties 字典走 BlueprintJsonOptions
        // ============================================================

        [Fact]
        public void Properties_ColorRoundTrip_ViaSmartActivator()
        {
            // 模拟实际蓝图加载:Properties 字典含 Color,经 JSON 序列化后 SmartActivator 注入到目标对象。
            // 关键不变式:目标实例的 Color 属性必须等于源 Color (绕过 default JSON 的嵌套对象失败)。
            var raw = new Dictionary<string, object?>
            {
                ["TargetColor"] = Color.FromRgb(0x14, 0x16, 0x1B),
            };
            var json = JsonSerializer.Serialize(raw, Opts);
            var revived = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, Opts);
            Assert.NotNull(revived);

            var instance = new TestPropTarget();
            SmartActivator.InjectProperties(instance, revived);
            Assert.Equal(Color.FromRgb(0x14, 0x16, 0x1B), instance.TargetColor);
        }

        [Fact]
        public void Properties_BrushRoundTrip_ViaSmartActivator()
        {
            // IHevoBrush 走 SmartActivator: revived JsonElement 应被识别为 brush converter 的目标
            // 进而走 JsonSerializer.Deserialize<IHevoBrush>(...) 路径。
            var raw = new Dictionary<string, object?>
            {
                ["TargetBrush"] = (IHevoBrush)new HevoSolidBrush(Color.FromRgb(0x14, 0x16, 0x1B)),
            };
            var json = JsonSerializer.Serialize(raw, Opts);
            var revived = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, Opts);

            var instance = new TestPropTarget();
            SmartActivator.InjectProperties(instance, revived);
            Assert.IsType<HevoSolidBrush>(instance.TargetBrush);
            Assert.Equal(Color.FromRgb(0x14, 0x16, 0x1B), ((HevoSolidBrush)instance.TargetBrush!).Color);
        }

        // 测试用注入目标 —— 仅暴露 Color / IHevoBrush 两个属性。
        public class TestPropTarget
        {
            public Color TargetColor { get; set; }
            public IHevoBrush? TargetBrush { get; set; }
        }
    }
}
