using System.Numerics;

namespace Hevo.Charting.Renderers.Raster
{
    /// <summary>线端样式,数值跟 WPF <c>PenLineCap</c> 对齐(HevoPen.LineCap 直接强转)。</summary>
    internal enum StrokeCap : byte { Flat = 0, Square = 1, Round = 2, Triangle = 3 }

    /// <summary>拐角样式,数值跟 WPF <c>PenLineJoin</c> 对齐。</summary>
    internal enum StrokeJoin : byte { Miter = 0, Bevel = 1, Round = 2 }

    /// <summary>描边参数(设备像素)。虚线长度 = DashArray[i] × Width,跟 WPF DashStyle 的语义一致。</summary>
    internal readonly struct StrokeStyle
    {
        public readonly float Width;
        public readonly StrokeCap StartCap;
        public readonly StrokeCap EndCap;
        public readonly StrokeCap DashCap;
        public readonly StrokeJoin Join;
        public readonly float MiterLimit;
        public readonly double[]? DashArray;

        public StrokeStyle(float width, StrokeCap startCap, StrokeCap endCap, StrokeJoin join,
            double[]? dashArray = null, StrokeCap dashCap = StrokeCap.Square, float miterLimit = 10f)
        {
            Width = width; StartCap = startCap; EndCap = endCap; Join = join;
            DashArray = dashArray; DashCap = dashCap; MiterLimit = miterLimit;
        }

        public bool IsDashed => DashArray is { Length: > 0 };
    }

    /// <summary>
    /// 把折线描边展开成多边形(每段一个四边形 + 拐角 + 线端),全部强制成同一方向后交给
    /// <see cref="ScanlineFiller"/> 按非零规则合并填充。虚线先按 DashArray 切成若干段再逐段展开。
    /// 内部点缓冲跨帧复用,稳态 0 分配。
    /// </summary>
    internal sealed class Stroker
    {
        private Vector2[] _clean = new Vector2[256];
        private Vector2[] _dash = new Vector2[256];
        private readonly Vector2[] _shape = new Vector2[130];

        /// <summary>描一条折线(设备坐标)。closed = 首尾相连(无线端,首点也做拐角)。</summary>
        public void Stroke(ScanlineFiller f, ReadOnlySpan<Vector2> pts, bool closed, in StrokeStyle style)
        {
            if (style.Width <= 0 || pts.Length == 0) return;

            // 去掉连续重复点(零长度段没有方向,会让法线变 NaN)
            if (_clean.Length < pts.Length + 1) _clean = new Vector2[Math.Max(pts.Length + 1, _clean.Length * 2)];
            int n = 0;
            for (int i = 0; i < pts.Length; i++)
            {
                var p = pts[i];
                if (!float.IsFinite(p.X) || !float.IsFinite(p.Y)) continue;
                if (n > 0 && _clean[n - 1] == p) continue;
                _clean[n++] = p;
            }
            if (closed && n > 1 && _clean[n - 1] == _clean[0]) n--;
            if (n < 2)
            {
                // 单点:圆 / 方线端会画出一个点,平头什么都不画(跟 WPF 一致)
                if (n == 1 && !closed) Dot(f, _clean[0], style.Width * 0.5f, style.StartCap);
                return;
            }

            if (style.DashArray is not { Length: > 0 } dashes)
            {
                StrokeSolid(f, new ReadOnlySpan<Vector2>(_clean, 0, n), closed, style.Width * 0.5f,
                    style.StartCap, style.EndCap, style.Join, style.MiterLimit);
                return;
            }

            StrokeDashed(f, n, closed, style, dashes);
        }

