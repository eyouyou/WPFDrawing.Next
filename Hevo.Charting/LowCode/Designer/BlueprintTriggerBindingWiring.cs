using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode.Designer.Handlers;

namespace Hevo.Charting.LowCode.Designer
{
    /// <summary>
    /// Phase 3 TriggerBinding 装配引擎 —— 把 <see cref="ChartBlueprint.TriggerBindings"/>
    /// 复刻成 <c>Workflow.Interval(...).FetchExclusive(verb-dispatched-action).Subscribe(...)</c> 调用链。
    /// <para>
    /// 90% 心跳场景(<see cref="TriggerBinding.Verb"/>=<c>Refresh</c>)零 handler / 零 driver,
    /// framework 直调 DS 的 <see cref="IRefreshable.RefreshAsync"/>(派生类如 KLineDataSource 自己 override 决策)。
    /// </para>
    /// <para>
    /// Verb 表:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>Refresh</c> → <see cref="IRefreshable.RefreshAsync"/>(默认)</item>
    ///   <item><c>Suspend</c> → <see cref="IPausable.Suspend"/></item>
    ///   <item><c>Resume</c>  → <see cref="IPausable.Resume"/></item>
    ///   <item><c>SwitchContext</c> → <see cref="ReactiveDataSource{TSource,TContext,TRequest,TResponse,TItem}.SwitchContextErasedAsync"/>(driver 算 ctx)</item>
    ///   <item><c>Request</c> → <see cref="ReactiveDataSource{TSource,TContext,TRequest,TResponse,TItem}.RequestAsync"/>(driver 算 request)</item>
    /// </list>
    /// </summary>
    public static class BlueprintTriggerBindingWiring
    {
        /// <summary>
        /// 按蓝图 <see cref="ChartBlueprint.TriggerBindings"/> 装配 trigger → DS verb 派发。
        /// </summary>
        public static IReadOnlyList<IDisposable> WireTriggerBindings(
            ChartBlueprint blueprint,
            IReadOnlyDictionary<string, object> dataSourceInstances,
            BlueprintHandlerRegistry handlers,
            IScopeContext scope)
        {
            if (blueprint == null) throw new ArgumentNullException(nameof(blueprint));
            if (dataSourceInstances == null) throw new ArgumentNullException(nameof(dataSourceInstances));
            if (handlers == null) throw new ArgumentNullException(nameof(handlers));
            if (scope == null) throw new ArgumentNullException(nameof(scope));

            if (blueprint.TriggerBindings.Count == 0) return Array.Empty<IDisposable>();

            var triggersById = BuildTriggersById(blueprint);
            var disposables = new List<IDisposable>(blueprint.TriggerBindings.Count);
            foreach (var binding in blueprint.TriggerBindings)
            {
                if (string.IsNullOrEmpty(binding.TriggerId))
                    throw new InvalidOperationException("TriggerBinding.TriggerId 不能为空。");
                if (string.IsNullOrEmpty(binding.TargetDataSourceId))
                    throw new InvalidOperationException(
                        $"TriggerBinding 引用 trigger '{binding.TriggerId}' 但 TargetDataSourceId 为空。");

                if (!triggersById.TryGetValue(binding.TriggerId, out var trigger))
                    throw new InvalidOperationException(
                        $"TriggerBinding: trigger '{binding.TriggerId}' 在 ChartBlueprint.Triggers 中找不到。" +
                        $"已知:[{string.Join(", ", triggersById.Keys)}]。");

                if (!dataSourceInstances.TryGetValue(binding.TargetDataSourceId, out var ds))
                    throw new InvalidOperationException(
                        $"TriggerBinding {binding.TriggerId}->{binding.TargetDataSourceId}: " +
                        $"DataSource 未在 dataSourceInstances 字典中找到。" +
                        $"已知:[{string.Join(", ", dataSourceInstances.Keys)}]。");

                if (!string.Equals(trigger.Kind, "Interval", StringComparison.Ordinal))
                    throw new NotSupportedException(
                        $"TriggerModel '{trigger.Id}'.Kind='{trigger.Kind}' 暂未支持。Phase 3 主线只支持 'Interval'。");
                if (trigger.IntervalSeconds <= 0)
                    throw new InvalidOperationException(
                        $"TriggerModel '{trigger.Id}'.IntervalSeconds={trigger.IntervalSeconds} 非法,必须 > 0。");

                var action = BuildVerbAction(binding, ds, handlers, scope);
                var sub = Subscribe(trigger, action);
                disposables.Add(sub);
            }
            return disposables;
        }

