# Hevo.Charting 架构演进与装配方案白皮书

> **版本**: v1.0  
> **日期**: 2026-03-07  
> **关键词**: 金融图表引擎、双轨架构、零耦合、DataPort、FeatureNode

---

## 核心愿景

打造一个**高内聚、零耦合、无心智负担**的现代金融图表引擎。

框架在底层全自动接管生命周期、防抖、数据分发与内存回收，业务开发者只需关注**"拼装"**与**"连线"**。

---

## 双轨装配方案概览

框架提供两种平滑过渡的装配方案，以适应不同的工程阶段：

| 方案 | 名称 | 适用场景 | 核心优势 |
|------|------|----------|----------|
| **方案 A** | 代码优先的流式装配 | 当前开发阶段 | 极致编码效率、类型推断、零运行时开销 |
| **方案 B** | 低代码节点编排 | 未来战略阶段 | 可视化拖拽、UI 蓝图编辑器对接 |

---

## 方案 A：代码优先的流式装配 (Code-First Workflow)

### 核心概念

#### 1. DataPort&lt;T&gt; — 数据引脚

贯穿引擎的**强类型"物理线缆"**。它是独立于 Feature 和 Schema 存在的内存地址标识。

```csharp
// 声明物理线缆
private readonly DataPort<ReadOnlyMemory<DateTime>> _timePort = 
    DataPort<ReadOnlyMemory<DateTime>>.Create("Time");
```

#### 2. Feature — 核心大组件

业务能力的集大成者（如 X 轴、K线、十字光标）。它们在构造时接收 DataPort，并在生命周期内自主完成图层挂载、事件订阅和数据投影。

#### 3. ReactiveSchema — 响应式图纸

提供干净的工作台，负责收集 Feature 并启动数据清洗管线（DataPipe）。

---

### 标准代码范式

#### 核心 Feature 设计

核心 Feature 仅依赖 DataPort 和 Provider，绝不硬编码业务逻辑（以真实的 XAxisFeature 为例）：

```csharp
public class XAxisFeature<TDomain> : ChartFeature
{
    // 1. 声明生命周期阶段 (引擎自动排序)
    public override FeaturePhase Phase => FeaturePhase.Scale;

    // 💥 接入全场唯一的视口全家桶，拒绝私拉乱接！
    public ViewportPorts Viewport { get; init; } = null!;
    private readonly ITickProvider<TDomain> _tickProvider; 
    
    public XAxisFeature(ITickProvider<TDomain> tickProvider)
    {
        _tickProvider = tickProvider;
    }
    
    public override void Project(RenderContext ctx, DataBlackboard board)
    {
        // 2. 防空拦截：直接从黑板读取全场公认的 ActiveRange
        var activeRange = board.Read(Viewport.ActiveRange);
        if (!activeRange.IsValid || activeRange.Span <= 0) return;

        // 3. 提取 Scale 策略，彻底与物理坐标系解耦
        var scaleStrategy = ctx.Shared().Read<ScaleStrategyTrait>() ?? ScaleStrategyTrait.Default;
        var strategy = _tickProvider.GetStrategy(board);
        
        // ... 使用 scaleStrategy 和 activeRange 执行渲染逻辑
    }
}
```

#### Schema 中的拼装体验 (DSL 领域特定语言)

在 Schema 中，管线构建与特征组装被完美分离，语义极其清晰：

