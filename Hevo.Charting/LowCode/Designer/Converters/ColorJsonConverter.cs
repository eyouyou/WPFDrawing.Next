using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Hevo.Charting.LowCode.Designer.Converters
{
    /// <summary>
    /// <see cref="Color"/> ↔ <c>"#AARRGGBB"</c> / <c>"#RRGGBB"</c> 字符串。
    /// <para>
    /// 写出:Alpha == 0xFF → 6 位 (#RRGGBB);否则 8 位 (#AARRGGBB),信息无损 round-trip。
    /// 读入:容忍 <c>#</c> 前缀可选、小写、3 位 (#RGB) / 4 位 (#ARGB) / 6 位 / 8 位四种长度。
    /// </para>
    /// <para>
    /// 默认 <see cref="JsonSerializer"/> 把 Color 序列化为嵌套 byte 对象,
    /// AI 生成蓝图 / 人手撸蓝图 / diff 评审都不友好,这个 converter 把它收敛到稳定的色码字符串。
    /// </para>
    /// </summary>
    public sealed class ColorJsonConverter : JsonConverter<Color>
    {
        public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException($"Color 必须是 \"#RRGGBB\" 或 \"#AARRGGBB\" 字符串,实际是 {reader.TokenType}");

            var s = reader.GetString();
            if (string.IsNullOrEmpty(s))
                throw new JsonException("Color 字符串为空");

            return ParseColor(s);
        }

        public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options)
        {
            // Alpha=255(不透明)写 6 位省字符;否则 8 位带 alpha,完全可逆。
            writer.WriteStringValue(value.A == 0xFF
                ? $"#{value.R:X2}{value.G:X2}{value.B:X2}"
                : $"#{value.A:X2}{value.R:X2}{value.G:X2}{value.B:X2}");
        }

        internal static Color ParseColor(string s)
        {
            var span = s.AsSpan().Trim();
            if (span.Length > 0 && span[0] == '#') span = span[1..];

            const System.Globalization.NumberStyles HEX = System.Globalization.NumberStyles.HexNumber;

            // 3 位 (#RGB) / 4 位 (#ARGB):每个 nibble 复制成 2 位 → "1A2" → "11AA22"。
            // 直接在窄长度上算出 byte 不绕道扩展 buffer (避免 stackalloc 跨域 lifetime 问题)。
            if (span.Length == 3)
            {
                byte r3 = ByteFromNibble(span[0]);
                byte g3 = ByteFromNibble(span[1]);
                byte b3 = ByteFromNibble(span[2]);
                return Color.FromArgb(0xFF, r3, g3, b3);
            }
            if (span.Length == 4)
            {
                byte a4 = ByteFromNibble(span[0]);
                byte r4 = ByteFromNibble(span[1]);
                byte g4 = ByteFromNibble(span[2]);
                byte b4 = ByteFromNibble(span[3]);
                return Color.FromArgb(a4, r4, g4, b4);
            }

            byte a = 0xFF, r, g, b;
            if (span.Length == 6)
            {
                r = byte.Parse(span[0..2], HEX);
                g = byte.Parse(span[2..4], HEX);
                b = byte.Parse(span[4..6], HEX);
            }
            else if (span.Length == 8)
            {
                a = byte.Parse(span[0..2], HEX);
                r = byte.Parse(span[2..4], HEX);
                g = byte.Parse(span[4..6], HEX);
                b = byte.Parse(span[6..8], HEX);
            }
            else
            {
                throw new JsonException($"Color 字符串长度非法: '{s}'。需要 #RGB / #ARGB / #RRGGBB / #AARRGGBB。");
            }
            return Color.FromArgb(a, r, g, b);
        }

        private static byte ByteFromNibble(char c)
        {
            int v = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => throw new JsonException($"非法的 hex 字符: '{c}'"),
            };
            // "1" → 0x11,即把 nibble 复制到高低 4 位
            return (byte)((v << 4) | v);
        }
    }
}
