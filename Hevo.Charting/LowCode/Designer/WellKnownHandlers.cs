using Hevo.Charting.Abstractions;   // RealRange
using Hevo.Charting.Core;

namespace Hevo.Charting.LowCode.Designer
{
    /// <summary>
    /// 框架内置 Feature / Trigger 的"已知 handler 类别"集合 ——
    /// 强类型 <see cref="HandlerKey{TDelegate}"/> 的工厂,业务侧拿来直接用,不必每个项目重写一份委托类型。
    ///
    /// <para>用法对比:</para>
    /// <code>
    /// // 弱类型(AutoDiscover 反射推断,签名错配运行时才报):
    /// registry.AutoDiscover(new KLineHandlers(ds));
    ///
    /// // 强类型(签名错配编译期 CS1503):
    /// registry.Register(WellKnownHandlers.Trigger.Fetch("on_heartbeat"),                  h.OnHeartbeatAsync)
    ///         .Register(WellKnownHandlers.Paging.OnRequireDataAsync("on_paging"),         h.OnRequirePagingDataAsync)
    ///         .Register(WellKnownHandlers.Crosshair.FutureXLabel("format_future_time"),   h.FormatFutureTimeLabel);
    /// </code>
    ///
    /// <para>
    /// 两路径可并存:同一 registry 既调 AutoDiscover 也调显式 Register 没问题,
    /// 后注册的同名 entry 覆盖前者(<see cref="BlueprintHandlerRegistry.RegisterDelegate"/> 语义)。
    /// </para>
    /// <para>
    /// 业务侧自定义 Feature 上的 Delegate 属性想搭便车,新建 <c>BizWellKnownHandlers</c> 类仿写即可,
    /// 框架不必改。
    /// </para>
    /// </summary>
    public static class WellKnownHandlers
    {
        /// <summary>
        /// <see cref="ChartBlueprint.Triggers"/> Kind=Interval 用的 fetch handler ——
        /// 签名跟 <see cref="WorkFlow.Workflow"/>.FetchExclusive 锁死。
        /// </summary>
        public static class Trigger
        {
            public static HandlerKey<Func<VersionToken, CancellationToken, Task<bool>>> Fetch(string name) => new(name);
        }

        /// <summary>
        /// <see cref="Features.DataPagingFeature.OnRequireDataAsync"/> ——
        /// 拖拽到数据边缘时分页拉取的业务回调。
        /// </summary>
        public static class Paging
        {
            /// <summary>(anchorIndex, count) → fetchedCount &gt; 0</summary>
            public static HandlerKey<Func<int, int, Task<bool>>> OnRequireDataAsync(string name) => new(name);
        }

        /// <summary>
        /// <see cref="Features.CrosshairFeature{TX}"/> 上的 3 个 formatter 委托。
        /// </summary>
        public static class Crosshair
        {
            /// <summary>X 轴超出数据范围时的标签 (logicalIndex → 文字),典型用于推算未来时间。</summary>
            public static HandlerKey<Func<int, string>> FutureXLabel(string name) => new(name);

            /// <summary>X 轴在数据范围内的标签 (TX → 文字)。TX 一般是 double 索引,业务用 DateTime 时另写一份。</summary>
            public static HandlerKey<Func<double, string>> XLabelDouble(string name) => new(name);

            /// <summary>DateTime X 轴版的 XLabel formatter (CrosshairDateTimeFeature 用)。</summary>
            public static HandlerKey<Func<DateTime, string>> XLabelDateTime(string name) => new(name);

            /// <summary>Y 轴标签 (double → 文字),典型用于价格 / 量值。</summary>
            public static HandlerKey<Func<double, string>> YLabel(string name) => new(name);
        }

        /// <summary>
        /// <see cref="Features.ChartInteractionFeature.CustomReset"/> ——
        /// 业务自定义 viewport 复位逻辑(double 击 / R 键),返回新的 RealRange。
        /// </summary>
        public static class Interaction
        {
            public static HandlerKey<Func<int, RealRange>> CustomReset(string name) => new(name);
        }
    }
}
