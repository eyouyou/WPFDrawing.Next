using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Linked
{
    /// <summary>
    /// 多 ChartCell 列式联动 dashboard 的共享状态容器(领域无关)。
    ///
    /// <para>
    /// 现实情况:每个 ChartCell 自带独立 <see cref="DataBlackboard"/>(由 schema 的 pipe new 出来)。
    /// 仅"两 schema 持有同一个 <see cref="DataPort{T}"/> 实例"无法跨 cell 同步状态——port 只是键,
    /// blackboard 才是值容器。本类内置端口镜像桥,把指定的几个 port 在所有
    /// attached board 之间双向同步,达成跨 cell 联动。
    /// </para>
    ///
    /// <para>
    /// 业务方有两种用法:
    /// <list type="number">
    ///   <item>直接 new <see cref="LinkedChartContext"/>:已经够用 4 个默认镜像 port</item>
    ///   <item>派生子类(如 <c>LinkedKLineContext : LinkedChartContext</c>),在子类里
    ///         挂业务自有的数据源/扩展端口,用 <see cref="RegisterMirror{T}"/> 加更多镜像 port</item>
    /// </list>
    /// </para>
    /// </summary>
    public class LinkedChartContext
    {
        /// <summary>
        /// 共享视口端口集——所有 schema 的 viewport 指向同一对 UserRange/ActiveRange/LogicalLength。
        /// </summary>
        public ViewportPorts SharedViewport { get; } = new();

        /// <summary>
        /// 共享 hit 端口——crosshair 同步的载体。
        /// </summary>
        public DataPort<PointerHitState?> SharedHit { get; } = new("LinkedHit");

        /// <summary>
        /// 所有 pane 必须使用相同的水平 layout 边距,crosshair 才能在垂直方向对齐。
        /// </summary>
        public ChartLength HorizontalLeft { get; init; } = ChartLength.Pixel(60);
        public ChartLength HorizontalRight { get; init; } = ChartLength.Pixel(0);

        // ── 端口镜像桥 ─────────────────────────────────────────────────────
        // 列表锁定哪些 port 跨 cell 镜像。默认 4 个;业务方可通过 RegisterMirror 扩展。
        private readonly List<IPortMirror> _mirrors;
        private readonly List<DataBlackboard> _boards = new();

        public LinkedChartContext()
        {
            _mirrors = new List<IPortMirror>
            {
                PortMirror.For(SharedViewport.UserRange),
                PortMirror.For(SharedViewport.ActiveRange),
                PortMirror.For(SharedViewport.LogicalLength),
                PortMirror.For(SharedHit),
            };
        }

        /// <summary>
        /// 业务方扩展点:把自定义 port 加入跨 cell 镜像列表。
        /// 如果调用时已有 board attached,会立即对其订阅(确保后续写入也镜像)。
        /// </summary>
        public void RegisterMirror<T>(DataPort<T> port)
        {
            if (port is null) throw new ArgumentNullException(nameof(port));
            var mirror = PortMirror.For(port);
            _mirrors.Add(mirror);
            for (int i = 0; i < _boards.Count; i++)
                mirror.SubscribeOn(_boards[i], _boards);
        }

        /// <summary>
        /// 由 <see cref="LinkedChartDashboard"/> 在每个 schema 的 board 就绪时调用一次。
        /// 同一 board 多次 attach 直接 short-circuit,幂等。
        /// </summary>
        internal void AttachBoard(DataBlackboard board)
        {
            if (board is null || _boards.Contains(board)) return;
            _boards.Add(board);

            // 关键:每个被镜像 port 各订阅一次。仅这几个 port 写入触发回调,
            // 数据列(Times/Closes/Volumes 等)写入零开销——不挂全局 OnPortUpdated。
            foreach (var m in _mirrors) m.SubscribeOn(board, _boards);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  端口镜像基础设施(框架内部)
    // ──────────────────────────────────────────────────────────────────────

    internal interface IPortMirror
    {
        void SubscribeOn(DataBlackboard board, IReadOnlyList<DataBlackboard> all);
    }

    /// <summary>
    /// 类型安全的 per-port 镜像器。当 origin board 写入 _port 时,把值
    /// 同步写到 all 中其它 board。<see cref="DataBlackboard.WriteIfChanged{T}"/>
    /// 在值相等时短路,自然防回环。
    /// </summary>
    internal sealed class PortMirror<T> : IPortMirror
    {
        private readonly DataPort<T> _port;
        public PortMirror(DataPort<T> port) => _port = port;

        public void SubscribeOn(DataBlackboard board, IReadOnlyList<DataBlackboard> all)
        {
            // 仅 _port 写入时回调,与该 board 上其它 port 写入完全无关。
            board.Subscribe(_port, origin =>
            {
                // 调用栈:NotifyPort ← ForceWrite/EndTransaction(均持 origin 写锁)。
                // ReaderWriterLockSlim 配置了 SupportsRecursion,同线程递归读 OK。
                T value;
                using (origin.AcquireReadLock()) value = origin.Read(_port);

                for (int i = 0; i < all.Count; i++)
                {
                    var b = all[i];
                    if (ReferenceEquals(b, origin)) continue;
                    using (b.AcquireWriteLock())
                    {
                        b.WriteIfChanged(_port, value);
                        //                ↑ 值相等就短路,自动断回环
                    }
                }
            });
        }
    }

    internal static class PortMirror
    {
        public static IPortMirror For<T>(DataPort<T> port) => new PortMirror<T>(port);
    }
}
