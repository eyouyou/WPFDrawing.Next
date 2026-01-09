using Hevo.Charting.Core;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.WorkFlow
{
    /// <summary>
    /// N 路时间轴并集合并算子（取代 PipeTimeUnion 2/3/4 + UnionDataSource{,3,4} + TimeAlignedPair/Triplet/Quad）。
    /// 单类承担任意路数：AddSource 任意多个 DataSource，运行期可加可减，端口名义绑定而非位置绑定。
    /// 合并复杂度 O(L × N)，稳态每帧 0 GC（缓冲按需扩容，绝不归还）。
    /// </summary>
    public sealed class TimeAxisCoordinator : IDisposable
    {
        private readonly DataPort<ReadOnlyMemory<DateTime>> _timePort;

        // 利用 VirtualDataSource 做管线粘合剂：任何源变化 → Feed 脉冲 → Ingestor.Process → Merge
        // Ingestor.Process 由 UniversalDataPipe 在 BeginTransaction (WriteLock) 内调用，写入天然原子
        private readonly VirtualDataSource<byte> _pulseSource = new();
        private static readonly List<byte> _pulsePayload = new() { 0 };

        private readonly List<SourceEntryBase> _sources = new();
        private readonly object _lock = new();

        // 跨帧复用的合并输出缓冲（只增不减）
        private DateTime[] _mergedTimes = Array.Empty<DateTime>();
        private int _mergedCount;
        private SourceEntryBase[] _snapshotBuf = Array.Empty<SourceEntryBase>();

        private ViewportPorts? _vp;
        private Func<int>? _logicalLengthOverride;
        private bool _bound;

        public TimeAxisCoordinator(DataPort<ReadOnlyMemory<DateTime>> timePort)
        {
            _timePort = timePort;
        }

        /// <summary>
        /// 注册一路数据源。运行期可重复调用追加新源。
        /// </summary>
        /// <param name="src">数据源实例</param>
        /// <param name="selector">从每个 TItem 提取 double 值的函数（第二参数为源自身，用于透传上下文）</param>
        /// <param name="outPort">本路对齐后值流的输出端口</param>
        public void AddSource<TSource, TItem>(
            DataSource<TSource, TItem> src,
            Func<TItem, TSource, double> selector,
            DataPort<ReadOnlyMemory<double>> outPort)
            where TSource : DataSource<TSource, TItem>
            where TItem : ITimePoint
        {
            var entry = new SourceEntry<TSource, TItem>((TSource)src, selector, outPort);

            lock (_lock) _sources.Add(entry);

            // 首次补发 + 订阅后续推送；任何变化 → Feed 脉冲触发 Merge
            entry.Subscription = src.Stream.StartWith(src.GetSnapshot()).Subscribe(_ => TriggerPulse());
        }

        /// <summary>运行期移除一路数据源，自动解订阅并触发一次 Merge。</summary>
        public void RemoveSource<TSource, TItem>(DataSource<TSource, TItem> src)
            where TSource : DataSource<TSource, TItem>
            where TItem : ITimePoint
        {
            SourceEntryBase? removed = null;
            lock (_lock)
            {
                for (int i = _sources.Count - 1; i >= 0; i--)
                {
                    if (_sources[i] is SourceEntry<TSource, TItem> e && ReferenceEquals(e.Src, src))
                    {
                        removed = e;
                        _sources.RemoveAt(i);
                        break;
                    }
                }
            }
            if (removed != null)
            {
                removed.Subscription?.Dispose();
                TriggerPulse();
            }
        }

        /// <summary>
        /// 覆盖 LogicalLength 来源。不调用时默认用实际合并点数 merged；
        /// 分时图场景必须传入 () => src.LogicalLength（即 Ruler.TotalLength = 242），
        /// 否则 VP.LogicalLength 会随数据到达从 0 增长，触发视口跳变和 Y 轴爆炸。
        /// </summary>
        public TimeAxisCoordinator UseLogicalLength(Func<int> getter)
        {
            _logicalLengthOverride = getter;
            return this;
        }

        /// <summary>
        /// 把合并后的 logical length 投影到 ViewportPorts.LogicalLength，供视口大法官与各种 Feature 消费。
        /// 链式调用语法糖：coord.UseLogicalLength(...).ProjectExtent(VP).BindTo(chart)。
        /// </summary>
        public TimeAxisCoordinator ProjectExtent(ViewportPorts vp)
        {
            _vp = vp;
            return this;
        }

        /// <summary>
        /// 装配完成、接入 ChartCell 管线。此方法只允许调用一次。
        /// </summary>
        public IRenderFlow<DataBlackboard> BindTo(ChartCell chart)
        {
            if (_bound) throw new InvalidOperationException("TimeAxisCoordinator 已 Bind");
            _bound = true;

            var builder = _pulseSource.Pipe();
            builder._pipe.AddIngestor(new MergeIngestor(this));
            return builder.OnDispose(Dispose).BindTo(chart);
        }

        /// <summary>
        /// Phase 12 / §I：MergeInto 版本，把 Coordinator 作为 DataFlowBinding 登记到 Schema 的 PipelineDispatcher。
        /// 与 BindTo 互斥，只允许调用其中一次。
        /// </summary>
        public void MergeInto(IDataFlowHost host, DataFlowRole role = DataFlowRole.Primary)
        {
            if (_bound) throw new InvalidOperationException("TimeAxisCoordinator 已 Bind");
            _bound = true;

            var builder = _pulseSource.Pipe();
            builder._pipe.AddIngestor(new MergeIngestor(this));
            builder.OnDispose(Dispose).MergeInto(host, role);
        }

        private void TriggerPulse()
        {
            if (!_bound) return;
            _pulseSource.Feed(_pulsePayload);
        }

        /// <summary>Merge 主逻辑：N 路时间轴并集扫描 + 缺失位填 NaN。</summary>
        private void Merge(DataBlackboard board)
        {
            int n;
            lock (_lock)
            {
                n = _sources.Count;
                if (_snapshotBuf.Length < n) _snapshotBuf = new SourceEntryBase[n];
                for (int i = 0; i < n; i++) _snapshotBuf[i] = _sources[i];
            }

            if (n == 0)
            {
                board.ForceWrite(_timePort, ReadOnlyMemory<DateTime>.Empty);
                if (_vp != null) board.WriteIfChanged(_vp.LogicalLength, 0);
                _mergedCount = 0;
                return;
            }

            // 1. 各源捕获当前快照 + 重置游标；同时估算合并上界用于扩容
            int maxTotal = 0;
            for (int i = 0; i < n; i++)
            {
                var e = _snapshotBuf[i];
                maxTotal += e.TakeSnapshot();
                e.Cursor = 0;
            }

            // 2. 按需扩容合并缓冲（只增不减，稳态 0 分配）
            if (_mergedTimes.Length < maxTotal) _mergedTimes = new DateTime[maxTotal];
            for (int i = 0; i < n; i++)
            {
                var e = _snapshotBuf[i];
                if (e.OutputBuffer.Length < maxTotal) e.OutputBuffer = new double[maxTotal];
            }

            // 3. N-arity 多路归并：每步扫 N 个游标找最小 Time，对齐 NaN 填空
            int merged = 0;
            while (true)
            {
                DateTime tMin = DateTime.MaxValue;
                bool anyLeft = false;
                for (int i = 0; i < n; i++)
                {
                    var e = _snapshotBuf[i];
                    if (e.Cursor < e.Count)
                    {
                        anyLeft = true;
                        var t = e.TimeAt(e.Cursor);
                        if (t < tMin) tMin = t;
                    }
                }
                if (!anyLeft) break;

                _mergedTimes[merged] = tMin;
                for (int i = 0; i < n; i++)
                {
                    var e = _snapshotBuf[i];
                    if (e.Cursor < e.Count && e.TimeAt(e.Cursor) == tMin)
                    {
                        e.OutputBuffer[merged] = e.ValueAt(e.Cursor);
                        e.Cursor++;
                    }
                    else
                    {
                        e.OutputBuffer[merged] = double.NaN;
                    }
                }
                merged++;
            }

            _mergedCount = merged;

            // 4. 写黑板（board 已在 UniversalDataPipe.Process 的 BeginTransaction 内）
            board.ForceWrite(_timePort, new ReadOnlyMemory<DateTime>(_mergedTimes, 0, merged));
            for (int i = 0; i < n; i++)
                board.ForceWrite(_snapshotBuf[i].OutPort, new ReadOnlyMemory<double>(_snapshotBuf[i].OutputBuffer, 0, merged));

            if (_vp != null)
            {
                int logLen = _logicalLengthOverride?.Invoke() ?? merged;
                board.WriteIfChanged(_vp.LogicalLength, logLen);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                for (int i = 0; i < _sources.Count; i++) _sources[i].Subscription?.Dispose();
                _sources.Clear();
            }
            _pulseSource.Dispose();
        }

        // ==========================================
        // 源条目：类型擦除抽象 + 泛型具体实现
        // ==========================================

        private abstract class SourceEntryBase
        {
            public DataPort<ReadOnlyMemory<double>> OutPort = null!;
            public double[] OutputBuffer = Array.Empty<double>();
            public int Cursor;
            public int Count;  // TakeSnapshot 后填入
            public IDisposable? Subscription;

            /// <summary>捕获当前源快照到本地字段；返回快照长度。</summary>
            public abstract int TakeSnapshot();
            public abstract DateTime TimeAt(int i);
            public abstract double ValueAt(int i);
        }

        private sealed class SourceEntry<TSource, TItem> : SourceEntryBase
            where TSource : DataSource<TSource, TItem>
            where TItem : ITimePoint
        {
            public readonly TSource Src;
            public readonly Func<TItem, TSource, double> Selector;
            private TItem[] _snap = Array.Empty<TItem>();

            public SourceEntry(TSource src, Func<TItem, TSource, double> selector, DataPort<ReadOnlyMemory<double>> outPort)
            {
                Src = src;
                Selector = selector;
                OutPort = outPort;
            }

            public override int TakeSnapshot()
            {
                var s = Src.GetSnapshot();
                var span = s.AsSpan();
                Count = span.Length;
                if (_snap.Length < Count) _snap = new TItem[Count];
                span.CopyTo(_snap);
                return Count;
            }

            public override DateTime TimeAt(int i) => _snap[i].Time;
            public override double ValueAt(int i) => Selector(_snap[i], Src);
        }

        // 自定义 Ingestor：Pipe 每次脉冲 → 调用 Coordinator.Merge（board 在 WriteLock 内）
        private sealed class MergeIngestor : IDataIngestor<byte>
        {
            private readonly TimeAxisCoordinator _coord;
            public MergeIngestor(TimeAxisCoordinator coord) { _coord = coord; }
            public void Process(DataSnapshot<byte> _, DataBlackboard board) => _coord.Merge(board);
        }
    }
}
