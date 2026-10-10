using System.Threading;

namespace Hevo.Charting.LowCode
{
    /// <summary>
    /// 列读者登记(纪元回收,epoch-based reclamation)。
    /// <para>
    /// 摄入器写进黑板的列(<c>ReadOnlyMemory&lt;T&gt;</c>)只在读锁内取引用,读元素都在锁外:ChartCell 一帧里的
    /// ProjectAll → 图层录制 → 上屏,ComputeFeature 等 Watch / WatchAsync 回调从读输入到写结果。
    /// 这些读者在开始前 <see cref="Begin"/>、结束后 Dispose;摄入器换下一块缓冲时记下退役纪元,
    /// 只有在退役之前进场的读者全部离场后,这块缓冲才会被重新写入(<see cref="ColumnBufferPool{T}"/>)。
    /// </para>
    /// <para>
    /// 全进程一张表,不按黑板分:列的 ROM 可能被转写到别的黑板(LinkedChartContext 的端口镜像),
    /// 读者读的不一定是写入它的那块黑板。固定槽位 + Interlocked,进场 / 离场不分配。
    /// 槽位用满时退化为"有人在读、谁都不回收"(摄入器改为新分配),只损失复用、不损失正确性。
    /// </para>
    /// <para>
    /// <b>读者约定</b>(开发规范《DataBlackboard 与 UsePort 物理读取铁律》第 5 条):
    /// 列 ROM 只在拿到它的那一帧 / 那次回调内有效 —— 必须在进场之后、从黑板(读锁内)取到,用完再离场;
    /// 不要存进字段跨帧 / 跨回调再读(需要保留就拷贝;TickProvider 的 RefBox 这类缓存引用必须每帧先 UsePort 刷新)。
    /// 帧和 Watch / WatchAsync 回调由框架自动登记;其它时机(鼠标事件、Task.Run、定时器、分页回调)读列要自己
    /// <c>using (ColumnReadScope.Begin())</c>。没登记的锁外读者不受保护:缓冲退役、且没有登记读者后就可能被重写。
    /// 2026-10 排查过框架内的帧外读取:交互事件只读标量端口(视口 / hit),trigger / URI 模板走 GetSnapshot 拷贝,
    /// DEBUG 拓扑追踪只缓存 ROM 用于显示长度摘要,不读元素 —— 目前没有需要额外登记的地方。
    /// </para>
    /// </summary>
    public static class ColumnReadScope
    {
        private const int SlotCount = 128;
        private static readonly long[] s_slots = new long[SlotCount];
        private static long s_epoch = 1;
        private static int s_overflow;

        /// <summary>读者进场:登记当前纪元,Dispose 时离场。可以嵌套(各占一个槽位)。</summary>
        public static Scope Begin() => Begin(releasable: false);

        /// <summary>
        /// 读者进场。<paramref name="releasable"/>=true 表示这个读者遵守"读输入 → 长计算 → 提交"的模式
        /// (WatchAsync 回调,如 ComputeFeature / PlotFeature / HandlerFeature):长计算开始后不再碰之前拿到的列,
        /// 允许在长计算期间用 <see cref="ReleaseForLongCall"/> 暂时让出,不挡全进程的缓冲复用。
        /// 帧(ProjectAll → 图层录制)和同步 Watch 不可让出。
        /// </summary>
        public static Scope Begin(bool releasable)
        {
            long e = Volatile.Read(ref s_epoch);
            int start = Environment.CurrentManagedThreadId & (SlotCount - 1);
            for (int k = 0; k < SlotCount; k++)
            {
                int i = (start + k) & (SlotCount - 1);
                if (Volatile.Read(ref s_slots[i]) == 0 && Interlocked.CompareExchange(ref s_slots[i], e, 0) == 0)
                {
                    Push(i + 1, releasable);
                    return new Scope(i, true);
                }
            }
            Interlocked.Increment(ref s_overflow);
            Push(0, releasable: false);
            return Scope.ForOverflow();
        }

        // 本线程当前在场的登记(按进场顺序):槽位 + 1(0 = 溢出)、能否让出
        [ThreadStatic] private static int[]? t_slots;
        [ThreadStatic] private static bool[]? t_releasable;
        [ThreadStatic] private static int t_depth;

