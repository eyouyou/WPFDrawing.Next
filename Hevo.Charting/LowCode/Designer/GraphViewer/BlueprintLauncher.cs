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

            if (blueprint.DataSource == null || string.IsNullOrEmpty(blueprint.DataSource.TypeName))
                return new BlueprintLaunchResult { Error = "蓝图缺少 DataSource 节点。请先在画布上添加一个数据源。" };

            Type dsType;
            try { dsType = ComponentRegistry.Resolve(blueprint.DataSource.TypeName); }
            catch (Exception ex) { return new BlueprintLaunchResult { Error = $"DataSource 类型未登记:{ex.Message}" }; }

            if (dsType.GetConstructor(Type.EmptyTypes) == null)
                return new BlueprintLaunchResult { Error = $"{dsType.Name} 没有公共无参构造函数,低代码场景无法实例化。" };

            if (NodeFactory.FindDataSourceItemType(dsType) == null)
                return new BlueprintLaunchResult { Error = $"{dsType.Name} 不是 DataSource<TSource, TItem> 的派生类型。" };

            if (dsType.GetProperty("Stream", BindingFlags.Public | BindingFlags.Instance) == null)
                return new BlueprintLaunchResult { Error = $"{dsType.Name}.Stream 属性不存在,请确认其继承自 DataSource<,>。" };

            // 端口类型登记表 —— 模拟运行时 GetOrCreatePort 的"先注册先赢"语义,
            // 第二次以不同类型注册同 portId 即冲突。值里带 source 串方便诊断回溯。
            var portTypes = new Dictionary<string, (Type Type, string Source)>(StringComparer.Ordinal);

            // ViewportPorts 三根 well-known id —— DynamicChartSchema ctor 预登记的常驻端口。
            portTypes["VP_LogicalLength"] = (typeof(int),       "Viewport.LogicalLength");
            portTypes["VP_UserRange"]     = (typeof(RealRange), "Viewport.UserRange");
            portTypes["VP_ActiveRange"]   = (typeof(RealRange), "Viewport.ActiveRange");

            // DataSource.ScalarMappings → portId 类型 = DataSource 上对应属性类型
            foreach (var kv in blueprint.DataSource.ScalarMappings)
            {
                var pi = dsType.GetProperty(kv.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (pi == null) continue;
                RegisterPortType(portTypes, kv.Value, pi.PropertyType, $"{dsType.Name}.{kv.Key}", diagnostics);
            }

            // DataSource.VectorMappings → portId 类型 = ReadOnlyMemory<TItem.字段类型>
            var itemType = NodeFactory.FindDataSourceItemType(dsType);
            if (itemType != null)
            {
                foreach (var kv in blueprint.DataSource.VectorMappings)
                {
                    var pi = itemType.GetProperty(kv.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (pi == null) continue;
                    var memType = typeof(ReadOnlyMemory<>).MakeGenericType(pi.PropertyType);
                    RegisterPortType(portTypes, kv.Value, memType, $"{itemType.Name}.{kv.Key}", diagnostics);
                }
            }

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
                foreach (var pi in featureType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!IsDataPortProperty(pi, out var portValueType, out _)) continue;
                    if (PortMetadataRegistry.ResolveDirection(featureType, pi) != PortDirection.Output) continue;
                    if (!f.PortBindings.ContainsKey(pi.Name))
                    {
                        diagnostics.Add(new BlueprintDiagnostic
                        {
                            Severity = BlueprintDiagnosticSeverity.Warning,
                            Code = "BP_OUTPUT_PORT_UNBOUND",
                            FeatureTypeName = f.TypeName,
                            PortName = pi.Name,
                            Message = "Output 端口未在 PortBindings 中焊接 → 运行时下游 Watch 拿到 null 触发 NRE 静默吞 → 黑屏",
                        });
                    }
                }

                // 类型一致性 —— 用 binding 标注的 globalId,反向校验跟已注册端口类型是否吻合
                foreach (var b in f.PortBindings)
                {
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

        private static string? LaunchInternal(
            ChartBlueprint blueprint,
            Window? owner,
            object? dataSourceContext,
            Action<object>? configureDataSource,
            BlueprintHandlerRegistry? handlers)
        {
            if (blueprint.DataSource == null || string.IsNullOrEmpty(blueprint.DataSource.TypeName))
                return "蓝图缺少 DataSource 节点。请先在画布上添加一个数据源。";

            // 1. 解析 DataSource 类型
            Type dsType;
            try { dsType = ComponentRegistry.Resolve(blueprint.DataSource.TypeName); }
            catch (Exception ex) { return $"DataSource 类型未登记:{ex.Message}"; }

            // 2. 必须有 public 无参 ctor (低代码运行时唯一可走的实例化路径)
            if (dsType.GetConstructor(Type.EmptyTypes) == null)
                return $"{dsType.Name} 没有公共无参构造函数,低代码场景无法实例化。"
                     + "请用业务侧 BlueprintRunner.Run<...>(cell, blueprint, ds) 自行装配。";

            // 3. 找出 TItem 类型 (走 DataSource<TSource,TItem> 基类链)
            var itemType = NodeFactory.FindDataSourceItemType(dsType);
            if (itemType == null)
                return $"{dsType.Name} 不是 DataSource<TSource, TItem> 的派生类型。";

            // 4. 实例化数据源 (走 ComponentRegistry 的编译委托缓存,首次反射后续直接 newobj)
            object dsInstance;
            try { dsInstance = ComponentRegistry.CreateInstance(dsType); }
            catch (Exception ex) { return $"实例化 {dsType.Name} 失败:{ex.Message}"; }

            // 4.1 类型化上下文参数 (优先) — 反射约定:找参数类型可吃 dataSourceContext 的 LoadAsync 方法。
            //   ReactiveDataSource<TSource, TContext, TItem>.LoadAsync(TContext) / LoadAsync(TContext, CancellationToken)
            //   两种 overload 都能命中,默认 CancellationToken.None。
            if (dataSourceContext != null)
            {
                var ctxType = dataSourceContext.GetType();
                var loadMethod = dsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
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
                    try { _ = loadMethod.Invoke(dsInstance, args); }    // fire & forget
                    catch (Exception ex) { return $"调用 {dsType.Name}.LoadAsync 失败:{ex.Message}"; }
                }
                // 没找到匹配的 LoadAsync 不算错 —— 业务可能想用 configureDataSource 走别的路径。
            }

            // 4.2 万能回调,业务自由发挥。
            try { configureDataSource?.Invoke(dsInstance); }
            catch (Exception ex) { return $"configureDataSource 抛异常:{ex.Message}"; }

            // 5. 取 ds.Stream(IWorkflow<DataSnapshot<TItem>>)
            var streamProp = dsType.GetProperty("Stream", BindingFlags.Public | BindingFlags.Instance);
            object? stream = streamProp?.GetValue(dsInstance);
            if (stream == null)
                return $"{dsType.Name}.Stream 取不到值。请确认其继承自 DataSource<,>。";

            // 6. 拉起预览窗口
            var cell = new ChartCell { Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1B, 0x1F)) };
            var win = new Window
            {
                Title = $"蓝图预览 — {dsType.Name}",
                Width = 1024, Height = 600,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = cell,
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x1A)),
            };

            // 7. 反射调 BlueprintRunner.Run<TItem>(cell, blueprint, dsInstance, stream, handlers)
            //    handlers 可选,蓝图无 Triggers / 无 Delegate 字段时为 null。
            try
            {
                // 选 5 参数版 Run<TItem>(ChartCell, ChartBlueprint, object, IWorkflow<>, BlueprintHandlerRegistry?)
                var runMethod = typeof(BlueprintRunner).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "Run" && m.IsGenericMethod && m.GetGenericArguments().Length == 1
                                && m.GetParameters().Length == 5)
                    .FirstOrDefault();
                if (runMethod == null) return "找不到 BlueprintRunner.Run<TItem>(...) 方法,反射调度失败。";
                var generic = runMethod.MakeGenericMethod(itemType);
                generic.Invoke(null, new object?[] { cell, blueprint, dsInstance, stream, handlers });
            }
            catch (TargetInvocationException ex) { return $"BlueprintRunner.Run 抛异常:{ex.InnerException?.Message ?? ex.Message}"; }
            catch (Exception ex) { return $"BlueprintRunner.Run 抛异常:{ex.Message}"; }

            // 8. 窗口关闭时回收 ChartCell 状态
            win.Closed += (_, __) => cell.Shutdown();
            win.Show();
            return null;
        }
    }
}
