using System.Collections.Generic;
using System.Linq;
using Python.Runtime;

namespace Hevo.Charting.PythonNet
{
    /// <summary>
    /// §D2.5.4 import 沙箱 —— 启动时把 Python <c>builtins.__import__</c> 替换成自家版本,
    /// 拦掉 <see cref="PythonSandboxOptions.BlockedImports"/> 里列出来的根模块。
    ///
    /// <para>
    /// <b>威胁模型</b>:误用 (分析师抄了 stackoverflow 的 <c>os.system(...)</c>) &gt; 主动恶意。
    /// 完美沙箱不可能(<c>__builtins__["__import__"]("os")</c> / 已被 numpy 间接 import 过的 <c>os</c>
    /// 等已知绕过路径承认存在),目标是"误用立刻 fail-fast"。
    /// </para>
    ///
    /// <para>
    /// <b>实装策略</b>:不动 <c>sys.modules</c>(避免破 pythonnet 自己用的 <c>importlib</c> / <c>sys</c>),
    /// 仅替换 <c>__import__</c> —— 已 import 过的模块 reference 仍然能用(包括 numpy 自己内部用的),
    /// 但用户 <c>.py</c> 新写 <c>import os</c> / <c>import subprocess</c> 直接 <see cref="System.Exception"/>。
    /// </para>
    ///
    /// <para>
    /// <b>幂等</b>:重复 <see cref="Install"/> 安全 —— 通过 <c>builtins._hevo_import_installed</c> flag 判定。
    /// 但二次 Install **不会更新**已安装的 _BLOCKED 列表(进程内只生效首次 BlockedImports);
    /// 业务侧二次 Initialize 改 BlockedImports 需要重启进程。
    /// </para>
    /// </summary>
    internal static class ImportInterceptor
    {
        /// <summary>调用前必须 acquire GIL。</summary>
        public static void Install(IReadOnlyCollection<string> blockedImports)
        {
            if (blockedImports == null || blockedImports.Count == 0) return;

            // 把 .NET set 序列化成 Python set literal:{'os', 'subprocess', ...}
            // 单引号内的 ' 转义成 \' 防注入。
            var blockedLiteral = "{" + string.Join(",",
                blockedImports.Select(s => "'" + (s ?? "").Replace("\\", "\\\\").Replace("'", "\\'") + "'")) + "}";

            // 注:Python f-string 里的 { } 要双写转义。
            // 注:installed flag 让重复调用幂等(同进程多个 PythonNetRuntime 共享一份 builtin override)。
            var script = @"
import builtins as _bi

if not getattr(_bi, '_hevo_import_installed', False):
    _hevo_orig_import = _bi.__import__
    _hevo_blocked = " + blockedLiteral + @"

    def _hevo_import(name, globals=None, locals=None, fromlist=(), level=0):
        root = name.split('.')[0]
        if root in _hevo_blocked:
            raise ImportError(
                ""[Hevo Sandbox] import '"" + str(name) + ""' is blocked. ""
                ""(threat model: stackoverflow-grade 误用拦截,生产安全靠 git review)"")
        return _hevo_orig_import(name, globals, locals, fromlist, level)

    _bi.__import__ = _hevo_import
    _bi._hevo_import_installed = True
";

            // PythonEngine.Exec 在 __main__ scope 跑这段 setup —— 一次性副作用,无需返回值。
            PythonEngine.Exec(script);
        }
    }
}
