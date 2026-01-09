
using Hevo.Charting.Abstractions;
using System.Windows;

namespace Hevo.Charting.Core
{
    public enum ChartUnitType
    {
        Pixel, // 写死绝对像素
        Auto,  // 根据内容测量动态计算
        Star   // 瓜分剩余空间 (比例)
    }

    /// <summary>
    /// 💥 工业级布局长度契约
    /// </summary>
    public readonly struct ChartLength
    {
        public double Value { get; }
        public ChartUnitType UnitType { get; }
        public double MinValue { get; }
        private ChartLength(double value, ChartUnitType type, double minValue = 0)
        {
            Value = value;
            UnitType = type;
            MinValue = minValue;
        }

        public static ChartLength Pixel(double pixels) => new(pixels, ChartUnitType.Pixel);
        public static ChartLength Auto() => new(0, ChartUnitType.Auto);
        public static ChartLength Star(double weight, double minPixels = 0) => new(weight, ChartUnitType.Star, minPixels);
        // 语法糖：直接写 60 就是 Pixel(60)
        public static implicit operator ChartLength(double pixels) => Pixel(pixels);
    }

    // =================================================================
    // 💥 2. 0-GC 极速网格布局引擎 (支持物理底线防御版)
    // 内部计算死守 double 精度，防止像素拼缝误差！
    // =================================================================
    public static class GridLayoutEngine
    {
        public static void Calculate(
            double totalSpace,
            ReadOnlySpan<ChartLength> definitions,
            Func<int, double>? autoMeasurer,
            Span<double> outputSizes)
        {
            outputSizes.Clear();

            // 💥 0-GC 标记数组：记录哪些列的尺寸已经“尘埃落定”
            Span<bool> finalized = stackalloc bool[definitions.Length];

            double remainingSpace = totalSpace;
            double totalStars = 0;

            // ==========================================
            // 第一遍：处理硬性霸占 (Pixel) 和统计总权重
            // ==========================================
            for (int i = 0; i < definitions.Length; i++)
            {
                var def = definitions[i];
                if (def.UnitType == ChartUnitType.Pixel)
                {
                    outputSizes[i] = def.Value;
                    remainingSpace -= def.Value;
                    finalized[i] = true;
                }
                else if (def.UnitType == ChartUnitType.Star)
                {
                    totalStars += def.Value;
                }
                else if (def.UnitType == ChartUnitType.Auto)
                {
                    double measuredSize = autoMeasurer?.Invoke(i) ?? 0.0;
                    outputSizes[i] = measuredSize;
                    remainingSpace -= measuredSize;
                    finalized[i] = true;
                }
            }

            // ==========================================
            // 第二遍：💥 Star 保底争夺战 (动态平衡循环)
            // ==========================================
            bool reallocated;
            do
            {
                reallocated = false;
                double currentTotalStars = totalStars;
                double currentRemaining = Math.Max(0, remainingSpace);

                for (int i = 0; i < definitions.Length; i++)
                {
                    var def = definitions[i];
                    if (def.UnitType == ChartUnitType.Star && !finalized[i])
                    {
                        // 理论应得空间
                        double allocated = currentRemaining * (def.Value / currentTotalStars);

                        // 💥 关键判定：如果理论空间被挤压到了底线以下
                        if (allocated <= def.MinValue)
                        {
                            outputSizes[i] = def.MinValue;         // 强制吃掉保底空间
                            remainingSpace -= def.MinValue;        // 💥 从公共池子里扣除该空间！让其他兄弟缩水！
                            totalStars -= def.Value;               // 💥 退出后续瓜分游戏！
                            finalized[i] = true;
                            reallocated = true; // 剩下的池子变小了，可能导致其他原本安全的列也跌破底线，必须重算！
                        }
                    }
                }
            } while (reallocated && totalStars > 0);

            // ==========================================
            // 第三遍：分配剩余空间给幸存的 Star
            // ==========================================
            remainingSpace = Math.Max(0, remainingSpace);
            if (totalStars > 0)
            {
                for (int i = 0; i < definitions.Length; i++)
                {
                    var def = definitions[i];
                    if (def.UnitType == ChartUnitType.Star && !finalized[i])
                    {
                        outputSizes[i] = remainingSpace * (def.Value / totalStars);
                        finalized[i] = true;
                    }
                }
            }
        }
    }

