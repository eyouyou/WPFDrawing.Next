using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Hevo.Charting.PythonNet
{
    /// <summary>
    /// §D2.5.7 Python DLL 路径探测链 —— 启动 <see cref="PythonNetRuntime"/> 之前
    /// 决定 <c>Runtime.PythonDLL</c> 写哪个值。优先级降序:
    /// <list type="number">
    ///   <item><c>PythonNetRuntime(pythonDllPath: ...)</c> ctor 显式传入</item>
    ///   <item>env <c>PYTHONNET_PYDLL</c>(pythonnet 标准约定)</item>
    ///   <item>env <c>PYTHONHOME</c> + 平台默认 dll 名探测</item>
    ///   <item>典型安装路径(Windows: %LOCALAPPDATA%\Programs\Python; Linux/macOS: 系统 lib 路径)</item>
    /// </list>
    /// 全找不到 → 返回 null。<see cref="PythonNetRuntime.Initialize"/> 接到 null 不主动设
    /// <c>Runtime.PythonDLL</c>,让 pythonnet 走自身探测(典型 LoadLibrary 系统路径搜索),
    /// 仍找不到时启动会抛 <c>DllNotFoundException</c> + 安装指引。
    /// </summary>
    public static class PythonHomeResolver
    {
        // pythonnet 3.0.5 官方支持的 CPython ABI 范围(限定 3.7-3.12)。
        // 3.13+ 当前 pythonnet 还没适配 ABI 变更,误选会抛 BadPythonDllException。
        // resolver 跳过不在白名单的版本,落到下一候选;升级 pythonnet 时把上界调高。
        private static readonly HashSet<int> SupportedMinorVersions = new() { 7, 8, 9, 10, 11, 12 };

        /// <summary>探测 Python DLL 绝对路径。失败返回 null。</summary>
        public static string? Resolve()
        {
            // 2. PYTHONNET_PYDLL
            var fromPydll = Environment.GetEnvironmentVariable("PYTHONNET_PYDLL");
            if (!string.IsNullOrWhiteSpace(fromPydll) && File.Exists(fromPydll))
                return fromPydll;

            // 3. PYTHONHOME + 平台默认 dll 名
            var home = Environment.GetEnvironmentVariable("PYTHONHOME");
            if (!string.IsNullOrWhiteSpace(home))
            {
                var probed = ProbeInDirectory(home!);
                if (probed != null) return probed;
            }

            // 4. 典型安装路径
            foreach (var dir in EnumerateDefaultInstallDirs())
            {
                var probed = ProbeInDirectory(dir);
                if (probed != null) return probed;
            }

            return null;
        }

        private static string? ProbeInDirectory(string dir)
        {
            if (!Directory.Exists(dir)) return null;

            // Windows: pythonXY.dll(典型 python310.dll / python311.dll)
            // Linux:   libpythonX.Y.so / libpythonX.Y.so.1.0
            // macOS:   libpythonX.Y.dylib
            string[] patterns = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new[] { "python3*.dll" }
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? new[] { "libpython3*.dylib" }
                    : new[] { "libpython3*.so", "libpython3*.so.1.0" };

            foreach (var pattern in patterns)
            {
                try
                {
                    var hit = Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly)
                        // 过滤:① 排除 python3.dll(stable ABI 通用 stub,首选具体版本);
                        //       ② 排除超出 pythonnet 支持范围的版本(3.13+ 抛 BadPythonDllException)。
                        .Where(p =>
                        {
                            var name = Path.GetFileNameWithoutExtension(p);
                            if (name.Equals("python3", StringComparison.OrdinalIgnoreCase)) return false;
                            return TryParseMinorVersion(name, out var minor) && SupportedMinorVersions.Contains(minor);
                        })
                        // 在支持范围内尽量选最新版本(字典序对 python37..python312 单调递增正确)。
                        .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    if (hit != null) return hit;
                }
                catch
                {
                    // 目录访问异常吞掉,继续下一个
                }
            }
            return null;
        }

        /// <summary>从 startDir 起向上枚举若干层祖先目录(包含 startDir 自身)。</summary>
        private static System.Collections.Generic.IEnumerable<string> EnumerateAncestors(string startDir, int maxDepth)
        {
            var current = Path.TrimEndingDirectorySeparator(startDir);
            for (int i = 0; i < maxDepth && !string.IsNullOrEmpty(current); i++)
            {
                yield return current;
                current = Path.GetDirectoryName(current) ?? string.Empty;
            }
        }

        /// <summary>枚举 dir 下匹配 pattern 的子目录,目录访问异常吞掉。</summary>
        private static System.Collections.Generic.IEnumerable<string> EnumerateSubDirsSafe(string dir, string pattern)
        {
            string[] hits;
            try
            {
                if (!Directory.Exists(dir)) return Array.Empty<string>();
                hits = Directory.GetDirectories(dir, pattern, SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return Array.Empty<string>();
            }
            return hits;
        }

        /// <summary>从 dll 文件名解析 minor 版本号 —— "python311"→11, "libpython3.10"→10 之类。</summary>
        private static bool TryParseMinorVersion(string fileNameNoExt, out int minor)
        {
            minor = 0;
            // Windows: pythonXY  (X 永远 3,Y 是 minor —— 7..12 单/双位都要支持)
            // Unix:    libpython3.X  /  libpython3.X.so.1.0 → 这里传进来是 "libpython3.10" 或 "libpython3.10.so"
            // 通用做法:从右往左找第一段连续数字,前面应该是 "3" 或 "3."。
            int end = fileNameNoExt.Length;
            // 跳过尾部非数字(.so / .so.1.0 / .dll 等已被 GetFileNameWithoutExtension 处理掉部分,残留 ".so" 之类的需要剥)
            while (end > 0 && !char.IsDigit(fileNameNoExt[end - 1])) end--;
            int start = end;
            while (start > 0 && char.IsDigit(fileNameNoExt[start - 1])) start--;
            if (start == end) return false;

            // 解析尾部数字段。可能是 "10" / "11" / "310" / "311" 几种形态:
            //   - "python311" → 末尾数字段 "311" → 取最后两位 minor = 11
            //   - "libpython3.10" → 末尾数字段 "10" → minor = 10
            var tail = fileNameNoExt.Substring(start, end - start);
            if (tail.Length == 0) return false;
            // 把 "311" 这种合在一起的拆开:前面是 "3",后面是 minor。
            if (tail.Length >= 3 && tail[0] == '3')
            {
                return int.TryParse(tail.Substring(1), out minor);
            }
            return int.TryParse(tail, out minor);
        }

        private static System.Collections.Generic.IEnumerable<string> EnumerateDefaultInstallDirs()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // 0. Portable 部署:应用同目录或上级目录的 Python3* 子目录(典型仓库自带运行时,
                //    bin\Debug\net8.0\Python312\python312.dll 这种布局)。优先级最高 —— portable
                //    部署的意图明确:"用我携带的 Python,别去探测系统装的"。
                //    maxDepth=8 覆盖最深布局:仓库根/Project/bin/Debug/<TFM>/<RID>/  共 5 层;
                //    再加 dotnet publish 的 publish/ 子目录、单体打包等场景,留 8 层余量。
                var baseDir = AppContext.BaseDirectory;
                foreach (var ancestor in EnumerateAncestors(baseDir, maxDepth: 8))
                {
                    foreach (var sub in EnumerateSubDirsSafe(ancestor, "Python3*"))
                        yield return sub;
                }

                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var programs = Path.Combine(localAppData, "Programs", "Python");
                if (Directory.Exists(programs))
                {
                    // %LOCALAPPDATA%\Programs\Python\Python310\, Python311\, ...
                    foreach (var sub in Directory.EnumerateDirectories(programs, "Python3*"))
                        yield return sub;
                }
                // 系统级安装(管理员)
                foreach (var p in new[] { @"C:\Python311", @"C:\Python310", @"C:\Python39", @"C:\Python38" })
                    if (Directory.Exists(p)) yield return p;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                foreach (var p in new[]
                {
                    "/opt/homebrew/Frameworks/Python.framework/Versions/3.11/lib",
                    "/opt/homebrew/Frameworks/Python.framework/Versions/3.10/lib",
                    "/usr/local/Frameworks/Python.framework/Versions/3.11/lib",
                    "/usr/local/Frameworks/Python.framework/Versions/3.10/lib",
                    "/Library/Frameworks/Python.framework/Versions/3.11/lib",
                    "/Library/Frameworks/Python.framework/Versions/3.10/lib",
                })
                    if (Directory.Exists(p)) yield return p;
            }
            else
            {
                foreach (var p in new[]
                {
                    "/usr/lib/x86_64-linux-gnu",
                    "/usr/lib64",
                    "/usr/lib",
                })
                    if (Directory.Exists(p)) yield return p;
            }
        }
    }
}
