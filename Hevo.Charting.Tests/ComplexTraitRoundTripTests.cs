using System.Text.Json;
using System.Windows.Media;
using Hevo.Charting;
using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode.Designer.Converters;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §7 的"覆盖度"验证:嵌套 record (LineStyle / HevoPen / AxisStyleTrait) 能否仅靠
    /// Color + IHevoBrush 两个底层 converter + System.Text.Json 默认 record 序列化完成 round-trip。
    /// <para>
    /// 如果这些都通过,说明不需要给每个 trait 单独写 converter ——
    /// 顶层多态(IHevoBrush 接口)有 converter,叶子值类型(Color)有 converter,
    /// 中间普通 record 由 System.Text.Json 默认走 primary ctor 反序列化即可。
    /// </para>
    /// </summary>
    public sealed class ComplexTraitRoundTripTests
    {
        private static readonly JsonSerializerOptions Opts = BlueprintJsonOptions.Default;

        // ============================================================
        // HevoPen — 含 IHevoBrush + 可空数组 + 多 primitive
        // ============================================================

        [Fact]
        public void HevoPen_Solid_RoundTrip()
        {
            var orig = new HevoPen(
                new HevoSolidBrush(Color.FromRgb(0xB0, 0xBE, 0xC5)),
                Thickness: 1.5,
                DashArray: new[] { 2.0, 2.0 },
                LineCap: 1,
                LineJoin: 2,
                IsAntialias: false);

            var json = JsonSerializer.Serialize(orig, Opts);
            var back = JsonSerializer.Deserialize<HevoPen>(json, Opts);

            Assert.NotNull(back);
            Assert.IsType<HevoSolidBrush>(back!.Brush);
            Assert.Equal(orig.Thickness, back.Thickness);
            Assert.Equal(orig.DashArray, back.DashArray);
            Assert.Equal(orig.LineCap, back.LineCap);
            Assert.Equal(orig.LineJoin, back.LineJoin);
            Assert.Equal(orig.IsAntialias, back.IsAntialias);
            // 💥 不能用 Assert.Equal(orig, back) —— record 的自动 Equals 对 double[] 字段
            //    走 ReferenceEquals,JSON round-trip 出来的新数组永远不等于原数组,即便内容相同。
            //    上面字段一一比对已经覆盖语义等价。
        }

        [Fact]
        public void HevoPen_NullDashArray_RoundTrip()
        {
            // null DashArray 时 record 自动 Equals 不踩"数组引用"坑(null == null),可以直接 Equal。
            var orig = new HevoPen(new HevoResourceBrush("BrushKeys.L1"), Thickness: 1.0);
            var json = JsonSerializer.Serialize(orig, Opts);
            var back = JsonSerializer.Deserialize<HevoPen>(json, Opts);
            Assert.Equal(orig, back);
        }

        // ============================================================
        // LineStyle — 含 HevoPen,典型 IVisualTrait
        // ============================================================

        [Fact]
        public void LineStyle_RoundTrip()
        {
            var orig = LineStyle.Create(Color.FromArgb(0x55, 0xB0, 0xBE, 0xC5), thickness: 1.0);
            var json = JsonSerializer.Serialize(orig, Opts);
            var back = JsonSerializer.Deserialize<LineStyle>(json, Opts);

            Assert.NotNull(back);
            Assert.Equal(orig.LinePen.Thickness, back!.LinePen.Thickness);
            Assert.IsType<HevoSolidBrush>(back.LinePen.Brush);
            var origBrush = (HevoSolidBrush)orig.LinePen.Brush;
            var backBrush = (HevoSolidBrush)back.LinePen.Brush;
            Assert.Equal(origBrush.Color, backBrush.Color);
        }

        [Fact]
        public void LineStyle_FromResource_RoundTrip()
        {
            var orig = LineStyle.FromResource("BrushKeys.L1", thickness: 2.0);
            var json = JsonSerializer.Serialize(orig, Opts);
            var back = JsonSerializer.Deserialize<LineStyle>(json, Opts);
            Assert.NotNull(back);
            Assert.IsType<HevoResourceBrush>(back!.LinePen.Brush);
            Assert.Equal("BrushKeys.L1", ((HevoResourceBrush)back.LinePen.Brush).ResourceKey);
        }

        // ============================================================
        // AxisStyleTrait — 含 IHevoBrush + LineStyle? + HevoTypeface,最复杂的实际 trait
        // ============================================================

        [Fact]
        public void AxisStyleTrait_FullStack_RoundTrip()
        {
            var orig = AxisStyleTrait.Create(
                placement: AxisPlacement.Right,
                textColor: Color.FromRgb(0xB0, 0xBE, 0xC5),
                baseLineStyle: LineStyle.Create(Color.FromArgb(0x55, 0xB0, 0xBE, 0xC5), 1.0),
                fontSize: 11.0);

            var json = JsonSerializer.Serialize(orig, Opts);
            var back = JsonSerializer.Deserialize<AxisStyleTrait>(json, Opts);

            Assert.NotNull(back);
            Assert.Equal(orig.Placement, back!.Placement);
            Assert.Equal(orig.FontSize, back.FontSize);
            Assert.IsType<HevoSolidBrush>(back.TextBrush);
            Assert.Equal(((HevoSolidBrush)orig.TextBrush).Color, ((HevoSolidBrush)back.TextBrush).Color);
            Assert.NotNull(back.BaseLineStyle);
            Assert.Equal(orig.Typeface.FontFamily, back.Typeface.FontFamily);
        }

        // ============================================================
        // 模拟实际蓝图加载:Properties 字典 → JSON → SmartActivator 注入 LineStyle
        // ============================================================

        public class TraitTarget
        {
            public LineStyle? BorderStyle { get; set; }
            public AxisStyleTrait? AxisStyle { get; set; }
        }

        [Fact]
        public void Properties_LineStyle_ViaSmartActivator()
        {
            var raw = new Dictionary<string, object?>
            {
                ["BorderStyle"] = LineStyle.Create(Color.FromArgb(0x55, 0xB0, 0xBE, 0xC5), 1.0),
            };
            var json = JsonSerializer.Serialize(raw, Opts);
            var revived = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, Opts);

            var target = new TraitTarget();
            Hevo.Charting.LowCode.Designer.SmartActivator.InjectProperties(target, revived);

            Assert.NotNull(target.BorderStyle);
            Assert.Equal(1.0, target.BorderStyle!.LinePen.Thickness);
            Assert.IsType<HevoSolidBrush>(target.BorderStyle.LinePen.Brush);
        }

        [Fact]
        public void Properties_AxisStyleTrait_ViaSmartActivator()
        {
            var raw = new Dictionary<string, object?>
            {
                ["AxisStyle"] = AxisStyleTrait.Create(
                    AxisPlacement.Right,
                    Color.FromRgb(0xB0, 0xBE, 0xC5),
                    fontSize: 11.0),
            };
            var json = JsonSerializer.Serialize(raw, Opts);
            var revived = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, Opts);

            var target = new TraitTarget();
            Hevo.Charting.LowCode.Designer.SmartActivator.InjectProperties(target, revived);

            Assert.NotNull(target.AxisStyle);
            Assert.Equal(AxisPlacement.Right, target.AxisStyle!.Placement);
            Assert.Equal(11.0, target.AxisStyle.FontSize);
        }
    }
}
