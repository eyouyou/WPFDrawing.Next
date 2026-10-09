using System.Threading;

namespace Hevo.Charting.LowCode
{
    /// <summary>
    /// 列读者登记(纪元回收,epoch-based reclamation)。
    /// <para>
    /// 摄入器写进黑板的列(<c>ReadOnlyMemory&lt;T&gt;</c>)只在读锁内取引用,读元素都在锁外:ChartCell 一帧里的
    /// ProjectAll → 图层录制 → 上屏,ComputeFeature 等 Watch / WatchAsync 回调从读输入到写结果。
    /// 这些读者在开始前 <see cref="Enter"/>、结束后 Dispose;摄入器换下一块缓冲时记下退役纪元,
    /// 只有在退役之前进场的读者全部离场后,这块缓冲才会被重新写入(<see cref="ColumnBufferRing{T}"/>)。
    /// </para>
    /// <para>
    /// 全进程一张表,不按黑板分:列的 ROM 可能被转写到别的黑板(LinkedChartContext 的端口镜像),
    /// 读者读的不一定是写入它的那块黑板。固定槽位 + Interlocked,进场 / 离场不分配。
    /// 槽位用满时退化为"有人在读、谁都不回收"(摄入器改为新分配),只损失复用、不损失正确性。
    /// </para>
    /// <para>
    /// 没登记的锁外读者(例如鼠标事件里直接读黑板后在锁外用列)不受保护 —— 跟修复前一样有读到改写数据的风险,
    /// 但缓冲至少要退役、且没有登记读者后才会被重写,窗口比原来"每次推送都原地覆盖"小得多。
    /// </para>
    /// </summary>
    public static class ColumnReaders
    {
        private const int SlotCount = 128;
        private static readonly long[] s_slots = new long[SlotCount];
        private static long s_epoch = 1;
        private static int s_overflow;

        /// <summary>读者进场:登记当前纪元,Dispose 时离场。可以嵌套(各占一个槽位)。</summary>
        public static Scope Enter()
        {
            long e = Volatile.Read(ref s_epoch);
            int start = Environment.CurrentManagedThreadId & (SlotCount - 1);
            for (int k = 0; k < SlotCount; k++)
            {
                int i = (start + k) & (SlotCount - 1);
                if (Volatile.Read(ref s_slots[i]) == 0 && Interlocked.CompareExchange(ref s_slots[i], e, 0) == 0)
                    return new Scope(i);
            }
            Interlocked.Increment(ref s_overflow);
            return new Scope(Scope.Overflow);
        }

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

        public readonly struct Scope : IDisposable
        {
            internal const int Overflow = -1;
            private readonly int _slot;
            internal Scope(int slot) => _slot = slot + 1; // 0 = default(Scope),Dispose 什么都不做

            public void Dispose()
            {
                int slot = _slot - 1;
                if (slot >= 0) Volatile.Write(ref s_slots[slot], 0);
                else if (slot == Overflow) Interlocked.Decrement(ref s_overflow);
            }
        }
    }

    /// <summary>
    /// 摄入器的列缓冲轮换:每次发布都写进一块"没有读者"的缓冲,绝不改写已发布的那块。
    /// <list type="bullet">
    ///   <item><see cref="Rent"/>:优先复用已退役、且 <see cref="ColumnReaders.IsReclaimable"/> 的缓冲,没有就新分配(带 25% 余量,
    ///         逐根追加时不必每次都换);绝不等待。</item>
    ///   <item><see cref="Published"/>:发布后调用,上一块退役并记下纪元。最多留 3 块退役缓冲,更旧的直接丢给 GC
    ///         (可能还有读者拿着,不能回收进任何池子)。</item>
    /// </list>
    /// 不再 Return 给 ArrayPool:还给共享池的数组会被别人租走改写,而读者可能还拿着它。
    /// 单写者(调用方在黑板写锁 / 数据源锁内),本类不加锁。
    /// </summary>
    internal sealed class ColumnBufferRing<T>
    {
        private const int MaxRetired = 3;
        private readonly T[][] _retired = new T[MaxRetired][];
        private readonly long[] _retiredAt = new long[MaxRetired];
        private int _retiredCount;
        private T[]? _published;

        public T[] Rent(int minLength)
        {
            for (int i = 0; i < _retiredCount; i++)
            {
                if (_retired[i].Length >= minLength && ColumnReaders.IsReclaimable(_retiredAt[i]))
                {
                    var arr = _retired[i];
                    RemoveAt(i);
                    return arr;
                }
            }
            return new T[minLength + (minLength >> 2) + 16];
        }

        /// <summary>最近一次发布的缓冲(写者自己读它是安全的:已发布的缓冲不会再被改写)。</summary>
        public T[]? Current => _published;

        /// <summary>租到但没发布(内容跟当前发布的一样、不必发布)的缓冲还回来:没人见过它,可以立刻再用。</summary>
        public void Unused(T[] array)
        {
            if (_retiredCount == MaxRetired) RemoveAt(0);
            _retired[_retiredCount] = array;
            _retiredAt[_retiredCount] = 0; // 纪元 0:任何读者都比它新,IsReclaimable 恒为真
            _retiredCount++;
        }

        public void Published(T[] array)
        {
            if (_published != null && !ReferenceEquals(_published, array))
            {
                if (_retiredCount == MaxRetired) RemoveAt(0);
                _retired[_retiredCount] = _published;
                _retiredAt[_retiredCount] = ColumnReaders.Retire();
                _retiredCount++;
            }
            _published = array;
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
