using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 💥 智能 X 轴提供者：支持策略注入与自动路由
    /// </summary>
    public class DomainTickProvider<TX> : ITickProvider<double>
    {
        private readonly DataPort<ReadOnlyMemory<TX>> _dataPort;
        private readonly ViewportPorts? _viewport;
        private readonly string _format;
        private readonly IHevoFormatter? _formatter;

        // 💥 终极 0-GC 改造 1：引入私有数据盒子！
        // Provider 只在图表组装时实例化一次，因此这个盒子终身只分配一次内存。
        private readonly RefBox<ReadOnlyMemory<TX>> _dataBox = new();

        // 💥 终极 0-GC 改造 2：策略工厂不再接收一次性切片，而是接收长生命周期的盒子！
        private readonly Func<RefBox<ReadOnlyMemory<TX>>, ITickStrategy<double>>? _strategyFactory;

        public DomainTickProvider(
            DataPort<ReadOnlyMemory<TX>> dataPort,
            ViewportPorts? viewport,
            string format = "G",
            IHevoFormatter? formatter = null,
            Func<RefBox<ReadOnlyMemory<TX>>, ITickStrategy<double>>? strategyFactory = null)
        {
            _dataPort = dataPort;
            _viewport = viewport;
            _format = format;
            _formatter = formatter;
            _strategyFactory = strategyFactory;
        }

        public ITickStrategy<double> GetStrategy(FeatureContext ctx)
        {
            // 💥 1. 从黑板提取当前帧真实数据流
            var (col, _) = ctx.UsePort(_dataPort);

            // 💥 2. 极速数据穿透！将新切片刷入盒子。
            // 仅发生 struct 的 16 字节值拷贝，绝对不触发任何堆内存分配 (0-GC)！
            _dataBox.Value = col;

            // 💥 3. 神级 Hook 闭环：依赖项设为 `this`！
            // 因为 Provider (this) 的内存地址永远不变，所以 UseMemo 永远不会脏。
            // 策略实例终身只 new 1 次！它只要从 _dataBox 取数据，永远是最新的！
            return ctx.UseMemo(this, _ =>
            {
                // 🚀 外部注入了策略工厂？把盒子交给它！
                if (_strategyFactory != null)
                {
                    return _strategyFactory(_dataBox);
                }

                // 默认智能路由逻辑
                if (typeof(TX) == typeof(DateTime))
                {
                    // 泛型擦除安全强转：因为只有 TX 是 DateTime 时才会进这个分支
                    var timeBox = (RefBox<ReadOnlyMemory<DateTime>>)(object)_dataBox;
                    return new TradingViewTimeMathStrategy(timeBox);
                }

                return new TradingViewAxisMathStrategy();
            }).Value;
        }

        public ITickStylePolicy<double> GetStyle(FeatureContext ctx)
        {
            // 再次确保渲染刻度文本时，盒子里的数据也是最新帧
            var (col, _) = ctx.UsePort(_dataPort);
            _dataBox.Value = col;

            var (offset, _) = _viewport != null ? ctx.UsePort(_viewport.Offset) : (0, false);

            // 💥 4. 致命 GC 修复：绝不能把 col 放进依赖元组里！
            // 这里我们只依赖 offset（视口平移）、_format 和 _formatter。
            // 这样，DelegateTickStylePolicy 只有在拖拽图表时才会重建，而在单纯跳动行情时不会重建！
            return ctx.UseMemo((offset, _format, _formatter), deps =>
                new DelegateTickStylePolicy<double>(mathIndex =>
                {
                    // 💥 5. 闭包的终极魔法：直接捕获外部的长生命周期盒子 _dataBox。
                    // 委托执行时，直接解包拿到最新 Span！
                    var span = _dataBox.Value.Span;

                    int localIndex = (int)Math.Round(mathIndex) - deps.offset;
                    if (localIndex >= 0 && localIndex < span.Length)
                    {
                        return span[localIndex].FormatValue(deps._format, deps._formatter);
                    }
                    return string.Empty;
                })).Value;
        }
    }

    /// <summary>
    /// 💥 智能 Y 轴提供者：支持策略注入（THS、TV、Adaptive）
    /// 注意：Y 轴由于不需要绑定 K 线时间序列，它的策略天然是纯数学计算，
    /// 所以无需使用 RefBox，当前写法已经非常完美。
    /// </summary>
    public class NumericTickProvider : ITickProvider<double>
    {
        private readonly string _format;
        private readonly IHevoFormatter? _provider;
        private readonly ITickStrategy<double>? _customStrategy;

        public NumericTickProvider(
            string format = "F2",
            IHevoFormatter? provider = null,
            ITickStrategy<double>? strategy = null)
        {
            _format = format;
            _provider = provider;
            _customStrategy = strategy;
        }

        public ITickStrategy<double> GetStrategy(FeatureContext ctx)
        {
            // 🚀 注入优先：如果外部指定了策略，直接返回
            if (_customStrategy != null) return _customStrategy;

            // 💥 这里的依赖已经是 this，所以只会 new 一次，绝对安全 0-GC！
            return ctx.UseMemo(this, _ => new TradingViewYAxisMathStrategy()).Value;
        }

        public ITickStylePolicy<double> GetStyle(FeatureContext ctx)
        {
            // 💥 这里依赖的是 (_format, _provider)，这两个都是只读字段，不会变，
            // 所以这里的委托也只会被 new 一次，完美！
            return ctx.UseMemo((_format, _provider),
                    args => new DelegateTickStylePolicy<double>(val =>
                    {
                        if (Math.Abs(val) < 1e-6) val = 0;
                        return val.FormatValue(args._format, args._provider);
                    })).Value;
        }
    }
}