        private void StrokeDashed(ScanlineFiller f, int n, bool closed, in StrokeStyle style, double[] dashes)
        {
            float total = 0;
            for (int i = 0; i < dashes.Length; i++) total += (float)Math.Max(0, dashes[i]) * style.Width;
            if (total <= 0.01f)
            {
                StrokeSolid(f, new ReadOnlySpan<Vector2>(_clean, 0, n), closed, style.Width * 0.5f,
                    style.StartCap, style.EndCap, style.Join, style.MiterLimit);
                return;
            }

            float h = style.Width * 0.5f;
            int segCount = closed ? n : n - 1;
            int di = 0;
            float remain = (float)Math.Max(0, dashes[0]) * style.Width;
            bool on = true;
            int dn = 0;
            bool dashStartsAtLineStart = true;
            if (_dash.Length < n + 2) _dash = new Vector2[Math.Max(n + 2, _dash.Length * 2)];

            for (int s = 0; s < segCount; s++)
            {
                var a = _clean[s];
                var b = _clean[(s + 1) % n];
                float len = Vector2.Distance(a, b);
                float pos = 0;
                while (pos < len)
                {
                    float step = Math.Min(remain, len - pos);
                    var p0 = Vector2.Lerp(a, b, pos / len);
                    var p1 = Vector2.Lerp(a, b, (pos + step) / len);
                    if (on)
                    {
                        if (dn == 0) _dash[dn++] = p0;
                        if (dn == _dash.Length) Array.Resize(ref _dash, dn * 2);
                        _dash[dn++] = p1;
                    }
                    pos += step;
                    remain -= step;
                    if (remain <= 1e-4f)
                    {
                        if (on && dn >= 2)
                        {
                            var startCap = dashStartsAtLineStart && !closed ? style.StartCap : style.DashCap;
                            StrokeSolid(f, new ReadOnlySpan<Vector2>(_dash, 0, dn), false, h,
                                startCap, style.DashCap, style.Join, style.MiterLimit);
                        }
                        dn = 0;
                        dashStartsAtLineStart = false;
                        on = !on;
                        di = (di + 1) % dashes.Length;
                        remain = (float)Math.Max(0, dashes[di]) * style.Width;
                    }
                }
            }
            if (on && dn >= 2)
            {
                var startCap = dashStartsAtLineStart && !closed ? style.StartCap : style.DashCap;
                StrokeSolid(f, new ReadOnlySpan<Vector2>(_dash, 0, dn), false, h,
                    startCap, closed ? style.DashCap : style.EndCap, style.Join, style.MiterLimit);
            }
        }

        private void StrokeSolid(ScanlineFiller f, ReadOnlySpan<Vector2> p, bool closed, float h,
            StrokeCap startCap, StrokeCap endCap, StrokeJoin join, float miterLimit)
        {
            int n = p.Length;
            int segCount = closed ? n : n - 1;
            Span<Vector2> quad = stackalloc Vector2[4];

            for (int s = 0; s < segCount; s++)
            {
                var a = p[s];
                var b = p[(s + 1) % n];
                var d = Vector2.Normalize(b - a);
                var nrm = new Vector2(-d.Y, d.X) * h;
                if (!closed)
                {
                    if (s == 0 && startCap == StrokeCap.Square) a -= d * h;
                    if (s == segCount - 1 && endCap == StrokeCap.Square) b += d * h;
                }
                quad[0] = a + nrm; quad[1] = b + nrm; quad[2] = b - nrm; quad[3] = a - nrm;
                f.AddPolygon(quad, +1);
            }

            // 拐角:只补外侧缺口(内侧被相邻两段的四边形覆盖)
            int jStart = closed ? 0 : 1, jEnd = closed ? n : n - 1;
            for (int j = jStart; j < jEnd; j++)
            {
                var prev = p[(j - 1 + n) % n];
                var cur = p[j];
                var next = p[(j + 1) % n];
                AddJoin(f, prev, cur, next, h, join, miterLimit);
            }

            if (!closed)
            {
                AddCap(f, p[0], Vector2.Normalize(p[0] - p[1]), h, startCap);
                AddCap(f, p[n - 1], Vector2.Normalize(p[n - 1] - p[n - 2]), h, endCap);
            }
        }

