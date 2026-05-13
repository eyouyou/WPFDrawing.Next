using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hevo.Charting.Buildin;
using Hevo.Charting.Features;

namespace Hevo.Charting.LowCode.Designer.Converters
{
    /// <summary>
    /// <see cref="IZoomStrategy"/> 的多态 JSON 转换器,跟 <see cref="HevoBrushJsonConverter"/> /
    /// <see cref="BrushResolverJsonConverter"/> 同款思路:用 <c>kind</c> 判别符路由到具体实现。
    ///
    /// <para>JSON 形态:</para>
    /// <code>
    /// { "kind": "smartAdaptive" }
    /// { "kind": "crosshairAnchor" }
    /// { "kind": "rightEdge" }
    /// { "kind": "fixedWindow",  "windowBars": 100, "alignment": "RightEdge" }
    /// { "kind": "historyAware", "duringPaging": { "kind": "rightEdge" } }
    /// </code>
    ///
    /// <para>
    /// <b>无状态策略 → 返回单例</b>:mouseAnchor / rightEdge / smartAdaptive / leftEdge /
    /// crosshairAnchor / directional 全部返回各自 <c>.Instance</c> 静态字段。这是 NodeEditorWindow
    /// 的接口下拉(MakeInterfaceInstancePicker)能在 JSON 往返后保持选中态的关键 ——
    /// picker 用 <c>ReferenceEquals</c> 找选中项,如果反序列化每次都 new 一个新对象,选项永远落不到原值。
    /// </para>
    ///
    /// <para>
    /// <b>有参策略 → 每次 new</b>:fixedWindow / historyAware 带配置(WindowBars / DuringPaging),
    /// 不能复用单例。这两类在编辑器的下拉里不会出现选中态(picker 找不到 ReferenceEquals 匹配),
    /// 但 JSON 加载/保存路径仍然正确 —— 编辑器对这两类的处理是后续 enhancement。
    /// </para>
    /// </summary>
    public sealed class ZoomStrategyJsonConverter : JsonConverter<IZoomStrategy>, IPolymorphicJsonConverter
    {
        public override bool CanConvert(Type t) => typeof(IZoomStrategy).IsAssignableFrom(t);

        // ──────────────────────────────────────────────────────────────────
        // IPolymorphicJsonConverter —— 给 NodeEditorWindow 的下拉 picker 用
        // ──────────────────────────────────────────────────────────────────

        public Type TargetType => typeof(IZoomStrategy);

        public IReadOnlyList<PolymorphicKind> Kinds { get; } = new PolymorphicKind[]
        {
            new("SmartAdaptive",   typeof(SmartAdaptiveZoomStrategy),   () => SmartAdaptiveZoomStrategy.Instance),
            new("MouseAnchor",     typeof(MouseAnchorZoomStrategy),     () => MouseAnchorZoomStrategy.Instance),
            new("RightEdge",       typeof(RightEdgeZoomStrategy),       () => RightEdgeZoomStrategy.Instance),
            new("LeftEdge",        typeof(LeftEdgeZoomStrategy),        () => LeftEdgeZoomStrategy.Instance),
            new("CrosshairAnchor", typeof(CrosshairAnchorZoomStrategy), () => CrosshairAnchorZoomStrategy.Instance),
            new("Directional",     typeof(DirectionalZoomStrategy),     () => DirectionalZoomStrategy.Instance),
            // 带参实现:factory 给出默认值,picker 渲染子编辑器让用户改 WindowBars / Alignment / DuringPaging
            new("FixedWindow",     typeof(FixedWindowZoomStrategy),     () => new FixedWindowZoomStrategy()),
            new("HistoryAware",    typeof(HistoryAwareZoomStrategy),    () => new HistoryAwareZoomStrategy()),
        };

        public override IZoomStrategy? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException($"IZoomStrategy 期望对象,实际是 {reader.TokenType}");

            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            if (!root.TryGetProperty("kind", out var kindElem) || kindElem.ValueKind != JsonValueKind.String)
                throw new JsonException("IZoomStrategy 对象缺少 'kind' 字符串字段");

            string kind = kindElem.GetString()!;
            return kind switch
            {
                "mouseAnchor"     => MouseAnchorZoomStrategy.Instance,
                "rightEdge"       => RightEdgeZoomStrategy.Instance,
                "smartAdaptive"   => SmartAdaptiveZoomStrategy.Instance,
                "leftEdge"        => LeftEdgeZoomStrategy.Instance,
                "crosshairAnchor" => CrosshairAnchorZoomStrategy.Instance,
                "directional"     => DirectionalZoomStrategy.Instance,
                "fixedWindow"     => ReadFixedWindow(root),
                "historyAware"    => ReadHistoryAware(root, options),
                _ => throw new JsonException($"未知的 IZoomStrategy kind: '{kind}'。" +
                    "支持:mouseAnchor / rightEdge / smartAdaptive / leftEdge / crosshairAnchor / directional / fixedWindow / historyAware"),
            };
        }

        public override void Write(Utf8JsonWriter writer, IZoomStrategy value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            switch (value)
            {
                case MouseAnchorZoomStrategy:     writer.WriteString("kind", "mouseAnchor"); break;
                case RightEdgeZoomStrategy:       writer.WriteString("kind", "rightEdge"); break;
                case SmartAdaptiveZoomStrategy:   writer.WriteString("kind", "smartAdaptive"); break;
                case LeftEdgeZoomStrategy:        writer.WriteString("kind", "leftEdge"); break;
                case CrosshairAnchorZoomStrategy: writer.WriteString("kind", "crosshairAnchor"); break;
                case DirectionalZoomStrategy:     writer.WriteString("kind", "directional"); break;
                case FixedWindowZoomStrategy fw:
                    writer.WriteString("kind", "fixedWindow");
                    writer.WriteNumber("windowBars", fw.WindowBars);
                    writer.WriteString("alignment", fw.Alignment.ToString());
                    break;
                case HistoryAwareZoomStrategy ha:
                    writer.WriteString("kind", "historyAware");
                    writer.WritePropertyName("duringPaging");
                    JsonSerializer.Serialize(writer, ha.DuringPaging, options);
                    break;
                default:
                    throw new JsonException($"未支持的 IZoomStrategy 实现: {value.GetType().FullName}。" +
                        "请在 ZoomStrategyJsonConverter 加 case,或业务侧自定义 converter。");
            }
            writer.WriteEndObject();
        }

        private static FixedWindowZoomStrategy ReadFixedWindow(JsonElement root)
        {
            int windowBars = root.TryGetProperty("windowBars", out var w) && w.ValueKind == JsonValueKind.Number
                ? w.GetInt32() : 100;
            ViewportAlignment alignment = root.TryGetProperty("alignment", out var a) && a.ValueKind == JsonValueKind.String
                ? Enum.Parse<ViewportAlignment>(a.GetString()!, ignoreCase: true)
                : ViewportAlignment.RightEdge;
            return new FixedWindowZoomStrategy { WindowBars = windowBars, Alignment = alignment };
        }

        private static HistoryAwareZoomStrategy ReadHistoryAware(JsonElement root, JsonSerializerOptions options)
        {
            IZoomStrategy during = RightEdgeZoomStrategy.Instance;
            if (root.TryGetProperty("duringPaging", out var dp) && dp.ValueKind != JsonValueKind.Null)
            {
                during = JsonSerializer.Deserialize<IZoomStrategy>(dp.GetRawText(), options)
                    ?? RightEdgeZoomStrategy.Instance;
            }
            return new HistoryAwareZoomStrategy { DuringPaging = during };
        }
    }
}
