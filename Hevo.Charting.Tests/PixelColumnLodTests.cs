using System;
using System.Collections.Generic;
using System.Linq;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 按像素列 LOD:OHLC 聚合 / 折线 M4 抽稀的正确性,边界列剔除,&lt;1px 与 ≥1px 的切换,DPI 换算。
    /// </summary>
    public class PixelColumnLodTests
    {
        // ── 纯函数 ─────────────────────────────────────────────────────────────

        [Fact]
        public void Accumulator_MergesSameColumn_FirstOpenLastCloseMaxHighMinLow()
        {
            var acc = new OhlcColumnAccumulator();
            Assert.False(acc.Add(5, open: 10, high: 12, low: 9, close: 11, out _));
            Assert.False(acc.Add(5, open: 11, high: 15, low: 10, close: 13, out _));
            Assert.False(acc.Add(5, open: 13, high: 14, low: 7, close: 8, out _));

            Assert.True(acc.Add(6, open: 8, high: 9, low: 6, close: 7, out var col5));
            Assert.Equal(new OhlcColumn(5, 10, 15, 7, 8), col5);

            Assert.True(acc.Flush(out var col6));
            Assert.Equal(new OhlcColumn(6, 8, 9, 6, 7), col6);
            Assert.False(acc.Flush(out _));
        }

        [Fact]
        public void Accumulator_OneBarPerColumn_PassesThroughUnchanged()
        {
            var acc = new OhlcColumnAccumulator();
            var emitted = new List<OhlcColumn>();
            for (int i = 0; i < 4; i++)
                if (acc.Add(i, 100 + i, 110 + i, 90 + i, 105 + i, out var c)) emitted.Add(c);
            if (acc.Flush(out var last)) emitted.Add(last);

            Assert.Equal(Enumerable.Range(0, 4).Select(i => new OhlcColumn(i, 100 + i, 110 + i, 90 + i, 105 + i)), emitted);
        }

        [Theory]
        [InlineData(0.99, 1.0, true)]
        [InlineData(1.0, 1.0, false)]
        [InlineData(4.0, 1.0, false)]
        [InlineData(0.8, 1.5, false)] // 0.8 DIP × 1.5 = 1.2 物理像素,不聚合
        [InlineData(0.6, 1.5, true)]  // 0.9 物理像素,聚合
        public void ShouldAggregate_ThresholdIsOnePhysicalPixel(double dipsPerUnit, double pixelsPerDip, bool expected)
            => Assert.Equal(expected, PixelColumnLod.ShouldAggregate(dipsPerUnit, pixelsPerDip));

        [Fact]
        public void ColumnInside_RejectsColumnsCrossingPlotEdges()
        {
            // 绘图区 [10.5, 20],1 DIP = 1 px:列 10 跨左边,列 19 = [19,20] 整列在内,列 20 越右边
            Assert.False(PixelColumnLod.ColumnInside(10, 1.0, 10.5, 20));
            Assert.True(PixelColumnLod.ColumnInside(11, 1.0, 10.5, 20));
            Assert.True(PixelColumnLod.ColumnInside(19, 1.0, 10.5, 20));
            Assert.False(PixelColumnLod.ColumnInside(20, 1.0, 10.5, 20));
        }

        [Fact]
        public void DecimatePolyline_KeepsFirstMinMaxLastPerColumn_InOrder()
        {
            var input = new List<HevoPoint>
            {
                new(0.1f, 5), new(0.3f, 1), new(0.5f, 9), new(0.9f, 4), // 列 0:首 5,最小 1,最大 9,末 4
                new(1.2f, 3),                                            // 列 1:只有一个点
                new(2.0f, 7), new(2.5f, 2),                              // 列 2:首 = 最大,末 = 最小
            };
            var output = new List<HevoPoint>();
            PixelColumnLod.DecimatePolyline(input, output, pixelsPerDip: 1.0);

            Assert.Equal(new[] { 5f, 1f, 9f, 4f, 3f, 7f, 2f }, output.Select(p => p.Y));
            Assert.Equal(output.Select(p => p.X).OrderBy(x => x), output.Select(p => p.X));
        }

        [Fact]
        public void DecimatePolyline_BoundsPointCountAndPreservesExtremes()
        {
            var rnd = new Random(42);
            var input = Enumerable.Range(0, 20_000).Select(i => new HevoPoint(i * 0.05f, (float)rnd.NextDouble() * 500)).ToList();
            var output = new List<HevoPoint>();
            PixelColumnLod.DecimatePolyline(input, output, pixelsPerDip: 1.0);

            int columns = (int)Math.Ceiling(20_000 * 0.05);
            Assert.True(output.Count <= 4 * columns, $"{output.Count} > {4 * columns}");
            Assert.Equal(input.Min(p => p.Y), output.Min(p => p.Y));
            Assert.Equal(input.Max(p => p.Y), output.Max(p => p.Y));
            Assert.Equal(input[0], output[0]);
            Assert.Equal(input[^1], output[^1]);
        }

        // ── 图层端到端 ─────────────────────────────────────────────────────────

        private const float PlotLeft = 0, PlotWidth = 100, PlotHeight = 100;

        private static (List<HevoRect> Up, List<HevoRect> Down, List<HevoPoint> Wicks) RenderCandles(
            double[] o, double[] h, double[] l, double[] c, RealRange xView, double pixelsPerDip = 1.0)
        {
            List<HevoRect> up = new(), down = new();
            List<HevoPoint> wicks = new();
            IncrementalTestKit.RunOnSta(() =>
            {
                var layer = new CandleLayer { PixelsPerDip = pixelsPerDip };
                // 跟 LayerFrameDriver 一样走 SubmitSync → PrepareTasks → Update,但在 PostRender 清脏之前 swap,才能读到 front buffer
                using (var ctx = IncrementalTestKit.NewRenderContext())
                {
                    var g = ctx.Shared();
                    g.PublishData(new PlotAreaTrait(new HevoRect(PlotLeft, 0, PlotWidth, PlotHeight)));
                    g.PublishData(new XAxisTrait(xView));
                    g.PublishData(new YAxisTrait(new RealRange(0, 200)));
                    g.PublishData(ScaleStrategyTrait.CandleMode);
                    ctx.For(layer).PublishData(new CandleData(0, o, h, l, c)); // 局部数据唤醒未发现的图层
                    ctx.SubmitSync(new IChartLayer[] { layer });
                    Assert.True(layer.IsDirty);
                    using var frame = ctx.PrepareTasks(new IChartLayer[] { layer });
                    foreach (var t in frame.Tasks) t.Layer.Update(t.DataSnapshot);
                }
                layer.SwapBuffer();
                var cmds = ((LayerBuffer)layer.Buffer).Drawing.Commands;
                foreach (var cmd in cmds)
                {
                    if (cmd.Op == DrawOp.DrawLineSegments) wicks.AddRange((List<HevoPoint>)cmd.RefData!);
                    if (cmd.Op == DrawOp.DrawRectangles)
                    {
                        var target = ReferenceEquals(cmd.Brush, CandleStyle.Default.UpBrush) ? up : down;
                        target.AddRange((IList<HevoRect>)cmd.RefData!);
                    }
                }
            });
            return (up, down, wicks);
        }

        private static (double[] O, double[] H, double[] L, double[] C) Bars(int n, int seed = 7)
        {
            var rnd = new Random(seed);
            double[] o = new double[n], h = new double[n], l = new double[n], c = new double[n];
            double p = 100;
            for (int i = 0; i < n; i++)
            {
                o[i] = p;
                c[i] = p = Math.Clamp(p + (rnd.NextDouble() - 0.5) * 6, 20, 180);
                h[i] = Math.Max(o[i], c[i]) + rnd.NextDouble() * 3;
                l[i] = Math.Min(o[i], c[i]) - rnd.NextDouble() * 3;
            }
            return (o, h, l, c);
        }

        [Fact]
        public void CandleLayer_WideBars_DrawsOneCandlePerBar()
        {
            var (o, h, l, c) = Bars(50);
            var (up, down, wicks) = RenderCandles(o, h, l, c, new RealRange(0, 50)); // 2 DIP/根
            Assert.Equal(50, up.Count + down.Count);
            Assert.Equal(100, wicks.Count);
        }

        [Fact]
        public void CandleLayer_SubPixelBars_AggregatesToAtMostOnePerColumn()
        {
            var (o, h, l, c) = Bars(2000);
            var (up, down, wicks) = RenderCandles(o, h, l, c, new RealRange(0, 2000)); // 0.05 DIP/根
            int candles = up.Count + down.Count;
            Assert.InRange(candles, 90, (int)PlotWidth);
            Assert.Equal(2 * candles, wicks.Count);

            // 每列一个 1px 实体,列中心互不重复
            var xs = wicks.Where((_, i) => i % 2 == 0).Select(p => p.X).ToList();
            Assert.Equal(xs.Count, xs.Distinct().Count());
            Assert.All(up.Concat(down), r => Assert.Equal(1f, r.Width, 3));
        }

        [Fact]
        public void CandleLayer_SubPixelBars_ColumnMatchesManualOhlcAggregate()
        {
            var (o, h, l, c) = Bars(2000);
            var (up, down, wicks) = RenderCandles(o, h, l, c, new RealRange(0, 2000));

            // 第一列 [0,1) DIP 覆盖的 K 线:CenteredSnapped 下第 i 根中心在 (i + 0.5) / 2000 × 100
            var inFirst = Enumerable.Range(0, 2000).Where(i => (i + 0.5) / 2000 * PlotWidth < 1.0).ToArray();
            double aggHigh = inFirst.Max(i => h[i]), aggLow = inFirst.Min(i => l[i]);
            double aggOpen = o[inFirst[0]], aggClose = c[inFirst[^1]];

            static double Y(double v) => PlotHeight - v / 200 * PlotHeight;
            Assert.Equal(0.5f, wicks[0].X, 3);
            Assert.Equal(Y(aggHigh), wicks[0].Y, 2);
            Assert.Equal(Y(aggLow), wicks[1].Y, 2);

            bool isUp = aggClose >= aggOpen;
            var body = (isUp ? up : down).First(r => Math.Abs(r.X) < 1e-3);
            Assert.Equal(Math.Min(Y(aggOpen), Y(aggClose)), body.Y, 2);
        }

        [Fact]
        public void CandleLayer_HighDpi_ThresholdUsesPhysicalPixels()
        {
            // 125 根画在 100 DIP = 0.8 DIP/根:100% DPI 下 0.8 px/根 → 按 100 个物理像素列聚合;
            // 150% DPI 下 1.2 px/根 → 走逐根路径(首尾实体越界的那一两根照旧剔除)
            var (o, h, l, c) = Bars(125);
            var at100 = RenderCandles(o, h, l, c, new RealRange(0, 125), pixelsPerDip: 1.0);
            var at150 = RenderCandles(o, h, l, c, new RealRange(0, 125), pixelsPerDip: 1.5);
            Assert.InRange(at100.Up.Count + at100.Down.Count, 90, 100);
            Assert.InRange(at150.Up.Count + at150.Down.Count, 123, 125);
        }
    }
}
