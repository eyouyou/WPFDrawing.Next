using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Linked;
using Hevo.Charting.LowCode;
using System.Reflection;
using System.Windows;
using System.Windows.Media;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// <see cref="DashboardLauncher.LaunchEx"/> / <see cref="DashboardLauncher.DryRun"/> 的结构化结果。
    /// Diagnostics 来自所有 cell 蓝图的 DryRun 结果聚合(每条诊断里 FeatureTypeName 前缀有 "Cell:{id} / ")。
    /// </summary>
    public sealed class DashboardLaunchResult
    {
        public LinkedChartDashboard? Dashboard { get; init; }
        public string? Error { get; init; }
        public IReadOnlyList<BlueprintDiagnostic> Diagnostics { get; init; } = Array.Empty<BlueprintDiagnostic>();
        public bool Launched => Error == null && Dashboard != null;
    }

    /// <summary>
    /// dashboard 级运行时上下文(per-cell context / configure / handlers)。每个字段都按 cell.Id keyed。
    /// </summary>
    public sealed class DashboardLaunchOptions
    {
        /// <summary>窗口父级(预览模式),null 时不弹窗。</summary>
        public Window? Owner { get; set; }

        /// <summary>
        /// 业务自定义 LinkedChartContext(子类可 RegisterMirror 加更多镜像端口);null 时按
        /// <see cref="Dashboard.HorizontalLeftPixel"/> / <see cref="Dashboard.HorizontalRightPixel"/> 默认 new 一个。
        /// </summary>
        public LinkedChartContext? Context { get; set; }

        /// <summary>cell.Id → 业务运行时上下文(典型 Security)。同 BlueprintLauncher.dataSourceContext。</summary>
        public Dictionary<string, object>? DataSourceContexts { get; set; }

        /// <summary>cell.Id → 数据源配置回调(LoadAsync 之后跑)。</summary>
        public Dictionary<string, Action<object>>? Configures { get; set; }

        /// <summary>cell.Id → BlueprintHandlerRegistry(每 cell 一份隔离,避免名字串台)。</summary>
        public Dictionary<string, BlueprintHandlerRegistry>? Handlers { get; set; }

        /// <summary>
        /// cell.Id → (leafDsId → 预构造 DS 实例)。<see cref="BuildCellSchema"/> 调用
        /// <see cref="BlueprintRunner.BuildSchema"/> 时把这些实例直接塞进 instances 字典,
        /// 覆盖 framework 走 <see cref="ComponentRegistry.CreateInstance"/> 自动 new 的默认行为。
        ///
        /// <para>
        /// 典型场景:业务侧持有 <see cref="KLineDataSource"/> 实例(已 <c>SwitchContextAsync</c> 过特定 Security),
        /// 不能让 framework 用无参 ctor 新建一个空 ds 装到蓝图里。注意这跟 <see cref="DataSourceContexts"/> 的差别:
        /// 后者是让 framework 给 leaf DS 调一次 <c>LoadAsync(context)</c>,业务侧只提供 context;
        /// 前者是业务侧自己 new + LoadAsync,framework 完全跳过实例化。
        /// </para>
        /// </summary>
        public Dictionary<string, IReadOnlyDictionary<string, object>>? DataSourceInstances { get; set; }
    }

    /// <summary>
    /// §D1 dashboard 蓝图运行时入口。把 <see cref="Dashboard"/> JSON 翻译成实际跑起来的
    /// <see cref="LinkedChartDashboard"/>:per-cell 走现有 BlueprintRunner.BuildSchema 拿 schema,然后按
    /// <see cref="DashboardCellRole"/> 分发 AddMaster / AddPane / AddRaw。
    ///
    /// <para>
    /// <b>跟 <see cref="BlueprintLauncher"/> 的区别</b>:Launcher 是单 cell + 弹窗预览;DashboardLauncher 装配
    /// 多 cell 联动,产出一个 <see cref="LinkedChartDashboard"/>(UserControl),业务侧自己决定挂哪。
    /// 联动机制完全复用 <see cref="LinkedChartContext"/>(端口镜像桥) + <see cref="SchemaContext"/>(每 cell
    /// 注入主图/副图上下文),dashboard 协议层零侵入到 schema 内部。
    /// </para>
    /// </summary>
    public static class DashboardLauncher
    {
        /// <summary>
        /// 静态校验:对 dashboard 内所有 cell 跑一遍 <see cref="BlueprintLauncher.DryRun"/>,
        /// 加上 dashboard 特有的诊断(角色顺序 / Master 唯一性 / cell.Id 重复 / 0 cells)。
        /// 不实例化任何数据源,不创建 LinkedChartContext。
        /// </summary>
        public static DashboardLaunchResult DryRun(Dashboard dashboard, DashboardLaunchOptions? options = null)
        {
            if (dashboard == null) return new DashboardLaunchResult { Error = "dashboard 为 null。" };
            if (dashboard.Cells == null || dashboard.Cells.Count == 0)
                return new DashboardLaunchResult { Error = "dashboard.Cells 为空,至少需要一个 cell。" };

            var diagnostics = new List<BlueprintDiagnostic>();

            // dashboard 级校验
            if (dashboard.Cells[0].Role != DashboardCellRole.Master)
            {
                return new DashboardLaunchResult
                {
                    Error = $"第一个 cell (Id='{dashboard.Cells[0].Id}') 必须是 Master 角色,实际为 {dashboard.Cells[0].Role}。" +
                            "LinkedChartDashboard.AddMaster 必须最先调,否则视口管家不在主图上,联动黑屏。",
                };
            }

            int masterCount = 0;
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < dashboard.Cells.Count; i++)
            {
                var c = dashboard.Cells[i];
                if (c == null)
                {
                    return new DashboardLaunchResult { Error = $"dashboard.Cells[{i}] 为 null。" };
                }
                if (string.IsNullOrEmpty(c.Id))
                {
                    return new DashboardLaunchResult { Error = $"dashboard.Cells[{i}].Id 为空,Id 是 dashboard 内 cell 的稳定标识符,必填。" };
                }
                if (!seenIds.Add(c.Id))
                {
                    return new DashboardLaunchResult { Error = $"cell.Id '{c.Id}' 重复,Dashboard 内必须唯一。" };
                }
                if (c.Role == DashboardCellRole.Master) masterCount++;
                if (c.HeightRatio <= 0)
                {
                    diagnostics.Add(new BlueprintDiagnostic
                    {
                        Severity = BlueprintDiagnosticSeverity.Warning,
                        Code = "BP_DASHBOARD_BAD_HEIGHT",
                        FeatureTypeName = $"Cell:{c.Id}",
                        Message = $"HeightRatio={c.HeightRatio} 非法(需 >0),运行时 Grid RowDefinition 退化",
                    });
                }
            }

            if (masterCount > 1)
            {
                return new DashboardLaunchResult
                {
                    Error = $"dashboard 含 {masterCount} 个 Master 角色,必须恰好 1 个。" +
                            "副图请用 Pane 或 Raw。",
                };
            }

            // per-cell DryRun 复用 BlueprintLauncher
            for (int i = 0; i < dashboard.Cells.Count; i++)
            {
                var cell = dashboard.Cells[i];
                var cellHandlers = options?.Handlers != null && options.Handlers.TryGetValue(cell.Id, out var h) ? h : null;
                var blueprintResult = BlueprintLauncher.DryRun(cell.Blueprint, cellHandlers);

                if (blueprintResult.Error != null)
                {
                    return new DashboardLaunchResult
                    {
                        Error = $"Cell '{cell.Id}' 蓝图错误:{blueprintResult.Error}",
                        Diagnostics = diagnostics,
                    };
                }

                // 合并诊断,加 Cell 前缀方便回溯
                BlueprintDiagnostic? firstFatal = null;
                foreach (var d in blueprintResult.Diagnostics)
                {
                    var prefixed = new BlueprintDiagnostic
                    {
                        Severity = d.Severity,
                        Code = d.Code,
                        FeatureTypeName = string.IsNullOrEmpty(d.FeatureTypeName) ? $"Cell:{cell.Id}" : $"Cell:{cell.Id} / {d.FeatureTypeName}",
                        PortName = d.PortName,
                        Message = d.Message,
                    };
                    diagnostics.Add(prefixed);
                    // Error 严重度的诊断对 dashboard 是阻断:cell 装不起来,联动失败
                    if (d.Severity == BlueprintDiagnosticSeverity.Error && firstFatal == null) firstFatal = prefixed;
                }

                if (firstFatal != null)
                {
                    return new DashboardLaunchResult
                    {
                        Error = $"Cell '{cell.Id}' 致命诊断:{firstFatal.Code} {firstFatal.Message}",
                        Diagnostics = diagnostics,
                    };
                }
            }

            return new DashboardLaunchResult { Error = null, Diagnostics = diagnostics };
        }

        /// <summary>
        /// 装配并启动 dashboard,返回 <see cref="LinkedChartDashboard"/> 给业务侧挂载到 Window/UserControl。
        /// 失败时 Error 非空,Dashboard 为 null。Diagnostics 始终带回(成功也可能有警告)。
        /// </summary>
        public static DashboardLaunchResult LaunchEx(Dashboard dashboard, DashboardLaunchOptions? options = null)
        {
            // 先 DryRun;致命错直接返
            var pre = DryRun(dashboard, options);
            if (pre.Error != null) return pre;

            // 1. 准备 LinkedChartContext (业务自带 / 默认按 dashboard 边距 new)
            var ctx = options?.Context ?? new LinkedChartContext
            {
                HorizontalLeft = ChartLength.Pixel(dashboard.HorizontalLeftPixel),
                HorizontalRight = ChartLength.Pixel(dashboard.HorizontalRightPixel),
            };

            // §K dashboard-scope shared ports —— Dashboard.SharedPorts 配置项反射建 DataPort<T> + RegisterMirror,
            // 注入 ctx.SharedPorts。各 cell 的 ChartBlueprint.DefineFeatures 再以 "dashboard:{name}" 前缀
            // 注入到 _portRegistry,feature PortBinding 引用同名拿到同一 port instance。
            if (dashboard.SharedPorts != null && dashboard.SharedPorts.Count > 0)
            {
                ctx.RegisterFromConfig(dashboard.SharedPorts);
            }

            var linkedDashboard = new LinkedChartDashboard(ctx);

            // 2. Phase-1:逐 cell 走 BuildSchemaDeferred —— schema 装配 + 订阅就位,但**不 fire LoadAsync**。
            //    每个 cell 加入 dashboard 顺序固定(Master 在 [0]),ChartCell 默认 IsLoading=false,
            //    第 3 阶段 WireCellDeferredLoad 显式翻 true,FirstFrameReady 时翻 false。
            var deferredList = new List<DeferredSchema>(dashboard.Cells.Count);
            for (int i = 0; i < dashboard.Cells.Count; i++)
            {
                var cell = dashboard.Cells[i];
                var cellOptions = ResolveCellOptions(cell.Id, options);

                var (deferred, error) = BuildCellSchemaDeferred(cell, cellOptions);
                if (error != null)
                {
                    return new DashboardLaunchResult
                    {
                        Error = $"Cell '{cell.Id}' 装配失败:{error}",
                        Diagnostics = pre.Diagnostics,
                    };
                }

                var schema = (Hevo.Charting.Core.ReactiveSchema)deferred!.Schema;
                switch (cell.Role)
                {
                    case DashboardCellRole.Master:
                        linkedDashboard.AddMaster(schema, cell.HeightRatio);
                        break;
                    case DashboardCellRole.Pane:
                        linkedDashboard.AddPane(schema, cell.HeightRatio);
                        break;
                    case DashboardCellRole.Raw:
                        linkedDashboard.AddRaw(schema, cell.HeightRatio);
                        break;
                }
                deferredList.Add(deferred);
            }

            // 3. Phase-2:dashboard 级 loading barrier ——
            //    所有 cell 一起 IsLoading=true,等 Task.WhenAll(各 cell FirstFrameReady) 完成后
            //    再一起 IsLoading=false。视觉上"全黑屏 spinner" → "全部同帧亮起"。
            //
            //    之前(WireCellDeferredLoad)是 per-cell 独立翻牌:谁先 ready 谁先 IsLoading=false。
            //    各 cell DS 都用同一份 mock 数据但 LoadAsync 走 thread-pool,完成时机有抖动 ——
            //    经常出现"strategy 已经画了 main 还在 loading"的视觉错乱。
            //
            //    cell.Loaded 触发 StartLoading 仍然 per-cell(每个 cell 自己的 UI 生命周期决定何时派发)。
            //    Barrier 只统一 IsLoading 翻牌时机。
            var cells = linkedDashboard.Cells;
            WireDashboardBarrier(cells, deferredList);

            return new DashboardLaunchResult
            {
                Dashboard = linkedDashboard,
                Diagnostics = pre.Diagnostics,
            };
        }

        /// <summary>
        /// Dashboard-level loading barrier:
        /// <list type="number">
        ///   <item>所有 cell 立即 IsLoading=true</item>
        ///   <item>每个 cell 的 Loaded 事件(WPF 生命周期)触发 BlueprintRunner.StartLoading 派发 LoadAsync</item>
        ///   <item>等 Task.WhenAll(所有 cell 的 FirstFrameReady) 完成 → 同帧统一 IsLoading=false</item>
        /// </list>
        /// <para>
        /// <b>兜底</b>:某个 cell 永远 ready 不了(DS 永不 publish / DefaultContext 配错)→ 10 秒超时后强制翻牌,
        /// 避免"一个坏 cell 卡死整个 dashboard"。
        /// </para>
        /// </summary>
        private static void WireDashboardBarrier(IReadOnlyList<ChartCell> cells, IReadOnlyList<DeferredSchema> deferredList)
        {
            // (1) 所有 cell 同时进 loading 状态
            for (int i = 0; i < cells.Count; i++) cells[i].IsLoading = true;

            // (2) per-cell:Loaded 事件触发 StartLoading 派发(这一步仍是 per-cell;
            //     Master / Pane 在不同的 Grid Row,WPF 加载时机可能略不同,但 LoadAsync 派发本身不影响其它 cell)
            for (int i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                var def  = deferredList[i];
                if (cell.IsLoaded)
                {
                    ScheduleStart(cell, def);
                }
                else
                {
                    System.Windows.RoutedEventHandler? handler = null;
                    handler = (_, _) =>
                    {
                        cell.Loaded -= handler;
                        ScheduleStart(cell, def);
                    };
                    cell.Loaded += handler;
                }
            }

            // (3) Dashboard-level barrier:等所有 FirstFrameReady,然后同帧翻所有 cell 的 IsLoading=false。
            //     10 秒兜底,避免单个坏 cell 卡死整个 dashboard。
            var allReady = Task.WhenAll(deferredList.Select(d => d.FirstFrameReady).ToArray());
            var timeout  = Task.Delay(TimeSpan.FromSeconds(10));
            Task.WhenAny(allReady, timeout).ContinueWith(_ =>
            {
                // 在每个 cell 的 Dispatcher 上同帧 BeginInvoke(同一个 UI 线程 → 视觉一致)
                foreach (var cell in cells)
                {
                    cell.Dispatcher.BeginInvoke(
                        new Action(() => cell.IsLoading = false),
                        System.Windows.Threading.DispatcherPriority.DataBind);
                }
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        private static void ScheduleStart(ChartCell c, DeferredSchema d)
        {
            c.Dispatcher.BeginInvoke(
                new Action(() => _ = BlueprintRunner.StartLoading(d)),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        // 旧 WireCellDeferredLoad(per-cell 独立翻牌)已删除,被 WireDashboardBarrier 替代 ——
        // 多 cell dashboard 视觉一致性优先:全黑屏 → 全部同帧亮起。
        // 若业务侧有"per-cell 独立 loading"需求,自行处理 cell.IsLoading 即可。

        /// <summary>
        /// 极简版本:返回错误字符串(成功 null)+ 已装好的 dashboard 通过 out 参数。失败时 dashboard 为 null。
        /// </summary>
        public static string? Launch(Dashboard dashboard, out LinkedChartDashboard? linkedDashboard, DashboardLaunchOptions? options = null)
        {
            var result = LaunchEx(dashboard, options);
            linkedDashboard = result.Dashboard;
            return result.Error;
        }

        // ── per-cell 工具 ────────────────────────────────────────────────────

        private readonly record struct CellOptions(
            object? DataSourceContext,
            Action<object>? Configure,
            BlueprintHandlerRegistry? Handlers,
            IReadOnlyDictionary<string, object>? PrebuiltInstances);

        private static CellOptions ResolveCellOptions(string cellId, DashboardLaunchOptions? options)
        {
            object? ctx = null;
            Action<object>? cfg = null;
            BlueprintHandlerRegistry? h = null;
            IReadOnlyDictionary<string, object>? prebuilt = null;
            if (options?.DataSourceContexts != null) options.DataSourceContexts.TryGetValue(cellId, out ctx!);
            if (options?.Configures != null) options.Configures.TryGetValue(cellId, out cfg!);
            if (options?.Handlers != null) options.Handlers.TryGetValue(cellId, out h!);
            if (options?.DataSourceInstances != null) options.DataSourceInstances.TryGetValue(cellId, out prebuilt!);
            return new CellOptions(ctx, cfg, h, prebuilt);
        }

        /// <summary>
        /// 为单个 cell 构造 schema:复刻 <see cref="BlueprintLauncher.LaunchInternal"/> 中的 ds 实例化 + LoadAsync + Stream
        /// 取值 + BuildSchema 反射调度路径,但不绑 ChartCell(由 LinkedChartDashboard 自己创建 cell)。
        /// <para>
        /// <b>deferred</b> 版:返回 <see cref="DeferredSchema"/>(schema 装配 + 订阅就位但不 fire LoadAsync),
        /// 上层 dashboard 把所有 cell 都加入 visual tree 后再统一 Background 派发 StartLoading,跨蓝图并行。
        /// </para>
        /// 失败返回 (null, errorMessage)。
        /// </summary>
        private static (DeferredSchema? Deferred, string? Error) BuildCellSchemaDeferred(DashboardCell cell, CellOptions opts)
        {
            var bp = cell.Blueprint;
            if (bp.DataSources.Count == 0) return (null, "缺少 DataSource 节点");

            // Render-leaf 推导(同 BlueprintLauncher / BlueprintRunner) —— opts.Context/Configure 只作用在 leaf 上。
            var upstreamIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in bp.Cascades)
                if (!string.IsNullOrEmpty(c.FromDataSourceId)) upstreamIds.Add(c.FromDataSourceId);
            var leafCandidates = bp.DataSources.Where(d => !upstreamIds.Contains(d.Id)).ToList();
            if (leafCandidates.Count == 0)
                return (null, "Render-leaf 推导失败:DataSources 全是 Cascade 上游");
            if (leafCandidates.Count > 1)
                return (null, $"Render-leaf 推导失败:多 leaf({string.Join(", ", leafCandidates.Select(l => l.Id))})");

            var leafDsModel = leafCandidates[0];
            if (string.IsNullOrEmpty(leafDsModel.TypeName)) return (null, $"DataSource '{leafDsModel.Id}' TypeName 为空");
            if (string.IsNullOrEmpty(leafDsModel.Id)) return (null, "DataSource Id 为空(节点化协议必填)");

            Type leafDsType;
            try { leafDsType = ComponentRegistry.Resolve(leafDsModel.TypeName); }
            catch (Exception ex) { return (null, $"DataSource 类型未登记:{ex.Message}"); }

            if (!BlueprintDataSourceProbe.IsValidDataSourceType(leafDsType, out var probeError))
                return (null, probeError);

            // 业务侧预构造路径:opts.PrebuiltInstances[cell.Id][leafDsModel.Id] 存在 →
            // 用业务侧已 SwitchContextAsync / LoadAsync 过的实例,framework 跳过 CreateInstance + LoadAsync + Configure。
            // 典型场景见 BlueprintDashboardCanvasConfig.DataSourceInstances 文档。
            object leafDsInstance;
            bool leafIsPrebuilt = opts.PrebuiltInstances != null
                && opts.PrebuiltInstances.TryGetValue(leafDsModel.Id, out var preLeaf)
                && preLeaf != null;
            if (leafIsPrebuilt)
            {
                leafDsInstance = opts.PrebuiltInstances![leafDsModel.Id];
                if (!leafDsType.IsInstanceOfType(leafDsInstance))
                    return (null, $"预构造 DS '{leafDsModel.Id}' 实例类型 {leafDsInstance.GetType().Name} 跟蓝图声明 {leafDsType.Name} 不匹配");
            }
            else
            {
                if (leafDsType.GetConstructor(Type.EmptyTypes) == null)
                    return (null, $"{leafDsType.Name} 没有公共无参构造函数");

                try { leafDsInstance = ComponentRegistry.CreateInstance(leafDsType); }
                catch (Exception ex) { return (null, $"实例化 {leafDsType.Name} 失败:{ex.Message}"); }

                // LoadAsync(context) 反射(同 BlueprintLauncher 的"路 A")
                if (opts.DataSourceContext != null)
                {
                    var ctxType = opts.DataSourceContext.GetType();
                    var loadMethod = leafDsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => m.Name == "LoadAsync" && m.GetParameters().Length >= 1)
                        .FirstOrDefault(m => m.GetParameters()[0].ParameterType.IsAssignableFrom(ctxType));
                    if (loadMethod != null)
                    {
                        var ps = loadMethod.GetParameters();
                        var args = new object?[ps.Length];
                        args[0] = opts.DataSourceContext;
                        for (int i = 1; i < ps.Length; i++)
                        {
                            args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                                      : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                        }
                        try { _ = loadMethod.Invoke(leafDsInstance, args); }
                        catch (Exception ex) { return (null, $"调用 {leafDsType.Name}.LoadAsync 失败:{ex.Message}"); }
                    }
                }

                // 万能 configure 回调(路 B)
                try { opts.Configure?.Invoke(leafDsInstance); }
                catch (Exception ex) { return (null, $"configureDataSource 抛异常:{ex.Message}"); }
            }

            // 走 BlueprintRunner.BuildSchemaDeferred —— leaf + 业务预构造的非 leaf DS 全塞 instances,
            // 剩下没塞的由 framework 自动 ComponentRegistry.Resolve + 无参 new(scanner 的 stocklist Composite 上游典型走自动路径)。
            // Deferred 形态:返回的 DeferredSchema 还没 fire LoadAsync,等 LaunchEx 把所有 cell 都加入 visual tree 后统一 Background 派发。
            try
            {
                var instances = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [leafDsModel.Id] = leafDsInstance,
                };
                if (opts.PrebuiltInstances != null)
                {
                    foreach (var kv in opts.PrebuiltInstances)
                    {
                        if (kv.Key == leafDsModel.Id) continue;   // leaf 已 set 过,无需 overwrite
                        if (kv.Value != null) instances[kv.Key] = kv.Value;
                    }
                }
                var deferred = BlueprintRunner.BuildSchemaDeferred(bp, instances, opts.Handlers);
                if (deferred.Schema is not ReactiveSchema)
                    return (null, "BlueprintRunner.BuildSchemaDeferred 返回非 ReactiveSchema 实例,蓝图装配失败");

                // wire cascade / trigger / cell-less 路径 —— Deferred 单独走的话 WireReactiveEdges 不在 BuildSchemaDeferred 内,
                // 这里 dashboard 没 ChartCell 引用(cell 由 LinkedChartDashboard.AppendSlot 创建),传 null 等价 BuildSchema 自己的旧路径。
                Hevo.Charting.LowCode.Designer.BlueprintRunner.WireReactiveEdgesForCellless(deferred);
                return (deferred, null);
            }
            catch (Exception ex) { return (null, $"BlueprintRunner.BuildSchemaDeferred 抛异常:{(ex.InnerException ?? ex).Message}"); }
        }
    }
}
