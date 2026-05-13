using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode.Designer.Handlers;

namespace Hevo.Charting.LowCode.Designer
{
    /// <summary>
    /// Phase 2 Cascade 装配引擎 —— 把 <see cref="ChartBlueprint.Cascades"/> 里声明的 DS-to-DS push 边
    /// 复刻成 <c>upstreamDs.Stream.Subscribe(snap =&gt; downstreamDs.SwitchContextErasedAsync(driver(ds, snap)))</c>。
    /// <para>
    /// 装配期校验:
    /// </para>
    /// <list type="number">
    ///   <item>FromDataSourceId / ToDataSourceId 必须在 instances 字典里命中,缺失 → 抛 InvalidOperationException</item>
    ///   <item>ContextDriver 必须在 handlers registry 注册过,缺失 → 抛</item>
    ///   <item>driver 返回类型必须匹配下游 ReactiveDataSource&lt;_,TContext,...&gt; 的 TContext,不匹配 → 抛(装配期 fail-fast)</item>
    /// </list>
    /// <para>
    /// 调用方<see cref="WireCascades"/> 拿到 <see cref="IDisposable"/> 列表,挂到 BlueprintCanvas / Schema 的 owned-disposables,
    /// Unloaded 时统一释放(切断 Subscribe → 上游 push 不再触发下游 SwitchContext)。
    /// </para>
    /// </summary>
    public static class BlueprintCascadeWiring
    {
        /// <summary>
        /// 按蓝图 <see cref="ChartBlueprint.Cascades"/> 装配 DS-to-DS push 链。
        /// </summary>
        /// <param name="blueprint">蓝图。读 <see cref="ChartBlueprint.Cascades"/>。</param>
        /// <param name="dataSourceInstances">蓝图内 DS 实例字典。Key = <see cref="DataSourceModel.Id"/>,
        ///     Value = 已实例化的 DS 对象(必须继承 <see cref="ReactiveDataSource{TSource,TContext,TRequest,TResponse,TItem}"/>
        ///     或 <see cref="ReactiveDataSource{TSource,TContext,TItem}"/>)。</param>
        /// <param name="handlers">handler/driver 注册表。装配期通过 <see cref="BlueprintHandlerRegistry.Resolve(string, IScopeContext, string?)"/>
        ///     按 driver 名查 lifecycle-aware 委托。</param>
        /// <param name="scope">蓝图实例级 ScopeContext —— PerNode driver 的 nodeId 用 <c>cascade:{From}-&gt;{To}</c> 复合 Id。</param>
        /// <returns>每条 Cascade 的 Subscribe 句柄,调用方负责持有 + Dispose。</returns>
        public static IReadOnlyList<IDisposable> WireCascades(
            ChartBlueprint blueprint,
            IReadOnlyDictionary<string, object> dataSourceInstances,
            BlueprintHandlerRegistry handlers,
            IScopeContext scope)
        {
            if (blueprint == null) throw new ArgumentNullException(nameof(blueprint));
            if (dataSourceInstances == null) throw new ArgumentNullException(nameof(dataSourceInstances));
            if (handlers == null) throw new ArgumentNullException(nameof(handlers));
            if (scope == null) throw new ArgumentNullException(nameof(scope));

            if (blueprint.Cascades.Count == 0) return Array.Empty<IDisposable>();

            var disposables = new List<IDisposable>(blueprint.Cascades.Count);
            foreach (var cascade in blueprint.Cascades)
            {
                if (string.IsNullOrEmpty(cascade.FromDataSourceId) || string.IsNullOrEmpty(cascade.ToDataSourceId))
                    throw new InvalidOperationException(
                        $"CascadeEdge.FromDataSourceId / ToDataSourceId 不能为空。当前:From='{cascade.FromDataSourceId}' To='{cascade.ToDataSourceId}'。");

                if (!dataSourceInstances.TryGetValue(cascade.FromDataSourceId, out var fromDs))
                    throw new InvalidOperationException(
                        $"CascadeEdge: 上游 DataSource '{cascade.FromDataSourceId}' 未在 dataSourceInstances 字典中找到。" +
                        $"已知:[{string.Join(", ", dataSourceInstances.Keys)}]。");

                if (!dataSourceInstances.TryGetValue(cascade.ToDataSourceId, out var toDs))
                    throw new InvalidOperationException(
                        $"CascadeEdge: 下游 DataSource '{cascade.ToDataSourceId}' 未在 dataSourceInstances 字典中找到。");

                if (string.IsNullOrEmpty(cascade.ContextDriver))
                    throw new InvalidOperationException(
                        $"CascadeEdge {cascade.FromDataSourceId}->{cascade.ToDataSourceId}: ContextDriver 不能为空。");

                if (!string.Equals(cascade.Trigger, "Stream", StringComparison.Ordinal))
                    throw new NotSupportedException(
                        $"CascadeEdge.Trigger='{cascade.Trigger}' 暂未支持。Phase 2 主线只支持 'Stream';" +
                        $"Feature 事件 trigger('FeatureId.EventName')计划 Phase 2.x 引入。");

                // PerNode driver 的 nodeId 约定:cascade:{From}->{To}。
                var nodeId = $"cascade:{cascade.FromDataSourceId}->{cascade.ToDataSourceId}";
                var driver = handlers.Resolve(cascade.ContextDriver, scope, nodeId);
                if (driver == null)
                    throw new InvalidOperationException(
                        $"CascadeEdge {cascade.FromDataSourceId}->{cascade.ToDataSourceId}: " +
                        $"ContextDriver '{cascade.ContextDriver}' 未注册或解析失败。" +
                        $"已注册 handlers: [{string.Join(", ", handlers.EnumerateNames())}]。");

                // 装配期类型校验 —— driver 返回类型必须匹配下游 TContext。
                ValidateDriverReturnType(driver, fromDs, toDs, cascade);

                var sub = SubscribeStreamToContext(fromDs, toDs, driver, cascade);
                disposables.Add(sub);
            }
            return disposables;
        }

