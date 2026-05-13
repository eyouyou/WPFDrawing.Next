# Hevo.Charting

> 基于 C# / WPF 的高性能金融图表引擎，针对千万级数据点设计，0-GC 渲染管线、强类型引脚、声明式装配，配套低代码蓝图与 LLM 工具链。

## 这是什么

一套自研的金融行情图表内核，目标是替代传统 WPF 图表库在大数据量、强交互、严苛帧率场景下的能力短板。它不是一个通用 BI 图表库，而是面向 K 线、分时、指标这类**高频量化场景**的专用引擎。

核心能力：

- **三明治物理模型**：底层 SkiaSharp 光栅 + 中层 `DrawingVisual` 矢量 + 顶层 WPF Widget 交互，按场景显式分层，避免 XAML 过度嵌套。
- **0-GC 渲染管线**：列式内存布局（SoA）、`ArrayPool` 复用、原位更新；Project 帧严禁分配闭包，依赖 `UseDynamicMemo` 做依赖追踪。
- **自研响应式流（`IWorkflow<T>`）**：弃用 `System.Reactive`，搭配 `DataBlackboard` + `ReaderWriterLockSlim` 实现多引脚的"时空一致性"。
- **强类型引脚 `DataPort<T>`**：贯穿数据源 → 计算 → 渲染的物理线缆，杜绝魔术字符串与隐式耦合。
- **ECS 化 Feature 模型**：`ChartFeature` + `Trait` + `ReactiveSchema` 声明式装配，渲染 / 交互 / 逻辑彻底解耦。
- **低代码蓝图装配**：`ChartBlueprint`(JSON) + `BlueprintRunner` 在运行时孵化 `DynamicChartSchema<T>`，反射绑定全局引脚与 Feature。
- **Python 指标支持**：嵌入 CPython（pythonnet），`@indicator` / `@handler` 装饰器即写即用，蓝图侧零 Python 概念。
- **MCP server**：Model Context Protocol 工具集（`list_components` / `describe_component` / `validate_blueprint`），让 LLM 安全地编辑与校验蓝图。
- **编译期校验**：配套 Roslyn 分析器与源生成器在编译期拦截 0-GC、Hooks、生命周期等架构红线。

## Quick Start

跑起来看看示例（K 线 + 指标 + Python handler + Mock 交易面板）：

```bash
dotnet build "Hevo.Drawing.slnx" -nologo
dotnet run --project Hevo.Drawing.LowCodeDemo -c Debug
```

> 要求：Windows + 支持 WPF 的 .NET 8 SDK（当前 TFM 为 `net8.0-windows10.0.19041.0`）。首次启动会同步嵌入式 Python 312 运行时与 demo 指标。

启动后能看到：低代码 Node Editor、内嵌的 K 线 Dashboard、AvalonEdit Python 编辑器、Mock 撮合面板。改完蓝图 JSON 热加载即可生效。

## 项目结构

| 项目 | 角色 | 说明 |
|------|------|------|
| [Hevo.Charting](./Hevo.Charting) | 引擎核心 | 抽象层、Feature 库、Layer / Renderer、低代码蓝图、Designer。 |
| [Hevo.Charting.CodeAnalysis](./Hevo.Charting.CodeAnalysis) | 编译期 | Roslyn 分析器与源生成器：`UsePort` Hook 校验、跨帧缓存检测、Trait Consumer / Required 校验、Port Generator。 |
| [Hevo.Drawing.LowCodeDemo](./Hevo.Drawing.LowCodeDemo) | 示例宿主 | 低代码蓝图运行时 + Node Editor + Python 编辑器 + Mock 撮合的 WPF 演示工程。 |
| [Hevo.Charting.PythonNet](./Hevo.Charting.PythonNet) | 集成层 | Python 集成：`IPythonRuntime` + `PythonHandlerRegistry` + 蓝图层 `PyFeature` 节点。详见 [PYTHON_HANDLER_GUIDE](./Hevo.Charting.PythonNet/PYTHON_HANDLER_GUIDE.md)。 |
| [Hevo.Charting.Mcp](./Hevo.Charting.Mcp) | LLM 工具链 | MCP 服务进程，向 LLM 暴露 ComponentRegistry 与 DryRun 校验器，闭合"see → describe → validate"循环。详见 [README](./Hevo.Charting.Mcp/README.md)。 |
| [Hevo.Charting.Tests](./Hevo.Charting.Tests) | 测试 | xUnit 单元 / 集成测试。 |
| [Hevo.Charting.Benchmarks](./Hevo.Charting.Benchmarks) | 基准 | BenchmarkDotNet 量化基准，覆盖蓝图编译化、反射 vs 编译 setter、端到端管线等。详见 [README](./Hevo.Charting.Benchmarks/README.md)。 |
| [Hevo.Trade.Abstractions](./Hevo.Trade.Abstractions) | 交易抽象 | `ITradeService` 接口 + DTO + `NullTradeService`；不依赖 Charting / WPF / Skia，可独立引用。 |
| [Hevo.Trade.Mock](./Hevo.Trade.Mock) | 交易实现 | `ITradeService` 的内存 Mock 实装，给 demo / 测试 / 策略回放用。 |

