using BenchmarkDotNet.Attributes;
using Hevo.Charting.LowCode.Designer;

namespace Hevo.Charting.Benchmarks
{
    /// <summary>
    /// §8 PortBindingValue 解析开销:CSV 旧格式 vs JSON 数组新格式 vs 单 string 单端口。
    /// 走 InternalsVisibleTo 直接调,避免反射污染数据。
    /// 单次几百 ns 量级,在 DefineFeatures 整体 ms 级流程里完全不可见,但仍量化记录。
    /// </summary>
    [MemoryDiagnoser]
    public class PortBindingValueBenchmarks
    {
        private readonly object _csv     = "id1,id2,id3,id4,id5";       // 旧 CSV
        private readonly object _array   = new List<string> { "id1", "id2", "id3", "id4", "id5" };  // 新数组
        private readonly object _single  = "single_id";                  // 退化单端口

        // ============================================================
        // ExtractList 三种输入形态
        // ============================================================

        [Benchmark(Baseline = true, Description = "ExtractList: CSV 5 ids (老格式)")]
        public IReadOnlyList<string> ExtractList_Csv5()
            => PortBindingValue.ExtractList(_csv);

        [Benchmark(Description = "ExtractList: List<string> 5 ids (新格式)")]
        public IReadOnlyList<string> ExtractList_Array5()
            => PortBindingValue.ExtractList(_array);

        [Benchmark(Description = "ExtractList: single string (退化)")]
        public IReadOnlyList<string> ExtractList_Single()
            => PortBindingValue.ExtractList(_single);

        // ============================================================
        // ExtractSingle 直接 string
        // ============================================================

        [Benchmark(Description = "ExtractSingle: string (单端口典型)")]
        public string ExtractSingle_String()
            => PortBindingValue.ExtractSingle("global_price_id");
    }
}
