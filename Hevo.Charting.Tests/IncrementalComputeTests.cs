using System;
using System.Collections.Generic;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Hevo.Charting.PythonNet;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §D2.6.4 IIncrementalCompute 增量协议:Mock 路径单测。
    /// 覆盖:Scanner kwarg 抓取 / TypeMapper Tuple → ValueTuple / Shim ValueTuple round-trip /
    /// DryRun NOT_STATEFUL 检查 + IsAssignableFrom 派生子类支持。
    /// state 装 feature 内部 private 字段(非 board 端口),反馈环概念已消除。
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class IncrementalComputeTests
    {
        // ── Scanner: incremental kwarg 抓取 ──────────────────────────────

        [Fact]
        public void Scanner_CapturesIncrementalKwarg()
        {
            var py = """
                @register("ema_20",
                          incremental=True,
                          signature="(ReadOnlyMemory[double], ReadOnlyMemory[double]) -> Tuple[ReadOnlyMemory[double], ReadOnlyMemory[double]]")
                def ema_20(close, prev):
                    return close, prev
                """;
            var result = PythonRegisterScanner.ScanText(py);
            Assert.Single(result);
            Assert.Equal("ema_20", result[0].Name);
            Assert.True(result[0].IsIncremental);
        }

        [Fact]
        public void Scanner_NoIncrementalKwarg_DefaultsFalse()
        {
            var py = """
                @register("ma_20", signature="(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]")
                def ma(close):
                    return close
                """;
            var result = PythonRegisterScanner.ScanText(py);
            Assert.Single(result);
            Assert.False(result[0].IsIncremental);
        }

        [Fact]
        public void Scanner_IncrementalFalse_DefaultsFalse()
        {
            var py = """
                @register("foo", incremental=False, signature="(int) -> int")
                def foo(x): return x
                """;
            var result = PythonRegisterScanner.ScanText(py);
            Assert.Single(result);
            Assert.False(result[0].IsIncremental);
        }

        // ── PythonTypeMapper: Tuple[X, Y] → ValueTuple<X, Y> ────────────

        [Fact]
        public void ResolveDelegateType_TupleReturn_BuildsFuncOfValueTuple()
        {
            var t = PythonTypeMapper.ResolveDelegateType(
                "(ReadOnlyMemory[double], ReadOnlyMemory[double]) -> Tuple[ReadOnlyMemory[double], ReadOnlyMemory[double]]");

            Assert.NotNull(t);
            // 期望 Func<ROM<double>, ROM<double>, ValueTuple<ROM<double>, ROM<double>>>
            var expectedRet = typeof(ValueTuple<ReadOnlyMemory<double>, ReadOnlyMemory<double>>);
            var expected = typeof(Func<,,>).MakeGenericType(
                typeof(ReadOnlyMemory<double>), typeof(ReadOnlyMemory<double>), expectedRet);
            Assert.Equal(expected, t);
        }

        [Fact]
        public void MapType_NestedTupleOfROM_RoundTrips()
        {
            var t = PythonTypeMapper.MapType("Tuple[ReadOnlyMemory[double], ReadOnlyMemory[double]]");
            Assert.Equal(typeof(ValueTuple<ReadOnlyMemory<double>, ReadOnlyMemory<double>>), t);
        }

        [Fact]
        public void MapType_LowercaseTuple_AlsoSupported()
        {
            // Python 3.9+ 支持 PEP 585: 直接 tuple[X, Y] 不必 from typing import Tuple
            var t = PythonTypeMapper.MapType("tuple[double, int]");
            Assert.Equal(typeof(ValueTuple<double, int>), t);
        }

        // ── PythonInvokerShim: ValueTuple round-trip via Mock ───────────

        [Fact]
        public void RegisterModule_IncrementalDelegate_StubReturnsValueTuple()
        {
            var runtime = new MockPythonRuntime();
            var registry = new PythonHandlerRegistry().UseRuntime(runtime).Initialize();

            registry.RegisterModule(
                "ema_20", "fake.py", "ema_20",
                "(ReadOnlyMemory[double], ReadOnlyMemory[double]) -> Tuple[ReadOnlyMemory[double], ReadOnlyMemory[double]]");

            // Mock 路径:StubResult 直接喂 ValueTuple,shim 应原样转交
            var output = new ReadOnlyMemory<double>(new[] { 42.0, 43.0 });
            var nextState = new ReadOnlyMemory<double>(new[] { 100.0 });
            runtime.Modules["fake.py"].StubResult = (output, nextState);

            var del = registry.TryGet("ema_20");
            Assert.NotNull(del);

            // 强类型 Func<ROM, ROM, ValueTuple<ROM, ROM>>
            var func = del as Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>,
                                   ValueTuple<ReadOnlyMemory<double>, ReadOnlyMemory<double>>>;
            Assert.NotNull(func);

            var (o, s) = func!(ReadOnlyMemory<double>.Empty, ReadOnlyMemory<double>.Empty);
            Assert.Equal(new[] { 42.0, 43.0 }, o.ToArray());
            Assert.Equal(new[] { 100.0 }, s.ToArray());
        }

        [Fact]
        public void RegisterModule_IncrementalDelegate_StubReturnsObjectArray_ConvertsToValueTuple()
        {
            // 真 Python 路径模拟:UnboxBestEffort 把 PyTuple 解包为 object?[]
            var runtime = new MockPythonRuntime();
            var registry = new PythonHandlerRegistry().UseRuntime(runtime).Initialize();

            registry.RegisterModule(
                "ema_20", "fake.py", "ema_20",
                "(ReadOnlyMemory[double], ReadOnlyMemory[double]) -> Tuple[ReadOnlyMemory[double], ReadOnlyMemory[double]]");

            var output = new ReadOnlyMemory<double>(new[] { 7.0 });
            var state = new ReadOnlyMemory<double>(new[] { 7.0 });
            runtime.Modules["fake.py"].StubResult = new object?[] { output, state };

            var func = (Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>,
                             ValueTuple<ReadOnlyMemory<double>, ReadOnlyMemory<double>>>)
                       registry.TryGet("ema_20")!;

            var (o, s) = func(ReadOnlyMemory<double>.Empty, ReadOnlyMemory<double>.Empty);
            Assert.Equal(new[] { 7.0 }, o.ToArray());
            Assert.Equal(new[] { 7.0 }, s.ToArray());
        }

        // ── DryRun: NOT_STATEFUL 错误码 ──────────────────────────────────

        [Fact]
        public void DryRun_IncrementalNonStatefulHandler_ReportsWarning()
        {
            var bp = MakeMinimalBlueprint();
            bp.Features.Add(new FeatureModel
            {
                TypeName = nameof(IncrementalComputeFeature),
                Properties = new Dictionary<string, object?> { ["Compute"] = "wrong_handler" },
                InputBindings  = new Dictionary<string, object?> { [nameof(IncrementalComputeFeature.InputPort)]  = "candle_close" },
                OutputBindings = new Dictionary<string, object?> { [nameof(IncrementalComputeFeature.OutputPort)] = "ind" },
            });

            // 注册一个**非**ValueTuple 返回的 handler
            var handlers = new BlueprintHandlerRegistry();
            handlers.RegisterDelegate("wrong_handler",
                new Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>(close => close));

            var r = BlueprintLauncher.DryRun(bp, handlers);
            Assert.Contains(r.Diagnostics, d =>
                d.Code == "BP_PYHANDLER_INCREMENTAL_NOT_STATEFUL" &&
                d.Severity == BlueprintDiagnosticSeverity.Warning);
        }

        // 反向用例:正确 ValueTuple 返回 handler → 不应报错
        [Fact]
        public void DryRun_IncrementalProperlyWired_NoNotStatefulDiagnostic()
        {
            var bp = MakeMinimalBlueprint();
            bp.Features.Add(new FeatureModel
            {
                TypeName = nameof(IncrementalComputeFeature),
                Properties = new Dictionary<string, object?> { ["Compute"] = "ema_correct" },
                InputBindings  = new Dictionary<string, object?> { [nameof(IncrementalComputeFeature.InputPort)]  = "candle_close" },
                OutputBindings = new Dictionary<string, object?> { [nameof(IncrementalComputeFeature.OutputPort)] = "ind_ema" },
            });

            var handlers = new BlueprintHandlerRegistry();
            handlers.RegisterDelegate("ema_correct",
                new Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>,
                         ValueTuple<ReadOnlyMemory<double>, ReadOnlyMemory<double>>>(
                    (close, prev) => (close, prev)));

            var r = BlueprintLauncher.DryRun(bp, handlers);
            Assert.DoesNotContain(r.Diagnostics, d => d.Code == "BP_PYHANDLER_INCREMENTAL_NOT_STATEFUL");
        }

        // ── 业务侧非蓝图用法:schema.Add 直接 IncrementalComputeFeature ────

        [Fact]
        public void StatefulCompute_StrongTypedDelegate_NoValueTupleSpelling()
        {
            // 业务侧最简洁形态 —— C# 9 tuple literal 直接喂,无需 ValueTuple<,> 拼写。
            // 验证字段类型签名能让 lambda 无强转直接赋值(编译就 OK 即视为通过)。
            var feature = new IncrementalComputeFeature
            {
                StatefulCompute = (input, prev) =>
                {
                    var alpha = 0.5;
                    var prevVal = prev.Length == 0 ? input.Span[0] : prev.Span[0];
                    var newEma = input.Span[input.Length - 1] * alpha + prevVal * (1 - alpha);
                    var arr = new[] { newEma };
                    return (arr, arr);   // ← C# 9 tuple,自动 conv 到 ValueTuple<ROM, ROM>
                },
            };
            Assert.NotNull(feature.StatefulCompute);

            // 直接调一次验证算子语义对(无 schema / 无 board,纯函数测试)
            var (out1, st1) = feature.StatefulCompute!(new ReadOnlyMemory<double>(new[] { 10.0 }), ReadOnlyMemory<double>.Empty);
            Assert.Equal(10.0, out1.Span[0]);   // bootstrap:input[0] == prev → output == input[-1]
            Assert.Equal(10.0, st1.Span[0]);

            var (out2, st2) = feature.StatefulCompute!(new ReadOnlyMemory<double>(new[] { 12.0 }), st1);
            Assert.Equal(11.0, out2.Span[0]);   // 12*0.5 + 10*0.5 = 11
        }

        // ── DryRun IsAssignableFrom: 派生子类自动受 NOT_STATEFUL 保护 ──

        [Fact]
        public void DryRun_DerivedIncrementalSubclass_AlsoCheckedForStatefulHandler()
        {
            // 派生子类应被 IsAssignableFrom 命中,跟基类一样被校验
            ComponentRegistry.Register<TestEmaSubclass>();

            var bp = MakeMinimalBlueprint();
            bp.Features.Add(new FeatureModel
            {
                TypeName = nameof(TestEmaSubclass),
                Properties = new Dictionary<string, object?> { ["Compute"] = "single_arg_handler" },
                InputBindings  = new Dictionary<string, object?> { [nameof(IncrementalComputeFeature.InputPort)]  = "candle_close" },
                OutputBindings = new Dictionary<string, object?> { [nameof(IncrementalComputeFeature.OutputPort)] = "ind" },
            });

            var handlers = new BlueprintHandlerRegistry();
            handlers.RegisterDelegate("single_arg_handler",
                new Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>(c => c));

            var r = BlueprintLauncher.DryRun(bp, handlers);
            Assert.Contains(r.Diagnostics, d =>
                d.Code == "BP_PYHANDLER_INCREMENTAL_NOT_STATEFUL" &&
                d.FeatureTypeName == nameof(TestEmaSubclass));
        }

        // 业务派生子类示例(测试夹具)—— 真实 EMA-N 风味
        public sealed class TestEmaSubclass : IncrementalComputeFeature { }

        // ── 多输入 stateful (§D2.6.4 + §D2.X 复用) ─────────────────────

        [Fact]
        public void Scanner_IncrementalPlusInputs_BothFlagsCaptured()
        {
            // ATR-stateful 风味:N 个数据输入 + 隐含 state 形参,signature 串里 N+1 参
            var py = """
                @register('atr_14_inc',
                          incremental=True,
                          inputs=['high','low','close'],
                          signature='(ReadOnlyMemory[double], ReadOnlyMemory[double], ReadOnlyMemory[double], ReadOnlyMemory[double]) -> Tuple[ReadOnlyMemory[double], ReadOnlyMemory[double]]')
                def atr_14_inc(h, l, c, prev): return h, prev
                """;
            var result = PythonRegisterScanner.ScanText(py);
            Assert.Single(result);
            Assert.True(result[0].IsIncremental);
            Assert.Equal(new[] { "high", "low", "close" }, result[0].Inputs!);   // ← state 不在 inputs 列表里
        }

        [Fact]
        public void TypeMapper_FourArgFuncWithTupleReturn_BuildsCorrectFuncType()
        {
            // 多输入 stateful:H/L/C/state 4 个 ROM 形参,返回 Tuple[ROM, ROM]
            var t = PythonTypeMapper.ResolveDelegateType(
                "(ReadOnlyMemory[double], ReadOnlyMemory[double], ReadOnlyMemory[double], ReadOnlyMemory[double]) -> Tuple[ReadOnlyMemory[double], ReadOnlyMemory[double]]");
            var expectedRet = typeof(ValueTuple<ReadOnlyMemory<double>, ReadOnlyMemory<double>>);
            var expected = typeof(Func<,,,,>).MakeGenericType(
                typeof(ReadOnlyMemory<double>), typeof(ReadOnlyMemory<double>),
                typeof(ReadOnlyMemory<double>), typeof(ReadOnlyMemory<double>),
                expectedRet);
            Assert.Equal(expected, t);
        }

        [Fact]
        public void RegisterModule_MultiInputIncrementalDelegate_StubReturnsValueTuple()
        {
            var runtime = new MockPythonRuntime();
            var registry = new PythonHandlerRegistry().UseRuntime(runtime).Initialize();

            registry.RegisterModule(
                "atr_14_inc", "fake.py", "atr_14_inc",
                "(ReadOnlyMemory[double], ReadOnlyMemory[double], ReadOnlyMemory[double], ReadOnlyMemory[double]) -> Tuple[ReadOnlyMemory[double], ReadOnlyMemory[double]]",
                inputs: new[] { "high", "low", "close" });

            // BlueprintHandlerRegistry 应记得 inputs 列表
            Assert.Equal(new[] { "high", "low", "close" }, registry.GetInputNames("atr_14_inc")!);

            // Mock 路径:StubResult 直接喂 ValueTuple
            var output = new ReadOnlyMemory<double>(new[] { 1.5 });
            var nextState = new ReadOnlyMemory<double>(new[] { 1.5 });
            runtime.Modules["fake.py"].StubResult = (output, nextState);

            var del = registry.TryGet("atr_14_inc");
            Assert.NotNull(del);

            var func = del as Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>,
                                   ReadOnlyMemory<double>, ReadOnlyMemory<double>,
                                   ValueTuple<ReadOnlyMemory<double>, ReadOnlyMemory<double>>>;
            Assert.NotNull(func);

            var (o, s) = func!(ReadOnlyMemory<double>.Empty, ReadOnlyMemory<double>.Empty,
                               ReadOnlyMemory<double>.Empty, ReadOnlyMemory<double>.Empty);
            Assert.Equal(new[] { 1.5 }, o.ToArray());
            Assert.Equal(new[] { 1.5 }, s.ToArray());
        }

        [Fact]
        public void DryRun_MultiInputIncremental_NotStateful_StillReports()
        {
            // 多输入 + 错误 handler(返回不是 ValueTuple) → 应报 NOT_STATEFUL
            var bp = MakeMinimalBlueprint();
            bp.Features.Add(new FeatureModel
            {
                TypeName = nameof(IncrementalComputeFeature),
                Properties = new Dictionary<string, object?>
                {
                    ["Compute"] = "atr_wrong",
                    ["InputOrder"] = new[] { "high", "low", "close" },
                },
                InputBindings = new Dictionary<string, object?>
                {
                    ["Inputs.high"]  = "RealTime_High",
                    ["Inputs.low"]   = "RealTime_Low",
                    ["Inputs.close"] = "RealTime_Close",
                },
                OutputBindings = new Dictionary<string, object?> { [nameof(IncrementalComputeFeature.OutputPort)] = "ind_atr" },
            });

            var handlers = new BlueprintHandlerRegistry();
            // 故意用非 ValueTuple 返回的 4-arg handler
            handlers.RegisterDelegate("atr_wrong",
                new Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>,
                         ReadOnlyMemory<double>, ReadOnlyMemory<double>,
                         ReadOnlyMemory<double>>((h, l, c, prev) => h),
                inputs: new[] { "high", "low", "close" });

            var r = BlueprintLauncher.DryRun(bp, handlers);
            Assert.Contains(r.Diagnostics, d =>
                d.Code == "BP_PYHANDLER_INCREMENTAL_NOT_STATEFUL" &&
                d.FeatureTypeName == nameof(IncrementalComputeFeature));
        }

        [Fact]
        public void DryRun_MultiInputIncremental_MissingInputBinding_ReportsInputUnconnected()
        {
            // 复用 §D2.X 的 BP_PYHANDLER_INPUT_UNCONNECTED 检查 —— IncrementalCompute 多输入应自动受保护
            var bp = MakeMinimalBlueprint();
            bp.Features.Add(new FeatureModel
            {
                TypeName = nameof(IncrementalComputeFeature),
                Properties = new Dictionary<string, object?>
                {
                    ["Compute"] = "atr_x",
                    ["InputOrder"] = new[] { "high", "low", "close" },
                },
                InputBindings = new Dictionary<string, object?>
                {
                    ["Inputs.high"] = "RealTime_High",
                    ["Inputs.low"]  = "RealTime_Low",
                    // 故意不焊 close
                },
                OutputBindings = new Dictionary<string, object?> { [nameof(IncrementalComputeFeature.OutputPort)] = "ind_atr" },
            });

            var r = BlueprintLauncher.DryRun(bp);
            Assert.Contains(r.Diagnostics, d =>
                d.Code == "BP_PYHANDLER_INPUT_UNCONNECTED" &&
                d.PortName == "Inputs.close");
        }

        // ── 测试夹具 ────────────────────────────────────────────────────

        private static ChartBlueprint MakeMinimalBlueprint()
        {
            EnsureTestDataSourceRegistered();
            return new ChartBlueprint
            {
                DataSources = { new DataSourceModel { Id = "primary", TypeName = nameof(TestDataSource) } },
            };
        }

        private static bool _testDsRegistered;
        private static void EnsureTestDataSourceRegistered()
        {
            if (_testDsRegistered) return;
            _testDsRegistered = true;
            ComponentRegistry.Register<TestDataSource>();
        }

        // Mock IPythonRuntime / IPythonModule —— 跟 PythonHandlerTests 同款,本地 copy 避开 internal 可见性问题
        private sealed class MockPythonRuntime : IPythonRuntime
        {
            public Dictionary<string, MockPythonModule> Modules { get; } = new();
            public void Initialize(PythonSandboxOptions options) { }
            public IPythonModule ImportModule(string moduleName, string filePath)
            {
                var mod = new MockPythonModule(moduleName, filePath);
                Modules[filePath] = mod;
                return mod;
            }
            public void Shutdown() { }
        }

        private sealed class MockPythonModule : IPythonModule
        {
            public string Name { get; }
            public string FilePath { get; }
            public object? StubResult { get; set; }
            public MockPythonModule(string name, string filePath) { Name = name; FilePath = filePath; }
            public object? Invoke(string functionName, params object?[] args) => StubResult;
            public bool HasFunction(string functionName) => true;
            public IReadOnlyList<PythonHandlerDescriptor> ListRegisteredHandlers() => Array.Empty<PythonHandlerDescriptor>();
        }
    }
}
