using System.Runtime.CompilerServices;

namespace Hevo.Charting.Renderers.Raster
{
    /// <summary>
    /// 光栅化用的颜色:预乘 BGRA32(跟 WPF <c>PixelFormats.Pbgra32</c> 同布局),可选两色线性渐变。
    /// 渐变参数已换算到设备像素:t = (x - OriginX) * DirX + (y - OriginY) * DirY,夹到 [0,1] 后在两色间插值。
    /// </summary>
    internal readonly struct RasterPaint
    {
        public readonly uint Color;
        public readonly bool IsGradient;
        public readonly uint Color1;
        public readonly float OriginX, OriginY, DirX, DirY;

        public RasterPaint(uint color)
        {
            Color = color; IsGradient = false; Color1 = 0; OriginX = OriginY = DirX = DirY = 0;
        }

        public RasterPaint(uint color0, uint color1, float originX, float originY, float dirX, float dirY)
        {
            Color = color0; Color1 = color1; IsGradient = true;
            OriginX = originX; OriginY = originY; DirX = dirX; DirY = dirY;
        }

        public bool IsVisible => IsGradient ? (Color | Color1) >> 24 != 0 : Color >> 24 != 0;
    }

    internal static class PixelMath
    {
        /// <summary>非预乘 ARGB + 额外不透明度 → 预乘 BGRA32。</summary>
        public static uint Premultiply(byte a, byte r, byte g, byte b, float opacity)
        {
            float fa = a * Math.Clamp(opacity, 0f, 1f);
            uint pa = (uint)(fa + 0.5f);
            if (pa == 0) return 0;
            float k = fa / 255f;
            uint pr = (uint)(r * k + 0.5f), pg = (uint)(g * k + 0.5f), pb = (uint)(b * k + 0.5f);
            return (pa << 24) | (pr << 16) | (pg << 8) | pb;
        }

        /// <summary>预乘色整体乘 m/255(四个通道一起缩放)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Scale(uint c, uint m)
        {
            uint rb = (c & 0x00FF00FFu) * m;
            uint ag = ((c >> 8) & 0x00FF00FFu) * m;
            rb = ((rb + ((rb >> 8) & 0x00FF00FFu) + 0x00800080u) >> 8) & 0x00FF00FFu;
            ag = ((ag + ((ag >> 8) & 0x00FF00FFu) + 0x00800080u) >> 8) & 0x00FF00FFu;
            return rb | (ag << 8);
        }

        /// <summary>预乘 src-over:dst = src + dst * (1 - srcA)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Over(uint dst, uint src)
        {
            uint a = src >> 24;
            if (a == 255) return src;
            if (a == 0) return dst;
            return src + Scale(dst, 255 - a);
        }

        /// <summary>两个预乘色按 t∈[0,256] 线性插值。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Lerp(uint c0, uint c1, uint t256)
        {
            uint inv = 256 - t256;
            uint rb = (((c0 & 0x00FF00FFu) * inv + (c1 & 0x00FF00FFu) * t256) >> 8) & 0x00FF00FFu;
            uint ag = ((((c0 >> 8) & 0x00FF00FFu) * inv + ((c1 >> 8) & 0x00FF00FFu) * t256) >> 8) & 0x00FF00FFu;
            return rb | (ag << 8);
        }

        /// <summary>
        /// 像素中心采样规则(跟 WPF EdgeMode.Aliased 一致):像素 i 的中心 i+0.5 落在 [a, b) 内才算覆盖。
        /// 返回第一个被覆盖的像素下标,即 ceil(a - 0.5)。坐标先夹到 ±2^24 防溢出。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CenterCeil(float v)
        {
            if (!(v > -16777216f)) return -16777216; // 含 NaN
            if (v > 16777216f) return 16777216;
            return (int)MathF.Ceiling(v - 0.5f);
        }
    }

