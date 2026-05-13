using System;
using System.Windows;
using System.Windows.Input;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// 行式 element-plot 一等 Feature 基类 —— 把 ScatterPlotFeature / ArrowMarkerFeature 重复的
    /// hover/双击订阅 + spec 缓存 + hit-test 调度收口。派生类填:
    /// <list type="bullet">
    ///   <item><see cref="CreateLayer"/>:实例化对应的 layer 类型</item>
    ///   <item><see cref="PublishSpecTrait"/>:OnProject 时把 ROM&lt;TSpec&gt; 包成 trait publish 给自己的 layer</item>
    ///   <item><see cref="ProjectToScreen"/>:把单个 spec 元素投到屏幕坐标</item>
    /// </list>
    /// 鼠标订阅 + ElementHovered/ElementDoubleClicked 事件 + <see cref="HitStatePort"/> 写入完全在基类。
    /// 事件 args 用 <see cref="PlotElementEvent{TSpec}"/> 强类型携带 spec 元素本身(struct 零分配),
    /// 框架不解释字段 — 业务侧用 Index 反查自家数据。
    ///
    /// <para>
    /// <b>跟 <see cref="TooltipWidgetFeature{TX}"/> 联动</b>:配置 <see cref="HitStatePort"/> 后,基类在 hover 命中时
    /// 写入 <see cref="PointerHitState"/>(<c>LocalIndex = element idx</c>, <c>MousePos = 命中屏幕点</c>),离开时写
    /// <c>IsOutOfBounds=true</c>。Tooltip 的 <c>HitStatePort</c> 接到这个端口即可显示 tooltip;layer 侧 publish 的
    /// <see cref="MetaTrait"/> + <see cref="DoubleSeriesDataTrait"/> 自动被 tooltip 抓取成行(field name + value)。
    /// </para>
    /// </summary>
    public abstract class ElementPlotFeatureBase<TSpec, TLayer> : ChartFeature
        where TSpec : struct
        where TLayer : ChartLayer
    {
        public override FeaturePhase Phase => FeaturePhase.Series;

        /// <summary>spec 输入端口 —— 上游 Feature 写,Feature OnProject 读 + publish 给 layer。</summary>
        public DataPort<ReadOnlyMemory<TSpec>> Spec { get; init; } = null!;

        /// <summary>
        /// 可选 hit state 输出端口 —— 配置后,hover 命中时基类写 <c>PointerHitState{LocalIndex=idx, MousePos=hit}</c>;
        /// 离开时写 <c>IsOutOfBounds=true</c> 的状态。<see cref="TooltipWidgetFeature{TX}"/> 可订阅这个端口。
        /// 不需要 tooltip 的场景留 null。
        /// </summary>
        [PortDirection(PortDirection.Output)]
        public DataPort<PointerHitState?>? HitStatePort { get; init; }

        /// <summary>
        /// Feature identifier —— 业务侧反查实例用(<c>schema.ListFeatures().OfType&lt;ScatterPlotFeature&gt;().First(f =&gt; f.Name == "...")</c>)。
        /// </summary>
        public string Name { get; init; } = "";

        /// <summary>底层 layer 名(调试用)。</summary>
        public string LayerName { get; init; } = "";

        /// <summary>Hover 命中容差(像素²)。默认 16²。</summary>
        public double HoverHitToleranceSq { get; init; } = 16.0 * 16.0;

        /// <summary>Hover 命中事件。args 携带强类型 spec 元素 + Index,业务侧自管 hover UI(WPF popup 等)。</summary>
        public event EventHandler<PlotElementEvent<TSpec>>? ElementHovered;

        /// <summary>双击命中事件(<c>MouseLeftButtonDown ClickCount==2</c>)。</summary>
        public event EventHandler<PlotElementEvent<TSpec>>? ElementDoubleClicked;

        // ── runtime ─────────────────────────────────────────────
        private TLayer? _layer;
        private VisualProxy<TLayer> _proxy;

        private readonly object _specLock = new();
        private ReadOnlyMemory<TSpec> _cachedSpec;
        private int _lastHoverIdx = -1;   // 上一帧命中 idx,用于"hit→no-hit"边界写 out-of-bounds 状态

        protected TLayer? Layer => _layer;
        protected VisualProxy<TLayer> Proxy => _proxy;

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            if (Spec == null) return;

            _layer = CreateLayer();
            _proxy = AttachLayer(_layer);

            OnAfterAttachLayer(ctx);
            HookMouse(chart, ctx);
        }

        protected override void OnProject(FeatureContext ctx)
        {
            if (_layer == null) return;
            var (spec, _) = ctx.UsePort(Spec);
            lock (_specLock) _cachedSpec = spec;
            // ⚠️ 不能用 OnCompose 时缓存的 _proxy —— ChartCell.AddUnmanagedLayer 返回的 proxy 是
            // "直写 live bag" 模式(base=draft=同一 localBag),Publish 跳过 RenderContext._transactionMap
            // 的 transaction draft,SubmitSync 时不会标 layer dirty → layer.OnUpdate 不被调 → 不渲染。
            // ctx.For(_layer) 返回的才是 base=live + draft=transaction 的正确 proxy,Publish 走 transaction,
            // SubmitSync 合并 draft 时检测到 trait 变化 → MarkDirty → 下一帧 OnUpdate 被调。
            PublishSpecTrait(ctx.For(_layer), spec);
        }

        // ── 派生类钩子 ────────────────────────────────────────────────────

        /// <summary>实例化具体 layer。</summary>
        protected abstract TLayer CreateLayer();

        /// <summary>把当前 spec 包成 trait publish 给 layer。</summary>
        protected abstract void PublishSpecTrait(VisualProxy<TLayer> proxy, ReadOnlyMemory<TSpec> spec);

        /// <summary>
        /// 把单个 spec 元素投到屏幕。返 null = 该元素被裁(出 plot area / NaN);非 null = 屏幕点。
        /// </summary>
        protected abstract HevoPoint? ProjectToScreen(RenderContext ctx, in TSpec elem);

        /// <summary>OnCompose layer attach 后调,派生类可在此 publish 一次性 layer-local trait(显式 axes 等)。</summary>
        protected virtual void OnAfterAttachLayer(RenderContext ctx) { }

        // ── 鼠标 / hit-test(完全通用)─────────────────────────────────────────

        private void HookMouse(ChartCell chart, RenderContext ctx)
        {
            // hover:命中 → fire ElementHovered + 写 HitStatePort(命中);no-hit → 写 HitStatePort(IsOutOfBounds=true)。
            // 用 WithBoard 拿到 board 才能 WriteIfChanged HitStatePort。
            WithBoard(this.OnMouse(UIElement.MouseMoveEvent)).Subscribe(t =>
            {
                if (_layer == null) return;
                var (args, board) = t;
                var pos = args.GetPosition(chart.DrawingCanvas);
                var (idx, hit) = HitTest(ctx, new HevoPoint((float)pos.X, (float)pos.Y));

                if (idx >= 0)
                {
                    ElementHovered?.Invoke(this, new PlotElementEvent<TSpec>(idx, GetCachedElement(idx)));
                    WriteHitState(board, new PointerHitState(
                        MousePos: hit,
                        Hit: default,
                        CenterX: hit.X,
                        LocalIndex: idx,
                        IsOutOfBounds: false));
                    _lastHoverIdx = idx;
                }
                else if (_lastHoverIdx >= 0)
                {
                    // 上次命中,这次 no-hit → 下发"已离开" 状态让 tooltip 隐藏
                    WriteHitState(board, new PointerHitState(
                        MousePos: new HevoPoint((float)pos.X, (float)pos.Y),
                        Hit: default,
                        CenterX: (float)pos.X,
                        LocalIndex: -1,
                        IsOutOfBounds: true));
                    _lastHoverIdx = -1;
                }
            }).OwnedBy(this);

            // 鼠标离开 chart 整体 → 强制下发 out-of-bounds(覆盖 hover 残留)
            WithBoard(this.OnMouse(UIElement.MouseLeaveEvent)).Subscribe(t =>
            {
                if (_layer == null || _lastHoverIdx < 0) return;
                var (_, board) = t;
                WriteHitState(board, new PointerHitState(
                    MousePos: default,
                    Hit: default,
                    CenterX: 0,
                    LocalIndex: -1,
                    IsOutOfBounds: true));
                _lastHoverIdx = -1;
            }).OwnedBy(this);

            this.OnUIEvent<MouseButtonEventArgs>(UIElement.MouseLeftButtonDownEvent).Subscribe(args =>
            {
                if (_layer == null || args.ClickCount != 2) return;
                var pos = args.GetPosition(chart.DrawingCanvas);
                var (idx, _) = HitTest(ctx, new HevoPoint((float)pos.X, (float)pos.Y));
                if (idx < 0) return;
                ElementDoubleClicked?.Invoke(this, new PlotElementEvent<TSpec>(idx, GetCachedElement(idx)));
            }).OwnedBy(this);
        }

        private void WriteHitState(DataBlackboard board, PointerHitState? state)
        {
            if (HitStatePort == null) return;
            using (board.AcquireWriteLock())
                board.WriteIfChanged(HitStatePort, state);
        }

        private (int Index, HevoPoint Hit) HitTest(RenderContext ctx, HevoPoint screen)
        {
            ReadOnlyMemory<TSpec> spec;
            lock (_specLock) spec = _cachedSpec;
            if (spec.Length == 0) return (-1, default);

            var span = spec.Span;
            int bestIdx = -1;
            double bestDist = HoverHitToleranceSq;
            var bestHit = default(HevoPoint);
            for (int i = 0; i < span.Length; i++)
            {
                var p = ProjectToScreen(ctx, in span[i]);
                if (p == null) continue;
                double dx = p.Value.X - screen.X, dy = p.Value.Y - screen.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestDist) { bestDist = d2; bestIdx = i; bestHit = p.Value; }
            }
            return (bestIdx, bestHit);
        }

        private TSpec GetCachedElement(int idx)
        {
            ReadOnlyMemory<TSpec> spec;
            lock (_specLock) spec = _cachedSpec;
            return (uint)idx < (uint)spec.Length ? spec.Span[idx] : default;
        }
    }
}
