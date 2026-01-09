namespace Hevo.Charting.Core
{
    /// <summary>
    /// 0-GC 绝对安全的无限版本令牌 (Value Type)。
    /// 💥 架构防御：屏蔽了大小比较（<, >），仅对外暴露对齐判定，利用整数溢出的环形特性实现真正的无限生命周期。
    /// </summary>
    public readonly struct VersionToken : IEquatable<VersionToken>
    {
        private readonly long _version;

        // 仅允许时钟内部实例化
        internal VersionToken(long version) => _version = version;

        public bool Equals(VersionToken other) => _version == other._version;
        public override bool Equals(object? obj) => obj is VersionToken other && Equals(other);
        public override int GetHashCode() => _version.GetHashCode();

        // 💥 编译器级拦截：只允许判等，彻底封杀任何试图比较大小的业务代码！
        // 这样即使底层 long 发生溢出绕回 (Wrap-around)，逻辑也绝对不会崩溃。
        public static bool operator ==(VersionToken left, VersionToken right) => left._version == right._version;
        public static bool operator !=(VersionToken left, VersionToken right) => left._version != right._version;
    }

    /// <summary>
    /// 全局通用状态时钟 (无锁、防溢出、无限运转的驱动源)
    /// 职责：为系统环境或数据源提供版本号推进，并生成 0-GC 的快照令牌。
    /// </summary>
    public class StateClock
    {
        private long _currentTicks = 0;

        /// <summary>
        /// 推进时钟 (线程安全)
        /// </summary>
        public void Advance()
        {
            // unchecked 是 C# 默认行为：当到达 long.MaxValue 时，会自动溢出绕回 long.MinValue。
            // 配合 VersionToken 禁用了大小比较，这种溢出变成了绝对安全的“环形运转”。
            Interlocked.Increment(ref _currentTicks);
        }

        /// <summary>
        /// 获取当前的时间切片快照 (0-GC Allocation)
        /// </summary>
        public VersionToken Snapshot()
        {
            return new VersionToken(Interlocked.Read(ref _currentTicks));
        }
    }

    /// <summary>
    /// 💥 全局通用的 0-GC 引用容器 (用于长生命周期组件内部的数据穿透)
    /// </summary>
    public class RefBox<T> { public T Value = default!; }
}
