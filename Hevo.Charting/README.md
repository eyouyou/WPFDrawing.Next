# Hevo.Charting

WPF/.NET 8 金融图表引擎与示例业务图。本文档是项目入口指南 + AI 协作准则 + 注释规范的合并归一。深入架构请读 `00–06.*.md` 系列。

---

## 1. 仓库范围

主要工作面:
- `Hevo.Charting`:核心图表引擎、workflow、schema、feature、blackboard、渲染管线、低代码蓝图
- `Hevo.Charting.CodeAnalysis`:Roslyn 生成器(尤其 PortGenerator)
- `Hevo.Charting.Tests`:xUnit 测试集(蓝图 RoundTrip / DryRun / ComponentRegistry / NodePortCache 等)
- `Hevo.Charting.Benchmarks`:BenchmarkDotNet 基准(低代码子系统 §1–§8 优化)
- `Hevo.Charting.Mcp`:MCP server,把蓝图能力暴露给 LLM
- `Hevo.Drawing.LowCodeDemo`:蓝图 + Node Editor 的可视化运行宿主(WPF)

业务工作请聚焦 `Hevo.Charting` / `Hevo.Charting.CodeAnalysis` / `Hevo.Drawing.LowCodeDemo`,其余按需。

---

## 2. ⚠️ 维持 .NET 5 兼容(硬性约束)

虽然项目当前编译目标是 **.NET 8**,但全部新增/修改代码 **必须保持可在 .NET 5 (SDK 5.0.408 + Roslyn 3.x) 下重新编译通过**。这是项目级硬约束,不是可选偏好。任何引入 .NET 6+/Roslyn 4+ 独占特性的提交都视为破坏约束,需要回退或提供等价兼容方案。

### 核心红线

1. **业务侧 (`Hevo.Drawing.Sample` 等下游项目) 严格 `LangVersion=9.0`**。csproj 已显式锁定。禁止使用 record struct、global using、file-scoped namespace、collection expression `[]`、`required` 修饰符、原始字符串字面量等 C# 10+ 特性。
2. **Framework 侧 (`Hevo.Charting`) `LangVersion=preview`**,可在 `.NET 5 SDK 5.0.408` 下解锁 record struct + global using;但仍只允许使用 .NET 5 SDK preview 编译器实际能编译的 C# 10 特性子集。**禁止使用 `required` (C# 11)、collection expression (C# 12) 等 .NET 5 SDK 不识别的特性**。
3. **BCL API 红线** — 不得使用以下 .NET 6+ API:
   - `ArgumentNullException.ThrowIfNull` → 用 `if (x is null) throw new ArgumentNullException(nameof(x))`
   - `Task.WaitAsync(CancellationToken)` → 手写 `CancellationTokenRegistration` + TCS 等价模式
   - `DateOnly` / `TimeOnly` / `PriorityQueue` / `Random.Shared` / `TimeProvider` / `SearchValues` / `FrozenDictionary` 等
4. **Roslyn 红线 — Generator 不得使用 `IIncrementalGenerator`**。所有 generator 必须实现 `ISourceGenerator + ISyntaxContextReceiver`(失去增量缓存的代价)。`Hevo.Charting.CodeAnalysis` 的 NuGet 引用 **钉死 Microsoft.CodeAnalysis.CSharp 3.11.0**。
5. **WPF 编译 quirk** — `Hevo.Drawing.Sample.csproj` 用 `<Analyzer Include="...dll" />` 直接注册 generator,而 `<ProjectReference>` 不带 `OutputItemType=Analyzer`。**这是为了绕过 .NET 5 SDK 的 `_wpftmp` 编译不继承 Analyzer 类型 ProjectReference 的 bug**。net8 已修但保留此写法以维持双向可切。
6. **包版本** — 例如 `System.Text.Encoding.CodePages` 钉 5.0.0;若升级请同步评估 net5 是否仍能解析。
7. **类型选型** — `IVisualTrait`(进 blackboard)用 `record class`;value payload(`HevoPoint` / `FieldMeta` / `PointerHitState` 等)用 `record struct` 或 `record class`(具体见 `00.架构协议与开发规范`)。**`record struct` 的 `with` 表达式是 C# 10 特性**,若调用方在 `LangVersion=9` 下,必须提供 `WithXxx(...)` helper 替代。

### 切回 .NET 5 的清单(测试用)

如果需要验证 net5 兼容是否被破坏,把以下三处改回即可重现:
- `Hevo.Charting.csproj`:`<TargetFramework>net5.0-windows10.0.19041.0</TargetFramework>`
- `Hevo.Drawing.Sample.csproj`:`<TargetFramework>net5.0-windows10.0.19041.0</TargetFramework>`
- 项目根新建 `global.json`:`{"sdk":{"version":"5.0.408","rollForward":"disable"}}`

预期 3 个项目 (`Hevo.Charting.CodeAnalysis` / `Hevo.Charting` / `Hevo.Drawing.Sample`) 全部 0 error 通过。

### 写代码时必须做的

