using BenchmarkDotNet.Running;

namespace Hevo.Charting.Benchmarks
{
    public static class Program
    {
        // BenchmarkDotNet 的 BenchmarkSwitcher 默认从命令行选 benchmark 类。
        // 跑全部:`dotnet run -c Release -- --filter "*"`
        // 跑特定:`dotnet run -c Release -- --filter "*ReflectionVsCompiled*"`
        public static void Main(string[] args)
            => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
