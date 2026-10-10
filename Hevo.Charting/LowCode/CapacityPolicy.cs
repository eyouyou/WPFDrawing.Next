namespace Hevo.Charting.LowCode
{
    /// <summary>
    /// 会长的列 / 快照缓冲的容量参数:留多少余量、什么时候缩、空闲缓冲最多占多少。
    /// 单位是<b>元素</b>(不是字节),<see cref="MaxIdleBytes"/> 除外。
    /// 思路参照 Microsoft.IO.RecyclableMemoryStream,各参数注释里写了对应的 RMS 参数。
    /// </summary>
    public sealed record CapacityPolicyOptions
    {
        public static CapacityPolicyOptions Default { get; } = new();

        /// <summary>最少预留多少个元素:小数组至少多留这么多,避免逐个元素地扩。(对应 RMS 的 BlockSize)</summary>
        public int MinReserve { get; init; } = 1024;

        /// <summary>按比例预留:余量 = 需要量 × ReserveRatio(默认 0.125,即 12.5%)。(RMS 没有对应项,它的小块是固定大小)</summary>
        public double ReserveRatio { get; init; } = 0.125;

        /// <summary>
        /// 最多预留多少个元素:大数组的余量封顶在这里,之后按这个步长线性增长(double 默认 64K 个 = 512 KB)。
        /// (对应 RMS 的 LargeBufferMultiple:大缓冲线性增长步长)
        /// </summary>
        public int MaxReserve { get; init; } = 64 * 1024;

        /// <summary>需要量低于容量的 1/ShrinkDivisor 时收缩(默认 2:不到一半就缩)。</summary>
        public int ShrinkDivisor { get; init; } = 2;

        /// <summary>
        /// 每个池的空闲缓冲总字节上限:超出时丢掉最旧的给 GC,不长期占着。(对应 RMS 的 MaximumLargePoolFreeBytes)
        /// 池里至少保留一块,否则稳态轮换不起来。
        /// </summary>
        public long MaxIdleBytes { get; init; } = 8L * 1024 * 1024;
    }

    /// <summary>
    /// 统一的扩缩容规则(纯函数)。用在 ColumnBufferPool、ResultBufferPool、Python 固定输入缓冲和
    /// 数据源快照(BufferedDataSource.ReserveSnapshotCapacity)上。
    /// <list type="bullet">
    ///   <item>扩容:容量 = 需要量 + clamp(需要量 × ReserveRatio, MinReserve, MaxReserve)。
    ///         小数组按比例留余量,大数组余量封顶、线性增长。<see cref="List{T}"/> 是翻倍,余量最多 100% 且不收缩。</item>
    ///   <item>收缩:需要量 &lt; 容量 / ShrinkDivisor 且容量超出复用范围时按需要量重新算。</item>
    ///   <item>复用:池里的旧缓冲容量在 [需要量, 需要量 + 2 × 余量] 内才复用,不拿超大的旧缓冲装短列。</item>
    /// </list>
    /// </summary>
    public static class CapacityPolicy
    {
        // Array.MaxLength 是 .NET 6+ API;开发规范要求可在 .NET 5 SDK 下编译,这里写死同值
        private const int MaxArrayLength = 0x7FFFFFC7;

        /// <summary>需要量对应的余量(元素)。</summary>
        public static int Headroom(int required, CapacityPolicyOptions? options = null)
        {
            var o = options ?? CapacityPolicyOptions.Default;
            if (required <= 0) return 0;
            int byRatio = (int)(required * Math.Max(0, o.ReserveRatio));
            return Math.Clamp(byRatio, o.MinReserve, Math.Max(o.MinReserve, o.MaxReserve));
        }

        /// <summary>为 <paramref name="required"/> 个元素分配时用的容量。</summary>
        public static int Capacity(int required, CapacityPolicyOptions? options = null)
        {
            if (required <= 0) return 0;
            long cap = (long)required + Headroom(required, options);
            return (int)Math.Min(cap, MaxArrayLength);
        }

        /// <summary>
        /// 当前容量 <paramref name="capacity"/> 装 <paramref name="required"/> 个元素时应该用的新容量;
        /// 返回值等于 capacity 表示不用换(够用且不需要收缩)。
        /// </summary>
        public static int Resize(int capacity, int required, CapacityPolicyOptions? options = null)
        {
            var o = options ?? CapacityPolicyOptions.Default;
            if (required <= 0) return capacity;
            if (capacity < required) return Capacity(required, o);
            if (ShouldShrink(capacity, required, o)) return Capacity(required, o);
            return capacity;
        }

        /// <summary>
        /// 需要量不到容量的 1/ShrinkDivisor,且当前容量已超出复用范围(<see cref="Fits"/> 为假)才缩 ——
        /// 小数组(余量下限 MinReserve 起主导)不会为了省几个元素来回换。
        /// </summary>
        public static bool ShouldShrink(int capacity, int required, CapacityPolicyOptions? options = null)
        {
            var o = options ?? CapacityPolicyOptions.Default;
            return (long)required * Math.Max(1, o.ShrinkDivisor) < capacity && !Fits(capacity, required, o);
        }

        /// <summary>容量为 <paramref name="capacity"/> 的旧缓冲能否拿来装 <paramref name="required"/> 个元素。</summary>
        public static bool Fits(int capacity, int required, CapacityPolicyOptions? options = null)
            => capacity >= required && (long)capacity <= (long)required + 2L * Headroom(required, options);
    }
}
