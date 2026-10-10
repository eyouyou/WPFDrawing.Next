using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using Hevo.Charting.Renderers.Raster;
using Xunit;
using Xunit.Abstractions;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 位图后端(RenderMode.Bitmap):
    /// ① 光栅核心的像素规则(像素中心采样、非零合并、虚线、裁剪、脏区);
    /// ② 同一套 DrawCmd 分别走 WPF 矢量和位图,在真 ChartCell 里用 RenderTargetBitmap 截图逐像素对比;
    /// ③ 策略切换只重新上屏、不重录图层;位图模式上屏不产生托管分配。
    /// </summary>
    public sealed class RasterRendererTests
    {
        private readonly ITestOutputHelper _out;
        public RasterRendererTests(ITestOutputHelper output) => _out = output;

        private static readonly uint Red = PixelMath.Premultiply(255, 255, 0, 0, 1f);
        private static readonly uint HalfBlue = PixelMath.Premultiply(128, 0, 0, 255, 1f);

        // ====================================================================
        // ① 光栅核心
        // ====================================================================

        private sealed class Canvas : IDisposable
        {
            public readonly int W, H;
            public readonly uint[] Px;
            private System.Runtime.InteropServices.GCHandle _pin;
            public readonly RasterSurface S = new();

            public Canvas(int w, int h)
            {
                W = w; H = h; Px = new uint[w * h];
                _pin = System.Runtime.InteropServices.GCHandle.Alloc(Px, System.Runtime.InteropServices.GCHandleType.Pinned);
                S.Bind(_pin.AddrOfPinnedObject(), w, h, w * 4, 1f);
            }

            public uint this[int x, int y] => Px[y * W + x];
            public int Count(Func<uint, bool> pred) { int n = 0; foreach (var p in Px) if (pred(p)) n++; return n; }
            public void Dispose() { S.Unbind(); _pin.Free(); }
        }

        [Fact]
        public void HorizontalLine_CoversExactlyOneRow()
        {
            using var c = new Canvas(40, 20);
            // PixelSnap 之后 1px 水平线中心在 y=10.5 → 覆盖 [10, 11) 这一行
            c.S.FillRect(0, 10, 20, 11, new RasterPaint(Red));
            Assert.Equal(20, c.Count(p => p != 0));
            Assert.Equal(Red, c[0, 10]);
            Assert.Equal(Red, c[19, 10]);
            Assert.Equal(0u, c[20, 10]);
        }

        [Fact]
        public void Rect_UsesPixelCenterSampling()
        {
            using var c = new Canvas(40, 20);
            // [10.3, 20.7):像素 10..20 的中心 10.5..20.5 都落在里面 → 11 列
            c.S.FillRect(10.3f, 5f, 20.7f, 6f, new RasterPaint(Red));
            Assert.Equal(11, c.Count(p => p != 0));
            Assert.Equal(0u, c[9, 5]);
            Assert.Equal(Red, c[10, 5]);
            Assert.Equal(Red, c[20, 5]);
            Assert.Equal(0u, c[21, 5]);
        }

        [Fact]
        public void SemiTransparentPolyline_CoversEachPixelOnce()
        {
            using var c = new Canvas(120, 80);
            var f = new ScanlineFiller();
            var st = new Stroker();
            var pts = new[] { new Vector2(10.5f, 10.5f), new Vector2(60.5f, 10.5f), new Vector2(60.5f, 60.5f), new Vector2(15.5f, 20.5f) };
            st.Stroke(f, pts, false, new StrokeStyle(5f, StrokeCap.Flat, StrokeCap.Flat, StrokeJoin.Miter));
            f.Fill(c.S, new RasterPaint(HalfBlue));
            uint maxA = 0;
            foreach (var p in c.Px) maxA = Math.Max(maxA, p >> 24);
            Assert.Equal(128u, maxA); // 拐角 / 自交处不叠深色
            Assert.NotEqual(0u, c[62, 8]); // 斜接尖角补上了
        }

        [Fact]
        public void Dash_4_4_HasSquareDashCaps_LikeWpfPen()
        {
            using var c = new Canvas(40, 10);
            var f = new ScanlineFiller();
            new Stroker().Stroke(f, new[] { new Vector2(0f, 5.5f), new Vector2(40f, 5.5f) }, false,
                new StrokeStyle(1f, StrokeCap.Flat, StrokeCap.Flat, StrokeJoin.Miter, new double[] { 4, 4 }));
            f.Fill(c.S, new RasterPaint(Red));
            var row = new char[40];
            for (int x = 0; x < 40; x++) row[x] = c[x, 5] != 0 ? '#' : '.';
            // 首段起点用 StartCap(Flat),之后每段两端是 WPF 默认的 DashCap=Square:4 → 5 像素
            Assert.StartsWith("####...#####...#####", new string(row));
        }

        [Fact]
        public void Circle_IsSymmetric_AndAreaMatches()
        {
            using var c = new Canvas(80, 80);
            var f = new ScanlineFiller();
            new Stroker().AddEllipse(f, new Vector2(40, 40), 20, 20, +1);
            f.Fill(c.S, new RasterPaint(Red));
            for (int y = 0; y < 80; y++)
                for (int x = 0; x < 80; x++)
                {
                    Assert.Equal(c[x, y] != 0, c[79 - x, y] != 0);
                    Assert.Equal(c[x, y] != 0, c[x, 79 - y] != 0);
                }
            Assert.InRange(c.Count(p => p != 0), Math.PI * 400 - 40, Math.PI * 400 + 40);
        }

        [Fact]
        public void Clip_And_DirtyBounds()
        {
            using var c = new Canvas(120, 80);
            c.S.ClipX1 = 100; c.S.ClipY1 = 50;
            var f = new ScanlineFiller();
            new Stroker().Stroke(f, new[] { new Vector2(10f, 20f), new Vector2(50f, 70f), new Vector2(110f, 15f) }, false,
                new StrokeStyle(8f, StrokeCap.Round, StrokeCap.Round, StrokeJoin.Round));
            f.Fill(c.S, new RasterPaint(Red));
            for (int y = 0; y < 80; y++)
                for (int x = 0; x < 120; x++)
                    if (x >= 100 || y >= 50) Assert.Equal(0u, c[x, y]);
            Assert.True(c.S.HasDirty);
            Assert.True(c.S.DirtyX1 <= 100 && c.S.DirtyY1 <= 50);
        }

        [Fact]
        public void PremultipliedOver_IsCorrect()
        {
            uint a = PixelMath.Over(0xFFFFFFFF, HalfBlue);
            Assert.Equal(0xFFu, a >> 24);
            Assert.Equal(0xFFu, a & 0xFF);
            Assert.InRange((a >> 16) & 0xFF, 126u, 128u);
        }

        // ====================================================================
        // ② 跟 WPF 矢量路径逐像素对比
        // ====================================================================

        private sealed class ScriptLayer : ChartLayer
        {
            private readonly Action<IDrawingSink> _draw;
            public ScriptLayer(Action<IDrawingSink> draw, ChartLayerType level = ChartLayerType.Main)
            {
                _draw = draw;
                Level = level;
                Name = "script";
            }
            protected override void OnUpdate(IVisualData data, IDrawingSink drawSink, WidgetBuffer widgetSink) => _draw(drawSink);
        }

        private sealed class NoData : IVisualData
        {
            public static readonly NoData Instance = new();
            public T? Get<T>() where T : class => null;
            public void Publish<T>(T snapshot) where T : class { }
        }

        private const int W = 240, H = 160;

        private static ChartCell NewCell()
        {
            // ChartCell 只在有 ChartTemplate 时才把内部画布挂进可视树(生产里是 schema);
            // 不挂模板的话画布没有布局尺寸,位图路径会按"还没布局"回退矢量。这里给一个只放 ContentPresenter 的空模板。
            var cell = new ChartCell
            {
                Template = new ChartTemplate { VisualTree = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter)) },
            };
            cell.Measure(new Size(W, H));
            cell.Arrange(new Rect(0, 0, W, H));
            cell.UpdateLayout();
            Assert.True(cell.DrawingCanvas.ActualWidth > 0, "测试用 ChartCell 没有完成布局");
            return cell;
        }

        /// <summary>录一次指令并上屏(生产路径:Update → SwapBuffer → RenderWpfLayers → PostRender)。</summary>
        private static void Record(ChartCell cell, ChartLayer layer)
        {
            layer.MarkDirty();
            layer.Update(NoData.Instance);
            cell.Invalidate();
        }

        private static uint[] Capture(Visual v)
        {
            var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(v);
            var px = new int[W * H];
            rtb.CopyPixels(px, W * 4, 0);
            var u = new uint[px.Length];
            Buffer.BlockCopy(px, 0, u, 0, px.Length * 4);
            return u;
        }

        private (uint[] Wpf, uint[] Bmp) RenderBoth(Action<IDrawingSink> draw)
        {
            var cell = NewCell();
            var layer = new ScriptLayer(draw);
            cell.AddUnmanagedLayer(layer);
            Record(cell, layer);
            var wpf = Capture(cell);

            layer.Mode = RenderMode.Bitmap;
            cell.Invalidate(); // 不脏,只因策略变了重新上屏
            Assert.True(layer.BitmapSurface is { IsMounted: true });
            var bmp = Capture(cell);
            return (wpf, bmp);
        }

        private static readonly HevoSolidBrush RedBrush = new(Colors.Red);
        private static readonly HevoSolidBrush GreenBrush = new(Color.FromRgb(0, 160, 0));
        private static readonly HevoPen GridPen = new(new HevoSolidBrush(Color.FromRgb(90, 90, 90)), 1);
        private static readonly HevoPen Pen2 = new(new HevoSolidBrush(Colors.Navy), 2);
        private static readonly HevoPen WickPen = new(new HevoSolidBrush(Colors.Black), 1);

        private static void AxisAlignedScene(IDrawingSink d)
        {
            for (int i = 1; i < 4; i++)
            {
                d.DrawLine(GridPen, new HevoPoint(10, 30 * i + 0.3f), new HevoPoint(230, 30 * i + 0.3f));
                d.DrawLine(GridPen, new HevoPoint(55.6f * i, 5), new HevoPoint(55.6f * i, 150));
            }
            var ups = new List<HevoRect>();
            var downs = new List<HevoRect>();
            var wicks = new List<HevoPoint>();
            for (int i = 0; i < 30; i++)
            {
                float x = 12.3f + i * 7.1f;
                float top = 40 + (i * 37 % 50), h = 6 + (i * 13 % 30);
                (i % 3 == 0 ? downs : ups).Add(new HevoRect(x, top, 4.6f, h));
                wicks.Add(new HevoPoint(x + 2.3f, top - 8));
                wicks.Add(new HevoPoint(x + 2.3f, top + h + 8));
            }
            using (d.PushClip(new HevoRect(5, 5, 225, 140)))
            {
                d.DrawLineSegments(WickPen, wicks);
                d.DrawRectangles(RedBrush, null, ups);
                d.DrawRectangles(GreenBrush, null, downs);
            }
            d.DrawRectangle(null, Pen2, new HevoRect(30.2f, 110.7f, 60, 30));
            d.DrawRectangle(GreenBrush, GridPen, new HevoRect(150.4f, 112.2f, 50.5f, 25.5f));
        }

        private static int CountDiff(uint[] a, uint[] b, out int covered)
        {
            int diff = 0;
            covered = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != 0 || b[i] != 0) covered++;
                if (a[i] != b[i]) diff++;
            }
            return diff;
        }

        [Fact]
        public void AxisAlignedScene_MatchesWpfPixelForPixel() => IncrementalTestKit.RunOnSta(() =>
        {
            var (wpf, bmp) = RenderBoth(AxisAlignedScene);
            int diff = CountDiff(wpf, bmp, out int covered);
            _out.WriteLine($"轴对齐场景:覆盖 {covered} 像素,不同 {diff}");
            Assert.True(covered > 2000, "场景没画出来");
            Assert.Equal(0, diff);
        });

        [Fact]
        public void MixedScene_CloseToWpf() => IncrementalTestKit.RunOnSta(() =>
        {
            var line = new List<HevoPoint>();
            for (int i = 0; i < 200; i++) line.Add(new HevoPoint(10 + i * 1.1f, 80 + 50 * MathF.Sin(i / 15f)));
            var linePen = new HevoPen(new HevoSolidBrush(Colors.DarkOrange), 1.5);
            var dashPen = new HevoPen(new HevoSolidBrush(Colors.Gray), 1, new double[] { 4, 4 });
            var (wpf, bmp) = RenderBoth(d =>
            {
                d.DrawPolyline(linePen, line);
                d.DrawLine(dashPen, new HevoPoint(10, 20), new HevoPoint(230, 20));
                d.DrawLine(dashPen, new HevoPoint(120, 5), new HevoPoint(120, 155));
                d.DrawEllipse(RedBrush, null, new HevoPoint(60, 120), 15, 15);
                d.DrawEllipse(null, Pen2, new HevoPoint(180, 120), 20, 12);
                d.DrawRoundedRectangle(GreenBrush, null, new HevoRect(20, 30, 50, 25), 6, 6);
            });
            int diff = CountDiff(wpf, bmp, out int covered);
            double ratio = (double)diff / Math.Max(1, covered);
            _out.WriteLine($"混合场景(斜线 / 虚线 / 椭圆 / 圆角):覆盖 {covered} 像素,不同 {diff}({ratio:P1})");
            Assert.True(covered > 1000, "场景没画出来");
            Assert.True(ratio < 0.15, $"跟 WPF 差异 {ratio:P1} 超过 15%");
        });

        [Fact]
        public void Text_LandsInSameBoxAsWpf() => IncrementalTestKit.RunOnSta(() =>
        {
            var tf = new HevoTypeface("Arial");
            var black = new HevoSolidBrush(Colors.Black);
            var bg = new HevoSolidBrush(Color.FromRgb(230, 230, 250));
            var (wpf, bmp) = RenderBoth(d =>
            {
                d.DrawText("12345.67", tf, black, 12, new HevoPoint(120, 40), TextAlignX.Center, TextAlignY.Center, bg, GridPen, 4, 2);
                d.DrawText("Vol 9.8K", tf, black, 14, new HevoPoint(230, 120), TextAlignX.Right, TextAlignY.Bottom);
            });
            // 背景框(纯色矩形 + 描边)差 ≤ 1 像素;文字墨迹的包围盒差 ≤ 2 像素
            InkBox(wpf, 0, 0, W, 80, out var a);
            InkBox(bmp, 0, 0, W, 80, out var b);
            _out.WriteLine($"标签框 WPF {a} / 位图 {b}");
            Assert.False(b.IsEmpty, "位图没画出标签框");
            // 框宽 = 文字宽 + padding,文字宽来自字形 advance 之和(WPF 是 FormattedText.Width),取整后允许差 1 像素
            Assert.InRange(Math.Abs(a.Left - b.Left), 0, 1);
            Assert.InRange(Math.Abs(a.Right - b.Right), 0, 1);
            Assert.InRange(Math.Abs(a.Top - b.Top), 0, 1);
            Assert.InRange(Math.Abs(a.Bottom - b.Bottom), 0, 1);
            InkBox(wpf, 0, 80, W, H, out var ta);
            InkBox(bmp, 0, 80, W, H, out var tb);
            _out.WriteLine($"文字墨迹 WPF {ta} / 位图 {tb}");
            Assert.False(tb.IsEmpty, "位图没画出文字");
            Assert.InRange(Math.Abs(ta.Left - tb.Left), 0, 2);
            Assert.InRange(Math.Abs(ta.Right - tb.Right), 0, 2);
            Assert.InRange(Math.Abs(ta.Top - tb.Top), 0, 2);
            Assert.InRange(Math.Abs(ta.Bottom - tb.Bottom), 0, 2);
        });

        private readonly record struct Box(int Left, int Top, int Right, int Bottom)
        {
            public static readonly Box Empty = new(0, 0, -1, -1);
            public bool IsEmpty => Right < Left;
        }

        private static void InkBox(uint[] px, int x0, int y0, int x1, int y1, out Box box)
        {
            int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    if ((px[y * W + x] >> 24) > 64)
                    {
                        l = Math.Min(l, x); r = Math.Max(r, x);
                        t = Math.Min(t, y); b = Math.Max(b, y);
                    }
            box = l == int.MaxValue ? Box.Empty : new Box(l, t, r, b);
        }

        // ====================================================================
        // ③ ChartCell 里的切换与分配
        // ====================================================================

        [Fact]
        public void ModeSwitch_RepresentsWithoutReRecording() => IncrementalTestKit.RunOnSta(() =>
        {
            var cell = NewCell();
            int updates = 0;
            var layer = new ScriptLayer(d => { updates++; AxisAlignedScene(d); });
            cell.AddUnmanagedLayer(layer);
            Record(cell, layer);
            Assert.Equal(1, updates);
            Assert.IsNotType<ImageDrawing>(FirstLeaf(layer.Drawing));

            layer.Mode = RenderMode.Bitmap;
            cell.Invalidate();
            Assert.Equal(1, updates);                              // 只重新上屏,不重录
            Assert.IsType<ImageDrawing>(FirstLeaf(layer.Drawing)); // Visual 里只剩一张位图

            layer.Mode = RenderMode.Software;
            cell.Invalidate();
            Assert.Null(layer.BitmapSurface);                      // 切回矢量释放位图
            Assert.IsNotType<ImageDrawing>(FirstLeaf(layer.Drawing));

            cell.RenderModeOverride = RenderMode.Bitmap;           // 整张图统一切
            cell.Invalidate();
            Assert.IsType<ImageDrawing>(FirstLeaf(layer.Drawing));
            Assert.Equal(1, updates);
        });

        [Fact]
        public void BitmapPresent_AllocatesFarLessThanWpf() => IncrementalTestKit.RunOnSta(() =>
        {
            var cell = NewCell();
            var layer = new ScriptLayer(AxisAlignedScene);
            cell.AddUnmanagedLayer(layer);

            long Measure(int frames)
            {
                for (int i = 0; i < 5; i++) Record(cell, layer); // 预热(缓存、位图、列表容量)
                long total = 0;
                for (int i = 0; i < frames; i++)
                {
                    layer.MarkDirty();
                    layer.Update(NoData.Instance);
                    long a0 = GC.GetAllocatedBytesForCurrentThread();
                    cell.Invalidate();
                    total += GC.GetAllocatedBytesForCurrentThread() - a0;
                }
                return total / frames;
            }

            long wpf = Measure(50);
            layer.Mode = RenderMode.Bitmap;
            long bmp = Measure(50);
            _out.WriteLine($"上屏分配:WPF 矢量 {wpf} B/帧,位图 {bmp} B/帧");
            Assert.True(bmp * 4 < wpf, $"位图 {bmp} B/帧 没有明显少于 WPF {wpf} B/帧");
        });

        [Fact]
        public void HiddenLayer_ClearsBitmap() => IncrementalTestKit.RunOnSta(() =>
        {
            var cell = NewCell();
            bool draw = true;
            var layer = new ScriptLayer(d => { if (draw) d.DrawRectangle(RedBrush, null, new HevoRect(10, 10, 50, 50)); }) { Mode = RenderMode.Bitmap };
            cell.AddUnmanagedLayer(layer);
            Record(cell, layer);
            Assert.True(Count(Capture(cell)) > 2000);
            draw = false;
            Record(cell, layer);
            Assert.Equal(0, Count(Capture(cell)));     // 上一帧画过的区域被清掉
        });

        private static int Count(uint[] px) { int n = 0; foreach (var p in px) if (p != 0) n++; return n; }

        private static System.Windows.Media.Drawing? FirstLeaf(System.Windows.Media.Drawing? d)
        {
            if (d is DrawingGroup g)
            {
                foreach (var child in g.Children)
                {
                    var leaf = FirstLeaf(child);
                    if (leaf != null) return leaf;
                }
                return null;
            }
            return d;
        }
    }
}
