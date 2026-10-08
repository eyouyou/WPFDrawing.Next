using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Linked;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 联动 dashboard:schema 在 DefineFeatures 里 RequireAttached(Chart) 拿到的 viewport 必须就是共享 viewport。
    /// 回归点:以前 ViewportPortsFeature 先挂本地 ports,等 DefineFeatures 之后 Decorate 才换成共享的,
    /// AddDomainAxis 这类在 DefineFeatures 里捕获 vp.ActiveRange 的 Feature 盯的是本地端口 —— 主图平移时时间轴不跟随。
    /// </summary>
    public sealed class LinkedViewportAttachTests
    {
        private sealed class CaptureSchema : ChartReactiveSchema
        {
            public ViewportPorts? Captured { get; private set; }
            private readonly WorkflowTrigger<DataBlackboard> _trigger = new();

            protected override void DefineDataFlow(ChartCell chart) => _trigger.BindTo(chart);

            protected override void DefineFeatures(IFeatureContext canvas)
            {
                canvas.Environment(env => env.SetupViewport(minVisibleCount: 10, alignment: ViewportAlignment.RightEdge));
                Captured = ViewportPorts.RequireAttached(Chart);
            }
        }

        private static CaptureSchema Compose(System.Func<CaptureSchema, ChartCell> host)
        {
            var schema = new CaptureSchema();
            var cell = host(schema);
            using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);
            return schema;
        }

        [Fact]
        public void LinkedMaster_DefineFeatures_SeesSharedViewport()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var context = new LinkedChartContext();
                var dashboard = new LinkedChartDashboard(context);
                var schema = Compose(s => { dashboard.AddMaster(s); return dashboard.Cells[0]; });
                Assert.Same(context.SharedViewport, schema.Captured);
            });
        }

        [Fact]
        public void LinkedPane_DefineFeatures_SeesSharedViewport()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var context = new LinkedChartContext();
                var dashboard = new LinkedChartDashboard(context);
                dashboard.AddMaster(new CaptureSchema());
                var schema = Compose(s => { dashboard.AddPane(s); return dashboard.Cells[1]; });
                Assert.Same(context.SharedViewport, schema.Captured);
            });
        }

        [Fact]
        public void Standalone_KeepsOwnViewport()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var other = new LinkedChartContext();
                var schema = Compose(s => new ChartCell { Template = s });
                Assert.NotNull(schema.Captured);
                Assert.NotSame(other.SharedViewport, schema.Captured);
            });
        }
    }
}
