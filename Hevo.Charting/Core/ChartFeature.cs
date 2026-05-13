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
    // 💥 引擎大管家:FeatureContext
    // 热路径(每帧重绘)绝对零分配,全部复用已有对象!
    // ==========================================
    public partial class FeatureContext
    {
        internal RenderContext _renderCtx = null!;
        internal DataBlackboard _board = null!;
        internal SubscriptionRegistry _registry = null!;
        internal Feature _currentFeature = null!;

        // 这里的 _isFullPass 实际上是由外部 ReactiveSchema 传入的 isEnvironmentSync
        // 代表环境纪元是否发生了改变(如 SizeChanged)
        internal bool _isFullPass;

        internal int _hookCursor = 0;
        internal int _expectedHookCount = -1;
        internal readonly Dictionary<string, object> _dynamicStatePocket = new();

        // 💥 终极修复:彻底废弃易溢出的 _portTicks,换成 0-GC 的无限版本令牌!
        internal readonly Dictionary<object, VersionToken> _portTokens = new();

        internal void BeginProject(RenderContext render, DataBlackboard board) { _renderCtx = render; _board = board; _hookCursor = 0; }
        internal void EndProject()
        {
            if (_expectedHookCount == -1) _expectedHookCount = _hookCursor;
            else if (_hookCursor != _expectedHookCount)
                throw new InvalidOperationException("[Hevo 致命错误] Hook 游标错位!");
        }

        // 修复 H1 配套:Decompose 时清空跨帧状态。
        // 同一 Feature 实例可能被 Transact 重新装配(hot-plug),残留的 _portTokens / _hookPocket 会
        // 让重装后的首帧脏检查认为"已对齐"而跳过重绘,因此卸载时必须显式归零。
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
        /// 泛型重载:保留具体 layer 类型,让强类型 <c>PublishData&lt;TLayer, TData&gt;</c> 在跨程序集
        /// (如 Hevo.Charting.PythonNet 内 PyPlotFeature)调用时仍能命中正确的 IConsumes&lt;TData&gt; 约束。
        /// 业务侧 typed call: <c>ctx.For&lt;LineLayer&gt;(_lineLayer).PublishData(new XAxisTrait(...))</c>。
        /// </summary>
        public VisualProxy<TLayer> For<TLayer>(TLayer layer) where TLayer : IChartLayer
            => _renderCtx.For(layer);

        /// <summary>
        /// 💥 隐式查脏核心:值类型防抖,不生成任何对象,仅返回 struct Tuple
        /// </summary>
        // ⚠️ 已知脏追踪盲区:
        //   本方法只追踪 DataPort 的 VersionToken,不覆盖:
        //     - ctx.Shared().Read<TTrait>() 读取的 Trait(ViewportSizeTrait / PlotAreaTrait / ScaleStrategyTrait 等)
        //     - flow.Watch / flow.WatchAsync 自带的端口订阅(另一套独立机制)
        //   若未来出现类似 "某 Feature 读了 Trait 但没标脏导致残影" 的现象,
        //   定点修(给该 Feature 补 UsePort,或补一个 UseTrait<T>() API)。
        public (T Value, bool IsChanged) UsePort<T>(
            DataPort<T> port,
            [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(port))] string? portExpression = null)
        {
            // 端口非空契约:UsePort 的语义("订阅 + 读 + 防抖")在 port=null 下没有合法路径。
            // 提前 fail-fast 替代 board.Read 内深处的 NRE,堆栈直接定位到出错的 feature + 字段。
            // CallerArgumentExpression 自动捕获 caller 写的字面量(`ctx.UsePort(DataPort)` → "DataPort"),
            // 业务侧 0 改动;不预设 caller 是哪种 port 来源(init / 局部变量 / 动态生成)。
            if (port is null)
                throw new ArgumentNullException(
                    portExpression,
                    $"{_currentFeature?.GetType().Name ?? "<feature>"}.{portExpression} is null — " +
                    "ctx.UsePort 不接受 null 端口,调用前确保端口已装配。");

            // 1. 0-GC 拉取物理数据
            T val = _board.Read(port);

            // 2. 拉取该引脚在黑板上的最新纪元令牌
            VersionToken currentToken = _board.GetVersion(port);
            bool changed = false;

            // 3. 💥 架构级防御:用 VersionToken 对齐版本 + 顺手做"首次访问"探测
            //    隐式 Subscribe 只在 Feature 首次见到 port 时跑一次,跨帧稳态零 lock / 零 dict 改动。
            //    Decompose 走 _context.Reset() 会清空 _portTokens,Transact 重装后首帧自动重新订阅。
            if (!_portTokens.TryGetValue(port, out var lastToken))
            {
                // 首次:登记订阅 + 落桩 token
                if (_currentFeature != null && _registry != null) _registry.Subscribe(port, _currentFeature);
                _portTokens[port] = currentToken;
                changed = true;
            }
            else if (lastToken != currentToken)
            {
                _portTokens[port] = currentToken;
                changed = true;
            }

            // 4. 环境突变拦截:如果外部(如 SizeChanged)要求全量重绘
            // 则强制将本帧获取的所有数据视为"变脏",以触发下游彻底重新计算投影
            if (_isFullPass) changed = true;

            return (val, changed);
        }

        /// <summary>
        /// 💥 动态批量引脚订阅 (专治 HEVO003 循环拦截)
        /// 允许安全地订阅一组引脚,数据直接写入 buffer,全程 0-GC。
        /// </summary>
        public bool UsePorts<T>(
            DataPort<T>[] ports,
            T[] buffer,
            [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(ports))] string? portsExpression = null)
        {
            if (ports.Length != buffer.Length)
                throw new ArgumentException("Ports 数组和 Buffer 数组的长度必须一致!");

            bool changed = false;

            for (int i = 0; i < ports.Length; i++)
            {
                var port = ports[i];

                // 数组扇入:逐元素 null check,定位到具体索引,中性 message 不预设来源。
                if (port is null)
                    throw new ArgumentNullException(
                        $"{portsExpression}[{i}]",
                        $"{_currentFeature?.GetType().Name ?? "<feature>"}.{portsExpression}[{i}] is null — " +
                        "ctx.UsePorts 不接受数组中含 null 的端口,调用前确保所有端口已装配。");

                // 0-GC 物理拉取
                buffer[i] = _board.Read(port);

                // 顺手用 _portTokens 是否存在条目作为"首次访问"信号 —— 首次才做隐式订阅。
                VersionToken currentToken = _board.GetVersion(port);
                if (!_portTokens.TryGetValue(port, out var lastToken))
                {
                    if (_currentFeature != null && _registry != null)
                        _registry.Subscribe(port, _currentFeature);
                    _portTokens[port] = currentToken;
                    changed = true;
                }
                else if (lastToken != currentToken)
                {
                    _portTokens[port] = currentToken;
                    changed = true;
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
    // 💥 Chart 业务特化 feature 基类。
    // 通用 reactive 基础设施(layer / lifecycle / IDisposable / IPausable / Phase / IsSingleton /
    // FeatureContext / OnCompose+flow / OnProject / Project 帧入口 / 嵌套子 feature 等)全部由
    // <see cref="Feature"/> 基类承担(Phase 2/Route A);本类只补一项 chart 专属概念:Viewport
    // (1D 量程,LogicalLength + UserRange + ActiveRange,概念上不适用于 graph editor 等 2D 场景)。
    // ==========================================
    public abstract class ChartFeature : Feature
    {
        // 顶层视口(L6 / §B.2.6):ComposeCore 阶段从 ChartCell attached property 一次性注入字段,
        // 后续业务 Feature 直接读 this.Viewport.X 是字段访问,不走 WPF DP。
        //
        // 时序保证:
        //   InitializeRegistry: EnsureBaseFeatures(挂 PortsFeature) → DefineFeatures(业务挂各 ChartFeature)
        //                       → Decorate(LinkedMaster/Pane 阶段 mutate PortsFeature.Ports = SharedViewport,
        //                          setter 内部 SetAttached 同步更新 ChartCell 上的 attached property)
        //   BuildAndActivatePipeline: 按 Phase 序遍历 _orderedFeatures,调 InternalCompose → ComposeCore →
        //                             ★ 此时 attached ports 已是最终值,字段一次注入即可
        //   ⇒ Decorate 之后再没人 mutate Ports;字段方案不会拿到过时引用。
        //
        // 错误时机访问保护:在 ComposeCore 之前(罕见:子类在 ctor/OnAttached 内提前读)抛 InvalidOperation,
        // 而非 NRE,定位清晰。

        private ViewportPorts? _viewport;

        /// <summary>
        /// 当前 schema 的 viewport ports。InternalCompose 之后稳定可用;之前 access 抛 InvalidOperation。
        /// </summary>
        public ViewportPorts Viewport => _viewport
            ?? throw new InvalidOperationException(
                $"{GetType().Name}.Viewport 在 ComposeCore 之前被访问。" +
                "ChartFeature 应仅在 OnCompose / OnProject / Watch 回调中读 viewport,不要在 ctor / OnAttached 中读。");

        protected override void ComposeCore(ChartCell chart, RenderContext ctx)
        {
            base.ComposeCore(chart, ctx);
            // Decorate 已跑过,attached ports 此时是最终值(LinkedMaster/Pane 的 SharedViewport 或独立 schema 的私 ports)。
            _viewport = ViewportPorts.RequireAttached(chart);
        }

        protected override void OnDecompose()
        {
            base.OnDecompose();
            // Transact 热插拔场景:同一 feature 实例 Decompose 后可能再次 Add → 再次 ComposeCore 重新注入。
            // 这里 reset null 让"未注入就读"的协议错误能被 throwing getter 捕获。
            _viewport = null;
        }
    }

    // ==========================================
    // 💥 Rx 流的生命周期绑定扩展
    // ==========================================
    public static class RenderFlowLifecycleExtensions
    {
        /// <summary>
        /// 💥 将当前 Rx 流的生命周期强制托管给指定的 Feature。
        /// Feature 被卸载时自动 Dispose 该流,且 Suspend / Resume 期间正确级联——绝不泄漏内存。
        /// </summary>
        public static IRenderFlow<T> OwnedBy<T>(this IRenderFlow<T> source, Feature feature)
        {
            var intercepted = new WorkflowEngine<T>((next, error) =>
            {
                // 执行实际的订阅并拿到句柄
                var token = source.Subscribe(next, error);

                // 将句柄托管给 Feature 的黑洞口袋
                feature.RegisterDisposable(token);

                return token;
            });
            // 重新穿上马甲,保持图表上下文
            return intercepted.BindTo(source.Chart);
        }
    }

    public static class HookExtensions
    {
        // 💥 魔法 1:双元组解构 -> (Value, IsChanged)
        // 适用于绝大多数只读数据拉取和 Memo
        public static void Deconstruct<T>(this IMemo<T> memo, out T value, out bool isChanged)
        {
            value = memo.Value;
            isChanged = memo.IsChanged;
        }

        // 💥 魔法 2:三元组解构 -> (Value, SetValue, IsChanged)
        // 适用于需要双向绑定的内部 UI 状态 (类似 React)
        public static void Deconstruct<T>(this IState<T> state, out T value, out Action<T> setValue, out bool isChanged)
        {
            value = state.Value;
            setValue = v => state.Value = v;
            isChanged = state.IsChanged;
        }
    }
}
