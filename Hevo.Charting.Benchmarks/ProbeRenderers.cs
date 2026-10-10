using System.Linq;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Layers;

namespace Hevo.Charting.Benchmarks
{
    /// <summary>测量用的绘制策略:WPF 矢量(默认)/ 全部图层位图 / 只有序列图层(K 线、折线、柱、散点)位图。</summary>
    internal enum ProbeRenderer { Wpf, BitmapAll, BitmapSeries }

    internal static class ProbeRenderers
    {
        /// <summary>--renderer=wpf|bitmap|series(latency-probe 用;render-probe 走 bmp / bmp-series 模式)。</summary>
        public static ProbeRenderer Parse(string? value) => value?.ToLowerInvariant() switch
        {
            null or "" or "wpf" or "software" => ProbeRenderer.Wpf,
            "bitmap" or "bmp" or "all" => ProbeRenderer.BitmapAll,
            "series" or "bmp-series" => ProbeRenderer.BitmapSeries,
            _ => throw new System.ArgumentException($"未知 --renderer={value}(可选 wpf / bitmap / series)"),
        };

        /// <summary>
        /// 只用公开 API 切策略:整张图用 <see cref="ChartCell.RenderModeOverride"/>,单个图层用 <see cref="ChartLayer.Mode"/>。
        /// 切换本身会排一帧重新上屏,调用方紧接着跑的归位帧会把它吃掉,不进测量。
        /// </summary>
        public static void Apply(ProbeRig rig, ProbeRenderer renderer)
        {
            var cells = rig.Charts.Select(c => c.Cell).Concat(rig.Panes.Select(p => p.Cell));
            foreach (var cell in cells)
            {
                cell.RenderModeOverride = renderer == ProbeRenderer.BitmapAll ? RenderMode.Bitmap : null;
                foreach (var layer in cell.ActiveLayers.OfType<ChartLayer>())
                    layer.Mode = renderer == ProbeRenderer.BitmapSeries && IsSeries(layer) ? RenderMode.Bitmap : RenderMode.Software;
            }
        }

        private static bool IsSeries(ChartLayer layer) => layer is CandleLayer or LineLayer or BarLayer or ScatterPlotLayer;
    }
}