```csharp
public class KLineSchema : ReactiveSchema<KLineItem>
{
    // 1. 声明纯粹的业务数据引脚
    private static readonly DataPort<ReadOnlyMemory<float>> ClosePort = new("Close");
    
    // 💥 声明统一的视口总线全家桶
    private readonly ViewportPorts VP = new();

    public KLineSchema(KLineDataSource dataSource) { ... }

    // 2. 核心数据管线：极致纯净，只做数据映射，0 GC！
    protected override IRenderFlow<DataBlackboard> DefineDataFlow(ChartCell chart)
    {
        var pipe = new UniversalDataPipe<KLineItem>()
            .From(_dataSource)
            // 将基础属性映射为底层列 (0 闭包分配)
            .LinkStream(cfg => cfg.Map(ClosePort, x => x.Close, 0f))
            // 写入总长度供大管家使用
            .Inject(ds => ds.Count).ForwardTo(VP.LogicalLength).End()
            .Process(new CandleYAxisProcessor { /* 独立算子，无 Lambda */ })
            .Build();

        return _dataSource.Stream.BindTo(chart).With(pipe);
    }

    // 3. 画布拼装：职责分明，高内聚！
    protected override void DefineFeatures(IFeatureCanvas canvas)
    {
        canvas.Seed(ScaleStrategyTrait.Default); // 播种地基

        canvas.Use(new GridLayoutFeature { Right = 60, Bottom = 24 });
        
        // 💥 让专业的人做专业的事：视口大管家接管范围计算与碰撞
        canvas.Use(new ViewportManagerFeature { Viewport = VP, DefaultVisibleCount = 100 });
        
        // 动态注入时间刻度策略，传入全家桶
        canvas.Use(new XAxisFeature<double>(new TimeTickProvider(TimePort, "MM-dd")) { Viewport = VP });
        
        canvas.Use(new CandleFeature { Viewport = VP, ClosePort = ClosePort /* ... */ });
    }
}
```

---

## 方案 B：低代码节点编排 (Node-Based Blueprint)

### 适用场景

未来战略阶段，需对接 UI 蓝图编辑器，实现**可视化拖拽生成图表**。

---

### 核心概念

| 概念 | 说明 |
|------|------|
| **InPin&lt;T&gt; / OutPin&lt;T&gt;** | 挂载在节点外部的逻辑接口，用于在 UI 上呈现连接端子 |
| **FeatureNode&lt;T&gt;** | 将核心 Feature 包装为可连线的节点 |
| **Wire.Bind()** | 将 OutPin 或 DataPort 物理连入 InPin 插座 |

---

### 标准代码范式

Schema 彻底变成**"电气接线板"**，完全契合节点编辑器的序列化结构：

```csharp
public class KLineBlueprintSchema : ChartBlueprintSchema<KLineData>
{
    protected override void BuildBlueprint(IChartBlueprint blueprint)
    {
        // 1. 实例化节点
        var xAxisNode = new TimeAxisNode();
        
        // 2. 将全局线缆插进节点的插座里 (可视化连线的代码映射)
        xAxisNode.TimeIn.Bind(_timeBus);
        xAxisNode.ViewportIn.Bind(_viewportBus);

        // 3. 挂载到蓝图
        blueprint.Attach(xAxisNode);
    }
}
```

---

## 终极武库：从 A 到 B 的无痛迁移方案

### 设计原则：开闭原则 (Open-Closed Principle)

在向方案 B（低代码）迁移时，**绝对不修改方案 A 中的任何核心 Feature 代码**。

核心 Feature（如 `TimeAxisFeature`）继续保持它**"只认 DataPort"**的高效形态。我们通过引入**泛型包装器模式 (Generic Adapter Pattern)** 实现平滑升级。

---

### 核心引擎支持：泛型包装基类 FeatureNode&lt;TFeature&gt;

框架提供该基类，自动吸纳所有防呆校验、反射扫描和生命周期代理逻辑：