        // 反射拿下游 ReactiveDataSource<...,TContext,...> 的 TContext,跟 driver.Method.ReturnType 对账。
        private static void ValidateDriverReturnType(Delegate driver, object fromDs, object toDs, CascadeEdge cascade)
        {
            var contextType = ResolveContextType(toDs.GetType());
            if (contextType == null)
                throw new InvalidOperationException(
                    $"CascadeEdge {cascade.FromDataSourceId}->{cascade.ToDataSourceId}: " +
                    $"下游 {toDs.GetType().Name} 不是 ReactiveDataSource&lt;_, TContext, ...&gt;,无法装配 Cascade。");

            var driverReturn = driver.Method.ReturnType;
            if (!contextType.IsAssignableFrom(driverReturn))
                throw new InvalidOperationException(
                    $"CascadeEdge {cascade.FromDataSourceId}->{cascade.ToDataSourceId}:" +
                    $"driver '{cascade.ContextDriver}' 返回类型 {driverReturn.FullName} " +
                    $"不匹配下游 TContext={contextType.FullName}。装配期校验失败。");

            // 第一参类型应为上游 DS(或 object,driver 不需要持有上游引用时);校验更严的话,这里再加一道
            // driver.Method.GetParameters()[0].ParameterType.IsAssignableFrom(fromDs.GetType()) 即可。
            // Phase 2 暂不强制 —— 业务可能写无类型 driver (Func<DataSnapshot<T>, TContext>),保持灵活。
        }

