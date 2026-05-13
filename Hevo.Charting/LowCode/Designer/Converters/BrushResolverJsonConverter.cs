using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hevo.Charting.Buildin;
using Hevo.Charting.Core;

namespace Hevo.Charting.LowCode.Designer.Converters
{
    /// <summary>
    /// <see cref="IBrushResolver{Double}"/> 的多态 JSON 转换器,跟 <see cref="HevoBrushJsonConverter"/>
    /// / <see cref="HevoStringJsonConverter"/> 同款思路:用 <c>kind</c> 判别符路由到具体实现。
    ///
    /// <para>JSON 形态(新 schema):</para>
    /// <code>
    /// { "kind": "static",    "brush": { "kind": "solid", "color": "#CCCCCC" } }
    /// { "kind": "threshold", "threshold": 0,
    ///   "above": { "kind": "solid", "color": "#33FF66" },
    ///   "below": { "kind": "solid", "color": "#FF4444" },
    ///   "equal": { "kind": "solid", "color": "#CCCCCC" },
    ///   "base":  null }
    /// </code>
    ///
    /// <para>
    /// 同时兼容 legacy shape(直接序列化 record/class 公开属性):
    /// <c>{ "ConstantBrush": { "kind": "solid", ... } }</c> → 被识别为 <see cref="StaticBrushResolver{Double}"/>。
    /// </para>
    /// </summary>
    public sealed class BrushResolverJsonConverter : JsonConverter<IBrushResolver<double>>, IPolymorphicJsonConverter
    {
        // Properties 字典按 runtime type 查 converter;扩展到所有 IBrushResolver<double> 后代,
        // 否则 StaticBrushResolver<double> / ThresholdBrushResolver fallback 到 POCO 序列化。
        public override bool CanConvert(Type typeToConvert)
            => typeof(IBrushResolver<double>).IsAssignableFrom(typeToConvert);

        public Type TargetType => typeof(IBrushResolver<double>);

        public IReadOnlyList<PolymorphicKind> Kinds { get; } = new PolymorphicKind[]
        {
            new("Static",    typeof(StaticBrushResolver<double>),
                () => new StaticBrushResolver<double>(new HevoSolidBrush(System.Windows.Media.Colors.White))),
            // threshold 五个 brush 参数,picker 渲染时部分可编辑(ConstantBrush 是 get-only,其余 above/below/equal 是私有)
            // —— 实用价值有限,但保留 kind 让用户从 JSON 看明白可选项;真要改参用 JSON tab 比 picker 直观。
            new("Threshold", typeof(ThresholdBrushResolver),
                () => new ThresholdBrushResolver(
                    threshold: 0,
                    above: new HevoSolidBrush(System.Windows.Media.Color.FromRgb(0x33, 0xFF, 0x66)),
                    below: new HevoSolidBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x44, 0x44)),
                    equal: new HevoSolidBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    baseBrush: null)),
        };

        public override IBrushResolver<double>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException($"IBrushResolver<double> 期望对象,实际是 {reader.TokenType}");

            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            // 新 schema:看 kind 判别符
            if (root.TryGetProperty("kind", out var kindElem) && kindElem.ValueKind == JsonValueKind.String)
            {
                return kindElem.GetString() switch
                {
                    "static"    => ReadStatic(root, options),
                    "threshold" => ReadThreshold(root, options),
                    var k => throw new JsonException($"未知的 IBrushResolver<double> kind: '{k}'"),
                };
            }

            // legacy shape:无 kind,但有 ConstantBrush → StaticBrushResolver
            if (root.TryGetProperty("ConstantBrush", out var cbElem))
            {
                var brush = JsonSerializer.Deserialize<IHevoBrush>(cbElem.GetRawText(), options)
                            ?? throw new JsonException("legacy ConstantBrush 反序列化为 null");
                return new StaticBrushResolver<double>(brush);
            }

            // 空对象 `{}` 兜底:返回 null,让上层 nullable 字段 fallback 接住
            if (root.EnumerateObject().MoveNext() == false) return null;

            throw new JsonException("IBrushResolver<double> 对象缺少 \"kind\" 字段或 \"ConstantBrush\" legacy 字段。");
        }

        public override void Write(Utf8JsonWriter writer, IBrushResolver<double> value, JsonSerializerOptions options)
        {
            switch (value)
            {
                case StaticBrushResolver<double> stat:
                    writer.WriteStartObject();
                    writer.WriteString("kind", "static");
                    writer.WritePropertyName("brush");
                    JsonSerializer.Serialize(writer, stat.ConstantBrush, options);
                    writer.WriteEndObject();
                    return;
                case ThresholdBrushResolver thr:
                    WriteThreshold(writer, thr, options);
                    return;
                default:
                    throw new JsonException($"未支持的 IBrushResolver<double> 实现: {value.GetType().Name}。请扩展 BrushResolverJsonConverter 或在业务侧自定义。");
            }
        }

        private static StaticBrushResolver<double> ReadStatic(JsonElement root, JsonSerializerOptions options)
        {
            if (!root.TryGetProperty("brush", out var b))
                throw new JsonException("static resolver 必须含 \"brush\" 字段");
            var brush = JsonSerializer.Deserialize<IHevoBrush>(b.GetRawText(), options)
                        ?? throw new JsonException("static resolver 的 brush 反序列化为 null");
            return new StaticBrushResolver<double>(brush);
        }

        private static ThresholdBrushResolver ReadThreshold(JsonElement root, JsonSerializerOptions options)
        {
            if (!root.TryGetProperty("threshold", out var t) || t.ValueKind != JsonValueKind.Number)
                throw new JsonException("threshold resolver 必须含 \"threshold\" 数字字段");
            var above = ReadRequiredBrush(root, "above", options);
            var below = ReadRequiredBrush(root, "below", options);
            var equal = ReadRequiredBrush(root, "equal", options);
            IHevoBrush? baseBrush = null;
            if (root.TryGetProperty("base", out var b) && b.ValueKind != JsonValueKind.Null)
                baseBrush = JsonSerializer.Deserialize<IHevoBrush>(b.GetRawText(), options);
            return new ThresholdBrushResolver(t.GetDouble(), above, below, equal, baseBrush);
        }

        private static IHevoBrush ReadRequiredBrush(JsonElement root, string name, JsonSerializerOptions options)
        {
            if (!root.TryGetProperty(name, out var e))
                throw new JsonException($"threshold resolver 缺少 \"{name}\" 字段");
            return JsonSerializer.Deserialize<IHevoBrush>(e.GetRawText(), options)
                   ?? throw new JsonException($"threshold resolver 的 \"{name}\" 反序列化为 null");
        }

        // ThresholdBrushResolver 私有字段没有 public 访问 —— 只能写 ConstantBrush + threshold(无法回放 above/below/equal)。
        // 这是 framework 限制,本 converter 序列化时降级成 static + ConstantBrush 提示用户去改源类型暴露内部。
        // 实际目前蓝图里只用 static,这个 case 极少触发。
        private static void WriteThreshold(Utf8JsonWriter writer, ThresholdBrushResolver thr, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "static");
            writer.WritePropertyName("brush");
            JsonSerializer.Serialize(writer, thr.ConstantBrush, options);
            writer.WriteEndObject();
        }
    }
}