        // 按 Id 索引。Id 为空时用兜底复合 Id "trigger:{IntervalSeconds}s:{handler-or-noop}"。
        private static Dictionary<string, TriggerModel> BuildTriggersById(ChartBlueprint blueprint)
        {
            var dict = new Dictionary<string, TriggerModel>(StringComparer.Ordinal);
            int autoIdx = 0;
            foreach (var t in blueprint.Triggers)
            {
                var id = string.IsNullOrEmpty(t.Id) ? $"__autotrigger_{autoIdx++}" : t.Id;
                if (dict.ContainsKey(id))
                    throw new InvalidOperationException($"TriggerModel.Id '{id}' 重复。");
                dict[id] = t;
            }
            return dict;
        }

        // 把 binding 的 verb + driver 翻译成 (VersionToken, CancellationToken) → Task<bool> 闭包。
        // FetchExclusive 期望此签名,bool 仅用于内部 CAS 区分,这里始终返 true(成功 / 失败异常已包好)。
        private static Func<VersionToken, CancellationToken, Task<bool>> BuildVerbAction(
            TriggerBinding binding, object ds, BlueprintHandlerRegistry handlers, IScopeContext scope)
        {
            switch (binding.Verb)
            {
                case "Refresh":
                {
                    if (ds is not IRefreshable refreshable)
                        throw new InvalidOperationException(
                            $"TriggerBinding verb=Refresh 要求 DataSource '{binding.TargetDataSourceId}' " +
                            $"({ds.GetType().Name})实现 IRefreshable。");
                    return async (_, ct) =>
                    {
                        await refreshable.RefreshAsync(ct).ConfigureAwait(false);
                        return true;
                    };
                }

                case "Suspend":
                {
                    if (ds is not IPausable pausable)
                        throw new InvalidOperationException(
                            $"TriggerBinding verb=Suspend 要求 DataSource '{binding.TargetDataSourceId}' 实现 IPausable。");
                    return (_, _) =>
                    {
                        pausable.Suspend();
                        return Task.FromResult(true);
                    };
                }

                case "Resume":
                {
                    if (ds is not IPausable pausable)
                        throw new InvalidOperationException(
                            $"TriggerBinding verb=Resume 要求 DataSource '{binding.TargetDataSourceId}' 实现 IPausable。");
                    return (_, _) =>
                    {
                        pausable.Resume();
                        return Task.FromResult(true);
                    };
                }

                case "SwitchContext":
                    return BuildSwitchContextAction(binding, ds, handlers, scope);

                case "Request":
                    return BuildRequestAction(binding, ds, handlers, scope);

                default:
                    throw new NotSupportedException(
                        $"TriggerBinding.Verb='{binding.Verb}' 未知。支持:Refresh / Suspend / Resume / SwitchContext / Request。");
            }
        }

        private static Func<VersionToken, CancellationToken, Task<bool>> BuildSwitchContextAction(
            TriggerBinding binding, object ds, BlueprintHandlerRegistry handlers, IScopeContext scope)
        {
            if (string.IsNullOrEmpty(binding.Driver))
                throw new InvalidOperationException(
                    $"TriggerBinding verb=SwitchContext 要求 Driver 字段非空(driver 算 TContext)。");

            var nodeId = $"trigger:{binding.TriggerId}->{binding.TargetDataSourceId}";
            var driver = handlers.Resolve(binding.Driver, scope, nodeId)
                ?? throw new InvalidOperationException(
                    $"TriggerBinding {binding.TriggerId}->{binding.TargetDataSourceId}: " +
                    $"driver '{binding.Driver}' 未注册或解析失败。");

            // 找下游 SwitchContextErasedAsync(object, CancellationToken)
            var switchMethod = ds.GetType().GetMethod("SwitchContextErasedAsync",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(object), typeof(CancellationToken) },
                modifiers: null)
                ?? throw new InvalidOperationException(
                    $"TriggerBinding verb=SwitchContext: DataSource '{binding.TargetDataSourceId}' " +
                    $"({ds.GetType().Name}) 没有 SwitchContextErasedAsync(object, CancellationToken)。");

