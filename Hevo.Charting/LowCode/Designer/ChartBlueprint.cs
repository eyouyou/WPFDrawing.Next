using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Hevo.Charting.LowCode.Designer
{
    public class ChartBlueprint
    {
        public DataSourceModel? DataSource { get; set; }

        // 💥 淘汰 Layer 和 Sink！现在只有 Feature！
        public List<FeatureModel> Features { get; set; } = new();

        public List<StyleModel> InitialTraits { get; set; } = new();

        /// <summary>
        /// 💥 Schema 级触发器(协议扩展):声明式心跳 / 定时 / 节流 等周期性数据驱动。
        /// 当前仅支持 Kind="Interval" + Workflow.FetchExclusive 防重入语义,业务侧 handler 通过
        /// <see cref="BlueprintHandlerRegistry"/> 命名注册,蓝图本身不携带闭包(JSON 友好)。
        /// 详见低代码优化方案 §K (K 线迁移引入的 trigger 协议)。
        /// </summary>
        public List<TriggerModel> Triggers { get; set; } = new();
    }

    /// <summary>
    /// Schema 级触发器(蓝图协议扩展)。
    /// <para>
    /// 等价 C# 调用链:
    /// </para>
    /// <code>
    /// Workflow.Interval(TimeSpan.FromSeconds(IntervalSeconds))
    ///         .FetchExclusive(handlers.GetFetch(Handler))
    ///         .Subscribe(_ => {}, ex => log)
    ///         .OwnedBy(this);
    /// </code>
    /// <para>
    /// JSON 形态:
    /// </para>
    /// <code>
    /// { "Kind": "Interval", "IntervalSeconds": 1, "Handler": "OnHeartbeat", "Exclusive": true }
    /// </code>
    /// </summary>
    public class TriggerModel
    {
        /// <summary>触发器类型。当前仅支持 "Interval";后续可扩展 "Throttle" / "Debounce"。</summary>
        public string Kind { get; set; } = "Interval";

        /// <summary>Interval 周期(秒)。Kind="Interval" 时必填,小于等于 0 视为非法,trigger 跳过装配。</summary>
        public double IntervalSeconds { get; set; } = 1.0;

        /// <summary>handler 名称。业务侧调 <see cref="BlueprintHandlerRegistry.RegisterFetch"/> 注册同名委托。</summary>
        public string Handler { get; set; } = "";

        /// <summary>
        /// true → <see cref="Workflow"/>.FetchExclusive (CAS 防重入,正在跑则丢弃新信号);
        /// false → 每 tick fire-and-forget 调用 handler,允许并发堆叠(handler 需自负重入风险)。
        /// </summary>
        public bool Exclusive { get; set; } = true;
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

        // 2. 💥 引脚连线板:Key=Feature 的属性名(如 "PricePort" / "ValuePorts")。
        //    Value 形态(全部由 PortBindingValue 解析):
        //      - 单端口 (DataPort<T>):    string  "global_price_id"
        //      - 扇入端口 (DataPort<T>[]): string[] / List<string> ["id1","id2","id3"]  (新格式)
        //      - <v1 兼容: CSV 字符串 "id1,id2,id3"  (反序列化能读但写出统一升级到数组)
        //    用 object? 是为了让 System.Text.Json 在反序列化时既识别字符串也识别 JSON 数组,
        //    业务侧手撸 dict 也能直接塞 string / List<string> / string[]。
        public Dictionary<string, object?> PortBindings { get; set; } = new();
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
        private readonly BlueprintHandlerRegistry? _handlers;

        // 💥 全局引脚注册表：按 ID 缓存实例化的 DataPort<T>
        private readonly Dictionary<string, object> _portRegistry = new();

        // IFeatureContext.Seed<T> 的反射句柄 —— 详见 DefineFeatures 里 Seed 段的注释。
        private static readonly MethodInfo _seedMethod =
            typeof(IFeatureContext).GetMethod(nameof(IFeatureContext.Seed))
            ?? throw new InvalidOperationException("IFeatureContext.Seed 方法签名变了,需同步更新蓝图反射调用。");

        // 💥 Seed<T> 编译委托缓存:每次 MakeGenericMethod + Invoke 一笔反射开销 (~几十 μs + box arg)。
        // 编译后直接 callvirt Seed<T> + 丢弃返回值 (Lambda<Action> 自动 pop),trait 多时累计可观。
        // key=trait runtime type;首次访问编译,后续命中直接调委托。
        private static readonly ConcurrentDictionary<Type, Action<IFeatureContext, object>> _seedInvokerCache = new();

        private static Action<IFeatureContext, object> GetSeedInvoker(Type traitType)
            => _seedInvokerCache.GetOrAdd(traitType, BuildSeedInvoker);

        private static Action<IFeatureContext, object> BuildSeedInvoker(Type traitType)
        {
            var ctxParam   = Expression.Parameter(typeof(IFeatureContext), "ctx");
            var traitParam = Expression.Parameter(typeof(object), "trait");
            // 💥 关键:必须显式 MakeGenericMethod 而不是 Seed<object> 之类 ——
            //    VisualDataBag.Publish<T> 按 T 静态类型算 TraitId<T>.Id 决定槽位,
            //    T 必须锁成 trait 真实运行时类型,否则下游 Read<ConcreteType>() 找的是另一个槽 → 黑屏。
            var seedClosed = _seedMethod.MakeGenericMethod(traitType);
            var call       = Expression.Call(ctxParam, seedClosed, Expression.Convert(traitParam, traitType));
            // Seed 返回 IFeatureContext,Lambda<Action> 编译时自动 pop 掉返回值,跟原 Invoke 丢弃返回值同语义。
            return Expression.Lambda<Action<IFeatureContext, object>>(call, ctxParam, traitParam).Compile();
        }

        public DynamicChartSchema(
            ChartBlueprint blueprint,
            object dataSourceInstance,
            IWorkflow<DataSnapshot<TItem>> sourceStream,
            BlueprintHandlerRegistry? handlers = null)
        {
            _blueprint = blueprint ?? throw new ArgumentNullException(nameof(blueprint));
            _dataSourceInstance = dataSourceInstance ?? throw new ArgumentNullException(nameof(dataSourceInstance));
            _sourceStream = sourceStream ?? throw new ArgumentNullException(nameof(sourceStream));
            _handlers = handlers;

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

            // 💥 Triggers 装配 (协议扩展 §K) —— 蓝图声明的 Schema 级触发器逐条复刻成
            //   Workflow.Interval(...).FetchExclusive(handler).Subscribe(...).OwnedBy(this) 调用链。
            //   handler 通过 BlueprintHandlerRegistry 命名查表,蓝图本身保持纯数据。
            //   注:必须放在 BindTo 之后,确保 chart cell 已经接管主流;trigger 自身的 IDisposable
            //   通过 Own(...) 挂在 schema 生命周期上,Suspend / Dispose 时一并冻结/清理。
            foreach (var trigger in _blueprint.Triggers)
            {
                WireTrigger(trigger);
            }
        }

        /// <summary>
        /// 把一条 <see cref="TriggerModel"/> 复刻成实际的 Workflow 调用链。
        /// 失败原因 (handler 未注册 / handlers 为 null / Kind 不识别) 不抛,打 warning 跳过 ——
        /// 跟 Feature/Trait 装配的 silent-skip 风格保持一致,蓝图局部坏不影响其他段。
        /// </summary>
        private void WireTrigger(TriggerModel def)
        {
            if (def == null) return;
            if (def.Kind != "Interval")
            {
                Console.WriteLine($"[Hevo 蓝图警告] 未知 Trigger Kind '{def.Kind}',目前只支持 Interval,跳过。");
                return;
            }
            if (def.IntervalSeconds <= 0)
            {
                Console.WriteLine($"[Hevo 蓝图警告] Trigger '{def.Handler}' IntervalSeconds={def.IntervalSeconds} 非法,跳过。");
                return;
            }
            if (_handlers == null)
            {
                Console.WriteLine($"[Hevo 蓝图警告] Trigger '{def.Handler}' 需要 BlueprintHandlerRegistry,DynamicChartSchema 未提供 handlers,跳过。");
                return;
            }
            var fetch = _handlers.TryGetFetch(def.Handler);
            if (fetch == null)
            {
                Console.WriteLine($"[Hevo 蓝图警告] Trigger handler '{def.Handler}' 未注册或类型不匹配 Func<VersionToken,CancellationToken,Task<bool>>,跳过。");
                return;
            }
            // Exclusive=true (默认): FetchExclusive — CAS 防重入,正在跑则直接丢弃新信号。
            // Exclusive=false: 不加门,每个 tick fire-and-forget 调一次 handler;handler 自己接受
            //                  并发堆叠风险(典型场景:多个独立资源并发 fetch,互不阻塞)。
            //                  Errors 走 lambda 内部 catch 打日志,不让单次失败把 Subscribe 错误流毒掉。
            string handlerName = def.Handler;
            IDisposable sub;
            if (def.Exclusive)
            {
                sub = Workflow.Interval(TimeSpan.FromSeconds(def.IntervalSeconds))
                              .FetchExclusive(fetch)
                              .Subscribe(_ => { },
                                         ex => Console.WriteLine($"[Hevo 蓝图 Trigger 异常 '{handlerName}'] {ex}"));
            }
            else
            {
                sub = Workflow.Interval(TimeSpan.FromSeconds(def.IntervalSeconds))
                              .Subscribe(async tick =>
                              {
                                  try { await fetch(tick, CancellationToken.None); }
                                  catch (Exception ex)
                                  {
                                      Console.WriteLine($"[Hevo 蓝图 Trigger 异常 '{handlerName}' (Exclusive=false)] {ex}");
                                  }
                              },
                              ex => Console.WriteLine($"[Hevo 蓝图 Trigger 异常 '{handlerName}'] {ex}"));
            }
            // ReactiveSchema.Own 挂载到 schema 生命周期 —— Suspend / Dispose 时级联冻结/清理,
            // IntervalSubscription 自身实现 IPausable 也会被框架自动触达。
            Own(sub);
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

        /// <summary>
        /// 协议扩展 §K:把 Properties 字典里"指向 Delegate 属性的字符串名字"翻译成 BlueprintHandlerRegistry 里的实际委托。
        /// 跳过翻译的条目会被从返回字典里剔除 —— 让 SmartActivator 不要再尝试把 string 塞 Delegate 字段(注定失败 + 打 warning)。
        /// 非 Delegate 属性 / 字符串值不命中任何注册条目 / handlers 为 null 时,该 entry 原样保留。
        /// </summary>
        private Dictionary<string, object?> ResolveHandlerReferences(Type featureType, Dictionary<string, object?>? props)
        {
            if (props == null || props.Count == 0) return new Dictionary<string, object?>();
            var resolved = new Dictionary<string, object?>(props);
            foreach (var kv in props)
            {
                if (kv.Value is not string handlerName || string.IsNullOrEmpty(handlerName)) continue;
                var pi = featureType.GetProperty(kv.Key,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (pi == null) continue;
                if (!typeof(Delegate).IsAssignableFrom(pi.PropertyType)) continue;

                // 该属性是 Delegate 类型且 Properties 里给的是字符串 → 必走 handler 注册表查表。
                if (_handlers == null)
                {
                    Console.WriteLine($"[Hevo 蓝图警告] Feature {featureType.Name}.{kv.Key} 期望 Delegate 但 handlers 未注册,跳过该字段。");
                    resolved.Remove(kv.Key);
                    continue;
                }
                var del = _handlers.TryGet(handlerName);
                if (del == null)
                {
                    Console.WriteLine($"[Hevo 蓝图警告] Feature {featureType.Name}.{kv.Key} 引用 handler '{handlerName}' 未注册,跳过该字段。");
                    resolved.Remove(kv.Key);
                    continue;
                }
                if (!pi.PropertyType.IsAssignableFrom(del.GetType()))
                {
                    Console.WriteLine($"[Hevo 蓝图警告] Feature {featureType.Name}.{kv.Key}: handler '{handlerName}' 类型 {del.GetType().Name} 不兼容目标 {pi.PropertyType.Name},跳过该字段。");
                    resolved.Remove(kv.Key);
                    continue;
                }
                resolved[kv.Key] = del;
            }
            return resolved;
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
                GetSeedInvoker(traitType)(canvas, traitInstance);
            }

            // 2. 💥 动态组装 Features
            foreach (var featureDef in _blueprint.Features)
            {
                Type featureType = ComponentRegistry.Resolve(featureDef.TypeName);
                // ComponentRegistry.CreateInstance 走编译委托缓存,首次反射后续直接 newobj,
                // 50 Feature 蓝图加载从 ~30ms 反射 ctor 降到 ~3ms。
                var feature = (ChartFeature)ComponentRegistry.CreateInstance(featureType);

                // 步骤 A:注入普通基本属性 (如 PaddingRatio = 0.05)。
                // 协议扩展 §K:先做 handler 名字解析 —— Properties 里若有 string 值且对应属性是 Delegate 类型,
                // 翻译为已注册的实际委托;翻译失败的条目会被剥离,SmartActivator 拿不到字符串再去硬塞 Delegate 槽。
                var injectableProps = ResolveHandlerReferences(featureType, featureDef.Properties);
                SmartActivator.InjectProperties(feature, injectableProps);

                // 步骤 B:执行引脚焊接 (Port Binding)
                //   单 DataPort<T>:   PortBindings["DataPort"]   = "global_price_id"            (string)
                //   扇入 DataPort<T>[]: PortBindings["ValuePorts"] = ["id1","id2","id3"]         (数组,新格式)
                //                       PortBindings["ValuePorts"] = "id1,id2,id3"              (CSV,<v1 兼容)
                //   值的实际形态由 PortBindingValue.ExtractSingle/ExtractList 解释,业务 / JSON / 编辑器 三方手撸都行。
                foreach (var binding in featureDef.PortBindings)
                {
                    string propertyName = binding.Key; // 如 "PricePort" / "ValuePorts"
                    object? rawValue    = binding.Value;

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
                        var id = PortBindingValue.ExtractSingle(rawValue);
                        if (string.IsNullOrEmpty(id)) continue;
                        var portInstance = GetOrCreatePort(portDataType, id);
                        if (portInstance == null)
                        {
                            Console.WriteLine($"[Hevo 蓝图警告] {featureType.Name}.{propertyName} 端口类型冲突,焊接跳过");
                            continue;
                        }
                        propInfo.SetValue(feature, portInstance);
                        continue;
                    }

                    // 2) DataPort<T>[] —— 多源扇入,数组 / CSV 都能读,逐个 GetOrCreatePort 后塞数组
                    if (pt.IsArray)
                    {
                        Type? elem = pt.GetElementType();
                        if (elem != null && elem.IsGenericType && elem.GetGenericTypeDefinition() == typeof(DataPort<>))
                        {
                            Type portDataType = elem.GetGenericArguments()[0];
                            var ids = PortBindingValue.ExtractList(rawValue);
                            // 类型冲突时整段 binding 跳过,避免半截数组让 ingestor 拿到 null 槽位崩溃。
                            var resolved = new List<object>(ids.Count);
                            bool ok = true;
                            foreach (var id in ids)
                            {
                                var inst = GetOrCreatePort(portDataType, id);
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