        private void AddJoin(ScanlineFiller f, Vector2 prev, Vector2 cur, Vector2 next, float h, StrokeJoin join, float miterLimit)
        {
            var da = Vector2.Normalize(cur - prev);
            var db = Vector2.Normalize(next - cur);
            float cross = da.X * db.Y - da.Y * db.X;
            float dot = Vector2.Dot(da, db);
            if (MathF.Abs(cross) < 1e-6f && dot > 0) return; // 共线,无缺口

            if (join == StrokeJoin.Round)
            {
                AddCircle(f, cur, h);
                return;
            }

            float side = cross > 0 ? -1f : 1f; // 外侧
            var na = new Vector2(-da.Y, da.X) * (h * side);
            var nb = new Vector2(-db.Y, db.X) * (h * side);

            Span<Vector2> poly = stackalloc Vector2[4];
            if (join == StrokeJoin.Miter)
            {
                var sum = na + nb;
                float len = sum.Length();
                if (len > 1e-6f)
                {
                    var u = sum / len;
                    float cosHalf = Vector2.Dot(u, na) / h;
                    if (cosHalf > 1e-6f && 1f / cosHalf <= miterLimit)
                    {
                        poly[0] = cur; poly[1] = cur + na; poly[2] = cur + u * (h / cosHalf); poly[3] = cur + nb;
                        f.AddPolygon(poly, +1);
                        return;
                    }
                }
            }
            // Bevel(以及超出斜接上限的 Miter)
            poly[0] = cur; poly[1] = cur + na; poly[2] = cur + nb;
            f.AddPolygon(poly.Slice(0, 3), +1);
        }

        private void AddCap(ScanlineFiller f, Vector2 p, Vector2 outward, float h, StrokeCap cap)
        {
            switch (cap)
            {
                case StrokeCap.Round:
                    AddCircle(f, p, h);
                    break;
                case StrokeCap.Triangle:
                {
                    var nrm = new Vector2(-outward.Y, outward.X) * h;
                    Span<Vector2> tri = stackalloc Vector2[3];
                    tri[0] = p + nrm; tri[1] = p + outward * h; tri[2] = p - nrm;
                    f.AddPolygon(tri, +1);
                    break;
                }
                // Flat:无;Square:已在首末段四边形里外延
            }
        }

        private void Dot(ScanlineFiller f, Vector2 p, float h, StrokeCap cap)
        {
            if (cap == StrokeCap.Round) AddCircle(f, p, h);
            else if (cap == StrokeCap.Square)
            {
                Span<Vector2> q = stackalloc Vector2[4];
                q[0] = new(p.X - h, p.Y - h); q[1] = new(p.X + h, p.Y - h);
                q[2] = new(p.X + h, p.Y + h); q[3] = new(p.X - h, p.Y + h);
                f.AddPolygon(q, +1);
            }
        }

        /// <summary>圆 / 椭圆展开成多边形(段数按周长取,8..128)。</summary>
        public void AddEllipse(ScanlineFiller f, Vector2 c, float rx, float ry, int orientation)
        {
            if (rx <= 0 || ry <= 0) return;
            int segs = EllipseSegments(rx, ry);
            var pts = new Span<Vector2>(_shape, 0, segs);
            for (int i = 0; i < segs; i++)
            {
                float t = i * (MathF.PI * 2f / segs);
                pts[i] = new Vector2(c.X + rx * MathF.Cos(t), c.Y + ry * MathF.Sin(t));
            }
            f.AddPolygon(pts, orientation);
        }

        private void AddCircle(ScanlineFiller f, Vector2 c, float r) => AddEllipse(f, c, r, r, +1);

        internal static int EllipseSegments(float rx, float ry)
            => Math.Clamp((int)MathF.Ceiling(MathF.PI * (rx + ry) / 2f), 8, 128);
    }
}
