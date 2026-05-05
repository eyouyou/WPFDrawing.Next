using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Buildin
{
    /// <summary>
    /// 💥 Schedule-driven X 轴 tick provider —— 跟 <see cref="DomainTickProvider{TX}"/> 并列的另一种实现:
    ///
    /// <list type="bullet">
    ///   <item><see cref="DomainTickProvider{TX}"/>:数据驱动 (data-driven)。
    ///         strategy / stylePolicy 都从 <see cref="DataPort{T}"/> 读 times 数组,
    ///         tick 数量受限于实际到达的数据长度(K 线 / 通用时间轴典型场景)。</item>
    ///   <item><see cref="ScheduleTickProvider"/>:计划驱动 (schedule-driven)。
    ///         strategy / stylePolicy 都从 <c>Func&lt;int&gt; totalLength</c> + <c>Func&lt;int, DateTime&gt; indexToTime</c>
    ///         读取——索引到时间的映射独立于实际数据,因此即便数据只到达上午盘 (90 根),
    ///         下午盘 (空白区) 也能给出完整 tick 与 label(分时 / 强弱评级 / 权重表达 等业务)。</item>
    /// </list>
    ///
    /// 业务侧把 ruler.TotalLength / ruler.IndexToTime 委托给两个 Func,框架侧 0 业务依赖。
    /// </summary>
    public class ScheduleTickProvider : ITickProvider
    {
        private readonly Func<int> _totalLengthProvider;
        private readonly Func<int, DateTime> _indexToTime;
        private readonly string _format;
        private readonly IHevoFormatter? _formatter;
        private readonly Func<ITickStrategy>? _strategyFactory;

        /// <param name="totalLengthProvider">全网格逻辑长度(典型:<c>() => dataSource.LogicalLength</c>)。</param>
        /// <param name="indexToTime">逻辑索引 → 实际时间(典型:<c>idx => dataSource.IndexToTime(idx)</c>)。</param>
        /// <param name="format">label 输出格式(默认 HH:mm)。</param>
        /// <param name="formatter">可选自定义 formatter。</param>
        /// <param name="strategyFactory">可选 strategy 工厂。null 时默认用 <see cref="TradeTimeTickStrategy"/>。</param>
        public ScheduleTickProvider(
            Func<int> totalLengthProvider,
            Func<int, DateTime> indexToTime,
            string format = "HH:mm",
            IHevoFormatter? formatter = null,
            Func<ITickStrategy>? strategyFactory = null)
        {
            _totalLengthProvider = totalLengthProvider ?? throw new ArgumentNullException(nameof(totalLengthProvider));
            _indexToTime = indexToTime ?? throw new ArgumentNullException(nameof(indexToTime));
            _format = format;
            _formatter = formatter;
            _strategyFactory = strategyFactory;
        }

        public ITickStrategy GetStrategy(FeatureContext ctx)
        {
            // strategy 实例终身只 new 一次,从 _totalLengthProvider/_indexToTime 取最新值
            return ctx.UseMemo(this, _ =>
                _strategyFactory != null
                    ? _strategyFactory()
                    : new TradeTimeTickStrategy(_totalLengthProvider, _indexToTime)).Value;
        }

        public ITickStylePolicy GetStyle(FeatureContext ctx)
        {
            // label 直接走 indexToTime,跟 times 数组无关——空白区也有正确 label
            return ctx.UseMemo((_format, _formatter, _indexToTime),
                static deps => new DelegateTickStylePolicy(mathIndex =>
                {
                    int idx = (int)Math.Round(mathIndex);
                    DateTime time = deps._indexToTime(idx);
                    if (time == DateTime.MinValue) return string.Empty;
                    return time.FormatValue(deps._format, deps._formatter);
                })).Value;
        }
    }
}
