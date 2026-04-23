using Hevo.Charting.Core;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.WorkFlow
{
    /// <summary>
    /// N 路时间轴并集合并算子 —— 把任意数量的 <see cref="DataSource{TSource, TItem}"/>（TItem : ITimePoint）
    /// 按时间戳对齐成并集行，每路可同时 Emit 多个 double 序列到各自的 DataPort。
    /// 合并复杂度 O(L × N)，稳态每帧 0 GC（缓冲按需扩容，绝不归还）。
    ///
    /// 定位：Source Composer —— 配置完多源合并语义后，通过 <see cref="Pipe"/> 进入
    /// <see cref="DataPipeBuilder{TSource, TItem}"/> 世界，享用 <c>Compute</c> / <c>OnDispose</c> / <c>BindTo</c> 等全套能力。
    /// 单源老代码可继续用 <see cref="BindTo(ChartCell)"/> 语法糖，一行落地。
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
        private DataPipeBuilder<VirtualDataSource<byte>, byte>? _pipe;
        private bool _bound;

        public TimeAxisCoordinator(DataPort<ReadOnlyMemory<DateTime>> timePort)
        {
            _timePort = timePort;
        }

        /// <summary>
        /// 注册一路数据源并配置其 emitters（多端口支持）。
        /// </summary>
        public TimeAxisCoordinator AddSource<TSource, TItem>(
            DataSource<TSource, TItem> src,
            Action<SourceMapConfigurator<TSource, TItem>> configure)
            where TSource : DataSource<TSource, TItem>
            where TItem : ITimePoint
        {
            var cfg = new SourceMapConfigurator<TSource, TItem>();
            configure(cfg);
            if (cfg.Emitters.Count == 0)
                throw new InvalidOperationException($"AddSource({typeof(TSource).Name}) 至少要配置一个 Map(...)");

            var entry = new SourceEntry<TSource, TItem>((TSource)src, cfg.Emitters);

            lock (_lock) _sources.Add(entry);

            // 首次补发 + 订阅后续推送；任何变化 → Feed 脉冲触发 Merge
            entry.Subscription = src.Stream.StartWith(src.GetSnapshot()).Subscribe(_ => TriggerPulse());
            return this;
        }

        /// <summary>
        /// 语法糖重载：单端口场景一行登记（最常见的 90% 情况）。
        /// </summary>
        public TimeAxisCoordinator AddSource<TSource, TItem>(
            DataSource<TSource, TItem> src,
            Func<TItem, TSource, double> selector,
            DataPort<ReadOnlyMemory<double>> outPort)
            where TSource : DataSource<TSource, TItem>
            where TItem : ITimePoint
            => AddSource(src, cfg => cfg.Map(outPort, selector));

        /// <summary>更简单的重载：selector 不需要 source 上下文时用这个。</summary>
        public TimeAxisCoordinator AddSource<TSource, TItem>(
            DataSource<TSource, TItem> src,
            Func<TItem, double> selector,
            DataPort<ReadOnlyMemory<double>> outPort)
            where TSource : DataSource<TSource, TItem>
            where TItem : ITimePoint
            => AddSource(src, cfg => cfg.Map(outPort, selector));

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
        /// 进入 DataPipeBuilder 世界 —— 拿到内部粘合 pipe，享用 Compute / OnDispose / BindTo 等全套 DSL。
        /// 业务需要在合并后做派生计算时走这里：<c>coord.AddSource(...).Pipe().Compute(...).BindTo(chart)</c>。
        /// </summary>
        public DataPipeBuilder<VirtualDataSource<byte>, byte> Pipe()
        {
            if (_pipe != null) return _pipe;
            _pipe = _pulseSource.Pipe();
            _pipe._pipe.AddIngestor(new MergeIngestor(this));
            _pipe.OnDispose(Dispose);
            return _pipe;
        }

        /// <summary>
        /// 装配完成、接入 ChartCell 管线。此方法只允许调用一次。
        /// 等价于 <c>Pipe().BindTo(chart)</c>，为 90% 业务场景保留一行语法糖。
        /// </summary>
        public IRenderFlow<DataBlackboard> BindTo(ChartCell chart)
        {
            if (_bound) throw new InvalidOperationException("TimeAxisCoordinator 已 Bind");
            _bound = true;
            return Pipe().BindTo(chart);
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
            for (int i = 0; i < n; i++) _snapshotBuf[i].EnsureBuffers(maxTotal);

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
                        e.EmitAt(merged, e.Cursor, missing: false);
                        e.Cursor++;
                    }
                    else
                    {
                        e.EmitAt(merged, snapIndex: 0, missing: true);
                    }
                }
                merged++;
            }

            _mergedCount = merged;

            // 4. 写黑板（board 已在 UniversalDataPipe.Process 的 BeginTransaction 内）
            board.ForceWrite(_timePort, new ReadOnlyMemory<DateTime>(_mergedTimes, 0, merged));
            for (int i = 0; i < n; i++) _snapshotBuf[i].WriteOutputs(board, merged);

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

        internal abstract class SourceEntryBase
        {
            public int Cursor;
            public int Count;  // TakeSnapshot 后填入
            public IDisposable? Subscription;

            /// <summary>捕获当前源快照到本地字段；返回快照长度。</summary>
            public abstract int TakeSnapshot();
            public abstract DateTime TimeAt(int i);
            /// <summary>确保所有 Emitter 缓冲能容纳 capacity 个数据点。</summary>
            public abstract void EnsureBuffers(int capacity);
            /// <summary>将第 snapIndex 条的数据（或 NaN）写入每个 Emitter 缓冲的 merged 位。</summary>
            public abstract void EmitAt(int merged, int snapIndex, bool missing);
            /// <summary>把每个 Emitter 缓冲的 [0..length) 一次性写到它的 Port。</summary>
            public abstract void WriteOutputs(DataBlackboard board, int length);
        }

        private sealed class SourceEntry<TSource, TItem> : SourceEntryBase
            where TSource : DataSource<TSource, TItem>
            where TItem : ITimePoint
        {
            public readonly TSource Src;
            private readonly EmitterSlot<TItem, TSource>[] _emitters;
            private TItem[] _snap = Array.Empty<TItem>();

            public SourceEntry(TSource src, List<(Func<TItem, TSource, double> Selector, DataPort<ReadOnlyMemory<double>> Port)> emitters)
            {
                Src = src;
                _emitters = new EmitterSlot<TItem, TSource>[emitters.Count];
                for (int i = 0; i < emitters.Count; i++)
                    _emitters[i] = new EmitterSlot<TItem, TSource>(emitters[i].Selector, emitters[i].Port);
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

            public override void EnsureBuffers(int capacity)
            {
                for (int k = 0; k < _emitters.Length; k++)
                {
                    if (_emitters[k].Buffer.Length < capacity)
                        _emitters[k].Buffer = new double[capacity];
                }
            }

            public override void EmitAt(int merged, int snapIndex, bool missing)
            {
                if (missing)
                {
                    for (int k = 0; k < _emitters.Length; k++) _emitters[k].Buffer[merged] = double.NaN;
                }
                else
                {
                    var item = _snap[snapIndex];
                    for (int k = 0; k < _emitters.Length; k++)
                        _emitters[k].Buffer[merged] = _emitters[k].Selector(item, Src);
                }
            }

            public override void WriteOutputs(DataBlackboard board, int length)
            {
                for (int k = 0; k < _emitters.Length; k++)
                    board.ForceWrite(_emitters[k].Port, new ReadOnlyMemory<double>(_emitters[k].Buffer, 0, length));
            }
        }

        private sealed class EmitterSlot<TItem, TSource>
        {
            public readonly Func<TItem, TSource, double> Selector;
            public readonly DataPort<ReadOnlyMemory<double>> Port;
            public double[] Buffer = Array.Empty<double>();

            public EmitterSlot(Func<TItem, TSource, double> selector, DataPort<ReadOnlyMemory<double>> port)
            {
                Selector = selector;
                Port = port;
            }
        }

        // 自定义 Ingestor：Pipe 每次脉冲 → 调用 Coordinator.Merge（board 在 WriteLock 内）
        private sealed class MergeIngestor : IDataIngestor<byte>
        {
            private readonly TimeAxisCoordinator _coord;
            public MergeIngestor(TimeAxisCoordinator coord) { _coord = coord; }
            public void Process(DataSnapshot<byte> _, DataBlackboard board) => _coord.Merge(board);
        }
    }

    /// <summary>
    /// 单源多端口配置器。和 <see cref="ScatterConfigurator{TSource, TItem}"/> 语义对仗：<c>Map(port, selector)</c>。
    /// </summary>
    public sealed class SourceMapConfigurator<TSource, TItem>
        where TSource : DataSource<TSource, TItem>
        where TItem : ITimePoint
    {
        internal readonly List<(Func<TItem, TSource, double> Selector, DataPort<ReadOnlyMemory<double>> Port)> Emitters = new();

        /// <summary>多端口映射：同一源可重复调用，每次为一个 DataPort 配置一个 selector。</summary>
        public SourceMapConfigurator<TSource, TItem> Map(
            DataPort<ReadOnlyMemory<double>> port,
            Func<TItem, double> selector)
        {
            Emitters.Add(((it, _) => selector(it), port));
            return this;
        }

        /// <summary>带 source 上下文的重载：需要用到源自身属性（如 PreClose）时用这个。</summary>
        public SourceMapConfigurator<TSource, TItem> Map(
            DataPort<ReadOnlyMemory<double>> port,
            Func<TItem, TSource, double> selector)
        {
            Emitters.Add((selector, port));
            return this;
        }
    }
}
