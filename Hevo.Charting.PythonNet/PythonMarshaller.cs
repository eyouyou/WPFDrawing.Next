using System;
using System.Runtime.InteropServices;
using Python.Runtime;

namespace Hevo.Charting.PythonNet
{
    /// <summary>
    /// §D2.5.3 跨边界数据转换:.NET → Python(Box) / Python → .NET(Unbox)。
    /// <para>
    /// 当前实装是 <b>第一波 Marshal.Copy 版</b>(<see cref="ReadOnlyMemory{T}"/> ↔ numpy ndarray
    /// 走一次 memcpy)。1000 点 double ~1μs,跟"把蓝图跑起来"比是 noise。
    /// </para>
    /// <para>
    /// 后续优化(§D2.5.3+)走 <c>MemoryHandle.Pin</c> + numpy <c>ctypeslib.as_array</c> 共享底层内存,
    /// 但需要管 GC pin 生命周期 + numpy 引用计数,放到 zero-copy 迭代里再做。
    /// </para>
    /// </summary>
    internal static class PythonMarshaller
    {
        /// <summary>
        /// .NET → Python:把任意支持类型装成 PyObject。null 走 <c>None</c>。
        /// 调用方拿到 PyObject 必须自己 Dispose(GIL 内 using)。
        /// </summary>
        /// <remarks>调用前必须已经 acquire GIL。</remarks>
        public static PyObject Box(object? value)
        {
            if (value is null)
            {
                // pythonnet 3.0.x 没暴露 PyObject.None 静态字段;builtins.None 是稳妥跨版本路径。
                using var builtins = Py.Import("builtins");
                return builtins.GetAttr("None");
            }

            switch (value)
            {
                case bool b:    return new PyInt(b ? 1 : 0);   // pythonnet 没 PyBool 公开 ctor,bool→int 走 truthiness
                case int i:     return new PyInt(i);
                case long l:    return new PyInt(l);
                case double d:  return new PyFloat(d);
                case float f:   return new PyFloat(f);
                case string s:  return new PyString(s);
                case DateTime dt: return new PyInt(dt.ToUniversalTime().Ticks);

                // ReadOnlyMemory<double>:首选业务数据形态(指标计算的核心)
                case ReadOnlyMemory<double> rom_d: return ToNumpyArray(rom_d, "float64");
                case Memory<double> m_d:           return ToNumpyArray(m_d.Span, "float64");
                case double[] arr_d:               return ToNumpyArray(arr_d.AsSpan(), "float64");

                case ReadOnlyMemory<int> rom_i: return ToNumpyArray(rom_i, "int32");
                case Memory<int> m_i:           return ToNumpyArray(m_i.Span, "int32");
                case int[] arr_i:               return ToNumpyArray(arr_i.AsSpan(), "int32");

                // 兜底:让 pythonnet 自己反射桥接(record / 自定义 .NET 类型 → Python 通用 .NET ref)
                default:
                    return value.ToPython();
            }
        }

        /// <summary>
        /// Python → .NET:把 PyObject 反向转成期望类型 <paramref name="expected"/>。
        /// 期望 <c>void</c> / null → 直接返 null。
        /// </summary>
        /// <remarks>调用前必须已经 acquire GIL,<paramref name="py"/> 由调用方负责 Dispose。</remarks>
        public static object? Unbox(PyObject py, Type expected)
        {
            if (py == null || py.IsNone()) return null;
            if (expected == typeof(void)) return null;

            // 标量:直接走 As<T>()
            if (expected == typeof(int))      return py.As<int>();
            if (expected == typeof(long))     return py.As<long>();
            if (expected == typeof(double))   return py.As<double>();
            if (expected == typeof(float))    return py.As<float>();
            if (expected == typeof(bool))     return py.As<bool>();
            if (expected == typeof(string))   return py.As<string>();
            if (expected == typeof(DateTime)) return new DateTime(py.As<long>(), DateTimeKind.Utc);

            // ndarray → ROM<T>
            if (expected == typeof(ReadOnlyMemory<double>)) return FromNumpyArray<double>(py);
            if (expected == typeof(ReadOnlyMemory<int>))    return FromNumpyArray<int>(py);
            if (expected == typeof(double[]))               return FromNumpyArrayToArray<double>(py);
            if (expected == typeof(int[]))                  return FromNumpyArrayToArray<int>(py);

