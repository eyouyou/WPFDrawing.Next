# Hevo.Charting.Benchmarks

低代码蓝图子系统优化方案 (§1–§6) 的量化基准。

## 跑

```bash
# 全部 benchmark
cd Hevo.Charting.Benchmarks
dotnet run -c Release -- --filter "*"

# 单组
dotnet run -c Release -- --filter "*ReflectionVsCompiled*"
dotnet run -c Release -- --filter "*BlueprintEndToEnd*"

# 加快/减少迭代 (粗看用)
dotnet run -c Release -- --filter "*" --warmupCount 3 --iterationCount 5
```

> ⚠️ **必须 Release 配置**。Debug 数据没意义。
> ⚠️ 不要传 `--runtimes net10.0`。项目 TFM 是 `net10.0-windows10.0.19041.0`;BDN 0.14 不认识 .NET 10,
> 按宿主 runtime 生成的 boilerplate 是 `net10.0`,引用本项目会 NU1201。`Program.BenchConfig` 已把默认 job 的工具链
> 钉到 `net10.0-windows10.0.19041.0`(`--job dry/short/medium/long` 也在那里映射),不要再手动覆盖。

## 实测结果 (.NET 10.0.12, Win11, 5 warmup / 10 iter)

测量机:AMD Ryzen 9 9950X3D(16C/32T),BenchmarkDotNet 0.14.0,2026-10-08。
"旧数据"列是此前 .NET 8.0.26 的实测,**机器不同**,两列差异是 runtime + 硬件的合计,只看量级和相对排序。

### §3 属性 setter 编译化 ([SetterReflectionVsCompiledBenchmarks](ReflectionVsCompiledBenchmarks.cs))

| 路径 | Mean | Allocated | Ratio | 旧数据 (.NET 8) |
|---|---:|---:|---:|---:|
| `PropertyInfo.SetValue` (含 GetProperty,模拟旧 SmartActivator 路径) | 20.4 ns | 40 B | 1.00 (baseline) | 121 ns |
| `SmartActivator.InjectProperties` (编译 setter 缓存) | **14.2 ns** | **0 B** | **0.70** | 58 ns |
| `PropertyInfo.SetValue` (无 GetProperty,直接写) | 6.7 ns | 0 B | 0.33 | 33 ns |

**结论**:编译 setter 仍比"GetProperty + SetValue"快且零分配,但 .NET 10 下反射路径本身快了很多,
差距从 2.1× 缩到 1.4×。50 Feature × 5 prop = 250 次注入:旧路径 ~5μs + 10KB / 新路径 ~3.6μs + 0B,
收益主要剩"零分配"。

### §4 Seed 编译委托缓存 ([SeedDispatchReflectionVsCompiledBenchmarks](ReflectionVsCompiledBenchmarks.cs))

| 路径 | Mean | Allocated | Ratio | 旧数据 (.NET 8) |
|---|---:|---:|---:|---:|
| `MakeGenericMethod + Invoke` (每次) | 116.6 ns | 232 B | 1.00 (baseline) | 589 ns |
| 缓存的 `Action<IFeatureContext, object>` | **6.6 ns** | **0 B** | **0.06 (17.6× faster)** | 38 ns |

**结论**:Seed dispatch 的核心成本是 `MakeGenericMethod` 跟 `Invoke` 的 box arg 数组分配。
编译委托 ~18× 快 + 完全消除 232B/调用的分配。InitialTraits 多于 5 个的蓝图收益最明显。

### §2 ctor 编译委托 ([CtorReflectionVsCompiledBenchmarks](ReflectionVsCompiledBenchmarks.cs))

| 路径 | Mean | Allocated | Ratio | 旧数据 (.NET 8) |
|---|---:|---:|---:|---:|
| `Activator.CreateInstance(type)` | **4.2 ns** | 24 B | 1.00 (baseline) | 14 ns |
| `ComponentRegistry.CreateInstance(type)` (编译委托缓存) | 5.3 ns | 24 B | 1.24 (slower) | 25 ns |

**结论 (诚实交代)**:.NET 8 / 10 的 `Activator.CreateInstance(Type)` 内部已经做得很好,
我们的 `ConcurrentDictionary.GetOrAdd → Func<object> invoke` 仍比它慢约 1ns(.NET 8 上是 11ns)。

