using System.Runtime.InteropServices;

namespace Hevo.Charting.LowCode
{
    /// <summary>
    /// 一个"把计算结果写进列端口"的调用点(ComputeFeature / PlotFeature 的一个订阅)持有的输出缓冲池。
    /// <para>
    /// handler(典型是 Python 指标)每算完一次,结果要落到一块 double[] 里再写进黑板。以前每次 new 一块,
    /// 2 万根时每块 160 KB 直接进 LOH。现在宿主(PythonNet 的 marshaller)在 <see cref="Current"/> 不为空时
    /// 从这里 <see cref="Rent"/>,调用点把结果写进端口后用 <see cref="CallScope.Written"/> 登记,
    /// 被换下的旧缓冲按列读者纪元退役(同 <see cref="ColumnBufferRing{T}"/>:退役纪元推迟到下一次 Rent 才打),
    /// 在它退役之前进场的读者全部离场后才会再交出去。已发布在任何端口上的缓冲绝不交出去。
    /// </para>
    /// <para>
    /// 读者约定同摄入器的列(开发规范《DataBlackboard 与 UsePort 物理读取铁律》第 5 条):输出列只在拿到它的那一帧 /
    /// 那次回调内有效,要跨帧保留就拷贝。
    /// </para>
    /// <para>
    /// 单写者:调用点一次只有一个回调在跑(WatchAsync single-flight);内部仍加锁,防止同一 Feature 被装配出两条订阅时并发。
    /// 调用超时(PerCallTimeoutWatchdog)时 Python 线程可能还在用本对象,宿主调 <see cref="Abandon"/>,调用点换一个新的。
    /// </para>
    /// </summary>
    public sealed class ColumnCallBuffers
    {
        private const int MaxFree = 6;
        private readonly ArrayGrowthOptions _growth;

        public ColumnCallBuffers(ArrayGrowthOptions? growth = null) => _growth = growth ?? ArrayGrowthOptions.Default;

        [ThreadStatic] private static ColumnCallBuffers? t_current;

        /// <summary>当前线程正在进行的调用所属的调用点(调用点外为 null,宿主照旧每次新分配)。</summary>
        public static ColumnCallBuffers? Current => t_current;

        private readonly object _lock = new();
        private readonly Dictionary<object, double[]> _publishedByPort = new();
        private readonly Dictionary<double[], int> _publishedRefs = new(ReferenceEqualityComparer.Instance);
        private readonly List<double[]> _pendingRetire = new();
        private readonly List<(double[] Array, long RetiredAt)> _free = new();
        private readonly List<double[]> _rented = new();

        /// <summary>宿主按调用点缓存的东西(PythonNet:固定在 POH 上的输入缓冲 + 指向它的 ndarray 视图)。</summary>
        public object? HostCache { get; set; }

        /// <summary>调用超时、宿主线程可能还在用本对象时置位;调用点据此换一个新的。</summary>
        public bool IsAbandoned { get; private set; }

        public void Abandon() => IsAbandoned = true;

        /// <summary>开始一次调用:本线程的 <see cref="Current"/> 指向本对象,Dispose 时恢复,并把租了没发布的缓冲收回。</summary>
        public CallScope BeginCall()
        {
            var prev = t_current;
            t_current = this;
            lock (_lock) _rented.Clear();
            return new CallScope(this, prev);
        }

        /// <summary>租一块至少 <paramref name="minLength"/> 长的缓冲(调用方只用前 minLength 个)。</summary>
        public double[] Rent(int minLength)
        {
            lock (_lock)
            {
                StampPending();
                for (int i = 0; i < _free.Count; i++)
                {
                    var (arr, at) = _free[i];
                    if (ArrayGrowth.Fits(arr.Length, minLength, _growth) && ColumnReaders.IsReclaimable(at))
                    {
                        _free.RemoveAt(i);
                        _rented.Add(arr);
                        return arr;
                    }
                }
                var fresh = new double[ArrayGrowth.Capacity(minLength, _growth)];
                _rented.Add(fresh);
                return fresh;
            }
        }

        private void Written(object port, ReadOnlyMemory<double> value)
        {
            lock (_lock)
            {
                double[]? arr = MemoryMarshal.TryGetArray(value, out var seg) ? seg.Array : null;
                bool pooled = arr != null && _rented.Remove(arr);
                if (!pooled && arr != null && _publishedByPort.TryGetValue(port, out var same) && ReferenceEquals(same, arr))
                    return; // 同一块又写一次(WriteIfChanged 不会真写)
                if (_publishedByPort.Remove(port, out var old)) Release(old);
                if (pooled || (arr != null && _publishedRefs.ContainsKey(arr)))
                {
                    _publishedByPort[port] = arr!;
                    _publishedRefs[arr!] = _publishedRefs.TryGetValue(arr!, out var n) ? n + 1 : 1;
                }
            }
        }

        private void Release(double[] arr)
        {
            if (!_publishedRefs.TryGetValue(arr, out var n)) return;
            if (n > 1) { _publishedRefs[arr] = n - 1; return; }
            _publishedRefs.Remove(arr);
            _pendingRetire.Add(arr); // 退役纪元推迟到下一次 Rent:写事务提交时的通知(端口镜像)可能还在转发旧值
        }

        private void EndCall()
        {
            lock (_lock)
            {
                // 租了没写进端口的(handler 返回了没用上的列、写之前出错):没人见过,可以立刻再用
                foreach (var arr in _rented) AddFree(arr, 0);
                _rented.Clear();
            }
        }

        private void StampPending()
        {
            if (_pendingRetire.Count == 0) return;
            long at = ColumnReaders.Retire();
            foreach (var arr in _pendingRetire) AddFree(arr, at);
            _pendingRetire.Clear();
        }

        private void AddFree(double[] arr, long at)
        {
            if (_free.Count == MaxFree) _free.RemoveAt(0); // 更旧的丢给 GC(可能还有读者,不能进任何共享池)
            _free.Add((arr, at));
            // 空闲总字节封顶(至少留最新的一块)
            long bytes = 0;
            foreach (var (a, _) in _free) bytes += (long)a.Length * sizeof(double);
            while (_free.Count > 1 && bytes > _growth.MaximumFreeBytes)
            {
                bytes -= (long)_free[0].Array.Length * sizeof(double);
                _free.RemoveAt(0);
            }
        }

        /// <summary>测试用:当前发布在各端口上的缓冲。</summary>
        internal IReadOnlyCollection<double[]> PublishedForTest { get { lock (_lock) return _publishedRefs.Keys.ToArray(); } }

        public readonly struct CallScope : IDisposable
        {
            private readonly ColumnCallBuffers? _owner;
            private readonly ColumnCallBuffers? _prev;
            internal CallScope(ColumnCallBuffers owner, ColumnCallBuffers? prev) { _owner = owner; _prev = prev; }

            /// <summary>调用点自己租输出缓冲(C# 指标的 Span 签名:框架租好交给 handler 写)。</summary>
            public double[] Rent(int minLength) => _owner != null ? _owner.Rent(minLength) : new double[minLength];

            /// <summary>结果 <paramref name="value"/> 已写进 <paramref name="port"/>(调用点在写锁内写完后调)。</summary>
            public void Written(object port, ReadOnlyMemory<double> value) => _owner?.Written(port, value);

            public void Dispose()
            {
                if (_owner == null) return;
                t_current = _prev;
                _owner.EndCall();
            }
        }
    }
}
