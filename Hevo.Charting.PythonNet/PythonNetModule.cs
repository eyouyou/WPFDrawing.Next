using System;
using System.Collections.Generic;
using Python.Runtime;

namespace Hevo.Charting.PythonNet
{
    /// <summary>
    /// §D2.5.2 真实 <see cref="IPythonModule"/> 实装:持一个 Python 模块的 PyObject 引用,
    /// 暴露 <see cref="HasFunction"/> / <see cref="Invoke"/> / <see cref="ListRegisteredHandlers"/>。
    /// <para>
    /// 与 PythonNetRuntime 1:N 关系:每个 <c>.py</c> 文件 import 后产生一个本类实例,缓存在
    /// <see cref="PythonHandlerRegistry._loadedModules"/> 字典里供后续 <c>RegisterModule</c> 复用。
    /// </para>
    /// <para>
    /// 调用 <see cref="Invoke"/> 时跨线程也安全 —— 内部每次 <c>using (Py.GIL())</c>。性能损耗就是
    /// 一次 GIL 抢锁(几十 ns)+ 参数 marshalling。指标算子层(1-10Hz)完全够用。
    /// </para>
    /// </summary>
    public sealed class PythonNetModule : IPythonModule
    {
        private readonly PyObject _module;
        private readonly string _filePath;
        private readonly int _perCallTimeoutMs;

        public string Name { get; }

        internal PythonNetModule(PyObject module, string name, string filePath)
            : this(module, name, filePath, perCallTimeoutMs: 0) { }

        internal PythonNetModule(PyObject module, string name, string filePath, int perCallTimeoutMs)
        {
            _module           = module ?? throw new ArgumentNullException(nameof(module));
            Name              = name   ?? throw new ArgumentNullException(nameof(name));
            _filePath         = filePath ?? "";
            _perCallTimeoutMs = perCallTimeoutMs;
        }

        public bool HasFunction(string functionName)
        {
            if (string.IsNullOrEmpty(functionName)) return false;
            using (Py.GIL())
            {
                if (!_module.HasAttr(functionName)) return false;
                using var fn = _module.GetAttr(functionName);
                return fn.IsCallable();
            }
        }

        /// <summary>
        /// 调用模块上 <paramref name="functionName"/> 函数。
        /// 参数按位置 <see cref="PythonMarshaller.Box"/> 装箱,返回值反向用调用方期望类型 unbox。
        /// 调用方拿到的 <c>object?</c> 可能是标量 / <see cref="ReadOnlyMemory{T}"/> / 字符串 / null;
        /// PythonInvokerShim 会再次 Convert 到强类型委托返回值。
        /// </summary>
        public object? Invoke(string functionName, params object?[] args)
        {
            if (string.IsNullOrEmpty(functionName))
                throw new ArgumentNullException(nameof(functionName));

            // §D2.5.5 超时硬切:_perCallTimeoutMs > 0 时整个 Python 调用包在 Task.Run 里;
            // 调用方 Wait 超时 → 抛 TimeoutException 放出来,Python 线程继续跑(orphan,详见 PerCallTimeoutWatchdog 注释)。
            // <= 0 = inline 跑。
            return PerCallTimeoutWatchdog.RunWithTimeout(_perCallTimeoutMs, () => InvokeCore(functionName, args));
        }

        private object? InvokeCore(string functionName, object?[] args)
        {
            using (Py.GIL())
            {
                if (!_module.HasAttr(functionName))
                {
                    throw new InvalidOperationException(
                        $"Python module '{Name}' 不含函数 '{functionName}'。");
                }

                using var fn = _module.GetAttr(functionName);
                if (!fn.IsCallable())
                {
                    throw new InvalidOperationException(
                        $"Python module '{Name}.{functionName}' 不是 callable。");
                }

                var pyArgs = new PyObject[args.Length];
                try
                {
                    for (int i = 0; i < args.Length; i++)
                        pyArgs[i] = PythonMarshaller.Box(args[i]);

                    PyObject result;
                    try
                    {
                        result = fn.Invoke(pyArgs);
                    }
                    catch (PythonException pex)
                    {
                        throw PythonNetRuntime.TranslateException(pex, _filePath, functionName);
                    }

                    using (result)
                    {
                        // 返回 PyObject 让调用方决定怎么 unbox —— 这里走最常见 case 直接 ToManagedValue。
                        // 调用方(PythonInvokerShim.Call → Expression.Convert)期望的是裸 object 然后强 cast,
                        // 所以这里把 ndarray / 标量都 unbox 到对应 .NET 类型再返回。
                        return UnboxBestEffort(result);
                    }
                }
                finally
                {
                    foreach (var p in pyArgs) p?.Dispose();
                }
            }
        }

