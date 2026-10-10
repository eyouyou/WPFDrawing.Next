namespace Hevo.Charting.LowCode
{
    /// <summary>
    /// 会长的列 / 快照缓冲的容量参数。参数名对齐 Microsoft.IO.RecyclableMemoryStream
    /// (BlockSize / LargeBufferMultiple / MaximumLargePoolFreeBytes),单位是<b>元素</b>(不是字节),池上限除外。
    /// </summary>
    public sealed record ArrayGrowthOptions
    {
        public static ArrayGrowthOptions Default { get; } = new();

        /// <summary>余量下限(元素):小数组至少多留这么多,避免逐个元素地扩。</summary>
        public int BlockSize { get; init; } = 1024;

        /// <summary>按比例的余量 = 需要量 / GrowthDivisor(默认 8 → 12.5%)。</summary>
        public int GrowthDivisor { get; init; } = 8;

        /// <summary>
        /// 余量上限(元素):大数组的余量封顶在这里,之后按这个步长线性增长(double 默认 64K 个 = 512 KB)。
        /// 对应 RecyclableMemoryStream 的大缓冲线性增长步长。
        /// </summary>
        public int LargeBufferMultiple { get; init; } = 64 * 1024;

        /// <summary>需要量低于容量的 1/ShrinkDivisor 时收缩(默认 2:不到一半就缩)。</summary>
        public int ShrinkDivisor { get; init; } = 2;

        /// <summary>
        /// 每个池的空闲缓冲总字节上限(对应 MaximumLargePoolFreeBytes):超出时丢掉最旧的给 GC,不长期占着。
        /// 池里至少保留一块,否则稳态轮换不起来。
        /// </summary>
        public long MaximumFreeBytes { get; init; } = 8L * 1024 * 1024;
    }

    /// <summary>
    /// 统一的扩缩容规则(纯函数)。用在 ColumnBufferRing、ColumnCallBuffers、Python 固定输入缓冲和
    /// 数据源快照(BufferedDataSource.SnapshotGrowth)上。
    /// <list type="bullet">
    ///   <item>扩容:容量 = 需要量 + clamp(需要量 / GrowthDivisor, BlockSize, LargeBufferMultiple)。
    ///         小数组按比例留余量,大数组余量封顶、线性增长。<see cref="List{T}"/> 是翻倍,余量最多 100% 且不收缩。</item>
    ///   <item>收缩:需要量 &lt; 容量 / ShrinkDivisor 且容量超出复用范围时按需要量重新算。</item>
    ///   <item>复用:池里的旧缓冲容量在 [需要量, 需要量 + 2 × 余量] 内才复用,不拿超大的旧缓冲装短列。</item>
    /// </list>
    /// </summary>
    public static class ArrayGrowth
    {
        /// <summary>需要量对应的余量(元素)。</summary>
        public static int Headroom(int required, ArrayGrowthOptions? options = null)
        {
            var o = options ?? ArrayGrowthOptions.Default;
            if (required <= 0) return 0;
            int byRatio = required / Math.Max(1, o.GrowthDivisor);
            return Math.Clamp(byRatio, o.BlockSize, Math.Max(o.BlockSize, o.LargeBufferMultiple));
        }

        /// <summary>为 <paramref name="required"/> 个元素分配时用的容量。</summary>
        public static int Capacity(int required, ArrayGrowthOptions? options = null)
        {
            if (required <= 0) return 0;
            long cap = (long)required + Headroom(required, options);
            return (int)Math.Min(cap, Array.MaxLength);
        }

        /// <summary>
        /// 当前容量 <paramref name="capacity"/> 装 <paramref name="required"/> 个元素时应该用的新容量;
        /// 返回值等于 capacity 表示不用换(够用且不需要收缩)。
        /// </summary>
        public static int Resize(int capacity, int required, ArrayGrowthOptions? options = null)
        {
            var o = options ?? ArrayGrowthOptions.Default;
            if (required <= 0) return capacity;
            if (capacity < required) return Capacity(required, o);
            if (ShouldShrink(capacity, required, o)) return Capacity(required, o);
            return capacity;
        }

        /// <summary>
        /// 需要量不到容量的 1/ShrinkDivisor,且当前容量已超出复用范围(<see cref="Fits"/> 为假)才缩 ——
        /// 小数组(余量下限 BlockSize 起主导)不会为了省几个元素来回换。
        /// </summary>
        public static bool ShouldShrink(int capacity, int required, ArrayGrowthOptions? options = null)
        {
            var o = options ?? ArrayGrowthOptions.Default;
            return (long)required * Math.Max(1, o.ShrinkDivisor) < capacity && !Fits(capacity, required, o);
        }

        /// <summary>容量为 <paramref name="capacity"/> 的旧缓冲能否拿来装 <paramref name="required"/> 个元素。</summary>
        public static bool Fits(int capacity, int required, ArrayGrowthOptions? options = null)
            => capacity >= required && (long)capacity <= (long)required + 2L * Headroom(required, options);
    }
}