    /// <summary>
    /// 预乘 BGRA32 像素目标。不持有内存,只绑定外部指针(WriteableBitmap.BackBuffer 等)。
    /// <list type="bullet">
    /// <item>所有写入都先按 <see cref="ClipX0"/>..<see cref="ClipX1"/>(半开区间)裁剪;</item>
    /// <item>记录本帧实际写过的像素范围(Dirty*):下一帧只清这一块,提交给 WPF 的脏区也只有这一块。</item>
    /// <item><see cref="Scale"/> 是 DIP → 设备像素比,渲染器把它乘进基础变换。</item>
    /// </list>
    /// </summary>
    internal unsafe sealed class RasterSurface
    {
        private uint* _px;
        private int _stride; // 以像素计

        public int Width { get; private set; }
        public int Height { get; private set; }
        public float Scale { get; private set; } = 1f;

        // 可见区域(位图容量可能比它大);ResetClip 回到这里
        private int _visibleW, _visibleH;

        public int ClipX0, ClipY0, ClipX1, ClipY1;
        public int DirtyX0, DirtyY0, DirtyX1, DirtyY1;

        public bool IsBound => _px != null;
        public bool HasDirty => DirtyX1 > DirtyX0 && DirtyY1 > DirtyY0;

        public void Bind(IntPtr pixels, int width, int height, int strideBytes, float scale)
        {
            _px = (uint*)pixels;
            Width = width; Height = height;
            _stride = strideBytes >> 2;
            Scale = scale > 0 ? scale : 1f;
            _visibleW = width; _visibleH = height;
            ResetClip();
            ResetDirty();
        }

        public void Unbind()
        {
            _px = null;
            Width = Height = 0;
        }

        /// <summary>限定可写区域为 [0, w) × [0, h)(不超过表面尺寸),并把裁剪框重置到这里。</summary>
        public void SetVisible(int w, int h)
        {
            _visibleW = Math.Min(w, Width);
            _visibleH = Math.Min(h, Height);
            ResetClip();
        }

        public void ResetClip()
        {
            ClipX0 = 0; ClipY0 = 0; ClipX1 = _visibleW; ClipY1 = _visibleH;
        }