- 引入新的 BCL API 前,在 [docs.microsoft.com](https://learn.microsoft.com/dotnet/api) 查 "Applies to" 是否包含 .NET 5;不包含就找等价手写实现或加 polyfill。
- 添加新的 generator/analyzer 前,确认只用 Microsoft.CodeAnalysis 3.11 已有的 API。
- 在 Sample 项目内写新文件时,**手写完整 `using` 列表**(System / Collections.Generic / Linq / Threading / Threading.Tasks 等),不要依赖 ImplicitUsings — Sample 没开启它。

---

## 3. 常用命令

按项目单独编译:

```bash
dotnet build "Hevo.Charting/Hevo.Charting.csproj" -nologo
dotnet build "Hevo.Charting.CodeAnalysis/Hevo.Charting.CodeAnalysis.csproj" -nologo
dotnet build "Hevo.Drawing.LowCodeDemo/Hevo.Drawing.LowCodeDemo.csproj" -nologo
```

整解决方案编译:

```bash
dotnet build "Hevo.Drawing.slnx" -nologo
```

跑测试 / 基准:

```bash
dotnet test "Hevo.Charting.Tests/Hevo.Charting.Tests.csproj" -nologo
dotnet run -c Release --project "Hevo.Charting.Benchmarks/Hevo.Charting.Benchmarks.csproj" -- --filter "*"
```

注意:
- 仅修改单模块时优先单项目 build,避免被 WPF `_wpftmp` 重编译拖慢节奏。
- `Hevo.Charting.Mcp` 通过 ProjectReference 间接依赖 WPF 运行时,目前 Windows-only。

---

## 4. 架构铁律(摘要)

完整规约见 `00.架构协议与开发规范`。下列是日常写代码必须随手记得的红线:

- 不引入 `System.Reactive`,只用自研 `IWorkflow<T>`。
- 数据进黑板后只通过 `DataPort<T>` 通信,业务实体泛型 `TItem` 仅停留在数据接入侧。
- `OnProject` 内 **顶层、无条件、稳定顺序** 调用 `UsePort(...)`;严禁在 `OnProject` 内写 `DataBlackboard`,严禁跨帧缓存 `UsePort` 返回的 memory。
- Feature 执行顺序由 `FeaturePhase` 控制,严禁靠 `List.Add` 顺序硬编码。
- 物理坐标必须经 `IScale.Normalize`,严禁手写 `(x - min) / span * width` 之类反推公式。
- 运行时增删 Feature 必须走 `Transact(...)`,不要散装 `Add/Remove`。
- 业务侧首选 `Environment / Axes / Series / Interactions` 四段 builder,优先 Roslyn 生成的 ports/mapping 替代手写样板。

---

## 5. 注释规范

> **核心哲学**:关键逻辑不落空,废话半句都不多。
> 注释只解释 **「为什么 (Why)」** 与 **「意图 (Intent)」**,不翻译 **「是什么 (What)」**。

### 准则一:意图导向,拒绝"废话文学"

不要把 C# 语法翻译成中文。注释要说明这段代码在 **业务链路** 或 **架构约束** 中的真实作用。

❌ 翻译语法:
```csharp
// 如果 range 无效,就 return
if (!range.IsValid) return;
```

✅ 阐述意图:
```csharp
// 💥 0-GC 短路拦截:数据不足或未初始化时直接退帧,防止无效计算击穿底层渲染
if (!range.IsValid) return;
```

### 准则二:死磕"魔法数字"与数学公式

任何脱离上下文的常量(如 `0.5`、`12`)或复杂数学,**必须**配推导注释。

❌ 不明所以的常量:
```csharp
double padding = maxDiff * 1.1;
```

✅ 标注物理意义:
```csharp
// 叠加 10% 安全边距 (PaddingRatio),防止极值点紧贴画板上下边缘
double padding = maxDiff * (1 + PaddingRatio);
```

### 准则三:标明"架构红线"与"物理约束"

违背常规直觉的写法(为遵守 Hooks 铁律 / 绕系统 bug / 极致优化)必须用 `// 💥` 醒目标记。

```csharp
// 💥 铁律:必须在顶层无条件解包 UsePort!严禁放入分支,防止触发 HEVO003 游标错乱
var (range, _) = ctx.UsePort(RangePort);
```

### 准则四:解释"响应式依赖"的触发边界

使用 `UseDynamicMemo` / 响应式流时必须简要说明 **什么情况下会重算**。

```csharp
// 缓存刻度。依赖 plotArea 和 range,当且仅当窗口拉伸或数据量程变化时触发重算
var (ticks, ticksChanged) = ctx.UseDynamicMemo("AxisTicks", (range, plotArea), deps => { ... });
```

### 自检清单

- [ ] 是否有"翻译"语法?(有则删)
- [ ] 魔法数字是否标注了物理含义?
- [ ] 复杂的 `if` 是否有业务理由?
- [ ] Hooks 调用位置(`UsePort` / `UseDynamicMemo` 等)是否加了"铁律"警示?
- [ ] 生命周期敏感代码(`OnSuspend / OnResume / Dispose`)是否注明 "Phase 11 / §H" 并说明为何需要手动处理?

---

## 6. AI 协作唤醒词

新任务起手用以下唤醒词锁定风格:

> 请按 Hevo.Charting 项目规范执行:严守 .NET 5 兼容(SDK 5.0.408 红线,见 README §2)、`00.架构协议` 全部铁律、`Environment/Axes/Series/Interactions` 四段 builder 装配、注释只阐述 Why/Intent。

---

## 7. 文档地图

- `README.md`(本文件)— 项目规范、注释守则、AI 协作准则
- `00.Hevo.Charting 架构协议与开发规范.md` — 架构红线、铁律、SOP、cookbook(主参考)
- `01.业务Feature设计.md` — 业务侧 Feature 拆解模式
- `04.交互层架构.md` — 鼠标 / 键盘 / 滚轮 → 视口数学
- `06.WPF 渲染规约.md` — 像素对齐契约 / snap 实现 / WPF 后端性能路线图
- `Renderer.md` — 接入新渲染后端的指南
- `TODO.md` — 触发条件 + 改动方案的技术债
- `LowCode/Designer/低代码.md` — 蓝图编译器与 ChartBlueprint
