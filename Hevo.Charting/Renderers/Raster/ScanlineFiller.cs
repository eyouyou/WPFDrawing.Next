using System.Numerics;

namespace Hevo.Charting.Renderers.Raster
{
    /// <summary>
    /// 多边形扫描线填充(无抗锯齿,像素中心采样,跟 WPF EdgeMode.Aliased 的覆盖规则一致)。
    /// <para>
    /// 用法:<see cref="Reset"/> → 若干次 <see cref="AddEdge"/> / <see cref="AddPolygon"/> → <see cref="Fill"/>。
    /// 一次 Fill 内所有多边形按非零环绕(或奇偶)合并成一个形状,每个像素只写一次,
    /// 所以半透明折线在拐角 / 自交处不会叠深色。
    /// </para>
    /// <para>边表、活动边表、交点表都是跨帧复用的数组,只增不减;稳态下 0 分配。</para>
    /// </summary>
    internal sealed class ScanlineFiller
    {
        private struct Edge
        {
            public float X0;    // 上端点 x
            public float Y0;    // 上端点 y
            public float Y1;    // 下端点 y
            public float Slope; // dx/dy
            public int Dir;     // 原方向向下 +1,向上 -1
        }

        private Edge[] _edges = new Edge[256];
        private float[] _keys = new float[256];
        private int _count;
        private int[] _active = new int[64];
        private float[] _xs = new float[64];
        private int[] _ds = new int[64];
        private float _minY, _maxY;

        public ScanlineFiller() => Reset();

        public bool IsEmpty => _count == 0;

        public void Reset()
        {
            _count = 0;
            _minY = float.MaxValue;
            _maxY = float.MinValue;
        }

        public void AddEdge(float x0, float y0, float x1, float y1)
        {
            if (y0 == y1) return;
            if (!float.IsFinite(x0) || !float.IsFinite(y0) || !float.IsFinite(x1) || !float.IsFinite(y1)) return;
            int dir = 1;
            if (y0 > y1)
            {
                (x0, x1) = (x1, x0);
                (y0, y1) = (y1, y0);
                dir = -1;
            }
            if (_count == _edges.Length)
            {
                Array.Resize(ref _edges, _count * 2);
                Array.Resize(ref _keys, _count * 2);
            }
            ref var e = ref _edges[_count];
            e.X0 = x0; e.Y0 = y0; e.Y1 = y1; e.Dir = dir;
            e.Slope = (x1 - x0) / (y1 - y0);
            _keys[_count] = y0;
            _count++;
            if (y0 < _minY) _minY = y0;
            if (y1 > _maxY) _maxY = y1;
        }

        /// <summary>
        /// 加一个闭合多边形(自动首尾相连)。
        /// orientation:0 = 原样;+1 / -1 = 强制成正 / 负有向面积(必要时倒序加边)。
        /// 描边把各段四边形、拐角三角形都强制成同一方向再非零合并,得到并集而不是互相抵消;
        /// 环形(矩形 / 椭圆描边)用外圈 +1、内圈 -1 挖空。
        /// </summary>
        public void AddPolygon(ReadOnlySpan<Vector2> pts, int orientation = 0)
        {
            int n = pts.Length;
            if (n < 3) return;
            bool reverse = false;
            if (orientation != 0)
            {
                float area = 0;
                for (int i = 0, j = n - 1; i < n; j = i++)
                    area += (pts[j].X * pts[i].Y) - (pts[i].X * pts[j].Y);
                if (area == 0) return;
                reverse = (area > 0) != (orientation > 0);
            }
            for (int i = 0; i < n; i++)
            {
                var a = pts[i];
                var b = pts[i + 1 == n ? 0 : i + 1];
                if (reverse) AddEdge(b.X, b.Y, a.X, a.Y);
                else AddEdge(a.X, a.Y, b.X, b.Y);
            }
        }

        public void Fill(RasterSurface s, in RasterPaint paint, bool evenOdd = false)
        {
            if (_count == 0) return;
            Array.Sort(_keys, _edges, 0, _count);

            int yStart = Math.Max(PixelMath.CenterCeil(_minY), s.ClipY0);
            int yEnd = Math.Min(PixelMath.CenterCeil(_maxY), s.ClipY1);
            int next = 0, activeCount = 0;

            for (int y = yStart; y < yEnd; y++)
            {
                float yc = y + 0.5f;

                // 1. 移除已经结束的边
                int w = 0;
                for (int i = 0; i < activeCount; i++)
                {
                    int ei = _active[i];
                    if (_edges[ei].Y1 > yc) _active[w++] = ei;
                }
                activeCount = w;

                // 2. 加入这一行开始覆盖的边(上端点 <= 采样线 < 下端点)
                while (next < _count && _edges[next].Y0 <= yc)
                {
                    if (_edges[next].Y1 > yc)
                    {
                        if (activeCount == _active.Length) Array.Resize(ref _active, activeCount * 2);
                        _active[activeCount++] = next;
                    }
                    next++;
                }
                if (activeCount == 0)
                {
                    if (next >= _count) break;
                    continue;
                }

                // 3. 求交点并按 x 插入排序(每行交点通常很少)
                if (_xs.Length < activeCount)
                {
                    Array.Resize(ref _xs, _active.Length);
                    Array.Resize(ref _ds, _active.Length);
                }
                for (int i = 0; i < activeCount; i++)
                {
                    ref var e = ref _edges[_active[i]];
                    float x = e.X0 + (yc - e.Y0) * e.Slope;
                    int d = e.Dir;
                    int j = i - 1;
                    while (j >= 0 && _xs[j] > x)
                    {
                        _xs[j + 1] = _xs[j];
                        _ds[j + 1] = _ds[j];
                        j--;
                    }
                    _xs[j + 1] = x;
                    _ds[j + 1] = d;
                }

                // 4. 按环绕规则输出区间
                int winding = 0;
                float spanStart = 0;
                for (int i = 0; i < activeCount; i++)
                {
                    bool wasInside = evenOdd ? (winding & 1) != 0 : winding != 0;
                    winding += _ds[i];
                    bool inside = evenOdd ? (winding & 1) != 0 : winding != 0;
                    if (!wasInside && inside) spanStart = _xs[i];
                    else if (wasInside && !inside)
                        s.FillSpan(y, PixelMath.CenterCeil(spanStart), PixelMath.CenterCeil(_xs[i]), paint);
                }
            }
        }
    }
}
