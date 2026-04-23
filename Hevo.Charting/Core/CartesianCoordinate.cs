using Hevo.Charting.Abstractions;
using System.Windows;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 承载图表整体可用视区大小的特质
    /// </summary>
    public record ViewportSizeTrait(double Width, double Height) : IVisualTrait;
    // =================================================================
    // 1. 彻底正交的坐标系特质 (ECS Components)
    // =================================================================

    /// <summary>
    /// 物理绘制区域 (通常由 ChartCell 的 SizeChanged 触发并挂载在 Shared)
    /// </summary>
    public record PlotAreaTrait(HevoRect Area) : IVisualTrait
    {
        public static readonly PlotAreaTrait Default = new(HevoRect.Empty);
    }

    /// <summary>
    /// X 轴逻辑视口 (通常挂载在 Shared，供所有常规图层共享)
    /// </summary>
    public record XAxisTrait(RealRange Viewport, RealRange World = default) : IVisualTrait
    {
        public static readonly XAxisTrait Default = new(RealRange.Empty, RealRange.Empty);
    }

    /// <summary>
    /// Y 轴逻辑视口 (通常挂载在特定的 Layer 上，实现同屏多轨 Y 轴)
    /// </summary>
    public record YAxisTrait(RealRange Viewport) : IVisualTrait
    {
        public static readonly YAxisTrait Default = new(RealRange.Empty);
    }

    /// <summary>
    /// 全局比例尺策略：彻底剥离几何 X/Y 概念，回归数学本质
    /// </summary>
    public record ScaleStrategyTrait(
        IScale DomainScale, // 自变量比例尺（原 XScale，如：时间、K线索引、类别）
        IScale ValueScale   // 因变量比例尺（原 YScale，如：价格、成交量、MACD值）
    ) : IVisualTrait
    {
        // 经典折线图模式：两边都是连续的线性映射，对齐边缘
        public static readonly ScaleStrategyTrait Default = new(CategoryScale.Edge, new LinearScale());

        // 经典 K 线模式：自变量强制带有 +0.5 的居中偏移，因变量是线性映射
        public static readonly ScaleStrategyTrait CandleMode = new(CategoryScale.Centered, new LinearScale());

        // 对数 K 线模式：自变量居中偏移，因变量是对数映射 (适合查看长期趋势)
        public static readonly ScaleStrategyTrait LogCandleMode = new(CategoryScale.Centered, new LogarithmicScale());
    }

    // =================================================================
    // 3. 坐标系交互逻辑库 (保持原样，纯数学算法)
    // =================================================================
    public static class CoordinateLogic
    {
        public static RealRange Pan(RealRange current, double delta)
        {
            if (current.Span <= 0) return current;
            return new RealRange(current.Min + delta, current.Max + delta);
        }

        public static RealRange Zoom(RealRange current, double pivot, double factor)
        {
            if (current.Span <= 0) return current;
            double newSpan = current.Span * factor;
            double ratio = (pivot - current.Min) / current.Span;
            double newMin = pivot - (newSpan * ratio);
            double newMax = newMin + newSpan;
            return new RealRange(newMin, newMax);
        }

        public static RealRange Clamp(RealRange proposed, RealRange worldLimits)
        {
            if (worldLimits.Span <= 0) return proposed;

            double min = proposed.Min;
            double max = proposed.Max;
            double span = max - min;

            if (span > worldLimits.Span)
            {
                min = worldLimits.Min;
                max = worldLimits.Max;
            }
            else if (span < double.Epsilon)
            {
                span = 1.0;
                max = min + span;
            }

            if (min < worldLimits.Min)
            {
                min = worldLimits.Min;
                max = min + span;
            }
            if (max > worldLimits.Max)
            {
                max = worldLimits.Max;
                min = max - span;
            }

            return new RealRange(min, max);
        }
    }

    // =================================================================
    // 4. 坐标系投影映射扩展 (解耦版)
    // 职责：输入散装的范围，输出像素。彻底与 Trait 的组装方式解耦。
    // =================================================================
    public static class CoordinateExtensions
    {
        /// <summary>
        /// 💥 [核心咽喉] 将逻辑坐标（自变量，因变量）投射到屏幕物理坐标。
        /// 内部计算保持 double 绝对精度，仅在出口进行 0-GC 的 float 降维。
        /// </summary>
        public static HevoPoint ProjectToScreen(
            HevoRect area,
            RealRange rangeDomain,
            RealRange rangeValue,
            ScaleStrategyTrait axis,
            double logicDomain,
            double logicValue)
        {
            if (area.Width <= 0 || area.Height <= 0) return new HevoPoint(0, 0);

            // 1. 纯数学计算保持 double 绝对精度
            double dNorm = axis.DomainScale.Normalize(logicDomain, rangeDomain);
            double vNorm = axis.ValueScale.Normalize(logicValue, rangeValue);

            // 2. 算完之后，在最后一步安全强制转换为 float
            return new HevoPoint(
                (float)(area.Left + (dNorm * area.Width)),
                (float)(area.Top + (area.Height * (1.0 - vNorm))) // Y轴翻转逻辑
            );
        }
        /// <summary>
        /// 仅投射因变量（Value）到屏幕高度坐标
        /// </summary>
        public static float ProjectValueToScreen(HevoRect area, RealRange rangeValue, ScaleStrategyTrait axis, double logicValue)
        {
            if (area.Height <= 0) return 0f;
            double vNorm = axis.ValueScale.Normalize(logicValue, rangeValue);
            return (float)(area.Top + (area.Height * (1.0 - vNorm)));
        }

    }

    // =================================================================
    // 5. 渲染上下文扩展 (业务端流式 API)
    // =================================================================
    public static class CoordinateContextExtensions
    {
        /// <summary>
        /// 💥 [供 Feature 使用] 更新全局 X 轴 (所有 Layer 默认共享)。
        /// 优雅调用: ctx.Shared().UpdateSharedXAxis(range);
        /// </summary>
        public static void UpdateSharedXAxis(
            this VisualProxy<IVisualData> proxy,
            RealRange viewport,
            RealRange world = default)
        {
            // 如果没传 world 范围，就默认和 viewport 一样大
            var actualWorld = world.IsEmpty ? viewport : world;

            // 直接面向当前的 Proxy 对象发布更新指令！
            proxy.UpdateData<XAxisTrait>(c =>
                (c ?? XAxisTrait.Default) with { Viewport = viewport, World = actualWorld }
            );
        }
        /// <summary>
        /// 💥 [供 Feature 使用] 更新私有 Y 轴 (绑定给特定的 Layer)。
        /// 优雅调用: ctx.For(_layer).UpdateYAxis(range);
        /// </summary>
        public static void UpdateYAxis<Layer>(
            this VisualProxy<Layer> proxy,
            RealRange viewport) where Layer : IChartLayer
        {
            // 彻底杜绝了传错 layer 参数的可能，面向对象编程的巅峰美学！
            proxy.UpdateData<YAxisTrait>(c =>
                (c ?? YAxisTrait.Default) with { Viewport = viewport }
            );
        }
    }

    public static class LayoutWorkflowExtensions
    {
        /// <summary>
        /// 💥 监听尺寸变化 -> 计算 3x3 -> 更新 PlotAreaTrait -> 返回流以供触发 Project
        /// </summary>
        public static IWorkflow<PlotAreaTrait> ObservePlotArea(
                    this ChartCell chart, // 必须派生自 FrameworkElement
                    ChartLength left = default,
                    ChartLength right = default,
                    ChartLength top = default,
                    ChartLength bottom = default)
        {
            return chart.OnRoutedEvent<SizeChangedEventArgs>(FrameworkElement.SizeChangedEvent)
                .Where(e => e.NewSize.Width > 0 && e.NewSize.Height > 0)
                .Select(e =>
                {
                    // 1. 💥 既然没有闭包限制，直接在 Lambda 里分配 Span
                    // 中间列/行 固定补齐 1* (Star)
                    var star = ChartLength.Star(1);

                    Span<ChartLength> colDefs = stackalloc ChartLength[3] { left, star, right };
                    Span<ChartLength> rowDefs = stackalloc ChartLength[3] { top, star, bottom };

                    Span<double> colSizes = stackalloc double[3];
                    Span<double> rowSizes = stackalloc double[3];

                    // 2. 💥 调用 4 参数的 Calculate 核心
                    GridLayoutEngine.Calculate(e.NewSize.Width, colDefs, null, colSizes);
                    GridLayoutEngine.Calculate(e.NewSize.Height, rowDefs, null, rowSizes);

                    // 3. 💥 提取纯净绘图区 [1, 1]，并将 double 安全降维为 float！
                    return new PlotAreaTrait(new HevoRect(
                        (float)colSizes[0],
                        (float)rowSizes[0],
                        (float)colSizes[1],
                        (float)rowSizes[1]));
                })
                .Do(plotArea =>
                {
                    // 4. 💥 静默写黑板，发布更新推演
                    chart.GetSharedData().Publish(plotArea);
                });
        }
    }
}