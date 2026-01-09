using Hevo.Charting.Abstractions;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 💥 视口状态全家桶：彻底终结引脚满天飞的终极收纳盒！
    /// 将逻辑边界、物理偏移、用户意图全部打包，作为一个整体在管道中传递。
    /// </summary>
    public class ViewportPorts
    {
        /// <summary>
        /// 1. 数据的总逻辑长度 (供大法官防越界)
        /// </summary>
        public DataPort<int> LogicalLength { get; } = new("VP_LogicalLength");

        /// <summary>
        /// 2. 系统的默认显示范围
        /// </summary>
        public DataPort<RealRange> SystemRange { get; } = new("VP_SystemRange");

        /// <summary>
        /// 3. 用户拖拽鼠标产生的意图范围
        /// </summary>
        public DataPort<RealRange> UserRange { get; } = new("VP_UserRange");

        /// <summary>
        /// 4. 大法官 (ViewportManagerFeature) 裁决后的最终生效范围
        /// </summary>
        public DataPort<RealRange> ActiveRange { get; } = new("VP_ActiveRange");

        /// <summary>
        /// 5. 💥 底层 0-GC 切片引擎 (LinkViewportStream) 吐出的“真实物理起点”
        /// </summary>
        public DataPort<int> Offset { get; } = new("VP_Offset");
    }
}
