using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// 缩放配置参数
    /// </summary>
    public record ZoomOptions
    {
        /// <summary> 缩放灵敏度 (默认 0.15 即每次滚轮缩放 15% 的视口) </summary>
        public double Sensitivity { get; init; } = 0.15;
    }

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

    // ==========================================
    // 💥 交互状态载荷 (彻底更新为 0-GC 的值类型及纯 float 坐标 HevoPoint)
    // ==========================================

    /// <summary> X 轴逻辑域碰撞结果 </summary>
    public record struct DomainHitResult(int LogicalIndex, double SnapX, double CenterRelativeX);

    /// <summary> 全局指针 (鼠标/十字光标) 命中状态 </summary>
    public record struct PointerHitState(HevoPoint MousePos, DomainHitResult Hit, double CenterX, int LocalIndex, bool IsOutOfBounds);

    /// <summary> 指针越界策略 </summary>
    public enum PointerOutOfBoundsStrategy
    {
        /// <summary> 自由模式：超出数据区依然显示光标 </summary>
        Free,
        /// <summary> 强吸附模式：超出数据区时，光标强制吸附在最后一根或第一根有效 K 线上 </summary>
        SnapToValidData
    }

    /// <summary> 图表支持的交互模式标记 (支持位运算组合) </summary>
    [Flags]
    public enum ChartInteractionMode
    {
        None = 0,
        Hover = 1 << 0,
        Pan = 1 << 1,
        Zoom = 1 << 2,
        Keyboard = 1 << 3,
        All = Hover | Pan | Zoom | Keyboard,
        TimeShareDefault = Hover,
        Default = Hover | Keyboard
    }

    /// <summary>
    /// 💥 图表交互总枢纽大脑
    /// 职责：接管底层 UI 框架事件 -> 降维至 Hevo 坐标系 -> 推演视口数学变化 -> 写入黑板。
    /// 附带边界侦测与异步数据分页调度能力。
    /// </summary>
    public class ChartInteractionFeature : ChartFeature
    {
        // 铁律：交互运算必须在布局之前完成，确保后续的图层拿到的是最新视口
        public override FeaturePhase Phase => FeaturePhase.PreLayout;

        // --- 核心引脚（Viewport 由 ChartFeature 基类自动注入）---
        /// <summary> 暴露给外部层(如 Tooltip, Crosshair) 订阅的光标全局状态 </summary>
        public DataPort<PointerHitState?> PointerHitPort { get; init; } = new("GlobalPointerHit");
        public DataPort<int>? ValidDataCountPort { get; init; }

        // --- 行为配置 ---
        public ChartInteractionMode SupportedModes { get; init; } = ChartInteractionMode.All;
        public PointerOutOfBoundsStrategy PointerSnapMode { get; init; } = PointerOutOfBoundsStrategy.Free;
        public Func<int, int, Task<bool>>? OnRequireDataAsync { get; init; }
        public IZoomStrategy ZoomStrategy { get; init; } = new SmartAdaptiveZoomStrategy();
        public ZoomOptions ZoomConfig { get; init; } = new();
        public DataFetchOptions FetchConfig { get; init; } = new();
        public double EdgeHitTolerance { get; init; } = 1.0;

        private const double StandardWheelDelta = 120.0;

        // ==========================================
        // 💥 内部交互状态 (全部降维至 HevoPoint)
        // ==========================================
        private HevoPoint _lastPanPos;
        private bool _isPanning;
        private HevoPoint? _lastMousePos;

        // ==========================================
        // 💥 分页加载状态锁 (极其重要，防止雪崩式请求)
        // ==========================================
        private volatile bool _isFetching = false;
        private bool _isLeftWallHit = false;  // 左墙锁：历史数据已见底，不要再请求了
        private bool _isRightWallHit = false; // 右墙锁：最新数据已见顶，不要再请求了
        private int _lastSeenLength = -1;     // 用于侦测底层大数组是否真的发生了扩容

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            // 1. 鼠标按下：启动平移 (Pan) 状态
            WithBoard(this.OnMouse(UIElement.MouseDownEvent)).Subscribe(c =>
            {
                chart.Focusable = true; chart.Focus();
                if (!SupportedModes.HasFlag(ChartInteractionMode.Pan)) return;

                _isPanning = true;
                // 💥 拦截口：将 WPF 原生 Point 瞬间降维转换，隔离底层依赖
                var wpfPos = c.Event.GetPosition(chart);
                _lastPanPos = new HevoPoint((float)wpfPos.X, (float)wpfPos.Y);

                // 拖拽时隐藏十字光标
                using (c.Board.AcquireWriteLock()) c.Board.WriteIfChanged(PointerHitPort, null);
                chart.CaptureMouse();
            }).DisposeWith(this);

            // 2. 鼠标抬起：解除平移状态
            WithBoard(this.OnMouse(UIElement.MouseUpEvent)).Subscribe(c =>
            {
                if (_isPanning) { _isPanning = false; chart.ReleaseMouseCapture(); }
            }).DisposeWith(this);

            // 3. 鼠标离开：清空光标缓存，隐藏十字光标
            WithBoard(this.OnMouse(UIElement.MouseLeaveEvent)).Subscribe(c =>
            {
                _lastMousePos = null;
                if (!_isPanning) using (c.Board.AcquireWriteLock()) c.Board.WriteIfChanged(PointerHitPort, null);
            }).DisposeWith(this);

            // 4. 鼠标移动：处理平移拖拽，或更新 Hover 十字光标
            WithBoard(this.OnMouse(UIElement.MouseMoveEvent)).Subscribe(c =>
            {
                // 💥 拦截口：获取相对于绘图区画布的局部坐标，并降维转换
                var wpfPos = c.Event.GetPosition(chart.DrawingCanvas);
                _lastMousePos = new HevoPoint((float)wpfPos.X, (float)wpfPos.Y);

                if (_isPanning)
                {
                    // 拖拽时获取相对于整个控件的全局坐标以避免抖动
                    var wpfPanPos = c.Event.GetPosition(chart);
                    HandlePan(ctx, c.Board, new HevoPoint((float)wpfPanPos.X, (float)wpfPanPos.Y));
                }
                else if (SupportedModes.HasFlag(ChartInteractionMode.Hover))
                {
                    using (c.Board.AcquireUpgradeableReadLock())
                    {
                        var activeRange = c.Board.Read(Viewport.ActiveRange);
                        var scaleStrategy = ctx.Shared().Read<ScaleStrategyTrait>();
                        if (activeRange.IsValid && scaleStrategy != null && !ctx.GetPlotArea().IsEmpty)
                            UpdatePointerStateFromMouse(ctx.GetPlotArea(), scaleStrategy, c.Board, activeRange, _lastMousePos.Value);
                    }
                }
            }).DisposeWith(this);

            // 5. 鼠标滚轮：处理 Zoom 缩放
            WithBoard(this.OnMouse(UIElement.MouseWheelEvent)).Subscribe(c =>
            {
                if (c.Event is not MouseWheelEventArgs wheelEvent || !SupportedModes.HasFlag(ChartInteractionMode.Zoom)) return;

                // 💥 拦截口：降维转换
                var wpfPos = wheelEvent.GetPosition(chart.DrawingCanvas);
                _lastMousePos = new HevoPoint((float)wpfPos.X, (float)wpfPos.Y);

                HandleZoom(ctx, c.Board, _lastMousePos.Value, wheelEvent.Delta);
            }).DisposeWith(this);

            // 6. 键盘事件：支持方向键微调光标位置
            WithBoard(this.OnKey(UIElement.PreviewKeyDownEvent)).Subscribe(c =>
            {
                if (!SupportedModes.HasFlag(ChartInteractionMode.Keyboard) || c.Event is not KeyEventArgs keyEvent) return;

                if (keyEvent.Key == Key.Left) { HandleKeyboardNavigation(ctx, c.Board, -1); keyEvent.Handled = true; }
                else if (keyEvent.Key == Key.Right) { HandleKeyboardNavigation(ctx, c.Board, 1); keyEvent.Handled = true; }
            }).DisposeWith(this);

            // 7. 💥 核心监视器：永远监听视口的改变！
            // 只要视口改变，立即触发两件事：1. 判断是否需要加载更多数据；2. 刷新十字光标位置。
            flow.Watch(new object[] { Viewport.ActiveRange }, board =>
            {
                using (board.AcquireUpgradeableReadLock())
                {
                    var activeRange = board.Read(Viewport.ActiveRange);
                    int logicalLength = board.Read(Viewport.LogicalLength);

                    // ctx.GetPlotArea() 现已纯净返回 HevoRect
                    var plotArea = ctx.GetPlotArea();
                    var scaleStrategy = ctx.Shared().Read<ScaleStrategyTrait>();

                    if (!activeRange.IsValid || plotArea.IsEmpty || scaleStrategy == null) return;

                    CheckBoundaries(activeRange, logicalLength, board);

                    if (_lastMousePos.HasValue && !_isPanning)
                        UpdatePointerStateFromMouse(plotArea, scaleStrategy, board, activeRange, _lastMousePos.Value);
                }
            });

        }

        /// <summary>
        /// 处理图表拖拽：将物理像素的位移，反向推演为逻辑视口的平移
        /// </summary>
        private void HandlePan(RenderContext ctx, DataBlackboard board, HevoPoint currentPos)
        {
            using (board.AcquireUpgradeableReadLock())
            {
                var plotArea = ctx.GetPlotArea();
                var scaleStrategy = ctx.Shared().Read<ScaleStrategyTrait>();
                var baseRange = board.Read(Viewport.ActiveRange);

                if (plotArea.IsEmpty || scaleStrategy == null || !baseRange.IsValid || baseRange.Span <= 0) return;

                double deltaX = currentPos.X - _lastPanPos.X;
                _lastPanPos = currentPos;
                if (deltaX == 0) return;

                // 算法：物理位移比例 * 逻辑总跨度 = 逻辑位移量
                double expandedSpan = scaleStrategy.DomainScale.Denormalize(1.0, baseRange) - scaleStrategy.DomainScale.Denormalize(0.0, baseRange);
                double logicalDelta = (deltaX / plotArea.Width) * expandedSpan;

                using (board.AcquireWriteLock())
                    board.WriteIfChanged(Viewport.UserRange, new RealRange(baseRange.Min - logicalDelta, baseRange.Max - logicalDelta));
            }
        }

        /// <summary>
        /// 处理滚轮缩放：根据鼠标所在的相对位置，以其为原点进行视口伸缩
        /// </summary>
        private void HandleZoom(RenderContext ctx, DataBlackboard board, HevoPoint pos, int delta)
        {
            using (board.AcquireUpgradeableReadLock())
            {
                var baseRange = board.Read(Viewport.ActiveRange);
                int logicalLength = board.Read(Viewport.LogicalLength);
                var plotArea = ctx.GetPlotArea();

                if (!baseRange.IsValid || plotArea.Width <= 0 || logicalLength <= 0) return;

                // 基础缩放系数推导
                double steps = delta / StandardWheelDelta;
                double zoomFactor = Math.Pow(1.0 - ZoomConfig.Sensitivity, steps);

                // 获取极值限制，防止缩放至崩溃
                var limits = ctx.Shared().Read<ViewportLimitsTrait>();
                double minSpan = limits != null ? limits.MinSpan : 2.0;

                double maxIndex = Math.Max(0, logicalLength - 1);
                double maxSpan = limits != null ? maxIndex * limits.MaxSpanMultiplier : maxIndex * 2.0;
                if (maxSpan < minSpan) maxSpan = minSpan;

                double rawTargetSpan = baseRange.Span * zoomFactor;
                double clampedTargetSpan = Math.Clamp(rawTargetSpan, minSpan, maxSpan);

                // 视口大小未发生实质变化则静默返回
                if (Math.Abs(clampedTargetSpan - baseRange.Span) < 1e-5) return;

                // 核心：捕获鼠标当前相对画口的百分比位置，作为缩放不动的锚点
                double relativeX = Math.Clamp((pos.X - plotArea.Left) / plotArea.Width, 0, 1.0);
                var zoomCtx = new ZoomContext(baseRange, clampedTargetSpan, board.Read(PointerHitPort), relativeX);

                RealRange newRange = ZoomStrategy.Calculate(zoomCtx);

                using (board.AcquireWriteLock())
                    board.WriteIfChanged(Viewport.UserRange, newRange);
            }
        }

        /// <summary> 
        /// 处理键盘方向键导航 (平移视口或移动十字光标)
        /// </summary>
        private void HandleKeyboardNavigation(RenderContext ctx, DataBlackboard board, int indexDelta)
        {
            using (board.AcquireUpgradeableReadLock())
            {
                var hit = board.Read(PointerHitPort);
                var baseRange = board.Read(Viewport.ActiveRange);

                // 💥 拿到极其重要的渲染上下文
                var plotArea = ctx.GetPlotArea();
                var scaleStrategy = ctx.Shared().Read<ScaleStrategyTrait>();

                if (hit == null || !baseRange.IsValid || plotArea.IsEmpty || scaleStrategy == null) return;

                int targetIndex = hit.Value.Hit.LogicalIndex + indexDelta;

                // 1. 防线：绝对不能越界到没有数据的地方
                int logicalLength = board.Read(Viewport.LogicalLength);
                int offset = board.Read(Viewport.Offset);
                int validCount = ValidDataCountPort != null ? board.Read(ValidDataCountPort) : logicalLength;
                if (validCount <= 0) validCount = logicalLength;

                int minIndex = offset;
                int maxIndex = offset + validCount - 1;

                targetIndex = Math.Clamp(targetIndex, minIndex, maxIndex);

                // 如果已经到底了，拒绝移动
                if (targetIndex == hit.Value.Hit.LogicalIndex) return;

                // 2. 若目标索引在当前视口之外，强制视口跟随平移
                bool isRangeChanged = false;
                if (targetIndex < baseRange.Min)
                {
                    using (board.AcquireWriteLock()) board.WriteIfChanged(Viewport.UserRange, new RealRange(targetIndex, targetIndex + baseRange.Span));
                    isRangeChanged = true;
                }
                else if (targetIndex > baseRange.Max)
                {
                    using (board.AcquireWriteLock()) board.WriteIfChanged(Viewport.UserRange, new RealRange(targetIndex - baseRange.Span, targetIndex));
                    isRangeChanged = true;
                }

                // 3. 核心修复：用真实的 Scale 精准反推物理坐标，绝对的一分不差！
                // 注意：如果视口刚才发生了平移，我们需要用平移后的新视口来计算
                var currentRange = isRangeChanged ? board.Read(Viewport.ActiveRange) : baseRange;

                double centerRelativeX = scaleStrategy.DomainScale.Normalize(targetIndex, currentRange);
                double targetPhysicalX = plotArea.Left + (plotArea.Width * centerRelativeX);

                // 4. 合成完美无瑕的物理鼠标位置 (保留 Y 轴位置不变)
                _lastMousePos = new HevoPoint((float)targetPhysicalX, hit.Value.MousePos.Y);

                // 5. 💥 终极答案：复用统一的判定管道！
                // 这将完美触发吸附逻辑、边界逻辑，并由该方法负责写回黑板
                UpdatePointerStateFromMouse(plotArea, scaleStrategy, board, currentRange, _lastMousePos.Value);
            }
        }

        /// <summary> 
        /// 指针碰撞检测：将鼠标物理位置投射至黑板的数学模型中，并触发吸附判定
        /// </summary>
        private void UpdatePointerStateFromMouse(HevoRect plotArea, ScaleStrategyTrait scaleStrategy, DataBlackboard board, RealRange activeRange, HevoPoint pos)
        {
            double margin = EdgeHitTolerance;
            // 若超出容差边缘，判定为离场，清空光标
            if (pos.X < plotArea.Left - margin || pos.X > plotArea.Right + margin || pos.Y < plotArea.Top - margin || pos.Y > plotArea.Bottom + margin)
            {
                using (board.AcquireWriteLock()) board.WriteIfChanged(PointerHitPort, null);
                return;
            }

            // 1. 根据鼠标相对物理位移，反向 Denormalize 查找到准确的逻辑索引
            double relativeX = Math.Clamp((pos.X - plotArea.Left) / plotArea.Width, 0.0, 1.0);
            double logicalValue = scaleStrategy.DomainScale.Denormalize(relativeX, activeRange);
            int globalIndex = (int)Math.Round(logicalValue); // 吸附到最近的 K线索引

            // 2. 校验有效性边界
            int logicalLength = board.Read(Viewport.LogicalLength);
            int validCount = ValidDataCountPort != null ? board.Read(ValidDataCountPort) : logicalLength;
            if (validCount <= 0) validCount = logicalLength;

            int offset = board.Read(Viewport.Offset);
            bool isOutOfBounds = globalIndex < offset || globalIndex >= offset + validCount;

            // 3. 吸附防线：是否强制把越界的光标拉回合法数据区
            if (PointerSnapMode == PointerOutOfBoundsStrategy.SnapToValidData)
            {
                globalIndex = Math.Clamp(globalIndex, offset, Math.Max(offset, offset + validCount - 1));
                isOutOfBounds = false;
            }

            // 4. 正向投影：根据吸附后的标准逻辑索引，重新计算出它绝对居中的物理屏幕 X 坐标
            double centerRelativeX = scaleStrategy.DomainScale.Normalize(globalIndex, activeRange);
            double centerX = plotArea.Left + (plotArea.Width * centerRelativeX);
            var hit = new DomainHitResult(globalIndex, centerX, centerRelativeX);

            using (board.AcquireWriteLock())
            {
                board.WriteIfChanged(PointerHitPort, new PointerHitState(
                    new HevoPoint((float)centerX, pos.Y),
                    hit,
                    centerX,
                    globalIndex - offset,
                    isOutOfBounds));
            }
        }

        // ==========================================
        // 💥 引擎级无缝加载 (Infinite Paging) 调度控制组
        // ==========================================

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
                    // 数据变短（如切换股票发生了 Clear），双墙重置，迎接新生命
                    _isLeftWallHit = false;
                    _isRightWallHit = false;
                }
                else
                {
                    // 数据变长（心跳拉了新数据，或左侧拉了历史）
                    // 只要数据长了，说明右侧有可能有新空间了，解除右墙限制！
                    // 但是左墙代表上市首日，如果曾被击中则永远不解，除非切股票。
                    _isRightWallHit = false;
                }
                _lastSeenLength = logicalLength;
            }

            int offsetAmount = (int)Math.Ceiling(activeRange.Span * FetchConfig.PrefetchMultiplier);

            // 2. 纯粹的饥饿判断 (Buffer Threshold Check)
            // 如果向左拖拽即将触底且未曾撞墙
            if (activeRange.Min <= FetchConfig.SafeBuffer && !_isLeftWallHit)
            {
                Debug.WriteLine($"🚀 [Fetch] Hitting LEFT boundary. Requesting data...");
                FireRequestSafe(isLeft: true, anchorIndex: 0, offsetAmount, board);
            }
            // 如果向右拖拽即将越界且未曾撞墙
            else if (activeRange.Max >= logicalLength - FetchConfig.SafeBuffer && !_isRightWallHit)
            {
                Debug.WriteLine($"🚀 [Fetch] Hitting RIGHT boundary. Requesting data...");
                FireRequestSafe(isLeft: false, anchorIndex: logicalLength - 1, offsetAmount, board);
            }
        }

        // 修复 H5：调用方包装。FireRequestAsync 已改为 async Task，调用点位于
        // UpgradeableReadLock 内不能 await，故走 fire-and-forget；ContinueWith 兜底
        // 任何穿透内层 catch 的异常（如线程池继承上下文异常），避免成为 UnobservedTaskException。
        private void FireRequestSafe(bool isLeft, int anchorIndex, int offsetAmount, DataBlackboard board)
        {
            _ = FireRequestAsync(isLeft, anchorIndex, offsetAmount, board)
                .ContinueWith(
                    t => Debug.WriteLine($"🚨 [Fetch] Unobserved exception escaped FireRequestAsync: {t.Exception}"),
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        /// <summary>
        /// 异步防火墙队列：调度拉取数据，提供异常熔断、重复数据识别防呆机制
        /// </summary>
        private async Task FireRequestAsync(bool isLeft, int anchorIndex, int offsetAmount, DataBlackboard board)
        {
            if (_isFetching) return; // 防御并发重入
            _isFetching = true;

            bool isNetworkFault = false, hasMoreData = true;
            TimeSpan delay = FetchConfig.Pacing; // 默认延迟为正常的拖拽节流阀 (200ms)

            // 💥 1. 记下请求前的“物理真理” (用于后续判断业务层是否欺骗了我们)
            int lengthBeforeFetch = 0;
            if (!board.IsDisposed)
            {
                using (board.AcquireReadLock())
                {
                    lengthBeforeFetch = board.Read(Viewport.LogicalLength);
                }
            }

            try
            {
                try
                {
                    hasMoreData = await OnRequireDataAsync!.Invoke(anchorIndex, offsetAmount);
                }
                catch (OperationCanceledException)
                {
                    // 💥 修复“等好久”问题：因为 HttpClient 5秒 超时导致的取消异常，免除额外的熔断惩罚时间！
                    Debug.WriteLine($"⚠️ [Fetch] Timeout occurred. Bypassing additional throttle penalty.");
                    isNetworkFault = true;
                    delay = TimeSpan.Zero;
                }
                catch (Exception ex)
                {
                    // 真正的网络断开/解析异常，施加 3 秒强制冷却，保护后端服务器避免 DDOS 自己
                    Debug.WriteLine($"⚠️ [Fetch] Exception: {ex.Message}");
                    isNetworkFault = true;
                    delay = FetchConfig.NetworkFaultThrottle;
                }

                if (!isNetworkFault && !board.IsDisposed)
                {
                    using (board.AcquireReadLock())
                    {
                        int lengthAfterFetch = board.Read(Viewport.LogicalLength);

                        // 💥 2. 被打脸后重新加回来的终极防线！
                        // 就算外围的业务层信誓旦旦地说有数据 (hasMoreData = true返回了)，
                        // 但如果我发现底层黑板大数组的长度根本没变长，说明拉到的是完全重复的废数据！
                        // 为了防止死循环，由框架强行执行“判死刑”拦截！
                        if (hasMoreData && lengthAfterFetch <= lengthBeforeFetch)
                        {
                            Debug.WriteLine($"🛑 [Fetch] Fake/Duplicate data detected! Length remained at {lengthAfterFetch}. Forcing wall lock.");
                            hasMoreData = false;
                        }

                        // 一旦宣告无数据，立即打上永久封印锁
                        if (!hasMoreData)
                        {
                            Debug.WriteLine($"🛑 [Fetch] Wall reached on {(isLeft ? "LEFT" : "RIGHT")}. Locking.");
                            if (isLeft) _isLeftWallHit = true;
                            else _isRightWallHit = true;
                        }
                    }
                }

                // 执行调度排队 (0, 200ms, 或 3s)
                if (delay > TimeSpan.Zero) await Task.Delay(delay);
            }
            catch (TaskCanceledException) { /* 安全吃掉 Task.Delay 因为框架卸载带来的系统取消异常 */ }
            catch (Exception ex) { Debug.WriteLine($"🚨 [Fetch] Fatal: {ex}"); }
            finally
            {
                // 彻底释放调度锁
                _isFetching = false;

                // 痊愈后自启循环引擎：验证刚才加载的一波数据是否足够填饱肚子
                try
                {
                    if (board != null && !board.IsDisposed)
                    {
                        using (board.AcquireReadLock())
                        {
                            CheckBoundaries(board.Read(Viewport.ActiveRange), board.Read(Viewport.LogicalLength), board);
                        }
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"🔥 [Fetch] CRITICAL ERROR IN RECOVERY LOOP: {ex}"); }
            }
        }

        protected override void OnProject(FeatureContext ctx) { }
    }
}