但这是**空 POCO** 的微基准,放大了 dispatch 比例。真实业务里:
- LineSeriesFeature 的 ctor 本身 ~50μs(各种字段、PortGenerator 注册),dispatch 占比 <1%,
  反射 vs 编译差异淹没在 ctor 自身工作里 —— 改造无害也无观察收益。
- 优化的真正价值在**避免反射通道在 .NET 5 / 老运行时上的不稳定**(README §2 兼容线),
  以及统一 dispatch 路径让上层 (SmartActivator / BlueprintLauncher) 代码更干净。

**保留这条优化**,因为:① 一致性 ② 在更老 runtime 上 Activator 没这么快 ③ 自带缓存防 ctor lookup 重复。

### §1 端口元数据缓存 + §5 DryRun ([BlueprintEndToEndBenchmarks](BlueprintEndToEndBenchmarks.cs))

50 Feature 蓝图实测:

| Benchmark | Mean | Allocated | 旧数据 (.NET 8) |
|---|---:|---:|---:|
| `CreateNode_50Features` (端口扫描走缓存命中) | **3.6 μs** (~70 ns/node) | 14.5 KB | 15-17 μs / 14 KB |
| `DryRun_50Features` (诊断 50 Feature 的端口冲突 / 未焊接) | **59.5 μs** (~1.2 μs/feature) | 79.3 KB | 110-121 μs / 23 KB |

**结论**:
- §1 的 NodePortCache 让端口扫描收敛到 ConcurrentDictionary 查找 + Node 实例分配,
  ~70ns/node 主要是 Guid 生成 + Properties dict alloc,反射开销几乎消失。
- §5 的 DryRun 在 50 Feature 蓝图上 ~60μs 一次,可以接受 —— Launcher 入口跑一次诊断,
  把"加载成功但黑屏"的隐性故障早期暴露出来,RoI 极高。
- ⚠️ DryRun 分配从旧记录的 23KB 涨到 79KB(耗时反而降了),原因未查;旧数据不是同一台机器同一提交,
  需要在同一机器上对比旧提交确认是不是回归。
- §8 数组化后 DryRun 不变(在噪声内),证明新格式没拖慢 hot path。

### §D2.X Python marshalling round-trip ([PythonMarshallingBenchmarks](PythonMarshallingBenchmarks.cs))

ROM&lt;double&gt; ↔ numpy.ndarray round-trip 跨边界开销,4 个 size:1 / 100 / 1000 / 10000 点。

```bash
dotnet run -c Release --project Hevo.Charting.Benchmarks -- --filter "*PythonMarshalling*"
```

⚠️ **InProcess 必须开**(已在文件里 `[Config(typeof(InProcessConfig))]` 配好):BDN 默认 spawn 子进程,
子进程 BaseDirectory 不在 repo 内 → ResolveDll 找不到 Python312/python312.dll → GlobalSetup throw → benchmark NA。
InProcess 让 benchmark 在主进程内跑(共享 PythonEngine 全进程 single-init 状态)。

**实测**(.NET 10.0.12,Ryzen 9 9950X3D,Python 3.12;原先的预估一并列出):

| size | Mean | Allocated | 原预估 Mean |
|---|---:|---:|---:|
| 1 点 | **6.0 μs** | 1.6 KB | ~50-100 μs |
| 100 点 | **6.4 μs** | 2.4 KB | 同上 |
| 1000 点(time-share 典型) | **8.3 μs** | 9.4 KB | ~60-120 μs |
| 10000 点 | **10.4 μs** | 79.7 KB | ~80-150 μs |

**结论**:
- 实测比预估低一个数量级。固定成本(GIL + Marshal + 调度)约 6μs,数据量从 1 到 1 万点只多出约 4μs,
  分配随点数线性增长(1 万点约 80KB,已接近 LOH 阈值)。
- 指标算子层(1-10Hz)完全够用。按实测,单次 round-trip 约占 60Hz 帧预算(16.7ms)的 0.04-0.06%,
  原来"热路径每帧 6-9% 帧预算"的判断不成立;但这个基准是单线程、无 GIL 竞争、Python 侧不做计算的纯搬运,
  §D2.X 的热路径划线要不要放宽,得拿真实算子 + 并发场景再测。
- zero-copy(待 D2.5.3 优化)真正受益的 case 是 10K+ 点指标,那时 Marshal.Copy 和 80KB 分配才显著。

### §8 PortBindings 解析 ([PortBindingValueBenchmarks](PortBindingValueBenchmarks.cs))