```csharp
public abstract class FeatureNode<TFeature> : ChartAspect where TFeature : ChartAspect
{
    private TFeature? _coreFeature;
    
    // 💥 静态字典缓存：每个节点类型只反射一次，彻底实现 0-GC 极速启动！
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _pinCache = new();

    protected abstract TFeature CreateFeature();

    public sealed override void Compose(ChartCell chart, RenderContext ctx)
    {
        // 1. O(1) 极速提取插座元数据
        var pinProps = _pinCache.GetOrAdd(this.GetType(), type => 
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => typeof(IInPin).IsAssignableFrom(p.PropertyType))
                .ToArray());

        // 2. 校验连线状态
        bool allConnected = true;
        foreach (var prop in pinProps)
        {
            if (prop.GetValue(this) is IInPin pin && !pin.IsConnected)
            {
                allConnected = false;
                break;
            }
        }

        if (!allConnected)
        {
            Console.WriteLine($"[Node Warning] {this.GetType().Name} 存在悬空引脚，取消核心组件挂载！");
            return; // 防空保护！
        }

        // 3. 引脚完备，实例化老组件并代理其生命周期
        _coreFeature = CreateFeature();
        _coreFeature.Compose(chart, ctx);
    }

    public sealed override void Project(RenderContext ctx, DataBlackboard board) => 
        _coreFeature?.Project(ctx, board);
    
    public sealed override void Decompose(ChartCell chart, RenderContext ctx) => 
        _coreFeature?.Decompose(chart, ctx);
}
```

---

### 业务迁移实操指南

当低代码团队需要引入一个已有的 `XxxFeature` 时，只需极简的**三步**：

#### Step 1: 创建 Wrapper 类

继承 `FeatureNode<T>`，明确你要包装的目标老组件。

#### Step 2: 声明插座 (InPin)

对外暴露这个节点需要什么数据。

#### Step 3: 实现转换工厂

将 `InPin` 内部连接的 `DataPort` 提取出来，喂给老组件的构造函数。

---

### 示例：极简的包装器代码

```csharp
/// <summary>
/// 这是一个为可视化平台准备的包装节点，它内部包裹着原封不动的 TimeAxisFeature
/// </summary>
public class TimeAxisNode : FeatureNode<TimeAxisFeature>
{
    // 声明对外暴露的插座
    public InPin<ReadOnlyMemory<DateTime>> TimeIn { get; } = new();
    public InPin<DoubleRange> ViewportIn { get; } = new();

    // 唯一的一句业务逻辑：用插座里的线缆，去实例化老组件
    protected override TimeAxisFeature CreateFeature() => 
        new TimeAxisFeature(TimeIn.ConnectedPort!, ViewportIn.ConnectedPort!);
}
```

---

### 快捷迁移通道：内联包装器

如果某个 Feature 过于简单，连新建一个 `XxxNode` 类的动作都想省去，可以在装配时直接使用静态工厂完成动态包装：

```csharp
// 在 Schema 中直接将老组件包装成节点，无需声明新类
var lineNode = NodeWrapper.Wrap(pricePin, port => new LineSeriesFeature(port, "现价"));
```

---

## 架构总结

这套**"双轨制 + 包装器"**架构为引擎带来了无与伦比的纵深：

| 层级 | 特性 | 优势 |
|------|------|------|
| **下层** | 坚若磐石 | 核心引擎只有 Feature 和 DataPort，性能极高，代码极简，适合资深研发快速堆砌图表 |
| **上层** | 千变万化 | 低代码扩展层通过 InPin 和 FeatureNode 提供了极强的防呆校验和反射自描述能力，可以直接被 UI 连线图读取渲染 |
| **架构** | 彻底解耦 | 底层渲染逻辑永远不知道上层节点编辑器的存在，实现了真正意义上的关注点分离 |

---

## 附录：术语表

| 术语 | 英文 | 说明 |
|------|------|------|
| 数据引脚 | DataPort&lt;T&gt; | 强类型内存地址标识，贯穿引擎的"物理线缆" |
| 核心大组件 | Feature | 业务能力的集大成者，如 X 轴、K线、十字光标 |
| 响应式图纸 | ReactiveSchema | 负责收集 Feature 并启动数据清洗管线 |
| 插座 | InPin&lt;T&gt; / OutPin&lt;T&gt; | 节点外部的逻辑接口，用于 UI 连接端子 |
| 节点包装器 | FeatureNode&lt;T&gt; | 将核心 Feature 包装为可连线的节点 |
| 接线 | Wire.Bind() | 将 OutPin 或 DataPort 物理连入 InPin 插座 |

---

*本文档由 Hevo.Charting 架构团队编写，如有疑问请联系架构组。*
