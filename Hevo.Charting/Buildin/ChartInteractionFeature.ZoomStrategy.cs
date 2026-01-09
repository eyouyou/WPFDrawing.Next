using Hevo.Charting.Abstractions;
using Hevo.Charting.Features;

namespace Hevo.Charting.Buildin
{
    // =========================================================
    // 💥 1. 缩放物理上下文 (纯栈内存，绝对 0-GC)
    // 删除了 MaxLogicalLength，因为策略层不需要（也严禁）知道物理边界！
    // =========================================================
    public readonly record struct ZoomContext(
        RealRange BaseRange,       // 缩放发生前的量程基准 (UserRange 或 ActiveRange)
        double TargetSpan,         // 用户意图缩放后的绝对跨度
        PointerHitState? HitState, // 当前十字光标的全局吸附状态
        double MouseRelativeX      // 鼠标在画板中的物理百分比位置 (0.0 ~ 1.0)
    );

    // =========================================================
    // 💥 2. 缩放策略底层契约
    // =========================================================
    public interface IZoomStrategy
    {
        RealRange Calculate(ZoomContext ctx);
    }

    // =========================================================
    // 💥 3. 基础原子策略 A：基于鼠标物理百分比的几何锚点缩放
    // =========================================================
    public class MouseAnchorZoomStrategy : IZoomStrategy
    {
        public RealRange Calculate(ZoomContext ctx)
        {
            // 💥 纯初中几何推导，极其优雅：
            // 新的起点 = 老的起点 + 相对百分比 * (老跨度 - 新跨度)
            // 保证缩放后，鼠标指向的逻辑索引在屏幕上的物理坐标绝对不变！
            double newMin = ctx.BaseRange.Min + ctx.MouseRelativeX * (ctx.BaseRange.Span - ctx.TargetSpan);

            return new RealRange(newMin, newMin + ctx.TargetSpan);
        }
    }

    // =========================================================
    // 💥 4. 基础原子策略 B：基于最新数据的右侧钉死缩放 (盯盘模式)
    // =========================================================
    public class RightEdgeZoomStrategy : IZoomStrategy
    {
        public RealRange Calculate(ZoomContext ctx)
        {
            // 无论鼠标在哪里，图表的右边界永远钉死在当前的 Max 处，只能向左挤压或拉伸
            double newMax = ctx.BaseRange.Max;
            double newMin = newMax - ctx.TargetSpan;

            return new RealRange(newMin, newMax);
        }
    }

    // =========================================================
    // 💥 5. 智能路由策略 (Smart Router)
    // =========================================================
    public class SmartAdaptiveZoomStrategy : IZoomStrategy
    {
        // 预先实例化好基础策略，严格遵守 0-GC 红线
        private readonly IZoomStrategy _mouseAnchorStrategy = new MouseAnchorZoomStrategy();
        private readonly IZoomStrategy _rightEdgeStrategy = new RightEdgeZoomStrategy();

        public RealRange Calculate(ZoomContext ctx)
        {
            // 只要鼠标在绘图主区域内，统统围绕鼠标的物理百分比缩放
            if (ctx.MouseRelativeX >= 0 && ctx.MouseRelativeX <= 1.0)
            {
                return _mouseAnchorStrategy.Calculate(ctx);
            }

            // 如果鼠标在外部（例如右侧的 Y轴 价格刻度区滚轮），默认采用贴死最新价的盯盘缩放
            return _rightEdgeStrategy.Calculate(ctx);
        }
    }
}