5 个 globalId 的扇入端口三种输入形态对比:

| Benchmark | Mean | Allocated | Ratio | 旧数据 (.NET 8) |
|---|---:|---:|---:|---:|
| `ExtractList: CSV 5 ids (老格式)` | 72.9 ns | 424 B | 1.00 (baseline) | 610 ns / 568 B |
| `ExtractList: List<string> 5 ids (新格式)` | **40.5 ns** | **192 B** | **0.56 (1.8× faster, 55% 少分配)** | 388 ns / 336 B |
| `ExtractList: single string (退化)` | 4.4 ns | 32 B | 0.06 | 30 ns |
| `ExtractSingle: string (单端口典型)` | **≈0 ns** (低于计时分辨率) | **0 B** | - | 5 ns |

**结论**:新数组格式比老 CSV 1.8× 快 + 减 55% 分配 —— 省了一次 `string.Split + Trim + Where` 链的临时对象。
但绝对值只在几十 ns 量级,落到 50 Feature 蓝图整体 ~60μs 流程里贡献 <5%,优化主要价值在
**JSON 可读性 / diff 友好 / AI 生成正确率**,perf 是顺手红利。

## 总评

| § | 描述 | 实测收益 (.NET 10) | 评估 |
|---|---|---|---|
| §1 | 端口元数据缓存 | ~70 ns/node (反射开销近消失) | ✅ 编辑器手感 |
| §2 | ctor 编译委托 | 微基准 +1 ns(略慢于 Activator) | ⚠️ 维持代码一致性,无业务收益 |
| §3 | setter 编译化 | **1.4× faster + 0 alloc**(.NET 8 上 2.1×) | ✅ 蓝图加载阶段 GC 减压 |
| §4 | Seed 编译委托 | **17.6× faster + 0 alloc** | ✅ 高 Trait 蓝图明显提速 |
| §5 | DryRun 早期诊断 | 50 Feature ~60μs(分配 79KB 待查) | ✅ 调试时间省分钟级 |
| §7 | JsonConverter | 2 个 leaf converter 撑住整个 trait 树 | ✅ AI 生成 / diff 友好 |
| §8 | PortBindings 数组化 | 老 CSV → 新数组,**1.8× faster + 55% 少分配** | ✅ JSON 可读性主升,perf 顺手红利 |

实事求是:
- **§3 §4 §8 是真实 perf 优化**,§7 是 AI / 可读性优化,§5 是工程价值,§1 是编辑器交互优化
- **§2 在 .NET 8 / 10 上意义有限** (.NET 5 / 老 runtime 上仍有意义,且统一了 dispatch 路径)
- **§3 的收益随 runtime 进步在缩小**:.NET 10 反射 setter 已经很快,剩下的主要是零分配
- **§7 的"少做"**:`LineStyle` / `AxisStyleTrait` 不需要单独写 converter ——
  顶层多态 (IHevoBrush) + 叶子值类型 (Color) 各自有 converter,中间普通 record 由 System.Text.Json
  默认 primary ctor 反序列化处理,组合即可。这把"每个 trait 单独 converter"的工作量从 "n 个" 降到 "2 个"。

## 结果产物路径

每次跑后,markdown / csv / html 在 `BenchmarkDotNet.Artifacts/results/` 下:

```
Hevo.Charting.Benchmarks.SetterReflectionVsCompiledBenchmarks-report-github.md
Hevo.Charting.Benchmarks.SeedDispatchReflectionVsCompiledBenchmarks-report-github.md
Hevo.Charting.Benchmarks.CtorReflectionVsCompiledBenchmarks-report-github.md
Hevo.Charting.Benchmarks.BlueprintEndToEndBenchmarks-report-github.md
Hevo.Charting.Benchmarks.PortBindingValueBenchmarks-report-github.md
Hevo.Charting.Benchmarks.PythonMarshallingBenchmarks-report-github.md
Hevo.Charting.Benchmarks.SubmitSyncBenchmarks-report-github.md        # 增量核心组,见下文
Hevo.Charting.Benchmarks.ProjectAllBenchmarks-report-github.md
Hevo.Charting.Benchmarks.FrameScalingBenchmarks-report-github.md
Hevo.Charting.Benchmarks.BlackboardTransactionBenchmarks-report-github.md
```

## 增量渲染前后对比 (`--render-probe`, [RenderProbe](RenderProbe.cs))

