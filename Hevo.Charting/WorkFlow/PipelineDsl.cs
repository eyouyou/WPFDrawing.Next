using Hevo.Charting.Core;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.WorkFlow
{
    // ==========================================
    // 💥 强类型管道构建器：完美接管 CRTP 的派生类类型
    // [架构哲学]：向外提供 Fluent API，支持无限连写，绝不主动封口。
    // ==========================================
    public class DataPipeBuilder<TSource, TItem> where TSource : DataSource<TSource, TItem>
    {
        private readonly TSource _dataSource; // 💥 强类型的大管家！
        private readonly IWorkflow<DataSnapshot<TItem>> _stream;
        private Action? _onDisposeCallbacks;
        internal readonly UniversalDataPipe<TItem> _pipe;

        internal TSource DataSource => _dataSource;

        internal DataPipeBuilder(TSource dataSource, IWorkflow<DataSnapshot<TItem>> stream)
        {
            _dataSource = dataSource;
            _stream = stream;
            _pipe = new UniversalDataPipe<TItem>();
        }

        /// <summary>
        /// 💥 架构护城河：注册生命周期销毁钩子。
        /// 允许管线在被外部（如 ChartCell）销毁时，顺手带走其他关联资源。
        /// </summary>
        public DataPipeBuilder<TSource, TItem> OnDispose(Action action)
        {
            _onDisposeCallbacks += action;
            return this;
        }

        /// <summary>
        /// 💥 外部上下文注入器 (重载 1：注入任意其他环境，如 ThemeManager)
        /// </summary>
        public ValueLinker<TContext, TItem, TValue, TSource> InjectContext<TContext, TValue>(TContext context, Func<TContext, TValue> selector)
        {
            var ingestor = new ContextBranchingIngestor<TContext, TItem, TValue>(context, selector);
            _pipe.AddIngestor(ingestor);
            return new ValueLinker<TContext, TItem, TValue, TSource>(this, ingestor);
        }

        /// <summary>
        /// 💥 神级语法糖 (重载 2)：默认注入当前大管家！
        /// 优势：直接 .Inject(ds => ds.PreClose)，底层自动注入 _dataSource。
        /// C# 编译器完美推断 ds 是 TimeShareDataSource，无任何报错！
        /// </summary>
        public ValueLinker<TSource, TItem, TValue, TSource> Inject<TValue>(Func<TSource, TValue> selector)
        {
            return InjectContext(_dataSource, selector);
        }

        /// <summary>
        /// 💥 架构级语法糖：内置于构建器中的原生视口驱动器！
        /// 彻底绕过 C# 泛型扩展推断的缺陷，全自动推断，100% 0 报错！
        /// </summary>
        public DataPipeBuilder<TSource, TItem> ProjectExtent(ViewportPorts vp, Action<ValueLinker<TSource, TItem, int, TSource>>? extra = null)
        {
            // 在内部调用自己的 Inject，逻辑极其自洽
            var linker = this.Inject(ds => ds.LogicalLength);
            if (vp != null) linker.ForwardTo(vp.LogicalLength);
            extra?.Invoke(linker);
            return linker.End();
        }

        // ==========================================
        // 💥 阶段 1：向量流装配 (基础数据映射)
        // 升级：不再强制 Seal，返回 Builder 自身以支持连写！
        // ==========================================
        public DataPipeBuilder<TSource, TItem> LinkStream(Action<ScatterConfigurator<TSource, TItem>> scatterAction, Func<TItem, int>? indexSelector = null)
        {
            // 💥 底层自动提取大管家的引用喂给切片器，完美实现 0-GC 闭包透传！
            var configurator = new ScatterConfigurator<TSource, TItem>(_pipe, _dataSource, indexSelector, () => _dataSource.LogicalLength);
            scatterAction(configurator);

            return this; // 开放链式调用
        }

        // ==========================================
        // 💥 阶段 2：派生数学计算节点 (独立算子)
        // ==========================================
        public DataPipeBuilder<TSource, TItem> Compute<TState>(TState state, Action<DataBlackboard, TSource, TState> computeAction)
        {
            // 直接将纯计算节点追加到执行管线的末尾，与 LinkStream 享有同一把事务锁！
            _pipe.AddIngestor(new ComputeIngestor<TSource, TItem, TState>(_dataSource, state, computeAction));
            return this;
        }

        // ==========================================
        // 💥 管线收束终结符
        // ==========================================
        public IRenderFlow<DataBlackboard> BindTo(ChartCell chart)
        {
            return this.Seal().BindTo(chart);
        }

        /// <summary>
        /// 💥 终极解耦黑科技：向扩展方法开放的“生命周期缝合”接口
        /// </summary>
        public IWorkflow<DataBlackboard> Seal()
        {
            // 💥 绝杀修复：在管线的源头强行注入一次大管家的当前快照！
            // 这样无论下游（图表）什么时候来 Subscribe，都能瞬间拿到当前的最新状态（哪怕是空的），绝不丢失第一帧！
            var boardStream = _stream
                .StartWith(_dataSource.GetSnapshot()) // 👈👈👈 就加这一行！！！
                .Select(snapshot => _pipe.Process(snapshot));

            return boardStream.DoOnDispose(() =>
            {
                _pipe.Dispose();
                _onDisposeCallbacks?.Invoke();
            });
        }

        /// <summary>
        /// 💥 重算逻辑：引擎每一帧或事务合并时调用
        /// 修复：全面适配 DataSnapshot 唯一真理模型！
        /// </summary>
        public void Reevaluate(DataBlackboard board)
        {
            var snapshot = _dataSource.GetSnapshot();

            // 3. 极速投喂 (0-GC)
            if (snapshot.Count > 0)
            {
                _pipe.ProcessTo(snapshot, board);
            }
        }

        // 别忘了提供一个只读暴露，供专属 Configurator 使用
        internal UniversalDataPipe<TItem> InternalPipe => _pipe;
    }

    // ==========================================
    // 💥 标量分支路由器 (持有强类型的 TSource 用于 End() 链式返回)
    // ==========================================
    public class ValueLinker<TContext, TItem, TValue, TSource> where TSource : DataSource<TSource, TItem>
    {
        private readonly DataPipeBuilder<TSource, TItem> _parent;
        private readonly ContextBranchingIngestor<TContext, TItem, TValue> _ingestor;

        internal ValueLinker(DataPipeBuilder<TSource, TItem> parent, ContextBranchingIngestor<TContext, TItem, TValue> ingestor)
        {
            _parent = parent; _ingestor = ingestor;
        }

        public ValueLinker<TContext, TItem, TValue, TSource> ForwardTo(DataPort<TValue> port)
        {
            _ingestor.AddRouter((val, board) => board.WriteIfChanged(port, val));
            return this;
        }

        public ValueLinker<TContext, TItem, TValue, TSource> ForwardTo<TMeta>(DataPort<TMeta> metaPort, Func<TMeta, TValue, TMeta> updater)
        {
            _ingestor.AddRouter((val, board) =>
            {
                var currentMeta = board.Read(metaPort);
                if (currentMeta != null) board.WriteIfChanged(metaPort, updater(currentMeta, val));
            });
            return this;
        }

        /// <summary>
        /// 回到主构建器
        /// </summary>
        public DataPipeBuilder<TSource, TItem> End() => _parent;
    }

    // ==========================================
    // 💥 AoS 向量切片配置器 (升级：全量支持 TSource 泛型透传)
    // ==========================================
    public class ScatterConfigurator<TSource, TItem> where TSource : DataSource<TSource, TItem>
    {
        private readonly UniversalDataPipe<TItem> _pipe;
        private readonly Func<TItem, int>? _indexSelector;
        private readonly Func<int> _nativeLengthProvider;

        internal TSource Source { get; } // 💥 核心透传引擎

        internal ScatterConfigurator(UniversalDataPipe<TItem> pipe, TSource source, Func<TItem, int>? indexSelector, Func<int> nativeLengthProvider)
        {
            _pipe = pipe; Source = source; _indexSelector = indexSelector; _nativeLengthProvider = nativeLengthProvider;
        }

        internal UniversalDataPipe<TItem> InternalPipe => _pipe;
        internal Func<int> NativeLengthProvider => _nativeLengthProvider;

        // 1. 常规标量映射 (保留兼容)
        public ScatterConfigurator<TSource, TItem> Map<TValue>(DataPort<ReadOnlyMemory<TValue>> targetPort, Func<TItem, TValue> valueSelector, TValue defaultValue = default!)
        {
            _pipe.AddIngestor(new ScatterIngestor<TItem, TValue>(targetPort, _nativeLengthProvider, defaultValue, _indexSelector, valueSelector));
            return this;
        }

        // 2. 💥 自动 Source 透传 Map：消灭外部变量的_dataSource闭包，强迫 0-GC！
        public ScatterConfigurator<TSource, TItem> Map<TValue>(DataPort<ReadOnlyMemory<TValue>> targetPort, Func<TItem, TSource, TValue> selector)
        {
            _pipe.AddIngestor(new FastSourceMapIngestor<TSource, TItem, TValue>(targetPort, _nativeLengthProvider, Source, selector));
            return this;
        }

        // 3. 💥 多状态透传 Map：想传多个参数？直接塞 Tuple 元组，全部 0-GC！
        public ScatterConfigurator<TSource, TItem> Map<TValue, TState>(DataPort<ReadOnlyMemory<TValue>> targetPort, TState state, Func<TItem, TSource, TState, TValue> selector)
        {
            _pipe.AddIngestor(new FastStateMapIngestor<TSource, TItem, TValue, TState>(targetPort, _nativeLengthProvider, Source, state, selector));
            return this;
        }

        // 💥 官方插件接入口！允许挂载任何自定义的 Ingestor
        public ScatterConfigurator<TSource, TItem> Plug(IDataIngestor<TItem> ingestor)
        {
            _pipe.AddIngestor(ingestor);
            return this;
        }
    }

    // ==========================================
    // 💥 1. 0-GC 极速映射算子 (透传 Source)
    // ==========================================
    internal class FastSourceMapIngestor<TSource, TItem, TValue> : IDataIngestor<TItem>
    {
        private readonly DataPort<ReadOnlyMemory<TValue>> _port;
        private readonly Func<int> _lengthProvider;
        private readonly TSource _source;
        private readonly Func<TItem, TSource, TValue> _selector;

        private VersionToken _lastVersion; // 💥 显式查脏哨兵
        private TValue[] _buffer = Array.Empty<TValue>();

        public FastSourceMapIngestor(DataPort<ReadOnlyMemory<TValue>> port, Func<int> lenProv, TSource source, Func<TItem, TSource, TValue> selector)
        {
            _port = port; _lengthProvider = lenProv; _source = source; _selector = selector;
        }

        public void Process(DataSnapshot<TItem> snapshot, DataBlackboard board)
        {
            // 💥 极速查脏：宁愿重复这行代码，也要保持类的扁平和直观！
            if (snapshot.Version == _lastVersion) return;
            _lastVersion = snapshot.Version;

            ReadOnlySpan<TItem> items = snapshot.AsSpan();
            int len = _lengthProvider();
            if (len <= 0) return;

            if (_buffer.Length < len) Array.Resize(ref _buffer, len);

            for (int i = 0; i < items.Length; i++)
            {
                _buffer[i] = _selector(items[i], _source);
            }

            board.WriteIfChanged(_port, _buffer.AsMemory(0, items.Length));
        }
    }

    // ==========================================
    // 💥 2. 0-GC 多状态极速映射算子 (透传 Source + State)
    // ==========================================
    internal class FastStateMapIngestor<TSource, TItem, TValue, TState> : IDataIngestor<TItem>
    {
        private readonly DataPort<ReadOnlyMemory<TValue>> _port;
        private readonly Func<int> _lengthProvider;
        private readonly TSource _source;
        private readonly TState _state;
        private readonly Func<TItem, TSource, TState, TValue> _selector;

        private VersionToken _lastVersion; // 💥 显式查脏哨兵
        private TValue[] _buffer = Array.Empty<TValue>();

        public FastStateMapIngestor(DataPort<ReadOnlyMemory<TValue>> port, Func<int> lenProv, TSource source, TState state, Func<TItem, TSource, TState, TValue> selector)
        {
            _port = port; _lengthProvider = lenProv; _source = source; _state = state; _selector = selector;
        }

        public void Process(DataSnapshot<TItem> snapshot, DataBlackboard board)
        {
            if (snapshot.Version == _lastVersion) return;
            _lastVersion = snapshot.Version;

            ReadOnlySpan<TItem> items = snapshot.AsSpan();
            int len = _lengthProvider();
            if (len <= 0) return;

            if (_buffer.Length < len) Array.Resize(ref _buffer, len);

            for (int i = 0; i < items.Length; i++)
            {
                _buffer[i] = _selector(items[i], _source, _state);
            }

            board.WriteIfChanged(_port, _buffer.AsMemory(0, items.Length));
        }
    }

    // ==========================================
    // 💥 3. 独立派生计算算子 (Compute)
    // ==========================================
    internal class ComputeIngestor<TSource, TItem, TState> : IDataIngestor<TItem>
    {
        private readonly TSource _source;
        private readonly TState _state;
        private readonly Action<DataBlackboard, TSource, TState> _computeAction;

        private VersionToken _lastVersion; // 💥 显式查脏哨兵

        public ComputeIngestor(TSource source, TState state, Action<DataBlackboard, TSource, TState> computeAction)
        {
            _source = source; _state = state; _computeAction = computeAction;
        }

        public void Process(DataSnapshot<TItem> snapshot, DataBlackboard board)
        {
            if (snapshot.Version == _lastVersion) return;
            _lastVersion = snapshot.Version;

            // Compute 节点属于第二阶段的派生计算，不需要遍历 Span。
            // 直接读取黑板上第一阶段 Map 好的基础数据，执行纯数学运算！
            _computeAction(board, _source, _state);
        }
    }
}
