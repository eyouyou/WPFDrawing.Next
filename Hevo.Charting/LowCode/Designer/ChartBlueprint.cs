using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using System.Reflection;

namespace Hevo.Charting.LowCode.Designer
{
    public class ChartBlueprint
    {
        public DataSourceModel? DataSource { get; set; }

        // 💥 淘汰 Layer 和 Sink！现在只有 Feature！
        public List<FeatureModel> Features { get; set; } = new();

        public List<StyleModel> InitialTraits { get; set; } = new();
    }

    public class DataSourceModel
    {
        public string TypeName { get; set; } = string.Empty;

        // 低代码配置：把数据源里的哪些字段，切片/映射到全局哪些引脚 ID 上
        public Dictionary<string, string> ScalarMappings { get; set; } = new();
        public Dictionary<string, string> VectorMappings { get; set; } = new();

        /// <summary>
        /// 数据源的 init/setter 属性配置(典型:<c>MinuteCeiling</c> / 是否预热缓存等)。
        /// 跟 Feature 的 Properties 同语义,启动时由 SmartActivator.InjectProperties 注入到实例。
        /// 注意:<b>"运行时上下文"(典型 LoadAsync(Security))描述不出来</b>,
        /// 那种走 BlueprintLauncher.Launch 的 dataSourceContext 参数。
        /// </summary>
        public Dictionary<string, object?> Properties { get; set; } = new();
    }

    public class StyleModel
    {
        public string TraitTypeName { get; set; } = string.Empty;

        /// <summary>
        /// 💥 预设引用：指向目标 Trait 类型上的 public static 字段或属性 (如 ScaleStrategyTrait.Default)。
        /// 用途：record 类型 / 含位置参数的 trait 没法走无参 ctor + InjectProperties，
        /// 走预设字段直接引用静态实例,JSON 里只配名字,无须知道内部构造细节。
        /// 同时设置 Properties 时,先取预设实例再注入属性 (覆盖部分字段)。
        /// </summary>
        public string? Preset { get; set; }

        public Dictionary<string, object?> Properties { get; set; } = new();
    }

    public class FeatureModel
    {
        public string TypeName { get; set; } = string.Empty;

        // 1. 普通属性配置 (如 LineColor, Period 等)
        public Dictionary<string, object?> Properties { get; set; } = new();

        // 2. 💥 引脚连线板：Key=Feature的属性名(如 "PricePort"), Value=全局引脚ID(如 "GlobalPrice")
        public Dictionary<string, string> PortBindings { get; set; } = new();
    }

    /// <summary>
    /// 💥 动态响应式图表骨架 (由 JSON 蓝图在运行时动态孵化)
    /// 三件事:DefineDataFlow 反射拼 ContextIngestor / ScatterIngestor;
    /// DefineFeatures 反射创建 Feature + 焊接 Port;全局引脚注册表跨 Feature 共享 DataPort 实例。
    /// </summary>
    public class DynamicChartSchema<TItem> : ReactiveSchema
    {
        private readonly ChartBlueprint _blueprint;
        private readonly object _dataSourceInstance;
        private readonly IWorkflow<DataSnapshot<TItem>> _sourceStream;

        // 💥 全局引脚注册表：按 ID 缓存实例化的 DataPort<T>
        private readonly Dictionary<string, object> _portRegistry = new();

        // IFeatureContext.Seed<T> 的反射句柄 —— 详见 DefineFeatures 里 Seed 段的注释。
        private static readonly MethodInfo _seedMethod =
            typeof(IFeatureContext).GetMethod(nameof(IFeatureContext.Seed))
            ?? throw new InvalidOperationException("IFeatureContext.Seed 方法签名变了,需同步更新蓝图反射调用。");

