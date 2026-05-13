# Hevo.Charting TODO

性能优化、架构清理、待还的技术债。每条都是触发条件 + 改动方案 + 触发后的实施步骤。

---

## ✅ 已完成:Skia 引用移除（commit 8350acf）

**触发原因**:Skia 路径在全屏 + 特定 canvas size 下出现 ±1 列错位 bug（SkiaSharp.Views.WPF 多个图层栅格化路径都没法绕开）。已经全员切到 WPF Software 并且通过 `_drawingCanvas.CacheMode = BitmapCache` 拿回了 perf,Skia 引用整体可拔。

**改动清单(全部完成)**:
- ✅ 删 `Renderers/SkiaRenderer.cs` / `Renderers/SkiaRasterRenderer.cs`
- ✅ `Renderers/RenderProvider.cs` 去 `SkiaRenderProvider`
- ✅ `Core/ChartCell.cs` 去 `_skiaElement` / `_skiaProvider` / `OnSkiaPaintSurface` / `_hardwareLayers` 全部相关引用
- ✅ `Hevo.Charting.csproj` 去 `SkiaSharp` / `SkiaSharp.Views.WPF` PackageReference
- ✅ `IDrawingSink.PushPixelSnapping` + `DrawOp.PushGuidelineSet` 一并删除(本就是 Skia 时代的 hack)

**API 兼容**:`RenderMode.Hardware` 枚举值保留(避免下游枚举值消失编译错),但路由到 Software。详见 [06.WPF 渲染规约.md](06.WPF%20渲染规约.md) §1 / §15。

---

## ⏳ 待触发:动态 CandleLayer 路由到独立 host

**触发条件**(任意一条):
- 心跳/tick 频率 ≥ 5 Hz
- BitmapCache 重建单次 ≥ 5ms
- hover 期间出现节奏跟心跳一致的可见卡顿

**背景**:[CandleFeature.cs:107-109](Features/CandleFeature.cs:107) 已经做了冷热分离 (`_staticLayer` + `_dynamicLayer`),但两个都进 `_drawingCanvas`(Level=Main)。`_drawingCanvas` 挂了 BitmapCache → dynamic 任何改动都让缓存整体失效 → 历史 9999 根蜡烛重新栅格化。冷热分离的红利吃不到。

**方案 A(推荐,5 行)**:
```csharp
// CandleFeature.cs
private readonly CandleLayer _dynamicLayer = new() { Level = ChartLayerType.Interaction };
```
ChartCell 路由器自动把它送进 `_overlayCanvas`(无 cache,跟 Crosshair / Tooltip 同 host)。心跳改动不污染 `_drawingCanvas` 缓存。

**潜在副作用**:dynamic K 线 z-order 跟 Crosshair 同层。如果业务希望 Crosshair 必须盖住 K 线,需要调整 `_overlayVisualRegistry` 插入顺序,或走方案 B。

**方案 B(架构纯净,~50 行)**:加第三个 canvas `_hotCanvas` + `ChartLayerType.Hot` 新枚举值。z-order 干净。
- ChartCell 加 `_hotCanvas` + `_hotVisualRegistry`
- `ChartLayerType` 新增 `Hot = 15`(在 Main 之后、Selection 之前)
- 路由器扩展为四分支:Hardware / Hot / Interaction / 其它
- CandleFeature 改 `_dynamicLayer.Level = Hot`

实现量是 A 的 ~10 倍。先跑 A,不够再升 B。

**验证方法**:跟 `[FrameGap]` 同款 stopwatch,只在 `_drawingCanvas.CacheMode != null` 重建时打印,对比改动前后 gap。

---

## ⏳ 待触发:Crosshair 延迟进一步压低

**触发条件**:用户反馈 hover 仍有微感延迟(目前已经做过 Dispatcher.InvokeAsync 跳过的 sync 优化)。

**当前架构**:
- mouse move (UI 线程) → board write → RequestUpdate → 同步订阅 `CompositionTarget.Rendering` → 下一帧 vsync 跑管线 → WPF 上屏 (vsync+1 帧)
- 总延迟 ≈ 1-2 vsync(16-32ms)

**进一步优化方向**:
1. **同步直渲**:mouse move handler 内,在写完 PointerHitPort 之后,直接同步 `ExecutePipeline + Invalidate`,跳过 `CompositionTarget.Rendering` 这一跳。需要解决:管线在 board write 锁内被同步调用的可重入问题。
2. **Crosshair 走 Skia**(如果将来 Skia 回归):crosshair 是 thin line,不会触发 grid ±1 列 bug。Skia bitmap 隔离 + GPU 路径,延迟和帧率都能拿到最优。
3. **自定义 UIElement 替代 DrawingVisual**:Override `OnRender(DrawingContext)` 自己控制 invalidation 粒度,缩 dirty bbox 到 crosshair 实际像素带宽。

---

## ⏳ 待触发:静态层缓存策略需要业务级灵活性

**触发条件**:出现 tick 行情 schema 或者超低 N 的展示型 schema,默认 `StaticCachePolicy=On` 不再合适。

**已实施**([ChartCell.cs](Core/ChartCell.cs)):
- 三档枚举 `StaticCachePolicy.On / Off / Adaptive`
- Adaptive 滞回阈值 `StaticCacheEnableThreshold` (500) / `StaticCacheDisableThreshold` (200)
- `ViewportManagerFeature` 自动调 `chart.ReportVisibleDataCount()`,业务无感

**未实施扩展点**:
- 命中率统计:跟踪过去 1s 内缓存复用 vs 重建比例,完全自适应,不需要业务配阈值
- per-layer-group 缓存:不同 layer group 各自独立的 cache canvas(目前所有 background-level static 都共一个 `_drawingCanvas`)

---

## ⏳ 待还的技术债

### ✅ 已完成:Grid pixel asymmetry workaround
1.5px 妥协是 Skia ±1 列错位 bug 的临时方案。Skia 拔除 + §14 vertex snap + AA on 路径已能正常处理 1px H/V 线,业务侧 [DualTimeShareSchemaBase.cs:91](../Hevo.Drawing/TimeShare/DualTimeShareSchemaBase.cs:91) 已恢复 `LineStyle.FromResource(BrushKeys.L1, 1.0)`。`GridStyleTrait.Create / FromResource` 默认值本来就是 `thickness=1.0`,无需进一步改动。

### `_overlayVisualRegistry` 插入排序
当前 `AddUnmanagedLayer` 对 `_overlayVisualRegistry` 走 Level 升序,但 overlay 内只有 Crosshair 和 TooltipWidget 都是 Interaction(同 Level),实际 Z-order 由插入顺序决定。如果将来动态 CandleLayer 也进 overlay(方案 A),要明确 Z 序规则。

### `dc.DrawRectangle` 逐 rect vs 批量
[DrawingRenderer.cs](Renderers/DrawingRenderer.cs) 的 `DrawRectangles` 当前逐 rect 调用,是为了规避 StreamGeometry 多 figure + isClosed=true 在 WPF 下的红色幻线伪影。如果将来发现批量是性能瓶颈,可探索 `GeometryGroup` of `RectangleGeometry`(每个 rect 独立 Geometry,绕开 figure 间幻线)的批量方案。

### 移动端 / 跨平台
当前 WPF-only,没有 Avalonia / MAUI 路径。如果需要跨平台,框架的 `IRenderer<TBuffer, TContext>` 接口设计支持新增 backend,但需要为每个 platform 实现 RasterRenderer + DrawingRenderer。
