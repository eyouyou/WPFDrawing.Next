using System.Windows;
using System.Windows.Controls;
using Hevo.Charting.Linked;
using Hevo.Drawing.LowCodeDemo.CodedKLine;

namespace Hevo.Drawing.LowCodeDemo
{
    /// <summary>
    /// §对照实现 host —— 跟 <c>Hevo.Drawing.Sample.Kline.Linked.KLineIndicatorView</c>(canonical 范本)
    /// 完全同款打法:
    /// <list type="number">
    ///   <item><b>共享一份 DataSource</b>:两 schema 都接 View 持有的 <see cref="MockKLineDataSource"/>,
    ///         所有 cell 的 pipe 订阅同一根 _trigger。View 调一次 <c>SwitchContext</c> 数据自动喂到两条 pipe。</item>
    ///   <item><b>ctor 期完成装配 + 挂入 visual tree</b>:WPF 第一次 measure 时整链自然触发,
    ///         _rootContainer SizeChanged 自然 fire → InvalidateEnvironment → 首帧 FullPass。</item>
    ///   <item><b>Loaded 触发数据流</b>:此时 schema 已 ComposeAll、pipe 已 subscribe,
    ///         SwitchContext 同步先 Clear+Publish 一次(空),再 fire-and-forget 异步 fetch,数据齐了 pipe 自然消费。</item>
    /// </list>
    /// </summary>
    public partial class CodedKLineView : UserControl
    {
        // 一份 DS 跨两 schema 共享,生命周期归 View(对应 KLineIndicatorView._dataSource = new())。
        private readonly MockKLineDataSource _dataSource = new();
        private readonly LinkedChartDashboard _dashboard;

        public CodedKLineView()
        {
            InitializeComponent();

            var ctx = new LinkedChartContext();
            _dashboard = new LinkedChartDashboard(ctx)
                .AddMaster(new KLineMainSchema  (_dataSource), heightRatio: 3)
                .AddPane  (new KLineVolumeSchema(_dataSource), heightRatio: 1);

            // ctor 期挂入 visual tree —— 同 canonical KLineIndicatorView,WPF measure 自然走完整链。
            RootGrid.Children.Add(_dashboard);

            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // SwitchContext 走 ReactiveDataSource 单飞 fetch:
            //   1. 立即 Clear+Publish(空 buffer),让 pipe 先看到一帧空数据(无关紧要,ingestors 都有 version check 短路);
            //   2. 异步派发 _ctxBus.Push,handler 跑 OnFetchAsync → MockKLineDataSource.EnsureSharedFeedStarted
            //      seed 30 根 + 启进程 timer(每 150ms append 1 根);
            //   3. UpdateBuffer 完成时 Publish 真实数据,两个 schema 的 pipe 同时被喂(同一 _trigger),
            //      port 都被写、AutoScale Y 量程算出来、Candle/Bar layer 渲染。
            _dataSource.SwitchContext("demo");
        }
    }
}