不是 BenchmarkDotNet:要真窗口和完整 WPF 管线,所以单独一个入口。只测 **UI 线程的 CPU 管线**;
渲染线程合成上屏和输入延迟见下一节 `--latency-probe`。

```bash
cd Hevo.Charting.Benchmarks
dotnet run -c Release -- --render-probe                                  # 2000 根 × 1 张图,全部场景和模式,5 轮
dotnet run -c Release -- --render-probe --bars=2000,20000,100000          # 数据量伸缩曲线
dotnet run -c Release -- --render-probe --charts=1,4,9 --modes=inc,full,inc-par,full-par   # 多图并行 + PlotMode.Parallel
dotnet run -c Release -- --render-probe --scenarios=Hover,Zoom --rounds=10 --out=out/probe.csv
```

| 参数 | 默认 | 说明 |
|---|---|---|
| `--bars=` | 2000 | K 线根数,逗号分隔可扫多个 |
| `--charts=` | 1 | 同一窗口里的图数(UniformGrid 排布,各自独立 schema / 黑板),逗号分隔可扫多个 |
| `--steps=` / `--warmup=` / `--rounds=` | 300 / 100 / 5 | 每轮每组合的测量帧数 / 预热帧数 / 轮数 |
| `--scenarios=` | 全部 + Startup | `Hover,Pan,Zoom,Tick,Append,Resize,Startup`,`all` = 全部 |
| `--modes=` | 全部 | `inc,nobag,layerfull,featfull,full,inc-par,full-par` |
| `--window=` | 1280x720 | 图表区域尺寸(定在内容上,窗口 SizeToContent) |
| `--out=` | render-probe.csv | 逐帧明细;同名 `.md` 汇总表、`.json` 汇总数据 |
| `--baseline=` / `--write-baseline=` / `--tolerance=` | - / - / 0.02 | 计数回归门槛(见下面 CI 一节) |
| `--ci` | | CI 预设:只跑 `inc`,120 帧 × 1 轮,图表区 960x540,无 Startup |
| `--alloc-types` | | 进程内订阅 GCAllocationTick,按类型 + SOH/LOH 汇总每帧分配(UI 线程,含脚本输入),追加到 `.md`。用来查"分配/帧"的大头 |

**图表**:跟 LowCodeDemo `KLineMainSchema` 同款装配(蜡烛 + SMA20 + 时间轴 + 价格轴 + 联动头 + 标准交互),
数据是固定种子的随机游走,黑板由脚本直接写。

**场景**(每步改一次黑板,再手动跑一帧 `ChartCell.RunFrameNow`,跟 `CompositionTarget` 回调里那一帧是同一段代码,
含 `RequestUpdate` 排队的事务,不受 VSync 节奏影响):

| 场景 | 每步做什么 |
|---|---|
| Hover | 十字光标在绘图区横扫,每步 3px,写 `PointerHitPort`(命中计算复刻 `ChartInteractionFeature`) |
| Pan | 平移 1 根 K 线,写 `Viewport.UserRange`,每 100 步换方向 |
| Zoom | 滚轮缩放:右缘锚定,可见跨度每步 ×1.1,在 60 根和 min(数据量, 20000) 根之间来回 |
| Tick | 最后一根 K 线收盘价跳动,`ForceWrite` High/Low/Close |
| Append | 实时追加:每步新收一根 K 线,所有序列长度 +1,视口跟随最新一根 |
| Resize | 图表宽度每步变 8px(走真实 WPF 布局 → `SizeChanged` → 环境纪元 → FullPass) |
| Startup | 单独成表:新建窗口 → 模板装配(ComposeAll)→ 写数据 → 首帧出图,冷启动(首次,含 JIT)和热启动分开报 |

**模式**(`DevTools/IncrementalRenderProbe` 的静态开关,默认全关,关着时行为跟原来一致):

| key | 模式 | 含义 |
|---|---|---|
| inc | 增量(默认) | 现状 |
| nobag | 去掉Bag短路 | 关掉 `RenderContext.SubmitSync` 的 Bag 级短路,图层仍做引用比对。验证短路只是省 CPU,不改变重绘集合 |
| layerfull | 图层侧全重绘 | 已发现的图层每帧都重录(关掉 `VisualDependencyTracker` 判脏) |
| featfull | Feature侧全量 | 每帧都当作环境纪元变化,所有 Feature 重投影、`UsePort` 全部视为变脏 |
| full | 全量(两侧都关) | 上面两项同时打开,相当于没有增量机制 |
| inc-par / full-par | +Parallel | 同 inc / full,但图层录制走 `PlotMode.Parallel`(脏图层 ≥3 时 `Parallel.ForEach`) |

