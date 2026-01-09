using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using System.Windows.Media;

namespace Hevo.Charting.Abstractions
{
    /// <summary>
    /// 层级接口
    /// </summary>
    public interface IChartLayer : IDisposable
    {
        string Name { get; set; }
        ChartLayerType Level { get; }

        /// <summary>
        /// <summary>
        /// 这是核心产物：一堆录制好的指令
        /// 外部获取产物
        /// </summary>
        RenderBuffer Buffer { get; }

        // 1. 录制阶段 (Update)
        // 职责：清空Buffer -> 计算坐标 -> Push指令
        void Update(IVisualData data);
    }


    /// <summary>
    /// 💥 运行时的无形之手：完美拦截条件分支 (if-else) 下的动态依赖
    /// </summary>
    public readonly struct TrackerDataProxy : IVisualData
    {
        private readonly ChartLayer _layer;
        private readonly IVisualData _snapshot; // 真实的数据快照 (CascadingVisualData)

        public TrackerDataProxy(ChartLayer layer, IVisualData snapshot)
        {
            _layer = layer;
            _snapshot = snapshot;

            // 💥 关键：每次跑 Update 前，清空上一帧的记忆！
            // 保证图层在 if-else 里随时切换分支时，依赖图谱永远是最新的。
            _layer.DependencyTracker.PrepareForNewFrame();
        }

        public T? Get<T>() where T : class
        {
            // 1. 从真实快照中拿数据
            var trait = _snapshot.Get<T>();

            // 3. 💥 指向追踪器：存入录像带
            _layer.DependencyTracker.TrackedRefs[TraitId<T>.Id] = trait;

            return trait;
        }

        public void Publish<T>(T snapshot) where T : class
        {
            // 如果你在 Update 里允许强行写数据，透传即可
            _snapshot.Publish(snapshot);
        }
    }

    /// <summary>
    /// 💥 依赖追踪与保底资产管理器 (彻底为 ChartLayer 瘦身)
    /// </summary>
    internal class VisualDependencyTracker
    {
        // 💥 记忆海马体：记录上一次 Update 时，实际拿到了哪些对象 (Key: TraitId, Value: 对象的内存引用)
        // 字典只在增删键时有微小开销，预热后 0 分配
        public Dictionary<int, object?> TrackedRefs { get; } = new();

        // 把清理工作也搬过来
        public void PrepareForNewFrame()
        {
            TrackedRefs.Clear();
        }

        public bool CheckIfDirty(VisualDataBag liveLocalBag, VisualDataBag liveGlobalData)
        {
            foreach (var kvp in TrackedRefs)
            {
                int traitId = kvp.Key;
                object? lastSeenObj = kvp.Value;

                // 级联获取: 局部(Bag) > 全局(Bag) > 保底(Dictionary)
                // 全程 O(1) 极速碰撞
                object? currentObj = liveLocalBag.GetById(traitId)
                                  ?? liveGlobalData.GetById(traitId);

                // 只要内存地址变了，立刻判定为脏！
                if (!ReferenceEquals(lastSeenObj, currentObj))
                {
                    return true;
                }
            }
            return false;
        }
    }

    // 既是 DrawingVisual (物理)，也是 IChartLayer (逻辑)
    // 名字建议统一叫 ChartLayer，因为它比 VisualElement 更贴切业务
    public abstract class ChartLayer : DrawingVisual, IChartLayer
    {
        public string Name { get; set; } = string.Empty;
        public ChartLayerType Level { get; set; }
        public RenderMode Mode { get; set; } = RenderMode.Software;

        // ==========================================
        // 双缓冲机制：存的是 LayerBuffer (大礼包)
        // ==========================================
        private LayerBuffer _frontBuffer = new(); // UI 读
        private LayerBuffer _backBuffer = new();  // 后台 写
        private readonly object _lock = new();

        public bool IsDirty { get; protected set; } = false;

        internal void MarkDirty()
        {
            IsDirty = true;
        }

        internal VisualDependencyTracker DependencyTracker { get; } = new();

        // 💥 是否已经被局部唤醒并完成了第一次采样？
        internal bool IsDiscovered { get; set; } = false;

        // 接口实现：隐式转型为 RenderBuffer 基类
        public RenderBuffer Buffer
        {
            get { lock (_lock) return _frontBuffer; }
        }

        // --- 1. Update (后台录制) ---
        public void Update(IVisualData data)
        {
            // 1. 【通用特质拦截】：引擎级显隐控制
            var visibility = data.Get<VisibilityTrait>();
            if (visibility != null && !visibility.IsVisible)
            {
                // 如果被隐藏了，必须清空后备缓冲，消除残影，并重置脏标记
                lock (_lock) _backBuffer.Clear();
                IsDirty = true; // 标记脏状态，强制 ChartCell 把这层透明画面刷到前台
                return;         // 🚨 0 开销跳过，绝对不执行 OnUpdate
            }

            // 2. 传统图层脏检查
            CheckDirtyState(data);

            // 如果不脏，直接 return！保护 CPU，实现 0 开销跳过
            if (!IsDirty) return;

            LayerBuffer active;
            lock (_lock) active = _backBuffer;

            active.Clear();

            // 3. 执行纯粹的业务渲染（这里面再也没有恶心的 if(visible) 了！）
            OnUpdate(data, active.Drawing, active.Widget);
        }

        // --- 2. Swap (UI 线程同步) ---
        public void SwapBuffer()
        {
            lock (_lock)
            {
                if (IsDirty)
                {   
                    (_frontBuffer, _backBuffer) = (_backBuffer, _frontBuffer);
                }
            }
        }

        // 物理渲染上屏后，由 ChartCell 调用，清理脏标记
        internal void PostRender() => IsDirty = false;

        // 如果子类是 ReactiveChartLayer，它会通过 Bind 流来修改 IsDirty。
        protected virtual void CheckDirtyState(IVisualData data) { }
        // 【子类实现】
        // 这里是你写业务逻辑的地方，画线用 drawSink，放按钮用 widgetSink 
        protected abstract void OnUpdate(IVisualData data, IDrawingSink drawSink, WidgetBuffer widgetSink);

        // ==========================================
        // 资源释放 (Dispose Pattern)
        // ==========================================
        private bool _disposedValue;

        public void Dispose()
        {
            // 不要更改此代码。请将清理代码放入 "Dispose(bool disposing)" 方法中
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // 1. 清理其他可能的大内存引用，帮助 GC 尽早回收
                    _frontBuffer.Clear();
                    _backBuffer.Clear();
                }

                // 释放未托管的资源(未托管的对象)并重写终结器
                // 将大型字段设置为 null
                _disposedValue = true;
            }
        }
        // 【性能优化】彻底屏蔽 WPF HitTest
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
        protected override GeometryHitTestResult? HitTestCore(GeometryHitTestParameters hitTestParameters) => null;
    }
}
