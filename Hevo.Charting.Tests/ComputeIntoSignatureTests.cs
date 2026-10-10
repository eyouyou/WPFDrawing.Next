using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using Xunit;
using static Hevo.Charting.Tests.IngestorColumnConsistencyTests;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// ComputeFeature 的零分配 C# 签名(ComputeInto / ComputeIntoMulti):结果正确、输出缓冲在调用点池里复用
    /// (稳态只在几块之间轮换)、旧 Func 签名照常可用、handler 抛异常后还能继续算。
    /// 下游用第二个 ComputeFeature 读输出端口,记录看到的值和底层数组。
    /// </summary>
    [Collection(nameof(ColumnReadScopeCollection))]
    public sealed class ComputeIntoSignatureTests
    {
        private sealed class Sink
        {
            public readonly List<(double First, double Last, int Length)> Seen = new();
            public readonly HashSet<double[]> Arrays = new(ReferenceEqualityComparer.Instance);

            public ComputeFeature Reader(DataPort<ReadOnlyMemory<double>> port, string name) => new()
            {
                InputPort = port,
                OutputPort = new DataPort<ReadOnlyMemory<double>>("CC_Sink_" + name),
                Compute = (Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>)(col =>
                {
                    lock (this)
                    {
                        Seen.Add((col.Span[0], col.Span[^1], col.Length));
                        if (MemoryMarshal.TryGetArray(col, out var seg)) Arrays.Add(seg.Array!);
                    }
                    return new double[] { 0 };
                }),
            };

            public (double First, double Last, int Length) Last { get { lock (this) return Seen[^1]; } }
        }

        private static void WaitFor(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!done() && DateTime.UtcNow < until) Thread.Sleep(5);
        }

        private static void Run(Func<RaceSchema, Feature[]> features, Action<RaceSchema> drive)
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var schema = new RaceSchema(features);
                var cell = new ChartCell { Template = schema };
                using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);
                drive(schema);
            });
        }

        [Fact]
        public void MigrationHint_OnlyForCSharpHandlersReturningNewArrays()
        {
            Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>> csharp = col => new double[col.Length];
            Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>, ReadOnlyMemory<double>> csharp2 = (a, b) => a;
            Func<ReadOnlyMemory<double>, IDictionary<string, object?>> dict = _ => new Dictionary<string, object?>();
            ComputeInto span = (i, o) => { };
            ComputeIntoMulti spanMulti = (i, o) => { };
            // Python handler 的委托是表达式树编译出来的(跟 PythonInvokerShim 一样)
            var p = System.Linq.Expressions.Expression.Parameter(typeof(ReadOnlyMemory<double>));
            var compiled = System.Linq.Expressions.Expression.Lambda<Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>>(p, p).Compile();

            Assert.True(ComputeFeature.AllocatesResultEachCall(csharp));
            Assert.True(ComputeFeature.AllocatesResultEachCall(csharp2));
            Assert.False(ComputeFeature.AllocatesResultEachCall(dict));
            Assert.False(ComputeFeature.AllocatesResultEachCall(span));
            Assert.False(ComputeFeature.AllocatesResultEachCall(spanMulti));
            Assert.False(ComputeFeature.AllocatesResultEachCall(compiled));
        }

        [Fact]
        public void SingleInput_SpanSignature_ProducesCorrectValues_AndReusesBuffers()
        {
            var sink = new Sink();
            var outPort = new DataPort<ReadOnlyMemory<double>>("CC_Out1");
            Run(s => new Feature[]
            {
                new ComputeFeature
                {
                    InputPort = s.Mapped, OutputPort = outPort,
                    Compute = (ComputeInto)((input, output) => { for (int i = 0; i < input.Length; i++) output[i] = input[i] * 2 + i; }),
                },
                sink.Reader(outPort, "1"),
            }, s =>
            {
                for (int gen = 1; gen <= 60; gen++)
                {
                    int len = gen % 3 == 0 ? 2000 : 1500;      // 长度来回变
                    s.Source.PublishGen(gen, len);
                    WaitFor(() => { lock (sink) return sink.Seen.Count > 0 && sink.Last.First == gen * 2; });
                    var last = sink.Last;
                    Assert.Equal((gen * 2.0, gen * 2.0 + len - 1, len), last);
                }
            });
            Assert.True(sink.Arrays.Count <= 6, $"60 次结果用了 {sink.Arrays.Count} 块数组,输出缓冲没被复用");
        }

        [Fact]
        public void MultiInput_SpanSignature_ProducesCorrectValues()
        {
            var sink = new Sink();
            var outPort = new DataPort<ReadOnlyMemory<double>>("CC_Out2");
            Run(s => new Feature[]
            {
                new ComputeFeature
                {
                    Inputs = new() { ["a"] = s.Mapped, ["b"] = s.Scatter },
                    InputOrder = new[] { "a", "b" },
                    OutputPort = outPort,
                    Compute = (ComputeIntoMulti)((inputs, output) =>
                    {
                        var a = inputs[0].Span; var b = inputs[1].Span;
                        for (int i = 0; i < output.Length; i++) output[i] = a[i] + b[i];
                    }),
                },
                sink.Reader(outPort, "2"),
            }, s =>
            {
                for (int gen = 1; gen <= 20; gen++)
                {
                    s.Source.PublishGen(gen, 1000);
                    WaitFor(() => { lock (sink) return sink.Seen.Count > 0 && sink.Last.First == gen * 2; });
                    Assert.Equal((gen * 2.0, gen * 2.0, 1000), sink.Last);
                }
            });
        }

        [Fact]
        public void FuncSignature_StillWorks_AndThrowingSpanHandler_Recovers()
        {
            var sinkFunc = new Sink();
            var sinkSpan = new Sink();
            var outFunc = new DataPort<ReadOnlyMemory<double>>("CC_OutF");
            var outSpan = new DataPort<ReadOnlyMemory<double>>("CC_OutS");
            Run(s => new Feature[]
            {
                new ComputeFeature
                {
                    InputPort = s.Mapped, OutputPort = outFunc,
                    Compute = (Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>)(col => new[] { col.Span[0] + 1 }),
                },
                new ComputeFeature
                {
                    InputPort = s.Mapped, OutputPort = outSpan,
                    Compute = (ComputeInto)((input, output) =>
                    {
                        if (input[0] == 3) throw new InvalidOperationException("boom");
                        for (int i = 0; i < input.Length; i++) output[i] = -input[i];
                    }),
                },
                sinkFunc.Reader(outFunc, "F"),
                sinkSpan.Reader(outSpan, "S"),
            }, s =>
            {
                for (int gen = 1; gen <= 6; gen++)
                {
                    s.Source.PublishGen(gen, 100);
                    WaitFor(() => { lock (sinkFunc) return sinkFunc.Seen.Count > 0 && sinkFunc.Last.First == gen + 1; });
                    if (gen != 3) WaitFor(() => { lock (sinkSpan) return sinkSpan.Seen.Count > 0 && sinkSpan.Last.First == -gen; });
                }
            });
            Assert.Equal((7.0, 7.0, 1), sinkFunc.Last);
            Assert.Equal((-6.0, -6.0, 100), sinkSpan.Last);
        }
    }
}
