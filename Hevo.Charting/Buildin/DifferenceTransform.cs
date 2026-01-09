using Hevo.Charting.Core;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Hevo.Charting.Buildin
{
    // ==========================================
    // 💡 官方实现 1：一阶差分变换 (支持 AVX2 硬件级 SIMD 加速！)
    // ==========================================
    public class DifferenceTransform : ISequenceTransform
    {
        private readonly int _period;

        public DifferenceTransform(int period = 1)
        {
            _period = period;
        }

        public void Transform(ReadOnlySpan<double> source, Span<double> target)
        {
            int len = source.Length;
            if (len == 0) return;

            int i = _period;

            // 💥 修正 1：对于 double，每次处理 4 个元素，所以边界预留 4 个位置
            if (Avx.IsSupported && len >= 4 + _period)
            {
                unsafe
                {
                    // 固定内存指针，彻底绕过 C# 数组的边界检查
                    fixed (double* pSrc = source, pDst = target)
                    {
                        // 💥 修正 2：安全边界限制，保证最后一次读取不会越界
                        int limit = len - 4;

                        // 💥 修正 3：步长改为 4！(因为 Vector256<double> 只能装 4 个)
                        for (; i <= limit; i += 4)
                        {
                            // 硬件并行加载 (JIT 会自动发射 vmovupd 非对齐加载指令，安全防崩溃)
                            Vector256<double> curr = Avx.LoadVector256(pSrc + i);
                            Vector256<double> prev = Avx.LoadVector256(pSrc + i - _period);

                            // 硬件并行减法，并写回目标内存
                            Avx.Store(pDst + i, Avx.Subtract(curr, prev));
                        }
                    }
                }
            }

            // ==========================================
            // 处理尾部不足一个 SIMD 向量宽度的零头数据
            // ==========================================
            for (; i < len; i++)
            {
                target[i] = source[i] - source[i - _period];
            }

            // ==========================================
            // 处理头部的无效数据 (如 period=1 时，第 0 个元素无法与前面的数做差)
            // 填充 NaN，完美迎合管线断点机制
            // ==========================================
            for (int j = 0; j < _period; j++)
            {
                target[j] = double.NaN;
            }
        }
    }
}