        // ReactiveDataSource<TSource, TContext, TItem>(单 shape):args[1] = TContext。
        // 沿继承链找,直到命中 ReactiveDataSource`3 或 object。
        private static Type? ResolveContextType(Type dsType)
        {
            for (var t = dsType; t != null && t != typeof(object); t = t.BaseType)
            {
                if (!t.IsGenericType) continue;
                var gen = t.GetGenericTypeDefinition().FullName;
                if (gen == null) continue;
                if (gen.StartsWith("Hevo.Charting.ReactiveDataSource`", StringComparison.Ordinal))
                {
                    var args = t.GetGenericArguments();
                    if (args.Length >= 2) return args[1];
                }
            }
            return null;
        }

        // 反射调用 fromDs.Stream.Subscribe(snap => toDs.SwitchContextErasedAsync(driver(fromDs, snap)))。
        // Stream 类型是 IWorkflow<DataSnapshot<TItem>>(TItem 编译期不可见),走反射 Subscribe;
        // SwitchContextErasedAsync 是非泛型 (object, CancellationToken),直接 method.Invoke。
        // driver 签名 (TUpstreamDs, DataSnapshot<TItem>) → TContext,用 DynamicInvoke 容忍多种实参签名形态。
        private static IDisposable SubscribeStreamToContext(object fromDs, object toDs, Delegate driver, CascadeEdge cascade)
        {
            // 1. 反射拿 fromDs.Stream
            var streamProp = fromDs.GetType().GetProperty("Stream", BindingFlags.Public | BindingFlags.Instance);
            if (streamProp == null)
                throw new InvalidOperationException(
                    $"CascadeEdge {cascade.FromDataSourceId}->{cascade.ToDataSourceId}: " +
                    $"上游 {fromDs.GetType().Name} 没有 public Stream 属性,无法装配 Cascade。");
            var stream = streamProp.GetValue(fromDs)
                ?? throw new InvalidOperationException(
                    $"CascadeEdge: 上游 {fromDs.GetType().Name}.Stream 返回 null。");

            // 2. stream 是 IWorkflow<DataSnapshot<TItem>>;走 IWorkflow<T>.Subscribe 反射调用。
            //    IWorkflow<T> 在 Hevo.Charting 命名空间(非 .Core);Stream 属性可能返回更具体的实现类型
            //    (如 WorkflowTrigger<T>),沿其 generic interfaces 找接口本身。
            const string IWorkflowFullName = "Hevo.Charting.IWorkflow`1";
            Type streamIfaceType = streamProp.PropertyType;
            if (!streamIfaceType.IsGenericType || streamIfaceType.GetGenericTypeDefinition().FullName != IWorkflowFullName)
            {
                streamIfaceType = Array.Find(stream.GetType().GetInterfaces(),
                    i => i.IsGenericType && i.GetGenericTypeDefinition().FullName == IWorkflowFullName)
                    ?? throw new InvalidOperationException(
                        $"CascadeEdge: 上游 Stream 不是 IWorkflow<T>,实际类型 {stream.GetType().FullName}。");
            }

            var snapshotType = streamIfaceType.GetGenericArguments()[0];   // DataSnapshot<TItem>

            // 3. 用 Action<TSnapshot> 包 driver + SwitchContextErasedAsync 调用。
            //    Action<object> + boxing 也行,但保留强类型签名让 Subscribe 反射调用更直观。
            var actionType = typeof(Action<>).MakeGenericType(snapshotType);

            // 编译强类型 Action<TSnapshot> forwarder:(snap) => ctxBag.OnSnapshot((object)snap)
            // Delegate.CreateDelegate 的签名兼容性要求 first-arg + remaining params 跟方法签名严格对齐,
            // value-type → object 装箱 cast 不在它视野内 —— 走 Expression.Lambda 编译过 callvirt + box 即可。
            var ctxBag = new CascadeContext(fromDs, toDs, driver, cascade);
            var snapParam = Expression.Parameter(snapshotType, "snap");
            var onSnapMethod = typeof(CascadeContext).GetMethod(
                nameof(CascadeContext.OnSnapshot), BindingFlags.Public | BindingFlags.Instance)!;
            var body = Expression.Call(
                Expression.Constant(ctxBag),
                onSnapMethod,
                Expression.Convert(snapParam, typeof(object)));   // box value-type snapshot 到 object
            var closedAction = Expression.Lambda(actionType, body, snapParam).Compile();

            // 4. stream.Subscribe(Action<T>, Action<Exception>?) → 反射调用,返 IDisposable。
            //    onError 传 null,异常上抛由 Stream 自身路径处理(跟 ReactiveSchema OnError 走同一签名约定)。
            var actionExceptionType = typeof(Action<Exception>);
            var subscribeMethod = streamIfaceType.GetMethod("Subscribe", new[] { actionType, actionExceptionType })
                ?? throw new InvalidOperationException(
                    $"CascadeEdge: IWorkflow<{snapshotType.Name}>.Subscribe(Action<{snapshotType.Name}>, Action<Exception>?) 未找到。");
            var sub = (IDisposable)subscribeMethod.Invoke(stream, new object?[] { closedAction, null })!;
            return sub;
        }

