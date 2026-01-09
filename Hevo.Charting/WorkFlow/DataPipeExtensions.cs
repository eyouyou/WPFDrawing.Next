using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.DevTools;
using Hevo.Charting.LowCode;
using System.Buffers;
using System.Runtime.InteropServices;

namespace Hevo.Charting.WorkFlow
{
    public static class DataPipeExtensions
    {
        // ==========================================
        // 💥 核心分支 2：视口驱动的流切片 (适用于 K线等百万级历史数据)
        // 语义：开启雷达，紧盯 Viewport，只切出屏幕可见的数组，并吐出物理偏移量！
        // ==========================================
        public static IWorkflow<DataBlackboard> LinkViewportStream<TSource, TItem>(
            this DataPipeBuilder<TSource, TItem> builder, // 👈 挂载在泛型构建器上，C# 完美推断！
            DataPort<RealRange> viewportPort,           // 👈 监听这个引脚 (ActiveXRangePort)
            DataPort<int> offsetPort,                     // 👈 输出起始偏移量到这个引脚 (ViewportOffsetPort)
            Action<ViewportScatterConfigurator<TItem>> scatterAction,
            int overdrawBuffer = 2)                       // 默认左右各多画 2 根，防止平移闪烁
            where TSource : DataSource<TSource, TItem>
        {
            var configurator = new ViewportScatterConfigurator<TItem>();
            scatterAction(configurator);

            builder.InternalPipe.AddIngestor(new ViewportBatchIngestor<TItem>(
                viewportPort,
                offsetPort,
                configurator.Mappers, // 传递所有的切片规则
                overdrawBuffer
            ));

            var baseStream = builder.Seal();
            return baseStream.Watch(new object[] { viewportPort }, builder.Reevaluate);
        }

        // ==========================================
        // 🚀 轨道 2：高铁 SoA 轨道
        // 这里的 TBlock 替换了 TItem 的位置！
        // ==========================================
        public static IWorkflow<DataBlackboard> LinkSoAViewportStream<TSource, TBlock>(
                    this DataPipeBuilder<TSource, TBlock> builder,
                    DataPort<RealRange> viewportPort,
                    DataPort<int> offsetPort,
                    Action<SoAScatterConfigurator<TBlock>> scatterAction,
                    int overdrawBuffer = 2)
                    where TSource : DataSource<TSource, TBlock>
        {
            var cfg = new SoAScatterConfigurator<TBlock>(builder.InternalPipe);
            scatterAction(cfg);

            builder.InternalPipe.AddIngestor(new SoAViewportIngestor<TBlock>(
                            viewportPort, offsetPort, cfg.Slicers, overdrawBuffer));
            return builder.Seal().Watch(new object[] { viewportPort }, builder.Reevaluate);
        }
    }

    // ==========================================
    // 💥 映射规则收纳盒 (不再直接生成 Ingestor)
    // ==========================================
    public class ViewportScatterConfigurator<TItem>
    {
        internal List<IColumnMapper<TItem>> Mappers { get; } = new();

        public ViewportScatterConfigurator<TItem> Map<TValue>(
            DataPort<ReadOnlyMemory<TValue>> targetPort,
            Func<TItem, TValue> valueSelector,
            TValue defaultValue = default!)
        {
            // 仅仅将映射规则暂存起来
            Mappers.Add(new ColumnMapper<TItem, TValue>(targetPort, valueSelector, defaultValue));
            return this;
        }
    }

    // ==========================================
    // 💥 内部接口与实现：定义每列数据该怎么切片
    // ==========================================
    internal interface IColumnMapper<TItem> : IDisposable
    {
        // 接口升级，接收 DataSnapshot 
        void MapAndWrite(DataSnapshot<TItem> snapshot, int startIdx, int visibleCount, DataBlackboard board);
    }

    // ⚠️ H3（ArrayPool 读写竞争）已知风险，暂缓至后续 Phase：
    //   本类单 buffer 原地覆写；UI 线程持有已发布的 ReadOnlyMemory 跨越 ReadLock 到
    //   Skia 异步 PaintSurface，期间后台 BeginTransaction 可能覆写同一 buffer。
    //   表现为偶发、难复现的 K 线数值错位。
    //   候选修复：双缓冲（内存 ×2，已否决）/ Feature 端 ToArray 浅拷贝 / 延长 ReadLock 至 Invalidate。
    //   参见 `架构诊断报告.md §4.1`、`plan.md §B.3 H3`。
    internal class ColumnMapper<TItem, TValue> : IColumnMapper<TItem>
    {
        private readonly DataPort<ReadOnlyMemory<TValue>> _targetPort;
        private readonly Func<TItem, TValue> _valueSelector;
        private readonly TValue _defaultValue;

        private TValue[]? _buffer;

