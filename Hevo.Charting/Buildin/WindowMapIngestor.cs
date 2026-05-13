using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using System.Buffers;

namespace Hevo.Charting.Buildin
{
    internal class WindowMapIngestor<TSource, TItem> : IDataIngestor<TItem>, IDisposable
    {
        private readonly DataPort<ReadOnlyMemory<double>> _targetPort;
        private readonly Func<int> _lengthProvider;
        private readonly TSource _source; // 💥 核心 1：持有大管家引用
        private readonly Func<TItem, TSource, double> _extractor; // 💥 核心 2：要求业务层提供双参委托
        private readonly Func<double, double, double> _reducer;
        private readonly int _period;

        private VersionToken _lastVersion;
        private double[]? _rentedArray;

        public WindowMapIngestor(
            DataPort<ReadOnlyMemory<double>> targetPort,
            Func<int> lengthProvider,
            TSource source,
            int period,
            Func<TItem, TSource, double> extractor,
            Func<double, double, double> reducer)
        {
            _targetPort = targetPort;
            _lengthProvider = lengthProvider;
            _source = source;
            _period = period;
            _extractor = extractor;
            _reducer = reducer;
        }

        public void Process(DataSnapshot<TItem> snapshot, DataBlackboard board)
        {
            // 💥 防抖
            if (snapshot.Version == _lastVersion) return;
            _lastVersion = snapshot.Version;

            // 💥 获取极速指针
            ReadOnlySpan<TItem> sourceSpan = snapshot.AsSpan();

            int len = _lengthProvider();
            if (len <= 0) return;

            if (_rentedArray == null || _rentedArray.Length < len)
            {
                if (_rentedArray != null) ArrayPool<double>.Shared.Return(_rentedArray);
                _rentedArray = ArrayPool<double>.Shared.Rent(len);
            }

            Span<double> span = _rentedArray.AsSpan(0, len);

            // 💥 内存降维：直接基于 Span 提取，并将大管家透传给业务层！
            double[] raw = ArrayPool<double>.Shared.Rent(len);
            for (int i = 0; i < sourceSpan.Length; i++)
            {
                raw[i] = _extractor(sourceSpan[i], _source); // 👈 完美消灭闭包
            }

            // 执行滑动窗口数学计算
            for (int i = 0; i < len; i++)
            {
                span[i] = i < _period ? double.NaN : _reducer(raw[i], raw[i - _period]);
            }

            ArrayPool<double>.Shared.Return(raw);
            board.ForceWrite(_targetPort, new ReadOnlyMemory<double>(_rentedArray, 0, len));
        }

        public void Dispose()
        {
            if (_rentedArray != null) { ArrayPool<double>.Shared.Return(_rentedArray); _rentedArray = null; }
        }
    }

    // ==========================================
    // 2. 摄入器语法糖 (供业务 Schema 链式调用)
    // ==========================================
    public static class ScatterConfiguratorExtensions
    {
        // 💥 核心 3：升级为双泛型，返回自身以支持无限连写！
        public static ScatterConfigurator<TSource, TItem> MapWindow<TSource, TItem>(
            this ScatterConfigurator<TSource, TItem> cfg,
            DataPort<ReadOnlyMemory<double>> targetPort,
            int period, // 为了语义清晰，建议把必填参数 period 往前挪
            Func<TItem, TSource, double> selector,
            Func<double, double, double> reducer)
            where TSource : BufferedDataSource<TSource, TItem>
        {
            cfg.Plug(new WindowMapIngestor<TSource, TItem>(
                targetPort,
                cfg.NativeLengthProvider,
                cfg.Source, // 👈 从配置器中拔出大管家，喂给底层算子
                period,
                selector,
                reducer));

            return cfg; // 💥 返回自身！
        }
    }
}