        public DynamicChartSchema(
            ChartBlueprint blueprint,
            object dataSourceInstance,
            IWorkflow<DataSnapshot<TItem>> sourceStream)
        {
            _blueprint = blueprint ?? throw new ArgumentNullException(nameof(blueprint));
            _dataSourceInstance = dataSourceInstance ?? throw new ArgumentNullException(nameof(dataSourceInstance));
            _sourceStream = sourceStream ?? throw new ArgumentNullException(nameof(sourceStream));

            // 💥 数据源生命周期托管：IDisposable / IPausable 都跟着 schema 一起销毁/暂停。
            // 等价于业务侧 `_dataSource.OwnedBy(this)`,这里替蓝图用户隐式补上。
            if (_dataSourceInstance is IDisposable disposable)
            {
                RegisterDisposable(disposable);
            }

            // 💥 数据源 init/setter 属性注入(蓝图侧的 DataSource.Properties 字典)。
            // 走跟 Feature 一样的 SmartActivator.InjectProperties 路径,业务在 GraphViewer
            // 编辑器双击 DataSource 节点改的字段(如 MinuteCeiling)就此生效。
            if (_blueprint.DataSource?.Properties is { Count: > 0 } props)
            {
                SmartActivator.InjectProperties(_dataSourceInstance, props);
            }

            // 💥 预登记 schema 顶层 ViewportPorts 实例 ——
            //    GraphViewer 上的 "Viewport" 节点把端口指向这些 well-known id,
            //    任何 ScalarMapping / PortBindings 引用这些 id 时直接拿到 schema 实际持有的
            //    DataPort 实例,不创建新的副本(否则 ViewportManager 看到的端口和数据源写入的
            //    端口分裂成两个,length 永远到不了 schema → 黑屏)。
            _portRegistry["VP_LogicalLength"] = this.Viewport.LogicalLength;
            _portRegistry["VP_UserRange"]     = this.Viewport.UserRange;
            _portRegistry["VP_ActiveRange"]   = this.Viewport.ActiveRange;
        }

        /// <summary>
        /// 💥 核心魔法:反射获取或创建强类型 DataPort&lt;T&gt;。
        /// <para>
        /// 类型严格化:同一个 portId 已经被以另一个 T 注册过,返回 null 让调用方决定 skip。
        /// 这种碰撞通常来自蓝图侧错连(如 Time:DateTime 接到 XAxisDataPort:double),
        /// 此前会导致 ctor 类型不符抛 ArgumentException 把整张图带崩。
        /// </para>
        /// </summary>
        private object? GetOrCreatePort(Type portGenericType, string portId)
        {
            var expectedPortType = typeof(DataPort<>).MakeGenericType(portGenericType);
            if (_portRegistry.TryGetValue(portId, out var existing))
            {
                // 已注册,验证类型匹配。不匹配 → 返回 null,调用方 warn + skip。
                if (expectedPortType.IsInstanceOfType(existing)) return existing;

                Console.WriteLine(
                    $"[Hevo 蓝图警告] 端口 '{portId}' 已注册为 {existing.GetType().Name}," +
                    $"无法以 {expectedPortType.Name} 重连,跳过。" +
                    $"通常是蓝图错连(类型不匹配的两端被拉到一根线上),检查 Port.DataTypeName。");
                return null;
            }
            // 首次注册
            var port = Activator.CreateInstance(expectedPortType, portId)!;
            _portRegistry[portId] = port;
            return port;
        }

        // ==========================================
        // 1. 动态编译数据流摄入管线
        // ==========================================
        protected override void DefineDataFlow(ChartCell chart)
        {
            // 在低代码/反射场景下，我们绕过 Fluent DSL，直接组装 UniversalDataPipe
            var pipe = new UniversalDataPipe<TItem>();
            var dsType = _dataSourceInstance.GetType();

            // 💥 蓝图编译期透明优化代理：若数据源类型在 ComponentMetadataRegistry 里登记了
            // 自定义管线策略 (如 TimeShare 的预分配数组占位),整支管线交给策略接管,
            // 蓝图层零感知性能细节。
            var customPolicy = ComponentMetadataRegistry.GetPipelinePolicy(dsType);
            if (customPolicy != null)
            {
                // IPipelinePolicy 期望 Func<Type,string,object> 非空返回;蓝图自定义策略对类型有完整把控,
                // 类型冲突时直接 throw 暴露 bug 比 silent skip 合适。
                customPolicy.Compile(_blueprint, _dataSourceInstance, pipe,
                    (t, id) => GetOrCreatePort(t, id)
                        ?? throw new InvalidOperationException(
                            $"端口 '{id}' 类型 {t.Name} 与已注册类型冲突,业务自定义 pipeline policy 不允许这种碰撞。"));
            }
            else if (_blueprint.DataSource != null)
            {
                CompileScalarMappings(_blueprint.DataSource, pipe, dsType);
                CompileVectorMappings(_blueprint.DataSource, pipe, dsType);
                // 💥 LogicalLength 桥不再隐式装配 ——
                //    业务侧通过 ViewportLengthFeedFeature 节点显式连线 (DataSource.LogicalLength →
                //    feed.LogicalLengthPort),把"传统 .ProjectExtent(Viewport) DSL"上提到蓝图层,
                //    画布上看得见、可改可断。
            }

            // 💥 将组装好的反射管道与流绑定到图表生命周期。
            // - StartWith(GetSnapshot):首帧立即下发当前数据源快照,UI 不必等下一次 Push。
            //   反射 GetSnapshot,因为 _dataSourceInstance 类型对编译器是 object,无法直接调用泛型方法。
            // - DoOnDispose:管线随 schema 关闭时同步销毁 pipe,释放 ArrayPool 占用。
            DataSnapshot<TItem>? initialSnapshot = TryGetInitialSnapshot();
            var stream = initialSnapshot.HasValue
                ? _sourceStream.StartWith(initialSnapshot.Value)
                : _sourceStream;

            stream.Select(snap => pipe.Process(snap))
                  .DoOnDispose(pipe.Dispose)
                  .BindTo(chart);
        }

