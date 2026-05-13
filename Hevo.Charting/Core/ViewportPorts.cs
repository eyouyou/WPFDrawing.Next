using System.Windows;
using Hevo.Charting.Abstractions;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 视口状态：纯语义层，全部 RealRange / int 表达"用户在看哪儿"。
    /// 离散索引（slice offset 等）已移除 —— 数据流不再切片，世界索引 == 数组下标。
    /// </summary>
    public class ViewportPorts
    {
        /// <summary>数据总规模：bar 数。视图管家用作 clamp 上界。</summary>
        public DataPort<int> LogicalLength { get; } = new("VP_LogicalLength");

        /// <summary>用户意图范围：交互层（Pan/Zoom/Keyboard）写入。可越界（视 OverscrollPolicy）。</summary>
        public DataPort<RealRange> UserRange { get; } = new("VP_UserRange");

        /// <summary>当前显示范围：视图管家裁决后的合法 range。下游所有消费者读这一个。</summary>
        public DataPort<RealRange> ActiveRange { get; } = new("VP_ActiveRange");

        // 默认 ctor 留空 —— 历史 debug trace 已删除(详见低代码.md §8 PyPlotFeature Viewport bug 修复)。
        public ViewportPorts() { }

        // ==========================================
        // 💥 ChartCell attached property
        //
        // viewport 跟 ChartCell 关联(它是 cell 的运行时状态),不再跟 Schema 关联。
        // ReactiveSchema.InitializeRegistry 在 chart 上 SetAttached 一份默认 ports;
        // SchemaContext.LinkedMaster/Pane Decorate 阶段可 mutate 共享 ports;
        // ChartFeature.OnAttached 通过 Chart.Viewport 拿到 ports 注入自家 Viewport 字段。
        // 业务 schema body 显式 `Chart!.Viewport.X` —— 表达"我从 cell 取",而非"我持有"。
        // ==========================================

        public static readonly DependencyProperty AttachedProperty =
            DependencyProperty.RegisterAttached(
                "Attached",
                typeof(ViewportPorts),
                typeof(ViewportPorts),
                new FrameworkPropertyMetadata(default(ViewportPorts)));

        /// <summary>读取附加在 <paramref name="d"/>(典型 ChartCell)上的 viewport ports;未挂返回 null。</summary>
        public static ViewportPorts? GetAttached(DependencyObject d) =>
            (ViewportPorts?)d.GetValue(AttachedProperty);

        /// <summary>
        /// 读取附加在 <paramref name="d"/> 上的 viewport ports —— 未挂时抛 InvalidOperation。
        /// 业务侧"我确信 schema 装了 viewport"的场景用这个,调用面零 <c>!</c> 后缀,语义清晰。
        /// </summary>
        public static ViewportPorts RequireAttached(DependencyObject d) =>
            GetAttached(d) ?? throw new InvalidOperationException(
                "ChartCell 上没 attached ViewportPorts。" +
                "chart 业务 schema 通常通过 env.SetupViewport(...) 自助装配 ViewportPortsFeature(它会自己 SetAttached);" +
                "若你在 ComposeAll 之前/DecomposeAll 之后访问,请检查调用时机。");

        /// <summary>把 viewport ports 附加到 <paramref name="d"/>(典型 ChartCell)。</summary>
        public static void SetAttached(DependencyObject d, ViewportPorts? value) =>
            d.SetValue(AttachedProperty, value);
    }
}