        /// <summary>
        /// 模块上 <c>_hevo_handlers</c> 字典(由 §D2.5.6 hevo_indicators 包的 <c>@register</c> 装饰器维护)
        /// 解析成 <see cref="PythonHandlerDescriptor"/> 列表。
        /// <para>
        /// 当前(轮 A)实装走 <b>静态正则扫描</b>(<see cref="PythonRegisterScanner.ScanFile"/>),
        /// 不依赖 hevo_indicators 包运行时落地;本方法目前返回 <see cref="Array.Empty"/>,
        /// 等 §D2.5.6 落地后再补真实运行时反射路径。
        /// </para>
        /// </summary>
        public IReadOnlyList<PythonHandlerDescriptor> ListRegisteredHandlers()
            => Array.Empty<PythonHandlerDescriptor>();

        // ===== 私有:Python → .NET 通用 unbox(没具体期望类型时的兜底) =====
        // PythonInvokerShim 走 Expression.Convert 强 cast,所以返回 object 必须能 cast 到目标。
        // 走最常见的几种命中路径,其它由调用方处理。
        //
        // ⚠️ 关键:InvokeCore 在 `using (result) {...}` 内调本方法,出块即 Dispose。
        //   所以**任何返回的 PyObject 引用都是 use-after-dispose**。本方法必须把所有需要的数据
        //   提前转成纯 .NET 对象(数组 / 字典 / 标量),不能裸返 PyObject。
        //
        //   - ndarray → ReadOnlyMemory&lt;T&gt;(memcpy 出来)
        //   - dict → Dictionary&lt;string, object?&gt;(递归 unbox 每个 value)
        //   - 标量 → As&lt;T&gt;()
        //   - 兜底:返 null,调用方按缺省值兜底
        private static object? UnboxBestEffort(PyObject py)
        {
            if (py == null || py.IsNone()) return null;

            // numpy ndarray 探测:dtype + size 同时存在 → 视为 ndarray。
            if (py.HasAttr("dtype") && py.HasAttr("size"))
            {
                string dtName = "";
                try
                {
                    using var dtype = py.GetAttr("dtype");
                    using var dtypeName = dtype.GetAttr("name");
                    dtName = dtypeName.As<string>() ?? "";
                }
                catch { /* 不是真 ndarray,fall through */ }

                switch (dtName)
                {
                    case "float64": return ReadOnlyMemoryFromNumpy<double>(py);
                    case "float32": return ReadOnlyMemoryFromNumpy<float>(py);
                    case "int32":   return ReadOnlyMemoryFromNumpy<int>(py);
                    case "int64":   return ReadOnlyMemoryFromNumpy<long>(py);
                }
            }

            // §D2.6.4: Python tuple / list 探测 —— has __len__ + __getitem__ 但没 dtype / 没 items()。
            // 增量协议 handler 返回 (output, next_state),Python 端就是 tuple。这里递归 unbox 每个元素到
            // 纯 .NET 对象数组,shim 层的 ConstructValueTuple<TR> 再装到 ValueTuple<...>。
            // 必须排除字符串(也有 __getitem__)—— class.__name__ 显式校验。
            if (py.HasAttr("__getitem__") && py.HasAttr("__len__")
                && !py.HasAttr("items") /* not dict */
                && !py.HasAttr("dtype") /* not ndarray */)
            {
                try
                {
                    string clsName = "";
                    using (var cls = py.GetAttr("__class__"))
                    using (var nm = cls.GetAttr("__name__"))
                    {
                        clsName = nm.As<string>() ?? "";
                    }
                    if (clsName == "tuple" || clsName == "list")
                    {
                        using var lenObj = py.InvokeMethod("__len__", Array.Empty<PyObject>());
                        int n = lenObj.As<int>();
                        var arr = new object?[n];
                        for (int i = 0; i < n; i++)
                        {
                            using var item = py.GetItem(i);
                            arr[i] = UnboxBestEffort(item);
                        }
                        return arr;
                    }
                }
                catch
                {
                    // tuple/list 解析失败 → fall through 到 dict / 标量
                }
            }

