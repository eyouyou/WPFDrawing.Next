using Hevo.Charting.Abstractions;

namespace Hevo.Charting.Renderers
{
    /// <summary>按像素列聚合后的一根 K 线:列号是物理像素列,OHLC 为列内首开、最高、最低、末收。</summary>
    public readonly record struct OhlcColumn(int Column, double Open, double High, double Low, double Close);

    /// <summary>
    /// 按像素列的 LOD(细节层次)工具。可见数据密到一个物理像素里挤了不止一个点时,
    /// 逐点画只是在同一列像素上反复覆盖,却让 WPF 为每个点建几何 / 绘制记录(2 万根可见时每帧上 MB 的 LOH 分配)。
    /// 这里把同一物理像素列里的点合并,输出量上限 ≈ 绘图区物理宽度,画面在 1px 精度下一致。
    /// 坐标约定:输入 X 是 DIP,列号 = floor(X × pixelsPerDip),即物理像素列。
    /// </summary>
    public static class PixelColumnLod
    {
        /// <summary>单根(相邻两个数据点)的物理宽度不足 1 像素时才聚合;≥1px 时走原逐根路径,行为不变。</summary>
        public static bool ShouldAggregate(double dipsPerUnit, double pixelsPerDip)
            => dipsPerUnit * pixelsPerDip < 1.0;

        /// <summary>DIP 坐标所在的物理像素列。</summary>
        public static int ColumnOf(double xDip, double pixelsPerDip) => (int)Math.Floor(xDip * pixelsPerDip);

        /// <summary>物理像素列 [column, column+1) 是否整列落在 [left, right](DIP)内。</summary>
        public static bool ColumnInside(int column, double pixelsPerDip, double left, double right)
            => column / pixelsPerDip >= left && (column + 1) / pixelsPerDip <= right;

        /// <summary>物理像素列中心的 DIP 坐标。</summary>
        public static double ColumnCenter(int column, double pixelsPerDip) => (column + 0.5) / pixelsPerDip;

        /// <summary>
        /// M4 抽稀:<paramref name="input"/> 按 X 单调递增,同一物理像素列内只保留首点、Y 最小、Y 最大、末点(按原顺序),
        /// 折线在该列的竖向跨度和进出点都不变。结果追加到 <paramref name="output"/>,不分配。
        /// </summary>
        public static void DecimatePolyline(List<HevoPoint> input, List<HevoPoint> output, double pixelsPerDip)
        {
            int n = input.Count;
            int i = 0;
            while (i < n)
            {
                int column = ColumnOf(input[i].X, pixelsPerDip);
                int first = i, minIdx = i, maxIdx = i, last = i;
                for (i++; i < n && ColumnOf(input[i].X, pixelsPerDip) == column; i++)
                {
                    if (input[i].Y < input[minIdx].Y) minIdx = i;
                    if (input[i].Y > input[maxIdx].Y) maxIdx = i;
                    last = i;
                }

                output.Add(input[first]);
                int lo = Math.Min(minIdx, maxIdx), hi = Math.Max(minIdx, maxIdx);
                if (lo != first && lo != last) output.Add(input[lo]);
                if (hi != first && hi != last && hi != lo) output.Add(input[hi]);
                if (last != first) output.Add(input[last]);
            }
        }
    }

    /// <summary>
    /// 逐根喂入 (列号, OHLC),列号变化时吐出上一列的聚合结果。列号须单调(按 X 顺序喂)。
    /// 值类型、无分配,放在 Layer 的 OnUpdate 局部变量里用。
    /// </summary>
    public struct OhlcColumnAccumulator
    {
        private OhlcColumn _current;
        private bool _has;

        /// <summary>喂一根。若这一根开了新列且之前有未吐出的列,返回 true 并经 <paramref name="completed"/> 给出上一列。</summary>
        public bool Add(int column, double open, double high, double low, double close, out OhlcColumn completed)
        {
            if (_has && column == _current.Column)
            {
                _current = _current with
                {
                    High = Math.Max(_current.High, high),
                    Low = Math.Min(_current.Low, low),
                    Close = close,
                };
                completed = default;
                return false;
            }

            bool emitted = _has;
            completed = _current;
            _current = new OhlcColumn(column, open, high, low, close);
            _has = true;
            return emitted;
        }

        /// <summary>喂完后取最后一列。</summary>
        public bool Flush(out OhlcColumn completed)
        {
            completed = _current;
            bool had = _has;
            _has = false;
            return had;
        }
    }
}
