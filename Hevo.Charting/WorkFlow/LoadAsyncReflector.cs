using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Hevo.Charting.WorkFlow
{
    /// <summary>
    /// "字符串上下文 + 反射 LoadAsync" 工具 —— 复用在两处:
    /// <list type="bullet">
    ///   <item><see cref="LowCode.Designer.BlueprintRunner"/> 自动 LoadAsync <see cref="LowCode.Designer.DataSourceModel.DefaultContext"/></item>
    ///   <item><see cref="CompositeDataSource{TSource,TItem}"/> 内联 <see cref="UpstreamSpec.DefaultContext"/></item>
    /// </list>
    /// 约定式 string → TContext 解析:<c>Parse(string)</c> / <c>FromString(string)</c> 静态方法,业务 TContext 顺手实现即可零配置接入。
    /// 同步等待最多 <c>timeout</c> 秒(sync-over-async 经 Task.Run 兜 UI 线程死锁)。
    /// </summary>
    internal static class LoadAsyncReflector
    {
        /// <summary>
        /// 反射调用 <c>dsInstance.LoadAsync(parseContext, [CancellationToken])</c>,同步等回包。
        /// 失败(没匹配的 LoadAsync overload / context 解析失败 / 超时 / 异常) 全部打 warning 后静默退出。
        /// </summary>
        /// <summary>
        /// 非阻塞版 —— 反射调用 <c>dsInstance.LoadAsync(parseContext)</c>,返回底层 Task 让调用方自己决定如何 await。
        /// 找不到匹配 overload / context 解析失败 → 返回 <see cref="Task.CompletedTask"/>(打 warning),
        /// 跟 <see cref="TryInvoke"/> 的 silent-skip 语义对齐,只是把"等回包"决定权交还给上层。
        /// <para>
        /// 设计动机:framework 现在支持 deferred load(BuildSchemaDeferred + StartLoading 两阶段),
        /// CompositeDataSource 内联 Upstreams 也要走"先订阅、后 LoadAsync"的延后启动路径。原来的
        /// sync-over-async <c>Wait(timeout)</c> 在 dashboard 多 cell 场景下串行阻塞,改成 Task 形态,
        /// 上层用 <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/> 并行点火。
        /// </para>
        /// </summary>
        public static Task TryInvokeAsync(object dsInstance, string contextText)
        {
            var dsType = dsInstance.GetType();
            var candidates = dsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "LoadAsync"
                            && m.GetParameters().Length >= 1
                            && m.GetParameters()[0].ParameterType != typeof(CancellationToken));

            foreach (var m in candidates)
            {
                var ps = m.GetParameters();
                if (!TryParseContext(contextText, ps[0].ParameterType, out var ctx)) continue;

                var args = new object?[ps.Length];
                args[0] = ctx;
                for (int i = 1; i < ps.Length; i++)
                {
                    args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                              : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                }
                try
                {
                    var task = m.Invoke(dsInstance, args) as Task;
                    return task ?? Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LoadAsyncReflector] {dsType.Name}.LoadAsync 失败:{ex.Message}");
                    return Task.CompletedTask;
                }
            }
            Console.WriteLine(
                $"[LoadAsyncReflector] {dsType.Name} 没有匹配 '{contextText}' 的 LoadAsync 重载,DefaultContext 跳过。");
            return Task.CompletedTask;
        }

        public static void TryInvoke(object dsInstance, string contextText, TimeSpan timeout)
        {
            var dsType = dsInstance.GetType();
            var candidates = dsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "LoadAsync"
                            && m.GetParameters().Length >= 1
                            && m.GetParameters()[0].ParameterType != typeof(CancellationToken));

            foreach (var m in candidates)
            {
                var ps = m.GetParameters();
                if (!TryParseContext(contextText, ps[0].ParameterType, out var ctx)) continue;

                var args = new object?[ps.Length];
                args[0] = ctx;
                for (int i = 1; i < ps.Length; i++)
                {
                    args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                              : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                }
                try
                {
                    var task = m.Invoke(dsInstance, args) as Task;
                    if (task != null)
                    {
                        // sync-over-async:Task.Run 推到 ThreadPool 等结果,避免 UI 线程 sync-over-async 死锁
                        // (跟 BlueprintLauncher.LoadAsync 等待策略对齐)。超时不抛,fall through 让流走异步路径。
                        Task.Run(() => task).Wait(timeout);
                    }
                }
                catch (AggregateException ae)
                {
                    Console.WriteLine(
                        $"[LoadAsyncReflector] {dsType.Name}.LoadAsync 异常:{ae.GetBaseException().Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LoadAsyncReflector] {dsType.Name}.LoadAsync 失败:{ex.Message}");
                }
                return;
            }
            Console.WriteLine(
                $"[LoadAsyncReflector] {dsType.Name} 没有匹配 '{contextText}' 的 LoadAsync 重载,DefaultContext 跳过。");
        }

        /// <summary>
        /// 约定式 string → <paramref name="targetType"/> 转换:
        /// <list type="bullet">
        ///   <item><c>targetType == string</c>:原样返回</item>
        ///   <item>有静态 <c>Parse(string)</c> 方法:invoke</item>
        ///   <item>有静态 <c>FromString(string)</c> 方法:invoke</item>
        ///   <item>否则:返回 false</item>
        /// </list>
        /// </summary>
        public static bool TryParseContext(string text, Type targetType, out object? value)
        {
            if (targetType == typeof(string)) { value = text; return true; }

            const BindingFlags F = BindingFlags.Public | BindingFlags.Static;
            var parse = targetType.GetMethod("Parse", F, null, new[] { typeof(string) }, null);
            if (parse != null)
            {
                try { value = parse.Invoke(null, new object[] { text }); return value != null; }
                catch { }
            }
            var fromString = targetType.GetMethod("FromString", F, null, new[] { typeof(string) }, null);
            if (fromString != null)
            {
                try { value = fromString.Invoke(null, new object[] { text }); return value != null; }
                catch { }
            }
            value = null;
            return false;
        }
    }
}