        private static void Push(int rawSlot, bool releasable)
        {
            t_slots ??= new int[4];
            t_releasable ??= new bool[4];
            if (t_depth == t_slots.Length)
            {
                Array.Resize(ref t_slots, t_depth * 2);
                Array.Resize(ref t_releasable, t_depth * 2);
            }
            t_slots[t_depth] = rawSlot;
            t_releasable[t_depth] = releasable;
            t_depth++;
        }

        private static void Pop()
        {
            if (t_depth > 0) t_depth--;
        }

        /// <summary>
        /// 本线程在场的登记是否全部可让出(且至少有一个)。调用方据此决定要不要先把输入列拷成私有副本再
        /// <see cref="ReleaseForLongCall"/>(典型:Python handler 调用前,PythonInvokerShim 用它)。
        /// </summary>
        public static bool CanReleaseForLongCall
        {
            get
            {
                if (t_depth == 0) return false;
                for (int i = 0; i < t_depth; i++)
                    if (!t_releasable![i] || t_slots![i] == 0) return false;
                return true;
            }
        }

        /// <summary>
        /// 长计算期间让出本线程的读者登记:槽位挂"不阻挡任何回收"的值(仍归本线程占用),Dispose 时按当前纪元重新登记。
        /// 前提:<see cref="CanReleaseForLongCall"/> 为 true,且调用方在此之后、Dispose 之前不再读之前拿到的列
        /// (需要的输入先拷成私有副本)。不满足前提时返回空凭据,什么都不做。
        /// </summary>
        public static Released ReleaseForLongCall()
        {
            if (!CanReleaseForLongCall) return default;
            for (int i = 0; i < t_depth; i++) Volatile.Write(ref s_slots[t_slots![i] - 1], long.MaxValue);
            return new Released(t_depth);
        }

        public readonly struct Released : IDisposable
        {
            private readonly int _depth;
            internal Released(int depth) => _depth = depth;
            public bool IsReleased => _depth > 0;

            public void Dispose()
            {
                if (_depth == 0) return;
                long e = Volatile.Read(ref s_epoch);
                for (int i = 0; i < _depth && i < t_depth; i++) Volatile.Write(ref s_slots[t_slots![i] - 1], e);
            }
        }

        /// <summary>测试用:读槽位当前值(0 = 空,long.MaxValue = 让出中,其它 = 进场纪元)。</summary>
        internal static long ReadSlot(int slot) => Volatile.Read(ref s_slots[slot]);

        /// <summary>摄入器换下一块缓冲时调用:返回这块缓冲的退役纪元,并推进全局纪元。</summary>
        internal static long Retire() => Interlocked.Increment(ref s_epoch) - 1;

        /// <summary>退役纪元为 <paramref name="retiredAt"/> 的缓冲是否已没有可能还在读它的读者。</summary>
        internal static bool IsReclaimable(long retiredAt)
        {
            if (Volatile.Read(ref s_overflow) != 0) return false;
            for (int i = 0; i < SlotCount; i++)
            {
                long v = Volatile.Read(ref s_slots[i]);
                if (v != 0 && v <= retiredAt) return false;
            }
            return true;
        }

        /// <summary>
        /// 读者登记凭据。编码:0 = default(Scope),Dispose 什么都不做;正数 = 槽位 + 1;
        /// <see cref="OverflowMarker"/> = 槽位用满时的溢出登记。三者互不重叠 —— default(Scope) 的 Dispose
        /// 绝不能被当成溢出离场去减计数(减成负数会让 IsReclaimable 永远返回 false,全进程停止复用)。
        /// </summary>
        public readonly struct Scope : IDisposable
        {
            private const int OverflowMarker = int.MinValue;
            private readonly int _raw;
            private Scope(int raw) => _raw = raw;

            internal Scope(int slot, bool _) : this(slot + 1) { }
            /// <summary>测试用:占用的槽位(溢出 / default 为 -1)。</summary>
            internal int SlotIndex => _raw > 0 ? _raw - 1 : -1;
            internal static Scope ForOverflow() => new(OverflowMarker);

            public void Dispose()
            {
                if (_raw > 0) { Volatile.Write(ref s_slots[_raw - 1], 0); Pop(); }
                else if (_raw == OverflowMarker) { Interlocked.Decrement(ref s_overflow); Pop(); }
            }
        }
    }

