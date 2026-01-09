namespace Hevo.Charting.LowCode
{
    /// <summary>
    /// 万能泛型列：可以装载任何类型 (DateTime, Enum, struct 等)
    /// </summary>
    public readonly struct Column<T>
    {
        // 🚨 核心改变：对外暴露的不再是裸数组 double[]，而是切片 ArraySegment
        public readonly ArraySegment<T> Values;

        public int Count => Values.Count; // 永远等于你传入的真实有效长度

        public Column(T[] rawArray, int count)
        {
            // 在这一步，0 GC 截取前 count 个有效数据！彻底屏蔽 ArrayPool 的脏尾巴
            Values = new ArraySegment<T>(rawArray, 0, count);
        }
    }

    /// <summary>
    /// 富数字列：把数组、长度、极值高内聚封装在一起。
    /// struct 结构体，0 GC 分配。
    /// </summary>
    public readonly struct NumericColumn
    {
        public readonly ArraySegment<double> Values;

        public readonly double Max;
        public readonly double Min;

        public int Count => Values.Count;

        public NumericColumn(double[] rawArray, int count, double max, double min)
        {
            // 0 GC 截断！
            Values = new ArraySegment<double>(rawArray, 0, count);
            Max = max;
            Min = min;
        }
    }
}
