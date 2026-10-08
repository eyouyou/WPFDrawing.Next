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
> ⚠️ 不要传 `--runtimes net10.0`。项目 TFM 是 `net10.0-windows10.0.19041.0`,BDN 自动 boilerplate 会跟主项目对齐;手动覆盖会导致 NU1201 mismatch。

## 实测结果 (.NET 8.0.26, Win11, 5 warmup / 10 iter)

### §3 属性 setter 编译化 ([SetterReflectionVsCompiledBenchmarks](ReflectionVsCompiledBenchmarks.cs))

| 路径 | Mean | Allocated | Ratio |
|---|---|---|---|
| `PropertyInfo.SetValue` (含 GetProperty,模拟旧 SmartActivator 路径) | 121 ns | 40 B | 1.00× (baseline) |
| `SmartActivator.InjectProperties` (编译 setter 缓存) | **58 ns** | **0 B** | **0.48× (2.1× faster)** |
| `PropertyInfo.SetValue` (无 GetProperty,直接写) | 33 ns | 0 B | 0.27× |

**结论**:跟旧的"GetProperty + SetValue"路径比,编译 setter 快 2 倍且零分配。
单次 ~63ns 的节省 + 40B GC 减负看似小,但 50 Feature × 5 prop = 250 次注入累计:
旧路径 30μs + 10KB / 新路径 15μs + 0B,蓝图加载阶段 GC 压力可见下降。

### §4 Seed 编译委托缓存 ([SeedDispatchReflectionVsCompiledBenchmarks](ReflectionVsCompiledBenchmarks.cs))

| 路径 | Mean | Allocated | Ratio |
|---|---|---|---|
| `MakeGenericMethod + Invoke` (每次) | 589 ns | 232 B | 1.00× (baseline) |
| 缓存的 `Action<IFeatureContext, object>` | **38 ns** | **0 B** | **0.07× (15× faster)** |

**结论**:Seed dispatch 的核心成本是 `MakeGenericMethod` 跟 `Invoke` 的 box arg 数组分配。
编译委托 15× 快 + 完全消除 232B/调用的分配。InitialTraits 多于 5 个的蓝图收益最明显。

### §2 ctor 编译委托 ([CtorReflectionVsCompiledBenchmarks](ReflectionVsCompiledBenchmarks.cs))

| 路径 | Mean | Allocated | Ratio |
|---|---|---|---|
| `Activator.CreateInstance(type)` | **14 ns** | 24 B | 1.00× (baseline) |
| `ComponentRegistry.CreateInstance(type)` (编译委托缓存) | 25 ns | 24 B | 1.76× (slower!) |

**结论 (诚实交代)**:.NET 8 的 `Activator.CreateInstance(Type)` 内部已经做得很好,
我们的 `ConcurrentDictionary.GetOrAdd → Func<object> invoke` 反而比它慢 11ns。

但这是**空 POCO** 的微基准,放大了 dispatch 比例。真实业务里:
- LineSeriesFeature 的 ctor 本身 ~50μs(各种字段、PortGenerator 注册),dispatch 占比 <1%,
  反射 vs 编译差异淹没在 ctor 自身工作里 —— 改造无害也无观察收益。
- 优化的真正价值在**避免反射通道在 .NET 5 / 老运行时上的不稳定**(README §2 兼容线),
  以及统一 dispatch 路径让上层 (SmartActivator / BlueprintLauncher) 代码更干净。

**保留这条优化**,因为:① 一致性 ② 在更老 runtime 上 Activator 没这么快 ③ 自带缓存防 ctor lookup 重复。

### §1 端口元数据缓存 + §5 DryRun ([BlueprintEndToEndBenchmarks](BlueprintEndToEndBenchmarks.cs))

50 Feature 蓝图实测:

| Benchmark | Mean | Allocated |
|---|---|---|
| `CreateNode_50Features` (端口扫描走缓存命中) | **15-17 μs** (~300 ns/node) | 14 KB |
| `DryRun_50Features` (诊断 50 Feature 的端口冲突 / 未焊接) | **110-121 μs** (~2.4 μs/feature) | 23 KB |

**结论**:
- §1 的 NodePortCache 让端口扫描收敛到 ConcurrentDictionary 查找 + Node 实例分配,
  300ns/node 主要是 Guid 生成 + Properties dict alloc,反射开销几乎消失。
- §5 的 DryRun 在 50 Feature 蓝图上 110-121μs 一次,可以接受 —— Launcher 入口跑一次诊断,
  把"加载成功但黑屏"的隐性故障早期暴露出来,RoI 极高。
- §8 数组化后 DryRun 不变(在噪声内),证明新格式没拖慢 hot path。

### §D2.X Python marshalling round-trip ([PythonMarshallingBenchmarks](PythonMarshallingBenchmarks.cs))

**MR 加 + 未跑实测**(交付 benchmark 文件 + InProcess 配置;实跑数据交业务侧手动触发)。
ROM&lt;double&gt; ↔ numpy.ndarray round-trip 跨边界开销,4 个 size:1 / 100 / 1000 / 10000 点。

