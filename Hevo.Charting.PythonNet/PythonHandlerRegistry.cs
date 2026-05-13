using System.Collections.Generic;
using System.Reflection;
using Hevo.Charting.LowCode.Designer;

namespace Hevo.Charting.PythonNet
{
    /// <summary>
    /// §D2 Python 嵌入指标的 handler 注册表 —— 继承 <see cref="BlueprintHandlerRegistry"/>,
    /// 从 .py 文件 <c>@register("name")</c> 装饰器登记的 Python 函数自动构造对应 .NET 委托并注册。
    ///
    /// <para>
    /// <b>蓝图侧零变化</b>:still string handler name 引用,跟 §K AutoDiscover 路径完全一致。
    /// Feature.Properties 里 <c>"Compute": "ma_close_20"</c> 既可能解析成 C# AutoDiscover 注册的方法,
    /// 也可能是 PythonHandlerRegistry 注册的 Python 函数 —— 同一份 BlueprintHandlerRegistry 里查表统一。
    /// </para>
    ///
    /// <para>
    /// <b>启用 Python.NET</b>:默认走 <see cref="NullPythonRuntime"/>(任何调用抛 + 安装指引)。
    /// 业务侧引入 <c>Python.Runtime</c> NuGet,实现 <see cref="IPythonRuntime"/> 子类后调
    /// <see cref="UseRuntime"/> 注入,启用真实 Python 调度。
    /// </para>
    ///
    /// <code>
    /// // 业务侧典型用法:
    /// var registry = new PythonHandlerRegistry()
    ///     .UseRuntime(new PythonNetRuntime())          // 业务自己实现的 IPythonRuntime
    ///     .AutoDiscoverDirectory("C:/indicators",      // 扫这个目录的 .py 文件
    ///                            new PythonSandboxOptions { PerCallTimeoutMs = 100 });
    ///
    /// // 跟 C# handler 共存:
    /// registry.AutoDiscover(new TimeShareHandlers(ds));
    /// </code>
    /// </summary>
    public sealed class PythonHandlerRegistry : BlueprintHandlerRegistry
    {
        private IPythonRuntime _runtime = NullPythonRuntime.Instance;
        private readonly Dictionary<string, IPythonModule> _loadedModules = new(StringComparer.Ordinal);
        private bool _initialized;
        private PythonSandboxOptions _options = new();

        // §D2.3: tracks which handler names were registered from which source file,
        // so UnregisterBySourceFile can remove the right handlers on deletion.
        private readonly Dictionary<string, string> _handlerSourceFile = new(StringComparer.Ordinal);

        /// <summary>
        /// 注入实际的 Python 运行时实现。可在任何注册前调一次;<see cref="Initialize"/> / 注册之后调会 throw。
        /// </summary>
        public PythonHandlerRegistry UseRuntime(IPythonRuntime runtime)
        {
            if (_initialized)
                throw new InvalidOperationException("UseRuntime 必须在 Initialize / RegisterModule / AutoDiscover 之前调用。");
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            return this;
        }

        /// <summary>
        /// 应用沙箱配置并启动 Python 解释器。多次调幂等。<paramref name="options"/> 不传 = 默认沙箱。
        /// </summary>
        public PythonHandlerRegistry Initialize(PythonSandboxOptions? options = null)
        {
            _options = options ?? new PythonSandboxOptions();
            _runtime.Initialize(_options);
            _initialized = true;
            return this;
        }

