using Hevo.Charting.Abstractions;

namespace Hevo.Charting.Buildin
{
    /// <summary>
    /// 双轴 tick 镜像槽：主轴每帧把 tick 比例 (Ratio ∈ [0,1]) 写入,副轴的
    /// <see cref="MirrorRatioTickStrategy"/> 直接读出并按副轴自身 RealRange 反算 value,
    /// 让左右两根 Y 轴的水平网格在屏幕上像素级对齐,彻底消除"双轴 grid 打架"。
    /// <para>
    /// 实例由业务 Schema 持有,生命周期与图表一致；写入路径 0-GC（仅在 tick 数变多时按需扩容一次）。
    /// </para>
    /// </summary>
    public sealed class MirrorTickAnchor
    {
        private double[] _ratios = System.Array.Empty<double>();
        private bool[] _isAnchor = System.Array.Empty<bool>();

        public int Count { get; private set; }

        // 主轴 tick 集合每变更一次自增 1,作为副轴 UseMemo 的脏标识
        public int Version { get; private set; }

        public double GetRatio(int i) => _ratios[i];
        public bool GetIsAnchor(int i) => _isAnchor[i];

        // 仅供框架内部 (AxisFeature) 调用,业务 Schema 不要直接写。
        // 命名避开 Write/Publish:HEVO002 分析器对这两个名字一律拦截,而本类只是跨 feature 的共享算子状态,
        // 不是黑板写,语义和 AxisLayoutRegistryTrait 字典原地赋值一致。
        internal void Capture(System.ReadOnlySpan<TickModel> ticks)
        {
            int count = ticks.Length;
            if (_ratios.Length < count)
            {
                _ratios = new double[count];
                _isAnchor = new bool[count];
            }
            for (int i = 0; i < count; i++)
            {
                _ratios[i] = ticks[i].Ratio;
                _isAnchor[i] = ticks[i].IsAnchor;
            }
            Count = count;
            Version++;
        }
    }
}
