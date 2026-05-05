using Hevo.Charting.Abstractions;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 💥 分类标尺 (CategoryScale)：图形学归一化的终极大脑
    /// 完美抹平了“折线点映射(Edge)”与“柱状图波段映射(Centered)”的物理差异。
    /// 所有的图层(Layer)只需无脑调用 Normalize，即可获得绝对对齐的物理坐标！
    ///
    /// 可选 <paramref name="SnapEdges"/>：开启后 ChartInteractionFeature 的 Pan/Zoom 会把视口边界量化到整数索引，
    /// 用户拖拽时绝不会出现边缘半根 K 线被切的情况（ratcheting 行为，跨过一根才走一步）。
    /// </summary>
    public record CategoryScale(double Offset = 0.5, bool SnapEdges = false) : IScale, ISnappableScale
    {
        // ==========================================
        // 💥 预设标尺模式 (静态单例，0-GC)
        // ==========================================

        /// <summary>
        /// 柱状图/K线/混合图专用：数据点落在格子正中心 (左右各推移 0.5 个物理格子)
        /// </summary>
        public static readonly CategoryScale Centered = new(0.5);

        /// <summary>
        /// 柱状图/K线 + 整根对齐：拖拽/缩放时边缘永远不出现半根被切（推荐用于 bar/candle 图）。
        /// </summary>
        public static readonly CategoryScale CenteredSnapped = new(0.5, SnapEdges: true);

        /// <summary>
        /// 纯折线图/分时图专用：数据点死死钉在格子边缘 (0 偏移，首尾绝对贴边)
        /// </summary>
        public static readonly CategoryScale Edge = new(0.0);

        /// <summary>
        /// 纯折线图/分时图专用 + 整根对齐：数据点死死钉在格子边缘 (0 偏移，首尾绝对贴边)
        /// </summary>
        public static readonly CategoryScale EdgeSnapped = new(0.0, SnapEdges: true);

        // ==========================================
        // ISnappableScale：量化到整数索引（仅当 SnapEdges 开启时生效）
        // ==========================================
        public bool SnapEnabled => SnapEdges;
        public double Snap(double logicalValue) => SnapEdges ? Math.Round(logicalValue) : logicalValue;

        // ==========================================
        // 💥 核心抽象：万能归一化引擎
        // ==========================================

        /// <summary>
        /// 将逻辑索引映射为 0.0 ~ 1.0 的屏幕物理百分比。
        /// 
        /// [核心数学公式 - 万能归一化] :
        /// Normalize(v) = (v - v_min + Offset) / (v_span + 2 * Offset)
        /// 
        /// - 分子 (+ Offset)：将逻辑坐标系向右平移，实现居中对齐。
        /// - 分母 (+ 2 * Offset)：根据两端留白，动态扩张物理网格总数。
        /// </summary>
        public double Normalize(double value, RealRange range)
        {
            // 分母扩张：计算包含容差的物理网格总跨度
            double expandedSpan = range.Span + (2 * Offset);

            // 终极兜底防 0 除暴毙
            if (expandedSpan <= 0) return 0;

            // 分子偏移：计算当前值在扩张跨度中的绝对百分比
            return (value - range.Min + Offset) / expandedSpan;
        }

        /// <summary>
        /// 反向推演：将屏幕百分比 (0.0 ~ 1.0) 反推回逻辑索引。
        /// 
        /// [核心数学公式 - 逆向求解] :
        /// Denormalize(p) = p * (v_span + 2 * Offset) + v_min - Offset
        /// 
        /// 架构用途：用于十字光标 (Crosshair) 拾取鼠标位置时，精准算出当前悬浮在第几根 K 线上！
        /// </summary>
        public double Denormalize(double normalValue, RealRange range)
        {
            double expandedSpan = range.Span + (2 * Offset);
            return (normalValue * expandedSpan) + range.Min - Offset;
        }
    }
}
