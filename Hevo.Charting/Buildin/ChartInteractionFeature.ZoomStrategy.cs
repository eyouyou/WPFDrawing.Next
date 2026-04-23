using Hevo.Charting.Abstractions;
using Hevo.Charting.Features;

namespace Hevo.Charting.Buildin
{
    /// <summary>
    /// 视口跨度合法范围（已 resolve 为绝对值）。
    /// </summary>
    public readonly record struct SpanLimits(double MinSpan, double MaxSpan);

    /// <summary>
    /// 缩放上下文：策略所需的全部输入。
    /// 策略**自行负责** clamp（用 <see cref="ZoomMath.ClampSpan"/>）；HandleZoom 不再预 clamp。
    /// </summary>
    public readonly record struct ZoomContext(
        RealRange BaseRange,            // 缩放发生前的视口（来自 ActiveRange）
        double RawTargetSpan,           // 用户意图跨度（未 clamp）
        double MouseRelativeX,          // 鼠标相对画板位置；不在 [0,1] 表示鼠标在 plot 区外
        PointerHitState? HitState,      // 命中状态（命中锚定/事件加权等场景使用）
        int LogicalLength,              // 数据总长度
        SpanLimits Limits,              // 合法 Span 范围
        IScale? DomainScale,            // 时间↔索引转换；ISnappableScale 量化
        bool LeftDataExhausted,         // 左侧无更多历史可加载（"左墙已撞"）
        bool RightDataExhausted         // 右侧无更多未来数据可加载（"右墙已撞"）
    );

    /// <summary>
    /// 缩放策略契约：根据上下文产出新视口范围。
    /// 实现方负责处理 clamp、锚定、量化等所有几何决策；ViewportManager 仅做越界保险。
    /// </summary>
    public interface IZoomStrategy
    {
        RealRange Calculate(in ZoomContext ctx);
    }

    /// <summary>
    /// 缩放几何工具集：高频复用的算式抽出来作为 0-GC 静态方法。
    /// 策略可借用，也可完全自定义两端而不调用这里。
    /// </summary>
    public static class ZoomMath
    {
        /// <summary>
        /// 相对锚点缩放：保证 anchor (∈ [0,1]) 处的逻辑索引在画板上的视觉位置不变。
        /// 0 = 左缘锚定；1 = 右缘锚定；0.5 = 中心锚定。
        /// </summary>
        public static RealRange AnchoredZoom(RealRange baseRange, double targetSpan, double anchor)
        {
            double newMin = baseRange.Min + anchor * (baseRange.Span - targetSpan);
            return new RealRange(newMin, newMin + targetSpan);
        }

        /// <summary>
        /// 命中锚定缩放：保证特定逻辑索引在画板上的相对位置不变（盘后回看常用）。
        /// </summary>
        public static RealRange HitAnchoredZoom(RealRange baseRange, double targetSpan, double anchorLogicalIndex)
        {
            double anchor = baseRange.Span > 0
                ? (anchorLogicalIndex - baseRange.Min) / baseRange.Span
                : 0.5;
            return AnchoredZoom(baseRange, targetSpan, System.Math.Clamp(anchor, 0, 1));
        }

        /// <summary>
        /// 跨度夹紧：策略调用以保证返回的 Span 处于合法范围内。
        /// </summary>
        public static double ClampSpan(double rawSpan, SpanLimits limits)
            => System.Math.Clamp(rawSpan, limits.MinSpan, System.Math.Max(limits.MinSpan, limits.MaxSpan));
    }

    /// <summary>
    /// 鼠标锚点策略：缩放后鼠标指向的逻辑索引视觉位置不变。
    /// </summary>
    public sealed class MouseAnchorZoomStrategy : IZoomStrategy
    {
        public RealRange Calculate(in ZoomContext ctx)
        {
            double span = ZoomMath.ClampSpan(ctx.RawTargetSpan, ctx.Limits);
            double anchor = System.Math.Clamp(ctx.MouseRelativeX, 0, 1);
            return ZoomMath.AnchoredZoom(ctx.BaseRange, span, anchor);
        }
    }

    /// <summary>
    /// 右缘钉死策略（盯盘模式）：右边界恒定贴最新数据，缩放只挤压/拉伸左边。
    /// </summary>
    public sealed class RightEdgeZoomStrategy : IZoomStrategy
    {
        public RealRange Calculate(in ZoomContext ctx)
        {
            double span = ZoomMath.ClampSpan(ctx.RawTargetSpan, ctx.Limits);
            return ZoomMath.AnchoredZoom(ctx.BaseRange, span, 1.0);
        }
    }

    /// <summary>
    /// 智能自适应：鼠标在 plot 区内 → 鼠标锚；在 plot 区外（如 Y 轴上滚轮）→ 右缘钉死。
    /// </summary>
    public sealed class SmartAdaptiveZoomStrategy : IZoomStrategy
    {
        public RealRange Calculate(in ZoomContext ctx)
        {
            double span = ZoomMath.ClampSpan(ctx.RawTargetSpan, ctx.Limits);
            double anchor = ctx.MouseRelativeX is >= 0 and <= 1 ? ctx.MouseRelativeX : 1.0;
            return ZoomMath.AnchoredZoom(ctx.BaseRange, span, anchor);
        }
    }