## 文档导航

所有规范、协议与白皮书集中在引擎核心目录下。**新成员或 AI 助手在写代码前，先读 `Hevo.Charting/README.md`（项目级开发准则），再按需翻 00 → 01 → 04 系列。**

### 项目级

| # | 文档 | 受众 | 内容 |
|---|------|------|------|
| — | [Hevo.Charting/README.md](./Hevo.Charting/README.md) | 全员 / AI | 项目入口指南、开发准则、注释规范、AI 协作唤醒词。 |
| 00 | [架构协议与开发规范](./Hevo.Charting/00.Hevo.Charting%20架构协议与开发规范.md) | AI / 架构师 / 开发者 | 架构红线、System Prompt、ECS 协议、并发与上下文边界。**最高纲领文件。** |
| 01 | [业务 Feature 设计](./Hevo.Charting/01.业务Feature设计.md) | 业务开发者 | Feature 铁律、`OnCompose` / `OnProject` 切分、`DataBlackboard` / `UsePort` 物理读取限制。 |
| 04 | [交互层架构](./Hevo.Charting/04.交互层架构.md) | 交互 Feature 开发者 | WPF 事件 → `ChartInteractionFeature` → 视口意图 → 命中广播的全链路。 |
| 06 | [WPF 渲染规约](./Hevo.Charting/06.WPF%20渲染规约.md) | 渲染层维护者 | 像素对齐契约、snap 实现、WPF 后端性能路线图。 |
| — | [Renderer.md](./Hevo.Charting/Renderer.md) | 渲染层维护者 | 接入新渲染后端（Skia / WPF / 自定义）的指南。 |
| — | [LowCode/Designer/低代码.md](./Hevo.Charting/LowCode/Designer/低代码.md) | 低代码 / 业务开发者 | 蓝图编译器、`ChartBlueprint` / `BlueprintRunner` / `ComponentRegistry` 六件套。 |
| — | [TODO.md](./Hevo.Charting/TODO.md) | 全员 | 触发条件 + 改动方案的技术债。 |

### 模块级

| 文档 | 受众 | 内容 |
|------|------|------|
| [Hevo.Charting.Mcp/README.md](./Hevo.Charting.Mcp/README.md) | LLM 集成 | MCP 工具说明、安装到 Claude / Cursor 的步骤、JSON schema。 |
| [Hevo.Charting.PythonNet/PYTHON_HANDLER_GUIDE.md](./Hevo.Charting.PythonNet/PYTHON_HANDLER_GUIDE.md) | 策略 / 指标开发者 | Python handler 写法、`@indicator` 装饰器、PortBindings 反射、热重载、诊断。 |
| [Hevo.Charting.Benchmarks/README.md](./Hevo.Charting.Benchmarks/README.md) | 性能维护者 | 跑法、实测数据、§1–§6 优化方案对照。 |

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

完整列表与判例见 [00 号文档](./Hevo.Charting/00.Hevo.Charting%20架构协议与开发规范.md)，下面是最常踩的几条：

- **0-GC**：`Project` 帧禁止 `new`、禁止隐式闭包，昂贵计算只走 `ctx.UseDynamicMemo` 且必须带 `static`。
- **Hooks 铁律**：`UsePort` / `UseDynamicMemo` 必须在 `OnProject` 顶层、无条件、按固定顺序执行；防空短路只能在解包之后做。
- **物理坐标隔离**：禁止手写 `(x - min) / span * width`，一律走 `IScale.Normalize`。
- **Feature 不持有 DataSource**：业务 Feature 只认 `DataPort<T>` 引脚，所有数据装配统一走 `Schema.DefineDataFlow`。
- **禁用 `System.Reactive`**：全盘走自研 `IWorkflow<T>`。

## 构建与测试

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

测试 / 基准：

```bash
dotnet test  "Hevo.Charting.Tests/Hevo.Charting.Tests.csproj" -c Release
dotnet run --project Hevo.Charting.Benchmarks -c Release -- --filter "*"
```

> 目标平台：Any CPU / x64。需要支持 WPF 的 .NET 8 SDK；MCP / Tests / Benchmarks 子项目同样锁 `net8.0-windows10.0.19041.0`（因 ProjectReference 透传 WPF 依赖）。