        // 💥 三维防抖大坝！
        private VersionToken _lastSourceVersion;
        private int _lastStartIdx = -1;
        private int _lastVisibleCount = -1;

        public ColumnMapper(DataPort<ReadOnlyMemory<TValue>> targetPort, Func<TItem, TValue> valueSelector, TValue defaultValue)
        {
            _targetPort = targetPort; _valueSelector = valueSelector; _defaultValue = defaultValue;
        }

        public void MapAndWrite(DataSnapshot<TItem> snapshot, int startIdx, int visibleCount, DataBlackboard board)
        {
            // 1. 防抖大坝前置：数据源纪元和切片均未变化，直接跳过！
            bool sourceChanged = snapshot.Version != _lastSourceVersion;
            bool sliceChanged = startIdx != _lastStartIdx || visibleCount != _lastVisibleCount;
            if (!sourceChanged && !sliceChanged) return;

            // 2. 记录本次切片状态
            _lastSourceVersion = snapshot.Version;
            _lastStartIdx = startIdx;
            _lastVisibleCount = visibleCount;

            ReadOnlySpan<TItem> sourceSpan = snapshot.AsSpan();

            // 3. 按需扩容单缓冲
            if (_buffer == null || _buffer.Length < visibleCount)
            {
                if (_buffer != null) ArrayPool<TValue>.Shared.Return(_buffer, clearArray: false);
                _buffer = ArrayPool<TValue>.Shared.Rent(visibleCount);
            }

            // 4. 将数据写入缓冲
            Span<TValue> span = _buffer.AsSpan(0, visibleCount);
            span.Fill(_defaultValue);
            for (int i = 0; i < visibleCount; i++)
            {
                // 💥 从 Span 中极速提取
                span[i] = _valueSelector(sourceSpan[startIdx + i]);
            }

            board.ForceWrite(_targetPort, new ReadOnlyMemory<TValue>(_buffer, 0, visibleCount));
        }

        public void Dispose()
        {
            if (_buffer != null) { ArrayPool<TValue>.Shared.Return(_buffer, clearArray: false); _buffer = null; }
        }
    }

    // ==========================================
    // 💥 终极单体性能怪兽：ViewportBatchIngestor
    // 无论配了几个 Map，全军只由这一个大脑指挥！
    // ==========================================
    internal class ViewportBatchIngestor<TItem> : IDataIngestor<TItem>, IDisposable
    {
        private readonly DataPort<RealRange> _viewportPort;
        private readonly DataPort<int> _offsetOutputPort;
        private readonly List<IColumnMapper<TItem>> _mappers;
        private readonly int _overdrawBuffer;

        public ViewportBatchIngestor(DataPort<RealRange> viewportPort, DataPort<int> offsetOutputPort, List<IColumnMapper<TItem>> mappers, int overdrawBuffer)
        {
            _viewportPort = viewportPort; _offsetOutputPort = offsetOutputPort; _mappers = mappers; _overdrawBuffer = overdrawBuffer;
        }

        public void Process(DataSnapshot<TItem> snapshot, DataBlackboard board)
        {
            if (snapshot.Count == 0) return;

            var range = board.Read(_viewportPort);
            if (!range.IsValid) return;

            // 1. 全局只算 1 次边界！
            int startIdx = Math.Max(0, (int)Math.Floor(range.Min) - _overdrawBuffer);
            int endIdx = Math.Min(snapshot.Count - 1, (int)Math.Ceiling(range.Max) + _overdrawBuffer);
            int visibleCount = endIdx - startIdx + 1;

            if (visibleCount <= 0) return;

#if DEBUG
            using var scope = TopologyTracer.EnterScope(null);
#endif
            // 2. 批量切肉：依次处理，传入 snapshot，由 ColumnMapper 内部负责三维防抖
            for (int i = 0; i < _mappers.Count; i++)
            {
                _mappers[i].MapAndWrite(snapshot, startIdx, visibleCount, board);
            }

            // 3. 终极防卡：Offset 全局只写 1 次！
            board.ForceWrite(_offsetOutputPort, startIdx);
        }

        public void Dispose()
        {
            for (int i = 0; i < _mappers.Count; i++) _mappers[i].Dispose();
        }
    }

    /// <summary>
    /// SoA 列式存储配置器 (SoAScatterConfigurator)
    /// </summary>
    /// <typeparam name="TBlock"></typeparam>
    public class SoAScatterConfigurator<TBlock>
    {
        private readonly UniversalDataPipe<TBlock> _pipe;
        internal List<ISoAColumnSlicer<TBlock>> Slicers { get; } = new();

        internal SoAScatterConfigurator(UniversalDataPipe<TBlock> pipe)
        {
            _pipe = pipe;
        }

        internal UniversalDataPipe<TBlock> InternalPipe => _pipe;

