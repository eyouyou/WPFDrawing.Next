using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.PythonNet;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §D2 Python 嵌入指标的核心组件测试。
    /// 不依赖真实 Python.NET 运行时,用 mock IPythonModule 验证调度路径正确;
    /// 类型映射 / 装饰器扫描器是纯文本逻辑,无需 mock。
    /// </summary>
    public sealed class PythonHandlerTests
    {
        // ── PythonTypeMapper(D2.2)────────────────────────────────────────

        [Theory]
        [InlineData("int",      typeof(int))]
        [InlineData("Int32",    typeof(int))]
        [InlineData("long",     typeof(long))]
        [InlineData("double",   typeof(double))]
        [InlineData("float",    typeof(float))]
        [InlineData("bool",     typeof(bool))]
        [InlineData("string",   typeof(string))]
        [InlineData("DateTime", typeof(DateTime))]
        [InlineData("None",     typeof(void))]
        public void MapType_PrimitiveAliases(string name, Type expected)
        {
            Assert.Equal(expected, PythonTypeMapper.MapType(name));
        }

        [Fact]
        public void MapType_GenericReadOnlyMemoryDouble()
        {
            Assert.Equal(typeof(ReadOnlyMemory<double>), PythonTypeMapper.MapType("ReadOnlyMemory[double]"));
        }

        [Fact]
        public void MapType_UnknownType_ReturnsNull()
        {
            Assert.Null(PythonTypeMapper.MapType("MyCustomType"));
            Assert.Null(PythonTypeMapper.MapType("List[double]"));   // 暂不支持非 ReadOnlyMemory 的泛型
        }

        // 签名解析 → Func / Action 正确推断
        [Fact]
        public void ResolveDelegateType_FuncSingleParam()
        {
            var t = PythonTypeMapper.ResolveDelegateType("(int) -> string");
            Assert.Equal(typeof(Func<int, string>), t);
        }

        [Fact]
        public void ResolveDelegateType_FuncReadOnlyMemoryToReadOnlyMemory()
        {
            var t = PythonTypeMapper.ResolveDelegateType("(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]");
            Assert.Equal(typeof(Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>), t);
        }

        [Fact]
        public void ResolveDelegateType_ActionMultiParam()
        {
            var t = PythonTypeMapper.ResolveDelegateType("(int, double, bool)");
            Assert.Equal(typeof(Action<int, double, bool>), t);
        }

        [Fact]
        public void ResolveDelegateType_FuncZeroParam()
        {
            var t = PythonTypeMapper.ResolveDelegateType("() -> int");
            Assert.Equal(typeof(Func<int>), t);
        }

        [Fact]
        public void ResolveDelegateType_VoidActionZeroParam()
        {
            var t = PythonTypeMapper.ResolveDelegateType("()");
            Assert.Equal(typeof(Action), t);
        }

        [Fact]
        public void ResolveDelegateType_BadSyntax_ReturnsNull()
        {
            Assert.Null(PythonTypeMapper.ResolveDelegateType("not a signature"));
            Assert.Null(PythonTypeMapper.ResolveDelegateType("(unknown_type) -> int"));
        }

        // ── PythonRegisterScanner(D2.1)──────────────────────────────────

        [Fact]
        public void Scanner_FindsRegisterDecorator()
        {
            var py = """
                from hevo_indicators import register
                @register("ma_close_20", signature="(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]")
                def moving_average(close):
                    return close
                """;
            var result = PythonRegisterScanner.ScanText(py);
            Assert.Single(result);
            Assert.Equal("ma_close_20", result[0].Name);
            Assert.Equal("moving_average", result[0].FunctionName);
            Assert.Equal("(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]", result[0].Signature);
        }

        [Fact]
        public void Scanner_FindsMultipleHandlers()
        {
            var py = """
                @register("h1", signature="(int) -> int")
                def f1(x): return x

                @register("h2")
                def f2(x): return x * 2
                """;
            var result = PythonRegisterScanner.ScanText(py);
            Assert.Equal(2, result.Count);
            Assert.Equal("h1", result[0].Name);
            Assert.Null(result[1].Signature);  // 没声明 signature 时为 null
        }

        [Fact]
        public void Scanner_IgnoresNonRegisterDecorators()
        {
            var py = """
                @other_decorator("foo")
                def f1(): pass

                @register("real")
                def f2(): pass
                """;
            var result = PythonRegisterScanner.ScanText(py);
            Assert.Single(result);
            Assert.Equal("real", result[0].Name);
        }

        [Fact]
        public void Scanner_DirectoryScansPyFiles()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"hevo_pyscan_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "ma.py"),
                    "@register(\"ma\", signature=\"(int) -> int\")\ndef ma(x): return x\n");
                File.WriteAllText(Path.Combine(dir, "rsi.py"),
                    "@register(\"rsi\")\ndef rsi(x): return x\n");
                File.WriteAllText(Path.Combine(dir, "skip.txt"), "not a python file");

                var result = PythonRegisterScanner.ScanDirectory(dir);
                Assert.Equal(2, result.Count);
                Assert.Contains(result.Keys, k => k.EndsWith("ma.py"));
                Assert.Contains(result.Keys, k => k.EndsWith("rsi.py"));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ── PythonHandlerRegistry(D2.1 + D2.2)────────────────────────────

        [Fact]
        public void NullPythonRuntime_ThrowsHelpfulMessage()
        {
            var registry = new PythonHandlerRegistry();
            var ex = Assert.Throws<InvalidOperationException>(() =>
                registry.Initialize(new PythonSandboxOptions()));
            Assert.Contains("Python.NET", ex.Message);
            Assert.Contains("Python.Runtime", ex.Message);   // 安装指引在错误信息里
        }

        // 整合:用 mock IPythonRuntime 跑通 RegisterModule + 调用回到 module.Invoke
        [Fact]
        public void RegisterModule_BuildsDelegate_RoutesToPythonInvoke()
        {
            var runtime = new MockPythonRuntime();
            var registry = new PythonHandlerRegistry()
                .UseRuntime(runtime)
                .Initialize();

            // 模拟一个 .py 文件路径(mock runtime 不真读盘)
            registry.RegisterModule("ma_test", "fake.py", "moving_average", "(int) -> int");

            // 取出注册好的委托,验证调用确实落到 mock module
            var del = registry.TryGet("ma_test");
            Assert.NotNull(del);
            Assert.IsType<Func<int, int>>(del);

            var func = (Func<int, int>)del!;
            runtime.Modules["fake.py"].StubResult = 42;
            var ret = func(7);
            Assert.Equal(42, ret);
            Assert.Equal("moving_average", runtime.Modules["fake.py"].LastFunctionName);
            Assert.Equal(7, runtime.Modules["fake.py"].LastArgs![0]);
        }

        // 沙箱越界:RegisterModule 路径不在 AllowedRootDirectory 之下 → 抛 UnauthorizedAccessException
        [Fact]
        public void RegisterModule_PathOutsideSandbox_ThrowsUnauthorized()
        {
            var runtime = new MockPythonRuntime();
            var registry = new PythonHandlerRegistry()
                .UseRuntime(runtime)
                .Initialize(new PythonSandboxOptions { AllowedRootDirectory = Path.Combine(Path.GetTempPath(), "sandboxed") });

            Assert.Throws<UnauthorizedAccessException>(() =>
                registry.RegisterModule("h", "C:/Windows/System32/foo.py", "f", "(int)->int"));
        }

        // ── D2.3: Unregister / UnregisterBySourceFile ────────────────────

        [Fact]
        public void Unregister_RemovesHandler()
        {
            var runtime = new MockPythonRuntime();
            var registry = new PythonHandlerRegistry().UseRuntime(runtime).Initialize();
            registry.RegisterModule("to_remove", "fake.py", "f", "(int) -> int");

            Assert.True(registry.Contains("to_remove"));
            registry.Unregister("to_remove");
            Assert.False(registry.Contains("to_remove"));
        }

        [Fact]
        public void UnregisterBySourceFile_RemovesHandlersFromThatFile()
        {
            var runtime = new MockPythonRuntime();
            var registry = new PythonHandlerRegistry().UseRuntime(runtime).Initialize();

            var dir = Path.Combine(Path.GetTempPath(), $"hevo_unreg_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var fileA = Path.Combine(dir, "a.py");
            var fileB = Path.Combine(dir, "b.py");
            try
            {
                registry.RegisterModule("ha1", fileA, "f1", "(int) -> int");
                registry.RegisterModule("ha2", fileA, "f2", "(int) -> int");
                registry.RegisterModule("hb1", fileB, "f3", "(int) -> int");

                Assert.True(registry.Contains("ha1"));
                Assert.True(registry.Contains("ha2"));
                Assert.True(registry.Contains("hb1"));

                registry.UnregisterBySourceFile(fileA);

                Assert.False(registry.Contains("ha1"));
                Assert.False(registry.Contains("ha2"));
                Assert.True(registry.Contains("hb1"));   // b.py handlers untouched
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void InternalImportModule_ForcesReimport()
        {
            var runtime = new MockPythonRuntime();
            var registry = new PythonHandlerRegistry().UseRuntime(runtime).Initialize();

            var module1 = registry.InternalImportModule("file.py");
            var module2 = registry.InternalImportModule("file.py");   // hot-reload: new module instance

            // MockPythonRuntime creates a fresh MockPythonModule each ImportModule call
            // (because InternalImportModule removes from cache before re-importing)
            Assert.NotSame(module1, module2);
        }

        // ── D2.4: PythonDiagnosticsException ─────────────────────────────

        [Fact]
        public void PythonDiagnosticsException_ToStringContainsTraceback()
        {
            var ex = new PythonDiagnosticsException(
                message: "division by zero",
                pythonExceptionType: "ZeroDivisionError",
                pythonTraceback: "Traceback (most recent call last):\n  File 'ma.py', line 5",
                sourceFilePath: "ma.py",
                functionName: "moving_average");

            var str = ex.ToString();
            Assert.Contains("ZeroDivisionError", str);
            Assert.Contains("division by zero", str);
            Assert.Contains("Traceback", str);
            Assert.Contains("moving_average", str);
        }

        [Fact]
        public void DryRunImports_AllSucceed_ReturnsSuccessForEachFile()
        {
            var runtime = new MockPythonRuntime();
            var registry = new PythonHandlerRegistry().UseRuntime(runtime).Initialize();

            var dir = Path.Combine(Path.GetTempPath(), $"hevo_dryrun_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "ma.py"),
                    "@register(\"ma\", signature=\"(int) -> int\")\ndef ma(x): return x\n");
                File.WriteAllText(Path.Combine(dir, "rsi.py"),
                    "@register(\"rsi\", signature=\"(int) -> int\")\ndef rsi(x): return x\n");

                var diagnostics = registry.DryRunImports(dir);

                Assert.Equal(2, diagnostics.Count);
                Assert.All(diagnostics, d => Assert.True(d.Success));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void DryRunImports_FailingImport_ReturnsFailureEntry()
        {
            // runtime that throws PythonDiagnosticsException on ImportModule
            var runtime = new FailingImportRuntime();
            var registry = new PythonHandlerRegistry().UseRuntime(runtime).Initialize();

            var dir = Path.Combine(Path.GetTempPath(), $"hevo_dryrun_fail_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "bad.py"), "syntax error here\n");

                var diagnostics = registry.DryRunImports(dir);

                Assert.Single(diagnostics);
                Assert.False(diagnostics[0].Success);
                Assert.Contains("SyntaxError", diagnostics[0].Error);
                Assert.NotNull(diagnostics[0].PythonTraceback);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ── D2.X 多输入指标 ─────────────────────────────────────────────

        [Fact]
        public void Scanner_CapturesInputsKwarg()
        {
            var py = """
                @register("atr_14",
                          signature="(ReadOnlyMemory[double], ReadOnlyMemory[double], ReadOnlyMemory[double]) -> ReadOnlyMemory[double]",
                          inputs=['high', 'low', 'close'])
                def atr(high, low, close): return high
                """;
            var result = PythonRegisterScanner.ScanText(py);
            Assert.Single(result);
            Assert.Equal("atr_14", result[0].Name);
            Assert.NotNull(result[0].Inputs);
            Assert.Equal(new[] { "high", "low", "close" }, result[0].Inputs!);
        }

        [Fact]
        public void Scanner_NoInputsKwarg_LeavesInputsNull()
        {
            var py = """
                @register("ma", signature="(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]")
                def ma(close): return close
                """;
            var result = PythonRegisterScanner.ScanText(py);
            Assert.Single(result);
            Assert.Null(result[0].Inputs);   // 单输入 handler 默认 null
        }

        [Fact]
        public void Scanner_KwargOrder_DoesNotMatter()
        {
            // inputs 在 signature 之前
            var py = """
                @register("vwap", inputs=['price','volume'], signature="(ReadOnlyMemory[double], ReadOnlyMemory[double]) -> ReadOnlyMemory[double]")
                def vwap(p, v): return p
                """;
            var result = PythonRegisterScanner.ScanText(py);
            Assert.Single(result);
            Assert.Equal(new[] { "price", "volume" }, result[0].Inputs!);
            Assert.Contains("ReadOnlyMemory", result[0].Signature ?? string.Empty); // signature 仍要捕到
        }

        [Fact]
        public void RegisterModule_WithInputs_StoresMetadataInBaseRegistry()
        {
            var runtime = new MockPythonRuntime();
            var registry = new PythonHandlerRegistry().UseRuntime(runtime).Initialize();

            registry.RegisterModule(
                "atr_14", "fake.py", "atr",
                "(ReadOnlyMemory[double], ReadOnlyMemory[double], ReadOnlyMemory[double]) -> ReadOnlyMemory[double]",
                inputs: new[] { "high", "low", "close" });

            // BlueprintHandlerRegistry.GetInputNames 应该能查到 input names
            var inputs = registry.GetInputNames("atr_14");
            Assert.NotNull(inputs);
            Assert.Equal(new[] { "high", "low", "close" }, inputs!);
        }

        [Fact]
        public void RegisterDelegate_NullInputs_DoesNotStoreMetadata()
        {
            var registry = new BlueprintHandlerRegistry();
            registry.RegisterDelegate("h", new Action<int>(_ => { }), inputs: null);
            Assert.Null(registry.GetInputNames("h"));
        }

        [Fact]
        public void Unregister_AlsoClearsInputsMetadata()
        {
            var registry = new BlueprintHandlerRegistry();
            registry.RegisterDelegate("h", new Action<int>(_ => { }), inputs: new[] { "a" });
            Assert.NotNull(registry.GetInputNames("h"));
            registry.Unregister("h");
            Assert.Null(registry.GetInputNames("h"));
        }

        // ── Mock IPythonRuntime ─────────────────────────────────────────

        private sealed class MockPythonRuntime : IPythonRuntime
        {
            public Dictionary<string, MockPythonModule> Modules { get; } = new();
            public PythonSandboxOptions? Applied { get; private set; }

            public void Initialize(PythonSandboxOptions options) { Applied = options; }

            public IPythonModule ImportModule(string moduleName, string filePath)
            {
                // always creates a NEW instance (simulates hot-reload semantic: re-import = fresh module)
                var mod = new MockPythonModule(moduleName, filePath);
                Modules[filePath] = mod;
                return mod;
            }

            public void Shutdown() { }
        }

        private sealed class FailingImportRuntime : IPythonRuntime
        {
            public void Initialize(PythonSandboxOptions options) { }

            public IPythonModule ImportModule(string moduleName, string filePath)
                => throw new PythonDiagnosticsException(
                    "invalid syntax",
                    "SyntaxError",
                    "Traceback (most recent call last):\n  File 'bad.py', line 1\nSyntaxError: invalid syntax",
                    filePath);

            public void Shutdown() { }
        }

        private sealed class MockPythonModule : IPythonModule
        {
            public string Name { get; }
            public string FilePath { get; }
            public object? StubResult { get; set; }
            public string? LastFunctionName { get; private set; }
            public object?[]? LastArgs { get; private set; }

            public MockPythonModule(string name, string filePath) { Name = name; FilePath = filePath; }

            public object? Invoke(string functionName, params object?[] args)
            {
                LastFunctionName = functionName;
                LastArgs = args;
                return StubResult;
            }

            public bool HasFunction(string functionName) => true;   // mock 默认全有

            public IReadOnlyList<PythonHandlerDescriptor> ListRegisteredHandlers() => Array.Empty<PythonHandlerDescriptor>();
        }
    }
}