    public readonly struct LayoutResult3x3
    {
        // 原始列/行尺寸保留 double，方便外部如果需要绝对高精度的偏移计算
        public readonly double Col0, Col1, Col2;
        public readonly double Row0, Row1, Row2;

        // 💥 完美契合新底座：向外暴露给视觉层的是纯净的 HevoRect
        public readonly HevoRect PlotArea;
        public readonly bool IsValid;

        public LayoutResult3x3(ReadOnlySpan<double> cols, ReadOnlySpan<double> rows)
        {
            Col0 = cols[0]; Col1 = cols[1]; Col2 = cols[2];
            Row0 = rows[0]; Row1 = rows[1]; Row2 = rows[2];

            // 💥 在边界交付处：将 double 安全降维至 float，0-GC，极致丝滑
            PlotArea = new HevoRect((float)cols[0], (float)rows[0], (float)cols[1], (float)rows[1]);
            IsValid = PlotArea.Width > 0 && PlotArea.Height > 0;
        }
    }

    // =================================================================
    // 💥 3. 渲染代理扩展 (透传 MinValue 语法糖)
    // =================================================================
    public static class ChartLayoutExtensions
    {
        public static LayoutResult3x3 ExecuteGrid3x3Layout(
            this VisualProxy<IVisualData> proxy,
            ReadOnlySpan<ChartLength> colDefs,
            ReadOnlySpan<ChartLength> rowDefs,
            Func<int, double>? measureColAuto = null,
            Func<int, double>? measureRowAuto = null)
        {
            var viewport = proxy.Read<ViewportSizeTrait>();

            // 💥 防线 1：如果视口本身就不合法，立刻广播清空！
            if (viewport == null || viewport.Width <= 0 || viewport.Height <= 0)
            {
                // 💥 全局替换为 HevoRect.Empty
                proxy.PublishData(new PlotAreaTrait(HevoRect.Empty));
                return default;
            }

            Span<double> colSizes = stackalloc double[colDefs.Length];
            Span<double> rowSizes = stackalloc double[rowDefs.Length];

            GridLayoutEngine.Calculate(viewport.Width, colDefs, measureColAuto, colSizes);
            GridLayoutEngine.Calculate(viewport.Height, rowDefs, measureRowAuto, rowSizes);

            var result = new LayoutResult3x3(colSizes, rowSizes);

            // ==========================================
            // 💥 核心修复：绝对禁止脏数据残留！
            // 如果布局坍缩 (IsValid = false)，必须主动发布一个 Empty 区域，
            // 通知所有下游的 Layer 和 Feature 停止使用上一帧的旧尺寸！
            // ==========================================
            if (result.IsValid)
            {
                // result.PlotArea 现在已经是 HevoRect 了，完美匹配！
                proxy.PublishData(new PlotAreaTrait(result.PlotArea));
            }
            else
            {
                // 💥 彻底干掉 Rect.Empty，换成 HevoRect.Empty！
                proxy.PublishData(new PlotAreaTrait(HevoRect.Empty));
            }

            return result;
        }

        /// <summary>
        /// 💥 极简 3x3 布局重载：增加 minPlotWidth 和 minPlotHeight 参数！
        /// </summary>
        public static LayoutResult3x3 ExecuteGridLayout(
            this VisualProxy<IVisualData> proxy,
            ChartLength left, ChartLength right,
            ChartLength top, ChartLength bottom,
            double minPlotWidth = 0, double minPlotHeight = 0) // 💥 扩展参数
        {
            // 💥 将物理底线注入到 Star 中
            Span<ChartLength> cols = stackalloc ChartLength[3] { left, ChartLength.Star(1, minPlotWidth), right };
            Span<ChartLength> rows = stackalloc ChartLength[3] { top, ChartLength.Star(1, minPlotHeight), bottom };

            return proxy.ExecuteGrid3x3Layout(cols, rows);
        }
    }
}