        /// <summary>
        /// 💥 标量映射:每个 ScalarMapping 反射出一个 ContextIngestor&lt;TItem,TSource,TValue&gt;,
        /// 把数据源属性值塞进对应全局引脚。Selector 用 Expression 编译,杜绝运行时反射开销。
        /// </summary>
        private void CompileScalarMappings(DataSourceModel ds, UniversalDataPipe<TItem> pipe, Type dsType)
        {
            foreach (var kvp in ds.ScalarMappings)
            {
                string propName = kvp.Key;   // DataSource 的属性名
                string portId = kvp.Value;   // 全局引脚 ID

                var propInfo = dsType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (propInfo == null)
                {
                    Console.WriteLine($"[Hevo 蓝图警告] 数据源 {dsType.Name} 找不到标量属性 {propName},已跳过");
                    continue;
                }

                var portInstance = GetOrCreatePort(propInfo.PropertyType, portId);
                if (portInstance == null) continue;   // 端口类型冲突,GetOrCreatePort 已警告

                var ingestorType = typeof(ContextIngestor<,,>).MakeGenericType(typeof(TItem), dsType, propInfo.PropertyType);

                // 动态生成 selector 委托: (ds) => ds.Property
                var param = System.Linq.Expressions.Expression.Parameter(dsType, "ds");
                var getter = System.Linq.Expressions.Expression.Property(param, propInfo);
                var selectorType = typeof(Func<,>).MakeGenericType(dsType, propInfo.PropertyType);
                var selector = System.Linq.Expressions.Expression.Lambda(selectorType, getter, param).Compile();

                var ingestor = Activator.CreateInstance(ingestorType, portInstance, _dataSourceInstance, selector)!;
                pipe.AddIngestor((IDataIngestor<TItem>)ingestor);
            }
        }

