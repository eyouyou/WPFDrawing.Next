using Hevo.Charting.Core;
using System.Buffers;

namespace Hevo.Charting.LowCode
{
    // ==========================================
    // 1. 标量连线器 (Context Ingestor)
    // ==========================================
    internal class ContextIngestor<TItem, TSource, TValue> : IDataIngestor<TItem>
    {
        private readonly TSource _source;
        private readonly Func<TSource, TValue> _selector;
        private readonly DataPort<TValue> _port;

        // 💥 O(1) 查脏哨兵
        private VersionToken _lastVersion;

        public ContextIngestor(DataPort<TValue> port, TSource source, Func<TSource, TValue> selector)
        {
            _port = port; _source = source; _selector = selector;
        }

        public void Process(DataSnapshot<TItem> snapshot, DataBlackboard board)
        {
            if (snapshot.Version == _lastVersion) return;
            _lastVersion = snapshot.Version;

            board.WriteIfChanged(_port, _selector(_source));
        }
    }

    // ==========================================
    // 2. 数组流连线器 (Scatter Ingestor) - 0 GC 极致性能
    // ==========================================
    internal class ScatterIngestor<TItem, TValue> : IDataIngestor<TItem>, IDisposable
    {
        private readonly DataPort<ReadOnlyMemory<TValue>> _targetPort;
        private readonly Func<int> _lengthProvider;
        private readonly TValue _defaultValue;
        private readonly Func<TItem, int>? _indexSelector;
        private readonly Func<TItem, TValue> _valueSelector;

        private VersionToken _lastVersion;
        private TValue[]? _rentedArray;

        public ScatterIngestor(DataPort<ReadOnlyMemory<TValue>> targetPort, Func<int> lengthProvider, TValue defaultValue, Func<TItem, int>? indexSelector, Func<TItem, TValue> valueSelector)
        {
            _targetPort = targetPort; _lengthProvider = lengthProvider; _defaultValue = defaultValue; _indexSelector = indexSelector; _valueSelector = valueSelector;
        }

        public void Process(DataSnapshot<TItem> snapshot, DataBlackboard board)
        {
            // 💥 数据纪元未变，直接 0 开销滚蛋！
            if (snapshot.Version == _lastVersion) return;
            _lastVersion = snapshot.Version;

            // 💥 瞬间转为栈上的极速指针
            ReadOnlySpan<TItem> sourceSpan = snapshot.AsSpan();

            int exactLength = _lengthProvider();
            if (exactLength <= 0) return;

            if (_rentedArray == null || _rentedArray.Length < exactLength)
            {
                if (_rentedArray != null) ArrayPool<TValue>.Shared.Return(_rentedArray, clearArray: false);
                _rentedArray = ArrayPool<TValue>.Shared.Rent(exactLength);
            }

            Span<TValue> span = _rentedArray.AsSpan(0, exactLength);
            span.Fill(_defaultValue);

            // 💥 极速离散映射，直接操作 Span
            for (int i = 0; i < sourceSpan.Length; i++)
            {
                int idx = _indexSelector != null ? _indexSelector(sourceSpan[i]) : i;
                if ((uint)idx < (uint)exactLength) span[idx] = _valueSelector(sourceSpan[i]);
            }

            board.ForceWrite(_targetPort, new ReadOnlyMemory<TValue>(_rentedArray, 0, exactLength));
        }

        public void Dispose()
        {
            if (_rentedArray != null) { ArrayPool<TValue>.Shared.Return(_rentedArray, clearArray: false); _rentedArray = null; }
        }
    }

    /// <summary>
    /// 💥 多路分支摄入器：一次提取，多处焊线
    /// </summary>
    internal class ContextBranchingIngestor<TContext, TItem, TValue> : IDataIngestor<TItem>
    {
        private readonly TContext _context;
        private readonly Func<TContext, TValue> _selector;
        private readonly List<Action<TValue, DataBlackboard>> _routers = new();

        private VersionToken _lastVersion;

        public ContextBranchingIngestor(TContext context, Func<TContext, TValue> selector)
        {
            _context = context; _selector = selector;
        }

        public void AddRouter(Action<TValue, DataBlackboard> router) => _routers.Add(router);

        public void Process(DataSnapshot<TItem> snapshot, DataBlackboard board)
        {
            if (snapshot.Version == _lastVersion) return;
            _lastVersion = snapshot.Version;

            TValue val = _selector(_context);
            foreach (var route in _routers) route(val, board);
        }
    }
}
