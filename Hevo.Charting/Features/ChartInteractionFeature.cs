using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
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
    /// 分页/无缝加载已拆为独立的 <see cref="DataPagingFeature"/>，本 Feature 只关心手势翻译。
    /// </summary>
    public class ChartInteractionFeature : ChartFeature
    {
        // 铁律：交互运算必须在布局之前完成，确保后续的图层拿到的是最新视口
        public override FeaturePhase Phase => FeaturePhase.PreLayout;

        // --- 核心引脚（Viewport 由 ChartFeature 基类自动注入）---
        /// <summary> 暴露给外部层(如 Tooltip, Crosshair) 订阅的光标全局状态 </summary>
        public DataPort<PointerHitState?> PointerHitPort { get; init; } = new("GlobalPointerHit");
        public DataPort<int>? ValidDataCountPort { get; init; }

        /// <summary>
        /// 数据可用性端口：由 DSL 注入，与 <see cref="DataPagingFeature.AvailabilityPort"/> 共用同一实例。
        /// 仅 HandleZoom 在锁内 Read 一次喂给 ZoomContext。未挂载分页 Feature 时为 null。
        /// </summary>
        public DataPort<DataAvailability>? AvailabilityPort { get; init; }

        // --- 行为配置 ---
        public ChartInteractionMode SupportedModes { get; init; } = ChartInteractionMode.All;
        public PointerOutOfBoundsStrategy PointerSnapMode { get; init; } = PointerOutOfBoundsStrategy.Free;
        public IZoomStrategy ZoomStrategy { get; init; } = new SmartAdaptiveZoomStrategy();
        public ZoomOptions ZoomConfig { get; init; } = new();
        public double EdgeHitTolerance { get; init; } = 1.0;

        private const double StandardWheelDelta = 120.0;

        // ==========================================
        // 💥 内部交互状态 (全部降维至 HevoPoint)
        // ==========================================
        private HevoPoint _lastPanPos;
        private bool _isPanning;
        private HevoPoint? _lastMousePos;

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

            // 7. 💥 视口变更后刷新十字光标位置（分页判定已交给 DataPagingFeature 独立 Watch）
            flow.Watch(new object[] { Viewport.ActiveRange }, board =>
            {
                using (board.AcquireUpgradeableReadLock())
                {
                    var activeRange = board.Read(Viewport.ActiveRange);
                    var plotArea = ctx.GetPlotArea();
                    var scaleStrategy = ctx.Shared().Read<ScaleStrategyTrait>();

                    if (!activeRange.IsValid || plotArea.IsEmpty || scaleStrategy == null) return;

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
                System.Diagnostics.Debug.WriteLine($"deltaX：{deltaX}");
                if (deltaX == 0) return;
                
                // 算法：物理位移比例 * 逻辑总跨度 = 逻辑位移量
                // expandedSpan：plotArea 整个宽度对应的 domain 跨度。
                //   CategoryScale.Edge 下 = Span；CategoryScale.Centered 下 = Span + 1（两端各留半格）。
                //   走 Denormalize(1) - Denormalize(0) 是为了对任何 IScale 实现都正确。
                double expandedSpan = scaleStrategy.DomainScale.Denormalize(1.0, baseRange) - scaleStrategy.DomainScale.Denormalize(0.0, baseRange);
                double logicalDelta = (deltaX / plotArea.Width) * expandedSpan;

                // 💥 ISnappableScale 支持：开启 SnapEdges 的 CategoryScale 走 ratcheting 平移。
                //   用 Math.Truncate（向零截断）而非 Round —— Round 会"过度消费"：用户只拖了 0.51 unit，
                //   Round 提交 1 unit 的像素消费，导致 _lastPanPos 跳到用户手指前面，后续 deltaX 变负、
                //   永远 round 到 0，pan 卡死。Truncate 保证 consumedPx ≤ 实际 deltaX，不留余额透支。
                if (scaleStrategy.DomainScale is ISnappableScale)
                {
                    double snappedDelta = Math.Truncate(logicalDelta);
                    if (snappedDelta == 0) return;  // 不足 1 unit，累积到下一次
                    double consumedPx = (snappedDelta / expandedSpan) * plotArea.Width;
                    _lastPanPos = new HevoPoint((float)(_lastPanPos.X + consumedPx), _lastPanPos.Y);
                    logicalDelta = snappedDelta;
                }
                else
                {
                    _lastPanPos = currentPos;
                }

                using (board.AcquireWriteLock())
                    board.WriteIfChanged(Viewport.UserRange, new RealRange(baseRange.Min - logicalDelta, baseRange.Max - logicalDelta));
            }
        }

        /// <summary>
        /// 处理滚轮缩放：构造完整 ZoomContext 后委派给 IZoomStrategy。
        /// 交互层不再 clamp Span —— 该决策由策略持有（用 ZoomMath.ClampSpan）；ViewportManager 仅作越界保险。
        /// </summary>
        private void HandleZoom(RenderContext ctx, DataBlackboard board, HevoPoint pos, int delta)
        {
            using (board.AcquireUpgradeableReadLock())
            {
                var baseRange = board.Read(Viewport.ActiveRange);
                int logicalLength = board.Read(Viewport.LogicalLength);
                var plotArea = ctx.GetPlotArea();
                var scaleStrategy = ctx.Shared().Read<ScaleStrategyTrait>();

                if (!baseRange.IsValid || plotArea.Width <= 0 || logicalLength <= 0) return;

                // 用户意图跨度（不 clamp，由策略决定如何处理）
                double steps = delta / StandardWheelDelta;
                double zoomFactor = Math.Pow(1.0 - ZoomConfig.Sensitivity, steps);
                double rawTargetSpan = baseRange.Span * zoomFactor;

                // 解析视图层 trait → 绝对 SpanLimits
                var limitsTrait = ctx.Shared().Read<ViewportSpanLimitsTrait>();
                double maxIndex = Math.Max(0, logicalLength - 1);
                double minSpan = limitsTrait?.MinSpan ?? 2.0;
                double maxSpan = limitsTrait != null ? maxIndex * limitsTrait.MaxSpanMultiplier : maxIndex * 2.0;
                var limits = new SpanLimits(minSpan, Math.Max(minSpan, maxSpan));

                // 鼠标相对位置：保留越界（不 Clamp 到 [0,1]），让策略判断鼠标是否在 plot 区
                double relativeX = (pos.X - plotArea.Left) / plotArea.Width;

                // 数据可用性：DataPagingFeature 写、本 Feature 读，board 锁兜底
                // AvailabilityPort 为 null 表示业务未启用分页 → 一律视为"未耗尽"
                var availability = AvailabilityPort != null ? board.Read(AvailabilityPort) : default;

                var zoomCtx = new ZoomContext(
                    BaseRange: baseRange,
                    RawTargetSpan: rawTargetSpan,
                    MouseRelativeX: relativeX,
                    HitState: board.Read(PointerHitPort),
                    LogicalLength: logicalLength,
                    Limits: limits,
                    DomainScale: scaleStrategy?.DomainScale,
                    LeftDataExhausted: availability.LeftExhausted,
                    RightDataExhausted: availability.RightExhausted);

                RealRange newRange = ZoomStrategy.Calculate(in zoomCtx);

                // 视口未发生实质变化静默返回
                if (Math.Abs(newRange.Span - baseRange.Span) < 1e-5
                    && Math.Abs(newRange.Min - baseRange.Min) < 1e-5) return;

                // ISnappableScale 量化：所有策略共用的渲染对齐后处理
                if (scaleStrategy?.DomainScale is ISnappableScale snappable)
                {
                    double snappedMin = snappable.Snap(newRange.Min);
                    double snappedMax = snappable.Snap(newRange.Max);
                    if (snappedMax - snappedMin >= 1.0)
                        newRange = new RealRange(snappedMin, snappedMax);
                }

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
                int validCount = ValidDataCountPort != null ? board.Read(ValidDataCountPort) : logicalLength;
                if (validCount <= 0) validCount = logicalLength;

                // 数据世界范围 [0, validCount-1]（删 Slicer 后，世界索引 == 数组下标）
                int minIndex = 0;
                int maxIndex = validCount - 1;

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

            // 数据世界范围 [0, validCount-1]（删 Slicer 后，世界索引 == 数组下标）
            bool isOutOfBounds = globalIndex < 0 || globalIndex >= validCount;

            // 3. 吸附防线：是否强制把越界的光标拉回合法数据区
            if (PointerSnapMode == PointerOutOfBoundsStrategy.SnapToValidData)
            {
                // 💥 同时收进"可见整数索引"范围，保证正向投影回来的 centerX ∈ [plot.Left, plot.Right]，
                //    让竖线与交点圆点和 series 画出来的柱子/点位严格对齐。
                int visibleLeft = (int)Math.Ceiling(scaleStrategy.DomainScale.Denormalize(0.0, activeRange));
                int visibleRight = (int)Math.Floor(scaleStrategy.DomainScale.Denormalize(1.0, activeRange));
                int lo = Math.Max(0, visibleLeft);
                int hi = Math.Min(validCount - 1, visibleRight);
                // 可见范围与数据范围无交集时回退到数据范围（极端缩放场景）
                if (hi < lo) { lo = 0; hi = Math.Max(0, validCount - 1); }
                globalIndex = Math.Clamp(globalIndex, lo, hi);
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
                    globalIndex,    // 删 Slicer 后世界索引 == 数组下标，LocalIndex 无意义，直接用 world
                    isOutOfBounds));
            }
        }

        protected override void OnProject(FeatureContext ctx) { }
    }
}