**统计与标准化**:
- 进程 High 优先级、UI 线程 Highest;每个组合先预热一遍,再歇 300ms 让后台 Tier1 编译落地。
- 每个组合跑 `--rounds` 轮,轮与轮之间穿插(第 1 轮的所有组合 → 第 2 轮 ...),机器状态漂移对各组合影响一致。
- 帧耗时报"各轮中位数的均值 ± 95% 置信区间"(t 分布),另报全部样本的 P95 / P99;每千帧 Gen0/1/2 GC 次数。
- 计数(Feature 重算/帧、图层重录/帧、绘制命令/帧)各轮应完全一致,不一致的会标 ⚠。
- 开头打印测量环境:CPU、GPU、WPF RenderTier、是否远程桌面、刷新率、DPI。**远程桌面下 WPF 走软件渲染**,
  CPU 管线数字仍可比,但不代表本机显示器 + GPU 的体验,正式数据请在本机直接显示时跑。

"帧耗时"只算 UI 线程这一帧(排队事务 + Feature 投影 + 判脏 + 图层录制 + 上屏指令派发),
不含 WPF 渲染线程合成和 GPU 时间;"输入耗时"是写黑板 / 改尺寸本身,含同步 `Watch` 副作用(视口钳位、自动量程)和布局。

> ⚠️ 必须 Release。DEBUG 下每个 schema 都挂拓扑追踪器,每次读写都有记录开销。

### 实测 (render-probe)

> ⚠️ **以下实测数据的环境**:Ryzen 9 9950X3D,.NET 10.0.12,2026-10-08,**远程桌面会话**
> (GPU = Microsoft Remote Display Adapter,WPF **RenderTier 0 软件渲染**,刷新率 32Hz)。
> render-probe 只测 UI 线程 CPU 管线,数字可比;**本机直显 + GPU 下的数据和 `--latency-probe` 结果待补**。

**2000 根 × 1 张图**(默认参数,300 帧 × 5 轮,1280x720;帧耗时 = 各轮中位数的均值,单位 ms;LOD 优化前测得,Zoom 的前后对比见下文):

| 场景 | 增量 帧耗时 | 全量 帧耗时 | 增量 Feature重算 / 图层重录 每帧 | 全量 Feature重算 / 图层重录 每帧 | 增量 分配/帧 |
|---|---:|---:|---:|---:|---:|
| Hover | **0.030** | 0.112 | 3 / 2 | 12 / 10 | 14.6 KB |
| Pan | **0.052** | 0.073 | 3 / 5.1 | 12 / 10 | 61.8 KB |
| Zoom | 0.101 | 0.100 | 3.3 / 5.5 | 12 / 10 | 209 KB |
| Tick | **0.020** | 0.064 | 1 / 2 | 12 / 10 | 40.0 KB |
| Append | **0.047** | 0.064 | 6.2 / 7.4 | 12 / 10 | 69.8 KB |
| Resize | 0.061 | 0.060 | 12 / 10 | 12 / 10 | 80.5 KB |

- Resize 走环境纪元 FullPass,增量和全量本来就一样。
- `inc-par` / `full-par`(PlotMode.Parallel)在所有组合里都没有收益,全量时略慢(单图 10 个图层,并行调度开销盖过收益)。
- Startup:冷启动 装配 167 ms / 首帧 49 ms;热启动 装配 14.3 ms / 首帧 0.94 ms(2000 根,10 个图层)。

**数据量伸缩**(`--bars=2000,20000,100000`,3 轮,增量 / 全量,帧耗时 ms,K 线 LOD 优化后):

| 场景 | 2000 根 | 2 万根 | 10 万根 |
|---|---:|---:|---:|
| Tick | 0.018 / 0.069 | 0.018 / 0.066 | 0.018 / 0.062 |
| Pan | 0.113 / 0.110 | 0.042 / 0.063 | 0.061 / 0.073 |
| Zoom | 0.085 / 0.098 | 0.215 / 0.227(P99 0.49) | 0.195 / 0.214(P99 0.52) |