        /// <summary>
        /// 单文件 + 单函数登记:把 <c>{filePath}::{functionName}</c> 暴露为 handler 名 <paramref name="handlerName"/>。
        /// <paramref name="signature"/> 跟 <c>@register</c> 装饰器同语义,由 <see cref="PythonTypeMapper"/> 解析成委托类型。
        /// <paramref name="inputs"/> §D2.X 多输入指标 —— Python 形参名列表(顺序跟 signature 形参顺序一致),
        /// 蓝图侧 PortBindings <c>"Inputs.{name}"</c> 据此匹配。单输入 handler 留 null。
        /// </summary>
        public PythonHandlerRegistry RegisterModule(
            string handlerName,
            string filePath,
            string functionName,
            string signature,
            IReadOnlyList<string>? inputs = null)
        {
            if (string.IsNullOrEmpty(handlerName)) throw new ArgumentNullException(nameof(handlerName));
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentNullException(nameof(filePath));
            if (string.IsNullOrEmpty(functionName)) throw new ArgumentNullException(nameof(functionName));
            if (string.IsNullOrEmpty(signature)) throw new ArgumentNullException(nameof(signature));

            EnsureInitialized();

            // AllowedRootDirectory 守护:filePath 必须落在允许根下(防业务侧拼到 OS 任意路径)。
            EnforceAllowedRoot(filePath);

            var delegateType = PythonTypeMapper.ResolveDelegateType(signature)
                ?? throw new InvalidOperationException(
                    $"Python handler '{handlerName}' signature='{signature}' 无法映射到 .NET 委托类型。");

            var module = LoadOrReuseModule(filePath);
            if (!module.HasFunction(functionName))
            {
                throw new InvalidOperationException(
                    $"Python module '{module.Name}' 不含函数 '{functionName}'。");
            }

            var del = BuildPythonInvokerDelegate(delegateType, module, functionName);
            RegisterDelegate(handlerName, del, inputs);
            _handlerSourceFile[handlerName] = System.IO.Path.GetFullPath(filePath);
            return this;
        }

        /// <summary>
        /// 扫一个目录下所有 .py,把每个文件里 <c>@register("name", signature="...")</c> 装饰器声明的函数自动注册。
        /// 文件没声明 signature 的 handler 跳过(诊断侧可标 BP_PYHANDLER_NO_SIGNATURE)。
        /// </summary>
        public PythonHandlerRegistry AutoDiscoverDirectory(
            string directory,
            PythonSandboxOptions? options = null)
        {
            if (options != null) Initialize(options);
            else EnsureInitialized();

            EnforceAllowedRoot(directory);

            var perFile = PythonRegisterScanner.ScanDirectory(directory);
            foreach (var (file, descriptors) in perFile)
            {
                foreach (var d in descriptors)
                {
                    if (string.IsNullOrEmpty(d.Signature))
                    {
                        // 没声明签名的 handler 跳过 —— 后续可在 DryRun 加 BP_PYHANDLER_NO_SIGNATURE 警告。
                        continue;
                    }
                    try
                    {
                        RegisterModule(d.Name, file, d.FunctionName, d.Signature, d.Inputs);
                    }
                    catch (Exception)
                    {
                        // AutoDiscover 不让单个 handler 失败拖崩整个扫描;失败的 handler DryRun
                        // 阶段会再被 BP_HANDLER_NOT_REGISTERED 报出来,业务有路径修。
                    }
                }
            }
            return this;
        }

        /// <summary>
        /// 把 .py 文件里指定函数注册成 untyped <c>Func&lt;object?[], object?&gt;</c> handler。
        /// 用于 plot/scatter/arrow 等返回 <c>list[dict]</c> 而非 <c>ROM&lt;double&gt;</c> 的场景 ——
        /// PythonTypeMapper 的强类型签名路径不适用,业务侧手动登记。
        /// </summary>
        public PythonHandlerRegistry RegisterPythonFunction(
            string handlerName,
            string filePath,
            string functionName,
            IReadOnlyList<string>? inputs = null)
        {
            if (string.IsNullOrEmpty(handlerName)) throw new ArgumentNullException(nameof(handlerName));
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentNullException(nameof(filePath));
            if (string.IsNullOrEmpty(functionName)) throw new ArgumentNullException(nameof(functionName));

            EnsureInitialized();
            EnforceAllowedRoot(filePath);

            var module = LoadOrReuseModule(filePath);
            if (!module.HasFunction(functionName))
                throw new InvalidOperationException($"Python module '{module.Name}' 不含函数 '{functionName}'。");

            Func<object?[], object?> del = args => module.Invoke(functionName, args);
            RegisterDelegate(handlerName, del, inputs);
            _handlerSourceFile[handlerName] = System.IO.Path.GetFullPath(filePath);
            return this;
        }

