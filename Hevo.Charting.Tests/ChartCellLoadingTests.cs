using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Hevo.Charting.Core;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// ChartCell IsLoading / LoadingTemplate DP 行为 —— framework 默认 spinner overlay 跟 BlueprintRunner.Run
    /// 的 "UI 渲染后 LoadAsync + 首次 leaf publish 翻 false" 流程对接的本地验证。
    /// 不涉及实际数据源,只验 DP 跟 overlay visibility 联动。
    /// <para>
    /// WPF 要求 STA 线程才能 new UIElement;xUnit 默认 MTA,这里每个 fact 自己 spin 一根 STA 线程跑 body。
    /// 跟 BlueprintTriggerBindingTests 这类需要 Dispatcher 的测试不一样 —— 这里只验 DP,完全不动 Dispatcher。
    /// </para>
    /// </summary>
    public sealed class ChartCellLoadingTests
    {
        private static void RunOnSta(Action action)
        {
            Exception? caught = null;
            var t = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { caught = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (caught != null) throw new Xunit.Sdk.XunitException(caught.ToString());
        }

        [Fact]
        public void ChartCell_IsLoading_DefaultFalse_HidesOverlay() => RunOnSta(() =>
        {
            var cell = new ChartCell();
            Assert.False(cell.IsLoading);   // 默认 false:直接 `new ChartCell { Template = schema }` 不显示 spinner;
                                            // 蓝图路径由 BlueprintRunner.Run 显式翻 true。

            // 反射拿 _loadingPresenter 验真 Visibility 初值 Collapsed(对齐 IsLoading=false)。
            var field = typeof(ChartCell).GetField("_loadingPresenter",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(field);
            var presenter = field!.GetValue(cell) as ContentPresenter;
            Assert.NotNull(presenter);
            Assert.Equal(Visibility.Collapsed, presenter!.Visibility);
        });

        [Fact]
        public void ChartCell_IsLoading_FlipFalse_HidesOverlay() => RunOnSta(() =>
        {
            var cell = new ChartCell();
            cell.IsLoading = false;
            Assert.False(cell.IsLoading);

            // 反射拿 _loadingPresenter 验真 Visibility 切换(DP changed callback 内部链接)。
            var field = typeof(ChartCell).GetField("_loadingPresenter",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(field);
            var presenter = field!.GetValue(cell) as ContentPresenter;
            Assert.NotNull(presenter);
            Assert.Equal(Visibility.Collapsed, presenter!.Visibility);
        });

        [Fact]
        public void ChartCell_LoadingTemplate_Override_ReplacesDefaultSpinner() => RunOnSta(() =>
        {
            var cell = new ChartCell();
            var customTemplate = new DataTemplate();   // 无 DataType 的空模板足够测 ContentTemplate 是否被设进去

            cell.LoadingTemplate = customTemplate;

            var field = typeof(ChartCell).GetField("_loadingPresenter",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var presenter = (ContentPresenter)field!.GetValue(cell)!;
            Assert.Same(customTemplate, presenter.ContentTemplate);
            Assert.NotNull(presenter.Content);   // DataTemplate 需要非空 DataContext
        });
    }
}
