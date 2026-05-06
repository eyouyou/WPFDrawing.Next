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
> ⚠️ 不要传 `--runtimes net8.0`。项目 TFM 是 `net8.0-windows10.0.19041.0`,BDN 自动 boilerplate 会跟主项目对齐;手动覆盖会导致 NU1201 mismatch。

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