        // 闭包载体 —— 让 OnSnapshotErased 能拿到 (fromDs, toDs, driver, edge) 而无须每帧分配 lambda。
        private sealed class CascadeContext
        {
            public object FromDs { get; }
            public object ToDs { get; }
            public Delegate Driver { get; }
            public CascadeEdge Edge { get; }
            // 反射 SwitchContextErasedAsync MethodInfo —— 一次性绑定省每帧 GetMethod 开销。
            public MethodInfo SwitchMethod { get; }

            public CascadeContext(object fromDs, object toDs, Delegate driver, CascadeEdge edge)
            {
                FromDs = fromDs;
                ToDs = toDs;
                Driver = driver;
                Edge = edge;
                SwitchMethod = toDs.GetType().GetMethod("SwitchContextErasedAsync",
                                   BindingFlags.Public | BindingFlags.Instance,
                                   binder: null,
                                   types: new[] { typeof(object), typeof(CancellationToken) },
                                   modifiers: null)
                    ?? throw new InvalidOperationException(
                        $"CascadeEdge: 下游 {toDs.GetType().Name} 没有 SwitchContextErasedAsync(object, CancellationToken)。" +
                        $"是否继承自 ReactiveDataSource<...>?");
            }

            // OnSnapshot<TSnapshot> 调用入口(走 Action<TSnapshot> 的 boxed-arg 形态)。
            // driver 签名形态:(UpstreamDs, DataSnapshot<TItem>) → TContext;DynamicInvoke 容忍 ds 强类型 / object。
            public void OnSnapshot(object snapshot)
            {
                object? ctx;
                try
                {
                    ctx = Driver.DynamicInvoke(FromDs, snapshot);
                }
                catch (TargetInvocationException tex) when (tex.InnerException != null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[CascadeEdge {Edge.FromDataSourceId}->{Edge.ToDataSourceId}] driver '{Edge.ContextDriver}' 抛异常:{tex.InnerException}");
                    return;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[CascadeEdge {Edge.FromDataSourceId}->{Edge.ToDataSourceId}] driver '{Edge.ContextDriver}' 异常:{ex}");
                    return;
                }
                if (ctx == null) return;   // driver 返 null = 显式跳过这次切换

                // SwitchContextErasedAsync 是 fire-and-forget 风格 —— 返回的 Task 不阻塞 stream subscriber。
                // 异常吃在 ContinueWith 里打 log,不让 Cascade 错误污染 Stream 自身。
                var task = (System.Threading.Tasks.Task)SwitchMethod.Invoke(
                    ToDs, new object?[] { ctx, CancellationToken.None })!;
                task.ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[CascadeEdge {Edge.FromDataSourceId}->{Edge.ToDataSourceId}] SwitchContextErasedAsync 异常:{t.Exception.GetBaseException()}");
                    }
                }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }
        }

    }
}