    /// <summary>
    /// 摄入器的列缓冲轮换:每次发布都写进一块"没有读者"的缓冲,绝不改写已发布的那块。
    /// <list type="bullet">
    ///   <item><see cref="Rent"/>:优先复用已退役、<see cref="ColumnReadScope.IsReclaimable"/> 且容量合适(<see cref="CapacityPolicy.Fits"/>)
    ///         的缓冲,没有就按 <see cref="CapacityPolicy.Capacity"/> 新分配(带封顶的余量,逐根追加时不必每次都换);绝不等待。</item>
    ///   <item><see cref="Published"/>:发布后调用,上一块退役并记下纪元。最多留 3 块退役缓冲、且总字节不超过
    ///         <see cref="CapacityPolicyOptions.MaxIdleBytes"/>(至少留一块),更旧的直接丢给 GC
    ///         (可能还有读者拿着,不能回收进任何池子)。列变短很多时,太大的旧缓冲不再被选中,会被挤出去(即收缩)。</item>
    /// </list>
    /// 不再 Return 给 ArrayPool:还给共享池的数组会被别人租走改写,而读者可能还拿着它。
    /// 单写者(调用方在黑板写锁 / 数据源锁内),本类不加锁。
    /// </summary>
    internal sealed class ColumnBufferPool<T>
    {
        private const int MaxRetired = 3;
        private readonly T[][] _retired = new T[MaxRetired][];
        private readonly long[] _retiredAt = new long[MaxRetired];
        private int _retiredCount;
        private T[]? _published;
        // 刚被换下、还没打退役纪元的缓冲。纪元推迟到下一次 Rent 才打:Published 发生在写事务内,
        // 事务提交时的通知(如 LinkedChartContext 端口镜像把旧值转写到别的黑板)还没发出,
        // 这期间别的黑板上进场的读者仍可能拿到旧缓冲;在事务内就打戳的话,这些读者的纪元比退役纪元新,
        // 挡不住回收。下一次 Rent 时上一个事务的通知早已发完,此时打戳才安全。
        private T[]? _pendingRetire;

        private readonly CapacityPolicyOptions _growth;
        private static readonly int ElementSize = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();

        public ColumnBufferPool(CapacityPolicyOptions? growth = null) => _growth = growth ?? CapacityPolicyOptions.Default;

        public T[] Rent(int minLength)
        {
            StampPending();
            for (int i = 0; i < _retiredCount; i++)
            {
                if (CapacityPolicy.Fits(_retired[i].Length, minLength, _growth) && ColumnReadScope.IsReclaimable(_retiredAt[i]))
                {
                    var arr = _retired[i];
                    RemoveAt(i);
                    return arr;
                }
            }
            return new T[CapacityPolicy.Capacity(minLength, _growth)];
        }

        /// <summary>最近一次发布的缓冲(写者自己读它是安全的:已发布的缓冲不会再被改写)。</summary>
        public T[]? Current => _published;

        /// <summary>租到但没发布(内容跟当前发布的一样、不必发布)的缓冲还回来:没人见过它,可以立刻再用。</summary>
        public void Unused(T[] array) => AddRetired(array, 0); // 纪元 0:任何读者都比它新,IsReclaimable 恒为真

        public void Published(T[] array)
        {
            if (_published != null && !ReferenceEquals(_published, array))
            {
                StampPending(); // 正常不会有(每次发布前都 Rent 过);保险起见先把更早的那块打戳入环
                _pendingRetire = _published;
            }
            _published = array;
        }

        private void StampPending()
        {
            if (_pendingRetire == null) return;
            var arr = _pendingRetire;
            _pendingRetire = null;
            AddRetired(arr, ColumnReadScope.Retire());
        }

        private void AddRetired(T[] array, long retiredAt)
        {
            if (_retiredCount == MaxRetired) RemoveAt(0);
            _retired[_retiredCount] = array;
            _retiredAt[_retiredCount] = retiredAt;
            _retiredCount++;
            // 空闲总字节封顶(至少留最新的一块):超出时丢最旧的给 GC
            long bytes = 0;
            for (int i = 0; i < _retiredCount; i++) bytes += (long)_retired[i].Length * ElementSize;
            while (_retiredCount > 1 && bytes > _growth.MaxIdleBytes)
            {
                bytes -= (long)_retired[0].Length * ElementSize;
                RemoveAt(0);
            }
        }

        private void RemoveAt(int i)
        {
            for (int k = i; k < _retiredCount - 1; k++)
            {
                _retired[k] = _retired[k + 1];
                _retiredAt[k] = _retiredAt[k + 1];
            }
            _retiredCount--;
            _retired[_retiredCount] = null!;
        }
    }
}
