using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

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
    /// 框选缩放配置 (Box-zoom / Rubber-band).
    /// 默认 Shift+Drag 触发,与普通 Pan 共存;业务可改 ModifierKeys.None 让框选完全替代 Pan。
    /// </summary>
    public record BoxZoomOptions
    {
        /// <summary>激活修饰键。默认 Shift —— Shift+Drag 进入 box-zoom,无修饰走 Pan。</summary>
        public System.Windows.Input.ModifierKeys ActivationModifier { get; init; } = System.Windows.Input.ModifierKeys.Shift;

        /// <summary>选框最小有效像素。低于此值视为误操作丢弃,不写视口。</summary>
        public double MinPixelSize { get; init; } = 8.0;

        /// <summary>选框边框样式。null = 用默认浅灰虚线。</summary>
        public LineStyle? BorderStyle { get; init; }

        /// <summary>选框填充。null = 用默认半透明蓝。</summary>
        public IHevoBrush? FillBrush { get; init; }
    }

    /// <summary>
    /// 惯性 Pan 配置 (kinetic scrolling).
    /// 抬手时基于最近一段窗口的平均速度启动衰减动画,直至速度低于阈值或被新事件中断。
    /// </summary>
    public record InertiaPanOptions
    {
        /// <summary>每帧速度衰减系数 (∈ (0,1))。0.92 ≈ 半秒衰减到 ~10%。</summary>
        public double Friction { get; init; } = 0.92;

        /// <summary>低于此速度即停止 (像素 / 秒)。</summary>
        public double MinVelocityPxPerSec { get; init; } = 30.0;

        /// <summary>抬手前用于估算初速度的滑动窗口长度 (毫秒)。</summary>
        public double VelocityWindowMs { get; init; } = 60.0;
    }

    /// <summary>
    /// 双击复原配置。
    /// 默认重置目标 = ViewportManager 的首屏视口 (DefaultVisibleCount + Alignment),
    /// 业务可通过 <see cref="CustomReset"/> 注入自己的"原点"。
    /// </summary>
    public record DoubleClickResetOptions
    {
        /// <summary>两次按下的最大时间间隔。</summary>
        public TimeSpan MaxInterval { get; init; } = TimeSpan.FromMilliseconds(400);

        /// <summary>两次按下的最大像素位移 (超出则视为拖拽,不算双击)。</summary>
        public double MaxMovementPx { get; init; } = 6.0;

        /// <summary>自定义重置回调。入参 = LogicalLength,出参 = 复原后的视口。null = 走框架默认。</summary>
        public Func<int, RealRange>? CustomReset { get; init; }
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
    /// <remarks>
    /// 触屏 (WPF Manipulation 通道) 默认无条件挂载;捏合的 scale 段受 <see cref="Zoom"/> flag 控,
    /// 平移段受 <see cref="Pan"/> flag 控,与鼠标行为对称。
    /// </remarks>
    [Flags]
    public enum ChartInteractionMode
    {
        None = 0,
        Hover = 1 << 0,
        Pan = 1 << 1,
        Zoom = 1 << 2,
        Keyboard = 1 << 3,
        /// <summary>框选缩放 (默认 Shift+Drag,可配)。配套 <see cref="BoxZoomOptions"/>。</summary>
        BoxZoom = 1 << 4,
        /// <summary>抬手后惯性平移。叠加于 <see cref="Pan"/> 之上;Pan 关闭则惯性自然失效。配套 <see cref="InertiaPanOptions"/>。</summary>
        InertiaPan = 1 << 5,
        /// <summary>双击复原视口。配套 <see cref="DoubleClickResetOptions"/>。</summary>
        DoubleClickReset = 1 << 6,
        All = Hover | Pan | Zoom | Keyboard | BoxZoom | InertiaPan | DoubleClickReset,
        TimeShareDefault = Hover,
        Default = Hover | Keyboard
    }

    /// <summary>
    /// 💥 图表交互总枢纽大脑
    /// 职责：接管底层 UI 框架事件 -> 降维至 Hevo 坐标系 -> 推演视口数学变化 -> 写入黑板。
    /// 分页/无缝加载已拆为独立的 <see cref="DataPagingFeature"/>，本 Feature 只关心手势翻译。
    /// <para>
    /// <b>典型用法</b>:
    /// <code>
    /// var hitPort = new DataPort&lt;PointerHitState?&gt;("Hit");
    /// interactions.Add(new ChartInteractionFeature
    /// {
    ///     PointerHitPort  = hitPort,
    ///     SupportedModes  = ChartInteractionMode.All,
    ///     PointerSnapMode = PointerOutOfBoundsStrategy.SnapToValidData,
    ///     ZoomStrategy    = new SmartAdaptiveZoomStrategy(),
    /// });
    /// // 把 hitPort 传给 Crosshair / Tooltip / Header 即可全联动
    /// </code>
    /// </para>
    /// <para>
    /// <b>数据流</b>:UI 事件(鼠标/触屏/键盘) → 手势翻译 → 写 <see cref="Viewport.UserRange"/> / <see cref="PointerHitPort"/> → ViewportManager 钳制成 ActiveRange → 全图 OnProject 重算。
    /// </para>
    /// </summary>
    public class ChartInteractionFeature : ChartFeature
    {
        // 铁律：交互运算必须在布局之前完成，确保后续的图层拿到的是最新视口
        public override FeaturePhase Phase => FeaturePhase.PreLayout;

        // --- 核心引脚（Viewport 由 ChartFeature 基类自动注入）---
        /// <summary> 暴露给外部层(如 Tooltip, Crosshair) 订阅的光标全局状态 </summary>
        [PortDirection(PortDirection.Output)]
        public DataPort<PointerHitState?> PointerHitPort { get; init; } = new("GlobalPointerHit");

        /// <summary>有效数据计数端口(可选)。业务侧若部分数据是 NaN 占位,可写入真实可命中根数,影响键盘导航与吸附边界。null 时回退到 LogicalLength。</summary>
        public DataPort<int>? ValidDataCountPort { get; init; }

        /// <summary>
        /// 数据可用性端口：由 DSL 注入，与 <see cref="DataPagingFeature.AvailabilityPort"/> 共用同一实例。
        /// 仅 HandleZoom 在锁内 Read 一次喂给 ZoomContext。未挂载分页 Feature 时为 null。
        /// </summary>
        public DataPort<DataAvailability>? AvailabilityPort { get; init; }

        // --- 行为配置 ---
        /// <summary>启用的交互通道(位运算组合)。默认 All;TimeShareDefault 仅 hover;Default 仅 hover+键盘。</summary>
        public ChartInteractionMode SupportedModes { get; init; } = ChartInteractionMode.All;

        /// <summary>光标越界策略。Free = 越界自由;SnapToValidData = 强制吸附到首/末根有效数据。</summary>
        public PointerOutOfBoundsStrategy PointerSnapMode { get; init; } = PointerOutOfBoundsStrategy.Free;

        /// <summary>滚轮缩放算法实现。SmartAdaptive 默认会根据 hit 锚点 / 撞墙状态动态切策略。</summary>
        public IZoomStrategy ZoomStrategy { get; init; } = new SmartAdaptiveZoomStrategy();

        /// <summary>滚轮缩放参数(灵敏度等)。</summary>
        public ZoomOptions ZoomConfig { get; init; } = new();

        /// <summary>框选缩放参数(激活键 / 最小像素 / 选框样式)。需 SupportedModes 启用 BoxZoom。</summary>
        public BoxZoomOptions BoxZoomConfig { get; init; } = new();

        /// <summary>惯性 Pan 参数(摩擦/阈值/采样窗口)。需 SupportedModes 启用 InertiaPan。</summary>
        public InertiaPanOptions InertiaPanConfig { get; init; } = new();

        /// <summary>双击复原参数(时间间隔 / 像素位移 / 自定义重置回调)。需 SupportedModes 启用 DoubleClickReset。</summary>
        public DoubleClickResetOptions DoubleClickResetConfig { get; init; } = new();

        /// <summary>plotArea 边缘命中容差(像素)。鼠标越界但在容差内仍判定为有效命中,防止贴边光标闪烁。</summary>
        public double EdgeHitTolerance { get; init; } = 1.0;

        /// <summary>
        /// true = hover 通道默认未武装,需要用户在 chart 内单击一次后才会写 PointerHitPort。
        /// 影响所有读 hit 的下游 (Crosshair / Tooltip / 业务自定义)。
        /// 用于"chart 与其他 widget 共栖、不希望鼠标飘过就闪十字光标"的场景。
        /// </summary>
        public bool RequireClickToActivate { get; init; } = false;

        /// <summary>
        /// true = 按下 Esc 立即解除 hover 武装并清空 PointerHitPort,直至下次单击重新武装。
        /// 与 <see cref="RequireClickToActivate"/> 独立可用 (即便默认武装,Esc 也能临时藏 hover)。
        /// </summary>
        public bool DismissOnEscape { get; init; } = false;

        private const double StandardWheelDelta = 120.0;

        // ==========================================
        // 💥 内部交互状态 (全部降维至 HevoPoint)
        // ==========================================

        // Pan 锚点:每次 MouseDown 重置,MouseMove 通过 (currentPos - _lastPanPos) 算出本帧位移。
        // ISnappableScale 模式下还承担"未消费像素余额"的累积器角色。
        private HevoPoint _lastPanPos;
        // Pan 状态机:_isPanning 与 _box.IsActive 互斥,任意一个为 true 时 hover 路径 short-circuit。
        private bool _isPanning;
        // 上一次鼠标物理坐标(DrawingCanvas 系)。视口刷新 / 抬手补投影 / 滚轮缩放都靠这个回放命中。
        private HevoPoint? _lastMousePos;

        // --- 框选缩放(子系统封装,见嵌套类 BoxZoomGesture) ---
        private readonly BoxZoomGesture _box = new();

        // --- 惯性 Pan(子系统封装,见嵌套类 InertiaPanRunner) ---
        private readonly InertiaPanRunner _inertia = new();

        // --- 双击复原状态 ---
        private long _lastClickTimeMs;
        private HevoPoint _lastClickPos;

        // --- Hover 武装状态 ---
        // 单一字段统辖 RequireClickToActivate / DismissOnEscape 两个配置:
        //   RequireClickToActivate = true  → OnCompose 时 _isHoverArmed = false (等待单击武装)
        //   RequireClickToActivate = false → OnCompose 时 _isHoverArmed = true  (默认武装,跟旧行为一致)
        //   任意 MouseDown                 → _isHoverArmed = true
        //   Esc (若 DismissOnEscape)       → _isHoverArmed = false
        // Hover 路径写 PointerHitPort 前必须 _isHoverArmed == true。
        private bool _isHoverArmed = true;

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            // hover 武装初值:RequireClickToActivate 为 true 时进入"未武装,等单击"状态
            _isHoverArmed = !RequireClickToActivate;

            // 框选缩放图层 (Interaction 层级,与 CrosshairLayer 共栖)
            AttachLayer(_box.Layer);

            // 1. 鼠标按下:分流 双击复原 / 框选 / 平移
            WithBoard(this.OnMouse(UIElement.MouseDownEvent)).Subscribe(c =>
            {
                chart.Focusable = true; chart.Focus();

                var wpfPos = c.Event.GetPosition(chart);
                var chartPos = new HevoPoint((float)wpfPos.X, (float)wpfPos.Y);

                // 任意按下都立刻打断惯性
                _inertia.Stop();

                // 任意按下都武装 hover (单击 / 拖拽 / 框选 / 双击 起手都从这里走,统一)
                _isHoverArmed = true;

                // ① 双击复原检测 (在 pan / box 路径之前判定,避免被吞)
                long now = System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency;
                bool isDouble = SupportedModes.HasFlag(ChartInteractionMode.DoubleClickReset)
                                && (now - _lastClickTimeMs) <= DoubleClickResetConfig.MaxInterval.TotalMilliseconds
                                && Math.Abs(chartPos.X - _lastClickPos.X) <= DoubleClickResetConfig.MaxMovementPx
                                && Math.Abs(chartPos.Y - _lastClickPos.Y) <= DoubleClickResetConfig.MaxMovementPx;
                _lastClickTimeMs = now;
                _lastClickPos = chartPos;
                if (isDouble)
                {
                    HandleDoubleClickReset(ctx, c.Board);
                    _lastClickTimeMs = 0; // 防止三连击退化成第二个双击
                    return;
                }

                // ② 框选缩放分流:Modifier=None 任意按下进入,否则修饰键命中才进入
                bool wantBoxZoom = SupportedModes.HasFlag(ChartInteractionMode.BoxZoom)
                    && (BoxZoomConfig.ActivationModifier == ModifierKeys.None
                        || (Keyboard.Modifiers & BoxZoomConfig.ActivationModifier) == BoxZoomConfig.ActivationModifier);
                if (wantBoxZoom)
                {
                    var dcPos = c.Event.GetPosition(chart.DrawingCanvas);
                    _box.Begin(new HevoPoint((float)dcPos.X, (float)dcPos.Y));
                    using (c.Board.AcquireWriteLock())
                    {
                        c.Board.WriteIfChanged(PointerHitPort, null);
                        c.Board.WriteIfChanged(_box.StatePort, _box.MakeActiveTrait(BoxZoomConfig));
                    }
                    chart.CaptureMouse();
                    return;
                }

                // ③ 普通平移
                if (!SupportedModes.HasFlag(ChartInteractionMode.Pan)) return;

                _isPanning = true;
                _lastPanPos = chartPos;
                _inertia.ResetSamples();

                using (c.Board.AcquireWriteLock()) c.Board.WriteIfChanged(PointerHitPort, null);
                chart.CaptureMouse();
            }).OwnedBy(this);

            // 2. 鼠标抬起:结算 框选 / 触发惯性
            WithBoard(this.OnMouse(UIElement.MouseUpEvent)).Subscribe(c =>
            {
                if (_box.IsActive)
                {
                    _box.End();
                    chart.ReleaseMouseCapture();
                    FinalizeBoxZoom(ctx, c.Board);
                    return;
                }
                if (_isPanning)
                {
                    _isPanning = false;
                    chart.ReleaseMouseCapture();
                    if (SupportedModes.HasFlag(ChartInteractionMode.InertiaPan))
                    {
                        var board = c.Board;
                        _inertia.Start(InertiaPanConfig,
                            applyPxDelta: dx => ApplyPanDelta(ctx, board, dx),
                            readActiveRange: () =>
                            {
                                using (board.AcquireUpgradeableReadLock()) return board.Read(Viewport.ActiveRange);
                            });
                    }

                    // 单击-武装-松手三连不挪鼠标的 UX 兜底:
                    // MouseDown 路径会清 PointerHitPort 准备 Pan,如果用户其实没拖,松手后又不动鼠标,
                    // 没人写新的 hit → 永远看不到光标。这里若 hover 已武装且无惯性接管,立刻在当前位置投影一次。
                    if (!_inertia.IsRunning && _lastMousePos.HasValue && _isHoverArmed
                        && SupportedModes.HasFlag(ChartInteractionMode.Hover))
                    {
                        using (c.Board.AcquireUpgradeableReadLock())
                        {
                            var activeRange = c.Board.Read(Viewport.ActiveRange);
                            var scaleStrategy = ctx.Shared().Read<ScaleStrategyTrait>();
                            if (activeRange.IsValid && scaleStrategy != null && !ctx.GetPlotArea().IsEmpty)
                                UpdatePointerStateFromMouse(ctx.GetPlotArea(), scaleStrategy, c.Board, activeRange, _lastMousePos.Value);
                        }
                    }
                }
            }).OwnedBy(this);

            // 3. 鼠标离开:清空光标缓存
            WithBoard(this.OnMouse(UIElement.MouseLeaveEvent)).Subscribe(c =>
            {
                _lastMousePos = null;
                if (!_isPanning && !_box.IsActive) using (c.Board.AcquireWriteLock()) c.Board.WriteIfChanged(PointerHitPort, null);
            }).OwnedBy(this);

            // 4. 鼠标移动:框选刷新 / 平移采样 / Hover
            WithBoard(this.OnMouse(UIElement.MouseMoveEvent)).Subscribe(c =>
            {
                var dcPos = c.Event.GetPosition(chart.DrawingCanvas);
                _lastMousePos = new HevoPoint((float)dcPos.X, (float)dcPos.Y);

                if (_box.IsActive)
                {
                    _box.UpdateCurrent(_lastMousePos.Value);
                    using (c.Board.AcquireWriteLock()) c.Board.WriteIfChanged(_box.StatePort, _box.MakeActiveTrait(BoxZoomConfig));
                    return;
                }

                if (_isPanning)
                {
                    var wpfPanPos = c.Event.GetPosition(chart);
                    HandlePan(ctx, c.Board, new HevoPoint((float)wpfPanPos.X, (float)wpfPanPos.Y));
                    return;
                }

                if (SupportedModes.HasFlag(ChartInteractionMode.Hover) && _isHoverArmed)
                {
                    using (c.Board.AcquireUpgradeableReadLock())
                    {
                        var activeRange = c.Board.Read(Viewport.ActiveRange);
                        var scaleStrategy = ctx.Shared().Read<ScaleStrategyTrait>();
                        if (activeRange.IsValid && scaleStrategy != null && !ctx.GetPlotArea().IsEmpty)
                            UpdatePointerStateFromMouse(ctx.GetPlotArea(), scaleStrategy, c.Board, activeRange, _lastMousePos.Value);
                    }
                }
            }).OwnedBy(this);

            // 5. 鼠标滚轮:Zoom
            WithBoard(this.OnMouse(UIElement.MouseWheelEvent)).Subscribe(c =>
            {
                if (c.Event is not MouseWheelEventArgs wheelEvent || !SupportedModes.HasFlag(ChartInteractionMode.Zoom)) return;
                _inertia.Stop();

                var wpfPos = wheelEvent.GetPosition(chart.DrawingCanvas);
                _lastMousePos = new HevoPoint((float)wpfPos.X, (float)wpfPos.Y);

                HandleZoom(ctx, c.Board, _lastMousePos.Value, wheelEvent.Delta);
            }).OwnedBy(this);

            // 6. 键盘:Esc 解除 hover 武装(独立于 Keyboard mode);方向键导航(受 Keyboard mode 控)
            WithBoard(this.OnKey(UIElement.PreviewKeyDownEvent)).Subscribe(c =>
            {
                if (c.Event is not KeyEventArgs keyEvent) return;

                // Esc 处理优先,且不受 Keyboard mode 影响 —— 业务可能关 Keyboard 但仍想 Esc 能藏 hover。
                if (keyEvent.Key == Key.Escape && DismissOnEscape)
                {
                    _isHoverArmed = false;
                    using (c.Board.AcquireWriteLock()) c.Board.WriteIfChanged(PointerHitPort, null);
                    keyEvent.Handled = true;
                    return;
                }

                if (!SupportedModes.HasFlag(ChartInteractionMode.Keyboard)) return;
                _inertia.Stop();

                if (keyEvent.Key == Key.Left) { HandleKeyboardNavigation(ctx, c.Board, -1); keyEvent.Handled = true; }
                else if (keyEvent.Key == Key.Right) { HandleKeyboardNavigation(ctx, c.Board, 1); keyEvent.Handled = true; }
            }).OwnedBy(this);

            // 7. 触屏 Manipulation:Starting 配置容器 + 模式
            WithBoard(this.OnUIEvent<ManipulationStartingEventArgs>(UIElement.ManipulationStartingEvent)).Subscribe(c =>
            {
                // 只在需要时配置:任何 Pan 或 Zoom 模式启用时挂上;否则保持 None 让 WPF 走 mouse promote
                bool wantPan = SupportedModes.HasFlag(ChartInteractionMode.Pan);
                bool wantZoom = SupportedModes.HasFlag(ChartInteractionMode.Zoom);
                if (!wantPan && !wantZoom) { c.Event.Mode = ManipulationModes.None; return; }

                c.Event.ManipulationContainer = chart.DrawingCanvas;
                var mode = ManipulationModes.None;
                if (wantPan) mode |= ManipulationModes.TranslateX;
                if (wantZoom) mode |= ManipulationModes.Scale;
                c.Event.Mode = mode;
            }).OwnedBy(this);

            // 8. 触屏 Manipulation:Delta 翻译为 ApplyPan/ApplyZoom
            WithBoard(this.OnUIEvent<ManipulationDeltaEventArgs>(UIElement.ManipulationDeltaEvent)).Subscribe(c =>
            {
                _inertia.Stop();
                using (c.Board.AcquireWriteLock()) c.Board.WriteIfChanged(PointerHitPort, null);

                var delta = c.Event.DeltaManipulation;
                var origin = c.Event.ManipulationOrigin; // DrawingCanvas 坐标 (Container)

                double tx = delta.Translation.X;
                if (tx != 0 && SupportedModes.HasFlag(ChartInteractionMode.Pan))
                    ApplyPanDelta(ctx, c.Board, tx);

                double sx = delta.Scale.X;
                if (Math.Abs(sx - 1.0) > MathTolerance.ManipulationScale && SupportedModes.HasFlag(ChartInteractionMode.Zoom))
                    ApplyZoom(ctx, c.Board, 1.0 / sx, (float)origin.X);
            }).OwnedBy(this);

            // 9. 视口变更后刷新十字光标位置(分页判定已交给 DataPagingFeature 独立 Watch)
            flow.Watch(new object[] { Viewport.ActiveRange }, board =>
            {
                using (board.AcquireUpgradeableReadLock())
                {
                    var activeRange = board.Read(Viewport.ActiveRange);
                    var plotArea = ctx.GetPlotArea();
                    var scaleStrategy = ctx.Shared().Read<ScaleStrategyTrait>();

                    if (!activeRange.IsValid || plotArea.IsEmpty || scaleStrategy == null) return;

                    if (_lastMousePos.HasValue && !_isPanning && !_box.IsActive && _isHoverArmed)
                        UpdatePointerStateFromMouse(plotArea, scaleStrategy, board, activeRange, _lastMousePos.Value);
                }
            });
        }

        /// <summary>
        /// 处理图表拖拽:将物理像素的位移反向推演为逻辑视口的平移。
        /// 内核走 <see cref="ApplyPanDelta"/>;此方法只负责管 _lastPanPos 与 ISnappableScale 的像素 ratchet。
        /// </summary>
        private void HandlePan(RenderContext ctx, DataBlackboard board, HevoPoint currentPos)
        {
            double deltaX = currentPos.X - _lastPanPos.X;
            if (deltaX == 0) return;

            // ISnappableScale 模式下 ApplyPanDelta 会返回真实"消费"的像素量(可能少于 deltaX);
            // 这里据此推进 _lastPanPos 残留余额到下一帧累积,防止小幅拖动卡死。
            double consumedPx = ApplyPanDelta(ctx, board, deltaX);
            if (consumedPx == 0) return; // 不足 1 unit,余额留给下一帧

            // 速度采样: chart 像素位移 + 时间戳(毫秒)
            _inertia.RecordSample(consumedPx);

            _lastPanPos = new HevoPoint((float)(_lastPanPos.X + consumedPx), _lastPanPos.Y);
        }

        /// <summary>
        /// 通用平移内核:把物理像素位移换算成 logical delta 并写 UserRange,返回真实消费的像素量。
        /// 鼠标 / Manipulation / 惯性帧 共用同一份算法。
        /// 返回值:对 ISnappableScale 表示成功消费的像素;非 snap 场景永远等于入参 deltaPx。
        /// </summary>
        private double ApplyPanDelta(RenderContext ctx, DataBlackboard board, double deltaPx)
        {
            if (deltaPx == 0) return 0;
            using (board.AcquireUpgradeableReadLock())
            {
                var plotArea = ctx.GetPlotArea();
                var scaleStrategy = ctx.Shared().Read<ScaleStrategyTrait>();
                var baseRange = board.Read(Viewport.ActiveRange);

                if (plotArea.IsEmpty || scaleStrategy == null || !baseRange.IsValid || baseRange.Span <= 0) return 0;

                // expandedSpan: plot 整个宽度对应的 domain 跨度
                //   Edge scale = Span; Centered scale = Span + 1。
                //   Denormalize(1)-Denormalize(0) 通用兜底,对任何 IScale 实现都正确。
                double expandedSpan = scaleStrategy.DomainScale.Denormalize(1.0, baseRange) - scaleStrategy.DomainScale.Denormalize(0.0, baseRange);
                double logicalDelta = (deltaPx / plotArea.Width) * expandedSpan;
                double consumedPx = deltaPx;

                // ISnappableScale: 整数索引 ratcheting,Math.Truncate 防止"过度消费"
                if (scaleStrategy.DomainScale is ISnappableScale snappable && snappable.SnapEnabled)
                {
                    double snappedDelta = Math.Truncate(logicalDelta);
                    if (snappedDelta == 0) return 0;
                    consumedPx = (snappedDelta / expandedSpan) * plotArea.Width;
                    logicalDelta = snappedDelta;
                }

                using (board.AcquireWriteLock())
                    board.WriteIfChanged(Viewport.UserRange, new RealRange(baseRange.Min - logicalDelta, baseRange.Max - logicalDelta));

                return consumedPx;
            }
        }

        /// <summary>
        /// 处理滚轮缩放:换算 zoomFactor 后委派给 <see cref="ApplyZoom"/>。
        /// </summary>
        private void HandleZoom(RenderContext ctx, DataBlackboard board, HevoPoint pos, int delta)
        {
            double steps = delta / StandardWheelDelta;
            double zoomFactor = Math.Pow(1.0 - ZoomConfig.Sensitivity, steps);
            ApplyZoom(ctx, board, zoomFactor, pos.X);
        }

        /// <summary>
        /// 通用缩放内核:鼠标滚轮 / 双指捏合 共用。
        /// 构造 ZoomContext → IZoomStrategy.Calculate → ISnappableScale snap → 写 UserRange。
        /// </summary>
        /// <param name="zoomFactor">目标 Span 相对当前 Span 的比例(>1 缩小、&lt;1 放大)</param>
        /// <param name="anchorPxX">缩放锚点的物理 X 坐标(DrawingCanvas 系)</param>
        private void ApplyZoom(RenderContext ctx, DataBlackboard board, double zoomFactor, double anchorPxX)
        {
            if (zoomFactor <= 0 || double.IsNaN(zoomFactor) || double.IsInfinity(zoomFactor)) return;
            using (board.AcquireUpgradeableReadLock())
            {
                var baseRange = board.Read(Viewport.ActiveRange);
                int logicalLength = board.Read(Viewport.LogicalLength);
                var plotArea = ctx.GetPlotArea();
                var scaleStrategy = ctx.Shared().Read<ScaleStrategyTrait>();

                if (!baseRange.IsValid || plotArea.Width <= 0 || logicalLength <= 0) return;

                double rawTargetSpan = baseRange.Span * zoomFactor;

                // 解析视图层 trait → 绝对 SpanLimits
                var limitsTrait = ctx.Shared().Read<ViewportSpanLimitsTrait>();
                double maxIndex = Math.Max(0, logicalLength - 1);
                double minSpan = limitsTrait?.MinSpan ?? 2.0;
                double maxSpan = limitsTrait != null ? maxIndex * limitsTrait.MaxSpanMultiplier : maxIndex * 2.0;
                var limits = new SpanLimits(minSpan, Math.Max(minSpan, maxSpan));

                // 锚点相对位置:保留越界(不 Clamp 到 [0,1]),让策略判断锚点是否在 plot 区。
                // 未武装的 hover 状态(requireClickToActivate=true 未点击 / Esc 解除武装)下鼠标位置不可信,
                // 强制写成越界值 -1,让 SmartAdaptive / CrosshairAnchor / Directional 走各自的 out-of-plot 分支
                // (默认右缘锚定),严守"未武装前不采信鼠标"的契约。
                double relativeX = _isHoverArmed
                    ? (anchorPxX - plotArea.Left) / plotArea.Width
                    : -1.0;

                // 数据可用性(分页墙状态):未启用分页时一律视为未耗尽
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

                // 视口与上一帧无变化就退帧,省一次完整 ProjectAll。判等用 NumericEqual,跟撞墙判定同口径。
                if (Math.Abs(newRange.Span - baseRange.Span) < MathTolerance.NumericEqual
                    && Math.Abs(newRange.Min - baseRange.Min) < MathTolerance.NumericEqual) return;

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

        // ==========================================
        // 框选缩放子系统:状态 + 图层 + 选框 trait + 反投影几何全部封进去。
        // Feature 只在事件路径调 Begin/UpdateCurrent/End,在 OnProject 透传 trait,
        // 在 MouseUp 调 ProjectToDomainRange 拿到目标 range 后写 UserRange。
        // ==========================================
        private sealed class BoxZoomGesture
        {
            public BoxZoomLayer Layer { get; } = new();
            public DataPort<BoxZoomTrait> StatePort { get; } = new("BoxZoomState");
            public bool IsActive { get; private set; }

            private HevoPoint _start;
            private HevoPoint _current;

            public void Begin(HevoPoint start) { IsActive = true; _start = start; _current = start; }
            public void UpdateCurrent(HevoPoint current) { _current = current; }
            public void End() { IsActive = false; }

            public BoxZoomTrait MakeActiveTrait(BoxZoomOptions config)
                => new(true, ComputeRect(), config.BorderStyle, config.FillBrush);

            /// <summary>
            /// 反投影选框 X 范围到 domain。返回 null = 选框过小或与 plot 区无交集(误操作)。
            /// 调用方负责后续 ISnappableScale.Snap 与 UserRange 写入。
            /// </summary>
            public RealRange? ProjectToDomainRange(BoxZoomOptions config, HevoRect plotArea, IScale scale, RealRange baseRange)
            {
                float left = Math.Min(_start.X, _current.X);
                float right = Math.Max(_start.X, _current.X);
                if (right - left < config.MinPixelSize) return null;

                double clampedLeft = Math.Max(left, plotArea.Left);
                double clampedRight = Math.Min(right, plotArea.Right);
                if (clampedRight - clampedLeft < config.MinPixelSize) return null;

                double normLeft = (clampedLeft - plotArea.Left) / plotArea.Width;
                double normRight = (clampedRight - plotArea.Left) / plotArea.Width;
                double newMin = scale.Denormalize(normLeft, baseRange);
                double newMax = scale.Denormalize(normRight, baseRange);
                return newMax > newMin ? new RealRange(newMin, newMax) : (RealRange?)null;
            }

            private HevoRect ComputeRect()
            {
                float left = Math.Min(_start.X, _current.X);
                float right = Math.Max(_start.X, _current.X);
                float top = Math.Min(_start.Y, _current.Y);
                float bottom = Math.Max(_start.Y, _current.Y);
                return new HevoRect(left, top, right - left, bottom - top);
            }
        }

        private void FinalizeBoxZoom(RenderContext ctx, DataBlackboard board)
        {
            // 不管下面成不成,选框矩形必须立刻清掉
            using (board.AcquireWriteLock()) board.WriteIfChanged(_box.StatePort, BoxZoomTrait.Inactive);

            using (board.AcquireUpgradeableReadLock())
            {
                var plotArea = ctx.GetPlotArea();
                var scaleStrategy = ctx.Shared().Read<ScaleStrategyTrait>();
                var baseRange = board.Read(Viewport.ActiveRange);
                if (plotArea.IsEmpty || scaleStrategy == null || !baseRange.IsValid) return;

                var projected = _box.ProjectToDomainRange(BoxZoomConfig, plotArea, scaleStrategy.DomainScale, baseRange);
                if (projected is not RealRange newRange) return;

                if (scaleStrategy.DomainScale is ISnappableScale snappable)
                {
                    double sMin = snappable.Snap(newRange.Min);
                    double sMax = snappable.Snap(newRange.Max);
                    if (sMax - sMin >= 1.0) newRange = new RealRange(sMin, sMax);
                }

                using (board.AcquireWriteLock()) board.WriteIfChanged(Viewport.UserRange, newRange);
            }
        }

        // ==========================================
        // 双击复原
        // ==========================================
        private void HandleDoubleClickReset(RenderContext ctx, DataBlackboard board)
        {
            _inertia.Stop();
            using (board.AcquireUpgradeableReadLock())
            {
                int logicalLength = board.Read(Viewport.LogicalLength);
                if (logicalLength <= 0) return;

                RealRange resetRange;
                if (DoubleClickResetConfig.CustomReset != null)
                {
                    resetRange = DoubleClickResetConfig.CustomReset(logicalLength);
                }
                else
                {
                    // 走 ViewportManager publish 的 InitialViewportTrait,与首屏视口完全一致
                    var initial = ctx.Shared().Read<InitialViewportTrait>();
                    double maxIndex = Math.Max(0, logicalLength - 1);
                    double initialSpan = initial?.DefaultVisibleCount.HasValue == true
                        ? Math.Min(initial.DefaultVisibleCount.Value - 1, maxIndex)
                        : maxIndex;
                    initialSpan = Math.Max(0, initialSpan);

                    var alignment = initial?.Alignment ?? ViewportAlignment.RightEdge;
                    resetRange = alignment == ViewportAlignment.RightEdge
                        ? new RealRange(maxIndex - initialSpan, maxIndex)
                        : new RealRange(0, initialSpan);
                }

                if (!resetRange.IsValid) return;
                using (board.AcquireWriteLock()) board.WriteIfChanged(Viewport.UserRange, resetRange);
            }
        }

        // ==========================================
        // 惯性 Pan 子系统:速度环形采样 + CompositionTarget.Rendering 衰减帧 + 撞墙自停。
        // 完全自洽 —— 通过两个回调(applyPxDelta / readActiveRange)与外层通信,
        // 不暴露任何状态字段。Feature 只持有一个 `_inertia` 引用即可调度全部能力。
        // ==========================================
        private sealed class InertiaPanRunner
        {
            // 速度采样环形缓冲容量。16 帧 ≈ 抬手前 ~270ms (60fps),足够估出稳定的初速度,再大也用不到。
            private const int RingSize = 16;
            // 单帧 dt 上限(秒)。GC 暂停 / 系统休眠回来时帧间隔可能突然变成几百 ms,
            // 这种异常帧若用真实 dt 推算,会直接把 series 弹飞数百 px。直接跳过本帧,保留 _vPxPerSec 给下一帧。
            private const double MaxFrameDeltaSec = 0.1;
            // 摩擦系数的标定基准帧率。Friction=0.92 是按 60fps 校准的"半秒衰减到 ~10%",
            // 实际 fps 可能是 75/144Hz,此处通过 Math.Pow(friction, dt * FpsBaseline) 把衰减归一到时间维度,
            // 保证不同显示器刷新率下的惯性手感一致。
            private const double FpsBaseline = 60.0;

            private readonly (long Tms, double Dx)[] _ring = new (long, double)[RingSize];
            private int _head;
            private int _count;

            private bool _running;
            private double _vPxPerSec;
            private long _lastTickMs;
            private double _friction;
            private double _minV;
            private double _windowMs;
            private Action<double>? _applyDelta;
            private Func<RealRange>? _readActive;
            private EventHandler? _tickHandler;

            public bool IsRunning => _running;

            public void ResetSamples() { _head = 0; _count = 0; }

            public void RecordSample(double dxPx)
            {
                _ring[_head] = (NowMs(), dxPx);
                _head = (_head + 1) % RingSize;
                if (_count < RingSize) _count++;
            }

            /// <summary>抬手时启动衰减循环;速度低于阈值则不启动。可重复调 Start —— 内部先 Stop 旧循环。</summary>
            public void Start(InertiaPanOptions config, Action<double> applyPxDelta, Func<RealRange> readActiveRange)
            {
                Stop();
                _windowMs = config.VelocityWindowMs;
                double v = EstimateVelocity();
                if (Math.Abs(v) < config.MinVelocityPxPerSec) return;

                _running = true;
                _vPxPerSec = v;
                _friction = config.Friction;
                _minV = config.MinVelocityPxPerSec;
                _applyDelta = applyPxDelta;
                _readActive = readActiveRange;
                _lastTickMs = NowMs();
                _tickHandler = (_, _) => Tick();
                CompositionTarget.Rendering += _tickHandler;
            }

            public void Stop()
            {
                if (!_running) return;
                _running = false;
                if (_tickHandler != null) CompositionTarget.Rendering -= _tickHandler;
                _tickHandler = null;
                _applyDelta = null;
                _readActive = null;
            }

            private double EstimateVelocity()
            {
                if (_count == 0) return 0;
                long now = NowMs();
                double sumDx = 0;
                long oldest = now;
                int idx = (_head - 1 + RingSize) % RingSize;
                for (int i = 0; i < _count; i++)
                {
                    var (t, dx) = _ring[idx];
                    if (now - t > _windowMs) break;
                    sumDx += dx;
                    oldest = t;
                    idx = (idx - 1 + RingSize) % RingSize;
                }
                long spanMs = now - oldest;
                return spanMs > 0 ? sumDx * 1000.0 / spanMs : 0;
            }

            private void Tick()
            {
                if (!_running || _applyDelta == null || _readActive == null) { Stop(); return; }

                long now = NowMs();
                double dt = (now - _lastTickMs) / 1000.0;
                _lastTickMs = now;
                if (dt <= 0 || dt > MaxFrameDeltaSec) return; // 异常帧跳过,保留 velocity

                // 撞墙检测: 比对 ActiveRange 是否真动了(被 ViewportManager Hard 钳回则视为撞墙)
                var before = _readActive();
                _applyDelta(_vPxPerSec * dt);
                var after = _readActive();
                if (after.IsValid && before.IsValid
                    && Math.Abs(after.Min - before.Min) < MathTolerance.NumericEqual
                    && Math.Abs(after.Max - before.Max) < MathTolerance.NumericEqual)
                {
                    Stop(); return;
                }

                // 按 FpsBaseline (60Hz) 等效衰减:跨显示器刷新率保持一致手感
                _vPxPerSec *= Math.Pow(_friction, dt * FpsBaseline);
                if (Math.Abs(_vPxPerSec) < _minV) Stop();
            }

            private static long NowMs() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency;
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

        protected override void OnProject(FeatureContext ctx)
        {
            // 框选 trait 透传:_box.StatePort 由 mouse handler 写入,本方法只搬运到图层。
            // 端口未写过(默认 null)时退回 Inactive,避免 layer 看到 null trait。
            var (state, _) = ctx.UsePort(_box.StatePort);
            ctx.For(_box.Layer).PublishData(state ?? BoxZoomTrait.Inactive);
        }
    }
}