        // 常规列切片
        public SoAScatterConfigurator<TBlock> Slice<TValue>(DataPort<ReadOnlyMemory<TValue>> targetPort, Func<TBlock, List<TValue>> arraySelector)
            where TValue : IEquatable<TValue>
        {
            Slicers.Add(new SoAColumnSlicer<TBlock, TValue>(targetPort, arraySelector));
            return this;
        }

        // 💥 官方插件接入口！允许在 SoA 管线中挂载前置数学计算
        public SoAScatterConfigurator<TBlock> Plug(IDataIngestor<TBlock> ingestor)
        {
            _pipe.AddIngestor(ingestor);
            return this;
        }
    }

    // ==========================================
    // 🚀 高铁 SoA 轨道系列
    // ==========================================
    internal interface ISoAColumnSlicer<TBlock>
    {
        void SliceAndWrite(TBlock sourceBlock, int startIdx, int visibleCount, VersionToken currentVersion, DataBlackboard board);
    }

    // ⚠️ H3 已知风险同 ColumnMapper：单 buffer 原地覆写，UI 跨帧持有 ReadOnlyMemory 存在 race。参见其处注释。
    internal class SoAColumnSlicer<TBlock, TValue> : ISoAColumnSlicer<TBlock>, IDisposable where TValue : IEquatable<TValue>
    {
        private readonly DataPort<ReadOnlyMemory<TValue>> _targetPort;
        private readonly Func<TBlock, List<TValue>> _arraySelector;

        private TValue[]? _buffer;
        private VersionToken _lastSourceVersion;
        private int _lastStartIdx = -1;
        private int _lastSafeCount = -1;

        public SoAColumnSlicer(DataPort<ReadOnlyMemory<TValue>> port, Func<TBlock, List<TValue>> selector)
        {
            _targetPort = port; _arraySelector = selector;
        }

        public void SliceAndWrite(TBlock sourceBlock, int startIdx, int visibleCount, VersionToken currentVersion, DataBlackboard board)
        {
            List<TValue> sourceList = _arraySelector(sourceBlock);
            if (sourceList == null || startIdx < 0 || startIdx >= sourceList.Count) return;

            int safeCount = Math.Min(visibleCount, sourceList.Count - startIdx);
            if (safeCount <= 0) return;

            // 💥 三维防抖大坝！
            if (currentVersion == _lastSourceVersion && _lastStartIdx == startIdx && _lastSafeCount == safeCount) return;

            _lastSourceVersion = currentVersion;
            _lastStartIdx = startIdx;
            _lastSafeCount = safeCount;

            Span<TValue> sourceSpan = CollectionsMarshal.AsSpan(sourceList).Slice(startIdx, safeCount);
            if (_buffer == null || _buffer.Length < safeCount)
            {
                if (_buffer != null) ArrayPool<TValue>.Shared.Return(_buffer);
                _buffer = ArrayPool<TValue>.Shared.Rent(safeCount);
            }

            sourceSpan.CopyTo(_buffer);
            board.ForceWrite(_targetPort, new ReadOnlyMemory<TValue>(_buffer, 0, safeCount));
        }

        public void Dispose()
        {
            if (_buffer != null) { ArrayPool<TValue>.Shared.Return(_buffer); _buffer = null; }
        }
    }

    internal class SoAViewportIngestor<TBlock> : IDataIngestor<TBlock>
    {
        private readonly DataPort<RealRange> _viewportPort;
        private readonly DataPort<int> _offsetPort;
        private readonly List<ISoAColumnSlicer<TBlock>> _slicers;
        private readonly int _overdrawBuffer;

        public SoAViewportIngestor(DataPort<RealRange> vp, DataPort<int> offset, List<ISoAColumnSlicer<TBlock>> slicers, int overdraw)
        {
            _viewportPort = vp; _offsetPort = offset; _slicers = slicers; _overdrawBuffer = overdraw;
        }

        public void Process(DataSnapshot<TBlock> snapshot, DataBlackboard board)
        {
            if (snapshot.Count == 0) return;

            // SoA 中 TBlock 往往是全量对象，取第一个即可
            TBlock block = snapshot.AsSpan()[0];
            VersionToken currentVersion = snapshot.Version;

            var range = board.Read(_viewportPort);
            if (!range.IsValid) return;

            int startIdx = Math.Max(0, (int)Math.Floor(range.Min) - _overdrawBuffer);
            int visibleCount = (int)Math.Ceiling(range.Max) + _overdrawBuffer - startIdx + 1;
            if (visibleCount <= 0) return;

#if DEBUG
            using var scope = TopologyTracer.EnterScope(null);
            TracerRegistry.Get(board)?.RecordRead(_viewportPort);
#endif
            for (int i = 0; i < _slicers.Count; i++) _slicers[i].SliceAndWrite(block, startIdx, visibleCount, currentVersion, board);
            board.ForceWrite(_offsetPort, startIdx);
        }
    }
}
