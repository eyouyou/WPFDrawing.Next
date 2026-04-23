using Hevo.Charting.Abstractions;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Core
{
    public enum FeaturePhase
    {
        // 修复 H4：将原本的魔数 (FeaturePhase)(-5) 替换为具名枚举值。
        // PreLayout 保证交互引擎在布局计算前率先确定视口与碰撞状态。
        // 预留 [-10, 0) 区间供未来插入其他前置阶段，不影响现有排序。
        PreLayout = -10,  // 交互前置（视口/碰撞预算）
        Layout = 0,       // 算地盘
        Scale = 100,      // 算刻度
        Series = 200,     // 画图表（默认）
        Interaction = 300 // 画交互
    }

    // ==========================================
    // 💥 终极统一通道接口定义
    // ==========================================
    public interface IMemo<T>
    {
        T Value { get; }
        bool IsChanged { get; }
    }

    public interface IState<T> : IMemo<T>
    {
        new T Value { get; set; }
    }

    // ==========================================
    // 💥 引擎大管家：FeatureContext 
    // 热路径（每帧重绘）绝对零分配，全部复用已有对象！
    // ==========================================
    public partial class FeatureContext
    {
        internal RenderContext _renderCtx = null!;
        internal DataBlackboard _board = null!;
        internal SubscriptionRegistry _registry = null!;
        internal ChartFeature _currentFeature = null!;

        // 这里的 _isFullPass 实际上是由外部 ReactiveSchema 传入的 isEnvironmentSync
        // 代表环境纪元是否发生了改变（如 SizeChanged）
        internal bool _isFullPass;

        internal int _hookCursor = 0;
        internal int _expectedHookCount = -1;
        internal readonly Dictionary<string, object> _dynamicStatePocket = new();

        // 💥 终极修复：彻底废弃易溢出的 _portTicks，换成 0-GC 的无限版本令牌！
        internal readonly Dictionary<object, VersionToken> _portTokens = new();

        internal void BeginProject(RenderContext render, DataBlackboard board) { _renderCtx = render; _board = board; _hookCursor = 0; }
        internal void EndProject()
        {
            if (_expectedHookCount == -1) _expectedHookCount = _hookCursor;
            else if (_hookCursor != _expectedHookCount)
                throw new InvalidOperationException("[Hevo 致命错误] Hook 游标错位！");
        }

        // 修复 H1 配套：Decompose 时清空跨帧状态。
        // 同一 Feature 实例可能被 Transact 重新装配（hot-plug），残留的 _portTokens / _hookPocket 会
        // 让重装后的首帧脏检查认为"已对齐"而跳过重绘，因此卸载时必须显式归零。
        internal void Reset()
        {
            _portTokens.Clear();
            _hookPocket.Clear();
            _dynamicStatePocket.Clear();
            _expectedHookCount = -1;
            _hookCursor = 0;
            _isInsideHookFactory = false;
            _renderCtx = null!;
            _board = null!;
            _registry = null!;
            _currentFeature = null!;
        }

        // ==========================================
        // 💥 图层与共享状态代理
        // ==========================================
        public VisualProxy<IVisualData> Shared() => _renderCtx.Shared();
        public VisualProxy<IChartLayer> For(IChartLayer layer) => _renderCtx.For(layer);

        /// <summary>
        /// 💥 隐式查脏核心：值类型防抖，不生成任何对象，仅返回 struct Tuple
        /// </summary>
        // ⚠️ 已知脏追踪盲区：
        //   本方法只追踪 DataPort 的 VersionToken，不覆盖：
        //     - ctx.Shared().Read<TTrait>() 读取的 Trait（ViewportSizeTrait / PlotAreaTrait / ScaleStrategyTrait 等）
        //     - flow.Watch / flow.WatchAsync 自带的端口订阅（另一套独立机制）
        //   若未来出现类似 "某 Feature 读了 Trait 但没标脏导致残影" 的现象，
        //   定点修（给该 Feature 补 UsePort，或补一个 UseTrait<T>() API）。
        public (T Value, bool IsChanged) UsePort<T>(DataPort<T> port)
        {
            // 1. 隐式订阅：将当前 Feature 登记为引脚观察者
            if (_currentFeature != null && _registry != null) _registry.Subscribe(port, _currentFeature);

            // 2. 0-GC 拉取物理数据
            T val = _board.Read(port);

            // 3. 拉取该引脚在黑板上的最新纪元令牌
            VersionToken currentToken = _board.GetVersion(port);
            bool changed = false;

            // 4. 💥 架构级防御：使用 VersionToken 对齐版本！
            // 只用 != 判断，彻底免疫长周期运行带来的整数溢出风险
            if (!_portTokens.TryGetValue(port, out var lastToken) || lastToken != currentToken)
            {
                changed = true;
                _portTokens[port] = currentToken; // 更新本地记录，对齐最新纪元
            }

            // 5. 环境突变拦截：如果外部（如 SizeChanged）要求全量重绘
            // 则强制将本帧获取的所有数据视为“变脏”，以触发下游彻底重新计算投影
            if (_isFullPass) changed = true;

            return (val, changed);
        }

        /// <summary>
        /// 💥 动态批量引脚订阅 (专治 HEVO003 循环拦截)
        /// 允许安全地订阅一组引脚，数据直接写入 buffer，全程 0-GC。
        /// </summary>
        public bool UsePorts<T>(DataPort<T>[] ports, T[] buffer)
        {
            if (ports.Length != buffer.Length)
                throw new ArgumentException("Ports 数组和 Buffer 数组的长度必须一致！");

            bool changed = false;

            for (int i = 0; i < ports.Length; i++)
            {
                var port = ports[i];

                // 1. 隐式订阅
                if (_currentFeature != null && _registry != null)
                    _registry.Subscribe(port, _currentFeature);

                // 2. 0-GC 物理拉取
                buffer[i] = _board.Read(port);

                // 3. 对齐 VersionToken 防抖查脏
                VersionToken currentToken = _board.GetVersion(port);
                if (!_portTokens.TryGetValue(port, out var lastToken) || lastToken != currentToken)
                {
                    changed = true;
                    _portTokens[port] = currentToken;
                }
            }

            // 环境突变拦截
            if (_isFullPass) changed = true;

            return changed;
        }

        public HevoRect PlotArea
        {
            get
            {
                return _renderCtx.GetPlotArea();
            }
        }
    }


    // ==========================================
    // 💥 图表特征基类：生命周期管线与内存管家！
    // ==========================================
    public abstract class ChartFeature : ChartAspect, IDisposableHost, Abstractions.IPausable
    {
        public string InstanceId { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        private readonly List<IChartLayer> _managedLayers = new();

        // 💥 内存泄漏终结者：专门存放与图表生命周期绑定的 Rx 句柄
        private readonly List<IDisposable> _disposables = new();

        public virtual FeaturePhase Phase => FeaturePhase.Series;

        // ==========================================
        // 💥 Phase 11 / §H：IPausable 生命周期（与 ReactiveSchema 对称）
        // 95% 的 Feature（LineSeriesFeature / BarSeriesFeature / AxisFeature 等纯投影器）
        // 继承基类 no-op 行为即可。有内部定时器 / WPF 订阅要屏蔽的（ChartInteractionFeature 等）
        // 重载 OnSuspend / OnResume。Schema 级冻结会级联调用每个 Feature 的 Suspend。
        // ==========================================
        public bool IsActive { get; private set; } = true;

        public void Suspend()
        {
            if (!IsActive) return;
            IsActive = false;
            OnSuspend();
            for (int i = 0; i < _disposables.Count; i++)
                if (_disposables[i] is Abstractions.IPausable p) p.Suspend();
        }

        public void Resume()
        {
            if (IsActive) return;
            IsActive = true;
            for (int i = 0; i < _disposables.Count; i++)
                if (_disposables[i] is Abstractions.IPausable p) p.Resume();
            OnResume();
        }

        /// <summary>
        /// 子类重载以冻结 Feature 级内部状态（WPF 事件订阅屏蔽、定时器取消等）。
        /// 基类会自动处理 `_disposables` 中的 <see cref="Abstractions.IPausable"/>，无需重复。
        /// </summary>
        protected virtual void OnSuspend() { }

        /// <summary>
        /// 子类重载以解冻 Feature 级内部状态。
        /// </summary>
        protected virtual void OnResume() { }

        // 💥 顶层视口（L6 / §B.2.6）：由宿主 ReactiveSchema.Add 自动注入。
        // 业务 Feature 直接用 this.Viewport 即可，外部装配 API 无需再传 vp。
        // 罕见的双 X 轴场景：在 Feature 初始化块里显式 `new Feature { Viewport = customVp }`，
        // ReactiveSchema.Add 识别到非 null 值时不覆盖。
        // 用 plain `set` 而非 `init`，保证 Add(...) 能在构造后补注入。
        public ViewportPorts Viewport { get; set; } = null!;

        internal ChartCell Chart { get; private set; } = null!;
        internal ReactiveSchema Schema { get; private set; } = null!;
        private readonly FeatureContext _context = new();

        internal DataBlackboard? CurrentBoard => Schema.CurrentBoard;

        public sealed override void Compose(ChartCell chart, RenderContext ctx) { /* 封禁原本的虚方法 */ }

        internal IRenderFlow<DataBlackboard> InternalCompose(ChartCell chart, RenderContext ctx, ReactiveSchema schema, IRenderFlow<DataBlackboard> flow)
        {
            Chart = chart;
            Schema = schema;
            ComposeCore(chart, ctx);
            using var composeScope = FeatureComposeScope.Enter(flow);
            OnCompose(chart, ctx, flow);
            return FeatureComposeScope.Current!.Build();
        }

        public void Project(RenderContext ctx, DataBlackboard board, SubscriptionRegistry registry, bool isFullPass)
        {
#if DEBUG
            using var scope = DevTools.TopologyTracer.EnterScope(this);
#endif

            // 修复 H1：复用持久化的 _context 实例，不再每帧 new FeatureContext。
            //
            // 原代码的错误：每帧 new FeatureContext 会使 _portTokens 字典归零，
            // 导致 UsePort 里 TryGetValue 永远返回 false，changed 永远为 true，
            // 脏检查机制彻底失效，每帧触发全量重绘。
            //
            // 正确做法：只刷新帧级可变的调度字段，让 _portTokens 跨帧存活。
            // BeginProject 已负责重置 _hookCursor（Hook 游标），无需担心重入问题。
            _context._registry = registry;
            _context._currentFeature = this;
            _context._isFullPass = isFullPass;

            _context.BeginProject(ctx, board);
            OnProject(_context);
            _context.EndProject();
        }

        protected virtual void ComposeCore(ChartCell chart, RenderContext ctx) { }

        protected IWorkflow<(TEvent Event, DataBlackboard Board)> WithBoard<TEvent>(IWorkflow<TEvent> source)
        {
            return new WorkflowEngine<(TEvent Event, DataBlackboard Board)>((onNext, onError) =>
            {
                return source.Subscribe(
                    e =>
                    {
                        var board = CurrentBoard;
                        if (board == null) return;
                        onNext((e, board));
                    },
                    onError);
            });
        }

        protected abstract void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow);
        protected abstract void OnProject(FeatureContext ctx);

        protected VisualProxy<TLayer> AttachLayer<TLayer>(TLayer layer) where TLayer : IChartLayer
        {
            if (!layer.Name.EndsWith(InstanceId))
            {
                layer.Name = $"{layer.Name}_{InstanceId}";
            }
            if (!_managedLayers.Contains(layer)) _managedLayers.Add(layer);
            return Chart.AddUnmanagedLayer(layer);
        }

        /// <summary>
        /// 💥 暴露给 DisposeWith 的生命周期注册接口
        /// </summary>
        public void RegisterDisposable(IDisposable disposable)
        {
            if (disposable != null && !_disposables.Contains(disposable))
            {
                _disposables.Add(disposable);
            }
        }

        public sealed override void Decompose(ChartCell chart, RenderContext ctx)
        {
            // 1. 清理图层
            foreach (var layer in _managedLayers)
            {
                chart.RemoveUnmanagedLayer(layer);
            }
            _managedLayers.Clear();

            // 2. 💥 彻底斩断流式订阅，防止内存泄漏！
            foreach (var d in _disposables)
            {
                d.Dispose();
            }
            _disposables.Clear();

            // 3. 修复 H1 配套：清空跨帧状态。Transact 热插拔后再装配的同一实例必须以"全脏"姿态进入首帧。
            _context.Reset();
        }
    }

    // ==========================================
    // 💥 Rx 流的生命周期绑定扩展
    // ==========================================
    public static class RenderFlowLifecycleExtensions
    {
        /// <summary>
        /// 💥 将当前 Rx 流的生命周期强制绑定到指定的 Feature 上！
        /// Feature 被卸载时，自动 Dispose 该流，绝不泄漏内存。
        /// </summary>
        public static IRenderFlow<T> DisposeWith<T>(this IRenderFlow<T> source, ChartFeature feature)
        {
            var intercepted = new WorkflowEngine<T>((next, error) =>
            {
                // 执行实际的订阅并拿到句柄
                var token = source.Subscribe(next, error);

                // 将句柄托管给 Feature 的黑洞口袋
                feature.RegisterDisposable(token);

                return token;
            });
            // 重新穿上马甲，保持图表上下文
            return intercepted.BindTo(source.Chart);
        }
    }

    public static class HookExtensions
    {
        // 💥 魔法 1：双元组解构 -> (Value, IsChanged)
        // 适用于绝大多数只读数据拉取和 Memo
        public static void Deconstruct<T>(this IMemo<T> memo, out T value, out bool isChanged)
        {
            value = memo.Value;
            isChanged = memo.IsChanged;
        }

        // 💥 魔法 2：三元组解构 -> (Value, SetValue, IsChanged)
        // 适用于需要双向绑定的内部 UI 状态 (类似 React)
        public static void Deconstruct<T>(this IState<T> state, out T value, out Action<T> setValue, out bool isChanged)
        {
            value = state.Value;
            setValue = v => state.Value = v;
            isChanged = state.IsChanged;
        }
    }
}
