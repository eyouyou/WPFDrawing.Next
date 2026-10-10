using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hevo.Charting.Renderers.Raster
{
    /// <summary>文字测量结果(DIP),供 <see cref="TextLayoutHelper"/> 排版。</summary>
    internal struct RasterTextMeasure
    {
        public float Width;
        public float Height;
        internal RasterTextRenderer.FontEntry Font;
        internal RasterTextRenderer.Sprite? Sprite;
        internal float EmDevice;
    }

    /// <summary>
    /// 位图后端的文字:字形蒙版缓存 + 整串兜底。
    /// <list type="bullet">
    /// <item>快路径:字体能拿到 <see cref="GlyphTypeface"/> 且每个字符都有字形 → 按 (字形, 设备字号) 缓存 8 位覆盖率蒙版,
    /// 逐字形按 advance 摆放(落在整像素上)。蒙版只在第一次见到这个字形时用 RenderTargetBitmap 渲一次,
    /// 之后数字 / 字母组成的新标签(平移时坐标轴刻度天天变)不再触发任何 WPF 调用,0 分配。</item>
    /// <item>兜底:复合字体、缺字形(中文走字体回退)、代理对 → 整串用 FormattedText 渲成一张蒙版,按 (文本, 字体, 字号) 缓存。</item>
    /// </list>
    /// 两张表都有条数上限,超过就整表清空重建(只影响下一次 miss,不会无限增长)。
    /// </summary>
    internal sealed class RasterTextRenderer
    {
        internal sealed class FontEntry
        {
            public readonly Typeface Typeface;
            public GlyphTypeface? Glyphs;
            public double LineSpacing;
            public double Baseline;
            // ASCII 快表:0 = 未查,1 = 有字形,2 = 无字形
            public readonly byte[] AsciiState = new byte[128];
            public readonly ushort[] AsciiGlyph = new ushort[128];
            public readonly double[] AsciiAdvance = new double[128];

            public FontEntry(Typeface typeface) => Typeface = typeface;
        }

        internal sealed class GlyphMask
        {
            public byte[] Mask = Array.Empty<byte>();
            public int W, H, OffX, OffY;
        }

        internal sealed class Sprite
        {
            public byte[] Mask = Array.Empty<byte>();
            public int W, H, OffX, OffY;
            public float WidthDip, HeightDip;
        }

        private readonly record struct GlyphKey(GlyphTypeface Face, ushort Glyph, float EmSize);
        private readonly record struct SpriteKey(string Text, IHevoTypeface Typeface, float EmSize);

        private const int MaxGlyphs = 4096;
        private const int MaxSprites = 1024;

        private readonly Dictionary<IHevoTypeface, FontEntry> _fonts = new();
        private readonly Dictionary<GlyphKey, GlyphMask> _glyphs = new();
        private readonly Dictionary<SpriteKey, Sprite> _sprites = new();

        // 渲染蒙版时复用的单元素数组(GlyphRun 构造要 IList)
        private readonly ushort[] _oneGlyph = new ushort[1];
        private readonly double[] _oneAdvance = new double[1];

        public bool Measure(string text, IHevoTypeface typeface, float fontSize, float scale, out RasterTextMeasure m)
        {
            m = default;
            if (string.IsNullOrEmpty(text) || fontSize <= 0) return false;
            var font = GetFont(typeface);
            float em = fontSize * scale;
            m.Font = font;
            m.EmDevice = em;

            if (font.Glyphs is { } face && TryMeasureGlyphs(font, face, text, out double advance))
            {
                m.Width = (float)(advance * fontSize);
                m.Height = (float)(font.LineSpacing * fontSize);
                return true;
            }

            var sprite = GetSprite(text, typeface, font, em, scale);
            if (sprite == null) return false;
            m.Sprite = sprite;
            m.Width = sprite.WidthDip;
            m.Height = sprite.HeightDip;
            return true;
        }

        /// <summary>把测量过的文字画到设备坐标 (deviceX, deviceY)(文字框左上角)。</summary>
        public void Draw(RasterSurface s, string text, in RasterTextMeasure m, float deviceX, float deviceY, uint color)
        {
            int x0 = (int)MathF.Round(deviceX), y0 = (int)MathF.Round(deviceY);
            if (m.Sprite is { } sp)
            {
                s.BlendMask(sp.Mask, sp.W, sp.H, x0 + sp.OffX, y0 + sp.OffY, color);
                return;
            }

            var font = m.Font;
            var face = font.Glyphs;
            if (face == null) return;
            int baseY = y0 + (int)MathF.Round((float)(font.Baseline * m.EmDevice));
            float penX = x0;
            for (int i = 0; i < text.Length; i++)
            {
                if (!TryGlyph(font, face, text[i], out ushort g, out double adv)) continue;
                var gm = GetGlyph(face, g, m.EmDevice);
                if (gm.W > 0) s.BlendMask(gm.Mask, gm.W, gm.H, (int)MathF.Round(penX) + gm.OffX, baseY + gm.OffY, color);
                penX += (float)(adv * m.EmDevice);
            }
        }

        private static bool TryMeasureGlyphs(FontEntry font, GlyphTypeface face, string text, out double advance)
        {
            advance = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (!TryGlyph(font, face, text[i], out _, out double adv)) return false;
                advance += adv;
            }
            return true;
        }

        private static bool TryGlyph(FontEntry font, GlyphTypeface face, char c, out ushort glyph, out double advance)
        {
            if (c < 128)
            {
                byte st = font.AsciiState[c];
                if (st == 0)
                {
                    if (face.CharacterToGlyphMap.TryGetValue(c, out var g0))
                    {
                        font.AsciiGlyph[c] = g0;
                        font.AsciiAdvance[c] = face.AdvanceWidths[g0];
                        st = 1;
                    }
                    else st = 2;
                    font.AsciiState[c] = st;
                }
                glyph = font.AsciiGlyph[c];
                advance = font.AsciiAdvance[c];
                return st == 1;
            }
            if (char.IsSurrogate(c) || !face.CharacterToGlyphMap.TryGetValue(c, out glyph))
            {
                glyph = 0; advance = 0;
                return false;
            }
            advance = face.AdvanceWidths[glyph];
            return true;
        }

        private GlyphMask GetGlyph(GlyphTypeface face, ushort glyph, float em)
        {
            var key = new GlyphKey(face, glyph, em);
            if (_glyphs.TryGetValue(key, out var gm)) return gm;
            if (_glyphs.Count >= MaxGlyphs) _glyphs.Clear();

            _oneGlyph[0] = glyph;
            _oneAdvance[0] = face.AdvanceWidths[glyph] * em;
            var probe = new GlyphRun(face, 0, false, em, 1.0f, _oneGlyph, new Point(0, 0), _oneAdvance,
                null, null, null, null, null, null);
            Rect ink = probe.ComputeInkBoundingBox();
            if (ink.IsEmpty || ink.Width <= 0 || ink.Height <= 0)
            {
                gm = new GlyphMask { Mask = Array.Empty<byte>() }; // 空格等无墨迹字形
                _glyphs[key] = gm;
                return gm;
            }

            int ox = (int)Math.Floor(ink.X) - 1, oy = (int)Math.Floor(ink.Y) - 1;
            int w = (int)Math.Ceiling(ink.Right) + 1 - ox, h = (int)Math.Ceiling(ink.Bottom) + 1 - oy;
            var run = new GlyphRun(face, 0, false, em, 1.0f, _oneGlyph, new Point(-ox, -oy), _oneAdvance,
                null, null, null, null, null, null);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawGlyphRun(Brushes.White, run);
            gm = new GlyphMask { Mask = RenderAlpha(dv, w, h), W = w, H = h, OffX = ox, OffY = oy };
            _glyphs[key] = gm;
            return gm;
        }

        private Sprite? GetSprite(string text, IHevoTypeface typeface, FontEntry font, float em, float scale)
        {
            var key = new SpriteKey(text, typeface, em);
            if (_sprites.TryGetValue(key, out var sp)) return sp;
            if (_sprites.Count >= MaxSprites) _sprites.Clear();

            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                font.Typeface, em, Brushes.White, 1.0);
            const int pad = 2;
            int w = (int)Math.Ceiling(Math.Max(ft.Width, ft.WidthIncludingTrailingWhitespace)) + pad * 2;
            int h = (int)Math.Ceiling(ft.Height) + pad * 2;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawText(ft, new Point(pad, pad));
            sp = new Sprite
            {
                Mask = RenderAlpha(dv, w, h), W = w, H = h, OffX = -pad, OffY = -pad,
                WidthDip = (float)(ft.Width / scale), HeightDip = (float)(ft.Height / scale),
            };
            _sprites[key] = sp;
            return sp;
        }

        private static byte[] RenderAlpha(DrawingVisual dv, int w, int h)
        {
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var px = new int[w * h];
            rtb.CopyPixels(px, w * 4, 0);
            var mask = new byte[w * h];
            for (int i = 0; i < px.Length; i++) mask[i] = (byte)((uint)px[i] >> 24);
            return mask;
        }

        private FontEntry GetFont(IHevoTypeface desc)
        {
            if (_fonts.TryGetValue(desc, out var fe)) return fe;

            // 跟 WpfDrawingRenderer.GetTypeface 同一套解析规则
            string familyName = "Arial";
            int weightVal = 400;
            bool isItalic = false;
            if (desc is HevoTypeface ht)
            {
                familyName = ht.FontFamily;
                weightVal = ht.FontWeight;
                isItalic = ht.IsItalic;
            }
            else if (desc is HevoResourceTypeface rtf)
            {
                var res = Application.Current?.TryFindResource(rtf.ResourceKey);
                if (res is FontFamily ff) familyName = ff.Source;
                else if (res is string str) familyName = str;
                weightVal = rtf.FontWeight;
                isItalic = rtf.IsItalic;
            }

            var family = new FontFamily(familyName);
            var tf = new Typeface(family, isItalic ? FontStyles.Italic : FontStyles.Normal,
                FontWeight.FromOpenTypeWeight(weightVal), FontStretches.Normal);
            fe = new FontEntry(tf)
            {
                Glyphs = tf.TryGetGlyphTypeface(out var gt) ? gt : null,
                LineSpacing = family.LineSpacing,
                Baseline = family.Baseline,
            };
            _fonts[desc] = fe;
            return fe;
        }

        public void Clear()
        {
            _fonts.Clear();
            _glyphs.Clear();
            _sprites.Clear();
        }
    }
}
