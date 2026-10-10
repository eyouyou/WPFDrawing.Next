namespace Hevo.Charting.LowCode
{
    /// <summary>
    /// 自写摄入器(<see cref="IDataIngestor{TItem}"/>)往黑板发布一列时用:每次发布写进一块"没有读者在读"的缓冲,
    /// 绝不改写已经发布出去的那块(锁外的帧 / 指标可能正在读它)。内部就是框架摄入器用的 ColumnBufferPool,
    /// 容量按 <see cref="CapacityPolicy"/> 留余量,稳态在两三块缓冲之间轮换,不分配。
    /// <code>
    /// var buf = _diff.Rent(n);                    // 1. 租
    /// for (...) buf[i] = ...;                     // 2. 填前 n 个(缓冲里是旧值,每个都要写)
    /// _diff.Publish(board, _diffPort, buf, n);    // 3. 发布(写进端口,上一块退役)
    /// </code>
    /// 一个端口一个 ColumnPublisher;在 <c>Process</c>(黑板写事务内)里调用。单写者,不加锁。
    /// 中间计算用的临时数组不要走它(不发布的东西用普通字段或 ArrayPool 即可)。
    /// </summary>
    public sealed class ColumnPublisher<T>
    {
        private readonly ColumnBufferPool<T> _pool;

        public ColumnPublisher(CapacityPolicyOptions? options = null) => _pool = new ColumnBufferPool<T>(options);

        /// <summary>租一块至少 <paramref name="length"/> 长的缓冲(可能更长,只用前 length 个)。</summary>
        public T[] Rent(int length) => _pool.Rent(length);

        /// <summary>把租来的缓冲前 <paramref name="length"/> 个发布到 <paramref name="port"/>;上一次发布的那块退役。</summary>
        public void Publish(DataBlackboard board, DataPort<ReadOnlyMemory<T>> port, T[] buffer, int length)
        {
            board.ForceWrite(port, new ReadOnlyMemory<T>(buffer, 0, length));
            _pool.Published(buffer);
        }

        /// <summary>租了但决定不发布(比如内容没变):还回来,没人见过它,可以立刻再用。</summary>
        public void Discard(T[] buffer) => _pool.Unused(buffer);
    }
}
