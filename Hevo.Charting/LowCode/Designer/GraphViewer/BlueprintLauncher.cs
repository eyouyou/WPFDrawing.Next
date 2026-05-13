using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using System.Reflection;
using System.Windows;
using System.Windows.Media;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    public enum BlueprintDiagnosticSeverity { Warning, Error }

    /// <summary>
    /// 蓝图诊断条目 —— Launch 之前 dry-run 校验产出。
    /// Warning 不阻止启动(运行时 silent skip 行为保留),Error 直接中断启动。
    /// </summary>
    public sealed class BlueprintDiagnostic
    {
        public BlueprintDiagnosticSeverity Severity { get; init; }
        /// <summary>稳定 code,UI / 业务可据此分类显示。</summary>
        public string Code { get; init; } = "";
        public string FeatureTypeName { get; init; } = "";
        public string PortName { get; init; } = "";
        public string Message { get; init; } = "";

        public override string ToString()
        {
            var loc = string.IsNullOrEmpty(FeatureTypeName)
                ? PortName
                : (string.IsNullOrEmpty(PortName) ? FeatureTypeName : $"{FeatureTypeName}.{PortName}");
            return string.IsNullOrEmpty(loc)
                ? $"[{Severity}] {Code}: {Message}"
                : $"[{Severity}] {Code} {loc}: {Message}";
        }
    }

    /// <summary>
    /// <see cref="BlueprintLauncher.LaunchEx"/> / <see cref="BlueprintLauncher.DryRun"/> 的结构化结果。
    /// 旧的 string? 返回值入口 (<see cref="BlueprintLauncher.Launch"/>) 仍保留,只是把 Error 拆出来。
    /// </summary>
    public sealed class BlueprintLaunchResult
    {
        public string? Error { get; init; }
        public IReadOnlyList<BlueprintDiagnostic> Diagnostics { get; init; } = Array.Empty<BlueprintDiagnostic>();
        public bool Launched => Error == null;
    }

    /// <summary>
    /// 把 GraphViewer 当前画布编译出来的 ChartBlueprint 跑起来。
    /// 反射 BlueprintRunner.Run&lt;TItem&gt; 泛型调用,弹一个独立窗口承载 ChartCell。
    /// 调用方失败时不会抛——返回错误字符串供 UI 显示。
    ///
    /// <para>
    /// <b>关于运行时上下文注入</b>:蓝图模型只描述拓扑(类型 / 属性 / 端口),
    /// 描述不出"业务上下文"(典型:<c>TimeShareDataSource.LoadAsync(security)</c> 或 <c>Context = ...</c>)。
    /// 通过 <paramref name="configureDataSource"/> 回调,在 BlueprintRunner 执行**之前**给数据源注入运行时上下文,
    /// 业务侧可以为每种数据源准备一个 demo loader (例如:if (ds is TimeShareDataSource t) await t.LoadAsync(...))。
    /// 不传该回调则数据源保持初始空状态,LogicalLength=0 → ViewportManager 会写 invalid range → 整个图表
    /// 不渲染任何数据,仅看到底色(俗称"黑屏")。
    /// </para>
    /// </summary>
    public static class BlueprintLauncher
    {
        /// <summary>
        /// 启动并返回错误描述(成功时返回 null)。失败时弹窗 owner 用作父级。
        /// <para>
        /// 注入数据源运行时上下文有两条路,任选其一即可:
        /// <list type="bullet">
        ///   <item><b><paramref name="dataSourceContext"/></b>:类型化便捷参数。Launcher 反射查 DataSource 上是否有
        ///         <c>LoadAsync(TContext)</c> 方法(<see cref="ReactiveDataSource{TSource, TContext, TItem}"/> 派生类标配),
        ///         参数类型可赋值 → 自动调用,fire-and-forget。</item>
        ///   <item><b><paramref name="configureDataSource"/></b>:万能回调。<paramref name="dataSourceContext"/> 应用之后
        ///         同步执行,业务可在此自由处理(MinuteCeiling 配置 / 多步 Load 链 / 自定义 SwitchContext 等)。</item>
        /// </list>
        /// 两个参数同时提供时,先按类型化参数走 LoadAsync,再跑回调。
        /// </para>
        /// </summary>
        public static string? Launch(
            ChartBlueprint blueprint,
            Window? owner = null,
            object? dataSourceContext = null,
            Action<object>? configureDataSource = null,
            BlueprintHandlerRegistry? handlers = null)
            => LaunchEx(blueprint, owner, dataSourceContext, configureDataSource, handlers).Error;

        /// <summary>
        /// 结构化版 Launch:返回 <see cref="BlueprintLaunchResult"/>,既有 Error(致命错误,启动被中断)
        /// 也有 Diagnostics(警告,启动了但有连接被 skip)。UI 侧可用诊断列表把"加载成功但黑屏"
        /// 这种隐性故障变成显式提醒,调试时间从分钟级降到秒级。
        /// <para>
        /// <paramref name="handlers"/>(协议扩展 §K):蓝图含 <see cref="ChartBlueprint.Triggers"/> 或
        /// Feature Properties 里有 Delegate 字段(如 OnRequireDataAsync / FutureXLabelFormatter)时,
        /// 业务侧通过 <see cref="BlueprintHandlerRegistry.RegisterFetch"/> /
        /// <see cref="BlueprintHandlerRegistry.RegisterDelegate"/> 注册命名委托,蓝图侧用 string 名字引用。
        /// </para>
        /// </summary>
        public static BlueprintLaunchResult LaunchEx(
            ChartBlueprint blueprint,
            Window? owner = null,
            object? dataSourceContext = null,
            Action<object>? configureDataSource = null,
            BlueprintHandlerRegistry? handlers = null)
        {
            // 先 dry-run 收诊断;致命错误直接返回,警告随启动结果带回。
            var diag = DryRun(blueprint, handlers);
            if (diag.Error != null) return diag;

            var startError = LaunchInternal(blueprint, owner, dataSourceContext, configureDataSource, handlers);
            return new BlueprintLaunchResult { Error = startError, Diagnostics = diag.Diagnostics };
        }

        /// <summary>
        /// 不实际启动,只对蓝图做静态校验。返回致命错误 + 警告诊断。
        /// 检查项:
        /// 1. DataSource / Trait / Feature 的 TypeName 全部已在 ComponentRegistry 登记 (未登记 → Error)。
        /// 2. 同一全局 portId 被多次声明的类型必须一致 (典型坑:Time:DateTime 错连到 *:double → Warning)。
        /// 3. Feature 的 Output 端口必须在自己的 PortBindings 里出现 (未焊接 → Warning,运行时 Watch 会
        ///    在 null 端口上 NRE 静默吞 → 黑屏,这是 GraphSerializer 已修过的高危坑见低代码.md §8.1)。
        /// 4. DataSource.Stream 属性可达 (取不到 → Error)。
        /// </summary>
        public static BlueprintLaunchResult DryRun(ChartBlueprint blueprint, BlueprintHandlerRegistry? handlers = null)
        {
            if (blueprint == null) return new BlueprintLaunchResult { Error = "blueprint 为 null。" };

            var diagnostics = new List<BlueprintDiagnostic>();

            if (blueprint.DataSources.Count == 0)
                return new BlueprintLaunchResult { Error = "蓝图缺少 DataSource 节点。请先在画布上添加一个数据源。" };

            // 端口类型登记表 —— 模拟运行时 GetOrCreatePort 的"先注册先赢"语义,
            // 第二次以不同类型注册同 portId 即冲突。值里带 source 串方便诊断回溯。
            var portTypes = new Dictionary<string, (Type Type, string Source)>(StringComparer.Ordinal);

            // ViewportPorts 三根 well-known id —— DynamicChartSchema ctor 预登记的常驻端口。
            portTypes["VP_LogicalLength"] = (typeof(int),       "Viewport.LogicalLength");
            portTypes["VP_UserRange"]     = (typeof(RealRange), "Viewport.UserRange");
            portTypes["VP_ActiveRange"]   = (typeof(RealRange), "Viewport.ActiveRange");

            Type? primaryDsType = null;   // render-leaf 的 dsType,用于后面 fm.InputBindings/OutputBindings 校验
            Type? primaryItemType = null;
            foreach (var dsm in blueprint.DataSources)
            {
                if (string.IsNullOrEmpty(dsm.TypeName))
                    return new BlueprintLaunchResult { Error = $"DataSource '{dsm.Id}' TypeName 为空。" };

                Type curDsType;
                try { curDsType = ComponentRegistry.Resolve(dsm.TypeName); }
                catch (Exception ex) { return new BlueprintLaunchResult { Error = $"DataSource '{dsm.Id}' 类型未登记:{ex.Message}" }; }

                if (curDsType.GetConstructor(Type.EmptyTypes) == null)
                    return new BlueprintLaunchResult { Error = $"{curDsType.Name} 没有公共无参构造函数,低代码场景无法实例化。" };

                var curItemType = NodeFactory.FindDataSourceItemType(curDsType);
                if (curItemType == null)
                    return new BlueprintLaunchResult { Error = $"{curDsType.Name} 不是 DataSource<TSource, TItem> 的派生类型。" };

                if (curDsType.GetProperty("Stream", BindingFlags.Public | BindingFlags.Instance) == null)
                    return new BlueprintLaunchResult { Error = $"{curDsType.Name}.Stream 属性不存在,请确认其继承自 DataSource<,>。" };

                primaryDsType ??= curDsType;
                primaryItemType ??= curItemType;

                // 节点化协议:DS.OutputBindings —— 反射 DS 自身 scalar 优先,然后 TItem 行字段。
                foreach (var kv in dsm.OutputBindings)
                {
                    var portId = PortBindingValue.ExtractSingle(kv.Value);
                    if (string.IsNullOrEmpty(portId)) continue;

                    var scalarProp = curDsType.GetProperty(kv.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (scalarProp != null && IsScalarTypeForDryRun(scalarProp.PropertyType))
                    {
                        RegisterPortType(portTypes, portId, scalarProp.PropertyType, $"{curDsType.Name}.{kv.Key}", diagnostics);
                        continue;
                    }
                    var vectorProp = curItemType.GetProperty(kv.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (vectorProp != null && IsScalarTypeForDryRun(vectorProp.PropertyType))
                    {
                        var memType = typeof(ReadOnlyMemory<>).MakeGenericType(vectorProp.PropertyType);
                        RegisterPortType(portTypes, portId, memType, $"{curItemType.Name}.{kv.Key}", diagnostics);
                    }
                }
            }
            // 单 DS / 单 leaf 校验场景下,后面 feature 校验拿 primary(第一个 DS) 即可。
            // 多 DS / 多 leaf 场景 BlueprintRunner.PickRenderLeaf 自己挑;DryRun 阶段不严格区分。
            Type dsType = primaryDsType!;
            Type itemType = primaryItemType!;

            // Trait TypeName 校验 (登记缺失即蓝图加载必抛)
            foreach (var t in blueprint.InitialTraits)
            {
                if (!ComponentRegistry.IsRegistered(t.TraitTypeName))
                {
                    diagnostics.Add(new BlueprintDiagnostic
                    {
                        Severity = BlueprintDiagnosticSeverity.Error,
                        Code = "BP_UNREGISTERED_TYPE",
                        FeatureTypeName = t.TraitTypeName,
                        Message = "Trait 类型未在 ComponentRegistry 登记,蓝图加载将抛异常",
                    });
                }
            }

            // Feature 逐个校验
            foreach (var f in blueprint.Features)
            {
                Type? featureType = null;
                try { featureType = ComponentRegistry.Resolve(f.TypeName); }
                catch
                {
                    diagnostics.Add(new BlueprintDiagnostic
                    {
                        Severity = BlueprintDiagnosticSeverity.Error,
                        Code = "BP_UNREGISTERED_TYPE",
                        FeatureTypeName = f.TypeName,
                        Message = "Feature 类型未在 ComponentRegistry 登记,蓝图加载将抛异常",
                    });
                    continue;
                }

                // Output 端口必须有 binding(参考 低代码.md §8.1 "GraphSerializer 漏写 OUTPUT 端口绑定"坑)
                // nested dict Output(如 SeriesOutputs)用 StartsWith 前缀覆盖检查。
                foreach (var pi in featureType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!IsDataPortProperty(pi, out var portValueType, out _)) continue;
                    if (PortMetadataRegistry.ResolveDirection(featureType, pi) != PortDirection.Output) continue;
                    if (!f.OutputBindings.ContainsKey(pi.Name)
                        && !f.OutputBindings.Keys.Any(k => k.StartsWith(pi.Name + ".", StringComparison.Ordinal)))
                    {
                        diagnostics.Add(new BlueprintDiagnostic
                        {
                            Severity = BlueprintDiagnosticSeverity.Warning,
                            Code = "BP_OUTPUT_PORT_UNBOUND",
                            FeatureTypeName = f.TypeName,
                            PortName = pi.Name,
                            Message = "Output 端口未在 OutputBindings 中焊接 → 运行时下游 Watch 拿到 null 触发 NRE 静默吞 → 黑屏",
                        });
                    }
                }

                // 类型一致性 —— 用 binding 标注的 globalId,反向校验跟已注册端口类型是否吻合。
                // §D2.X 多输入指标 nested key 形如 "Inputs.high":拆 ParentField + ChildKey,
                // 反查 Feature 上 Dictionary<string, DataPort<T>> 字段,取 value type 校验。
                foreach (var b in f.InputBindings.Concat(f.OutputBindings))
                {
                    int dotIdx = b.Key.IndexOf('.');
                    if (dotIdx > 0)
                    {
                        var parent = b.Key.Substring(0, dotIdx);
                        var child  = b.Key.Substring(dotIdx + 1);
                        var pp = featureType.GetProperty(parent,
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (pp == null) continue;
                        var pt = pp.PropertyType;
                        if (!pt.IsGenericType || pt.GetGenericTypeDefinition() != typeof(Dictionary<,>)) continue;
                        var dictArgs = pt.GetGenericArguments();
                        if (dictArgs[0] != typeof(string)) continue;
                        if (!dictArgs[1].IsGenericType || dictArgs[1].GetGenericTypeDefinition() != typeof(DataPort<>)) continue;
                        var nestedExpected = dictArgs[1].GetGenericArguments()[0];
                        var id = PortBindingValue.ExtractSingle(b.Value);
                        if (!string.IsNullOrEmpty(id))
                            RegisterPortType(portTypes, id, nestedExpected, $"{f.TypeName}.{b.Key}", diagnostics);
                        continue;
                    }

                    var pi = featureType.GetProperty(b.Key,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (pi == null) continue;
                    if (!IsDataPortProperty(pi, out var expected, out var isArray)) continue;

                    // PortBindingValue 统一识别 string / list / JSON array / CSV。
                    var ids = isArray
                        ? PortBindingValue.ExtractList(b.Value)
                        : (IReadOnlyList<string>)new[] { PortBindingValue.ExtractSingle(b.Value) };
                    foreach (var id in ids)
                    {
                        if (string.IsNullOrEmpty(id)) continue;
                        RegisterPortType(portTypes, id, expected, $"{f.TypeName}.{b.Key}", diagnostics);
                    }
                }

                // §D2.X InputOrder 一致性校验 —— 多输入路径下,Feature 上有 Dictionary<string, DataPort<T>>
                // 字段且 PortBindings 含 nested key,InputOrder 数组里每个名字必须有对应 PortBindings。
                CheckMultiInputBindings(f, featureType, diagnostics);

                // §D2.6.4 IncrementalComputeFeature 校验 —— state 端口必填 / handler 类型符合
                CheckIncrementalCompute(f, featureType, handlers, diagnostics);

                // PlotFeature 统一 series 协议校验 —— 每个 PlotSeriesSpec.Kind 落 line/bar/scatter/arrow_markers 之一
                CheckPlotSeries(f, featureType, diagnostics);

                // Delegate 属性的 handler 名引用 —— Properties 里 string 值指向 Delegate 类型属性时,
                // 必须有同名 handler 注册过,否则运行时 ResolveHandlerReferences 会 silent skip → 黑屏。
                foreach (var p in f.Properties)
                {
                    if (p.Value is not string handlerName || string.IsNullOrEmpty(handlerName)) continue;
                    var pi = featureType.GetProperty(p.Key,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (pi == null) continue;
                    if (!typeof(Delegate).IsAssignableFrom(pi.PropertyType)) continue;
                    if (handlers == null || !handlers.Contains(handlerName))
                    {
                        diagnostics.Add(new BlueprintDiagnostic
                        {
                            Severity = BlueprintDiagnosticSeverity.Warning,
                            Code = "BP_HANDLER_NOT_REGISTERED",
                            FeatureTypeName = f.TypeName,
                            PortName = p.Key,
                            Message = $"Delegate 属性引用 handler '{handlerName}' 但 BlueprintHandlerRegistry 未注册,运行时该字段会被 skip",
                        });
                    }
                }
            }

            // §D2.5.E2E PyCompute / 任何"中间计算节点"的 ROM<double> 输出 → 必须连到 AutoScale.ValuePorts,
            // 否则 Y 范围只看其他 series,该指标输出可能完全在画布外被裁掉。
            // 检测:对每个 ROM<double> 输出端口,如果它的 globalId 被某 LineSeries.DataPort/BarSeries.DataPort
            // 消费,但 *没* 被任何 UniversalAutoScale.ValuePorts 消费 → 警告 BP_OUTPUT_NOT_SCALED。
            CheckOutputAutoScaleCoverage(blueprint, diagnostics);

            // Triggers 的 handler 引用校验 —— 协议扩展 §K
            foreach (var trig in blueprint.Triggers)
            {
                if (trig.Kind != "Interval")
                {
                    diagnostics.Add(new BlueprintDiagnostic
                    {
                        Severity = BlueprintDiagnosticSeverity.Warning,
                        Code = "BP_TRIGGER_UNKNOWN_KIND",
                        FeatureTypeName = trig.Handler,
                        Message = $"Trigger Kind '{trig.Kind}' 未支持(目前只有 Interval),运行时跳过",
                    });
                    continue;
                }
                if (trig.IntervalSeconds <= 0)
                {
                    diagnostics.Add(new BlueprintDiagnostic
                    {
                        Severity = BlueprintDiagnosticSeverity.Warning,
                        Code = "BP_TRIGGER_BAD_INTERVAL",
                        FeatureTypeName = trig.Handler,
                        Message = $"Interval={trig.IntervalSeconds}s 非法,需 >0",
                    });
                    continue;
                }
                if (handlers == null || !handlers.Contains(trig.Handler))
                {
                    diagnostics.Add(new BlueprintDiagnostic
                    {
                        Severity = BlueprintDiagnosticSeverity.Warning,
                        Code = "BP_TRIGGER_HANDLER_MISSING",
                        FeatureTypeName = trig.Handler,
                        Message = $"Trigger handler '{trig.Handler}' 未在 BlueprintHandlerRegistry 注册,运行时跳过",
                    });
                }
            }

            return new BlueprintLaunchResult { Error = null, Diagnostics = diagnostics };
        }

        private static bool IsDataPortProperty(PropertyInfo pi, out Type valueType, out bool isArray)
        {
            valueType = typeof(object);
            isArray = false;
            var pt = pi.PropertyType;
            if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(DataPort<>))
            {
                valueType = pt.GetGenericArguments()[0];
                return true;
            }
            if (pt.IsArray)
            {
                var elem = pt.GetElementType();
                if (elem != null && elem.IsGenericType && elem.GetGenericTypeDefinition() == typeof(DataPort<>))
                {
                    valueType = elem.GetGenericArguments()[0];
                    isArray = true;
                    return true;
                }
            }
            return false;
        }

        private static void RegisterPortType(
            Dictionary<string, (Type Type, string Source)> registry,
            string portId,
            Type type,
            string source,
            List<BlueprintDiagnostic> diagnostics)
        {
            if (string.IsNullOrEmpty(portId)) return;
            if (registry.TryGetValue(portId, out var existing))
            {
                if (existing.Type != type)
                {
                    diagnostics.Add(new BlueprintDiagnostic
                    {
                        Severity = BlueprintDiagnosticSeverity.Warning,
                        Code = "BP_PORT_TYPE_MISMATCH",
                        PortName = portId,
                        Message = $"端口类型冲突: 已被 {existing.Source} 注册为 {existing.Type.Name}," +
                                  $" {source} 试图重连为 {type.Name},运行时将 skip 后者 → 数据流断开",
                    });
                }
                return;
            }
            registry[portId] = (type, source);
        }

        // §D2.5.E2E "Output 没接 AutoScale" 警告 ——  PyComputeFeature / ComputeNodeFeature 等
        // 中间节点的 ROM<double> 输出如果只接到 LineSeries.DataPort 而不接 UniversalAutoScale.ValuePorts,
        // Y 范围只看其他 series,该指标在画布外被裁掉(典型坑见低代码.md §8 + 用户反馈)。
        private static void CheckOutputAutoScaleCoverage(ChartBlueprint blueprint, List<BlueprintDiagnostic> diagnostics)
        {
            // 1. 收集所有 UniversalAutoScale.ValuePorts 引用的 globalId 集合
            var autoScaleValueIds = new HashSet<string>(StringComparer.Ordinal);
            // 2. 收集所有"消费 ROM<double> 的 series 节点"(LineSeries / BarSeries 等)的 DataPort 引用 id
            var seriesConsumeIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var f in blueprint.Features)
            {
                Type? ft;
                try { ft = ComponentRegistry.Resolve(f.TypeName); } catch { continue; }
                if (ft == null) continue;

                bool isAutoScale = ft.Name == "UniversalAutoScaleFeature"
                                || ft.GetProperty("ValuePorts", BindingFlags.Public | BindingFlags.Instance) != null
                                && ft.Name.Contains("AutoScale", StringComparison.OrdinalIgnoreCase);

                bool isSeriesConsumer = ft.Name.EndsWith("LineSeriesFeature", StringComparison.OrdinalIgnoreCase)
                                     || ft.Name.EndsWith("BarSeriesFeature", StringComparison.OrdinalIgnoreCase);

                if (isAutoScale && f.InputBindings.TryGetValue("ValuePorts", out var vpBinding))
                {
                    foreach (var id in PortBindingValue.ExtractList(vpBinding))
                        if (!string.IsNullOrEmpty(id)) autoScaleValueIds.Add(id);
                }
                if (isSeriesConsumer && f.InputBindings.TryGetValue("DataPort", out var dpBinding))
                {
                    var id = PortBindingValue.ExtractSingle(dpBinding);
                    if (!string.IsNullOrEmpty(id)) seriesConsumeIds.Add(id);
                }
            }

            // 3. 遍历每个 Feature 的输出端口(ROM<double>),如果 binding 的 globalId 在 series 消费里
            //    但不在 AutoScale ValuePorts 里 → 警告
            foreach (var f in blueprint.Features)
            {
                Type? ft;
                try { ft = ComponentRegistry.Resolve(f.TypeName); } catch { continue; }
                if (ft == null) continue;

                foreach (var pi in ft.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!IsDataPortProperty(pi, out var valueType, out _)) continue;
                    if (PortMetadataRegistry.ResolveDirection(ft, pi) != PortDirection.Output) continue;
                    if (valueType != typeof(ReadOnlyMemory<double>)) continue;
                    if (!f.OutputBindings.TryGetValue(pi.Name, out var bindingValue)) continue;

                    var id = PortBindingValue.ExtractSingle(bindingValue);
                    if (string.IsNullOrEmpty(id)) continue;

                    if (seriesConsumeIds.Contains(id) && !autoScaleValueIds.Contains(id))
                    {
                        diagnostics.Add(new BlueprintDiagnostic
                        {
                            Severity = BlueprintDiagnosticSeverity.Warning,
                            Code = "BP_OUTPUT_NOT_SCALED",
                            FeatureTypeName = f.TypeName,
                            PortName = pi.Name,
                            Message = $"输出端口 '{id}' 已接到 LineSeries/BarSeries,但未接到任何 UniversalAutoScale.ValuePorts —— "
                                    + "Y 范围不会包含此指标输出,可能在画布外被裁掉。请把它也接进 ValuePorts(fan-in 数组,可加多源)。",
                        });
                    }
                }
            }
        }

        /// <summary>
        /// §D2.X 多输入指标:校验 InputOrder ↔ PortBindings nested keys 一致性。
        /// 1. Feature 类型上每个 Dictionary&lt;string, DataPort&lt;T&gt;&gt; 字段都扫一遍,
        /// 2. 收集 PortBindings 里 <c>{fieldName}.*</c> 形态的 child names,
        /// 3. 跟 Properties["InputOrder"] 声明的顺序对账,缺失项报 <c>BP_PYHANDLER_INPUT_UNCONNECTED</c>。
        /// 没有声明 InputOrder 的多输入 Feature 也单独警告(否则运行时会跳过装配)。
        /// </summary>
        private static void CheckMultiInputBindings(FeatureModel f, Type featureType, List<BlueprintDiagnostic> diagnostics)
        {
            // 找所有 Dictionary<string, DataPort<T>> 输入字段。Output 方向的 dict(如 PlotFeature.SeriesOutputs)
            // 不参与 multi-input 校验 —— 它走的是 fan-out 输出语义,跟 InputOrder 形参列表无关。
            var dictFields = featureType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(pi =>
                {
                    var pt = pi.PropertyType;
                    if (!pt.IsGenericType || pt.GetGenericTypeDefinition() != typeof(Dictionary<,>)) return false;
                    var args = pt.GetGenericArguments();
                    if (args[0] != typeof(string)) return false;
                    if (!args[1].IsGenericType || args[1].GetGenericTypeDefinition() != typeof(DataPort<>)) return false;
                    return PortMetadataRegistry.ResolveDirection(featureType, pi) != PortDirection.Output;
                }).ToArray();
            if (dictFields.Length == 0) return;

            // 收集 InputBindings 里的 nested keys(多输入),按 parent 分组
            var nestedByParent = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in f.InputBindings)
            {
                int dot = b.Key.IndexOf('.');
                if (dot <= 0) continue;
                var parent = b.Key.Substring(0, dot);
                var child = b.Key.Substring(dot + 1);
                if (!nestedByParent.TryGetValue(parent, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    nestedByParent[parent] = set;
                }
                set.Add(child);
            }

            // 拿 InputOrder 字符串数组(可能是 string[] / List<string> / JsonElement Array)
            var inputOrder = ExtractInputOrderProp(f);

            foreach (var pi in dictFields)
            {
                bool hasNested = nestedByParent.TryGetValue(pi.Name, out var boundChildren);
                if (!hasNested) continue;   // 该字段没接 → 走单输入兼容路径,无需校验

                if (inputOrder == null || inputOrder.Count == 0)
                {
                    diagnostics.Add(new BlueprintDiagnostic
                    {
                        Severity = BlueprintDiagnosticSeverity.Warning,
                        Code = "BP_PYHANDLER_INPUT_ORDER_MISSING",
                        FeatureTypeName = f.TypeName,
                        PortName = pi.Name,
                        Message = $"PortBindings 含 nested key '{pi.Name}.*' 但 Properties.InputOrder 未声明 — "
                                + "运行时无法确定调 Compute 委托的形参顺序,装配会跳过。",
                    });
                    continue;
                }

                // 对账:InputOrder 里每个 name 必须在 boundChildren 里出现
                foreach (var name in inputOrder)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    if (boundChildren!.Contains(name)) continue;
                    diagnostics.Add(new BlueprintDiagnostic
                    {
                        Severity = BlueprintDiagnosticSeverity.Warning,
                        Code = "BP_PYHANDLER_INPUT_UNCONNECTED",
                        FeatureTypeName = f.TypeName,
                        PortName = $"{pi.Name}.{name}",
                        Message = $"InputOrder 声明 '{name}' 但 PortBindings 缺 '{pi.Name}.{name}' 焊接 — "
                                + "运行时该输入端口为 null,装配会跳过当前节点。",
                    });
                }
            }
        }

        /// <summary>
        /// §D2.6.4 单 Feature 检查:IncrementalComputeFeature(及派生类)的 handler 类型必须是 ValueTuple 返回。
        /// 用 IsAssignableFrom 而非字符串名匹配 —— 派生子类自动受保护,不怕重命名 / 跨 assembly 同名。
        /// </summary>
        private static void CheckIncrementalCompute(
            FeatureModel f, Type featureType, BlueprintHandlerRegistry? handlers, List<BlueprintDiagnostic> diagnostics)
        {
            if (!typeof(Hevo.Charting.Features.IncrementalComputeFeature).IsAssignableFrom(featureType)) return;

            // BP_PYHANDLER_INCREMENTAL_NOT_STATEFUL:Compute 属性引用的 handler 必须是 ValueTuple<,> 返回的 Func。
            // 强类型 StatefulCompute 字段(C# 业务侧路径)蓝图 JSON 表达不出来,这里只校验 Compute(string handler 名)路径。
            if (f.Properties.TryGetValue("Compute", out var rawComputeName) &&
                rawComputeName is string handlerName && !string.IsNullOrEmpty(handlerName) &&
                handlers != null)
            {
                var delType = handlers.GetDelegateType(handlerName);
                if (delType != null)
                {
                    var invokeMethod = delType.GetMethod("Invoke");
                    var retType = invokeMethod?.ReturnType;
                    bool returnsValueTuple = retType != null && retType.IsGenericType
                        && IsValueTupleDef(retType.GetGenericTypeDefinition());
                    if (!returnsValueTuple)
                    {
                        diagnostics.Add(new BlueprintDiagnostic
                        {
                            Severity = BlueprintDiagnosticSeverity.Warning,
                            Code = "BP_PYHANDLER_INCREMENTAL_NOT_STATEFUL",
                            FeatureTypeName = f.TypeName,
                            PortName = "Compute",
                            Message = $"IncrementalComputeFeature.Compute 引用的 handler '{handlerName}' 返回 {retType?.Name ?? "?"} 而非 ValueTuple<,> — "
                                    + "增量协议要求 handler 签名 (input, prev_state) -> (output, next_state),运行时该 feature 装配会跳过。",
                        });
                    }
                }
            }
        }

        // 跟 NodeFactory.IsScalarType / DynamicChartSchema.IsScalarType 同款 —— 反射 DS / TItem 字段时
        // 区分"可投影端口"(基本类型 / DateTime / string / Nullable<T>),非标量字段不进 PortBindings 校验。
        private static bool IsScalarTypeForDryRun(Type t)
        {
            if (t == typeof(string)) return true;
            if (t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan)) return true;
            if (t.IsPrimitive) return true;
            if (t == typeof(decimal)) return true;
            if (Nullable.GetUnderlyingType(t) is { } u) return IsScalarTypeForDryRun(u);
            return false;
        }

        private static bool IsValueTupleDef(Type genericTypeDef)
            => genericTypeDef == typeof(ValueTuple<,>) ||
               genericTypeDef == typeof(ValueTuple<,,>) ||
               genericTypeDef == typeof(ValueTuple<,,,>) ||
               genericTypeDef == typeof(ValueTuple<,,,,>) ||
               genericTypeDef == typeof(ValueTuple<,,,,,>) ||
               genericTypeDef == typeof(ValueTuple<,,,,,,>);

        // PlotFeature 统一 series 协议校验 —— 遍历 Series 数组,按每个 PlotSeriesSpec.Kind 校验:
        //   - Kind 在 line/bar/scatter/arrow_markers 之外 → BP_PLOT_KIND_UNKNOWN(error)
        //   - arrow_markers spec 配 Domain → BP_PLOT_DOMAIN_MISMATCH(warning,arrow 永远 inherit,domain 被忽略)
        // 仅看 Properties 描述本身的形态闭合,运行时再细致 fail-fast。
        private static void CheckPlotSeries(FeatureModel f, Type featureType, List<BlueprintDiagnostic> diagnostics)
        {
            if (!string.Equals(featureType.Name, "PlotFeature", StringComparison.Ordinal)) return;
            if (!f.Properties.TryGetValue("Series", out var rawSeries) || rawSeries == null) return;

            // 容错 Series 字段形态(蓝图反射加载可能拿到 PlotSeriesSpec[] / IReadOnlyList<PlotSeriesSpec> / object[])。
            IEnumerable<object?>? seriesEnum = rawSeries switch
            {
                System.Collections.IEnumerable e and not string => e.Cast<object?>(),
                _ => null,
            };
            if (seriesEnum == null) return;

            int idx = 0;
            foreach (var item in seriesEnum)
            {
                if (item == null) { idx++; continue; }
                var t = item.GetType();
                string? kind = t.GetProperty("Kind")?.GetValue(item) as string;

                if (!string.IsNullOrWhiteSpace(kind))
                {
                    var k = kind.Trim().ToLowerInvariant();
                    if (k != "line" && k != "bar" && k != "scatter" && k != "arrow_markers")
                    {
                        diagnostics.Add(new BlueprintDiagnostic
                        {
                            Severity = BlueprintDiagnosticSeverity.Error,
                            Code = "BP_PLOT_KIND_UNKNOWN",
                            FeatureTypeName = f.TypeName,
                            PortName = $"Series[{idx}].Kind",
                            Message = $"Series[{idx}].Kind '{kind}' 不支持(line / bar / scatter / arrow_markers)",
                        });
                    }
                    else if (k == "arrow_markers")
                    {
                        // arrow_markers 永远 inherit;XDomain/YDomain 配置被忽略,提醒一下。
                        var xDomain = t.GetProperty("XDomain")?.GetValue(item);
                        var yDomain = t.GetProperty("YDomain")?.GetValue(item);
                        if (xDomain != null || yDomain != null)
                        {
                            diagnostics.Add(new BlueprintDiagnostic
                            {
                                Severity = BlueprintDiagnosticSeverity.Warning,
                                Code = "BP_PLOT_DOMAIN_MISMATCH",
                                FeatureTypeName = f.TypeName,
                                PortName = $"Series[{idx}].XDomain/YDomain",
                                Message = "arrow_markers 永远 inherit 主图 axes,XDomain/YDomain 配置会被忽略。",
                            });
                        }
                    }
                }
                idx++;
            }
        }

        /// <summary>从 FeatureModel.Properties["InputOrder"] 里抓字符串数组,容错多种形态。</summary>
        private static IReadOnlyList<string>? ExtractInputOrderProp(FeatureModel f)
        {
            if (!f.Properties.TryGetValue("InputOrder", out var raw) || raw == null) return null;
            if (raw is string[] arr) return arr;
            if (raw is IReadOnlyList<string> rls) return rls;
            if (raw is IEnumerable<string> seq) return seq.ToArray();
            if (raw is IEnumerable<object?> oseq) return oseq.Select(o => o?.ToString() ?? string.Empty).ToArray();
            if (raw is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var el in je.EnumerateArray())
                {
                    if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                        list.Add(el.GetString() ?? string.Empty);
                }
                return list;
            }
            return null;
        }

        private static string? LaunchInternal(
            ChartBlueprint blueprint,
            Window? owner,
            object? dataSourceContext,
            Action<object>? configureDataSource,
            BlueprintHandlerRegistry? handlers)
        {
            if (blueprint.DataSources.Count == 0)
                return "蓝图缺少 DataSource 节点。请先在画布上添加一个数据源。";

            // Render-leaf = 无 incoming Cascade 的 DS。BlueprintLauncher 的 dataSourceContext / configureDataSource
            // 两个回调只作用在 leaf 上(预览用例就只关心"渲染那个");其余 DS 由 framework 自动实例化 + DefaultContext。
            var upstreamIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in blueprint.Cascades)
                if (!string.IsNullOrEmpty(c.FromDataSourceId)) upstreamIds.Add(c.FromDataSourceId);
            var leafCandidates = blueprint.DataSources.Where(d => !upstreamIds.Contains(d.Id)).ToList();
            if (leafCandidates.Count == 0)
                return "Render-leaf 推导失败:DataSources 全是 Cascade 上游(无人渲染)。";
            if (leafCandidates.Count > 1)
                return $"Render-leaf 推导失败:多 leaf({string.Join(", ", leafCandidates.Select(l => l.Id))}) —— Parallel 暂不支持。";

            var leafDsModel = leafCandidates[0];
            if (string.IsNullOrEmpty(leafDsModel.TypeName)) return $"DataSource '{leafDsModel.Id}' TypeName 为空。";
            if (string.IsNullOrEmpty(leafDsModel.Id)) return "DataSource Id 为空(节点化协议必填)。";

            // 1. 解析 leaf DataSource 类型(framework 后面装配时也会再走一次,但 BlueprintLauncher 这里要预先实例化 leaf,
            //    给 dataSourceContext + configureDataSource 回调用 —— 完了再塞 instances 字典给 BlueprintRunner)。
            Type leafDsType;
            try { leafDsType = ComponentRegistry.Resolve(leafDsModel.TypeName); }
            catch (Exception ex) { return $"DataSource 类型未登记:{ex.Message}"; }

            if (leafDsType.GetConstructor(Type.EmptyTypes) == null)
                return $"{leafDsType.Name} 没有公共无参构造函数,低代码场景无法实例化。"
                     + "请用业务侧 BlueprintRunner.Run(cell, blueprint, instances) 自行装配。";

            if (!BlueprintDataSourceProbe.IsValidDataSourceType(leafDsType, out var probeError))
                return probeError!;

            // 2. 实例化 leaf DS (走 ComponentRegistry 编译委托缓存,首次反射后续直接 newobj)
            object leafDsInstance;
            try { leafDsInstance = ComponentRegistry.CreateInstance(leafDsType); }
            catch (Exception ex) { return $"实例化 {leafDsType.Name} 失败:{ex.Message}"; }

            // 2.1 类型化上下文参数 (优先) — 反射约定:找参数类型可吃 dataSourceContext 的 LoadAsync 方法。
            //   ReactiveDataSource<TSource, TContext, TItem>.LoadAsync(TContext) / LoadAsync(TContext, CancellationToken)
            //   两种 overload 都能命中,默认 CancellationToken.None。
            if (dataSourceContext != null)
            {
                var ctxType = dataSourceContext.GetType();
                var loadMethod = leafDsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "LoadAsync" && m.GetParameters().Length >= 1)
                    .FirstOrDefault(m => m.GetParameters()[0].ParameterType.IsAssignableFrom(ctxType));
                if (loadMethod != null)
                {
                    var ps = loadMethod.GetParameters();
                    var args = new object?[ps.Length];
                    args[0] = dataSourceContext;
                    for (int i = 1; i < ps.Length; i++)
                    {
                        args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                                  : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                    }
                    try
                    {
                        // ⚠️ 关键:必须等 LoadAsync 完成才 BindTo,否则 fire-and-forget 路径有 race ——
                        // LoadAsync 走 _requestBus 异步管线,BindTo 之前若 OnFetchAsync 还没跑完,
                        // GetSnapshot() 返回空快照(version=default=v0)。ContextIngestor / ScatterIngestor
                        // 版本去重跟初始 _lastVersion=v0 撞车 → 全部 skip,LogicalLength=0,黑屏。
                        //
                        // 直接 task.Wait() 在 UI 线程上会 sync-over-async 死锁(SwitchContextAsync 内部
                        // `await tcs.Task` 默认捕获 sync context,resume 回 UI 线程,而 UI 线程被 Wait 卡)。
                        // 解法:用 Task.Run 把 Wait 推到 ThreadPool,UI 线程在 ThreadPool Task 上 Wait,
                        // 内部 await 的 continuation 调度回 UI 时不需要拿锁(Task.Run 的 awaiter 在
                        // ThreadPool 上 resume),无死锁。
                        var loadResult = loadMethod.Invoke(leafDsInstance, args);
                        if (loadResult is System.Threading.Tasks.Task task)
                        {
                            try
                            {
                                System.Threading.Tasks.Task.Run(() => task).Wait(System.TimeSpan.FromSeconds(5));
                            }
                            catch (AggregateException ae)
                            {
                                return $"调用 {leafDsType.Name}.LoadAsync 抛异常:{ae.GetBaseException().Message}";
                            }
                            if (!task.IsCompleted)
                            {
                                Console.WriteLine($"[Hevo 蓝图警告] {leafDsType.Name}.LoadAsync 5 秒未完成,fall through 让 BindTo 走异步路径(可能首帧黑屏)。");
                            }
                        }
                    }
                    catch (Exception ex) { return $"调用 {leafDsType.Name}.LoadAsync 失败:{ex.Message}"; }
                }
                // 没找到匹配的 LoadAsync 不算错 —— 业务可能想用 configureDataSource 走别的路径。
            }

            // 2.2 万能回调,业务自由发挥。
            try { configureDataSource?.Invoke(leafDsInstance); }
            catch (Exception ex) { return $"configureDataSource 抛异常:{ex.Message}"; }

            // 3. 拉起预览窗口
            var cell = new ChartCell { Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1B, 0x1F)) };
            var win = new Window
            {
                Title = $"蓝图预览 — {leafDsType.Name}",
                Width = 1024, Height = 600,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = cell,
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x1A)),
            };

            // 4. 装配 schema 到 cell。leaf 实例显式塞 instances;其它 DS(Cascade 上游 stocklist 等)
            //    交给 BlueprintRunner 自动 ComponentRegistry.Resolve + new + InjectProperties。
            try
            {
                var instances = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [leafDsModel.Id] = leafDsInstance,
                };
                BlueprintRunner.Run(cell, blueprint, instances, handlers);
            }
            catch (Exception ex) { return $"RunBlueprint 抛异常:{(ex.InnerException ?? ex).Message}"; }

            // 8. 窗口关闭时回收 ChartCell 状态
            win.Closed += (_, __) => cell.Shutdown();
            win.Show();
            return null;
        }
    }
}
