using System;
using System.IO;
using System.Text;
using Python.Runtime;

namespace Hevo.Charting.PythonNet
{
    /// <summary>
    /// §D2.5.6 把 <see cref="HevoIndicatorsSources"/> 的 Python 源代码物化到本地 TEMP,
    /// 添加到 sys.path,让 Python 端能 <c>import hevo_indicators</c>。
    /// 必须在 <c>PythonEngine.Initialize</c> 之后、首次 <c>Py.Import("hevo_indicators")</c> 之前调用。
    ///
    /// <para>
    /// <b>部署目录</b>:<c>%TEMP%/hevo_indicators_runtime/hevo_indicators/</c> ——
    /// stable across launches,让同进程多次 Initialize / 多 PythonNetRuntime 实例共享。
    /// 多进程间偶然覆盖同名文件没问题(内容一致)。
    /// </para>
    ///
    /// <para>
    /// <b>幂等</b>:多次调用安全 —— <see cref="EnsureInstalled"/> 只在首次执行时落盘,
    /// 之后直接 sys.path 加 (重复加被 contains check 拦掉)。
    /// </para>
    /// </summary>
    internal static class HevoIndicatorsBootstrap
    {
        // 部署根:hevo_indicators 包父目录(sys.path 加这个)
        private static readonly string DeployRoot = Path.Combine(Path.GetTempPath(), "hevo_indicators_runtime");
        // 包目录:hevo_indicators 包本体所在
        private static readonly string PackageDir = Path.Combine(DeployRoot, "hevo_indicators");

        private static bool _written;
        private static readonly object _writeLock = new();

        /// <summary>
        /// 确保 hevo_indicators package 已部署 + sys.path 已注册。
        /// 调用方必须已经在 <c>using (Py.GIL())</c> 内(本方法假设 GIL 持有)。
        /// </summary>
        public static void EnsureInstalled()
        {
            // 落盘 —— 全局只跑一次
            lock (_writeLock)
            {
                if (!_written)
                {
                    Directory.CreateDirectory(PackageDir);
                    var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                    foreach (var (fileName, content) in HevoIndicatorsSources.Files)
                    {
                        var dest = Path.Combine(PackageDir, fileName);
                        try
                        {
                            File.WriteAllText(dest, content, utf8NoBom);
                        }
                        catch (IOException)
                        {
                            // 多进程偶发竞争:文件被另一进程占用 → 假设另一进程内容一致,跳过即可。
                        }
                    }
                    _written = true;
                }
            }

            // sys.path.insert(0, DeployRoot) —— GIL 持有路径
            using var sys = Py.Import("sys");
            using var path = sys.GetAttr("path");
            using var pyDir = new PyString(DeployRoot);
            using var contains = path.InvokeMethod("__contains__", new PyObject[] { pyDir });
            if (!contains.As<bool>())
            {
                using var zero = new PyInt(0);
                using var _ = path.InvokeMethod("insert", new PyObject[] { zero, pyDir });
            }
        }
    }
}