```bash
dotnet run -c Release --project Hevo.Charting.Benchmarks -- --filter "*PythonMarshalling*"
```

⚠️ **InProcess 必须开**(已在文件里 `[Config(typeof(InProcessConfig))]` 配好):BDN 默认 spawn 子进程,
子进程 BaseDirectory 不在 repo 内 → ResolveDll 找不到 Python312/python312.dll → GlobalSetup throw → benchmark NA。
InProcess 让 benchmark 在主进程内跑(共享 PythonEngine 全进程 single-init 状态)。

**预期量级**(开发机 i7 / Win11):

| size | 预期 Mean | 主导成本 |
|---|---|---|
| 1 点 | ~50-100μs | GIL acquire + Marshal.Copy + Task 调度税(数据量不重要) |
| 100 点 | 同上 | 数据 cost ~0.1μs 可忽略 |
| 1000 点(time-share 典型) | ~60-120μs | 数据 ~1μs,marshalling 路径仍主导 |
| 10000 点 | ~80-150μs | 数据 ~10μs 开始可见 |

**结论指引**:
- 每次 invoke ~50-150μs 主要是 GIL + 调度税,数据 size 影响小 → 指标算子层(1-10Hz)完全够
- 60Hz 热路径(crosshair 每帧 16ms)绝对不要走 Python(单次 invoke ~6-9% 帧预算)→ §D2.X 已划线
- zero-copy(待 D2.5.3 优化)真正受益的 case 是 10K+ 点指标,那时 Marshal.Copy 才显著

### §8 PortBindings 解析 ([PortBindingValueBenchmarks](PortBindingValueBenchmarks.cs))

5 个 globalId 的扇入端口三种输入形态对比:

| Benchmark | Mean | Allocated | Ratio |
|---|---|---|---|
| `ExtractList: CSV 5 ids (老格式)` | 610 ns | 568 B | 1.00× (baseline) |
| `ExtractList: List<string> 5 ids (新格式)` | **388 ns** | **336 B** | **0.64× (1.57× faster, 41% 少分配)** |
| `ExtractList: single string (退化)` | 30 ns | 32 B | 0.05× |
| `ExtractSingle: string (单端口典型)` | **5 ns** | **0 B** | 0.008× |

**结论**:新数组格式比老 CSV 1.57× 快 + 减 41% 分配 —— 省了一次 `string.Split + Trim + Where` 链的临时对象。
但绝对值只在几百 ns 量级,落到 50 Feature 蓝图整体 110μs 流程里贡献 <5%,优化主要价值在
**JSON 可读性 / diff 友好 / AI 生成正确率**,perf 是顺手红利。

## 总评

| § | 描述 | 实测收益 | 评估 |
|---|---|---|---|
| §1 | 端口元数据缓存 | 300 ns/node (反射开销近消失) | ✅ 编辑器手感 |
| §2 | ctor 编译委托 | 微基准 -11 ns(略慢于 .NET 8 Activator) | ⚠️ 维持代码一致性,无业务收益 |
| §3 | setter 编译化 | **2.1× faster + 0 alloc** | ✅ 蓝图加载阶段 GC 减压 |
| §4 | Seed 编译委托 | **15× faster + 0 alloc** | ✅ 高 Trait 蓝图明显提速 |
| §5 | DryRun 早期诊断 | 50 Feature 110-121μs | ✅ 调试时间省分钟级 |
| §7 | JsonConverter | 2 个 leaf converter 撑住整个 trait 树 | ✅ AI 生成 / diff 友好 |
| §8 | PortBindings 数组化 | 老 CSV → 新数组,**1.57× faster + 41% 少分配** | ✅ JSON 可读性主升,perf 顺手红利 |

实事求是:
- **§3 §4 §8 是真实 perf 优化**,§7 是 AI / 可读性优化,§5 是工程价值,§1 是编辑器交互优化
- **§2 在 .NET 8 上意义有限** (.NET 5 / 老 runtime 上仍有意义,且统一了 dispatch 路径)
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

## CI 回归门槛 ([.github/workflows/perf-regression.yml](../.github/workflows/perf-regression.yml))

每次 push main / PR:构建 → 单测 → `--render-probe --ci --baseline=render-probe-baseline.json` → 微基准 Dry 冒烟。

门槛**只看确定性计数**(Feature 重算/帧、图层重录/帧、绘制命令/帧),超过基线 ×(1+2%) 即失败;**不看耗时**,
托管 runner 是共享机器 + 软件渲染,耗时噪声太大。计数有意变化时(改了渲染路径):

```bash
dotnet run -c Release -- --render-probe --ci --write-baseline=render-probe-baseline.json
```

或者从 CI 产物 `perf-artifacts/render-probe/render-probe.json` 里取数更新 [render-probe-baseline.json](render-probe-baseline.json)。
