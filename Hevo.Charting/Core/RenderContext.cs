using Hevo.Charting.Abstractions;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Hevo.Charting.Core
{
    public static class ChartLifecycle
    {
        public static readonly DependencyProperty ActiveSessionProperty =
            DependencyProperty.RegisterAttached("ActiveSession", typeof(IDisposable), typeof(ChartLifecycle),
                new PropertyMetadata(null, (d, e) =>
                {
                    (e.OldValue as IDisposable)?.Dispose();
                }));


        public static void SetActiveSession(DependencyObject element, IDisposable value)
            => element.SetValue(ActiveSessionProperty, value);

        public static IDisposable GetActiveSession(DependencyObject element)
            => (IDisposable)element.GetValue(ActiveSessionProperty);

        // 这一步是点睛之笔：确保控件死的时候，属性一定会被清空
        public static void BindTo(ChartCell cell, IDisposable session)
        {
            // 1. 设置新 Session (旧的会自动 Dispose)
            cell.SetValue(ActiveSessionProperty, session);

            // 2. 挂载一个一次性的清理器，防止重复挂载 Unloaded
            void handler(object s, RoutedEventArgs e)
            {
                cell.Unloaded -= handler;
                cell.ClearValue(ActiveSessionProperty); // 这里触发最终的 Dispose
            }

            cell.Unloaded += handler;
        }
    }

    /// <summary>
    /// 该 Session 生命周期与 <see cref="ChartCell"/> 的Load和UnLoaded 绑定
    /// </summary>
    // 修复 §B.2.4（方案 B 最小合并）：ChartSession 实现 IDisposableHost，
    // 让 `.DisposeWith(session)` 与 `.DisposeWith(feature)` 走同一契约。
    // Feature / Schema 的 IDisposableHost 实现保留独立，scope 分离不变——
    // Feature 级订阅仍随 Decompose 释放，不会被升级到 chart 级泄漏（参见 §B.2.5）。
    public sealed class ChartSession : IDisposable, IDisposableHost
    {
        private readonly List<Action> _cleanups = new();
        private readonly CancellationTokenSource _cts = new();

        // 暴露给 Pipeline 使用的取消令牌
        public CancellationToken Token => _cts.Token;

        // 1. 你提议的包装：处理成对的事件或操作
        public void Watch<T>(T element, Action<T> subscribe, Action<T> unsubscribe) where T : notnull
        {
            subscribe(element);
            _cleanups.Add(() => unsubscribe(element));
        }

        // 2. 简易包装：直接处理已经写好的 Action
        public void AddCleanup(Action cleanup) => _cleanups.Add(cleanup);

        // 3. IDisposableHost 契约实现（chart 级生命周期回收）
        public void RegisterDisposable(IDisposable disposable)
        {
            if (disposable != null) _cleanups.Add(disposable.Dispose);
        }

        // 4. 自动回收 IDisposable 对象（如 Timer, Brush 等）——RegisterDisposable 的语法糖，保留返回值链式
        public T Own<T>(T disposable) where T : IDisposable
        {
            RegisterDisposable(disposable);
            return disposable;
        }

        public void Dispose()
        {
            // 先触发取消信号，停止所有正在跑的 Pipeline
            _cts.Cancel();
            _cts.Dispose();

            // 倒序执行清理逻辑（符合入栈出栈顺序，更安全）
            for (int i = _cleanups.Count - 1; i >= 0; i--)
            {
                try { _cleanups[i](); }
                catch { /* 屏蔽清理过程中的异常，确保清理完 */ }
            }
            _cleanups.Clear();
        }
    }

    public static class SessionExtensions
    {
        public static void Bind(this ChartSession session, Action subscribe, Action unsubscribe)
        {
            subscribe();
            session.AddCleanup(unsubscribe);
        }
    }



    // 定义一个轻量级的任务包
    public readonly struct RenderTask
    {
        public readonly IChartLayer Layer;
        public readonly IVisualData DataSnapshot; // 快照数据

        public RenderTask(IChartLayer layer, IVisualData data)
        {
            Layer = layer;
            DataSnapshot = data;
        }
    }

    public partial class RenderContext : IDisposable
    {
        // ==========================================
        // 1. 事务草稿箱 (隔离层)
        // ==========================================

        // 局部草稿：Layer -> 该图层本次的变更集
        private readonly Dictionary<IChartLayer, VisualDataBag> _transactionMap = new();

        // 全局草稿：本次流程中对全局属性的变更集
        private readonly VisualDataBag _globalDraft = new();

        // ==========================================
        // 2. 真实数据引用 (存储层)
        // ==========================================

        private readonly VisualDataBag _liveGlobalData;
        private readonly ConditionalWeakTable<IChartLayer, VisualDataBag> _liveLocalMap;

        /// <summary>
        /// 挂载在当前上下文中的生命周期 Session
        /// </summary>
        public ChartSession? Session { get; }

        public RenderContext(
            VisualDataBag globalData,
            ConditionalWeakTable<IChartLayer, VisualDataBag> localDataMap,
            ChartSession? session = null)
        {
            _liveGlobalData = globalData;
            _liveLocalMap = localDataMap;
            Session = session;
        }

        // ==========================================
        // 3. 核心流式 API (获取遥控器)
        // ==========================================

        /// <summary>
        /// 获取特定图层的操作代理。
        /// 所有的修改都会先进入私有的草稿箱。
        /// </summary>
        public VisualProxy<TLayer> For<TLayer>(TLayer layer) where TLayer : IChartLayer
        {
            // 1. 获取或创建该图层的"草稿袋"
            if (!_transactionMap.TryGetValue(layer, out var draftBag))
            {
                draftBag = new VisualDataBag();
                _transactionMap[layer] = draftBag;
            }

            // 2. 获取该图层的"实时袋" (作为读取基准)
            var liveLocal = _liveLocalMap.GetValue(layer, _ => new VisualDataBag());

            // 3. 返回代理：读 Live，写 Draft
            return new VisualProxy<TLayer>(layer, liveLocal, draftBag);
        }

        /// <summary>
        /// 获取全局共享属性的操作代理。
        /// 修改全局配置（如主题、坐标系）会存入全局草稿箱。
        /// </summary>
        public VisualProxy<IVisualData> Shared()
        {
            // 基准使用实时的全局袋子，写入使用全局草稿袋
            return new VisualProxy<IVisualData>(_liveGlobalData, _liveGlobalData, _globalDraft);
        }

        // ==========================================
        // 4. 事务提交 API (UI 线程执行)
        // ==========================================

        /// <summary>
        /// 【核心】提交事务并同步更新图层。
        /// 将所有草稿合并到真实存储，并根据变更情况标脏图层，最后触发 Layer.Update。
        /// </summary>
        public void SubmitSync(IEnumerable<IChartLayer> layers)
        {
            // 1. 合并全局变更
            bool globalChanged = !_globalDraft.IsEmpty;
            if (globalChanged) _liveGlobalData.CommitFrom(_globalDraft);

            // 2. 极速判脏循环
            foreach (var layer in layers)
            {
                if (layer is not ChartLayer cl) continue;

                var liveLocalBag = _liveLocalMap.GetValue(cl, _ => new VisualDataBag());
                bool hasLocalDraft = _transactionMap.TryGetValue(cl, out var draftBag) && !draftBag.IsEmpty;

                // 如果有局部数据提交，合并到它的 Live 库
                if (hasLocalDraft) liveLocalBag.CommitFrom(draftBag);

                // 如果已经脏了，就不用继续查了
                if (cl.IsDirty) continue;

                // ==========================================
                // 💥 策略 A：针对【深度睡眠】图层 (局部驱动唤醒)
                // ==========================================
                if (!cl.IsDiscovered)
                {
                    // 第一帧哪怕尺寸变了它都不管。只有专属于它的局部数据到了，它才会被“点醒”。
                    if (hasLocalDraft) cl.MarkDirty();
                    continue;
                }

                // ==========================================
                // 💥 策略 B：针对【工作状态】图层 (O(1) 引用指纹碰撞)
                // ==========================================
                if (cl.DependencyTracker.CheckIfDirty(liveLocalBag, _liveGlobalData))
                {
                    cl.MarkDirty();
                }
            }

            ClearTransactions();
        }

        private void ClearTransactions()
        {
            // 调用之前写好的 0 GC 清理方法
            foreach (var draft in _transactionMap.Values)
            {
                draft.Clear();
            }
            _globalDraft.Clear();
        }

        /// <summary>
        /// 为多线程渲染准备任务快照。
        /// 返回的 RenderTask 包含不可变的级联透镜。
        /// </summary>
        public FrameSnapshot PrepareTasks(IReadOnlyList<IChartLayer> layers)
        {
            int count = layers.Count;

            // 1. 借出全局快照
            var globalSnap = _liveGlobalData.RentSnapshot();

            // 2. 借出 Map 数组 (用于支持子图层寻址)
            var layerMap = ArrayPool<KeyValuePair<IChartLayer, object[]>>.Shared.Rent(count);

            // 3. 实例化帧上下文
            var frame = new FrameSnapshot(globalSnap, layerMap, count);

            // 4. 组装任务
            for (int i = 0; i < count; i++)
            {
                var layer = layers[i];
                var liveLocal = _liveLocalMap.GetValue(layer, _ => new VisualDataBag());

                // 借出局部快照
                var localSnap = liveLocal.RentSnapshot();

                // 登记到寻址地图中
                layerMap[i] = new KeyValuePair<IChartLayer, object[]>(layer, localSnap);

                // 生成带有寻址能力的级联透镜
                var view = new CascadingVisualData(localSnap, globalSnap, frame);
                frame.Tasks.Add(new RenderTask(layer, view));
            }

            return frame;
        }

        public void Dispose()
        {
            ClearTransactions();
            GC.SuppressFinalize(this);
        }
    }


    /// <summary>
    /// 轻量级 0 GC 代理遥控器。
    /// 负责生成新快照、替换旧指针，并自动触发脏重绘。
    /// </summary>
    /// <summary>
    /// 轻量级数据操作代理。
    /// 实现了“基于基准读，写入草稿箱”的逻辑。
    /// </summary>
    public readonly struct VisualProxy<TTarget>
    {
        public TTarget Target { get; }

        private readonly IVisualData _baseData;  // 读基准 (Live Data)
        private readonly IVisualData _draftData; // 写目标 (Transaction Draft)

        internal VisualProxy(TTarget target, IVisualData baseData, IVisualData draftData)
        {
            Target = target;
            _baseData = baseData;
            _draftData = draftData;
        }

        /// <summary>
        /// 【新增】读取当前视角的最新数据。
        /// 规则：优先读取当前事务的草稿(_draftData)，如果没有，再回退读取历史真实数据(_baseData)
        /// </summary>
        public TData? Read<TData>() where TData : class
        {
            return _draftData.Get<TData>() ?? _baseData.Get<TData>();
        }

        /// <summary>
        /// 基于老快照修改，生成新快照原子替换。
        /// </summary>
        /// <param name="modifier">
        /// 修改器函数。
        /// 入参：旧数据（可能是 null，表示之前未配置）。
        /// 返回：新数据（必须非 null）。
        /// </param>
        internal VisualProxy<TTarget> UpdateData<TData>(Func<TData?, TData> modifier)
            where TData : class
        {
            // 1. 优先从草稿箱找（同一个事务内可能改了多次）
            // 2. 其次从基准数据找（上一帧的数据）
            // 3. 都没有就是 null
            var oldData = _draftData.Get<TData>() ?? _baseData.Get<TData>();

            // 执行修改器。调用者负责处理 null 的情况 (通常使用 ?? 操作符)
            var newData = modifier(oldData);

            // 写入草稿
            _draftData.Publish(newData);
            return this;
        }

        /// <summary>
        /// 全量覆盖新数据快照。
        /// </summary>
        internal VisualProxy<TTarget> PublishData<TData>(TData snapshot) where TData : class
        {
            _draftData.Publish(snapshot);
            return this;
        }
    }

}