        /// <summary>
        /// 关闭 Python 解释器,清掉已加载模块缓存。典型应用退出钩子。
        /// </summary>
        public void Shutdown()
        {
            _loadedModules.Clear();
            _handlerSourceFile.Clear();
            _runtime.Shutdown();
            _initialized = false;
        }

        /// <summary>
        /// §D2.3: 移除由给定源文件注册的所有 handler（文件删除 / 失效时调用）。
        /// </summary>
        public void UnregisterBySourceFile(string filePath)
        {
            var abs = System.IO.Path.GetFullPath(filePath);
            var toRemove = _handlerSourceFile
                .Where(kv => string.Equals(kv.Value, abs, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var name in toRemove)
            {
                Unregister(name);
                _handlerSourceFile.Remove(name);
            }
            _loadedModules.Remove(filePath);
        }

        /// <summary>
        /// §D2.3: 热重载时强制重新 import 模块（等价于 Python importlib.reload）。
        /// 更新 _loadedModules 缓存中的条目，返回新模块引用。
        /// §D2.4: 若 runtime 抛出 <see cref="PythonDiagnosticsException"/> 则原样向上传播，
        /// 让热重载器的 HotReloadFailed 事件收到结构化的 Python traceback。
        /// </summary>
        internal IPythonModule InternalImportModule(string filePath)
        {
            EnsureInitialized();
            EnforceAllowedRoot(filePath);
            var moduleName = System.IO.Path.GetFileNameWithoutExtension(filePath);
            // 强制 reload: 删掉旧缓存，重新 import（IPythonRuntime.ImportModule 热重载语义）
            _loadedModules.Remove(filePath);
            var module = _runtime.ImportModule(moduleName, filePath);   // may throw PythonDiagnosticsException
            _loadedModules[filePath] = module;
            return module;
        }

        /// <summary>
        /// §D2.4 DryRun import 检查：尝试 import 给定目录下所有 .py，
        /// 每个文件产出一条 <see cref="PythonImportDiagnostic"/>。
        /// 不阻断其他文件的检查（单文件失败 → continue）。
        /// 典型在 <see cref="BlueprintLauncher.DryRun"/> 之前或之后调用，把结果附加到诊断列表。
        /// </summary>
        public IReadOnlyList<PythonImportDiagnostic> DryRunImports(string directory)
        {
            EnsureInitialized();

            var result = new List<PythonImportDiagnostic>();
            if (string.IsNullOrEmpty(directory) || !System.IO.Directory.Exists(directory))
                return result;

            var scanned = PythonRegisterScanner.ScanDirectory(directory);

            foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.py", System.IO.SearchOption.TopDirectoryOnly))
            {
                scanned.TryGetValue(file, out var handlers);
                handlers ??= Array.Empty<PythonHandlerDescriptor>();

                try
                {
                    EnforceAllowedRoot(file);
                    var moduleName = System.IO.Path.GetFileNameWithoutExtension(file);
                    _runtime.ImportModule(moduleName, file);   // dry-import: may throw

                    result.Add(new PythonImportDiagnostic
                    {
                        FilePath = file,
                        Success  = true,
                        Handlers = handlers,
                    });
                }
                catch (PythonDiagnosticsException pex)
                {
                    result.Add(new PythonImportDiagnostic
                    {
                        FilePath        = file,
                        Success         = false,
                        Error           = $"{pex.PythonExceptionType}: {pex.Message}",
                        PythonTraceback = pex.PythonTraceback,
                        Handlers        = handlers,
                    });
                }
                catch (Exception ex)
                {
                    result.Add(new PythonImportDiagnostic
                    {
                        FilePath = file,
                        Success  = false,
                        Error    = ex.Message,
                        Handlers = handlers,
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// §D2.3: 启用热重载。返回已启动的 <see cref="PythonHotReloader"/> 实例，
        /// 宿主持有引用直到不再需要热重载时 Dispose。
        /// </summary>
        public PythonHotReloader EnableHotReload(string directory, PythonSandboxOptions? options = null)
        {
            if (options != null) Initialize(options);
            else EnsureInitialized();

            var reloader = new PythonHotReloader(this, directory, _options);
            reloader.Start();
            return reloader;
        }

        // ── 内部 ────────────────────────────────────────────────────────────

        private void EnsureInitialized()
        {
            if (!_initialized) Initialize(_options);
        }

        private void EnforceAllowedRoot(string path)
        {
            var root = _options.AllowedRootDirectory;
            if (string.IsNullOrEmpty(root)) return;
            // 简化版守护:绝对路径形态,确保 path 以 root 开头(已规范化)。
            // 真实生产应走 Path.GetFullPath + StartsWith + 大小写策略;这里 PoC 够用。
            var abs = System.IO.Path.GetFullPath(path);
            var rootAbs = System.IO.Path.GetFullPath(root);
            if (!abs.StartsWith(rootAbs, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException(
                    $"Python 路径 '{abs}' 越出沙箱允许根 '{rootAbs}'。");
            }
        }

        private IPythonModule LoadOrReuseModule(string filePath)
        {
            // 同一 filePath 复用一份 module 实例;reload 走 Shutdown + 新 Initialize 重新装。
            if (_loadedModules.TryGetValue(filePath, out var cached)) return cached;
            var moduleName = System.IO.Path.GetFileNameWithoutExtension(filePath);
            var module = _runtime.ImportModule(moduleName, filePath);
            _loadedModules[filePath] = module;
            return module;
        }

        /// <summary>
        /// §D2.2 关键:给定目标委托类型(<paramref name="delegateType"/>)+ Python 函数引用,
        /// 构造一个跟 delegate 同签名的 .NET 委托,内部 routes 到 <see cref="IPythonModule.Invoke"/>。
        /// 通过 <see cref="DynamicInvokeShim{TDel}"/> 走 <c>Delegate.CreateDelegate</c> + 反射构造 ——
        /// 不依赖 Reflection.Emit / IL.Emit (§B12 划线)。
        /// </summary>
        internal Delegate BuildPythonInvokerDelegate(Type delegateType, IPythonModule module, string functionName)
        {
            // 抓 delegate 的 Invoke 签名,构造一个 args[] 适配的 generic shim,
            // 然后通过反射调用 PythonInvokerShim.MakeDelegate<TDel>(...) 转成强类型委托。
            var makeMethod = typeof(PythonInvokerShim)
                .GetMethod(nameof(PythonInvokerShim.MakeDelegate), BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(delegateType);

            var del = (Delegate)makeMethod.Invoke(null, new object[] { module, functionName })!;
            return del;
        }
    }

    /// <summary>
    /// 内部:给一个目标委托类型 TDel,返回一个 <c>TDel</c> 实例,内部把所有参数装 object[] 转给
    /// <see cref="IPythonModule.Invoke"/>,再把返回值强转成 TDel 的返回类型。
    /// 用反射 + Delegate.CreateDelegate(没有 Emit),实现极简 wrapper。
    /// </summary>
    internal static class PythonInvokerShim
    {
        // 单一 generic method,被 PythonHandlerRegistry.BuildPythonInvokerDelegate 反射闭合泛型调用。
        public static TDel MakeDelegate<TDel>(IPythonModule module, string functionName) where TDel : Delegate
        {
            var invokeMethod = typeof(TDel).GetMethod("Invoke")!;
            var paramInfos = invokeMethod.GetParameters();
            var paramCount = paramInfos.Length;

            // 把 TDel 的调用展开成: (a1,a2,..) => Invoke(module, fn, new object?[] { a1, a2, .. })
            // 用 LINQ Expression 构造,无 Emit。
            var paramExprs = paramInfos
                .Select(p => System.Linq.Expressions.Expression.Parameter(p.ParameterType, p.Name))
                .ToArray();

            var argsArrayInit = System.Linq.Expressions.Expression.NewArrayInit(
                typeof(object),
                paramExprs.Select(p => System.Linq.Expressions.Expression.Convert(p, typeof(object))));

            // PythonInvokerHelper.Call(module, functionName, args) → object?
            var helperMethod = typeof(PythonInvokerShim).GetMethod(nameof(Call), BindingFlags.NonPublic | BindingFlags.Static)!;

            var callExpr = System.Linq.Expressions.Expression.Call(
                helperMethod,
                System.Linq.Expressions.Expression.Constant(module),
                System.Linq.Expressions.Expression.Constant(functionName),
                argsArrayInit);

            // 返回值 cast:
            //   void                   → 丢掉 object? 返回值
            //   ValueTuple<...>(§D2.6.4)→ ConstructValueTuple<TR>(callExpr) 装 (output, state) 二元组
            //   其他                    → Convert 到 TDel 返回类型(原路径)
            System.Linq.Expressions.Expression body;
            var ret = invokeMethod.ReturnType;
            if (ret == typeof(void))
            {
                body = callExpr;   // 丢掉 object? 返回值
            }
            else if (ret.IsGenericType && IsValueTupleDef(ret.GetGenericTypeDefinition()))
            {
                var ctor = typeof(PythonInvokerShim)
                    .GetMethod(nameof(ConstructValueTuple), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(ret);
                body = System.Linq.Expressions.Expression.Call(ctor, callExpr);
            }
            else
            {
                body = System.Linq.Expressions.Expression.Convert(callExpr, ret);
            }

            var lambda = System.Linq.Expressions.Expression.Lambda<TDel>(body, paramExprs);
            return lambda.Compile();
        }

        // 实际调用入口 —— Expression 树编译后的委托内部调这个,经 IPythonModule 转给 Python 运行时。
        // §D2.4: PythonDiagnosticsException 原样穿透（让调用方收到 Python traceback）。
        // 其他异常也穿透 —— 不在这一层吞掉，宿主 / DryRun 自己决定怎么处理。
        private static object? Call(IPythonModule module, string functionName, object?[] args)
            => module.Invoke(functionName, args);   // may throw PythonDiagnosticsException

        // §D2.6.4 增量协议:把 raw 装到 ValueTuple<...> (TR)。
        //   - raw is TR              → 直接返回(Mock 路径:测试桩直接喂 ValueTuple)
        //   - raw is object?[]       → 反射 ValueTuple ctor 装(真 Python:UnboxBestEffort 把 PyTuple 解包成 object?[])
        //   - 其他                   → InvalidCastException(handler 实现错误,Watch 会 catch)
        private static TR ConstructValueTuple<TR>(object? raw) where TR : struct
        {
            if (raw is TR direct) return direct;
            if (raw is not object?[] arr)
                throw new InvalidCastException(
                    $"§D2.6.4 ValueTuple 反序列化失败:期望 {typeof(TR).Name},实际 {raw?.GetType().Name ?? "null"}。" +
                    "Python handler 必须返回 tuple/list,或 C# 直接返回 ValueTuple<...>。");

            var elemTypes = typeof(TR).GetGenericArguments();
            if (arr.Length != elemTypes.Length)
                throw new InvalidCastException(
                    $"§D2.6.4 ValueTuple 元数不匹配:期望 {elemTypes.Length},实际 tuple 长度 {arr.Length}。");

            var ctor = typeof(TR).GetConstructor(elemTypes)
                       ?? throw new InvalidOperationException($"ValueTuple ctor for {typeof(TR)} 找不到 — 这不应发生。");
            return (TR)ctor.Invoke(arr);
        }

        private static bool IsValueTupleDef(Type genericTypeDef)
            => genericTypeDef == typeof(ValueTuple<,>) ||
               genericTypeDef == typeof(ValueTuple<,,>) ||
               genericTypeDef == typeof(ValueTuple<,,,>) ||
               genericTypeDef == typeof(ValueTuple<,,,,>) ||
               genericTypeDef == typeof(ValueTuple<,,,,,>) ||
               genericTypeDef == typeof(ValueTuple<,,,,,,>);
    }
}
