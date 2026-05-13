using Hevo.Charting.Features;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;

namespace Hevo.Charting.PythonNet
{
    /// <summary>
    /// 通用 Compute / Handler / Plot 蓝图节点的分类注册 + 旧名向后兼容入口。业务侧引
    /// Hevo.Charting.PythonNet 后启动调一次:
    /// <code>
    /// PythonNetBootstrap.Register();
    /// </code>
    ///
    /// <para>
    /// <b>新现实</b>:<see cref="ComputeFeature"/> / <see cref="HandlerFeature"/> / <see cref="PlotFeature"/>
    /// 已下沉主项目 <c>Hevo.Charting</c>(无 Python 依赖),所以 <c>BuiltinRegistration.RegisterBuiltins</c>
    /// 已经会自动收录到 <see cref="ComponentRegistry"/>。本入口只补两件事:
    /// </para>
    /// <list type="number">
    ///   <item><see cref="FeatureCategoryRegistry"/> 分类(GraphViewer 画布泳道 / picker 配色)</item>
    ///   <item>蓝图 JSON 旧名向后兼容:<c>"TypeName": "PyComputeFeature"</c> alias 到新 <see cref="ComputeFeature"/></item>
    /// </list>
    ///
    /// <para>重复调用幂等。</para>
    /// </summary>
    public static class PythonNetBootstrap
    {
        /// <summary>注册分类 + 蓝图 JSON 旧名向后兼容 alias。</summary>
        public static void Register()
        {
            // FeatureCategoryRegistry:GraphViewer 画布泳道 / picker 分组用。
            // ComputeFeature 是数据计算节点 → Series 类(跟 LineSeries / BarSeries 同侧)。
            // HandlerFeature 是副作用(信号 / 下单 / 报警)→ Interaction 类。
            // PlotFeature(Pine 风味)= 多 series 自渲染 → Series 类。
            FeatureCategoryRegistry.Register<ComputeFeature>(FeatureCategory.Series);
            FeatureCategoryRegistry.Register<HandlerFeature>(FeatureCategory.Interaction);
            FeatureCategoryRegistry.Register<PlotFeature>(FeatureCategory.Series);

            // 旧蓝图 JSON 兼容别名 —— ComponentRegistry.Register 是 idempotent setter,重复调不会冲突。
            // "PyPlotFeature" 是 §E2 合并前的旧名,合并到 PlotFeature 后保留别名让历史 JSON 蓝图继续跑。
            ComponentRegistry.Register<ComputeFeature>("PyComputeFeature");
            ComponentRegistry.Register<HandlerFeature>("PyHandlerFeature");
            ComponentRegistry.Register<PlotFeature>("PyPlotFeature");
        }
    }
}
