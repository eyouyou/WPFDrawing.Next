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

            var linkedDashboard = new LinkedChartDashboard(ctx);

            // 2. 逐 cell 装配 (顺序 = Cells 列表,Master 已在 DryRun 校过必为 [0])
            for (int i = 0; i < dashboard.Cells.Count; i++)
            {
                var cell = dashboard.Cells[i];
                var cellOptions = ResolveCellOptions(cell.Id, options);

                var (schema, error) = BuildCellSchema(cell, cellOptions);
                if (error != null)
                {
                    return new DashboardLaunchResult
                    {
                        Error = $"Cell '{cell.Id}' 装配失败:{error}",
                        Diagnostics = pre.Diagnostics,
                    };
                }

                switch (cell.Role)
                {
                    case DashboardCellRole.Master:
                        linkedDashboard.AddMaster(schema!, cell.HeightRatio);
                        break;
                    case DashboardCellRole.Pane:
                        linkedDashboard.AddPane(schema!, cell.HeightRatio);
                        break;
                    case DashboardCellRole.Raw:
                        linkedDashboard.AddRaw(schema!, cell.HeightRatio);
                        break;
                }
            }

            return new DashboardLaunchResult
            {
                Dashboard = linkedDashboard,
                Diagnostics = pre.Diagnostics,
            };
        }

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

        private readonly record struct CellOptions(object? DataSourceContext, Action<object>? Configure, BlueprintHandlerRegistry? Handlers);

        private static CellOptions ResolveCellOptions(string cellId, DashboardLaunchOptions? options)
        {
            object? ctx = null;
            Action<object>? cfg = null;
            BlueprintHandlerRegistry? h = null;
            if (options?.DataSourceContexts != null) options.DataSourceContexts.TryGetValue(cellId, out ctx!);
            if (options?.Configures != null) options.Configures.TryGetValue(cellId, out cfg!);
            if (options?.Handlers != null) options.Handlers.TryGetValue(cellId, out h!);
            return new CellOptions(ctx, cfg, h);
        }

        /// <summary>
        /// 为单个 cell 构造 schema:复刻 <see cref="BlueprintLauncher.LaunchInternal"/> 中的 ds 实例化 + LoadAsync + Stream
        /// 取值 + BuildSchema 反射调度路径,但不绑 ChartCell(由 LinkedChartDashboard 自己创建 cell)。
        /// 失败返回 (null, errorMessage)。
        /// </summary>
        private static (ReactiveSchema? Schema, string? Error) BuildCellSchema(DashboardCell cell, CellOptions opts)
        {
            var bp = cell.Blueprint;
            if (bp.DataSource == null || string.IsNullOrEmpty(bp.DataSource.TypeName))
                return (null, "缺少 DataSource 节点");

            Type dsType;
            try { dsType = ComponentRegistry.Resolve(bp.DataSource.TypeName); }
            catch (Exception ex) { return (null, $"DataSource 类型未登记:{ex.Message}"); }

            if (dsType.GetConstructor(Type.EmptyTypes) == null)
                return (null, $"{dsType.Name} 没有公共无参构造函数");

            var itemType = NodeFactory.FindDataSourceItemType(dsType);
            if (itemType == null)
                return (null, $"{dsType.Name} 不是 DataSource<TSource, TItem> 派生类型");

            object dsInstance;
            try { dsInstance = ComponentRegistry.CreateInstance(dsType); }
            catch (Exception ex) { return (null, $"实例化 {dsType.Name} 失败:{ex.Message}"); }

            // LoadAsync(context) 反射(同 BlueprintLauncher 的"路 A")
            if (opts.DataSourceContext != null)
            {
                var ctxType = opts.DataSourceContext.GetType();
                var loadMethod = dsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
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
                    try { _ = loadMethod.Invoke(dsInstance, args); }
                    catch (Exception ex) { return (null, $"调用 {dsType.Name}.LoadAsync 失败:{ex.Message}"); }
                }
            }

            // 万能 configure 回调(路 B)
            try { opts.Configure?.Invoke(dsInstance); }
            catch (Exception ex) { return (null, $"configureDataSource 抛异常:{ex.Message}"); }

            // ds.Stream
            var streamProp = dsType.GetProperty("Stream", BindingFlags.Public | BindingFlags.Instance);
            object? stream = streamProp?.GetValue(dsInstance);
            if (stream == null)
                return (null, $"{dsType.Name}.Stream 取不到值");

            // 反射 BlueprintRunner.BuildSchema<TItem>(blueprint, dsInstance, stream, handlers)
            try
            {
                var buildMethod = typeof(BlueprintRunner).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "BuildSchema" && m.IsGenericMethod && m.GetGenericArguments().Length == 1
                                && m.GetParameters().Length == 4)
                    .FirstOrDefault();
                if (buildMethod == null) return (null, "找不到 BlueprintRunner.BuildSchema<TItem>(...) 方法");
                var generic = buildMethod.MakeGenericMethod(itemType);
                var schema = generic.Invoke(null, new object?[] { bp, dsInstance, stream, opts.Handlers }) as ReactiveSchema;
                if (schema == null) return (null, "BuildSchema 返回 null,蓝图装配失败");
                return (schema, null);
            }
            catch (TargetInvocationException ex) { return (null, $"BuildSchema 抛异常:{ex.InnerException?.Message ?? ex.Message}"); }
            catch (Exception ex) { return (null, $"BuildSchema 抛异常:{ex.Message}"); }
        }
    }
}
