using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using System.Buffers;

namespace Hevo.Charting.WorkFlow
{
    // ==========================================
    // 💥 专属 Span 委托：突破 C# 泛型限制！
    // 因为 ReadOnlySpan 是 ref struct，不能塞进 Func<T> 里，必须自定义委托
    // ==========================================
    public delegate double[] AoSSpanExtractor<TSource, TItem>(ReadOnlySpan<TItem> sourceSpan, TSource sourceRef);
    public delegate double[] SoASpanExtractor<TBlock>(ReadOnlySpan<TBlock> sourceSpan);

    // ==========================================
    // 💥 AoS 核心适配器：序列变换摄入器 (已升级 0-GC 与瞬间查脏)
    // ==========================================
    internal class AoSSequenceTransformIngestor<TSource, TItem> : IDataIngestor<TItem>, IDisposable
    {
        private readonly DataPort<ReadOnlyMemory<double>> _targetPort;
        private readonly Func<int> _lengthProvider;
        private readonly TSource _sourceRef; // 💥 大管家引用透传
        private readonly AoSSpanExtractor<TSource, TItem> _sourceExtractor;
        private readonly ISequenceTransform _transform;

        private VersionToken _lastVersion; // 💥 查脏哨兵
        private double[]? _rentedArray;

        public AoSSequenceTransformIngestor(
            DataPort<ReadOnlyMemory<double>> targetPort,
            Func<int> lengthProvider,
            TSource sourceRef,
            AoSSpanExtractor<TSource, TItem> sourceExtractor,
            ISequenceTransform transform)
        {
            _targetPort = targetPort; _lengthProvider = lengthProvider; _sourceRef = sourceRef;
            _sourceExtractor = sourceExtractor; _transform = transform;
        }

        public void Process(DataSnapshot<TItem> snapshot, DataBlackboard board)
        {
            // 💥 极速查脏
            if (snapshot.Version == _lastVersion) return;
            _lastVersion = snapshot.Version;

            int len = _lengthProvider();
            if (len <= 0) return;

            // 1. 目标内存智能扩容
            if (_rentedArray == null || _rentedArray.Length < len)
            {
                if (_rentedArray != null) ArrayPool<double>.Shared.Return(_rentedArray);
                _rentedArray = ArrayPool<double>.Shared.Rent(len);
            }

            // 2. 榨取降维：将 Span 投喂给自定义委托，并透传 _sourceRef 消除闭包！
            double[] rawSource = _sourceExtractor(snapshot.AsSpan(), _sourceRef);

            // 3. 💥 跨界调用：将 Span 投喂给纯数学变换层
            _transform.Transform(rawSource.AsSpan(0, len), _rentedArray.AsSpan(0, len));

            // 4. 打扫战场并上板
            ArrayPool<double>.Shared.Return(rawSource);
            board.ForceWrite(_targetPort, new ReadOnlyMemory<double>(_rentedArray, 0, len));
        }

        public void Dispose()
        {
            if (_rentedArray != null) { ArrayPool<double>.Shared.Return(_rentedArray); _rentedArray = null; }
        }
    }

    // ==========================================
    // 💥 SoA 核心适配器：序列变换摄入器
    // ==========================================
    internal class SoASequenceTransformIngestor<TBlock> : IDataIngestor<TBlock>, IDisposable
    {
        private readonly DataPort<ReadOnlyMemory<double>> _targetPort;
        private readonly Func<int> _lengthProvider;
        private readonly SoASpanExtractor<TBlock> _sourceExtractor;
        private readonly ISequenceTransform _transform;

        private VersionToken _lastVersion;
        private double[]? _rentedArray;

        public SoASequenceTransformIngestor(
            DataPort<ReadOnlyMemory<double>> targetPort,
            Func<int> lengthProvider,
            SoASpanExtractor<TBlock> sourceExtractor,
            ISequenceTransform transform)
        {
            _targetPort = targetPort; _lengthProvider = lengthProvider;
            _sourceExtractor = sourceExtractor; _transform = transform;
        }

        public void Process(DataSnapshot<TBlock> snapshot, DataBlackboard board)
        {
            if (snapshot.Version == _lastVersion) return;
            _lastVersion = snapshot.Version;

            int len = _lengthProvider();
            if (len <= 0) return;

            if (_rentedArray == null || _rentedArray.Length < len)
            {
                if (_rentedArray != null) ArrayPool<double>.Shared.Return(_rentedArray);
                _rentedArray = ArrayPool<double>.Shared.Rent(len);
            }

            // SoA 榨取直接传入 Span
            double[] rawSource = _sourceExtractor(snapshot.AsSpan());

            _transform.Transform(rawSource.AsSpan(0, len), _rentedArray.AsSpan(0, len));

            ArrayPool<double>.Shared.Return(rawSource);
            board.ForceWrite(_targetPort, new ReadOnlyMemory<double>(_rentedArray, 0, len));
        }

        public void Dispose()
        {
            if (_rentedArray != null) { ArrayPool<double>.Shared.Return(_rentedArray); _rentedArray = null; }
        }
    }

    // ==========================================
    // 💡 官方语法糖：为配置器提供高雅的扩展方法
    // ==========================================
    public static class TransformExtensions
    {
        /// <summary>
        /// [AoS 轨道专用] 挂载序列变换 (升级为 0-GC 泛型透传版！)
        /// </summary>
        public static ScatterConfigurator<TSource, TItem> ApplyTransform<TSource, TItem>(
            this ScatterConfigurator<TSource, TItem> cfg,
            DataPort<ReadOnlyMemory<double>> targetPort,
            Func<TItem, TSource, double> valueSelector,  // 💥 提取规则：双参委托，自动透传！
            ISequenceTransform transform)
            where TSource : DataSource<TSource, TItem>
        {
            // 定义 AoS 提取委托：按元素遍历榨取
            AoSSpanExtractor<TSource, TItem> extractor = (sourceSpan, sourceRef) =>
            {
                int len = cfg.NativeLengthProvider();
                double[] raw = ArrayPool<double>.Shared.Rent(len);

                // 极速循环
                for (int i = 0; i < len; i++)
                {
                    raw[i] = valueSelector(sourceSpan[i], sourceRef);
                }
                return raw;
            };

            cfg.Plug(new AoSSequenceTransformIngestor<TSource, TItem>(targetPort, cfg.NativeLengthProvider, cfg.Source, extractor, transform));
            return cfg; // 支持链式调用
        }

        /// <summary>
        /// [SoA 轨道专用] 挂载序列变换
        /// </summary>
        public static SoAScatterConfigurator<TBlock> ApplyTransform<TBlock>(
            this SoAScatterConfigurator<TBlock> cfg,
            DataPort<ReadOnlyMemory<double>> targetPort,
            Func<TBlock, List<double>> arraySelector, // 提取规则：直接从块里拔出整列 List
            ISequenceTransform transform,
            Func<int> lengthProvider)
        {
            // 定义 SoA 提取委托：直接通过 Span 级内存拷贝榨取，性能极高
            SoASpanExtractor<TBlock> extractor = sourceSpan =>
            {
                int len = lengthProvider();
                double[] raw = ArrayPool<double>.Shared.Rent(len);

                if (sourceSpan.Length > 0)
                {
                    var list = arraySelector(sourceSpan[0]);
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list).Slice(0, len).CopyTo(raw);
                }
                return raw;
            };

            cfg.Plug(new SoASequenceTransformIngestor<TBlock>(targetPort, lengthProvider, extractor, transform));
            return cfg;
        }
    }
}
