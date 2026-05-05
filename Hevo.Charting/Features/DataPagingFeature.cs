using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using System.Diagnostics;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// 数据无缝加载 (Infinite Scroll) 配置参数
    /// </summary>
    public record DataFetchOptions
    {
        /// <summary> 安全缓冲区：距离边缘还剩多少根 K 线时触发预加载 (默认 30 根) </summary>
        public int SafeBuffer { get; init; } = 30;
        /// <summary> 预加载乘数：每次触发加载时，请求当前视口宽度的几倍数据 (默认 3 倍) </summary>
        public double PrefetchMultiplier { get; init; } = 3.0;
        /// <summary> 正常节流阀：防止拖拽过快导致的密集请求 (默认 200 毫秒) </summary>
        public TimeSpan Pacing { get; init; } = TimeSpan.FromMilliseconds(200);
        /// <summary> 熔断节流阀：网络异常或报错时的强制冷却时间，保护服务器 (默认 3 秒) </summary>
        public TimeSpan NetworkFaultThrottle { get; init; } = TimeSpan.FromSeconds(3);
    }

    /// <summary>
    /// 数据可用性运行态：分页层是唯一写者，交互层 / 缩放策略是读者。
    /// 通过 <see cref="DataPort{T}"/> 跨 Feature 同步，board 锁保证一致性 ——
    /// 与 <c>PointerHitPort</c> / <c>Viewport.ActiveRange</c> 同模式。
    /// </summary>
    public readonly record struct DataAvailability(bool LeftExhausted, bool RightExhausted);

    /// <summary>
    /// 💥 分页加载 Feature：监听视口变化，命中 SafeBuffer 时调度后端 fetch；维护左右墙状态。
    /// 与 <see cref="ChartInteractionFeature"/> 解耦：交互层只负责手势 → 视口；分页层只负责视口 → fetch。
    /// 墙状态通过 <see cref="AvailabilityPort"/> 暴露，下游（如 HistoryAwareZoomStrategy）按需读取。
    /// <para>
    /// <b>典型用法</b>:
    /// <code>
    /// var availability = new DataPort&lt;DataAvailability&gt;("Avail");
    /// canvas.Add(new DataPagingFeature
    /// {
    ///     OnRequireDataAsync = async (anchor, count) =&gt; await client.LoadKlineAsync(anchor, count),
    ///     AvailabilityPort   = availability,
    /// });
    /// // 同时把 availability 喂给 ChartInteractionFeature.AvailabilityPort,
    /// // 让缩放策略知道"已经撞到上市首日,别再缩"。
    /// </code>
    /// </para>
    /// <para>
    /// <b>数据流</b>:Viewport.ActiveRange Watch → CheckBoundaries 命中 SafeBuffer → fire-and-forget OnRequireDataAsync → 业务侧扩容大数组 → LogicalLength 变化触发本 Feature 重判 → 撞墙时写 AvailabilityPort 锁死方向。
    /// </para>
    /// </summary>
    public class DataPagingFeature : ChartFeature
    {
        public override FeaturePhase Phase => FeaturePhase.PreLayout;

        /// <summary>
        /// 业务回调：(anchorIndex, count) → hasMoreData。
        /// anchorIndex=0 表示拉历史；anchorIndex=lastIndex 表示拉未来；count = 视口宽度 × PrefetchMultiplier。
        /// 返回 false 永久封锁对应方向（撞墙）。
        /// </summary>
        public Func<int, int, Task<bool>>? OnRequireDataAsync { get; init; }

        /// <summary>
        /// 拉取行为参数：节流阀、安全缓冲、网络故障熔断等。
        /// </summary>
        public DataFetchOptions FetchConfig { get; init; } = new();

        /// <summary>
        /// 墙状态对外通道：DSL 装配时与 <see cref="ChartInteractionFeature.AvailabilityPort"/> 共用同一实例。
        /// 写入路径走 board 写锁，跨线程安全；未挂载分页 Feature 的 schema 此端口为空。
        /// </summary>
        public DataPort<DataAvailability> AvailabilityPort { get; init; } = new("DataAvailability");

        // ==========================================
        // 💥 分页加载状态锁 (极其重要，防止雪崩式请求)
        // ==========================================
        // 单飞标记:任意时刻只有一笔 fetch 在路上。volatile 保证跨线程可见性(Watch 在 board 写入线程,fetch 任务可能切线程池)。
        private volatile bool _isFetching = false;
        // 左墙锁:历史数据已见底,onRequire 返回 false 后置为 true,LogicalLength 变化才解锁(切股票场景)。
        private bool _isLeftWallHit = false;
        // 右墙锁:最新数据已见顶,LogicalLength 长大就解锁(心跳场景)。
        private bool _isRightWallHit = false;
        // 上次见到的 LogicalLength,用来侦测大数组扩容 / 切股票清空,以决定是否解锁墙锁。
        private int _lastSeenLength = -1;

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            flow.Watch(new object[] { Viewport.ActiveRange }, board =>
            {
                using (board.AcquireUpgradeableReadLock())
                {
                    var activeRange = board.Read(Viewport.ActiveRange);
                    int logicalLength = board.Read(Viewport.LogicalLength);
                    if (!activeRange.IsValid || logicalLength <= 0) return;
                    CheckBoundaries(activeRange, logicalLength, board);
                }
            });
        }

        protected override void OnProject(FeatureContext ctx) { }

        private void PublishAvailability(DataBlackboard board)
        {
            using (board.AcquireWriteLock())
                board.WriteIfChanged(AvailabilityPort, new DataAvailability(_isLeftWallHit, _isRightWallHit));
        }

        /// <summary>
        /// 史诗级修正：彻底根除左右墙互锁与心跳破碎 Bug
        /// 负责在每一帧侦测：用户是否快把图表拖到头了？
        /// </summary>
        private void CheckBoundaries(RealRange activeRange, int logicalLength, DataBlackboard board)
        {
            if (OnRequireDataAsync == null || logicalLength <= 0 || _isFetching) return;

            // 1. 数据重置或增量侦测防线
            // 通过监测底层大数组的长度，智能判断墙锁是否应该被打破
            if (_lastSeenLength != logicalLength)
            {
                if (logicalLength < _lastSeenLength)
                {
                    // 数据变短（如切换股票发生了 Clear），双墙重置
                    _isLeftWallHit = false;
                    _isRightWallHit = false;
                }
                else
                {
                    // 数据变长（心跳拉了新数据，或左侧拉了历史）
                    // 只要数据长了，说明右侧有可能有新空间了，解除右墙限制
                    // 但是左墙代表上市首日，如果曾被击中则永远不解，除非切股票
                    _isRightWallHit = false;
                }
                _lastSeenLength = logicalLength;
                PublishAvailability(board);
            }

            int offsetAmount = (int)Math.Ceiling(activeRange.Span * FetchConfig.PrefetchMultiplier);

            // 2. 纯粹的饥饿判断 (Buffer Threshold Check)
            if (activeRange.Min <= FetchConfig.SafeBuffer && !_isLeftWallHit)
            {
                Debug.WriteLine($"🚀 [Paging] Hitting LEFT boundary. Requesting data...");
                FireRequestSafe(isLeft: true, anchorIndex: 0, offsetAmount, board);
            }
            else if (activeRange.Max >= logicalLength - FetchConfig.SafeBuffer && !_isRightWallHit)
            {
                Debug.WriteLine($"🚀 [Paging] Hitting RIGHT boundary. Requesting data...");
                FireRequestSafe(isLeft: false, anchorIndex: logicalLength - 1, offsetAmount, board);
            }
        }

        // FireRequestAsync 已改为 async Task，调用点位于 UpgradeableReadLock 内不能 await，
        // 故走 fire-and-forget；ContinueWith 兜底任何穿透内层 catch 的异常，避免成为 UnobservedTaskException。
        private void FireRequestSafe(bool isLeft, int anchorIndex, int offsetAmount, DataBlackboard board)
        {
            _ = FireRequestAsync(isLeft, anchorIndex, offsetAmount, board)
                .ContinueWith(
                    t => Debug.WriteLine($"🚨 [Paging] Unobserved exception escaped FireRequestAsync: {t.Exception}"),
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        /// <summary>
        /// 异步防火墙队列：调度拉取数据，提供异常熔断、重复数据识别防呆机制
        /// </summary>
        private async Task FireRequestAsync(bool isLeft, int anchorIndex, int offsetAmount, DataBlackboard board)
        {
            if (_isFetching) return; // 防御并发重入
            // 上游 OnRequireData 在 RequestPaging 入口已 null 守过；这里再捕获到本地，方便 NRT 流分析
            if (OnRequireDataAsync is not { } requestData) return;
            _isFetching = true;

            bool isNetworkFault = false, hasMoreData = true;
            TimeSpan delay = FetchConfig.Pacing;

            // 记下请求前的物理真理（用于后续判断业务层是否欺骗了我们）
            int lengthBeforeFetch = 0;
            if (!board.IsDisposed)
            {
                using (board.AcquireReadLock()) { lengthBeforeFetch = board.Read(Viewport.LogicalLength); }
            }

            try
            {
                try
                {
                    hasMoreData = await requestData.Invoke(anchorIndex, offsetAmount);
                }
                catch (OperationCanceledException)
                {
                    // HttpClient 超时：免除额外熔断惩罚
                    Debug.WriteLine($"⚠️ [Paging] Timeout occurred. Bypassing additional throttle penalty.");
                    isNetworkFault = true;
                    delay = TimeSpan.Zero;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"⚠️ [Paging] Exception: {ex.Message}");
                    isNetworkFault = true;
                    delay = FetchConfig.NetworkFaultThrottle;
                }

                if (!isNetworkFault && !board.IsDisposed)
                {
                    bool wallHit = false;
                    using (board.AcquireReadLock())
                    {
                        int lengthAfterFetch = board.Read(Viewport.LogicalLength);

                        // 假数据/重复数据防线：业务说有数据但底层长度没变 → 强制视为撞墙
                        if (hasMoreData && lengthAfterFetch <= lengthBeforeFetch)
                        {
                            Debug.WriteLine($"🛑 [Paging] Fake/Duplicate data detected! Length remained at {lengthAfterFetch}. Forcing wall lock.");
                            hasMoreData = false;
                        }

                        if (!hasMoreData)
                        {
                            Debug.WriteLine($"🛑 [Paging] Wall reached on {(isLeft ? "LEFT" : "RIGHT")}. Locking.");
                            if (isLeft) _isLeftWallHit = true;
                            else _isRightWallHit = true;
                            wallHit = true;
                        }
                    }

                    // ReadLock 释放后再 publish：AcquireWriteLock 不能在 ReadLock 内嵌套
                    if (wallHit) PublishAvailability(board);
                }

                if (delay > TimeSpan.Zero) await Task.Delay(delay);
            }
            catch (TaskCanceledException) { /* 安全吃掉 Task.Delay 因为框架卸载带来的系统取消异常 */ }
            catch (Exception ex) { Debug.WriteLine($"🚨 [Paging] Fatal: {ex}"); }
            finally
            {
                _isFetching = false;

                // 痊愈后自启循环引擎：验证刚才加载的一波数据是否足够填饱肚子
                // 注意：UpgradeableReadLock 而非 ReadLock —— CheckBoundaries 内部 PublishAvailability 需要升级到 WriteLock
                try
                {
                    if (board != null && !board.IsDisposed)
                    {
                        using (board.AcquireUpgradeableReadLock())
                        {
                            CheckBoundaries(board.Read(Viewport.ActiveRange), board.Read(Viewport.LogicalLength), board);
                        }
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"🔥 [Paging] CRITICAL ERROR IN RECOVERY LOOP: {ex}"); }
            }
        }
    }
}
