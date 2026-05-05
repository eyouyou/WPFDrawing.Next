using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 💥 智能 X 轴提供者：支持策略注入与自动路由。
    /// 泛型 TX 只用于描述 XAxis 原始数据列的元素类型（如 DateTime），
    /// 与刻度逻辑域本身无关 —— 刻度域严格是 double（索引 / 数值）。
    /// </summary>
    public class DomainTickProvider<TX> : ITickProvider
    {
        private readonly DataPort<ReadOnlyMemory<TX>> _dataPort;
        private readonly ViewportPorts? _viewport;
        private readonly string _format;
        private readonly IHevoFormatter? _formatter;

        // 💥 终极 0-GC 改造 1：引入私有数据盒子！
        // Provider 只在图表组装时实例化一次，因此这个盒子终身只分配一次内存。
        private readonly RefBox<ReadOnlyMemory<TX>> _dataBox = new();

        // 💥 终极 0-GC 改造 2：策略工厂不再接收一次性切片，而是接收长生命周期的盒子！
        private readonly Func<RefBox<ReadOnlyMemory<TX>>, ITickStrategy>? _strategyFactory;

        public DomainTickProvider(
            DataPort<ReadOnlyMemory<TX>> dataPort,
            ViewportPorts? viewport,
            string format = "G",
            IHevoFormatter? formatter = null,
            Func<RefBox<ReadOnlyMemory<TX>>, ITickStrategy>? strategyFactory = null)
        {
            _dataPort = dataPort;
            _viewport = viewport;
            _format = format;
            _formatter = formatter;
            _strategyFactory = strategyFactory;
        }

        public ITickStrategy GetStrategy(FeatureContext ctx)
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

        public ITickStylePolicy GetStyle(FeatureContext ctx)
        {
            // 再次确保渲染刻度文本时，盒子里的数据也是最新帧
            var (col, _) = ctx.UsePort(_dataPort);
            _dataBox.Value = col;

            // 删 Slicer 后，世界索引 == 数组下标。formatter 直接用 mathIndex 查表。
            return ctx.UseMemo((_format, _formatter), deps =>
                new DelegateTickStylePolicy(mathIndex =>
                {
                    var span = _dataBox.Value.Span;
                    int idx = (int)Math.Round(mathIndex);
                    if (idx >= 0 && idx < span.Length)
                    {
                        return span[idx].FormatValue(deps._format, deps._formatter);
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
    public class NumericTickProvider : ITickProvider
    {
        private readonly string _format;
        private readonly IHevoFormatter? _provider;
        private readonly ITickStrategy? _customStrategy;

        public NumericTickProvider(
            string format = "F2",
            IHevoFormatter? provider = null,
            ITickStrategy? strategy = null)
        {
            _format = format;
            _provider = provider;
            _customStrategy = strategy;
        }

        public ITickStrategy GetStrategy(FeatureContext ctx)
        {
            // 🚀 注入优先：如果外部指定了策略，直接返回
            if (_customStrategy != null) return _customStrategy;

            // 💥 这里的依赖已经是 this，所以只会 new 一次，绝对安全 0-GC！
            return ctx.UseMemo(this, _ => new TradingViewYAxisMathStrategy()).Value;
        }

        public ITickStylePolicy GetStyle(FeatureContext ctx)
        {
            // 💥 这里依赖的是 (_format, _provider)，这两个都是只读字段，不会变，
            // 所以这里的委托也只会被 new 一次，完美！
            return ctx.UseMemo((_format, _provider),
                    args => new DelegateTickStylePolicy(val =>
                    {
                        if (Math.Abs(val) < MathTolerance.NumericEqual) val = 0;
                        return val.FormatValue(args._format, args._provider);
                    })).Value;
        }
    }

    /// <summary>
    /// 锚点感知 Y 轴提供者：把 anchor / high-low hints 端口喂给 <see cref="AnchoredNiceTickStrategy"/>。
    ///
    /// 典型场景——分时图：
    /// <list type="bullet">
    ///   <item><c>anchorPort</c> = 昨收价（grid 原点 + baseline）</item>
    ///   <item><c>hintsPort</c> = 当日 high/low（尽量显示，离 grid 太近时让位 grid，永远不让位 baseline）</item>
    /// </list>
    /// 端口值通过 <see cref="RefBox{T}"/> 喂给策略，保持 0-GC 与 X 轴 <c>DomainTickProvider</c> 一致。
    /// </summary>
    public class AnchoredNumericTickProvider : ITickProvider
    {
        private readonly string _format;
        private readonly IHevoFormatter? _provider;
        private readonly DataPort<double> _anchorPort;
        private readonly DataPort<RealRange> _hintsPort;
        private readonly IHevoBrush? _baselineTextBrush;
        private readonly LineStyle? _baselineLineStyle;

        // 终身只分配一次的盒子——provider 实例本身寿命跟图表一致
        private readonly RefBox<double> _anchorBox = new() { Value = double.NaN };
        private readonly RefBox<RealRange> _hintsBox = new() { Value = RealRange.Empty };

        /// <summary>
        /// 锚点感知 Y 轴 Provider。两个端口均必填以满足 HEVO003 (UsePort 必须无条件调用)；
        /// 如果业务只要其中一种锚点，把另一个端口写一个永远 NaN/Empty 的常量即可。
        /// </summary>
        /// <param name="baselineTextBrush">
        /// 可选：baseline 标签独立着色。非 null 时通过 <see cref="ITickStylePolicy.GetOverrideBrush"/>
        /// 命中 anchor 值返回该笔刷（优先级最高），不命中放行 null，回落到 baseline 线笔/轴默认色。
        /// </param>
        /// <param name="baselineLineStyle">
        /// 可选：anchor 处 grid 线 per-tick 样式覆盖（如"0% 线加粗"）。非 null 时通过
        /// <see cref="ITickStylePolicy.GetOverrideStyle"/> 在 anchor 值返回该 LineStyle，
        /// 由 GridLineLayer 在该 tick 用这条 pen 替代默认 grid pen。
        /// </param>
        public AnchoredNumericTickProvider(
            string format,
            DataPort<double> anchorPort,
            DataPort<RealRange> hintsPort,
            IHevoFormatter? provider = null,
            IHevoBrush? baselineTextBrush = null,
            LineStyle? baselineLineStyle = null)
        {
            _format = format;
            _provider = provider;
            _anchorPort = anchorPort;
            _hintsPort = hintsPort;
            _baselineTextBrush = baselineTextBrush;
            _baselineLineStyle = baselineLineStyle;
        }

        public ITickStrategy GetStrategy(FeatureContext ctx)
        {
            // 1. 同步喂盒子（每帧；UsePort 顺序固定，符合 HEVO003）
            var (anchor, _) = ctx.UsePort(_anchorPort);
            _anchorBox.Value = anchor;

            var (hints, _) = ctx.UsePort(_hintsPort);
            _hintsBox.Value = hints;

            // 2. 终身 1 次构造策略实例
            return ctx.UseMemo(this, _ => new AnchoredNiceTickStrategy(_anchorBox, _hintsBox)).Value;
        }

        public ITickStylePolicy GetStyle(FeatureContext ctx)
        {
            // 闭包捕获 _anchorBox（引用稳定）以便 brush / styleSelector 每次读到最新 anchor。
            // _anchorBox 由 GetStrategy 在同一帧先填，调用顺序见 AxisFeature.OnProject。
            var anchorBox = _anchorBox;
            var baselineBrush = _baselineTextBrush;
            var baselineLine = _baselineLineStyle;
            return ctx.UseMemo((_format, _provider, baselineBrush, baselineLine, anchorBox),
                args => new DelegateTickStylePolicy(
                    formatter: val =>
                    {
                        if (Math.Abs(val) < MathTolerance.NumericEqual) val = 0;
                        return val.FormatValue(args._format, args._provider);
                    },
                    brushSelector: args.baselineBrush == null ? null : val =>
                    {
                        var anchor = args.anchorBox.Value;
                        if (double.IsNaN(anchor)) return null;
                        // 判 val 是否就是 anchor(典型:把昨收价那一根 tick 高亮上色)
                        return Math.Abs(val - anchor) < MathTolerance.NumericEqual ? args.baselineBrush : null;
                    },
                    styleSelector: args.baselineLine == null ? null : val =>
                    {
                        var anchor = args.anchorBox.Value;
                        if (double.IsNaN(anchor)) return null;
                        return Math.Abs(val - anchor) < MathTolerance.NumericEqual ? args.baselineLine : null;
                    })).Value;
        }
    }
}
