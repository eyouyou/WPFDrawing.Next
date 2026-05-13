using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hevo.Charting.LowCode.Designer.Converters
{
    /// <summary>
    /// <see cref="IHevoString"/> 的多态 JSON 转换器,跟 <see cref="HevoBrushJsonConverter"/> 同款思路:
    /// 用 <c>kind</c> 判别符路由到具体实现,默认 System.Text.Json 不能反序列化接口的坑点统一收拢在这。
    ///
    /// <para>JSON 形态:</para>
    /// <code>
    /// { "kind": "literal",  "text": "涨跌幅" }
    /// { "kind": "resource", "key":  "TooltipKeys.ChangePct" }
    /// </code>
    ///
    /// <para>
    /// 业务侧扩展自家 <see cref="IHevoString"/> 实现时,派生并在本 converter 加 case
    /// (或后续抽成 registry,本次范围只覆盖框架自带 2 种)。
    /// </para>
    /// </summary>
    public sealed class HevoStringJsonConverter : JsonConverter<IHevoString>, IPolymorphicJsonConverter
    {
        // CanConvert 扩展到所有 IHevoString 后代 —— 跟 HevoBrushJsonConverter 同款理由:
        // Properties 字典里按 runtime type 查 converter,默认只匹 IHevoString 本身,
        // 具体子类被 fallback 到 record POCO 序列化 → schema 跟约定不符。
        public override bool CanConvert(Type typeToConvert)
            => typeof(IHevoString).IsAssignableFrom(typeToConvert);

        public Type TargetType => typeof(IHevoString);

        public IReadOnlyList<PolymorphicKind> Kinds { get; } = new PolymorphicKind[]
        {
            new("Literal",  typeof(HevoLiteralString),  () => new HevoLiteralString("")),
            new("Resource", typeof(HevoResourceString), () => new HevoResourceString("")),
        };

        public override IHevoString? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException($"IHevoString 期望对象,实际是 {reader.TokenType}");

            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            // 空对象 `{}` 兜底 —— 兼容历史蓝图里 `"Name": {}` 这种占位写法,
            // 让 framework 上层 `?? new HevoLiteralString(...)` 的 fallback 接住。
            if (!root.TryGetProperty("kind", out var kindElem))
            {
                if (root.EnumerateObject().MoveNext() == false) return null;
                throw new JsonException("IHevoString 对象缺少 \"kind\" 字符串字段。");
            }
            if (kindElem.ValueKind != JsonValueKind.String)
                throw new JsonException("IHevoString 的 \"kind\" 必须是字符串。");

            return kindElem.GetString() switch
            {
                "literal"  => ReadLiteral(root),
                "resource" => ReadResource(root),
                var k => throw new JsonException($"未知的 IHevoString kind: '{k}'"),
            };
        }

        public override void Write(Utf8JsonWriter writer, IHevoString value, JsonSerializerOptions options)
        {
            switch (value)
            {
                case HevoLiteralString lit:
                    writer.WriteStartObject();
                    writer.WriteString("kind", "literal");
                    writer.WriteString("text", lit.Text);
                    writer.WriteEndObject();
                    return;
                case HevoResourceString res:
                    writer.WriteStartObject();
                    writer.WriteString("kind", "resource");
                    writer.WriteString("key", res.ResourceKey);
                    writer.WriteEndObject();
                    return;
                default:
                    throw new JsonException($"未支持的 IHevoString 实现: {value.GetType().Name}。请扩展 HevoStringJsonConverter 或在业务侧自定义。");
            }
        }

        private static HevoLiteralString ReadLiteral(JsonElement root)
        {
            if (!root.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String)
                throw new JsonException("literal string 必须含 \"text\" 字符串字段");
            return new HevoLiteralString(t.GetString()!);
        }

        private static HevoResourceString ReadResource(JsonElement root)
        {
            if (!root.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String)
                throw new JsonException("resource string 必须含 \"key\" 字符串字段");
            return new HevoResourceString(k.GetString()!);
        }
    }
}
