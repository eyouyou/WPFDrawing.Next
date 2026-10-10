using Hevo.Charting.Core;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hevo.Charting.Renderers.Raster
{
    /// <summary>
    /// 一个位图模式图层的像素宿主:持有一张 <see cref="WriteableBitmap"/>,每帧把图层指令光栅化进去。
    /// <para>
    /// 图层的 DrawingVisual 里只挂 裁剪 + 一条 DrawImage(这张位图),不管图层画了多少条指令,
    /// WPF 侧每帧的 RenderData 都是这固定的三条。每次重画后都重新挂一次(而不是只靠 WriteableBitmap 自己通知),
    /// 保证外层画布开着 BitmapCache 时缓存也一定失效重画;裁剪几何体按可见尺寸缓存并冻结。
    /// </para>
    /// <para>
    /// 尺寸按"只增不减 + 余量,低于一半才收缩"分配,拖动窗口时不会每帧重建位图;
    /// 每帧只清上一帧画过的区域,只把 (上一帧 ∪ 本帧) 画过的区域提交给 WPF。
    /// 内存:每张位图 ≈ 容量宽 × 容量高 × 8 字节(WriteableBitmap 前后台各一份)。
    /// </para>
    /// </summary>
    internal sealed class BitmapLayerSurface : IDisposable
    {
        private const int Align = 64;

        private WriteableBitmap? _bitmap;
        private readonly RasterSurface _surface = new();
        private int _capW, _capH;      // 位图容量(设备像素)
        private int _visW, _visH;      // 本帧可见区域(设备像素)
        private float _scale = 1f;
        private int _prevX0, _prevY0, _prevX1, _prevY1; // 上一帧画过的区域
        private RectangleGeometry? _clip;
        private int _clipW, _clipH;
        private float _clipScale;

        /// <summary>图层 Visual 当前挂的是不是这张位图(切回矢量 / 首次进入位图模式时为 false)。</summary>
        public bool IsMounted { get; private set; }

        /// <summary>光栅化一帧到位图;随后调用方 RenderOpen 并调 <see cref="Mount"/> 挂上去。</summary>
        public void Render(LayerBuffer buffer, RasterRenderProvider provider, double dipWidth, double dipHeight, double scale)
        {
            float s = scale > 0 ? (float)scale : 1f;
            int pw = Math.Max(1, (int)Math.Ceiling(dipWidth * s));
            int ph = Math.Max(1, (int)Math.Ceiling(dipHeight * s));
            bool fresh = EnsureCapacity(pw, ph);
            _visW = pw; _visH = ph; _scale = s;

            var bmp = _bitmap;
            if (bmp == null) return;
            bmp.Lock();
            try
            {
                _surface.Bind(bmp.BackBuffer, _capW, _capH, bmp.BackBufferStride, s);
                if (!fresh) _surface.ClearRect(_prevX0, _prevY0, _prevX1, _prevY1);

                _surface.SetVisible(pw, ph);
                _surface.ResetDirty();
                buffer.Execute(provider, _surface);

                int x0 = _surface.DirtyX0, y0 = _surface.DirtyY0, x1 = _surface.DirtyX1, y1 = _surface.DirtyY1;
                bool drew = _surface.HasDirty;
                if (!drew) { x0 = y0 = int.MaxValue; x1 = y1 = int.MinValue; }

                // 提交给 WPF 的脏区 = 上一帧画过的(已清掉) ∪ 本帧画过的
                int ux0 = Math.Min(x0, _prevX0), uy0 = Math.Min(y0, _prevY0);
                int ux1 = Math.Max(x1, _prevX1), uy1 = Math.Max(y1, _prevY1);
                ux0 = Math.Max(ux0, 0); uy0 = Math.Max(uy0, 0);
                ux1 = Math.Min(ux1, _capW); uy1 = Math.Min(uy1, _capH);
                if (ux1 > ux0 && uy1 > uy0) bmp.AddDirtyRect(new Int32Rect(ux0, uy0, ux1 - ux0, uy1 - uy0));

                _prevX0 = x0; _prevY0 = y0; _prevX1 = x1; _prevY1 = y1;
            }
            finally
            {
                _surface.Unbind();
                bmp.Unlock();
            }

        }

        /// <summary>把位图挂进图层的 DrawingContext(调用方负责 RenderOpen / Dispose)。</summary>
        public void Mount(DrawingContext dc)
        {
            if (_bitmap == null) return;
            double inv = 1.0 / _scale;
            // 位图容量可能比可见区大(留了余量),裁到可见区,图层的内容边界才跟矢量模式一样
            if (_clip == null || _clipW != _visW || _clipH != _visH || _clipScale != _scale)
            {
                _clip = new RectangleGeometry(new Rect(0, 0, _visW * inv, _visH * inv));
                _clip.Freeze();
                _clipW = _visW; _clipH = _visH; _clipScale = _scale;
            }
            dc.PushClip(_clip);
            dc.DrawImage(_bitmap, new Rect(0, 0, _capW * inv, _capH * inv));
            dc.Pop();
            IsMounted = true;
        }

        /// <summary>
        /// 容量策略:放不下就按需求 +1/8 余量、对齐到 64 像素重建;需求不到容量一半时收缩。
        /// 返回 true 表示新建了位图(新位图像素全 0,不用清)。
        /// </summary>
        private bool EnsureCapacity(int w, int h)
        {
            bool fits = _bitmap != null && w <= _capW && h <= _capH;
            // 收缩条件用"新容量 ≤ 旧容量一半",小尺寸时对齐到 64 不会反复重建
            bool tooBig = _bitmap != null && (AlignUp(w + w / 8) * 2 <= _capW || AlignUp(h + h / 8) * 2 <= _capH);
            if (fits && !tooBig) return false;

            _capW = AlignUp(w + w / 8);
            _capH = AlignUp(h + h / 8);
            _bitmap = new WriteableBitmap(_capW, _capH, 96, 96, PixelFormats.Pbgra32, null);
            _prevX0 = _prevY0 = int.MaxValue;
            _prevX1 = _prevY1 = int.MinValue;
            IsMounted = false;
            return true;
        }

        private static int AlignUp(int v) => (v + Align - 1) / Align * Align;

        public void Dispose()
        {
            _bitmap = null;
            _clip = null;
            _capW = _capH = 0;
            IsMounted = false;
        }
    }
}