Tick / Pan 跟数据量无关(只碰最后一根 / 可见窗口固定 ~120 根)。Zoom 的可见跨度在 60 根和 min(数据量, 2 万) 根之间来回,
大跨度帧由下面的按像素列 LOD 兜住。

**K 线 / 折线按像素列 LOD**([PixelColumnLod](../Hevo.Charting/Renderers/PixelColumnLod.cs)):
单根宽度不足 1 个物理像素(按 DPI 换算)时,CandleLayer 把落在同一物理像素列的 K 线聚合成一根(首开、最高、最低、末收,
涨跌色按聚合后的开收),LineLayer 对折线做 M4 抽稀(每列留首 / 最低 / 最高 / 末),输出量上限 ≈ 绘图区物理宽度;
≥1px 时走原逐根路径,行为不变。优化前 2 万根可见时每帧上 MB 的分配几乎全是 WPF 内部缓冲
(2 万 figure 的影线 StreamGeometry、2 万次 `DrawRectangle` 的 RenderData 和依赖资源列表),单个最大 1.4 MB 进 LOH。

同参数前后对比(`--bars=2000,20000,100000 --scenarios=Zoom,Pan,Tick --modes=inc,full --rounds=3 --alloc-types`,增量模式):

| Zoom | 帧耗时中位 | P95 | P99 | 分配/帧 | 其中 LOH | Gen0/1/2 每千帧 |
|---|---:|---:|---:|---:|---:|---:|
| 2000 根 优化前 → 后 | 0.139 → 0.085 | 0.47 → 0.34 | 0.75 → 0.44 | 213 → 185 KB | - | 6.7/3.3/3.3 → 3.3/0/0 |
| 2 万根 优化前 → 后 | 0.221 → 0.215 | **3.08 → 0.44** | **3.90 → 0.49** | **1066 → 291 KB** | **~730 → ~41 KB**(单个 ≤86 KB) | **70/67/67 → 3.3/0/0** |
| 10 万根 优化前 → 后 | 0.200 → 0.195 | **2.96 → 0.45** | **4.15 → 0.52** | **1067 → 293 KB** | 同上 | **38/34/34 → 3.3/0/0** |

- 中位数基本不变:Zoom 一半以上的帧可见跨度只有几十到几百根,本来就不触发 LOD;收益集中在大跨度帧的尾部(P95/P99)和 GC。
- Pan / Tick 前后一致(帧耗时、计数、分配都在噪声内),LOD 不影响常规缩放级别。
- 剩下的 ~41 KB/帧 LOH 是 ~1280 列实体的 RenderData 缓冲刚好越过 85 KB 阈值;Gen2 已归零,暂不再压。
- CI 计数基线随之更新:只有 `Zoom|inc` 的绘制命令/帧 22.17 → 22.73(命令数按批次计,跟根数无关),其余组合不变。
**多图**(`--charts=1,4,9`,Hover,3 轮,增量 / 全量,帧耗时 ms):1 图 0.067 / 0.326,4 图 0.083 / 0.245,9 图 0.192 / 0.504。

完整表(含 P95/P99、GC、置信区间)见 `--out=` 输出的 `.md`。

## 输入到画面的端到端延迟 (`--latency-probe`, [LatencyProbe](LatencyProbe.cs))

```bash
dotnet run -c Release -- --latency-probe [--samples=150] [--bars=2000] [--out=latency-probe.csv]
```

置顶窗口里放一张 K 线图,后台线程用 `SendInput` 注入**真实的系统鼠标输入**(移动 → 十字光标,滚轮 → 缩放),
然后不停用 GDI `BitBlt` 抓屏幕上绘图区里的两行像素直到像素变化。屏幕抓到的是 DWM 合成后的桌面,
所以这是"输入进系统 → 画面真的变了"的时间。两个 UI 线程钩子把总时长切成三段:

| 阶段 | 起点 → 终点 | 包含 |
|---|---|---|
| ① | `SendInput` → `Window.PreviewMouseMove/Wheel` | 系统输入队列 + Dispatcher 调度 |
| ② | UI 线程收到 → 这一帧 UI 线程做完(`IncrementalRenderProbe.FrameRendered`) | 写黑板 → 等下一个 `CompositionTarget.Rendering` → 管线 |
| ③ | 帧做完 → 屏幕像素变化 | **WPF 渲染线程合成 + Present + DWM 合成**,外加抓屏轮询粒度(报告单列) |

