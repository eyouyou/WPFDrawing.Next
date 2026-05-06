using Hevo.Charting.Core;

namespace Hevo.Charting.LowCode.Designer
{
    /// <summary>
    /// 💥 蓝图运行时入口:把 JSON 蓝图 + 数据源 + 数据流 三件套塞给图表宿主。
    /// <para>
    /// 典型用法 (业务侧):
    /// <code>
    /// // 1) 准备数据源 (业务自行 new + LoadAsync)
    /// var ds = new TimeShareDataSource();
    ///
    /// // 2) 反序列化或硬编码出蓝图
    /// var blueprint = JsonSerializer.Deserialize&lt;ChartBlueprint&gt;(json);
    ///
    /// // 3) 一行启动
    /// BlueprintRunner.Run&lt;TimeData&gt;(host.Cell, blueprint, ds, ds.Stream);
    /// </code>
    /// </para>
    /// </summary>
    public static class BlueprintRunner
    {
        /// <summary>
        /// 💥 装配并启动:为 chart 安装蓝图驱动的 <see cref="DynamicChartSchema{TItem}"/>。
        /// chart 已有 Template 时强制覆盖 (蓝图 reload 场景下旧 schema 会被 ChartCell 自动 Decompose)。
        /// </summary>
        public static DynamicChartSchema<TItem> Run<TItem>(
            ChartCell chart,
            ChartBlueprint blueprint,
            object dataSource,
            IWorkflow<DataSnapshot<TItem>> stream,
            BlueprintHandlerRegistry? handlers = null)
        {
            if (chart is null) throw new ArgumentNullException(nameof(chart));

            var schema = new DynamicChartSchema<TItem>(blueprint, dataSource, stream, handlers);
            chart.Template = schema;
            return schema;
        }

        /// <summary>
        /// 💥 极简重载:直接从 <see cref="DataSource{TSource, TItem}"/> 派生类接出 Stream,业务侧少写一个 ds.Stream。
        /// </summary>
        public static DynamicChartSchema<TItem> Run<TSource, TItem>(
            ChartCell chart,
            ChartBlueprint blueprint,
            DataSource<TSource, TItem> dataSource,
            BlueprintHandlerRegistry? handlers = null)
            where TSource : DataSource<TSource, TItem>
        {
            if (dataSource is null) throw new ArgumentNullException(nameof(dataSource));
            return Run(chart, blueprint, dataSource, dataSource.Stream, handlers);
        }
    }
}
