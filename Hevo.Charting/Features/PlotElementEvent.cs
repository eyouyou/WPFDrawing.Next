namespace Hevo.Charting.Features
{
    /// <summary>
    /// Plot 元素交互事件 args —— hover / 双击命中时,Feature 抛给业务订阅者。
    /// <para>
    /// 框架不解释具体字段:event 只携带 <see cref="Index"/>(命中元素在 spec 列表内的下标)+ <see cref="Element"/>
    /// (强类型 spec 元素本身,业务侧按 TElement 自带字段直接读)。
    /// </para>
    /// <para>
    /// 触发事件的 Feature 实例 = <c>sender</c> 参数;识别多 plot 时直接 cast / 跟持有的实例引用比较。
    /// </para>
    /// </summary>
    /// <typeparam name="TElement">spec 元素类型(典型 <see cref="Hevo.Charting.Core.ScatterPoint"/> /
    /// <see cref="Hevo.Charting.Core.ArrowMarker"/>)。</typeparam>
    /// <param name="Index">命中 spec 列表内索引。</param>
    /// <param name="Element">命中 spec 元素强类型副本(struct,无堆分配)。</param>
    public readonly record struct PlotElementEvent<TElement>(int Index, TElement Element);
}