        /// <summary>
        /// 💥 向量映射:每个 VectorMapping 反射出一个 ScatterIngestor&lt;TItem,TValue&gt;,
        /// 把 TItem 上某属性按行批量打包成 ReadOnlyMemory&lt;T&gt; 写到对应引脚。
        /// 引脚类型固定为 DataPort&lt;ReadOnlyMemory&lt;TValue&gt;&gt;,与 PortGenerator 生成的列流引脚同口径。
        /// </summary>
        private void CompileVectorMappings(DataSourceModel ds, UniversalDataPipe<TItem> pipe, Type dsType)
        {
            if (ds.VectorMappings.Count == 0) return;

            // 长度提供器:优先取数据源 LogicalLength (来自 DataSource<TSource,TItem> 基类),
            // 缺席时回退到 snapshot.Count (此分支只能在 Process 时拿到,不能预先固化为 Func<int>)。
            // 实际上凡是走低代码的数据源都继承 DataSource<,>,故 LogicalLength 几乎必有。
            var lengthProp = dsType.GetProperty("LogicalLength", BindingFlags.Public | BindingFlags.Instance);
            Func<int>? lengthProvider = null;
            if (lengthProp != null && lengthProp.PropertyType == typeof(int))
            {
                var dsRef = _dataSourceInstance;
                lengthProvider = () => (int)lengthProp.GetValue(dsRef)!;
            }

            var itemType = typeof(TItem);

            foreach (var kvp in ds.VectorMappings)
            {
                string propName = kvp.Key;
                string portId = kvp.Value;

                var propInfo = itemType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (propInfo == null)
                {
                    Console.WriteLine($"[Hevo 蓝图警告] 数据项 {itemType.Name} 找不到向量字段 {propName},已跳过");
                    continue;
                }

                Type valueType = propInfo.PropertyType;

                // 引脚类型: DataPort<ReadOnlyMemory<TValue>>
                Type memoryType = typeof(ReadOnlyMemory<>).MakeGenericType(valueType);
                var portInstance = GetOrCreatePort(memoryType, portId);
                if (portInstance == null) continue;   // 端口类型冲突 (典型:Time:DateTime 错连到 *:double),已警告,放弃这根

                // selector: (TItem) => item.PropertyName
                var param = System.Linq.Expressions.Expression.Parameter(itemType, "it");
                var getter = System.Linq.Expressions.Expression.Property(param, propInfo);
                var selectorType = typeof(Func<,>).MakeGenericType(itemType, valueType);
                var valueSelector = System.Linq.Expressions.Expression.Lambda(selectorType, getter, param).Compile();

                // ScatterIngestor<TItem, TValue> 需要的长度提供器:无法解析时给 0,Process 内部会短路。
                Func<int> resolvedLength = lengthProvider ?? (() => 0);

                var ingestorType = typeof(ScatterIngestor<,>).MakeGenericType(itemType, valueType);
                // ctor 签名:(DataPort, lengthProvider, defaultValue, indexSelector, valueSelector)
                object? defaultValue = valueType.IsValueType ? Activator.CreateInstance(valueType) : null;

                // ⚠️ 必须直接 ConstructorInfo.Invoke,不能走 Activator.CreateInstance(Type, params object[]) ——
                //    第 4 个参数 indexSelector=null,Activator 从"null + 其他运行时类型"反推不出
                //    Func<TItem,int>? 形参签名,抛 MissingMethodException "Constructor not found"。
                //    GetConstructors 拿到唯一 ctor 后,Invoke 直接按位置塞参数,不做类型推断。
                //    Public + NonPublic 都开,因为 ScatterIngestor 是 internal class(同程序集 OK)。
                var ctor = ingestorType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                       .FirstOrDefault(c => c.GetParameters().Length == 5);
                if (ctor == null)
                {
                    Console.WriteLine($"[Hevo 蓝图警告] 找不到 ScatterIngestor<{itemType.Name},{valueType.Name}> 5 参 ctor,跳过 {propName}");
                    continue;
                }
                var ingestor = ctor.Invoke(new object?[]
                {
                    portInstance,
                    resolvedLength,
                    defaultValue,
                    /* indexSelector */ null,
                    valueSelector
                });

                pipe.AddIngestor((IDataIngestor<TItem>)ingestor);
            }
        }

        /// <summary>反射拿数据源当前快照,用于 StartWith 首帧。失败时返回 null,流就不走 StartWith。</summary>
        private DataSnapshot<TItem>? TryGetInitialSnapshot()
        {
            var method = _dataSourceInstance.GetType().GetMethod("GetSnapshot", BindingFlags.Public | BindingFlags.Instance);
            if (method == null || method.ReturnType != typeof(DataSnapshot<TItem>)) return null;
            try { return (DataSnapshot<TItem>)method.Invoke(_dataSourceInstance, null)!; }
            catch { return null; }
        }