        public void ResetDirty()
        {
            DirtyX0 = DirtyY0 = int.MaxValue;
            DirtyX1 = DirtyY1 = int.MinValue;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Touch(int x0, int y0, int x1, int y1)
        {
            if (x0 < DirtyX0) DirtyX0 = x0;
            if (y0 < DirtyY0) DirtyY0 = y0;
            if (x1 > DirtyX1) DirtyX1 = x1;
            if (y1 > DirtyY1) DirtyY1 = y1;
        }

        /// <summary>把一块像素清成全透明(不受裁剪框影响,只夹到表面尺寸)。</summary>
        public void ClearRect(int x0, int y0, int x1, int y1)
        {
            x0 = Math.Max(x0, 0); y0 = Math.Max(y0, 0);
            x1 = Math.Min(x1, Width); y1 = Math.Min(y1, Height);
            if (x1 <= x0 || y1 <= y0) return;
            int n = x1 - x0;
            for (int y = y0; y < y1; y++)
                new Span<uint>(_px + (long)y * _stride + x0, n).Clear();
        }

        public uint GetPixel(int x, int y) => _px[(long)y * _stride + x];

        /// <summary>纯色填一段像素 [x0, x1)(第 y 行),按裁剪框截断,预乘 src-over。</summary>
        public void FillSpan(int y, int x0, int x1, uint color)
        {
            if (y < ClipY0 || y >= ClipY1) return;
            if (x0 < ClipX0) x0 = ClipX0;
            if (x1 > ClipX1) x1 = ClipX1;
            if (x1 <= x0) return;
            Touch(x0, y, x1, y + 1);

            uint* row = _px + (long)y * _stride;
            uint a = color >> 24;
            if (a == 255)
            {
                new Span<uint>(row + x0, x1 - x0).Fill(color);
            }
            else
            {
                uint inv = 255 - a;
                for (int x = x0; x < x1; x++) row[x] = color + PixelMath.Scale(row[x], inv);
            }
        }

        public void FillSpan(int y, int x0, int x1, in RasterPaint paint)
        {
            if (!paint.IsGradient) { FillSpan(y, x0, x1, paint.Color); return; }
            if (y < ClipY0 || y >= ClipY1) return;
            if (x0 < ClipX0) x0 = ClipX0;
            if (x1 > ClipX1) x1 = ClipX1;
            if (x1 <= x0) return;
            Touch(x0, y, x1, y + 1);

            uint* row = _px + (long)y * _stride;
            float yc = y + 0.5f - paint.OriginY;
            float tRow = yc * paint.DirY;
            for (int x = x0; x < x1; x++)
            {
                float t = (x + 0.5f - paint.OriginX) * paint.DirX + tRow;
                uint t256 = t <= 0f ? 0u : t >= 1f ? 256u : (uint)(t * 256f);
                row[x] = PixelMath.Over(row[x], PixelMath.Lerp(paint.Color, paint.Color1, t256));
            }
        }

        /// <summary>按像素中心规则填一个设备坐标的轴对齐矩形 [left, right) × [top, bottom)。</summary>
        public void FillRect(float left, float top, float right, float bottom, in RasterPaint paint)
        {
            int x0 = PixelMath.CenterCeil(left), x1 = PixelMath.CenterCeil(right);
            int y0 = PixelMath.CenterCeil(top), y1 = PixelMath.CenterCeil(bottom);
            if (y0 < ClipY0) y0 = ClipY0;
            if (y1 > ClipY1) y1 = ClipY1;
            for (int y = y0; y < y1; y++) FillSpan(y, x0, x1, paint);
        }

        /// <summary>
        /// 用 8 位覆盖率蒙版(文字字形)乘以颜色后叠加。蒙版左上角放在 (dx, dy)。
        /// </summary>
        public void BlendMask(byte[] mask, int maskW, int maskH, int dx, int dy, uint color)
        {
            int sx0 = Math.Max(0, ClipX0 - dx), sy0 = Math.Max(0, ClipY0 - dy);
            int sx1 = Math.Min(maskW, ClipX1 - dx), sy1 = Math.Min(maskH, ClipY1 - dy);
            if (sx1 <= sx0 || sy1 <= sy0) return;
            Touch(dx + sx0, dy + sy0, dx + sx1, dy + sy1);

            for (int sy = sy0; sy < sy1; sy++)
            {
                uint* row = _px + (long)(dy + sy) * _stride + dx;
                int mOff = sy * maskW;
                for (int sx = sx0; sx < sx1; sx++)
                {
                    uint m = mask[mOff + sx];
                    if (m == 0) continue;
                    uint c = m == 255 ? color : PixelMath.Scale(color, m);
                    row[sx] = PixelMath.Over(row[sx], c);
                }
            }
        }

        /// <summary>
        /// 最近邻缩放贴一张预乘 BGRA32 图到设备矩形 [dx0, dx1) × [dy0, dy1),整体再乘 opacity(0..255)。
        /// </summary>
        public void BlitScaled(uint* src, int srcW, int srcH, int srcStridePx,
            float dLeft, float dTop, float dRight, float dBottom, uint opacity)
        {
            if (srcW <= 0 || srcH <= 0 || opacity == 0) return;
            int x0 = PixelMath.CenterCeil(dLeft), x1 = PixelMath.CenterCeil(dRight);
            int y0 = PixelMath.CenterCeil(dTop), y1 = PixelMath.CenterCeil(dBottom);
            if (x1 <= x0 || y1 <= y0) return;
            float sxStep = srcW / (dRight - dLeft), syStep = srcH / (dBottom - dTop);
            int cx0 = Math.Max(x0, ClipX0), cx1 = Math.Min(x1, ClipX1);
            int cy0 = Math.Max(y0, ClipY0), cy1 = Math.Min(y1, ClipY1);
            if (cx1 <= cx0 || cy1 <= cy0) return;
            Touch(cx0, cy0, cx1, cy1);

            for (int y = cy0; y < cy1; y++)
            {
                int sy = Math.Clamp((int)((y + 0.5f - dTop) * syStep), 0, srcH - 1);
                uint* srow = src + (long)sy * srcStridePx;
                uint* drow = _px + (long)y * _stride;
                for (int x = cx0; x < cx1; x++)
                {
                    int sx = Math.Clamp((int)((x + 0.5f - dLeft) * sxStep), 0, srcW - 1);
                    uint c = srow[sx];
                    if (opacity != 255) c = PixelMath.Scale(c, opacity);
                    drow[x] = PixelMath.Over(drow[x], c);
                }
            }
        }
    }
}