            return async (tick, ct) =>
            {
                var ctx = driver.DynamicInvoke(ds, tick);
                if (ctx == null) return true;
                var task = (Task)switchMethod.Invoke(ds, new object?[] { ctx, ct })!;
                await task.ConfigureAwait(false);
                return true;
            };
        }

        private static Func<VersionToken, CancellationToken, Task<bool>> BuildRequestAction(
            TriggerBinding binding, object ds, BlueprintHandlerRegistry handlers, IScopeContext scope)
        {
            if (string.IsNullOrEmpty(binding.Driver))
                throw new InvalidOperationException(
                    $"TriggerBinding verb=Request 要求 Driver 字段非空(driver 算 TRequest)。");

            var nodeId = $"trigger:{binding.TriggerId}->{binding.TargetDataSourceId}";
            var driver = handlers.Resolve(binding.Driver, scope, nodeId)
                ?? throw new InvalidOperationException(
                    $"TriggerBinding {binding.TriggerId}->{binding.TargetDataSourceId}: " +
                    $"driver '{binding.Driver}' 未注册或解析失败。");

            // RequestAsync<TRequest>(TRequest, CancellationToken):反射找,签名 (TRequest, CancellationToken) → Task<int>。
            // TRequest 跟 driver 返回类型对账。
            var requestMethod = FindRequestAsyncMethod(ds.GetType(), driver.Method.ReturnType)
                ?? throw new InvalidOperationException(
                    $"TriggerBinding verb=Request: DataSource '{binding.TargetDataSourceId}' " +
                    $"({ds.GetType().Name}) 没有 RequestAsync({driver.Method.ReturnType.Name}, CancellationToken),或者 driver 返回类型跟 TRequest 不匹配。");

            return async (tick, ct) =>
            {
                var req = driver.DynamicInvoke(ds, tick);
                if (req == null) return true;
                var task = (Task)requestMethod.Invoke(ds, new object?[] { req, ct })!;
                await task.ConfigureAwait(false);
                return true;
            };
        }

        // 沿继承链找 RequestAsync(TRequest, CancellationToken) — TRequest 必须 IsAssignableFrom driver 返回类型。
        private static MethodInfo? FindRequestAsyncMethod(Type dsType, Type driverReturnType)
        {
            for (var t = dsType; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != "RequestAsync") continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 2) continue;
                    if (ps[1].ParameterType != typeof(CancellationToken)) continue;
                    if (!ps[0].ParameterType.IsAssignableFrom(driverReturnType)) continue;
                    return m;
                }
            }
            return null;
        }

        // 复刻原 DynamicChartSchema.WireTrigger 的 Subscribe(Workflow.Interval + FetchExclusive) 调用链。
        // 行为对齐:Exclusive=true CAS 防重入(默认),false 每 tick 并发堆叠。
        private static IDisposable Subscribe(TriggerModel trigger, Func<VersionToken, CancellationToken, Task<bool>> action)
        {
            var interval = Workflow.Interval(TimeSpan.FromSeconds(trigger.IntervalSeconds));
            if (trigger.Exclusive)
            {
                return interval
                    .FetchExclusive(action)
                    .Subscribe(_ => { },
                               ex => Console.WriteLine($"[Hevo TriggerBinding 异常 '{trigger.Id}'] {ex}"));
            }
            return interval
                .Subscribe(async tick =>
                {
                    try { await action(tick, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Hevo TriggerBinding 异常 '{trigger.Id}' (Exclusive=false)] {ex}");
                    }
                },
                ex => Console.WriteLine($"[Hevo TriggerBinding 异常 '{trigger.Id}'] {ex}"));
        }
    }
}
