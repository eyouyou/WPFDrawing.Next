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
    }
}