            // §D2.6.4: Python tuple → C# ValueTuple<...>(2~7 元)。
            // 增量协议 handler 返回 (output, next_state),解包逐元素 Unbox 到对应槽位。
            if (expected.IsGenericType && IsValueTupleDef(expected.GetGenericTypeDefinition()))
                return UnboxValueTuple(py, expected);

            // 任意 .NET 引用对象兜底(pythonnet 桥接)
            return py.AsManagedObject(expected);
        }

        // §D2.6.4: 判断给定的泛型定义是不是 ValueTuple<...> 家族(2~7 元)。
        private static bool IsValueTupleDef(Type genericTypeDef)
            => genericTypeDef == typeof(ValueTuple<,>) ||
               genericTypeDef == typeof(ValueTuple<,,>) ||
               genericTypeDef == typeof(ValueTuple<,,,>) ||
               genericTypeDef == typeof(ValueTuple<,,,,>) ||
               genericTypeDef == typeof(ValueTuple<,,,,,>) ||
               genericTypeDef == typeof(ValueTuple<,,,,,,>);

        // §D2.6.4: Python tuple-like (PyTuple / list / 任何支持 __len__ + __getitem__) → ValueTuple<...>。
        // 长度不匹配 / 元素 Unbox 失败抛 InvalidCastException —— Watch handler 会 catch + log。
        private static object UnboxValueTuple(PyObject py, Type valueTupleType)
        {
            var elemTypes = valueTupleType.GetGenericArguments();
            int expectedLen = elemTypes.Length;

            using var lenPy = py.GetAttr("__len__").Invoke();
            int actualLen = lenPy.As<int>();
            if (actualLen != expectedLen)
            {
                throw new InvalidCastException(
                    $"Python tuple 返回长度 {actualLen} 跟期望 ValueTuple<...> 长度 {expectedLen} 不一致。" +
                    "增量协议要求 handler 返回严格 (output, next_state) 二元组(或对应元数)。");
            }

            var elems = new object?[expectedLen];
            for (int i = 0; i < expectedLen; i++)
            {
                using var item = py.GetItem(i);
                elems[i] = Unbox(item, elemTypes[i]);
            }

            // ValueTuple<T1,T2,...> ctor 接受跟元数一致的位置参数。
            var ctor = valueTupleType.GetConstructor(elemTypes)
                       ?? throw new InvalidOperationException(
                           $"ValueTuple ctor for {valueTupleType} 找不到 — 这不应发生。");
            return ctor.Invoke(elems);
        }

        // ===== ROM<T> ↔ numpy ndarray (Marshal.Copy 版) ============================

        /// <summary>
        /// 构造 <c>numpy.empty(n, dtype)</c>,把 <paramref name="span"/> 的字节 memcpy 到底层 buffer。
        /// </summary>
        private static unsafe PyObject ToNumpyArray<T>(ReadOnlySpan<T> span, string dtype) where T : unmanaged
        {
            using var np = Py.Import("numpy");
            using var len = new PyInt(span.Length);
            using var dt = new PyString(dtype);
            // np.empty(n, dtype) → ndarray
            var arr = np.InvokeMethod("empty", new PyObject[] { len, dt });

            if (span.Length == 0) return arr;

            // ndarray.ctypes.data 拿到底层 buffer 的 native ptr,memcpy 进去。
            using var ctypes = arr.GetAttr("ctypes");
            using var dataAttr = ctypes.GetAttr("data");
            long dst = dataAttr.As<long>();

            int byteCount = sizeof(T) * span.Length;
            fixed (T* src = span)
            {
                Buffer.MemoryCopy(src, (void*)(IntPtr)dst, byteCount, byteCount);
            }
            return arr;
        }

        private static unsafe PyObject ToNumpyArray<T>(ReadOnlyMemory<T> mem, string dtype) where T : unmanaged
            => ToNumpyArray(mem.Span, dtype);

        /// <summary>
        /// numpy ndarray → ReadOnlyMemory&lt;T&gt; —— 走 ascontiguousarray 保证连续后 memcpy 一次。
        /// </summary>
        private static unsafe ReadOnlyMemory<T> FromNumpyArray<T>(PyObject ndarray) where T : unmanaged
            => FromNumpyArrayToArray<T>(ndarray);

        private static unsafe T[] FromNumpyArrayToArray<T>(PyObject ndarray) where T : unmanaged
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
