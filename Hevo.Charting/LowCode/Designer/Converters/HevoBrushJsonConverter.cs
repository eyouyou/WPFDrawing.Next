using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace Hevo.Charting.LowCode.Designer.Converters
{
    /// <summary>
    /// <see cref="IHevoBrush"/> 的多态 JSON 转换器。<see cref="IHevoBrush"/> 是个空接口,
    /// 默认 System.Text.Json 不能序列化抽象类型,通过本 converter 用 <c>kind</c> 判别符路由到具体实现。
    ///
    /// <para>JSON 形态:</para>
    /// <code>
    /// { "kind": "solid",    "color": "#14161B" }
    /// { "kind": "resource", "key":   "BrushKeys.L1" }
    /// { "kind": "linear",   "from":  "#000000", "to": "#FFFFFF",
    ///                       "p0":    [0, 0],    "p1": [1, 1] }
    /// </code>
    ///
    /// <para>
    /// AI 生成 / 人手撸蓝图都直观可写,业务侧扩展自定义 brush 类型时只需派生 IHevoBrush
    /// 并在本 converter 加一个 case(或后续抽成 registry,本次范围只覆盖框架自带 3 种)。
    /// </para>
    /// </summary>
    public sealed class HevoBrushJsonConverter : JsonConverter<IHevoBrush>
    {
        // 💥 关键:Properties 字典 (Dictionary<string, object?>) 序列化时,System.Text.Json 按 runtime
        //    type (HevoSolidBrush 等具体类型) 查 converter。默认 JsonConverter<IHevoBrush>.CanConvert
        //    只匹配 IHevoBrush 本身,具体子类被 fallback 到 record POCO 序列化 → schema 跟我们约定不符。
        //    这里扩展到所有 IHevoBrush 后代,统一走本 converter 的 kind 判别符 schema。
        public override bool CanConvert(Type typeToConvert)
            => typeof(IHevoBrush).IsAssignableFrom(typeToConvert);

        public override IHevoBrush? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException($"IHevoBrush 期望对象,实际是 {reader.TokenType}");

            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (!root.TryGetProperty("kind", out var kindElem) || kindElem.ValueKind != JsonValueKind.String)
                throw new JsonException("IHevoBrush 对象缺少 \"kind\" 字符串字段。");

            return kindElem.GetString() switch
            {
                "solid"    => ReadSolid(root),
                "resource" => ReadResource(root),
                "linear"   => ReadLinear(root),
                var k => throw new JsonException($"未知的 IHevoBrush kind: '{k}'"),
            };
        }

        public override void Write(Utf8JsonWriter writer, IHevoBrush value, JsonSerializerOptions options)
        {
            switch (value)
            {
                case HevoSolidBrush solid:
                    writer.WriteStartObject();
                    writer.WriteString("kind", "solid");
                    writer.WritePropertyName("color");
                    JsonSerializer.Serialize(writer, solid.Color, options);
                    writer.WriteEndObject();
                    return;
                case HevoResourceBrush res:
                    writer.WriteStartObject();
                    writer.WriteString("kind", "resource");
                    writer.WriteString("key", res.ResourceKey);
                    writer.WriteEndObject();
                    return;
                case HevoLinearGradientBrush lg:
                    writer.WriteStartObject();
                    writer.WriteString("kind", "linear");
                    writer.WritePropertyName("from"); JsonSerializer.Serialize(writer, lg.StartColor, options);
                    writer.WritePropertyName("to");   JsonSerializer.Serialize(writer, lg.EndColor, options);
                    writer.WriteStartArray("p0"); writer.WriteNumberValue(lg.StartPoint.X); writer.WriteNumberValue(lg.StartPoint.Y); writer.WriteEndArray();
                    writer.WriteStartArray("p1"); writer.WriteNumberValue(lg.EndPoint.X);   writer.WriteNumberValue(lg.EndPoint.Y);   writer.WriteEndArray();
                    writer.WriteEndObject();
                    return;
                default:
                    throw new JsonException($"未支持的 IHevoBrush 实现: {value.GetType().Name}。请扩展 HevoBrushJsonConverter 或在业务侧自定义。");
            }
        }

        private static HevoSolidBrush ReadSolid(JsonElement root)
        {
            if (!root.TryGetProperty("color", out var c) || c.ValueKind != JsonValueKind.String)
                throw new JsonException("solid brush 必须含 \"color\" 字符串字段");
            return new HevoSolidBrush(ColorJsonConverter.ParseColor(c.GetString()!));
        }

        private static HevoResourceBrush ReadResource(JsonElement root)
        {
            if (!root.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String)
                throw new JsonException("resource brush 必须含 \"key\" 字符串字段");
            return new HevoResourceBrush(k.GetString()!);
        }

        private static HevoLinearGradientBrush ReadLinear(JsonElement root)
        {
            Color from = ParseColorField(root, "from");
            Color to   = ParseColorField(root, "to");
            Point p0   = ParsePointField(root, "p0");
            Point p1   = ParsePointField(root, "p1");
            return new HevoLinearGradientBrush(from, to, p0, p1);
        }

        private static Color ParseColorField(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.String)
                throw new JsonException($"linear brush 缺少 \"{name}\" 字符串字段");
            return ColorJsonConverter.ParseColor(e.GetString()!);
        }

        private static Point ParsePointField(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Array)
                throw new JsonException($"linear brush 缺少 \"{name}\" 数组字段(应为 [x,y])");
            var arr = e.EnumerateArray().ToArray();
            if (arr.Length != 2) throw new JsonException($"\"{name}\" 数组长度必须 == 2");
            return new Point(arr[0].GetDouble(), arr[1].GetDouble());
        }
    }
}
