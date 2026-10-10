using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Python.Runtime;

namespace Hevo.Charting.PythonNet
{
    /// <summary>入参占位:第 <see cref="Index"/> 个参数已拷进 <see cref="PythonInputBuffers"/>,本次长度 <see cref="Length"/>。</summary>
    internal readonly record struct PinnedArg(int Index, int Length);

    /// <summary>
    /// 一个调用点(ComputeFeature / PlotFeature 的一个订阅,挂在 <see cref="Hevo.Charting.LowCode.ResultBufferPool.HostCache"/> 上)
    /// 的 Python 入参缓冲:每个参数一块固定在 POH 上的 double[],以及指向它的 numpy ndarray 视图(只在扩容时重建)。
    /// 每次调用只把输入列 memcpy 进固定缓冲一次,传给 Python 的是 <c>full[:n]</c> 切片视图,不再 np.empty + memcpy。
    /// <para>
    /// <b>输入 ndarray 只在本次调用内有效</b>:下一次调用会改写同一块内存。handler 要跨调用保留输入必须 <c>.copy()</c>。
    /// 视图通过一个持有 .NET 数组引用的 Python 对象做 base,handler 即使把视图存起来,底层内存也不会被回收(只是内容会变)。
    /// </para>
    /// 单线程使用:调用点一次只有一个调用在跑(WatchAsync single-flight);超时时整个 ResultBufferPool 被弃用。
    /// </summary>
    internal sealed class PythonInputBuffers
    {
        private sealed class Slot
        {
            public double[] Array = System.Array.Empty<double>();
            public PyObject? Full;   // 覆盖整个容量的 ndarray 视图(GIL 内创建 / 释放)
            public readonly List<PyObject> Stale = new();  // 换缓冲后待释放的旧视图(Stage 不持 GIL,留到下次 View 时在 GIL 内释放)
        }

        private readonly List<Slot> _slots = new();

        /// <summary>调用线程上(还登记着列读者)把输入列拷进第 <paramref name="index"/> 个固定缓冲,返回占位。</summary>
        public PinnedArg Stage(int index, ReadOnlySpan<double> column)
        {
            while (_slots.Count <= index) _slots.Add(new Slot());
            var slot = _slots[index];
            int cap = Hevo.Charting.LowCode.CapacityPolicy.Resize(slot.Array.Length, column.Length);
            if (cap != slot.Array.Length)   // 不够了就扩,远小于容量就缩(CapacityPolicy 统一规则)
            {
                slot.Array = GC.AllocateUninitializedArray<double>(cap, pinned: true);
                if (slot.Full != null) { slot.Stale.Add(slot.Full); slot.Full = null; }
            }
            column.CopyTo(slot.Array);
            return new PinnedArg(index, column.Length);
        }

        /// <summary>GIL 内:第 <paramref name="index"/> 个参数本次长度的 ndarray(<c>full[:n]</c>,调用方 Dispose)。</summary>
        public PyObject View(int index, int length)
        {
            var slot = _slots[index];
            foreach (var old in slot.Stale) old.Dispose();
            slot.Stale.Clear();
            slot.Full ??= Helpers.MakeView(slot.Array);
            return Helpers.Head(slot.Full, length);
        }

        /// <summary>测试用:第 index 个参数当前的固定缓冲。</summary>
        internal double[] ArrayForTest(int index) => _slots[index].Array;

        private static class Helpers
        {
            private static PyObject? s_view, s_head;

            // __array_interface__ 对象做 base:ndarray 引用它,它引用 .NET 数组(pythonnet 包装对象持 GCHandle),
            // 视图活着数组就活着;数组在 POH 上,地址不变。
            // 普通字符串拼接(原始字符串字面量是 C# 11,开发规范要求可在 .NET 5 SDK 下编译)
            private const string Code =
                "import numpy as _np\n" +
                "class _HevoPinned(object):\n" +
                "    __slots__ = ('__array_interface__', '_owner')\n" +
                "    def __init__(self, addr, n, owner):\n" +
                "        self.__array_interface__ = {'data': (addr, False), 'shape': (n,), 'typestr': '<f8', 'version': 3}\n" +
                "        self._owner = owner\n" +
                "def view(addr, n, owner):\n" +
                "    return _np.asarray(_HevoPinned(addr, n, owner))\n" +
                "def head(a, n):\n" +
                "    return a[:n]\n";

            private static void Ensure()
            {
                if (s_view != null) return;
                using var globals = new PyDict();
                PythonEngine.Exec(Code, globals);
                s_head = globals.GetItem("head");
                s_view = globals.GetItem("view");
            }

            public static PyObject MakeView(double[] pinned)
            {
                Ensure();
                long addr = (long)Marshal.UnsafeAddrOfPinnedArrayElement(pinned, 0);
                using var a = new PyInt(addr);
                using var n = new PyInt(pinned.Length);
                using var owner = PyObject.FromManagedObject(pinned);
                return s_view!.Invoke(a, n, owner);
            }

            public static PyObject Head(PyObject full, int length)
            {
                Ensure();
                using var n = new PyInt(length);
                return s_head!.Invoke(full, n);
            }
        }
    }
}
