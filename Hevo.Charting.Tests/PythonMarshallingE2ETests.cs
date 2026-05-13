using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §D2.8 Marshalling 边界 e2e 测试 —— 真 PythonNetRuntime 跨边界 ROM&lt;double&gt; ↔ numpy.ndarray
    /// 来回拷贝的边界值正确性。覆盖:
    /// <list type="bullet">
    ///   <item>NaN preserved 双向</item>
    ///   <item>±Inf preserved 双向</item>
    ///   <item>空 ROM<double> / 单元素 / 大数组(1000+ 点)</item>
    ///   <item>极小 / 极大正常值(double 边界)</item>
    /// </list>
    ///
    /// <para>
    /// <b>Why it matters</b>:NaN 在 indicator 里有语义("暖机不足"信号),如果 marshal 把 NaN 转成 0 或
    /// 抛 ValueError,下游 AutoScale / 渲染会拿到污染数据,无声错位。±Inf 类似(罕见但出现就毒主流)。
    /// </para>
    /// </summary>
    [Collection(nameof(RealPythonCollection))]
    public sealed class PythonMarshallingE2ETests
    {
        private readonly RealPythonFixture _fx;

        public PythonMarshallingE2ETests(RealPythonFixture fx)
        {
            _fx = fx;
        }

        // identity_double:输入 ROM<double> 直接返回(最纯的 marshal round-trip 测试)
        private const string IdentityPy = """
            from hevo_indicators import register
            import numpy as np

            @register('identity_double',
                      signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
            def identity_double(arr):
                # 强制经一遍 numpy(模拟真 indicator 路径)
                return np.asarray(arr, dtype=np.float64).copy()
            """;

        private Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>? GetIdentity()
        {
            if (!_fx.Available) return null;
            var registry = _fx.Registry!;
            var pyFile = Path.Combine(_fx.IndicatorsDir, $"identity_{Guid.NewGuid():N}.py");
            File.WriteAllText(pyFile, IdentityPy);
            registry.AutoDiscoverDirectory(_fx.IndicatorsDir);
            return registry.TryGet("identity_double") as Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>;
        }

        // ── NaN / Inf 边界 ──────────────────────────────────────────────────

        [Fact]
        public void NaN_PreservedThroughMarshal()
        {
            var fn = GetIdentity();
            if (fn == null) return;

            var input = new[] { 1.0, double.NaN, 3.0, double.NaN, 5.0 };
            var output = fn(input);

            Assert.Equal(5, output.Length);
            Assert.Equal(1.0, output.Span[0]);
            Assert.True(double.IsNaN(output.Span[1]), "NaN 应原样穿透 ROM ↔ ndarray");
            Assert.Equal(3.0, output.Span[2]);
            Assert.True(double.IsNaN(output.Span[3]));
            Assert.Equal(5.0, output.Span[4]);
        }

        [Fact]
        public void PositiveInfinity_PreservedThroughMarshal()
        {
            var fn = GetIdentity();
            if (fn == null) return;

            var input = new[] { 1.0, double.PositiveInfinity, 3.0 };
            var output = fn(input);

            Assert.Equal(3, output.Length);
            Assert.True(double.IsPositiveInfinity(output.Span[1]),
                $"+Inf 应原样穿透,实际拿到 {output.Span[1]}");
        }

        [Fact]
        public void NegativeInfinity_PreservedThroughMarshal()
        {
            var fn = GetIdentity();
            if (fn == null) return;

            var input = new[] { 1.0, double.NegativeInfinity, 3.0 };
            var output = fn(input);

            Assert.Equal(3, output.Length);
            Assert.True(double.IsNegativeInfinity(output.Span[1]),
                $"-Inf 应原样穿透,实际拿到 {output.Span[1]}");
        }

        [Fact]
        public void MixedNaNAndInf_AllPreserved()
        {
            var fn = GetIdentity();
            if (fn == null) return;

            var input = new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0.0, -0.0 };
            var output = fn(input);

            Assert.Equal(5, output.Length);
            Assert.True(double.IsNaN(output.Span[0]));
            Assert.True(double.IsPositiveInfinity(output.Span[1]));
            Assert.True(double.IsNegativeInfinity(output.Span[2]));
            Assert.Equal(0.0, output.Span[3]);
            Assert.Equal(0.0, output.Span[4]);   // -0.0 跟 0.0 在 == 上等价,marshal 不强 distinguish
        }

        // ── 长度边界 ────────────────────────────────────────────────────────

        [Fact]
        public void EmptyArray_RoundTripsAsEmpty()
        {
            var fn = GetIdentity();
            if (fn == null) return;

            var output = fn(ReadOnlyMemory<double>.Empty);
            Assert.Equal(0, output.Length);
        }

        [Fact]
        public void SingleElement_RoundTrips()
        {
            var fn = GetIdentity();
            if (fn == null) return;

            var output = fn(new[] { 42.0 });
            Assert.Equal(1, output.Length);
            Assert.Equal(42.0, output.Span[0]);
        }

        [Fact]
        public void LargeArray_1000Points_BitExact()
        {
            var fn = GetIdentity();
            if (fn == null) return;

            var input = new double[1000];
            var rng = new Random(42);
            for (int i = 0; i < input.Length; i++) input[i] = rng.NextDouble() * 1000.0 - 500.0;

            var output = fn(input);
            Assert.Equal(1000, output.Length);
            // 完全 bit-exact:ROM → ndarray (Marshal.Copy) → ndarray.copy() → ROM (Marshal.Copy back) 应零损失
            for (int i = 0; i < input.Length; i++)
            {
                Assert.Equal(input[i], output.Span[i]);
            }
        }

        // ── double 数值边界 ─────────────────────────────────────────────────

        [Fact]
        public void DoubleBoundaries_MaxMinEpsilon()
        {
            var fn = GetIdentity();
            if (fn == null) return;

            var input = new[]
            {
                double.MaxValue,
                double.MinValue,
                double.Epsilon,
                -double.Epsilon,
                1.7976931348623157e+308,    // ≈ MaxValue
                -1.7976931348623157e+308,
            };
            var output = fn(input);

            Assert.Equal(input.Length, output.Length);
            for (int i = 0; i < input.Length; i++)
            {
                Assert.Equal(input[i], output.Span[i]);
            }
        }
    }
}
