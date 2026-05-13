using System;
using System.Windows.Media;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;

namespace Hevo.Charting.Layers
{
    /// <summary>
    /// 散点图层 —— <see cref="ScatterSpecTrait"/> 列表按 <see cref="XAxisTrait"/>+<see cref="YAxisTrait"/>
    /// (layer-local 由 ScatterPlotFeature publish 或 inherit shared)线性映射,DrawEllipse N 次。
    /// 跟 line/bar 渲染同源(消费 XAxis/YAxis/PlotArea),没有 tooltip 之类的 metadata 渲染 —— 业务侧自管交互 UI。
    /// </summary>
    public sealed partial class ScatterPlotLayer : ChartLayer
    {
        // 限制诊断只打前几次,避免刷屏
        private int _diagPrinted = 0;
        private int _drawDiagPrinted = 0;

        public string LayerName
        {
            get => Name;
            set => Name = value;
        }

        public ScatterPlotLayer()
        {
            Name = "ScatterPlot";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Main;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var plot = data.Get<PlotAreaTrait>();
            var xAxis = data.Get<XAxisTrait>();
            var yAxis = data.Get<YAxisTrait>();
            var spec = data.Get<ScatterSpecTrait>();
            // 一行诊断 —— 仅在首次输入到位 / 输入缺失时打日志一次,正常运行不刷屏。
            if (_diagPrinted++ < 3)
                System.Diagnostics.Debug.WriteLine($"[ScatterPlotLayer.OnUpdate] plot={(plot!=null)} xAxis={(xAxis!=null)} yAxis={(yAxis!=null)} spec.Len={spec?.Points.Length ?? -1}");
            if (plot == null || xAxis == null || yAxis == null || spec == null) return;

            var area = plot.Area;
            var xRange = xAxis.Viewport;
            var yRange = yAxis.Viewport;
            // 一行诊断:看为啥到不了 draw 行
            if (spec.Points.Length > 0 && _drawDiagPrinted < 3)
            {
                System.Diagnostics.Debug.WriteLine($"[ScatterPlotLayer.preDraw] area=({area.Left:F1},{area.Top:F1} {area.Width:F1}x{area.Height:F1}) xR=[{xRange.Min:F3},{xRange.Max:F3}] xValid={xRange.IsValid} yR=[{yRange.Min:F3},{yRange.Max:F3}] yValid={yRange.IsValid}");
            }
            if (area.Width <= 0 || area.Height <= 0) return;
            if (!xRange.IsValid || !yRange.IsValid) return;
            double xSpan = xRange.Span;
            double ySpan = yRange.Span;

            int drawn = 0, clipped = 0;
            var span = spec.Points.Span;
            for (int i = 0; i < span.Length; i++)
            {
                ref readonly var p = ref span[i];
                double sx = area.Left + (p.X - xRange.Min) / xSpan * area.Width;
                double sy = area.Top + (1 - (p.Y - yRange.Min) / ySpan) * area.Height;
                if (sx < area.Left || sx > area.Right || sy < area.Top || sy > area.Bottom) { clipped++; continue; }

                var brush = PlotBrushCache.Resolve(p.Color);
                draw.DrawEllipse(brush, null, new HevoPoint((float)sx, (float)sy), p.Size, p.Size);
                drawn++;
            }
            // 只在 spec 有数据时打,避开 spec.Len=0 浪费配额
            if (span.Length > 0 && _drawDiagPrinted++ < 3)
            {
                System.Diagnostics.Debug.WriteLine($"[ScatterPlotLayer.draw] area=({area.Left:F0},{area.Top:F0} {area.Width:F0}x{area.Height:F0}) xR=[{xRange.Min:F3},{xRange.Max:F3}] yR=[{yRange.Min:F3},{yRange.Max:F3}] points={span.Length} drawn={drawn} clipped={clipped}");
            }
        }
    }

    /// <summary>
    /// scatter / arrow markers 共享的 color string → <see cref="HevoSolidBrush"/> 缓存。
    /// 支持 "#RGB" / "#RRGGBB" / "#AARRGGBB",非法值 fallback 到中性灰。Layer UI 线程消费,简单 lock 即可,稳态零分配。
    /// </summary>
    internal static class PlotBrushCache
    {
        private static readonly System.Collections.Generic.Dictionary<string, HevoSolidBrush> _cache = new(StringComparer.Ordinal);
        private static readonly object _lock = new();

        public static HevoSolidBrush Resolve(string color)
        {
            if (string.IsNullOrEmpty(color))
                return new HevoSolidBrush(Color.FromRgb(0x88, 0x88, 0x88));
            lock (_lock)
            {
                if (_cache.TryGetValue(color, out var hit)) return hit;
                var c = ParseColor(color);
                var brush = new HevoSolidBrush(c);
                _cache[color] = brush;
                return brush;
            }
        }

        private static Color ParseColor(string s)
        {
            if (string.IsNullOrEmpty(s) || s[0] != '#') return Color.FromRgb(0x88, 0x88, 0x88);
            try
            {
                var hex = s.Substring(1);
                if (hex.Length == 3)
                {
                    byte r = (byte)(HexNibble(hex[0]) * 0x11);
                    byte g = (byte)(HexNibble(hex[1]) * 0x11);
                    byte b = (byte)(HexNibble(hex[2]) * 0x11);
                    return Color.FromRgb(r, g, b);
                }
                if (hex.Length == 6)
                {
                    byte r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                    byte g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                    byte b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                    return Color.FromRgb(r, g, b);
                }
                if (hex.Length == 8)
                {
                    byte a = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                    byte r = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                    byte g = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                    byte b = byte.Parse(hex.Substring(6, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                    return Color.FromArgb(a, r, g, b);
                }
            }
            catch { /* fall through */ }
            return Color.FromRgb(0x88, 0x88, 0x88);
        }

        private static int HexNibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return 0;
        }
    }
}