        // ==========================================
        // 2. 动态装配声明式特征 (Features)
        // ==========================================
        protected override void DefineFeatures(IFeatureContext canvas)
        {
            // 1. 播种全局初始特质 (Seed)
            //
            // 💥 必须反射调泛型 Seed<TraitType>(...) ——
            //    直接 canvas.Seed(vt)(vt: IVisualTrait)会让 C# 把 T 推断成 IVisualTrait,
            //    VisualDataBag.Publish<T> 按 T 静态类型算 TraitId<T>.Id,trait 会被存进
            //    "IVisualTrait" 槽,而下游 ctx.Shared().Read<ScaleStrategyTrait>() 找的是
            //    "ScaleStrategyTrait" 槽 → 永远拿到 null → AxisFeature/LineSeries 全部
            //    早退黑屏。MakeGenericMethod 把 T 锁成真实运行时类型,槽对齐。
            foreach (var traitDef in _blueprint.InitialTraits)
            {
                Type traitType = ComponentRegistry.Resolve(traitDef.TraitTypeName);
                var traitInstance = SmartActivator.MaterializeTrait(traitType, traitDef);
                if (traitInstance is not IVisualTrait)
                {
                    Console.WriteLine($"[Hevo 蓝图警告] {traitDef.TraitTypeName} 不是 IVisualTrait,Seed 已跳过");
                    continue;
                }
                _seedMethod.MakeGenericMethod(traitType).Invoke(canvas, new[] { traitInstance });
            }

            // 2. 💥 动态组装 Features
            foreach (var featureDef in _blueprint.Features)
            {
                Type featureType = ComponentRegistry.Resolve(featureDef.TypeName);
                var feature = (ChartFeature)Activator.CreateInstance(featureType)!;

                // 步骤 A：注入普通基本属性 (如 PaddingRatio = 0.05)
                SmartActivator.InjectProperties(feature, featureDef.Properties);

                // 步骤 B：执行引脚焊接 (Port Binding)
                // 协议:
                //   单 DataPort<T>: PortBindings["DataPort"] = "global_price_id"
                //   数组 DataPort<T>[]: PortBindings["ValuePorts"] = "id1,id2,id3"  (CSV)
                //   ↑ 两种 shape 用同一字典 + 字符串值表达,后端在此统一拆。
                foreach (var binding in featureDef.PortBindings)
                {
                    string propertyName = binding.Key; // 如 "PricePort" / "ValuePorts"
                    string portIdOrCsv  = binding.Value;

                    // 引脚 setter 通常是 init-only;反射 SetValue 在 .NET 5+ 无视 init 修饰符,可绕过。
                    var propInfo = featureType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (propInfo == null)
                    {
                        Console.WriteLine($"[Hevo 蓝图警告] {featureType.Name} 缺少端口属性 {propertyName},焊接跳过");
                        continue;
                    }

                    Type pt = propInfo.PropertyType;

                    // 1) 单 DataPort<T>
                    if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(DataPort<>))
                    {
                        Type portDataType = pt.GetGenericArguments()[0];
                        var portInstance = GetOrCreatePort(portDataType, portIdOrCsv.Trim());
                        if (portInstance == null)
                        {
                            Console.WriteLine($"[Hevo 蓝图警告] {featureType.Name}.{propertyName} 端口类型冲突,焊接跳过");
                            continue;
                        }
                        propInfo.SetValue(feature, portInstance);
                        continue;
                    }

                    // 2) DataPort<T>[] —— 多源扇入,CSV 拆出来逐个 GetOrCreatePort 后塞数组
                    if (pt.IsArray)
                    {
                        Type? elem = pt.GetElementType();
                        if (elem != null && elem.IsGenericType && elem.GetGenericTypeDefinition() == typeof(DataPort<>))
                        {
                            Type portDataType = elem.GetGenericArguments()[0];
                            var ids = portIdOrCsv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                            // 类型冲突时整段 binding 跳过,避免半截数组让 ingestor 拿到 null 槽位崩溃。
                            var resolved = new List<object>(ids.Length);
                            bool ok = true;
                            foreach (var id in ids)
                            {
                                var inst = GetOrCreatePort(portDataType, id.Trim());
                                if (inst == null) { ok = false; break; }
                                resolved.Add(inst);
                            }
                            if (!ok)
                            {
                                Console.WriteLine($"[Hevo 蓝图警告] {featureType.Name}.{propertyName} 数组端口存在类型冲突,焊接跳过");
                                continue;
                            }
                            var arr = Array.CreateInstance(elem, resolved.Count);
                            for (int i = 0; i < resolved.Count; i++) arr.SetValue(resolved[i], i);
                            propInfo.SetValue(feature, arr);
                            continue;
                        }
                    }

                    Console.WriteLine($"[Hevo 蓝图警告] {featureType.Name}.{propertyName} 不是 DataPort<T> 或 DataPort<T>[],焊接跳过");
                }

                // 步骤 C：挂载到画布 (替换为最新标准 API: Add)
                canvas.Add(feature);
            }
        }
    }
}