    /// <summary>
    /// 左缘钉死策略：左边界恒定，缩放只挤压/拉伸右边。
    /// 适用场景：以历史起点为参照系比较收益率（"自上市以来"视图），或左侧固定基准的回测窗口。
    /// </summary>
    public sealed class LeftEdgeZoomStrategy : IZoomStrategy
    {
        public RealRange Calculate(in ZoomContext ctx)
        {
            double span = ZoomMath.ClampSpan(ctx.RawTargetSpan, ctx.Limits);
            return ZoomMath.AnchoredZoom(ctx.BaseRange, span, 0.0);
        }
    }

    /// <summary>
    /// 十字光标命中锚定：以当前命中的 bar 作为缩放锚点。
    /// 适用场景：盘后回看、复盘 —— 用户聚焦在某根 bar 时，缩放始终保持那根 bar 视觉位置不动。
    /// 无命中时退回鼠标锚（鼠标在 plot 内）或右缘（鼠标在 plot 外）。
    /// </summary>
    public sealed class CrosshairAnchorZoomStrategy : IZoomStrategy
    {
        public RealRange Calculate(in ZoomContext ctx)
        {
            double span = ZoomMath.ClampSpan(ctx.RawTargetSpan, ctx.Limits);
            if (ctx.HitState is { } hit)
                return ZoomMath.HitAnchoredZoom(ctx.BaseRange, span, hit.Hit.LogicalIndex);

            double anchor = ctx.MouseRelativeX is >= 0 and <= 1 ? ctx.MouseRelativeX : 1.0;
            return ZoomMath.AnchoredZoom(ctx.BaseRange, span, anchor);
        }
    }

    /// <summary>
    /// 方向感知策略：放大用鼠标锚（要精度），缩小用右缘（要纵览历史）。
    /// 适用场景：盯盘场景里既想"放大看细节"又想"缩小看走势"，避免每次方向切换都需要切策略。
    /// </summary>
    public sealed class DirectionalZoomStrategy : IZoomStrategy
    {
        public RealRange Calculate(in ZoomContext ctx)
        {
            double span = ZoomMath.ClampSpan(ctx.RawTargetSpan, ctx.Limits);
            bool zoomIn = span < ctx.BaseRange.Span;

            double anchor = zoomIn
                ? (ctx.MouseRelativeX is >= 0 and <= 1 ? ctx.MouseRelativeX : 1.0)
                : 1.0;

            return ZoomMath.AnchoredZoom(ctx.BaseRange, span, anchor);
        }
    }

    /// <summary>
    /// 固定窗口策略：忽略 RawTargetSpan，强制锁定为 <see cref="WindowBars"/> 根的视图。
    /// 适用场景：仪表盘 / kiosk 模式（"始终显示最近 100 根"），或对滚轮缩放彻底禁用 Span 变化。
    /// 注意：此策略下滚轮事件其实变成"无操作"——视图永远是 WindowBars 大小。
    /// </summary>
    public sealed class FixedWindowZoomStrategy : IZoomStrategy
    {
        /// <summary>窗口大小（根数）。</summary>
        public int WindowBars { get; init; } = 100;

        /// <summary>窗口对齐：RightEdge 显示最近 N 根；LeftEdge 显示最早 N 根。</summary>
        public ViewportAlignment Alignment { get; init; } = ViewportAlignment.RightEdge;

        public RealRange Calculate(in ZoomContext ctx)
        {
            double span = ZoomMath.ClampSpan(System.Math.Max(0, WindowBars - 1), ctx.Limits);
            double maxIdx = System.Math.Max(0, ctx.LogicalLength - 1);

            return Alignment == ViewportAlignment.RightEdge
                ? new RealRange(maxIdx - span, maxIdx)
                : new RealRange(0, span);
        }
    }

    /// <summary>
    /// 历史加载状态感知策略：分页期间走 <see cref="DuringPaging"/>（默认右缘钉死，让最新数据始终可见、history 从左侧流入）；
    /// 一旦左墙撞死（<see cref="ZoomContext.LeftDataExhausted"/>=true，全部历史已加载完毕），切换为左缘锚定，
    /// 防止再缩小时左侧拉伸出无数据空白，并以"自上市以来"的语义稳定停靠数据起点。
    /// 典型 K 线 UX：先请求最新 200 根 → 不断 zoom-out 拉历史 → 数据加载完毕后改为左缘锚。
    /// </summary>
    public sealed class HistoryAwareZoomStrategy : IZoomStrategy
    {
        /// <summary>
        /// 仍可继续加载历史时（左墙未撞）的缩放策略。默认 <see cref="RightEdgeZoomStrategy"/>：
        /// 滚轮缩小时最新数据保持视觉位置不变，更老的历史从左缘流入，与分页 fetch 行为天然对齐。
        /// </summary>
        public IZoomStrategy DuringPaging { get; init; } = new RightEdgeZoomStrategy();

        public RealRange Calculate(in ZoomContext ctx)
        {
            if (!ctx.LeftDataExhausted)
                return DuringPaging.Calculate(in ctx);

            // 左墙已撞：所有历史已加载完毕。锚定左缘，再缩小也不会拉出空白。
            double span = ZoomMath.ClampSpan(ctx.RawTargetSpan, ctx.Limits);
            return ZoomMath.AnchoredZoom(ctx.BaseRange, span, 0.0);
        }
    }
}
