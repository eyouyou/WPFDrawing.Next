using Hevo.Charting.Abstractions;
using Hevo.Charting.Renderers;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 模板类型，限定只能用于 ChartCell
    /// </summary>
    public class ChartTemplate : ControlTemplate
    {
        public string TemplateName { get; set; } = string.Empty;
        public ChartTemplate()
        {
            TargetType = typeof(ChartCell);
        }
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
    /// Chart 最小可部署单元 (已升级为三明治架构)。
    /// 职责：
    /// 1. 物理容器：管理 DrawingCanvas(底层), VectorCanvas(中层), InteractionCanvas(顶层)。
    /// 2. 层级管理：确保 D3D -> Vector -> Interaction -> Overlay 的物理顺序。
    /// 3. 生命周期：作为 ChartSession 的挂载点。
    /// </summary>
    public class ChartCell : ContentControl
    {
        // ==========================================
        // 1. 物理结构
        // ==========================================
        private readonly Grid _rootContainer = new();

        // [底层] 硬件光栅化层 (Skia / D3D)
        private readonly SKElement _skiaElement = new();

        // [中层] WPF 矢量层 (DrawingVisuals)
        private readonly ChartDrawingCanvas _drawingCanvas = new() { };

        // [顶层] 交互与控件层 (懒加载)
        private InteractionCanvas? _interactionCanvas;

        // ==========================================
        // 2. 注册表
        // ==========================================

        // 软件层 (WPF Retained Mode) -> 对应 _drawingCanvas
        private readonly List<(IChartLayer Layer, ChartLayerType Type)> _visualRegistry = new();

        // 硬件层 (Skia Immediate Mode) -> 对应 _skiaElement
        private readonly List<IChartLayer> _hardwareLayers = new();

        // 修复 H4-c：跨帧复用的脏图层缓冲。每帧 Clear 后填充，替代
        // `ActiveLayers.OfType<ChartLayer>().Where(l => l.IsDirty).ToList()` 的 LINQ 迭代器 + List 分配。
        // ExecutePipeline 仅在 UI 线程 OnCompositionTargetRendering 内调用，单线程复用安全。
        private readonly List<ChartLayer> _dirtyLayerBuffer = new();

        // Widget 宿主池: Layer -> 物理控件包装器
        private readonly Dictionary<IChartLayer, LayerWidgetPool> _widgetRegistry = new();

        // 跨帧持久化数据
        private readonly VisualDataBag _sharedData = new();
        private readonly ConditionalWeakTable<IChartLayer, VisualDataBag> _localData = new();


        // RendererProvider
        private readonly WpfRenderProvider _wpfProvider = new WpfRenderProvider();
        private readonly SkiaRenderProvider _skiaProvider = new SkiaRenderProvider();

        internal VisualDataBag GetSharedData() => _sharedData;
        internal VisualDataBag GetLocalBag(IChartLayer layer)
        {
            return _localData.GetValue(layer, _ => new VisualDataBag());
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
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            // 显式断开默认 Template，防止污染
            SetCurrentValue(TemplateProperty, null);

            // 基础结构：DrawingCanvas 铺底
            _rootContainer.Children.Add(_skiaElement);   // Bottom
            _rootContainer.Children.Add(_drawingCanvas); // Middle
            base.Content = _rootContainer;

            _skiaElement.PaintSurface += OnSkiaPaintSurface;

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
            // TabControl 切回 Tab 时重新 Loaded → Schema.Resume
            if (Template is Abstractions.IPausable p) p.Resume();

            _hookedWindow = Window.GetWindow(this);
            if (_hookedWindow != null)
            {
                _stateChangedHandler = (_, __) =>
                {
                    if (Template is not Abstractions.IPausable pp) return;
                    if (_hookedWindow.WindowState == WindowState.Minimized) pp.Suspend();
                    else                                                     pp.Resume();
                };
                _hookedWindow.StateChanged += _stateChangedHandler;
            }
        }

        private void OnCellUnloaded(object sender, RoutedEventArgs e)
        {
            // TabControl 切走 Tab 时 Unloaded → Schema.Suspend
            // 注意：ChartCell 的全量卸载（lifeTimeSession.Dispose）仍由 ChartLifecycle 附加属性走自己路径
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

        private void _rootContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width == 0 || e.NewSize.Height == 0) return;

            // 💥 依然保留亚像素震荡拦截防线！
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5 &&
                Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 0.5)
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
        /// 核心方法：请求更新
        /// 外部 (交互层) 通过这个方法申请一个新的“事务”
        /// 
        /// </summary>
        /// <param name="updateAction"></param>
        private bool _isUpdateRequested = false;
        private Action<RenderContext>? _pendingUpdateChain;

        // 💥 增加一把专用的调度锁
        private readonly object _updateLock = new object();

        public void RequestUpdate(Action<RenderContext> updateAction)
        {
            // 💥 无论是后台 WebSocket 线程，还是前台鼠标线程，进来都要排队！
            lock (_updateLock)
            {
                _pendingUpdateChain += updateAction;

                if (!_isUpdateRequested)
                {
                    _isUpdateRequested = true;

                    // 💥 按需挂载机制 (Dynamic VSync Hook)
                    // 只有真有任务了，才去唤醒 WPF 的 VSync 渲染泵。
                    // 使用 Dispatcher 确保安全跨线程投递到 UI 线程执行。
                    Dispatcher.InvokeAsync(() =>
                    {
                        // 先减后加，防御性编程，防止任何意外的重复订阅导致帧率翻倍异常
                        CompositionTarget.Rendering -= OnCompositionTargetRendering;
                        CompositionTarget.Rendering += OnCompositionTargetRendering;
                    }, System.Windows.Threading.DispatcherPriority.Render);
                }
            }
        }

        private void OnCompositionTargetRendering(object? sender, EventArgs e)
        {
            Action<RenderContext>? currentChain = null;

            // 💥 以极快的速度摘取任务链，然后立刻释放锁，绝不阻塞！
            lock (_updateLock)
            {
                if (_isUpdateRequested)
                {
                    currentChain = _pendingUpdateChain;
                    _pendingUpdateChain = null;
                    _isUpdateRequested = false;

                    // 💥 画完即休眠：立刻从 WPF 的 VSync 泵上拔除当前图表！
                    // 这行代码是消灭 10% GPU 静置占用的核心魔法。管线处理完当前帧后，直接让 GPU 进入绝对静默状态。
                    CompositionTarget.Rendering -= OnCompositionTargetRendering;
                }
            }

            // 出了锁之后，在安全的 UI 线程里慢慢执行管线
            if (currentChain != null)
            {
                using var ctx = new RenderContext(_sharedData, _localData);

                currentChain(ctx);
                ExecutePipeline(ctx, PlotMode.Sync);
                Invalidate();
            }
        }
        /// <summary>
        /// 触发物理重绘
        /// </summary>
        internal void Invalidate()
        {
            // ==========================================
            // 阶段 1：统一同步点 (Sync Point)
            // ==========================================
            // 将所有后台录制好的 BackBuffer 一次性全部推到前台 FrontBuffer。
            // 确保在这一刻之后，WPF、Skia、Widget 读到的全都是同一帧的最新数据！
            foreach (var layer in ActiveLayers.OfType<ChartLayer>())
            {
                if (layer.IsDirty) layer.SwapBuffer();
            }

            // ==========================================
            // 阶段 2：派发渲染指令
            // ==========================================

            // 管线 A: WPF 矢量层 (保留模式，仅给脏图层发新指令)
            RenderWpfLayers();

            // 管线 B: WPF 控件层 (仅给脏图层同步新的控件池摆放)
            RenderWidgetLayers();

            // 管线 C: Skia 硬件层 (发信号即可，它会异步读取已就绪的 FrontBuffer)
            bool needSkiaRedraw = false;
            foreach (var layer in _hardwareLayers.OfType<ChartLayer>())
            {
                // 因为前面的代码已经把变脏的图层 SwapBuffer 了
                // 此时它们的 IsDirty 依然是 true (要到阶段 3 才会设为 false)
                if (layer.IsDirty)
                {
                    needSkiaRedraw = true;
                    break;
                }
            }

            // 只有当有硬件图层真正更新了指令，或者 WPF 发生尺寸改变触发时，才去呼叫 Skia
            if (needSkiaRedraw)
            {
                _skiaElement.InvalidateVisual();
            }
            // ==========================================
            // 阶段 3：统一清理脏标记
            // ==========================================
            foreach (var layer in ActiveLayers.OfType<ChartLayer>())
            {
                layer.PostRender(); // IsDirty = false
            }
        }
        /// <summary>
        /// --- 管线 A: WPF Drawing ---
        /// </summary>
        private void RenderWpfLayers()
        {
            foreach (var (layer, _) in _visualRegistry)
            {
                // 【优化】：只有被标记为脏的，才需要清空旧指令并录制新指令
                if (layer is ChartLayer cl && cl.IsDirty)
                {
                    if (cl.Buffer is LayerBuffer buffer)
                    {
                        using var dc = cl.RenderOpen(); // 打开 WPF 的绘图上下文
                        buffer.Execute(_wpfProvider, dc);
                    }
                }
            }
        }

        // --- 管线 B: Widget Controls ---
        private void RenderWidgetLayers()
        {
            foreach (var layer in ActiveLayers)
            {
                // 【极其重要的优化】：只有图层变脏了，才去同步它的控件池！避免每帧无意义的遍历。
                if (layer is ChartLayer cl && cl.IsDirty)
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

        // --- 管线 C: Skia Hardware ---
        private void OnSkiaPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
        {
            if (_hardwareLayers.Count == 0) return;

            var canvas = e.Surface.Canvas;
            canvas.Clear(); // 硬件帧缓冲清屏

            foreach (var layer in _hardwareLayers)
            {
                if (layer is ChartLayer cl && cl.Buffer is LayerBuffer buffer)
                {
                    // 直接无脑 Execute！因为最新的指令早就在 Invalidate() 阶段准备好在 FrontBuffer 里了
                    buffer.Execute(_skiaProvider, canvas);
                }
            }
        }

        // =================================================================
        // 2. 核心属性
        // =================================================================

        public ChartDrawingCanvas DrawingCanvas => _drawingCanvas;

        public InteractionCanvas? InteractionCanvas => _interactionCanvas;

        // =================================================================
        // 3. 模板与生命周期 (最关键部分)
        // =================================================================

        public override void OnApplyTemplate()
        {
            // 1. 获取装饰器根节点 (由 ChartSchema 提供)
            var rootDecorator = GetTemplateChild("PART_Root") as AdornerDecorator;

            // 只有当 Template 是我们定义的 Schema 时才执行编排
            if (rootDecorator != null && Template is ChartSchema schema)
            {
                // 1. 创建长期 Session
                var lifeTimeSession = new ChartSession();
                // --- B. 编排阶段 (Compose) ---
                // 创建渲染上下文，它负责开启一个 Session
                // using 语句结束时会调用 Dispose，触发：
                // 1. Submit()：提交初始配置
                // 2. BindTo()：将 Session 绑定到 ChartCell 的 Unloaded 事件
                using var ctx = new RenderContext(_sharedData, _localData, lifeTimeSession);

                // 执行用户的组合逻辑 (User Code)
                schema.ComposeAll(this, ctx);
                var seedProxy = new ContentPresenter { Content = base.Content };
                // --- A. 装饰阶段 (Decorate) ---
                // 创建一个宿主来承载 ChartCell 自身的内容 (_rootContainer)
                var decoratedUI = schema.Aspect.Decorate(seedProxy);
                // 将装饰后的 UI 挂载到可视化树上
                rootDecorator.Child = decoratedUI;

                // 3. [原魔法复原] 显式绑定生命周期
                // 将 Compose 期间产生的事件订阅 (Session) 移交给 ChartCell 管理
                // 当 ChartCell Unloaded 时，这个 Session 会被 Dispose
                ChartLifecycle.BindTo(this, lifeTimeSession);

                ctx.SubmitSync(ActiveLayers);
            }
        }

        // =================================================================
        // 4. 层级管理逻辑
        // =================================================================

        /// <summary>
        /// 检查是否存在指定名称的图层
        /// </summary>
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
            // 策略：Hardware 模式进 Skia 列表，VisualElement 进 WPF 列表
            if (layer is ChartLayer cl && cl.Mode == RenderMode.Hardware)
            {
                _hardwareLayers.Add(layer);
                _hardwareLayers.Sort((a, b) => a.Level.CompareTo(b.Level));
            }
            else if (layer is ChartLayer v)
            {
                int insertIndex = 0;
                for (; insertIndex < _visualRegistry.Count; insertIndex++)
                {
                    if (_visualRegistry[insertIndex].Type > layer.Level) break;
                }
                _drawingCanvas.InsertVisual(insertIndex, v);
                _visualRegistry.Insert(insertIndex, (layer, layer.Level));
            }
            UpdateActiveLayersCache();

            var localBag = _localData.GetValue(layer, _ => new VisualDataBag());

            // 包装成 0-GC 结构体返回，供后续链式调用
            return new VisualProxy<TLayer>(layer, localBag, localBag);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="layerType"></param>
        /// <returns></returns>
        private volatile IReadOnlyList<IChartLayer> _activeLayersCache = Array.Empty<IChartLayer>();
        public IReadOnlyList<IChartLayer> ActiveLayers => _activeLayersCache;

        // 当你 AddLayer 或 RemoveLayer 时，同步更新这个缓存：
        // 修复 H4-d：手写 for 循环替代 Select / AsReadOnly。
        // List<T> 已实现 IReadOnlyList<T>，AsReadOnly 的 ReadOnlyCollection 包装在此处无额外价值。
        private void UpdateActiveLayersCache()
        {
            var newList = new List<IChartLayer>(_visualRegistry.Count + _hardwareLayers.Count);
            for (int i = 0; i < _visualRegistry.Count; i++) newList.Add(_visualRegistry[i].Layer);
            for (int i = 0; i < _hardwareLayers.Count; i++) newList.Add(_hardwareLayers[i]);

            // 原子操作：volatile 字段赋值，读侧按 IReadOnlyList<IChartLayer> 访问
            _activeLayersCache = newList;
        }
        /// <summary>
        /// 获取指定名称的图层 (配合使用)
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public IChartLayer? GetLayer(string name)
        {
            return ActiveLayers.FirstOrDefault(l => l.Name == name);
        }

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
            // ==========================================
            if (layer is ChartLayer cl && cl.Mode == RenderMode.Hardware)
            {
                // 1. 硬件模式：移出 Skia 渲染队列
                isRemoved = _hardwareLayers.Remove(layer);
            }
            else if (layer is ChartLayer v)
            {
                // 2. 软件模式：查找并从逻辑注册表中移除
                var registryItem = _visualRegistry.FirstOrDefault(x => x.Layer == layer);
                if (registryItem.Layer != null)
                {
                    _visualRegistry.Remove(registryItem);

                    // 【极度致命】：从 WPF 物理可视树中摘除！
                    // 斩断强引用，让图层实例可以被垃圾回收 (GC)
                    _drawingCanvas.RemoveVisual(v);
                    isRemoved = true;
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
