# Charting

> 基于 C# / WPF 的 AAA 级金融图表引擎，针对千万级数据点设计，0-GC、强类型引脚、声明式装配。

## 这是什么

一套自研的金融行情图表内核，目标是替代传统 WPF 图表库在大数据量、强交互、严苛帧率场景下的能力短板。它不是一个通用 BI 图表库，而是面向 K 线、分时、指标这类**高频量化场景**的专用引擎。

核心能力：

- **三明治物理模型**：底层 SkiaSharp 光栅 + 中层 `DrawingVisual` 矢量 + 顶层 WPF Widget 交互，按场景显式分层，避免 XAML 过度嵌套。
- **0-GC 渲染管线**：列式内存布局（SoA）、`ArrayPool` 复用、原位更新；Project 帧严禁分配闭包，依赖 `UseDynamicMemo` 做依赖追踪。
- **自研响应式流（`IWorkflow<T>`）**：弃用 `System.Reactive`，搭配 `DataBlackboard` + `ReaderWriterLockSlim` 实现多引脚的"时空一致性"。
- **强类型引脚 `DataPort<T>`**：贯穿数据源 → 计算 → 渲染的物理线缆，杜绝魔术字符串与隐式耦合。
- **ECS 化 Feature 模型**：`ChartFeature` + `Trait` + `ReactiveSchema` 声明式装配，渲染 / 交互 / 逻辑彻底解耦。
- **低代码蓝图装配**：`ChartBlueprint`(JSON) + `BlueprintRunner` 在运行时孵化 `DynamicChartSchema<T>`，反射绑定全局引脚与 Feature。
- **编译期校验**：配套 Roslyn 分析器与源生成器在编译期拦截 0-GC、Hooks、生命周期等架构红线。

## 项目结构

| 项目 | 说明 |
|------|------|
| [Hevo.Charting](./Hevo.Charting) | 引擎核心：抽象层、Feature 库、Layer / Renderer、低代码蓝图与 Designer。 |
| [Hevo.Charting.CodeAnalysis](./Hevo.Charting.CodeAnalysis) | Roslyn 分析器与源生成器：`UsePort` Hook 校验、跨帧缓存检测、Trait Consumer / Required 校验、Port Generator。 |
| [Hevo.Drawing.LowCodeDemo](./Hevo.Drawing.LowCodeDemo) | 低代码蓝图运行时与 Node Editor 的演示宿主（WPF）。 |

> 详细的开发约束（特别是 **.NET 5 兼容硬约束**、Roslyn 3.x 钉版、`LangVersion` 红线）见 [Hevo.Charting/README.md](./Hevo.Charting/README.md)。**所有新代码在提交前都需对照该文件 §2 自检。**

## 文档导航

所有规范、协议与白皮书集中在引擎核心目录下。**新成员或 AI 助手在写代码前，先读 `Hevo.Charting/README.md`（项目级开发准则），再按需翻 00 → 01 → 04 系列。**

| # | 文档 | 受众 | 内容 |
|---|------|------|------|
| — | [Hevo.Charting/README.md](./Hevo.Charting/README.md) | 全员 / AI | 项目入口指南、.NET 5 兼容红线、注释规范、AI 协作唤醒词。 |
| 00 | [架构协议与开发规范 v2.0](./Hevo.Charting/00.Hevo.Charting%20架构协议与开发规范%20\(v2.0\).md) | AI / 架构师 / 开发者 | 架构红线、System Prompt、ECS 协议、并发与上下文边界。**最高纲领文件。** |
| 01 | [业务 Feature 设计 & 开发手册](./Hevo.Charting/01.业务Feature设计.md) | 业务开发者 | Feature 铁律、`OnCompose` / `OnProject` 切分、`DataBlackboard` / `UsePort` 物理读取限制。 |
| 04 | [交互层架构](./Hevo.Charting/04.交互层架构.md) | 交互 Feature 开发者 | WPF 事件 → `ChartInteractionFeature` → 视口意图 → 命中广播的全链路。 |
| 06 | [WPF 渲染规约](./Hevo.Charting/06.WPF%20渲染规约.md) | 渲染层维护者 | 像素对齐契约、snap 实现、WPF 后端性能路线图。 |
| — | [Renderer.md](./Hevo.Charting/Renderer.md) | 渲染层维护者 | 接入新渲染后端（Skia / WPF / 自定义）的指南。 |
| — | [LowCode/Designer/低代码.md](./Hevo.Charting/LowCode/Designer/低代码.md) | 低代码 / 业务开发者 | 蓝图编译器、`ChartBlueprint` / `BlueprintRunner` / `ComponentRegistry` 六件套。 |
| — | [TODO.md](./Hevo.Charting/TODO.md) | 全员 | 触发条件 + 改动方案的技术债。 |

## 架构核心一图流

```text
[数据源] IWorkflow<IReadOnlyList<TItem>>
    │
    ▼
====================【UniversalDataPipe<TItem>】====================
    ├─► 摄入 Ingestors  : TItem → 0-GC 列式数组 (Column<T>)
    │
  [DataBlackboard 诞生，挂满 Column 数组]
    │
    ├─► 计算 ComputeNodes : 纯数学黑盒，读写黑板数组 (BOLL / MACD / 极值)
====================================================================
    │
    ▼
[路由分发] IWorkflow<DataBlackboard>
    │
    ├─► 渲染 SinkActions : 黑板数据映射为 Trait → ChartLayer
    │
    ▼
[光栅化] SkiaSharp (Hardware) / WPF DrawingVisual (Vector) / Widget (Interaction)
```

## 不可逾越的红线（节选）

完整列表见 [00 号文档](./Hevo.Charting/00.Hevo.Charting%20架构协议与开发规范%20\(v2.0\).md)，下面是最常踩的几条：

- **0-GC**：`Project` 帧禁止 `new`、禁止隐式闭包，昂贵计算只走 `ctx.UseDynamicMemo` 且必须带 `static`。
- **Hooks 铁律**：`UsePort` / `UseDynamicMemo` 必须在 `OnProject` 顶层、无条件、按固定顺序执行；防空短路只能在解包之后做。
- **物理坐标隔离**：禁止手写 `(x - min) / span * width`，一律走 `IScale.Normalize`。
- **NRT 严格合规**：禁止 `!` 包容操作符压制警告，必须用 `if` / `??` / `throw` 显式处理。
- **Feature 不持有 DataSource**：业务 Feature 只认 `DataPort<T>` 引脚，所有数据装配统一走 `Schema.DefineDataFlow`。
- **禁用 `System.Reactive`**：全盘走自研 `IWorkflow<T>`。
- **.NET 5 兼容硬约束**：禁止 `IIncrementalGenerator`、`ArgumentNullException.ThrowIfNull`、collection expression `[]`、`required` 等 .NET 6+ / Roslyn 4+ 独占特性，详见 [Hevo.Charting/README.md §2](./Hevo.Charting/README.md)。

## 构建

按项目单独编译（推荐）：

```bash
dotnet build "Hevo.Charting/Hevo.Charting.csproj" -nologo
dotnet build "Hevo.Charting.CodeAnalysis/Hevo.Charting.CodeAnalysis.csproj" -nologo
dotnet build "Hevo.Drawing.LowCodeDemo/Hevo.Drawing.LowCodeDemo.csproj" -nologo
```

整解决方案：

```bash
dotnet build "Hevo.Drawing.slnx" -nologo
```

> 目标平台：Any CPU / x64。需要支持 WPF 的 .NET SDK。仓库无独立测试项目，目前依赖 build 验证。