每次输入前随机等 80–96ms,让输入时刻跟 VSync 相位错开,得到的是分布而不是固定相位。

> ⚠️ 必须本机直接显示(不要远程桌面),测量约 30 秒,期间不要碰鼠标、不要切窗口。

**实测:待补**。2026-10-08 那轮测量机是远程桌面会话(RenderTier 0),按上面的要求跳过了,需要在本机直显时补跑。

## 增量核心路径微基准 ([IncrementalCoreBenchmarks](IncrementalCoreBenchmarks.cs))

```bash
dotnet run -c Release -- --filter "*SubmitSync*" "*ProjectAll*" "*FrameScaling*" "*BlackboardTransaction*"
```

| 基准 | 参数 | 场景 |
|---|---|---|
| `SubmitSyncBenchmarks` | 图层数 8 / 32 / 128 | Idle(Bag 短路)/ OneLocal(1 层换数据)/ GlobalRepublish(全局有提交但引用不变,满负荷判脏)/ GlobalChanged(全部判脏) |
| `ProjectAllBenchmarks` | Feature 数 8 / 32 / 128 | Idle / OnePort / AllPorts / FullPass |
| `FrameScalingBenchmarks` | 图层数 8 / 32 / 128(每层 1 个 Feature) | 整帧 `RunFrameNow`:Idle / OneDirty / AllDirty |
| `BlackboardTransactionBenchmarks` | 每事务端口数 1 / 8 / 32 | Unchanged(值防抖命中)/ Changed(真实写入 + 订阅标脏 + 弹脏名单);另有锁内读 |

### 实测 (.NET 10.0.12, Ryzen 9 9950X3D, 5 warmup / 10 iter)

BDN 微基准不经过 WPF 渲染线程,跟远程桌面 / RenderTier 无关。单位 ns,括号内为每次分配。

| 基准 | 场景 | 8 | 32 | 128 |
|---|---|---:|---:|---:|
| SubmitSync(图层数) | Idle | 68 (224 B) | 212 | 852 |
| | OneLocal | 140 (664 B) | 327 | 1,108 |
| | GlobalRepublish | 104 | 330 | 1,414 |
| | GlobalChanged | 110 | 344 | 1,339 |
| ProjectAll(Feature 数) | Idle | 33 (224 B) | 36 | 34 |
| | OnePort | 338 | 418 | 738 |
| | AllPorts | 2,235 (±781) | 6,095 | 25,388 |
| | FullPass | 598 | 1,724 | 6,793 |
| FrameScaling(图层数,整帧) | Idle | 107 (224 B) | 335 | 1,359 |
| | OneDirty | 985 | 1,502 | 3,267 |
| | AllDirty | 4,381 (8.4 KB) | 16,487 | 77,381 (122 KB) |

| BlackboardTransaction(每事务端口数) | 1 | 8 | 32 |
|---|---:|---:|---:|
| Transaction / Changed | 122 (48 B) | 805 (384 B) | 3,409 (1.5 KB) |
| Transaction / Unchanged(值防抖命中) | 31 (0 B) | 117 | 434 |
| ReadUnderLock | 13 | 31 | 103 |

- ProjectAll 的 Idle 不随 Feature 数增长;SubmitSync / FrameScaling 的 Idle 仍随图层数线性增长(每层约 7–11 ns),每帧固定分配 224 B。
- 整帧 OneDirty vs AllDirty 在 128 层时差 24×,这是增量机制在核心路径上的收益上限。
- ProjectAll 8 Feature / AllPorts 误差偏大(±781 ns),要用这个数时加大 `--iterationCount` 重测。

## CI 回归门槛 ([.github/workflows/perf-regression.yml](../.github/workflows/perf-regression.yml))

每次 push main / PR:构建 → 单测 → `--render-probe --ci --baseline=render-probe-baseline.json` → 微基准 Dry 冒烟。

门槛**只看确定性计数**(Feature 重算/帧、图层重录/帧、绘制命令/帧),超过基线 ×(1+2%) 即失败;**不看耗时**,
托管 runner 是共享机器 + 软件渲染,耗时噪声太大。计数有意变化时(改了渲染路径):

```bash
dotnet run -c Release -- --render-probe --ci --write-baseline=render-probe-baseline.json
```

或者从 CI 产物 `perf-artifacts/render-probe/render-probe.json` 里取数更新 [render-probe-baseline.json](render-probe-baseline.json)。
