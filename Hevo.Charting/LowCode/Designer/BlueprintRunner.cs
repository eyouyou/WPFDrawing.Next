using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode.Designer.Handlers;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.LowCode.Designer
{
    /// <summary>
    /// <see cref="BlueprintRunner.BuildSchemaDeferred"/> 的产物 —— schema 装配 + 订阅 / Attach 就位,
    /// 但<b>还没 fire LoadAsync</b>。调用方在合适时机(典型:UI 渲染完成后)调
    /// <see cref="BlueprintRunner.StartLoading(DeferredSchema)"/> 把数据请求批量发出去。
    /// <para>
    /// dashboard 多 cell:对 N 个 cell 各 BuildSchemaDeferred,所有 cell 都进 visual tree 后
    /// <c>Task.WhenAll</c> 等所有 StartLoading,数据请求跨蓝图并行,首屏延迟从 sum 变 max。
    /// </para>
    /// <para>
    /// 业务侧拿 <see cref="FirstFrameReady"/> 这条 Task 等"首屏数据真正落入 leaf"信号 —— framework 内部
    /// 在 ctor 就订阅了 leaf DS Stream 首次 publish,Task 在首发时完成。<see cref="BlueprintRunner.Run"/>
    /// / <see cref="GraphViewer.DashboardLauncher"/> 拿这个 Task 翻 ChartCell.IsLoading=false。
    /// </para>
    /// </summary>
    public sealed class DeferredSchema
    {
        /// <summary>已构造好的 schema(类型 = <see cref="DynamicChartSchema{TItem}"/>),可直接 set 到 ChartCell.Template。</summary>
        public object Schema { get; }
        public ChartBlueprint Blueprint { get; }
        internal IReadOnlyDictionary<string, object> ResolvedInstances { get; }
        internal IReadOnlyDictionary<string, object>? SuppliedInstances { get; }
        public BlueprintHandlerRegistry? Handlers { get; }
        /// <summary>render-leaf DS 实例 —— framework 内部用(订阅首发 publish)。业务侧不该直接读;走 <see cref="FirstFrameReady"/>。</summary>
        internal object LeafInstance { get; }
        public bool Started { get; private set; }

        private readonly TaskCompletionSource<object?> _firstFrameTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// 首屏数据到达信号 —— leaf DS Stream 第一次 publish 时完成。
        /// <para>典型用法:<c>deferred.FirstFrameReady.ContinueWith(_ =&gt; cell.IsLoading = false)</c>。</para>
        /// </summary>
        public Task FirstFrameReady => _firstFrameTcs.Task;

        internal DeferredSchema(
            object schema,
            ChartBlueprint blueprint,
            IReadOnlyDictionary<string, object> resolved,
            IReadOnlyDictionary<string, object>? supplied,
            BlueprintHandlerRegistry? handlers,
            object leafInstance)
        {
            Schema = schema;
            Blueprint = blueprint;
            ResolvedInstances = resolved;
            SuppliedInstances = supplied;
            Handlers = handlers;
            LeafInstance = leafInstance;

            // 立即订阅 leaf DS Stream —— ctor 完成时订阅就位,即便 BuildSchemaDeferred 之后 leaf publish 也不丢首帧。
            // (leaf 已经存在,Stream 属性是 BufferedDataSource 暴露的 IWorkflow<DataSnapshot<TItem>>。)
            DeferredSchemaInternals.AttachFirstPublishHandler(leafInstance, () => _firstFrameTcs.TrySetResult(null));
        }

        internal void MarkStarted() => Started = true;
    }

    // first-publish 订阅工具 —— 给 DeferredSchema ctor 跟其它 framework 装配点共用。
    internal static class DeferredSchemaInternals
    {
        // 反射调用 dsInstance.Stream.Subscribe(Action<TSnapshot>) 注册 first-publish 回调。
        // 首次 publish 后 subscription 自动 dispose。
        internal static void AttachFirstPublishHandler(object dsInstance, Action onFirst)
        {
            var streamProp = dsInstance.GetType().GetProperty("Stream",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (streamProp == null) return;
            var stream = streamProp.GetValue(dsInstance);
            if (stream == null) return;

            const string IWorkflowFullName = "Hevo.Charting.IWorkflow`1";
            var iface = Array.Find(stream.GetType().GetInterfaces(),
                i => i.IsGenericType && i.GetGenericTypeDefinition().FullName == IWorkflowFullName);
            if (iface == null) return;

            var snapshotType = iface.GetGenericArguments()[0];
            var actionType = typeof(Action<>).MakeGenericType(snapshotType);
            var subscribeMethod = iface.GetMethod("Subscribe", new[] { actionType, typeof(Action<Exception>) });
            if (subscribeMethod == null) return;

            var subBox = new IDisposable?[] { null };
            int fired = 0;
            var snapParam = System.Linq.Expressions.Expression.Parameter(snapshotType, "snap");
            var onFirstAction = new Action(() =>
            {
                if (System.Threading.Interlocked.Exchange(ref fired, 1) != 0) return;
                try { subBox[0]?.Dispose(); } catch { }
                onFirst();
            });
            var bodyExpr = System.Linq.Expressions.Expression.Call(
                System.Linq.Expressions.Expression.Constant(onFirstAction),
                typeof(Action).GetMethod(nameof(Action.Invoke))!);
            var lambda = System.Linq.Expressions.Expression.Lambda(actionType, bodyExpr, snapParam).Compile();

            subBox[0] = subscribeMethod.Invoke(stream, new object?[] { lambda, null }) as IDisposable;
        }
    }

    /// <summary>
    /// 蓝图运行时入口(Phase 4 节点化形态) —— 单一入口接收
    /// <c>(chart, blueprint, instances dict, handlers)</c>,framework 内部:
    /// <list type="number">
    ///   <item>装配缺失的 DS 实例(<see cref="ChartBlueprint.DataSources"/> 里 <see cref="ComponentRegistry.Resolve"/> + Activator,
    ///         instances 字典已有则跳过 —— 业务侧 ctor 带参的 DS 自己 new 后塞字典)</item>
    ///   <item>挑 render-leaf:无 incoming Cascade 的 DS;多 leaf 报错(本期 Parallel 单 leaf 限制)</item>
    ///   <item>反射 leaf TItem,<c>MakeGenericMethod</c> 一次构造 <see cref="DynamicChartSchema{TItem}"/></item>
    ///   <item>装配 Cascade / TriggerBinding,IDisposable 挂 schema 生命周期</item>
    /// </list>
    /// 业务方典型用法:
    /// <code>
    /// var schema = BlueprintRunner.Run(host.Cell, blueprint, instances: null, handlers);
    /// // ↑ instances=null:framework 全自动 new + InjectProperties + LoadAsync(DefaultContext)
    /// </code>
    /// 业务侧需要给某个 DS 传运行时 context(如 KLineDetailWindow 的 Security)时:
    /// <code>
    /// var schema = BlueprintRunner.Run(host.Cell, blueprint,
    ///     instances: new Dictionary&lt;string, object&gt; { ["primary"] = preBuiltKLineDs },
    ///     handlers);
    /// </code>
    /// </summary>
    public static class BlueprintRunner
    {
        /// <summary>
        /// 装配并启动 —— 蓝图驱动的 <see cref="DynamicChartSchema{TItem}"/> 挂到 chart。
        /// chart 已有 Template 时强制覆盖(蓝图 reload 场景旧 schema 由 ChartCell 自动 Decompose)。
        /// </summary>
        /// <param name="chart">目标 ChartCell。</param>
        /// <param name="blueprint">蓝图(节点化形态,<see cref="ChartBlueprint.DataSources"/> 必填)。</param>
        /// <param name="instances">业务侧预构造的 DS 实例字典(key = <see cref="DataSourceModel.Id"/>),
        ///     缺项 framework 自动 ComponentRegistry.Resolve + Activator.CreateInstance。可传 null = 全自动。</param>
        /// <param name="handlers">handler 注册表(<see cref="BlueprintHandlerRegistry"/>),null = 蓝图无 handler 引用时可省。</param>
        /// <returns>构造好的 schema(类型 = <see cref="DynamicChartSchema{TItem}"/>,TItem 为 render-leaf 的行类型)。</returns>
        public static object Run(
            ChartCell chart,
            ChartBlueprint blueprint,
            IReadOnlyDictionary<string, object>? instances = null,
            BlueprintHandlerRegistry? handlers = null)
        {
            if (chart is null) throw new ArgumentNullException(nameof(chart));
            if (blueprint is null) throw new ArgumentNullException(nameof(blueprint));

            // 两阶段:Build(订阅就位)→ chart.Loaded 后 Dispatcher.Background 派发 StartLoading(并行 fire 所有 LoadAsync)。
            // 首屏渲染不被数据请求阻塞 —— spinner overlay 由 HookCellLifecycle 显式 `chart.IsLoading = true` 开启,
            // FirstFrameReady Task 在 leaf DS 首发 publish 时完成,framework 派回 UI 线程翻 IsLoading=false。
            var deferred = BuildSchemaDeferred(blueprint, instances, handlers);
            chart.Template = (ChartSchema)deferred.Schema;
            WireReactiveEdgesForRun(deferred, chart);
            HookCellLifecycle(chart, deferred);
            return deferred.Schema;
        }

        /// <summary>
        /// Phase-1:构造 schema、装配 cascade/trigger、把 <see cref="DataSourceModel.UpstreamRefs"/> 反射 Attach 进 Composite —— 但<b>不 fire LoadAsync</b>。
        /// 返回 <see cref="DeferredSchema"/>,调用方在合适时机(典型:UI 渲染完成后)调
        /// <see cref="StartLoading(DeferredSchema)"/> 把数据请求批量发出去。
        /// <para>
        /// 用法:dashboard 多 cell 串行构造 schema,再 <c>Task.WhenAll</c> 等所有 cell 的 StartLoading;
        /// 单蓝图 + ChartCell 场景由 <see cref="Run"/> 自动 hook chart.Loaded 触发。
        /// </para>
        /// </summary>
        public static DeferredSchema BuildSchemaDeferred(
            ChartBlueprint blueprint,
            IReadOnlyDictionary<string, object>? instances = null,
            BlueprintHandlerRegistry? handlers = null)
        {
            if (blueprint is null) throw new ArgumentNullException(nameof(blueprint));

            var resolvedInstances = ResolveInstances(blueprint, instances);
            var (_, leafInstance) = PickRenderLeaf(blueprint, resolvedInstances);

            InjectHandlersAware(resolvedInstances, handlers);

            var schema = BuildSchemaForLeaf(blueprint, leafInstance, handlers);

            // node-wrap:把每个 Composite 的 UpstreamRefs 引用的上游 DS Stream 反射 Attach 进去。
            // Composite 本身只暴露 `Attach(IWorkflow<DataSnapshot<TItem>>)`,不知道蓝图存在;framework 这一层负责蓝图协议。
            WireCompositeUpstreams(blueprint, resolvedInstances);

            return new DeferredSchema(
                schema,
                blueprint,
                resolvedInstances,
                instances,
                handlers,
                leafInstance);
        }

        /// <summary>
        /// Phase-2:fire 所有顶层 <see cref="DataSourceModel.DefaultContext"/> 的 LoadAsync(并行),
        /// 用 <see cref="Task.WhenAll(IEnumerable{Task})"/> 等齐。返回的 Task 完成 = 首屏数据请求全部回包发出。
        /// <para>
        /// 注意:cascade 链条(scanner stocklist→primary)是订阅式 —— 这里 fire 的是<b>请求</b>,
        /// leaf DS 真正收到数据要等 cascade fire + leaf 自己 fetch。<see cref="DeferredSchema.FirstFrameReady"/> 才是首屏信号。
        /// </para>
        /// </summary>
        public static Task StartLoading(DeferredSchema deferred)
        {
            if (deferred == null) throw new ArgumentNullException(nameof(deferred));
            if (deferred.Started) return Task.CompletedTask;
            deferred.MarkStarted();

            var tasks = new List<Task>();

            // 所有顶层 DataSourceModel.DefaultContext —— 包括 UpstreamRefs 引用的上游一等节点(被 leaf composite Attach 过)。
            foreach (var dsm in deferred.Blueprint.DataSources)
            {
                if (string.IsNullOrEmpty(dsm.DefaultContext)) continue;
                if (!deferred.ResolvedInstances.TryGetValue(dsm.Id, out var inst)) continue;
                if (deferred.SuppliedInstances != null
                    && deferred.SuppliedInstances.ContainsKey(dsm.Id)
                    && HasUserLoadedContext(inst)) continue;

                tasks.Add(LoadAsyncReflector.TryInvokeAsync(inst, dsm.DefaultContext!));
            }

            return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
        }

        // Run 路径:挂 cell lifecycle:
        //   - FirstFrameReady → UI 线程翻 cell.IsLoading=false
        //   - cell.Loaded → Background 派发 StartLoading(layout + 首帧绘完后才 fire 数据请求)
        private static void HookCellLifecycle(ChartCell chart, DeferredSchema deferred)
        {
            chart.IsLoading = true;

            // 任意线程 → UI 线程翻 IsLoading
            deferred.FirstFrameReady.ContinueWith(_ =>
            {
                chart.Dispatcher.BeginInvoke(
                    new Action(() => chart.IsLoading = false),
                    System.Windows.Threading.DispatcherPriority.DataBind);
            }, TaskContinuationOptions.ExecuteSynchronously);

            if (chart.IsLoaded)
            {
                Schedule(chart, deferred);
            }
            else
            {
                RoutedEventHandler? handler = null;
                handler = (_, _) =>
                {
                    chart.Loaded -= handler;
                    Schedule(chart, deferred);
                };
                chart.Loaded += handler;
            }

            static void Schedule(ChartCell c, DeferredSchema d)
            {
                // Background 优先级:layout + 首次 paint 跑完后才轮到 StartLoading,确保"先渲染外壳、再请数据"。
                c.Dispatcher.BeginInvoke(
                    new Action(() => _ = StartLoading(d)),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        // Run 路径专用:WireReactiveEdges 拿到 chart 参数。
        private static void WireReactiveEdgesForRun(DeferredSchema d, ChartCell chart)
        {
            WireReactiveEdges(d.Schema, d.Blueprint, d.ResolvedInstances, d.Handlers, chart);
        }

        // dashboard / 测试路径:cell-less,WireReactiveEdges 传 chart: null。
        internal static void WireReactiveEdgesForCellless(DeferredSchema d)
        {
            WireReactiveEdges(d.Schema, d.Blueprint, d.ResolvedInstances, d.Handlers, chart: null);
        }

        // node-wrap 装配:扫所有 UpstreamRefs 非空的 DS,反射调 composite.Attach(upstream.Stream)。
        // Composite 本身的合并器接口只接受 IWorkflow<DataSnapshot<TItem>>,framework 通过反射闭合泛型 + 调 Attach。
        private static void WireCompositeUpstreams(
            ChartBlueprint blueprint,
            IReadOnlyDictionary<string, object> resolved)
        {
            foreach (var dsm in blueprint.DataSources)
            {
                if (dsm.UpstreamRefs == null || dsm.UpstreamRefs.Count == 0) continue;
                if (!resolved.TryGetValue(dsm.Id, out var compositeInst))
                {
                    Console.WriteLine($"[BlueprintRunner] UpstreamRefs 引用的 Composite '{dsm.Id}' 实例未找到,跳过整批 Attach。");
                    continue;
                }

                // 沿 compositeInst 的类型继承链找到闭合的 CompositeDataSource<,>,反射拿 Attach 方法。
                var attachMethod = ResolveAttachMethod(compositeInst.GetType());
                if (attachMethod == null)
                {
                    Console.WriteLine($"[BlueprintRunner] '{dsm.Id}' (TypeName={dsm.TypeName}) 不是 CompositeDataSource<,> 派生,UpstreamRefs 配置无效,跳过。");
                    continue;
                }

                foreach (var upId in dsm.UpstreamRefs)
                {
                    if (string.IsNullOrEmpty(upId)) continue;
                    if (!resolved.TryGetValue(upId, out var upInst))
                    {
                        Console.WriteLine($"[BlueprintRunner] UpstreamRefs '{upId}' (Composite '{dsm.Id}' 的上游) 实例未找到,跳过该 attachment。");
                        continue;
                    }

                    var streamProp = upInst.GetType().GetProperty("Stream", BindingFlags.Public | BindingFlags.Instance);
                    var stream = streamProp?.GetValue(upInst);
                    if (stream == null)
                    {
                        Console.WriteLine($"[BlueprintRunner] 上游 '{upId}' 没有 Stream 属性 / Stream=null,跳过。");
                        continue;
                    }

                    try
                    {
                        attachMethod.Invoke(compositeInst, new[] { stream });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            $"[BlueprintRunner] composite '{dsm.Id}'.Attach 失败(upstream '{upId}'):{(ex.InnerException ?? ex).Message}。" +
                            $"通常是 TItem 不一致 —— 上游产 DataSnapshot<{streamProp?.PropertyType.GetGenericArguments().FirstOrDefault()?.GetGenericArguments().FirstOrDefault()?.Name}> " +
                            $"vs composite 期望的 TItem。");
                    }
                }
            }
        }

        // 沿 type 继承链找到闭合的 CompositeDataSource<TSource, TItem>,返回这个闭合泛型上的 Attach 方法。
        // 闭合的 Attach 签名是 (IWorkflow<DataSnapshot<TItem>>) → IDisposable,反射调用时实参类型必须匹配。
        private static MethodInfo? ResolveAttachMethod(Type compositeType)
        {
            for (var t = compositeType; t != null && t != typeof(object); t = t.BaseType)
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Hevo.Charting.WorkFlow.CompositeDataSource<,>))
                {
                    return t.GetMethod("Attach", BindingFlags.Public | BindingFlags.Instance);
                }
            }
            return null;
        }

        private static void InjectHandlersAware(
            IReadOnlyDictionary<string, object> instances, BlueprintHandlerRegistry? handlers)
        {
            if (handlers == null) return;
            foreach (var inst in instances.Values)
                if (inst is Hevo.Charting.WorkFlow.IHandlerAware aware) aware.Handlers = handlers;
        }

        // 反射读 ReactiveDataSource<TSource, TContext, TItem>.Context —— 业务侧预构造 + 已 LoadAsync 过的实例
        // 在 StartLoading 阶段跳过 framework 的自动 fire,避免覆盖用户喂的 context。
        // 非 ReactiveDataSource(无 Context 属性)默认视为"未加载",让 DefaultContext 接管。
        private static bool HasUserLoadedContext(object instance)
        {
            var prop = instance.GetType().GetProperty("Context",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (prop == null) return false;
            return prop.GetValue(instance) != null;
        }

        /// <summary>
        /// 仅构造 schema,不绑 ChartCell ——给 dashboard 路径用(LinkedChartDashboard 自己管 cell + Template 赋值)。
        /// <para>
        /// <b>backward-compat sugar</b>:= <see cref="BuildSchemaDeferred"/> + 同步 <see cref="StartLoading(DeferredSchema)"/> + wire edges。
        /// 老调用方零改动;新调用方(典型 dashboard)用 <see cref="BuildSchemaDeferred"/> 分两阶段。
        /// </para>
        /// </summary>
        public static object BuildSchema(
            ChartBlueprint blueprint,
            IReadOnlyDictionary<string, object>? instances = null,
            BlueprintHandlerRegistry? handlers = null)
        {
            var deferred = BuildSchemaDeferred(blueprint, instances, handlers);
            WireReactiveEdges(deferred.Schema, deferred.Blueprint, deferred.ResolvedInstances, deferred.Handlers, chart: null);
            // 同步等 LoadAsync 回完 —— 跟旧 BuildSchema 的同步阻塞语义对齐。
            // 调用方不该在 UI 线程上等,但旧路径就是这样,保持兼容。新代码用 BuildSchemaDeferred 自己 await。
            try { _ = Task.Run(() => StartLoading(deferred)).Wait(TimeSpan.FromSeconds(5)); }
            catch { /* 单 DS 失败不阻断装配,跟旧 LoadAsyncReflector.TryInvoke 的 silent skip 一致 */ }
            return deferred.Schema;
        }

        // -----------------------------------------------------------------
        // 1. Auto-instantiate:framework 实例化字典里缺的 DS。
        // -----------------------------------------------------------------

        private static Dictionary<string, object> ResolveInstances(
            ChartBlueprint blueprint, IReadOnlyDictionary<string, object>? supplied)
        {
            if (blueprint.DataSources.Count == 0)
                throw new InvalidOperationException(
                    "ChartBlueprint.DataSources 为空 —— 节点化形态下必须至少声明一个 DataSource 节点。");

            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (supplied != null)
            {
                foreach (var kv in supplied) result[kv.Key] = kv.Value;
            }

            foreach (var dsm in blueprint.DataSources)
            {
                if (string.IsNullOrEmpty(dsm.Id))
                    throw new InvalidOperationException(
                        $"DataSourceModel.Id 不能为空(TypeName={dsm.TypeName}) —— 节点化形态下 Id 是 Cascade/TriggerBinding 的必要锚点。");
                if (string.IsNullOrEmpty(dsm.TypeName))
                    throw new InvalidOperationException($"DataSourceModel.TypeName 不能为空(Id={dsm.Id})。");

                if (result.ContainsKey(dsm.Id)) continue;

                // Sentinel Composite:开放泛型 framework 内置类,TItem 从 UpstreamRefs[0] 引用的上游 DS 反推闭合。
                // alias 来自 Composite<TItem> 上的 [BlueprintTypeAlias],framework 代码不硬编码字符串;
                // MatchesAlias 内部容错 ".NET 反射格式 Composite`1"(早期 NodeFactory 漏归一化的磁盘输出)。
                Type dsType = BlueprintTypeAlias.MatchesAlias(dsm.TypeName, typeof(Hevo.Charting.WorkFlow.Composite<>))
                    ? ResolveCompositeGenericTypeWithBlueprint(dsm, blueprint)
                    : ComponentRegistry.Resolve(dsm.TypeName);
                object instance;
                try { instance = Activator.CreateInstance(dsType)!; }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"DataSource '{dsm.Id}' (TypeName={dsm.TypeName}) 无参构造失败:{ex.Message}。" +
                        "ctor 带参 DS 业务侧需自行 new 后塞 instances 字典。", ex);
                }
                if (dsm.Properties is { Count: > 0 } props)
                    SmartActivator.InjectProperties(instance, props);
                result[dsm.Id] = instance;
            }

            return result;
        }

        // -----------------------------------------------------------------
        // 1b. "Composite" sentinel —— 从 dsm.UpstreamRefs[0] 引用的上游 DS 反推 TItem,
        //     闭合 Composite<TItem>。蓝图全配置式合并 DS,业务零类定义。
        // -----------------------------------------------------------------

        private static Type ResolveCompositeGenericTypeWithBlueprint(DataSourceModel dsm, ChartBlueprint blueprint)
        {
            if (dsm.UpstreamRefs == null || dsm.UpstreamRefs.Count == 0)
                throw new InvalidOperationException(
                    $"DataSource '{dsm.Id}' TypeName='Composite' 必须声明 UpstreamRefs 至少 1 条,用于反推 TItem。");

            string? firstId = dsm.UpstreamRefs.FirstOrDefault(s => !string.IsNullOrEmpty(s));
            if (string.IsNullOrEmpty(firstId))
                throw new InvalidOperationException(
                    $"DataSource '{dsm.Id}' TypeName='Composite' 的 UpstreamRefs 全为空字符串,无法反推 TItem。");

            var referenced = blueprint.DataSources.FirstOrDefault(d =>
                string.Equals(d.Id, firstId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"DataSource '{dsm.Id}' Composite UpstreamRefs[0]='{firstId}' " +
                    "在蓝图 DataSources 中找不到 —— 该 Id 是否拼错或未在同级声明?");

            Type upstreamType;
            try { upstreamType = ComponentRegistry.Resolve(referenced.TypeName); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"DataSource '{dsm.Id}' Composite 上游 '{firstId}' TypeName='{referenced.TypeName}' 解析失败:{ex.Message}", ex);
            }

            var itemType = BlueprintDataSourceProbe.GetItemType(upstreamType)
                ?? throw new InvalidOperationException(
                    $"DataSource '{dsm.Id}' Composite 上游 {referenced.TypeName} 不是 BufferedDataSource<TSource, TItem> 派生,反推不到 TItem。");

            return typeof(Hevo.Charting.WorkFlow.Composite<>).MakeGenericType(itemType);
        }

        // -----------------------------------------------------------------
        // 2. Render-leaf 隐式选取:无 incoming Cascade 的 DS 即 leaf;多 leaf 报错。
        // -----------------------------------------------------------------

        private static (DataSourceModel model, object instance) PickRenderLeaf(
            ChartBlueprint blueprint, IReadOnlyDictionary<string, object> instances)
        {
            // Leaf 定义:不被任何下游依赖(Cascade.From / DataSourceModel.UpstreamRefs)引用的 DS。
            // 单 DS 蓝图 → 唯一项即 leaf。多 DS:cascade / composite 引用的上游不算 leaf,只有终点节点是 leaf。
            var upstreamIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in blueprint.Cascades)
                if (!string.IsNullOrEmpty(c.FromDataSourceId)) upstreamIds.Add(c.FromDataSourceId);

            // UpstreamRefs 同样让被引用的 DS 退出 leaf 候选池 —— 它们是 composite 的输入(node-wrap)。
            foreach (var dsm in blueprint.DataSources)
            {
                if (dsm.UpstreamRefs == null) continue;
                foreach (var id in dsm.UpstreamRefs)
                    if (!string.IsNullOrEmpty(id)) upstreamIds.Add(id);
            }

            var leaves = blueprint.DataSources
                .Where(d => !upstreamIds.Contains(d.Id))
                .ToList();

            if (leaves.Count == 0)
                throw new InvalidOperationException(
                    "Render-leaf 推导:DataSources 全是 Cascade 上游(每个 DS 都被 Cascade.FromDataSourceId 引用)。" +
                    "至少需要一个 DS 不作为 cascade 上游 —— 即渲染叶。");
            if (leaves.Count > 1)
                throw new InvalidOperationException(
                    $"Render-leaf 推导:多 leaf({string.Join(", ", leaves.Select(l => l.Id))}) —— Parallel 多渲染叶蓝图属 Phase 4+ 范畴,本期暂不支持。" +
                    "用 Cascade 把多 DS 显式串成单一 leaf,或用 Coordinator Feature 扇入合并。");

            var leafModel = leaves[0];
            if (!instances.TryGetValue(leafModel.Id, out var leafInstance))
                throw new InvalidOperationException(
                    $"Render-leaf '{leafModel.Id}' 实例缺失:ResolveInstances 已自动 new,这里不应失败 —— framework bug 或 ComponentRegistry 问题。");
            return (leafModel, leafInstance);
        }

        // -----------------------------------------------------------------
        // 3. 反射 TItem 构造 DynamicChartSchema<TItem>。
        // -----------------------------------------------------------------

        private static readonly ConcurrentBuilderCache _builderCache = new();

        private static object BuildSchemaForLeaf(
            ChartBlueprint blueprint, object leafInstance, BlueprintHandlerRegistry? handlers)
        {
            // wrapper 式探针:沿 BufferedDataSource<,> 继承链拿 TItem + Stream,跟接口实现等价但零基类污染。
            // 派生 DS 类零侵入(不实现任何蓝图相关接口),所有反射 + Expression 编译开销由 probe 缓存吃掉。
            var (itemType, stream) = BlueprintDataSourceProbe.Inspect(leafInstance);
            var ctor = _builderCache.GetCtor(itemType);
            return ctor.Invoke(new[] { (object)blueprint, leafInstance, stream, (object?)handlers })!;
        }

        // 缓存 DynamicChartSchema<TItem>(blueprint, dsInstance, stream, handlers) 4 参 ctor。
        // 同 TItem 多次 RunBlueprint 不重复 MakeGenericType。
        private sealed class ConcurrentBuilderCache
        {
            private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, ConstructorInfo> _cache = new();
            public ConstructorInfo GetCtor(Type itemType) => _cache.GetOrAdd(itemType, ResolveCtor);

            private static ConstructorInfo ResolveCtor(Type itemType)
            {
                var schemaType = typeof(DynamicChartSchema<>).MakeGenericType(itemType);
                var streamType = typeof(IWorkflow<>).MakeGenericType(typeof(DataSnapshot<>).MakeGenericType(itemType));
                var ctor = schemaType.GetConstructor(new[]
                {
                    typeof(ChartBlueprint), typeof(object), streamType, typeof(BlueprintHandlerRegistry),
                });
                return ctor ?? throw new InvalidOperationException(
                    $"DynamicChartSchema<{itemType.Name}> 没有 (ChartBlueprint, object, IWorkflow<DataSnapshot<T>>, BlueprintHandlerRegistry?) ctor。");
            }
        }

        // -----------------------------------------------------------------
        // 4. Cascade / TriggerBinding 装配。
        // -----------------------------------------------------------------

        private static void WireReactiveEdges(
            object schema,
            ChartBlueprint blueprint,
            IReadOnlyDictionary<string, object> instances,
            BlueprintHandlerRegistry? handlers,
            ChartCell? chart)
        {
            if (blueprint.Cascades.Count == 0 && blueprint.TriggerBindings.Count == 0) return;

            if (handlers == null)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[BlueprintRunner] Blueprint 声明了 Cascades / TriggerBindings 但 handlers=null,跳过装配。");
                return;
            }

            // ScopeContext 跟 schema 同生灭 —— Apply 周期内 Scoped/PerNode handler 实例池随蓝图重载释放。
            var scope = new ScopeContext();
            if (chart != null) scope.AddService(typeof(ChartCell), chart);
            RegisterDisposable(schema, scope);

            if (blueprint.Cascades.Count > 0)
            {
                var cascadeSubs = BlueprintCascadeWiring.WireCascades(blueprint, instances, handlers, scope);
                foreach (var sub in cascadeSubs) RegisterDisposable(schema, sub);
            }

            if (blueprint.TriggerBindings.Count > 0)
            {
                var bindingSubs = BlueprintTriggerBindingWiring.WireTriggerBindings(blueprint, instances, handlers, scope);
                foreach (var sub in bindingSubs) RegisterDisposable(schema, sub);
            }
        }

        // schema 是 object 引用(运行时是 DynamicChartSchema<TItem> for some T),
        // ReactiveSchema.RegisterDisposable 是 public 实例方法,反射调用一次性 MethodInfo 缓存即可,
        // 但实际上所有 DynamicChartSchema 都从 ReactiveSchema 派,直接 cast 即可。
        private static void RegisterDisposable(object schema, IDisposable resource)
        {
            if (schema is Hevo.Charting.Core.ReactiveSchema rs) rs.RegisterDisposable(resource);
            else throw new InvalidOperationException($"Schema {schema.GetType().Name} 不是 ReactiveSchema —— RegisterDisposable 失败。");
        }
    }
}
