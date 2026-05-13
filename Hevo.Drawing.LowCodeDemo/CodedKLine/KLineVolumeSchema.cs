using System;
using System.Windows.Media;
using Hevo.Charting;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;

namespace Hevo.Drawing.LowCodeDemo.CodedKLine
{
    /// <summary>
    /// §对照实现 手写量副 schema —— 跟
    /// <c>Assets/default_kline_dashboard.json</c> 的 <c>volume</c> cell 同等表达力的纯 C# 版。
    /// <para>
    /// 副图 schema 跟主图独立各自实例化 <see cref="MockKLineDataSource"/>(进程级共享 timeline);
    /// 联动只在 <see cref="ViewportPorts"/> + <c>linkedHit</c> 这几根 port 上,由
    /// <c>LinkedChartDashboard.AddPane</c> 的 <c>SchemaContext.LinkedPane</c> 装饰钩子镜像桥接 ——
    /// schema body 不感知"联动"概念。
    /// </para>
    /// <para>
    /// <b>⚠ 必须 Seed ScaleStrategyTrait</b>(原因同 <see cref="KLineMainSchema"/>)—— 没这行
    /// BarLayer 拿不到 IScale,所有柱体坐标投影全错,scale 看起来"丢了"。
    /// </para>
    /// </summary>
    public sealed class KLineVolumeSchema : ChartReactiveSchema, IDisposable
    {
        // 跟 KLineMainSchema 同款显式 prefix —— 见那边注释。
        private readonly MockKLinePorts Ports = new("V_");
        private readonly DataPort<RealRange> _yRangePort = new("V_YRange");

        // §DS 共享 —— 跟 KLineMainSchema 同款,DS 生命周期归 View。
        private readonly MockKLineDataSource _ds;

        public KLineVolumeSchema(MockKLineDataSource dataSource)
        {
            _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        public void Dispose() { }

        protected override void OnResume() => _ds.RepublishLatest();

        protected override void DefineDataFlow(ChartCell chart)
        {
            // viewport 走 ChartCell attached property —— SchemaContext.LinkedPane.Decorate 在
            // InitializeRegistry 末尾会把 ViewportPortsFeature.Ports 换成 _ctx.SharedViewport,
            // 同时 SetAttached(Chart, SharedViewport)。DefineDataFlow 跑在 BuildAndActivatePipeline,
            // 即 Decorate 之后,RequireAttached 拿到的就是共享 viewport,跨 cell 镜像桥已就位。
            _ds.Pipe()
                .LinkStream(cfg => cfg.AutoMap(Ports))
                .ProjectExtent(ViewportPorts.RequireAttached(Chart))
                .BindTo(chart);
        }

        protected override void DefineFeatures(IFeatureContext canvas)
        {
            // §核心 ⚠ 跟 KLineMainSchema 同一块石头 —— 详见那边注释。
            canvas.Seed<ScaleStrategyTrait>(ScaleStrategyTrait.CandleMode);

            var hitPort    = HitPort;
            var volumeMeta = FieldMeta.Literal("成交量", Color.FromRgb(0x4F, 0xC3, 0xF7), "F0");
            var timeMeta   = FieldMeta.Literal("时间",   Colors.White,                    "HH:mm");

            canvas
                .Environment(env => env
                    .SetupLayout(
                        left:   ChartLength.Pixel(60),
                        top:    ChartLength.Pixel(28),  // 给本 cell 的 header 留位
                        right:  ChartLength.Pixel(0),
                        bottom: ChartLength.Pixel(4))
                    // 副图也 SetupViewport,但 LinkedPane.Decorate 会摘掉 VPM —— 钳制由主图独占。
                    .SetupViewport(
                        minVisibleCount: 10,
                        alignment:       ViewportAlignment.RightEdge,
                        overscrollMin:   OverscrollPolicy.Hard,
                        overscrollMax:   OverscrollPolicy.Hard)
                    .SetupAutoScale(
                        yRangePort:   _yRangePort,
                        valuePorts:   new[] { Ports.VolumePort },
                        paddingRatio: 0.05,
                        // 成交量从 0 起,纳入零轴让柱体视觉跟基线一致(不会半截悬空)。
                        strategy:     AutoScaleStrategy.IncludeZero)
                    .SetupUniversalHeader(hitPort: hitPort))
                // 跟 JSON 副图 volume cell 对齐:不挂 DomainAxis —— X 轴 tick 由主图底部唯一一处绘制。
                .Axes(axes => axes
                    .AddRangeAxis(_yRangePort, volumeMeta, AxisPlacement.Right))
                .Series(series => series
                    .AddBar(
                        dataPort:   Ports.VolumePort,
                        rangePort:  _yRangePort,
                        meta:       volumeMeta,
                        widthRatio: 0.6))
                // 副图也挂 crosshair —— 但用最简版(交互模式只开十字线,
                // pan/zoom 由主图独占,副图自己再开就重了)。
                .Interactions(i => i.AddCrosshair(
                    hitPort,
                    Ports.TimePort,
                    timeMeta));
        }
    }
}
