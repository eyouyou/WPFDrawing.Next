using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Benchmarks
{
    /// <summary>render-probe 的测量窗口种类:场景名决定用哪种窗口(见 <see cref="RenderProbe.RigKindOf"/>)。</summary>
    internal enum RigKind
    {
        /// <summary>1/N 张 K 线主图(蜡烛 + SMA + 轴 + 标题栏 + 标准交互,含 Tooltip)。</summary>
        Standard,
        /// <summary>同 Standard,但摘掉 TooltipWidgetFeature,用来跟 Hover 对照 tooltip 的开销。</summary>
        NoTooltip,
        /// <summary>LinkedChartDashboard:主图(+ scatter / arrow / text 标记)+ 成交量副图,联动视口与十字光标。</summary>
        Dashboard,
        /// <summary>模拟 150% / 200% DPI(LayoutTransform 放大 + 把 ChartCell 的 PixelsPerDip 设成缩放比)。</summary>
        Dpi150,
        Dpi200,
    }

    /// <summary>主图 schema 的可选装配项。</summary>
    internal sealed record ProbeChartOptions(bool Tooltip = true, bool Markers = false)
    {
        public static readonly ProbeChartOptions Default = new();
    }

    /// <summary>
    /// 成交量副图:Y 轴 + 柱状图 + 十字光标(跟 LowCodeDemo KLineVolumeSchema 同款装配),
    /// 黑板由脚本直接写。挂进 LinkedChartDashboard 后视口 / hit 跟主图联动。
    /// </summary>
    internal sealed class ProbeVolumeSchema : ChartReactiveSchema
    {
        public DataPort<ReadOnlyMemory<DateTime>> TimePort { get; } = new("V_Time");
        public DataPort<ReadOnlyMemory<double>> VolumePort { get; } = new("V_Volume");
        private readonly DataPort<RealRange> _yRangePort = new("V_YRange");
        public WorkflowTrigger<DataBlackboard> Trigger { get; } = new();
        public ViewportPorts Viewport { get; private set; } = null!;

        protected override void DefineDataFlow(ChartCell chart)
        {
            Viewport = ViewportPorts.RequireAttached(Chart);
            Trigger.BindTo(chart);
        }

        protected override void DefineFeatures(IFeatureContext canvas)
        {
            canvas.Seed<ScaleStrategyTrait>(ScaleStrategyTrait.CandleMode);
            var hitPort = HitPort;
            var volumeMeta = FieldMeta.Literal("成交量", Color.FromRgb(0x4F, 0xC3, 0xF7), "F0");
            var timeMeta = FieldMeta.Literal("时间", Colors.White, "HH:mm");

            canvas
                .Environment(env => env
                    .SetupLayout(left: ChartLength.Pixel(60), top: ChartLength.Pixel(28), right: ChartLength.Pixel(0), bottom: ChartLength.Pixel(4))
                    .SetupViewport(minVisibleCount: 10, alignment: ViewportAlignment.RightEdge,
                        overscrollMin: OverscrollPolicy.Hard, overscrollMax: OverscrollPolicy.Hard)
                    .SetupAutoScale(yRangePort: _yRangePort, valuePorts: new[] { VolumePort }, paddingRatio: 0.05,
                        strategy: AutoScaleStrategy.IncludeZero)
                    .SetupUniversalHeader(hitPort: hitPort))
                .Axes(axes => axes.AddRangeAxis(_yRangePort, volumeMeta, AxisPlacement.Right))
                .Series(series => series.AddBar(dataPort: VolumePort, rangePort: _yRangePort, meta: volumeMeta, widthRatio: 0.6))
                .Interactions(i => i.AddCrosshair(hitPort, TimePort, timeMeta));
        }
    }

    /// <summary>副图:schema + cell + 黑板;数据跟主图共用一份 <see cref="ProbeData"/>(长度跟主图同步)。</summary>
    internal sealed class ProbePane
    {
        public required ProbeVolumeSchema Schema { get; init; }
        public required ChartCell Cell { get; init; }
        public required DataBlackboard Board { get; init; }
        public required ProbeChart Master { get; init; }

        public void LoadData()
        {
            WriteSeries();
            Schema.Trigger.Push(Board);
            Schema.InvalidateEnvironment();
        }

        public void WriteSeries()
        {
            using (Board.BeginTransaction())
            {
                Board.WriteIfChanged(TimeOf(), Master.Slice(Master.Data.Time));
                Board.WriteIfChanged(Schema.VolumePort, Master.Slice(Master.Data.Volume));
            }
        }

        private DataPort<ReadOnlyMemory<DateTime>> TimeOf() => Schema.TimePort;

        /// <summary>DashTick:最后一根 K 线的成交量跟着跳(跟主图 TickStep 同一步)。</summary>
        public void TickStep(int i)
        {
            var d = Master.Data;
            int last = Master.Length - 1;
            // 取绝对值而不是累加:多轮测量之间不留痕迹(累加会让副图 AutoScale 每轮不同,计数跨轮不一致)
            d.Volume[last] = 1000 + 37 * (i % 11);
            using (Board.BeginTransaction())
                Board.ForceWrite(Schema.VolumePort, Master.Slice(d.Volume));
        }
    }

    internal static class ProbeDpi
    {
        public static double ScaleOf(RigKind kind) => kind switch { RigKind.Dpi150 => 1.5, RigKind.Dpi200 => 2.0, _ => 1.0 };

        /// <summary>
        /// 模拟高 DPI:内容按 1/scale 的 DIP 尺寸布局、LayoutTransform 放大 scale 倍(屏幕上仍占原像素),
        /// 再走 ChartCell 自己的 OnDpiChanged 把 PixelsPerDip 设成 scale(跟真实换屏同一条代码路径:
        /// 更新渲染器 DPI + 环境纪元重投影)。局限:WPF 实际仍按系统 DPI 光栅化、文字 hinting 跟真 150% 屏不同;
        /// 测的是 DIP 布局变小 + 按物理像素做 LOD 的那部分 CPU 管线。
        /// </summary>
        public static void Apply(ChartCell cell, double scale)
        {
            var m = typeof(ChartCell).GetMethod("OnDpiChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            m.Invoke(cell, new object[] { new DpiScale(1.0, 1.0), new DpiScale(scale, scale) });
        }
    }
}
