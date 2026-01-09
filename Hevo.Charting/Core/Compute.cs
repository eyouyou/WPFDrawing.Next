
namespace Hevo.Charting.Core
{
    // ==========================================
    // 💥 核心契约：序列变换 (Sequence Transform)
    // 描述：所有金融指标（均线、MACD、差分）的终极数学基类
    // ==========================================
    public interface ISequenceTransform
    {
        /// <summary>
        /// 执行序列变换计算
        /// </summary>
        /// <param name="source">原始数据的连续内存段 (只读)</param>
        /// <param name="target">目标结果的连续内存段 (可写)</param>
        void Transform(ReadOnlySpan<double> source, Span<double> target);
    }
}
