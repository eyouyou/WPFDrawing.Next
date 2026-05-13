using System;
using Python.Runtime;

namespace Hevo.Charting.PythonNet
{
    /// <summary>
    /// 业务侧 C# delegate 注入 Python <c>__main__</c> 命名空间的桥。
    /// 注入后 py 代码内可像普通 builtin 一样调用,跟 numpy 函数同语义。
    ///
    /// <para>典型场景:py indicator 函数算到关键信号(建仓 / 止损 / 异常)时直接调
    /// 业务侧 C# 提供的副作用 SDK(下单 / 邮件 / 短信),不绕道 BlueprintHandler 中转。</para>
    ///
    /// <para>用法:
    /// <code>
    /// // C# 启动期(必须在 Python runtime 已初始化之后调,否则 Py.GIL() 抛):
    /// PyBusinessBridge.RegisterBuiltin("place_order", new Action&lt;PyObject&gt;(req =&gt; { /* 走 TradeApi */ }));
    /// PyBusinessBridge.RegisterBuiltin("send_email", new Action&lt;PyObject&gt;(req =&gt; { /* 走 SmtpClient */ }));
    /// </code>
    /// </para>
    /// <para>py 端:
    /// <code>
    /// # 直接调,跟 numpy 一样自然
    /// place_order({'symbol': 'current', 'qty': 100, 'price': 12.34})
    /// send_email({'subject': 'Entry', 'body': 'priority crossed 0.03'})
    /// </code>
    /// </para>
    ///
    /// <para><b>线程模型</b>:RegisterBuiltin 内部用 <see cref="Py.GIL"/> 锁,可在任意线程调用,
    /// 但必须在 <c>PythonEngine.Initialize</c> 之后(典型:<see cref="EmbeddedPythonHost.EnsureInitialized"/> 完成之后)。
    /// 注入后 py 调用 delegate 时跑在 indicator 解析线程(GIL 内),delegate 内部如果触发耗时
    /// I/O(SMTP / TradeApi),业务侧应自己用 <c>Task.Run</c> fire-and-forget 包一层,避免阻塞 indicator。</para>
    /// </summary>
    public static class PyBusinessBridge
    {
        /// <summary>
        /// 把 C# delegate 注入 py <c>__main__</c> 命名空间,以指定名字暴露成 py builtin。
        /// 重复注册同名 builtin 后注册的覆盖前注册的(跟 py global namespace 同语义)。
        /// </summary>
        /// <param name="name">py 端可见的 builtin 名字,典型 snake_case。</param>
        /// <param name="callback">业务 C# delegate。typical: <c>Action&lt;PyObject&gt;</c>(吃 py dict)
        /// 或 <c>Func&lt;PyObject, PyObject&gt;</c>(吃参数返结果)。</param>
        /// <exception cref="ArgumentNullException">name / callback 为 null。</exception>
        /// <exception cref="InvalidOperationException">PythonEngine 尚未初始化(Py.GIL 抛 PythonException 时包装一层提示)。</exception>
        public static void RegisterBuiltin(string name, Delegate callback)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            try
            {
                using (Py.GIL())
                {
                    using var main = Py.Import("__main__");
                    using var pyCallback = callback.ToPython();
                    main.SetAttr(name, pyCallback);
                }
            }
            catch (PythonException ex)
            {
                throw new InvalidOperationException(
                    $"PyBusinessBridge.RegisterBuiltin('{name}', ...) 失败 —— 请确认 PythonEngine 已经 Initialize "
                    + "(典型:在 EmbeddedPythonHost.Default.EnsureInitialized 之后调本方法)。",
                    ex);
            }
        }
    }
}
