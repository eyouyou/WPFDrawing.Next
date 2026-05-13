using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Hevo.Charting.LowCode.Designer
{
    /// <summary>
    /// Schema 暴露 render-leaf DS —— BlueprintRunner.PickRenderLeaf 挑出来的那个 DS 实例。
    /// <para>
    /// 业务侧 <c>BlueprintCanvas.LeafDataSource&lt;T&gt;()</c> 内部 cast 走这条接口,
    /// 拿到散点图实际渲染的 DS 强类型,然后 .Stream.Subscribe / .GetSnapshot 自由消费。
    /// </para>
    /// <para>
    /// 多 cell 场景每个 cell 独立 schema,各自有自己的 leaf,天然隔离。
    /// 单 cell 多 leaf 当前 framework 装配期 fail-fast(<c>BlueprintRunner.PickRenderLeaf</c> 多 leaf 抛),
    /// 等 Phase 4+ Parallel/Coordinator 支持落地时本接口签名再扩 Role 维度。
    /// </para>
    /// </summary>
    public interface IHaveLeafDataSource
    {
        /// <summary>Render-leaf DS 实例(object 类型,业务侧自己 cast 强类型)。</summary>
        object LeafDataSource { get; }
    }

    public class ChartBlueprint
    {
        /// <summary>
        /// 多数据源协议(Phase 4 节点化形态) —— 蓝图内 DS 集合,跟 <see cref="Features"/> 平级,
        /// 不再有"主从 / 单 DS 中心"概念。Cascade / TriggerBinding 通过 <see cref="DataSourceModel.Id"/> 引用。
        /// <para>
        /// Render-leaf(被 series Feature 消费的渲染叶)由 framework 装配期隐式选取(无 incoming Cascade
        /// 的 DS 即 leaf,多 leaf 报错),不再由业务侧显式指定。
        /// </para>
        /// </summary>
        public List<DataSourceModel> DataSources { get; set; } = new();

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

        /// <summary>
        /// Phase 2 数据源级联边(stocknames → scanner 这种 push 依赖)。
        /// 上游 DS publish stream → ContextDriver(纯函数)投影 → 下游 DS <see cref="ReactiveDataSource{TSource,TContext,TRequest,TResponse,TItem}.SwitchContextErasedAsync"/>。
        /// 装配期校验 driver 返回类型 vs 下游 TContext,不匹配立即报错。
        /// </summary>
        public List<CascadeEdge> Cascades { get; set; } = new();

        /// <summary>
        /// Phase 3 trigger → DS verb 绑定。心跳 / 事件触发 → DS 的 Refresh/Suspend/Resume/SwitchContext/Request,
        /// 跟 <see cref="Triggers"/> 通过 <see cref="TriggerBinding.TriggerId"/> 关联。
        /// 90% 心跳场景(verb=Refresh)零 handler / 零 driver,纯声明式。
        /// </summary>
        public List<TriggerBinding> TriggerBindings { get; set; } = new();

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
        /// <summary>
        /// Phase 3 蓝图内唯一 Id —— <see cref="TriggerBinding.TriggerId"/> 通过此字段引用。
        /// 留空时 framework 用复合 Id "trigger:{IntervalSeconds}s" 兜底,但同蓝图多 trigger 时
        /// 必须显式 set 避免冲突。
        /// </summary>
        public string Id { get; set; } = "";

        /// <summary>触发器类型。当前仅支持 "Interval";后续可扩展 "Throttle" / "Debounce"。</summary>
        public string Kind { get; set; } = "Interval";

        /// <summary>Interval 周期(秒)。Kind="Interval" 时必填,小于等于 0 视为非法,trigger 跳过装配。</summary>
        public double IntervalSeconds { get; set; } = 1.0;

        /// <summary>
        /// <b>旧字段</b>(协议扩展 §K)—— Phase 3 起被 <see cref="ChartBlueprint.TriggerBindings"/> 取代。
        /// 留空时本字段不生效,新协议走 verb dispatcher;非空时旧 <see cref="DynamicChartSchema{TItem}.WireTrigger"/>
        /// 路径仍能装配(零破坏老蓝图)。新蓝图直接用 TriggerBinding(verb=Refresh 多数场景零 handler / 零 driver)。
        /// </summary>
        public string Handler { get; set; } = "";

        /// <summary>
        /// true → <see cref="Workflow"/>.FetchExclusive (CAS 防重入,正在跑则丢弃新信号);
        /// false → 每 tick fire-and-forget 调用 handler,允许并发堆叠(handler 需自负重入风险)。
        /// </summary>
        public bool Exclusive { get; set; } = true;
    }

    /// <summary>
    /// Phase 3 触发器→数据源 verb 绑定 —— 蓝图层把"心跳/事件 → DS 的 Refresh/Suspend/Resume/SwitchContext/Request"
    /// 表达成声明式数据,framework 自动接管装配。多数场景(90% 心跳)零 handler / 零 driver。
    /// <para>
    /// Verb 表:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>Refresh</c> → <see cref="Hevo.Charting.Abstractions.IRefreshable.RefreshAsync"/>(默认,90% 心跳)</item>
    ///   <item><c>Suspend</c> → <see cref="Hevo.Charting.Abstractions.IPausable.Suspend"/></item>
    ///   <item><c>Resume</c>  → <see cref="Hevo.Charting.Abstractions.IPausable.Resume"/></item>
    ///   <item><c>SwitchContext</c> → <see cref="ReactiveDataSource{TSource,TContext,TRequest,TResponse,TItem}.SwitchContextErasedAsync"/>(<see cref="Driver"/> 算 ctx)</item>
    ///   <item><c>Request</c> → <see cref="ReactiveDataSource{TSource,TContext,TRequest,TResponse,TItem}.RequestAsync"/>(<see cref="Driver"/> 算 request,仅 5 参 DS)</item>
    /// </list>
    /// </summary>
    public sealed class TriggerBinding
    {
        /// <summary>对应 <see cref="TriggerModel.Id"/>。</summary>
        public string TriggerId { get; set; } = "";

        /// <summary>对应 <see cref="DataSourceModel.Id"/>。</summary>
        public string TargetDataSourceId { get; set; } = "";

        /// <summary>verb 名(<c>Refresh / Suspend / Resume / SwitchContext / Request</c>),大小写敏感。</summary>
        public string Verb { get; set; } = "Refresh";

        /// <summary>
        /// verb 需要参数时的 driver 名(<c>SwitchContext</c> / <c>Request</c>)。Refresh / Suspend / Resume 不需要,留空。
        /// 引用 <see cref="LowCode.Designer.Handlers.TriggerDriverAttribute"/> 注册的命名函数。
        /// </summary>
        public string? Driver { get; set; }
    }

    /// <summary>
    /// Phase 4 节点化形态 —— DataSource 跟 Feature 同款节点协议:
    /// <list type="bullet">
    ///   <item><see cref="Id"/>:蓝图内唯一,Cascade / TriggerBinding 引用</item>
    ///   <item><see cref="TypeName"/>:ComponentRegistry alias</item>
    ///   <item><see cref="Properties"/>:DS init/setter 属性(<c>MinuteCeiling</c> / 是否预热缓存等),SmartActivator.InjectProperties 注入</item>
    ///   <item><see cref="OutputBindings"/>:端口名 → globalId(单端口 string / 扇入 List&lt;string&gt;);DS 永远是 publish 方,所有字段都是 output</item>
    ///   <item><see cref="DefaultContext"/>:LoadAsync 字符串实参,ContextParser 在运行时解析</item>
    /// </list>
    /// framework 反射 DS 字段类型自动分流 scalar(DS 自身属性,如 LogicalLength)/ vector(TItem 行字段,如 Time/Open/...)。
    /// </summary>
    public class DataSourceModel
    {
        public string Id { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;

        /// <summary>
        /// 数据源的 init/setter 属性(<c>MinuteCeiling</c> / 是否预热缓存等),
        /// SmartActivator.InjectProperties 启动时注入到实例。
        /// </summary>
        public Dictionary<string, object?> Properties { get; set; } = new();

        // ────────────────────────────────────────────────────────────────────────
        //  端口绑定按方向拆开:InputBindings(上游 OutputPort globalId)/ OutputBindings(本端口暴露的 globalId)
        // ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 输入端口绑定:key = Feature/DS 上 InputPort 字段名(含 nested dot 形态 <c>"Inputs.high"</c>),
        /// value = 上游 OutputPort 的 globalId。
        /// <list type="bullet">
        ///   <item>单端口:<c>string "ds_v"</c></item>
        ///   <item>扇入数组:<c>List&lt;string&gt; ["a","b","c"]</c>(对应 <c>DataPort&lt;T&gt;[]</c> 字段)</item>
        /// </list>
        /// <para>
        /// 实际 value 形态由 <see cref="PortBindingValue.ExtractSingle"/> / <see cref="PortBindingValue.ExtractList"/>
        /// 解析(string / List / JsonElement Array 三态统一)。
        /// </para>
        /// </summary>
        public Dictionary<string, object?> InputBindings { get; set; } = new();

        /// <summary>
        /// 输出端口绑定:key = Feature/DS 上 OutputPort 字段名(含 nested dot 形态 <c>"SeriesOutputs.upper"</c>),
        /// value = 本端口对外暴露的 globalId。
        /// <list type="bullet">
        ///   <item>单端口:<c>string "my_signal"</c></item>
        ///   <item>别名数组:<c>List&lt;string&gt; ["primary","legacy_alias"]</c> ——
        ///         <c>_portRegistry</c> 把多个 id 指向同一根 DataPort 实例,让多套消费方按各自约定的 id 都能订阅到</item>
        /// </list>
        /// <para>
        /// <b>注意</b>:Output 数组今天没用例(grep 不到 <c>DataPort&lt;T&gt;[]</c> 标 <c>PortDirection.Output</c>
        /// 的字段),但 schema 对称允许 —— 给未来 alias 用例留口。
        /// </para>
        /// </summary>
        public Dictionary<string, object?> OutputBindings { get; set; } = new();

        /// <summary>
        /// 运行时默认上下文(LoadAsync 实参的字符串描述)。蓝图层只能保持字符串形态(JSON 友好),
        /// 运行时由调用方(<see cref="BlueprintLauncher"/> / 业务宿主)用 ContextParser 解析成具体类型
        /// (典型:<c>"USZA600000"</c> → <c>Security</c> / <c>"SH600000"</c> → <c>KLineQueryContext</c>)。
        /// 不填(null / 空)时调用方走自己的默认值或 BlueprintCanvas DataSourceContexts 字典覆盖。
        /// </summary>
        public string? DefaultContext { get; set; }

        /// <summary>
        /// 上游 DataSource 节点 Id 列表 —— 仅对 <see cref="Composite{TItem}"/> / <see cref="CompositeDataSource{TSource,TItem}"/>
        /// 派生类有意义,引用同蓝图里的其它 DataSourceModel 作为合并器的上游 Stream。
        /// <para>
        /// framework 装配期会按顺序反射 <see cref="CompositeDataSource{TSource,TItem}.Attach"/>:
        /// </para>
        /// <code>
        /// foreach (id in UpstreamRefs) composite.Attach(resolved[id].Stream)
        /// </code>
        /// <para>
        /// render-leaf 推导排除这些 Id(类似 Cascade.FromDataSourceId);TItem 由 Composite 节点的 TypeName 推断,
        /// 装配期校验所有 UpstreamRefs 引用的 DS 必须输出 <c>IWorkflow&lt;DataSnapshot&lt;TItem&gt;&gt;</c>(TItem 一致)。
        /// </para>
        /// <para>
        /// 跟 <see cref="DefaultContext"/> 互补:这条是"我从哪些 DS 拿数据",DefaultContext 是"我自己启动时怎么跑 LoadAsync"。
        /// Composite 一般 UpstreamRefs 非空 + DefaultContext 空(不直接 LoadAsync,等上游 publish 触发 Merge)。
        /// </para>
        /// </summary>
        public List<string> UpstreamRefs { get; set; } = new();
    }

    /// <summary>
    /// Phase 2 数据源级联边 —— 蓝图内 DS-to-DS push 依赖。
    /// <para>
    /// 等价 C# 调用链(framework 装配生成):
    /// </para>
    /// <code>
    /// upstreamDs.Stream.Subscribe(snap =>
    /// {
    ///     var ctx = ContextDriver(upstreamDs, snap);              // 纯函数投影
    ///     downstreamDs.SwitchContextErasedAsync(ctx, default);    // 类型擦除桥
    /// })
    /// </code>
    /// <para>
    /// 业务作用:股票池(stocklist)推一批新 securities → scanner 自动 SwitchContext 重新拉行情;
    /// 双击开窗(scanner→kline) <b>不</b>是 Cascade,走 UriHandler。
    /// </para>
    /// </summary>
    public sealed class CascadeEdge
    {
        /// <summary>上游 DS 的 <see cref="DataSourceModel.Id"/>。</summary>
        public string FromDataSourceId { get; set; } = string.Empty;

        /// <summary>
        /// 下游 DS 的 <see cref="DataSourceModel.Id"/>。装配期 framework 校验 ContextDriver 返回类型
        /// 是否匹配下游 TContext,不匹配立即报错。
        /// </summary>
        public string ToDataSourceId { get; set; } = string.Empty;

        /// <summary>
        /// <see cref="ContextDriverAttribute"/> 注册的命名函数。签名约定:第一参 = 上游 DS 强类型,
        /// 后续参数 = 触发 payload(Stream 时是 <see cref="Hevo.Charting.Core.DataSnapshot{T}"/>),
        /// 返回 = 下游 TContext。零 UI 副作用,可单测。
        /// </summary>
        public string ContextDriver { get; set; } = string.Empty;

        /// <summary>
        /// 触发源:
        /// <list type="bullet">
        ///   <item><c>"Stream"</c>(默认):上游 DS publish 即触发 driver + SwitchContextErasedAsync</item>
        ///   <item><c>"&lt;FeatureId&gt;.&lt;EventName&gt;"</c>:Feature 事件触发(Phase 2.x 扩展,Phase 2 主线只支持 Stream)</item>
        /// </list>
        /// </summary>
        public string Trigger { get; set; } = "Stream";
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

        // ────────────────────────────────────────────────────────────────────────
        //  端口绑定按方向拆开:InputBindings(上游 OutputPort globalId)/ OutputBindings(本端口暴露的 globalId)
        // ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 输入端口绑定:key = Feature 上 InputPort 字段名(含 nested dot 形态 <c>"Inputs.high"</c>),
        /// value = 上游 OutputPort 的 globalId。
        /// <list type="bullet">
        ///   <item>单端口 <see cref="DataPort{T}"/>:<c>string "ds_v"</c></item>
        ///   <item>扇入数组 <see cref="DataPort{T}"/><c>[]</c>:<c>List&lt;string&gt; ["a","b","c"]</c></item>
        /// </list>
        /// <para>
        /// 实际 value 形态由 <see cref="PortBindingValue.ExtractSingle"/> / <see cref="PortBindingValue.ExtractList"/>
        /// 解析,接受 string / List&lt;string&gt; / JsonElement Array。
        /// </para>
        /// </summary>
        public Dictionary<string, object?> InputBindings { get; set; } = new();

        /// <summary>
        /// 输出端口绑定:key = Feature 上 OutputPort 字段名(含 nested dot 形态 <c>"SeriesOutputs.upper"</c>),
        /// value = 本端口对外暴露的 globalId。
        /// <list type="bullet">
        ///   <item>单端口:<c>string "my_signal"</c></item>
        ///   <item>别名数组(future):<c>List&lt;string&gt; ["primary","legacy_alias"]</c> —— 同一根 DataPort
        ///         在 <c>_portRegistry</c> 下注册多个 id,multi-consumer 各按约定 id 订阅</item>
        /// </list>
        /// <para>
        /// <b>注意</b>:Output 数组形态今天 0 用例(grep 不到 <c>[PortDirection(Output)]</c> 的 <c>DataPort&lt;T&gt;[]</c>),
        /// 但 schema 对称允许 —— 跟 <see cref="InputBindings"/> 镜像,且给未来 alias 用例留口。
        /// </para>
        /// </summary>
        public Dictionary<string, object?> OutputBindings { get; set; } = new();

        // 💥 事件路由(URI 风味,蓝图自描述交互):Key=Feature 的 C# 事件名(如 "ElementDoubleClicked"),
        //    Value=URI 模板(如 "hevo://open/kline_detail?index={Index}")。
        //    模板 {Path} 占位符在事件触发时反射 EventArgs 字段填充(支持 . 嵌套 "{Element.Symbol}")。
        //    DynamicChartSchema 装 feature 时反射事件 → 挂订阅 → 触发 → 模板 fill → UriDispatcher.Dispatch
        //    → 业务侧 BlueprintCanvas.OnUri 注册的 handler 接住。
        //    业务侧 0 行 OnSchemaApplied 反查 ceremony,蓝图自描述事件路由。
        public Dictionary<string, string> Events { get; set; } = new();

        /// <summary>
        /// GraphState 的 <c>Node.Properties</c> 字典里搭车存放本表的保留 key。
        /// GraphState 没有 Events 槽位,<see cref="GraphViewer.GraphDeserializer"/> /
        /// <see cref="GraphViewer.GraphSerializer"/> 通过这个 key 在 Properties 里透传 Events,
        /// 保证编辑器 round-trip 不丢失。<c>NodeEditorWindow</c> 按 .NET PropertyInfo 反射渲染,
        /// 不扫 Properties keys,UI 不暴露。
        /// </summary>
        public const string EventsPropertyKey = "__Events__";
    }

    /// <summary>
    /// 💥 动态响应式图表骨架 (由 JSON 蓝图在运行时动态孵化)
    /// 三件事:DefineDataFlow 反射拼 ContextIngestor / ScatterIngestor;
    /// DefineFeatures 反射创建 Feature + 焊接 Port;全局引脚注册表跨 Feature 共享 DataPort 实例。
    /// </summary>
    public class DynamicChartSchema<TItem> : ChartReactiveSchema, IHaveLeafDataSource
    {
        private readonly ChartBlueprint _blueprint;
        private readonly object _dataSourceInstance;
        private readonly IWorkflow<DataSnapshot<TItem>> _sourceStream;
        private readonly BlueprintHandlerRegistry? _handlers;

        // Phase 2:多 DS 蓝图下,这里只持 render-leaf DS 的 model(_dataSourceInstance 对应那个)。
        // 单 DS 蓝图等价 DataSources[0]。Cascade 上游 DS 由 canvas/host 管,不进 schema。
        private DataSourceModel? _renderLeafModel;

        /// <inheritdoc/>
        public object LeafDataSource => _dataSourceInstance;

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
            // Phase 4:从 DataSources 拿"render-leaf DS 的 model"(类型匹配 _dataSourceInstance),
            // 多 DS 蓝图(Cascade)里只有渲染那个 DS 的 mappings/properties 进 schema;
            // 上游 stocklist 之类的 DS 由 canvas 管,不进 schema。
            var renderLeafModel = ResolveRenderLeafModel();
            if (renderLeafModel?.Properties is { Count: > 0 } props)
            {
                SmartActivator.InjectProperties(_dataSourceInstance, props);
            }
            _renderLeafModel = renderLeafModel;

            // VP_* well-known ports 的 _portRegistry 注册已下移到 DefineDataFlow
            // (Phase 4 / 05.md):viewport 权威 = 已 Add 的 ViewportManagerFeature.Ports,
            // 此处 ctor 阶段 features 还没装 (DefineFeatures 在 ComposeAll 里才跑),
            // 提前抓 ports 没意义且 schema 不再持有 fallback ports。
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
                    $"[Hevo 蓝图警告] 端口 '{portId}' 已注册为 {existing.GetType().FullName}," +
                    $"无法以 {expectedPortType.FullName} 重连,跳过。" +
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
            // 💥 注册 VP_* well-known ports —— DefineDataFlow 跑在 BuildAndActivatePipeline 阶段,
            // 早于 PortsFeature.OnAttached(后者才把 ports 挂到 ChartCell attached property)。
            // 所以这里走 Find<>() 直接从 _features 拿 ports —— PortsFeature 在 DefineFeatures 阶段
            // 已 add(蓝图自助 ensure),Decorate 阶段可能被联动 mutate 为 SharedViewport。
            // 拿到 ports 后 ScalarMapping 走 GetOrCreatePort 拿到该 ports;OnProject 读这些端口都对齐。
            // 详见低代码.md §8 端口对齐坑。
            // 注:cell:viewport.* / VP_* 注册已上移到 DefineFeatures 开头(BEFORE 用户 feature 的 PortBinding 解析),
            // 保证 feature.RangePort 等绑定到这些名字时拿到正确的(Linked 场景下的 SharedViewport)port instance,
            // 不被"DefineFeatures 跑在 Decorate 之前"的时序坑掉。这里 DefineDataFlow 阶段不再重复注册。

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
            else if (_renderLeafModel != null)
            {
                CompilePortBindings(_renderLeafModel, pipe, dsType);
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

#if DEBUG
            // 时间线:每个 snapshot 推到 schema = 一个 DataSourcePublish 事件。
            // LoadAsync 完成、RefreshAsync 心跳、StartWith 首帧补发 都从这条路径过 —— 一笔不落。
            // tracer 在 ReactiveSchema ctor 里就 attach 了,这里 capture 给 lambda 闭包。
            var traceForFlow = DevTools.TracerRegistry.Get(this);
            var dsTypeName = _dataSourceInstance.GetType().Name;
            stream = stream.Select(snap =>
            {
                traceForFlow?.RecordKeyEvent(
                    DevTools.TopologyTracer.EventKind.DataSourcePublish,
                    dsTypeName,
                    $"snapshot len={snap.Count}");
                return snap;
            });
#endif

#if DEBUG
            // 💥 first-publish 修复:DataPipe 内部 ContextIngestor / ColumnPublishIngestor 会调
            // board.WriteIfChanged 写 DataSource 字段端口(Value/Time/Index 等)。RecordWrite 走
            // TracerRegistry.Get(board) 取 tracer,所以**必须在第一次 Process 之前** attach,否则
            // 首推那一帧的所有 DataPipe 写入都不会进 LinkHits → inspector 看不到 DataPipe → port 的连线,
            // 也就拿不到 writer prefix。pipe.Board 是跨帧复用的 _persistentBoard 实例,attach 一次永久生效。
            var traceForPipe = DevTools.TracerRegistry.Get(this);
            if (traceForPipe != null) DevTools.TracerRegistry.Attach(pipe.Board, traceForPipe);
#endif

            // 跟 DataPipeBuilder.Seal 共享 PipelineWiring.WireToPipe —— DoOnDispose / pipe.Dispose 一处定义,
            // 蓝图反射装配 + DSL fluent 装配两条路径行为对齐。
            Hevo.Charting.WorkFlow.PipelineWiring.WireToPipe(stream, pipe).BindTo(chart);

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
            // Phase 3 新协议:Handler 留空 + TriggerBindings 引用本 trigger,装配走 BlueprintTriggerBindingWiring。
            // 老路径不应 fire,也不应打"handler 未注册"误导日志 —— 静默退出。
            if (string.IsNullOrEmpty(def.Handler)) return;
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
        /// Phase 4 节点化:单 PortBindings 字典装配 —— framework 反射 DS / TItem 字段类型自动分流。
        /// <list type="bullet">
        ///   <item>key 命中 DS 自身 scalar 属性(<c>LogicalLength</c> 这类) → ContextIngestor&lt;TItem,TSource,TValue&gt;</item>
        ///   <item>key 命中 TItem 行字段(<c>Value</c>/<c>Time</c> 这类) → ScatterIngestor&lt;TItem,TValue&gt;</item>
        ///   <item>都不命中 → warning + skip</item>
        ///   <item>同时命中两种(理论不会,DS 跟 TItem 同名字段冲突) → DS scalar 优先</item>
        /// </list>
        /// value 走 <see cref="PortBindingValue.ExtractSingle"/>(本期 DS 端口暂不扇入,扇入 = 多上游写同 portId 的反例,
        /// 蓝图编辑器层就该拦,不在装配期处理)。
        /// </summary>
        private void CompilePortBindings(DataSourceModel ds, UniversalDataPipe<TItem> pipe, Type dsType)
        {
            if (ds.OutputBindings.Count == 0) return;

            var itemType = typeof(TItem);

            // LogicalLength 长度提供器(给 ScatterIngestor 用):一次性反射,scatter 路径若多个共用同一个 lambda。
            var lengthProp = dsType.GetProperty("LogicalLength", BindingFlags.Public | BindingFlags.Instance);
            Func<int>? lengthProvider = null;
            if (lengthProp != null && lengthProp.PropertyType == typeof(int))
            {
                var dsRef = _dataSourceInstance;
                lengthProvider = () => (int)lengthProp.GetValue(dsRef)!;
            }

            foreach (var kvp in ds.OutputBindings)
            {
                string fieldName = kvp.Key;
                string portId = PortBindingValue.ExtractSingle(kvp.Value);
                if (string.IsNullOrEmpty(portId))
                {
                    Console.WriteLine($"[Hevo 蓝图警告] DataSource {ds.Id}.{fieldName} 端口 globalId 为空,跳过。");
                    continue;
                }

                // 1. DS 自身 scalar 属性优先(LogicalLength 这种 DS 顶层标量)
                var scalarProp = dsType.GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (scalarProp != null && IsScalarType(scalarProp.PropertyType))
                {
                    EmitScalarIngestor(scalarProp, portId, pipe, dsType);
                    continue;
                }

                // 2. TItem 行字段(Value / Time / Open / Close 这类)
                var vectorProp = itemType.GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (vectorProp != null && IsScalarType(vectorProp.PropertyType))
                {
                    EmitVectorIngestor(vectorProp, portId, pipe, lengthProvider);
                    continue;
                }

                Console.WriteLine($"[Hevo 蓝图警告] DataSource {ds.Id}({dsType.Name}) / TItem {itemType.Name} 都没有字段 {fieldName},端口 {portId} 跳过。");
            }
        }

        // 标量类型(同 NodeFactory.IsScalarType 逻辑) —— 反射 DS / TItem 字段时区分"可投影端口"。
        private static bool IsScalarType(Type t)
        {
            if (t == typeof(string)) return true;
            if (t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan)) return true;
            if (t.IsPrimitive) return true;
            if (t == typeof(decimal)) return true;
            if (Nullable.GetUnderlyingType(t) is { } u) return IsScalarType(u);
            return false;
        }

        private void EmitScalarIngestor(PropertyInfo propInfo, string portId, UniversalDataPipe<TItem> pipe, Type dsType)
        {
            var portInstance = GetOrCreatePort(propInfo.PropertyType, portId);
            if (portInstance == null) return;

            var ingestorType = typeof(ContextIngestor<,,>).MakeGenericType(typeof(TItem), dsType, propInfo.PropertyType);

            var param = System.Linq.Expressions.Expression.Parameter(dsType, "ds");
            var getter = System.Linq.Expressions.Expression.Property(param, propInfo);
            var selectorType = typeof(Func<,>).MakeGenericType(dsType, propInfo.PropertyType);
            var selector = System.Linq.Expressions.Expression.Lambda(selectorType, getter, param).Compile();

            var ingestor = Activator.CreateInstance(ingestorType, portInstance, _dataSourceInstance, selector)!;
            pipe.AddIngestor((IDataIngestor<TItem>)ingestor);
        }

        private void EmitVectorIngestor(PropertyInfo propInfo, string portId, UniversalDataPipe<TItem> pipe, Func<int>? lengthProvider)
        {
            Type valueType = propInfo.PropertyType;
            Type memoryType = typeof(ReadOnlyMemory<>).MakeGenericType(valueType);
            var portInstance = GetOrCreatePort(memoryType, portId);
            if (portInstance == null) return;

            var itemType = typeof(TItem);
            var param = System.Linq.Expressions.Expression.Parameter(itemType, "it");
            var getter = System.Linq.Expressions.Expression.Property(param, propInfo);
            var selectorType = typeof(Func<,>).MakeGenericType(itemType, valueType);
            var valueSelector = System.Linq.Expressions.Expression.Lambda(selectorType, getter, param).Compile();

            Func<int> resolvedLength = lengthProvider ?? (() => 0);
            var ingestorType = typeof(ScatterIngestor<,>).MakeGenericType(itemType, valueType);
            object? defaultValue = valueType.IsValueType ? Activator.CreateInstance(valueType) : null;

            var ctor = ingestorType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                   .FirstOrDefault(c => c.GetParameters().Length == 5);
            if (ctor == null)
            {
                Console.WriteLine($"[Hevo 蓝图警告] 找不到 ScatterIngestor<{itemType.Name},{valueType.Name}> 5 参 ctor,跳过 {propInfo.Name}");
                return;
            }
            var ingestor = ctor.Invoke(new object?[]
            {
                portInstance, resolvedLength, defaultValue, /* indexSelector */ null, valueSelector,
            });
            pipe.AddIngestor((IDataIngestor<TItem>)ingestor);
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
                // JSON 持久化路径下 STJ 默认对 Dictionary<string, object?> 不展开 JSON 字符串成 string —— 拿到的是
                // JsonElement(Kind=String)。这里得把两种形态都规整成 string,否则 silent skip 会让 Delegate 字段
                // 上的字符串原样溜进 SmartActivator → setter "Unable to cast 'System.String' to 'System.Delegate'"
                // → handler 永远 null → flow.Watch 早退 → 蓝图编辑器路径下从 JSON 加载的图全黑屏。
                string? handlerName = kv.Value switch
                {
                    string s => s,
                    System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String
                        => je.GetString(),
                    _ => null,
                };
                if (string.IsNullOrEmpty(handlerName)) continue;
                var pi = featureType.GetProperty(kv.Key,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (pi == null) continue;
                if (!typeof(Delegate).IsAssignableFrom(pi.PropertyType)) continue;

                // 该属性是 Delegate 类型且 Properties 里给的是字符串 → 必走 handler 注册表查表。
                // 三种失败场景全是配置错误(蓝图作者引用了不存在/类型不对的 handler),fail fast。
                // 之前 silent remove 让 PlotFeature.Indicator / ComputeFeature.Compute 留 null,
                // 下游 OnCompose 静默早退,黑屏调试半天才能定位 —— 现在直接抛,问题立刻摆到屏前。
                // throw 之前先往 tracer 时间线打一条 —— 业务侧若把异常 catch 了,inspector 仍能看到这一笔。
                if (_handlers == null)
                {
#if DEBUG
                    DevTools.TracerRegistry.Get(this)?.RecordKeyEvent(
                        DevTools.TopologyTracer.EventKind.HandlerMissing, handlerName,
                        $"{featureType.Name}.{kv.Key}: handlers registry 为 null");
#endif
                    throw new InvalidOperationException(
                        $"Feature {featureType.Name}.{kv.Key} 引用 handler '{handlerName}' 但 BlueprintHandlerRegistry 未注册到 schema。" +
                        "业务侧需通过 BlueprintRunner.Run / IBlueprintRunnable.RunBlueprint 的 handlers 参数传 registry。");
                }
                var del = _handlers.TryGet(handlerName);
                if (del == null)
                {
#if DEBUG
                    DevTools.TracerRegistry.Get(this)?.RecordKeyEvent(
                        DevTools.TopologyTracer.EventKind.HandlerMissing, handlerName,
                        $"{featureType.Name}.{kv.Key} 未注册");
#endif
                    throw new InvalidOperationException(
                        $"Feature {featureType.Name}.{kv.Key} 引用 handler '{handlerName}' 未注册。" +
                        $"已注册 handlers: [{string.Join(", ", _handlers.EnumerateNames())}]。" +
                        "业务侧 RegisterDelegate / RegisterPythonFunction 是否成功(看上游 host init log)?名字是否拼写正确?");
                }
                if (!pi.PropertyType.IsAssignableFrom(del.GetType()))
                {
#if DEBUG
                    DevTools.TracerRegistry.Get(this)?.RecordKeyEvent(
                        DevTools.TopologyTracer.EventKind.HandlerMissing, handlerName,
                        $"{featureType.Name}.{kv.Key}: 类型 {del.GetType().Name} 不兼容 {pi.PropertyType.Name}");
#endif
                    throw new InvalidOperationException(
                        $"Feature {featureType.Name}.{kv.Key}: handler '{handlerName}' 类型 {del.GetType().Name} " +
                        $"不兼容目标属性类型 {pi.PropertyType.Name}。检查 RegisterDelegate 的委托签名是否跟 feature 字段匹配。");
                }
                resolved[kv.Key] = del;
            }
            return resolved;
        }

        /// <summary>
        /// 从 <see cref="ChartBlueprint.DataSources"/> 选 render-leaf DS 的 model ——
        /// TypeName 匹配 <see cref="_dataSourceInstance"/> 运行时类型(继承链兜底)。
        /// 单 DS 蓝图唯一项即结果;多 DS 蓝图框架装配期已挑好 leaf 实例传进来,这里按运行时类型反查 model。
        /// 找不到匹配时返回 null,装配走 customPolicy / 空管线。
        /// </summary>
        private DataSourceModel? ResolveRenderLeafModel()
        {
            var ds = _blueprint.DataSources;
            if (ds.Count == 0) return null;
            var dsType = _dataSourceInstance.GetType();

            // 精确匹配 + 继承链兜底(prefix 形态如 "kline:KLineDataSource" 走 ComponentRegistry.IsRegistered prefix-strip,
            // 这里直接按 short name 比对即可,蓝图侧 TypeName 是 ComponentRegistry alias)。
            foreach (var m in ds)
            {
                if (string.Equals(m.TypeName, dsType.Name, StringComparison.Ordinal)) return m;
            }
            foreach (var m in ds)
            {
                // 兜底:派生类匹配父类名 / 蓝图侧用 prefix alias 的场景。
                for (var t = dsType; t != null && t != typeof(object); t = t.BaseType)
                    if (string.Equals(m.TypeName, t.Name, StringComparison.Ordinal)) return m;
            }
            // 都没匹配上:单 DS 蓝图退化(只有一项 = 它就是 render leaf,即便 TypeName 写得不一样)
            if (ds.Count == 1) return ds[0];
            return null;
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
            // PortsFeature 由 ChartReactiveSchema(基类)在 EnsureBaseFeatures 阶段强制 ensure,
            // 蓝图侧不再自助 add。

            // §K binding scope 提前注册 ——
            //   1. dashboard:{name} 来自 LinkedChartContext.SharedPorts(含 ctor 默认 linkedHit + Dashboard 配置)
            //   2. cell:viewport.* 来自 SchemaContext.SharedViewport(Linked) 或本 schema 自家 ViewportPortsFeature(Standalone)
            //
            // 必须**在 features 的 PortBindings 解析(下面 foreach)之前注册** ——
            // GetOrCreatePort 先查 _portRegistry 再决定 reuse 还是 new。提前注册 = 所有绑这些名字的 feature
            // 拿到的是 Decorate 之后真正承载数据的 port instance,而不是被偷换的 LOCAL clone。
            //
            // 关键 trick:Standalone schema 没 dashboard 共享,SchemaContext.SharedViewport 返 null →
            // 走 this.Require<ViewportPortsFeature>().Ports 拿 local viewport(StandaloneSchemaContext.Decorate
            // 是 no-op,local 不会被替换 → 同实例 ok)。
            // LinkedMaster/Pane:SharedViewport != null → 直接拿共享 viewport;Decorate 后 ViewportPortsFeature.Ports
            // 也会被替换成同一个 SharedViewport → ViewportManager / 各 feature 全部对齐同实例。
            foreach (var (name, port) in SharedPorts)
            {
                _portRegistry["dashboard:" + name] = port;
            }
            // dashboard 共享的 hit alias("LinkedHit" 老名兜底业务侧手撸 blueprint 不经 BindingIdMigrator 翻译的场景)
            _portRegistry["LinkedHit"] = HitPort;

            // cell-shared viewport —— 优先用 SchemaContext.SharedViewport(Linked 场景共享实例),
            // fallback 到 schema 自家 ViewportPortsFeature.Ports(Standalone 场景 local instance)。
            var vpScope = SharedViewport ?? this.Require<Features.ViewportPortsFeature>().Ports;
            _portRegistry["cell:viewport.logicalLength"] = vpScope.LogicalLength;
            _portRegistry["cell:viewport.userRange"]     = vpScope.UserRange;
            _portRegistry["cell:viewport.active"]        = vpScope.ActiveRange;
            // legacy alias —— 旧 JSON 直接引用 "VP_*" 字面值时走同一份 port instance。
            _portRegistry["VP_LogicalLength"] = vpScope.LogicalLength;
            _portRegistry["VP_UserRange"]     = vpScope.UserRange;
            _portRegistry["VP_ActiveRange"]   = vpScope.ActiveRange;

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
                // cast 到 Feature 基类:GridLayoutFeature 等不消费 Viewport 的内置 feature 已从 ChartFeature 降级 Feature,
                // 蓝图节点反射 instantiate 后必须 cast 到通用基类,否则 InvalidCastException。
                var feature = (Feature)ComponentRegistry.CreateInstance(featureType);

                // 步骤 A:注入普通基本属性 (如 PaddingRatio = 0.05)。
                // 协议扩展 §K:先做 handler 名字解析 —— Properties 里若有 string 值且对应属性是 Delegate 类型,
                // 翻译为已注册的实际委托;翻译失败的条目会被剥离,SmartActivator 拿不到字符串再去硬塞 Delegate 槽。
                var injectableProps = ResolveHandlerReferences(featureType, featureDef.Properties);
                SmartActivator.InjectProperties(feature, injectableProps);

                // 步骤 B:执行引脚焊接 (Port Binding)
                //   OutputBindings: OutputPort 的 globalId — 必须先焊接,确保 port 实例被注册,Input 侧才能 reuse。
                //   InputBindings:  单端口 string / 扇入 List<string> / nested dict ("Inputs.high") 三形态。
                //   值形态由 PortBindingValue.ExtractSingle/ExtractList 解释,业务 / JSON / 编辑器三方手撸都行。
                foreach (var binding in featureDef.OutputBindings.Concat(featureDef.InputBindings))
                {
                    string propertyName = binding.Key; // 如 "PricePort" / "ValuePorts" / "Inputs.high"
                    object? rawValue    = binding.Value;

                    // §D2.X 多输入:nested key 形如 "Inputs.high" → 拆 ParentField + ChildKey,
                    // 父字段必须是 Dictionary<string, DataPort<T>> 类型。
                    int dotIdx = propertyName.IndexOf('.');
                    if (dotIdx > 0)
                    {
                        string parentName = propertyName.Substring(0, dotIdx);
                        string childKey = propertyName.Substring(dotIdx + 1);
                        if (!TryWireNestedDictPort(featureType, feature, parentName, childKey, rawValue))
                        {
                            // TryWireNestedDictPort 内部已打 warning。
                        }
                        continue;
                    }

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

                    // 1.5) object —— 调试 / 通用 feature(典型 ConsoleDebuggerFeature.TargetPort)接 object 装任意 DataPort<T>。
                    //      反射不知道 generic 参数,从 _portRegistry 按 id 查已注册的 port instance(其他 feature 之前
                    //      GetOrCreatePort 时注册过)。查不到则 warning + 跳过(ConsoleDebugger 自己 OnCompose 应 null-safe)。
                    if (pt == typeof(object))
                    {
                        var id = PortBindingValue.ExtractSingle(rawValue);
                        if (string.IsNullOrEmpty(id)) continue;
                        if (_portRegistry.TryGetValue(id, out var existingPort))
                        {
                            propInfo.SetValue(feature, existingPort);
                            continue;
                        }
                        Console.WriteLine($"[Hevo 蓝图警告] {featureType.Name}.{propertyName} 是 object 类型,蓝图未提前注册 port id '{id}'(其他 feature 应先引用过它),焊接跳过");
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

                // 步骤 C':事件路由(URI 风味)—— featureDef.Events 字典声明的事件订阅
                //          在挂载之前焊接,挂载后 framework 走完 ComposeAll feature 已就位,事件可触发。
                //          DataSource 引用传入,URI 模板 {Items[Index].X} 走 dataSource.GetSnapshot().Items 反射。
                WireFeatureEvents(feature, featureType, featureDef, _dataSourceInstance);

                // 步骤 C：挂载到画布 (替换为最新标准 API: Add)
                canvas.Add(feature);
            }
        }

        /// <summary>
        /// 反射蓝图 <see cref="FeatureModel.Events"/> 字典里声明的事件 → URI 路由,挂订阅到 feature 实例。
        /// 触发时反射 EventArgs + DataSource snapshot 填充 URI 模板,通过 <see cref="UriRouting.UriDispatcher.Default"/> 分发。
        /// </summary>
        private static void WireFeatureEvents(object feature, Type featureType, FeatureModel featModel, object dataSource)
        {
            if (featModel.Events == null || featModel.Events.Count == 0) return;

            foreach (var (eventName, uriTemplateString) in featModel.Events)
            {
                if (string.IsNullOrEmpty(eventName) || string.IsNullOrEmpty(uriTemplateString)) continue;

                var eventInfo = featureType.GetEvent(eventName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (eventInfo == null)
                {
                    Console.WriteLine($"[Hevo 蓝图警告] {featureType.Name} 没有事件 '{eventName}',Events 路由跳过");
                    continue;
                }

                UriRouting.UriTemplate template;
                try { template = UriRouting.UriTemplate.Parse(uriTemplateString); }
                catch (FormatException fex)
                {
                    Console.WriteLine($"[Hevo 蓝图警告] {featureType.Name}.{eventName} URI 模板解析失败:{fex.Message},路由跳过");
                    continue;
                }

                var handlerType = eventInfo.EventHandlerType
                    ?? throw new InvalidOperationException($"{featureType.Name}.{eventName} EventHandlerType 为 null");
                var del = BuildEventDispatchDelegate(handlerType, template, dataSource);
                eventInfo.AddEventHandler(feature, del);
            }
        }

        /// <summary>
        /// 构造匹配 EventHandler / EventHandler&lt;T&gt; 签名的 delegate:事件触发时
        /// ① 抓 DataSource 当前 snapshot.Items 塞进 URI context(让模板 <c>{Items[Index].X}</c> 能 resolve);
        /// ② 反射 EventArgs + context 填模板;
        /// ③ 调 UriDispatcher.Default.Dispatch。
        /// 业务侧 handler 拿到的 UriArgs 已经是 string 化的 stock symbol / index 等,无需再反查 DataSource。
        /// </summary>
        private static Delegate BuildEventDispatchDelegate(
            Type handlerType, UriRouting.UriTemplate template, object dataSource)
        {
            // 反射拿 DataSource.GetSnapshot 方法(DataSource<TSource, TItem>.GetSnapshot 协议);
            // snapshot 是 record DataSnapshot<TItem>,有 Items: TItem[] 字段。
            // forward delegate 触发时 lazy 调用,拿最新 snapshot。
            var dsType = dataSource.GetType();
            var getSnapshotMethod = dsType.GetMethod("GetSnapshot",
                BindingFlags.Public | BindingFlags.Instance, types: Type.EmptyTypes);

            var invoke = handlerType.GetMethod("Invoke")
                ?? throw new InvalidOperationException($"handler 类型 {handlerType} 没有 Invoke 方法");
            var parameters = invoke.GetParameters();

            var paramExprs = new System.Linq.Expressions.ParameterExpression[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                paramExprs[i] = System.Linq.Expressions.Expression.Parameter(parameters[i].ParameterType, parameters[i].Name);

            int argsIndex = parameters.Length >= 2 ? 1 : parameters.Length - 1;
            var argsExpr = System.Linq.Expressions.Expression.Convert(paramExprs[argsIndex], typeof(object));

            // delegate body: BuildContextAndDispatch(template, dataSource, getSnapshotMethod, eventArgs)
            var helperMethod = typeof(DynamicChartSchema<TItem>).GetMethod(
                nameof(InvokeUriRoute),
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    $"反射不到 {nameof(DynamicChartSchema<TItem>)}.{nameof(InvokeUriRoute)}");

            var body = System.Linq.Expressions.Expression.Call(
                helperMethod,
                System.Linq.Expressions.Expression.Constant(template),
                System.Linq.Expressions.Expression.Constant(dataSource),
                System.Linq.Expressions.Expression.Constant(getSnapshotMethod, typeof(MethodInfo)),
                argsExpr);

            return System.Linq.Expressions.Expression.Lambda(handlerType, body, paramExprs).Compile();
        }

        /// <summary>
        /// runtime 触发入口(被 <see cref="BuildEventDispatchDelegate"/> 编译成 delegate 调用)。
        /// 抓 DataSource snapshot.Items 塞 context,fill 模板,dispatch。
        /// </summary>
        private static void InvokeUriRoute(
            UriRouting.UriTemplate template,
            object dataSource,
            MethodInfo? getSnapshotMethod,
            object eventArgs)
        {
            Dictionary<string, object?>? context = null;
            if (getSnapshotMethod != null)
            {
                try
                {
                    var snapshot = getSnapshotMethod.Invoke(dataSource, null);
                    if (snapshot != null)
                    {
                        context = new Dictionary<string, object?>(StringComparer.Ordinal);
                        // DataSnapshot<TItem> 是 record,Items / Count 是字段或属性
                        var snapType = snapshot.GetType();
                        var itemsMember = (object?)snapType.GetProperty("Items") ?? snapType.GetField("Items");
                        var countMember = (object?)snapType.GetProperty("Count") ?? snapType.GetField("Count");
                        if (itemsMember is PropertyInfo ipi) context["Items"] = ipi.GetValue(snapshot);
                        else if (itemsMember is FieldInfo ifi) context["Items"] = ifi.GetValue(snapshot);
                        if (countMember is PropertyInfo cpi) context["Count"] = cpi.GetValue(snapshot);
                        else if (countMember is FieldInfo cfi) context["Count"] = cfi.GetValue(snapshot);
                        // DataSource 自身也暴露(模板可写 {DataSource.X})
                        context["DataSource"] = dataSource;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ChartBlueprint] URI 路由 snapshot 抓取失败:{ex.Message}");
                }
            }

            string uri;
            try { uri = template.Fill(eventArgs, context); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChartBlueprint] URI 模板填充失败 '{template.Template}':{ex.Message}");
                return;
            }

            // services 注入:DataSource 实例按运行时类型 + 所有继承链塞 dict,handler 用 RequireService<T> 强类型拿。
            // 替代 URI 模板里反序列化复杂业务对象(Symbol.Market / Symbol.Code 拆字段),URI 只传标量索引,
            // handler 通过 services 拿 typed DataSource 自由反查。
            var services = new Dictionary<Type, object>();
            for (var t = dataSource.GetType(); t != null && t != typeof(object); t = t.BaseType)
                services[t] = dataSource;

            UriRouting.UriDispatcher.Default.Dispatch(uri, services);
        }

        /// <summary>
        /// §D2.X 多输入指标:解析 PortBindings 里 <c>"Inputs.high"</c> 这类 nested key,
        /// 把端口实例写进 Feature 上 <c>Dictionary&lt;string, DataPort&lt;T&gt;&gt;</c> 字段对应槽位。
        /// 父字段不是字典 / 字典 value 不是 DataPort&lt;T&gt; / 端口类型冲突时打 warning + 跳过。
        /// </summary>
        private bool TryWireNestedDictPort(Type featureType, object feature, string parentName, string childKey, object? rawValue)
        {
            var parentProp = featureType.GetProperty(parentName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (parentProp == null)
            {
                Console.WriteLine($"[Hevo 蓝图警告] {featureType.Name} 缺少嵌套父字段 {parentName},焊接跳过 {parentName}.{childKey}");
                return false;
            }

            // 父字段类型必须是 Dictionary<TKey, TValue> —— Generic dict 走泛型反射,IDictionary 兜底走非泛型。
            var dictType = parentProp.PropertyType;
            if (!dictType.IsGenericType || dictType.GetGenericTypeDefinition() != typeof(Dictionary<,>))
            {
                Console.WriteLine($"[Hevo 蓝图警告] {featureType.Name}.{parentName} 不是 Dictionary<string, DataPort<T>>,焊接跳过 {parentName}.{childKey}");
                return false;
            }
            var genArgs = dictType.GetGenericArguments();
            if (genArgs[0] != typeof(string))
            {
                Console.WriteLine($"[Hevo 蓝图警告] {featureType.Name}.{parentName} 字典 key 不是 string,焊接跳过");
                return false;
            }
            var valueType = genArgs[1];   // DataPort<TPort>
            if (!valueType.IsGenericType || valueType.GetGenericTypeDefinition() != typeof(DataPort<>))
            {
                Console.WriteLine($"[Hevo 蓝图警告] {featureType.Name}.{parentName} 字典 value 不是 DataPort<T>,焊接跳过");
                return false;
            }
            Type portDataType = valueType.GetGenericArguments()[0];

            // 拿字典实例,init=null 时直接 new 一个塞回去(setter 是 init-only 也能反射通)。
            var dict = parentProp.GetValue(feature) as System.Collections.IDictionary;
            if (dict == null)
            {
                dict = (System.Collections.IDictionary)Activator.CreateInstance(dictType)!;
                parentProp.SetValue(feature, dict);
            }

            string id = PortBindingValue.ExtractSingle(rawValue);
            if (string.IsNullOrEmpty(id))
            {
                Console.WriteLine($"[Hevo 蓝图警告] {featureType.Name}.{parentName}.{childKey} 端口 id 空,焊接跳过");
                return false;
            }
            var port = GetOrCreatePort(portDataType, id);
            if (port == null)
            {
                Console.WriteLine($"[Hevo 蓝图警告] {featureType.Name}.{parentName}.{childKey} 端口类型冲突,焊接跳过");
                return false;
            }
            dict[childKey] = port;
            return true;
        }
    }
}