            // dict 探测:Python dict 同时有 keys() 和 items(),把 entries 递归 unbox 到纯 .NET Dictionary。
            // (PlotFeature.Indicator 返回 dict 走这条路 —— 必须解构成托管对象,不能裸返 PyObject 让调用方
            //  在 InvokeCore using 块外继续摸 已 dispose 的引用 → GetItem 静默拿不到值。)
            if (py.HasAttr("keys") && py.HasAttr("items"))
            {
                try
                {
                    var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                    using var keys = py.InvokeMethod("keys", Array.Empty<PyObject>());
                    using var iter = keys.GetIterator();
                    while (iter.MoveNext())
                    {
                        var keyObj = iter.Current;
                        if (keyObj == null) continue;
                        string? keyStr = null;
                        try { keyStr = keyObj.As<string>(); }
                        catch { /* 非字符串 key 跳过 */ }
                        if (keyStr == null) { keyObj.Dispose(); continue; }
                        using var val = py.GetItem(keyObj);
                        dict[keyStr] = UnboxBestEffort(val);   // 递归(ndarray → ROM、嵌套 dict 等)
                        keyObj.Dispose();
                    }
                    return dict;
                }
                catch
                {
                    // dict-like 但解构失败 → fall through 到标量探测
                }
            }

            // 标量 / 字符串 —— 用 pythonnet 自带的类型谓词(PyInt.IsIntType / PyFloat.IsFloatType / PyString.IsStringType)
            // 替代旧版 try/catch 顺序探测 As<long>/As<double>/As<bool>/As<string>。
            // 旧版每个 dict entry 跑 4 次 try/catch,失败时抛 InvalidCastException 被 VS Debug Output 显示成
            // first-chance 噪音 —— bb_breakout 单 tick 返几十个 dict entry 就刷出几十条。
            // 谓词路径零异常,性能也更快(避免 throw/catch unwinding)。
            //
            // 注:Python 的 bool 是 int 的子类,所以 PyInt.IsIntType 对 True/False 返 true。
            // 按 long 转换得到 0/1,跟旧版"先 As<long> 成功"的行为一致 —— 不需要单独 PyBool 分支。
            if (PyInt.IsIntType(py))       return py.As<long>();
            if (PyFloat.IsFloatType(py))   return py.As<double>();
            if (PyString.IsStringType(py)) return py.As<string>();

            // 兜底:不识别的类型(自定义 class 实例 / Decimal 等)—— 静默返 null,调用方按缺省值处理。
            // 绝不裸返 PyObject(已知会被 caller 的 using 块 dispose)。
            return null;
        }

        private static unsafe ReadOnlyMemory<T> ReadOnlyMemoryFromNumpy<T>(PyObject ndarray) where T : unmanaged
        {
            using var np = Py.Import("numpy");
            using var contig = np.InvokeMethod("ascontiguousarray", new PyObject[] { ndarray });
            using var sizeAttr = contig.GetAttr("size");
            int n = sizeAttr.As<int>();

            var arr = new T[n];
            if (n == 0) return arr;

            using var ctypes = contig.GetAttr("ctypes");
            using var dataAttr = ctypes.GetAttr("data");
            long src = dataAttr.As<long>();

            int byteCount = sizeof(T) * n;
            fixed (T* dst = arr)
            {
                Buffer.MemoryCopy((void*)(IntPtr)src, dst, byteCount, byteCount);
            }
            return arr;
        }
    }
}
