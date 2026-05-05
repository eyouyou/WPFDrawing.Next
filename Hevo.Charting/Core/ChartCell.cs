using Hevo.Charting.Abstractions;
using Hevo.Charting.DevTools;
using Hevo.Charting.Renderers;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 模板类型(限定只能用于 ChartCell)。
    /// 通常你不直接 new 这个类,而是 new <see cref="ChartSchema"/> 派生类(如 ReactiveSchema)。
    /// 类型守卫由 <see cref="ChartCell.CoerceTemplateValue"/> + <see cref="ChartCell.OnTemplateChanged"/> 在 Template 赋值时强制。
    /// </summary>
    public class ChartTemplate : ControlTemplate
    {
        /// <summary>调试用模板名(无业务语义,便于日志区分多个 schema 实例)。</summary>
        public string TemplateName { get; set; } = string.Empty;

        public ChartTemplate()
        {
            TargetType = typeof(ChartCell);
        }
    }

    /// <summary>
    /// 静态层(_drawingCanvas)BitmapCache 策略。
    /// 详见 <see cref="ChartCell.StaticCachePolicy"/>。
    /// </summary>
    public enum StaticCachePolicy
    {
        /// <summary>始终启用缓存。</summary>
        On,
        /// <summary>始终关闭缓存。</summary>
        Off,
        /// <summary>跟随 <see cref="ChartCell.ReportVisibleDataCount"/> 自适应,基于双阈值滞回。</summary>
        Adaptive
    }

    public enum ChartLayerType
    {
        /// <summary>
        /// 图表的背景色、渐变背景、或者可能存在的背景水印
        /// </summary>
        Background = 0,
        /// <summary>
        /// 折线/曲线（MainVisual）、柱状图、K线实体，以及底部的网格线（Grid Lines）
        /// </summary>
        Main = 10,          // 
        /// <summary>
        /// 选中状态、高亮
        /// </summary>
        Selection = 20,     // 
        /// <summary>
        /// 十字准星（Crosshair）、橡皮框（Rubber Band / Zoom Box）、鼠标悬停时的临时标记点。
        /// </summary>
        Interaction = 30,   // 
        /// <summary>
        /// 悬浮提示框（Tooltip/Tip）、标签、图例、或者悬浮在图表上方的操作按钮
        /// </summary>
        Overlay = 40        // 
    }

    /// <summary>
    /// Chart 最小可部署单元。
    /// 职责：
    /// 1. 物理容器：管理 DrawingCanvas(静态底层), OverlayCanvas(交互独立 host), InteractionCanvas(Widget 顶层)。
    /// 2. 层级管理：确保 Static → Overlay → Interaction 的物理 Z 顺序。
    /// 3. 生命周期：作为 ChartSession 的挂载点。
    ///
    /// LayerBuffer 三类子缓冲的路由(不变量):
    /// - Drawing(矢量指令):全部 Software,统一走 RenderWpfLayers → WpfRenderProvider。
    /// - Widget(WPF 控件):所有 Layer 通过 RenderWidgetLayers 进入 InteractionCanvas 控件池。
    ///
    /// 注:RenderMode.Hardware 枚举值在 API 层保留以避免破坏下游枚举调用,但当前所有 Hardware
    /// Layer 都被路由到 Software 路径(Skia 已从仓库移除,详见 TODO.md)。
    /// </summary>
    public class ChartCell : ContentControl
    {
        // ==========================================
        // 1. 物理结构
        // ==========================================

        // 亚像素震荡拦截阈值。WPF 容器在某些 DPI / 父布局下尺寸会以 0.1~0.4 像素的幅度反复抖动,
        // 0.5 是"半个物理像素"的物理意义边界 — 小于此值的变化在屏幕上肉眼不可见,
        // 但触发完整重排会让 chart 进入持续 20Hz 的伪 SizeChanged 风暴,白白烧 CPU。
        private const double SubPixelResizeThreshold = 0.5;

        private readonly Grid _rootContainer = new();

        // [中层] WPF 矢量层 (DrawingVisuals)
        private readonly ChartDrawingCanvas _drawingCanvas = new() { };

        // [中-高层] WPF 矢量交互层(独立 UIElement,跟 _drawingCanvas 物理隔离)。
        // CrosshairLayer 这类高频改动 + 满屏 dirty bbox 的 Software 层落到这里,
        // 避免它每次 dirty 时连带 _drawingCanvas 的 Grid / Axis 一起 tile-replay。
        private readonly ChartDrawingCanvas _overlayCanvas = new() { };

        // [顶层] 交互与控件层 (懒加载)
        private InteractionCanvas? _interactionCanvas;

        // ==========================================
        // 2. 注册表
        // ==========================================

        // 软件层 (WPF Retained Mode) -> 对应 _drawingCanvas (低频 / 静态: Grid / Axis / PlotAreaDecor)
        private readonly List<(IChartLayer Layer, ChartLayerType Type)> _visualRegistry = new();

        // 软件层(交互独立 host)-> 对应 _overlayCanvas
        // 路由规则: Level == Interaction (典型: CrosshairLayer / TooltipWidgetLayer)
        private readonly List<(IChartLayer Layer, ChartLayerType Type)> _overlayVisualRegistry = new();

        // 修复 H4-c：跨帧复用的脏图层缓冲。每帧 Clear 后填充，替代
        // `ActiveLayers.OfType<ChartLayer>().Where(l => l.IsDirty).ToList()` 的 LINQ 迭代器 + List 分配。
        // ExecutePipeline 仅在 UI 线程 OnCompositionTargetRendering 内调用，单线程复用安全。
        private readonly List<ChartLayer> _dirtyLayerBuffer = new();

        // Widget 宿主池: Layer -> 物理控件包装器
        private readonly Dictionary<IChartLayer, LayerWidgetPool> _widgetRegistry = new();

        // 跨帧持久化数据
        private readonly VisualDataBag _sharedData = new();
        private readonly ConditionalWeakTable<IChartLayer, VisualDataBag> _localData = new();


        // RendererProvider — 注入 per-cell diagnostics 收集器
        private readonly RenderDiagnostics _diagnostics = new();
        private readonly WpfRenderProvider _wpfProvider;

        /// <summary>
        /// 获取本 cell 渲染管线的诊断快照。所有计数 UI 线程累加,无锁。
        /// 业务侧典型用法:DispatcherTimer 每秒拉一次 → 绑定到 overlay TextBlock。
        /// </summary>
        public DiagnosticsSnapshot GetDiagnostics() => _diagnostics.Snapshot();

        internal VisualDataBag GetSharedData() => _sharedData;
        internal VisualDataBag GetLocalBag(IChartLayer layer)
        {
            return _localData.GetValue(layer, _ => new VisualDataBag());
        }

        // ==========================================
        // 静态层 BitmapCache 策略 (业务可配置)
        // ==========================================

        /// <summary>
        /// 静态层(_drawingCanvas) BitmapCache 策略。
        /// On = 始终缓存(默认,适合 N 大 + 低频更新的 K 线类业务)
        /// Off = 永不缓存(适合实时 tick / 极小 N 的场景,缓存反而拖累)
        /// Adaptive = 跟随 <see cref="ReportVisibleDataCount"/> 上报的可见数据量,在两阈值之间滞回切换
        /// </summary>
        public StaticCachePolicy StaticCachePolicy
        {
            get => _staticCachePolicy;
            set
            {
                if (_staticCachePolicy == value) return;
                _staticCachePolicy = value;
                ApplyStaticCacheState();
            }
        }
        private StaticCachePolicy _staticCachePolicy = StaticCachePolicy.On;

        /// <summary>Adaptive 模式:可见数据量超过这个阈值时启用缓存。默认 500。</summary>
        public int StaticCacheEnableThreshold { get; set; } = 500;

        /// <summary>Adaptive 模式:可见数据量低于这个阈值时关闭缓存(滞回阈值)。默认 200。
        /// 必须 &lt; <see cref="StaticCacheEnableThreshold"/> 否则会发生切换抖动。</summary>
        public int StaticCacheDisableThreshold { get; set; } = 200;

        // 实际生效的缓存状态(避免重复给 _drawingCanvas 设同样的 CacheMode 值)
        private bool _isStaticCacheActive;

        /// <summary>
        /// 业务在视口可见数据量变化时调用(典型:viewport ActiveRange 变化后)。
        /// 仅在 <see cref="StaticCachePolicy"/> = Adaptive 时影响缓存状态。
        /// On / Off 模式下此调用是 no-op。
        /// </summary>
        public void ReportVisibleDataCount(int count)
        {
            if (_staticCachePolicy != StaticCachePolicy.Adaptive) return;

            // 滞回:只在跨越对应阈值时才切换
            if (!_isStaticCacheActive && count >= StaticCacheEnableThreshold)
            {
                _isStaticCacheActive = true;
                ApplyStaticCacheState();
            }
            else if (_isStaticCacheActive && count <= StaticCacheDisableThreshold)
            {
                _isStaticCacheActive = false;
                ApplyStaticCacheState();
            }
        }

        /// <summary>
        /// 把"目标缓存状态"应用到 _drawingCanvas.CacheMode。
        /// On / Off 直接强制开关;Adaptive 看 _isStaticCacheActive 当前值(由 ReportVisibleDataCount 驱动)。
        /// </summary>
        private void ApplyStaticCacheState()
        {
            bool shouldCache = _staticCachePolicy switch
            {
                StaticCachePolicy.On => true,
                StaticCachePolicy.Off => false,
                StaticCachePolicy.Adaptive => _isStaticCacheActive,
                _ => false
            };

            if (shouldCache && _drawingCanvas.CacheMode == null)
            {
                _drawingCanvas.CacheMode = new BitmapCache { SnapsToDevicePixels = true, EnableClearType = false };
            }
            else if (!shouldCache && _drawingCanvas.CacheMode != null)
            {
                _drawingCanvas.CacheMode = null;
            }
        }

        // ==========================================
        // 3. 初始化
        // ==========================================
        static ChartCell()
        {
            // 强制要求 Template 必须是 ChartTemplate 类型
            TemplateProperty.OverrideMetadata(typeof(ChartCell),
                new FrameworkPropertyMetadata(null, OnTemplateChanged, CoerceTemplateValue));
        }

        public ChartCell()
        {
            // diagnostics 注入到 renderer provider —— per-cell 计数无侵入
            _wpfProvider = new WpfRenderProvider(_diagnostics);

            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            // 显式断开默认 Template，防止污染
            SetCurrentValue(TemplateProperty, null);

            // 像素对齐:Aliased 关闭 visual 边缘 AA,父级亚像素布局不影响内部像素栅格的呈现。
            RenderOptions.SetEdgeMode(_drawingCanvas, EdgeMode.Aliased);
            RenderOptions.SetEdgeMode(_overlayCanvas, EdgeMode.Aliased);

            // [关键] hit-test 兜底:_rootContainer / Canvas 默认 Background=null → 整个 chart 不 hit-test-visible
            // → mouse 事件穿透到 Window 下面,ChartInteractionFeature 的 MouseMove 订阅永远收不到。
            // Transparent 跟 null 不同:它是个真 brush,视觉透明但 hit-test 命中,事件能 bubble 到 ChartCell。
            // (Skia 时代不需要这步,因为 SKElement 渲染的位图本身 hit-test-visible。)
            _rootContainer.Background = System.Windows.Media.Brushes.Transparent;

            // 触屏支持:开启 WPF Manipulation 通道,触屏事件不再 promote 成 mouse,
            // 由 ChartInteractionFeature 的 ManipulationStarting/Delta 订阅接管(双指捏合 + 单指拖)。
            // 桌面无触屏场景实际开销 ~ 0(WPF 不会主动启动 PenIMC 线程)。
            IsManipulationEnabled = true;

            // [WPF perf] 默认按策略 On 挂 BitmapCache。业务可改 StaticCachePolicy 切到 Adaptive / Off。
            ApplyStaticCacheState();

            // 基础结构:从下到上 DrawingCanvas (静态 Software) → OverlayCanvas (交互 Software) → InteractionCanvas (Widget,懒加载)
            _rootContainer.Children.Add(_drawingCanvas); // Bottom-Middle (静态)
            _rootContainer.Children.Add(_overlayCanvas); // Middle-High (交互独立 host,跟静态层 dirty 互不牵连)
            base.Content = _rootContainer;

            // 💥 架构优化：已移除此处的 CompositionTarget.Rendering 订阅！
            // 绝对不允许在静置时挂载 VSync 泵，彻底消灭 GPU 空转 10% 的底层元凶！

            _rootContainer.SizeChanged += _rootContainer_SizeChanged;

            // 💥 Phase 11 / §H：Schema 可暂停生命周期宿主
            // 三种触发场景：
            //   1. Loaded/Unloaded —— TabControl 默认行为：切 Tab 时非激活的 TabItem 内容从 visual tree
            //      移除触发 Unloaded，回到时重新 Load 触发 Loaded。这是 TabControl 最可靠的事件。
            //   2. IsVisibleChanged —— 父容器 Visibility=Collapsed、虚拟化列表离屏等
            //   3. Window.StateChanged → Minimized —— WPF 最小化不改 IsVisible，必须单独挂 Window
            //
            // ChartHost 不再重复挂事件——由 ChartCell 统一承担，无论用户是走 ChartHost 包装还是
            // `new ChartCell { Template = schema }` 直接挂载（如 Window2.xaml.cs 的 KLine 用法）都能生效。
            Loaded += OnCellLoaded;
            Unloaded += OnCellUnloaded;
            IsVisibleChanged += OnCellIsVisibleChanged;
        }

        private Window? _hookedWindow;
        private EventHandler? _stateChangedHandler;

        private void OnCellLoaded(object sender, RoutedEventArgs e)
        {
            // TabControl 切回 Tab → schema.Resume:数据源 / 心跳触发 Resume,补一帧 snapshot 让 port 重写、
            //                                       layer 自然 mark dirty 触发重绘。
            if (Template is Abstractions.IPausable p) p.Resume();

            // Loaded 时注入真实 DPI:visual 已挂入 visual tree,VisualTreeHelper.GetDpi(this) 才能拿到所在屏的 DPI。
            _wpfProvider.UpdateDpi(VisualTreeHelper.GetDpi(this).PixelsPerDip);

            _hookedWindow = Window.GetWindow(this);
            if (_hookedWindow != null)
            {
                _stateChangedHandler = (sender, __) =>
                {
                    if (Template is not Abstractions.IPausable pp) return;
                    if (sender is not Window w) return;
                    if (w.WindowState == WindowState.Minimized) pp.Suspend();
                    else                                        pp.Resume();
                };
                _hookedWindow.StateChanged += _stateChangedHandler;
            }
        }

        private void OnCellUnloaded(object sender, RoutedEventArgs e)
        {
            // TabControl 切走 Tab → schema.Suspend:停定时器、Dispose 数据源 _pipelineSub 防止丧尸回包,
            // 但保留 buffer / compose 期订阅链 / lifeTimeSession 不动,等 Loaded 时 Resume 接续。
            //
            // session 的真正 Dispose 由 ChartLifecycle 绑 Window.Closed 兜底,跨 tab 不会误烧。
            if (Template is Abstractions.IPausable p) p.Suspend();

            if (_hookedWindow != null && _stateChangedHandler != null)
            {
                _hookedWindow.StateChanged -= _stateChangedHandler;
                _hookedWindow = null;
                _stateChangedHandler = null;
            }
        }

        private void OnCellIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (Template is not Abstractions.IPausable p) return;
            if ((bool)e.NewValue) p.Resume();
            else                  p.Suspend();
        }

        // 跨屏拖动 / 系统 DPI 变更 → 重新注入到 renderer,FormattedText 重新按真实 DPI 排版。
        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            _wpfProvider.UpdateDpi(newDpi.PixelsPerDip);
        }

        private void _rootContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width == 0 || e.NewSize.Height == 0) return;

            // 💥 依然保留亚像素震荡拦截防线！
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < SubPixelResizeThreshold &&
                Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < SubPixelResizeThreshold)
            {
                return;
            }

            // 4. 写入 ChartCell 的全局共享黑板
            RequestUpdate(ctx =>
            {
                // 1. 发送新的物理尺寸
                ctx.Shared().PublishData(new ViewportSizeTrait(e.NewSize.Width, e.NewSize.Height));

                // 如果你之前封装了 InvalidateEnvironment，就在这里调用
                if (Template is IFeatureProjector projector)
                {
                    projector.InvalidateEnvironment();
                }
            });
        }

        /// <summary>
        /// 核心渲染管线(每帧 VSync 由 OnCompositionTargetRendering 调一次)。
        /// 三阶段:① schema.ProjectAll → 让所有 Feature 重算 trait;② 标脏 + 制作 snapshot 喂给 Layer.Update;
        /// ③ 视任务量同步 / 并行执行。Parallel 阈值 ≥3 是经验值,任务太少时多线程 dispatch 反而亏。
        /// </summary>
        internal void ExecutePipeline(RenderContext ctx, PlotMode mode)
        {
            if (Template is IFeatureProjector projector)
            {
                projector.ProjectAll(ctx);
            }
            // 1. 提交与标脏
            ctx.SubmitSync(ActiveLayers);

            // 修复 H4-c：手写 for 循环 + 跨帧复用 _dirtyLayerBuffer，消除每帧 LINQ 迭代器 + List 分配。
            var active = ActiveLayers;
            _dirtyLayerBuffer.Clear();
            for (int i = 0; i < active.Count; i++)
            {
                if (active[i] is ChartLayer cl && cl.IsDirty) _dirtyLayerBuffer.Add(cl);
            }
            if (_dirtyLayerBuffer.Count == 0) return;

            // 2. 制作快照
            using var frame = ctx.PrepareTasks(_dirtyLayerBuffer);

            // 3. 执行更新 (智能降级)
            if (mode == PlotMode.Parallel && frame.Tasks.Count >= 3)
            {
                Parallel.ForEach(frame.Tasks, t => t.Layer.Update(t.DataSnapshot));
            }
            else
            {
                foreach (var t in frame.Tasks) t.Layer.Update(t.DataSnapshot);
            }
        }

        /// <summary>
        /// 申请一个事务,把 updateAction 排入下一帧的 pending 队列;
        /// 队列首次非空时才挂 CompositionTarget.Rendering(动态 VSync),帧尾立即拔除以保证静置 0 GPU 占用。
        /// </summary>
        /// <param name="updateAction">在下一帧 OnCompositionTargetRendering 内执行的事务体(写黑板 / 触发 invalidate 等)。</param>
        private bool _isUpdateRequested = false;

        // 双缓冲队列:RequestUpdate 写 _pendingUpdates,渲染帧 swap 出 _processingBuffer 后离锁迭代。
        // 替代原先 `Action<RenderContext> _pendingUpdateChain += action` 的 Delegate.Combine 模式 ——
        // 那种写法每次 += 都 new MulticastDelegate + 拷贝内部 _invocationList 数组,
        // mouse move / WebSocket 高频灌入时是 O(N²) 拷贝 + N 个递增大小的 MulticastDelegate 堆分配。
        // 改成 List 后单次 Add 是 O(1) 摊销,跨帧复用 backing array,稳态零分配。
        private List<Action<RenderContext>> _pendingUpdates = new(8);
        private List<Action<RenderContext>> _processingBuffer = new(8);

        // 💥 增加一把专用的调度锁
        private readonly object _updateLock = new object();

        public void RequestUpdate(Action<RenderContext> updateAction)
        {
            // 💥 无论是后台 WebSocket 线程，还是前台鼠标线程，进来都要排队！
            lock (_updateLock)
            {
                _pendingUpdates.Add(updateAction);

                if (!_isUpdateRequested)
                {
                    _isUpdateRequested = true;

                    // 💥 按需挂载机制 (Dynamic VSync Hook)
                    // 只有真有任务了，才去唤醒 WPF 的 VSync 渲染泵。
                    // UI 线程同步订阅,绕过 Dispatcher.InvokeAsync 的一帧延迟(crosshair tracking 关键路径)；
                    // 后台线程仍走 InvokeAsync 跨线程投递。
                    if (Dispatcher.CheckAccess())
                    {
                        CompositionTarget.Rendering -= OnCompositionTargetRendering;
                        CompositionTarget.Rendering += OnCompositionTargetRendering;
                    }
                    else
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            CompositionTarget.Rendering -= OnCompositionTargetRendering;
                            CompositionTarget.Rendering += OnCompositionTargetRendering;
                        }, System.Windows.Threading.DispatcherPriority.Render);
                    }
                }
            }
        }

        private void OnCompositionTargetRendering(object? sender, EventArgs e)
        {
            // 💥 以极快的速度交换缓冲，然后立刻释放锁，绝不阻塞！
            // swap 后 _pendingUpdates 立即可承接新写入(比如本帧 ExecutePipeline 内回流的 RequestUpdate),
            // 本帧迭代用的是旧 _processingBuffer,锁外读不会被并发追加。
            bool hasWork = false;
            lock (_updateLock)
            {
                if (_isUpdateRequested)
                {
                    if (_pendingUpdates.Count > 0)
                    {
                        (_pendingUpdates, _processingBuffer) = (_processingBuffer, _pendingUpdates);
                        hasWork = true;
                    }
                    _isUpdateRequested = false;

                    // 💥 画完即休眠：立刻从 WPF 的 VSync 泵上拔除当前图表！
                    // 这行代码是消灭 10% GPU 静置占用的核心魔法。管线处理完当前帧后，直接让 GPU 进入绝对静默状态。
                    CompositionTarget.Rendering -= OnCompositionTargetRendering;
                }
            }

            // 出了锁之后，在安全的 UI 线程里慢慢执行管线
            if (hasWork)
            {
                using var ctx = new RenderContext(_sharedData, _localData);

                // 直接 for 索引迭代,跳过 List<T>.Enumerator 的 IDisposable 调用链。
                var actions = _processingBuffer;
                for (int i = 0; i < actions.Count; i++) actions[i](ctx);
                actions.Clear(); // 释放对 captured closures 的引用,避免跨帧拖死 ctx 相关对象

                ExecutePipeline(ctx, PlotMode.Sync);
                Invalidate();
            }
        }
        /// <summary>
        /// 把后台录制好的图层指令真正"画"到屏幕上。
        /// 三阶段:① SwapBuffer 把所有脏图层 back→front 同步到同一帧;
        /// ② RenderWpfLayers / RenderWidgetLayers 分别派发矢量与控件指令;
        /// ③ PostRender 清脏标记 + Diagnostics 收口。
        /// </summary>
        internal void Invalidate()
        {
            // diagnostics:帧首清零累加器(per-layer Render 调用会向其内累加)
            _diagnostics.BeginFrame();

            // ==========================================
            // 阶段 1：统一同步点 (Sync Point)
            // ==========================================
            // 将所有后台录制好的 BackBuffer 一次性全部推到前台 FrontBuffer。
            // 确保在这一刻之后，WPF、Skia、Widget 读到的全都是同一帧的最新数据！
            // 走 IReadOnlyList<T> 索引 + is ChartLayer，避免 OfType<T> 的 LINQ 迭代器与 IEnumerator 装箱。
            var swapList = ActiveLayers;
            for (int i = 0; i < swapList.Count; i++)
            {
                if (swapList[i] is ChartLayer cl && cl.IsDirty) cl.SwapBuffer();
            }

            // ==========================================
            // 阶段 2：派发渲染指令
            // ==========================================

            // 管线 A: WPF 矢量层 (保留模式，仅给脏图层发新指令)
            RenderWpfLayers();

            // 管线 B: WPF 控件层 (仅给脏图层同步新的控件池摆放)
            RenderWidgetLayers();

            // ==========================================
            // 阶段 3：统一清理脏标记
            // ==========================================
            var postList = ActiveLayers;
            for (int i = 0; i < postList.Count; i++)
            {
                if (postList[i] is ChartLayer cl) cl.PostRender(); // IsDirty = false
            }

            // diagnostics:帧尾收口,FrameCount++ + 拍 LastFrame 快照
            _diagnostics.EndFrame();
        }
        /// <summary>
        /// --- 管线 A: WPF Drawing ---
        /// 同时刷 _drawingCanvas (静态层) 和 _overlayCanvas (交互层) 各自的 Visual。
        /// 两个 UIElement 各管自己的 dirty rect,WPF compositor 间不会牵连。
        /// </summary>
        private void RenderWpfLayers()
        {
            for (int i = 0; i < _visualRegistry.Count; i++)
            {
                var layer = _visualRegistry[i].Layer;
                if (layer is ChartLayer cl && cl.IsDirty && cl.Buffer is LayerBuffer buffer)
                {
                    using var dc = cl.RenderOpen();
                    buffer.Execute(_wpfProvider, dc);
                }
            }
            for (int i = 0; i < _overlayVisualRegistry.Count; i++)
            {
                var layer = _overlayVisualRegistry[i].Layer;
                if (layer is ChartLayer cl && cl.IsDirty && cl.Buffer is LayerBuffer buffer)
                {
                    using var dc = cl.RenderOpen();
                    buffer.Execute(_wpfProvider, dc);
                }
            }
        }

        // --- 管线 B: Widget Controls ---
        private void RenderWidgetLayers()
        {
            // for + 索引避免 IReadOnlyList<T> 上 foreach 的 IEnumerator 装箱(每帧都跑)。
            var layers = ActiveLayers;
            for (int i = 0; i < layers.Count; i++)
            {
                // 【极其重要的优化】：只有图层变脏了，才去同步它的控件池！避免每帧无意义的遍历。
                if (layers[i] is ChartLayer cl && cl.IsDirty)
                {
                    if (cl.Buffer is LayerBuffer buffer)
                    {
                        var pool = GetOrAddWidgetPool(cl); // 使用上一轮优化好的 Pool

                        // 无论空还是有指令，一把梭哈交给 Pool 自己去处理显隐和复用
                        pool.Sync(buffer.Widget.Commands);
                    }
                }
            }
        }

        // --- Widget Host Factory ---
        private LayerWidgetPool GetOrAddWidgetPool(IChartLayer layer)
        {
            if (_widgetRegistry.TryGetValue(layer, out var pool)) return pool;

            EnsureInteractionCanvasInitialized();

            pool = new LayerWidgetPool(_interactionCanvas!);
            _widgetRegistry[layer] = pool;
            return pool;
        }

        // =================================================================
        // 2. 核心属性
        // =================================================================

        /// <summary>静态矢量层宿主(N 大、低频更新的 Grid / Axis / Series)。可挂 BitmapCache,见 <see cref="StaticCachePolicy"/>。</summary>
        public ChartDrawingCanvas DrawingCanvas => _drawingCanvas;

        /// <summary>顶层交互控件宿主(Tooltip / WPF 控件)。懒加载:首次 AddLayer Widget 类时才创建。</summary>
        public InteractionCanvas? InteractionCanvas => _interactionCanvas;

        // =================================================================
        // 3. 模板与生命周期 (最关键部分)
        // =================================================================

        /// <summary>
        /// WPF Template 应用钩子。
        /// 数据流:首次 measure 触发 → 构造一个 ChartSession(compose 期订阅生命周期容器)
        /// → schema.ComposeAll(让所有 Feature 的 OnCompose 跑一遍,登记图层 + 订阅事件)
        /// → schema.Aspect.Decorate 把外壳 UI 包到 PART_Root → BindTo 把 session 绑到 ChartLifecycle.ActiveSession 附加属性。
        /// 之后第一次 SubmitSync 让所有图层吃到当前 trait 快照。
        /// </summary>
        public override void OnApplyTemplate()
        {
            var rootDecorator = GetTemplateChild("PART_Root") as AdornerDecorator;
            if (rootDecorator != null && Template is ChartSchema schema)
            {
                var lifeTimeSession = new ChartSession();
                using var ctx = new RenderContext(_sharedData, _localData, lifeTimeSession);

                schema.ComposeAll(this, ctx);
                var seedProxy = new ContentPresenter { Content = base.Content };
                var decoratedUI = schema.Aspect.Decorate(seedProxy);
                rootDecorator.Child = decoratedUI;

                ChartLifecycle.BindTo(this, lifeTimeSession);
                ctx.SubmitSync(ActiveLayers);
            }
        }

        /// <summary>
        /// 显式清理:把 lifeTimeSession 烧掉(compose 期所有订阅) + DecomposeAll 把 schema 状态彻底回收。
        /// 业务侧在 cell 真不再需要时调用——典型场景:Window.Closed、动态移除 cell 不再挂回。
        /// 不调也没大问题,GC 最终会回收(只是回收时机不确定)。
        /// </summary>
        public void Shutdown()
        {
            if (Template is ChartSchema schema && GetValue(ChartLifecycle.ActiveSessionProperty) != null)
            {
                using var ctx = CreateContext();
                schema.DecomposeAll(this, ctx);
            }
            ClearValue(ChartLifecycle.ActiveSessionProperty);
        }

        // =================================================================
        // 4. 层级管理逻辑
        // =================================================================

        /// <summary>检查是否存在指定名称的图层(按 Layer.Name 字符串匹配)。</summary>
        public bool HasLayer(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            // 使用 LINQ 遍历查找
            return ActiveLayers.Any(layer => layer.Name == name);
        }

        /// <summary>
        /// ⚠️ 添加非托管图层 (高危操作！)
        /// 此方法脱离了 Schema 的生命周期管理与 Transact 事务。
        /// 调用此方法的开发者，必须自行在适当的时机调用 RemoveUnmanagedLayer，否则会导致严重的内存泄漏与图层重影！
        /// 推荐做法：在 Schema 的 DefineFeatures 中使用 canvas.AddLayer()。
        /// </summary>
        public VisualProxy<TLayer> AddUnmanagedLayer<TLayer>(TLayer layer) where TLayer : IChartLayer
        {
            // 路由策略:
            //   Level == Interaction       → _overlayVisualRegistry (_overlayCanvas,跟静态层 dirty 隔离)
            //   其它 Level                  → _visualRegistry (_drawingCanvas,静态层)
            //   注:RenderMode.Hardware 当前等同于 Software,统一走 WPF 路径(详见 TODO.md "Skia 移除")
            if (layer is ChartLayer v && v.Level == ChartLayerType.Interaction)
            {
                int insertIndex = 0;
                for (; insertIndex < _overlayVisualRegistry.Count; insertIndex++)
                {
                    if (_overlayVisualRegistry[insertIndex].Type > v.Level) break;
                }
                _overlayCanvas.InsertVisual(insertIndex, v);
                _overlayVisualRegistry.Insert(insertIndex, (v, v.Level));
            }
            else if (layer is ChartLayer staticLayer)
            {
                int insertIndex = 0;
                for (; insertIndex < _visualRegistry.Count; insertIndex++)
                {
                    if (_visualRegistry[insertIndex].Type > staticLayer.Level) break;
                }
                _drawingCanvas.InsertVisual(insertIndex, staticLayer);
                _visualRegistry.Insert(insertIndex, (staticLayer, staticLayer.Level));
            }
            UpdateActiveLayersCache();

            var localBag = _localData.GetValue(layer, _ => new VisualDataBag());

            // 包装成 0-GC 结构体返回，供后续链式调用
            return new VisualProxy<TLayer>(layer, localBag, localBag);
        }

        // 跨帧缓存:Add/RemoveUnmanagedLayer 内部维护,避免 ActiveLayers 每次访问都拼一个 List。
        // volatile 保证后台线程偶发读到的是已发布的最新引用(写永远在 UI 线程)。
        private volatile IReadOnlyList<IChartLayer> _activeLayersCache = Array.Empty<IChartLayer>();

        /// <summary>当前所有活跃图层的只读列表(_visualRegistry + _overlayVisualRegistry 拼接结果)。读取无锁,内部走 volatile 缓存。</summary>
        public IReadOnlyList<IChartLayer> ActiveLayers => _activeLayersCache;

        // 当你 AddLayer 或 RemoveLayer 时，同步更新这个缓存：
        // 修复 H4-d：手写 for 循环替代 Select / AsReadOnly。
        // List<T> 已实现 IReadOnlyList<T>，AsReadOnly 的 ReadOnlyCollection 包装在此处无额外价值。
        private void UpdateActiveLayersCache()
        {
            var newList = new List<IChartLayer>(_visualRegistry.Count + _overlayVisualRegistry.Count);
            for (int i = 0; i < _visualRegistry.Count; i++) newList.Add(_visualRegistry[i].Layer);
            for (int i = 0; i < _overlayVisualRegistry.Count; i++) newList.Add(_overlayVisualRegistry[i].Layer);

            // 原子操作：volatile 字段赋值，读侧按 IReadOnlyList<IChartLayer> 访问
            _activeLayersCache = newList;
        }
        /// <summary>按 Name 查图层(找不到返回 null)。配合 <see cref="HasLayer"/> 使用。</summary>
        public IChartLayer? GetLayer(string name)
        {
            return ActiveLayers.FirstOrDefault(l => l.Name == name);
        }

        /// <summary>构造一个无 session 的渲染上下文。仅用于 Shutdown / OnTemplateChanged 等"无主"清理路径。</summary>
        internal RenderContext CreateContext()
        {
            return new RenderContext(_sharedData, _localData);
        }

        private void EnsureInteractionCanvasInitialized()
        {
            if (_interactionCanvas == null)
            {
                // 懒加载：只有当真正添加了交互层时才创建 Canvas
                _interactionCanvas = new InteractionCanvas();
                // 确保交互层在绘图层之上 (Z-Order)
                // Grid 默认行为：后添加的在上面
                _rootContainer.Children.Add(_interactionCanvas);
            }
        }

        /// <summary>
        /// 安全移除图层，并彻底清理其关联的物理资源、控件池与状态
        /// </summary>
        public void RemoveUnmanagedLayer(IChartLayer layer)
        {
            if (layer == null) return;

            bool isRemoved = false;

            // ==========================================
            // 第一步：从底层渲染管线中连根拔起
            //   路由策略跟 AddUnmanagedLayer 对称: Hardware / Software-Interaction / Software-其它
            // ==========================================
            if (layer is ChartLayer v && v.Level == ChartLayerType.Interaction)
            {
                // 手写 for 循环避免 LINQ FirstOrDefault 捕获 layer 产生的闭包分配。
                for (int i = 0; i < _overlayVisualRegistry.Count; i++)
                {
                    if (ReferenceEquals(_overlayVisualRegistry[i].Layer, layer))
                    {
                        _overlayVisualRegistry.RemoveAt(i);
                        _overlayCanvas.RemoveVisual(v);
                        isRemoved = true;
                        break;
                    }
                }
            }
            else if (layer is ChartLayer staticLayer)
            {
                for (int i = 0; i < _visualRegistry.Count; i++)
                {
                    if (ReferenceEquals(_visualRegistry[i].Layer, layer))
                    {
                        _visualRegistry.RemoveAt(i);
                        _drawingCanvas.RemoveVisual(staticLayer);
                        isRemoved = true;
                        break;
                    }
                }
            }

            // ==========================================
            // 第二步：从顶层交互管线中销毁控件池
            // ==========================================
            if (_widgetRegistry.TryGetValue(layer, out var widgetPool))
            {
                // 1. 从字典中注销
                _widgetRegistry.Remove(layer);

                // 2. 【核心魔法】：销毁整个池子！
                // 这会将该图层在 InteractionCanvas 上创建的 N 个 ContentPresenter 全部 Remove 掉。
                // 彻底消灭屏幕上的“幽灵残影”和 WPF 强引用。
                widgetPool.Destroy();
            }

            // ==========================================
            // 第三步：物理资源释放与重绘
            // ==========================================
            if (isRemoved)
            {
                // 调用 Dispose 释放该图层可能持有的 Skia 画笔、路径等非托管资源
                layer.Dispose();

                // 【幕后魔法说明】
                // 到此为止，layer 已经失去了所有的强引用。
                // 等待 GC 扫描时，ChartCell 里的 ConditionalWeakTable 会自动发现 layer 死了，
                // 然后静悄悄地把属于它的 VisualDataBag (状态快照) 给清理掉。0 泄漏！

                // 强制触发一次空重绘，把移除图层后的最新画面刷到屏幕上
                Invalidate();
            }

            UpdateActiveLayersCache();
        }
        // =================================================================
        // 5. 辅助与校验
        // =================================================================

        private static object? CoerceTemplateValue(DependencyObject d, object baseValue)
        {
            // 设计模式下不做限制，方便预览
            if (DesignerProperties.GetIsInDesignMode(d)) return baseValue;

            // 运行时严格限制：如果不是 ChartTemplate，直接拒绝 (返回 null)
            return baseValue is ChartTemplate ? baseValue : null;
        }

        private static void OnTemplateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue != null && e.NewValue is not ChartTemplate)
            {
                throw new InvalidOperationException($"ChartCell Template must be of type {nameof(ChartTemplate)}.");
            }

            if (d is ChartCell cell && e.OldValue is ChartSchema oldSchema)
            {
                // 创建一个临时的上下文，给旧图纸最后一次机会清理自己
                using var ctx = cell.CreateContext();

                // 扣动扳机：触发旧 Schema (如 ReactiveSchema) 的 DecomposeAll，
                // 进而触发所有 Feature 的连根拔起和图层卸载！
                oldSchema.DecomposeAll(cell, ctx);
            }
        }

        // 隐藏基类 Content 属性，避免外部开发者误用
        [EditorBrowsable(EditorBrowsableState.Never)]
        public new object Content
        {
            get => base.Content;
            private set => base.Content = value;
        }
    }

    public enum PlotMode
    {
        Sync,       // UI 线程，直接跑
        Async,      // 后台线程，单核跑 (任务少时)
        Parallel    // 后台线程，压榨多核 (任务多时)
    }


    public static class ChartPipelineExtensions
    {
        /// <summary>
        /// 💥 [全局流] 为整个 Chart 播种全局特质。
        /// </summary>
        public static ChartCell Seed<TTrait>(this ChartCell chart, TTrait trait)
            where TTrait : class, IVisualTrait
        {
            // 直接访问你类里的 _sharedData (对应之前的 LiveGlobalData)
            chart.GetSharedData().Publish<TTrait>(trait);
            return chart;
        }
    }
}
