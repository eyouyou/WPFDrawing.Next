# 新增渲染后端 (Renderer Backend) 指南

文档目的:**新接入一个图形后端**(Skia 回归 / Direct2D / OpenGL / Avalonia / 跨平台 GPU 等)时,该改哪些文件、扩展哪些抽象、如何融入 ChartCell 路由器。

写于 Skia 拔除之后(详见 [TODO.md](TODO.md)),当前仓库只剩 WPF DrawingContext 一条 backend。

---

## 框架抽象层

### 核心契约 (`Hevo.Charting.Abstractions/RenderBuffer` / `IRenderer` / `IRendererProvider`)

```csharp
// 1. 渲染缓冲分类(各 backend 都得能消费这三种 buffer)
public abstract class RenderBuffer { /* 命令录像基类 */ }
public class DrawingBuffer  : RenderBuffer { ... }   // 矢量命令(line / rect / path / text)
public class BitmapBuffer   : RenderBuffer { ... }   // 像素位图(预栅格化的 RGBA pixmap)
public class WidgetBuffer   : RenderBuffer { ... }   // WPF 控件(走 InteractionCanvas,跟 backend 无关)

// 2. 渲染器单元接口(一个 backend 给每种 buffer 提供一个实现)
public interface IRenderer<in TBuffer, in TContext> where TBuffer : RenderBuffer
{
    void Render(TBuffer buffer, TContext context);
}

// 3. 后端调度中心:按 buffer 类型路由到对应 IRenderer
public interface IRendererProvider<TContext>
{
    IRenderer<TBuffer, TContext>? GetRenderer<TBuffer>() where TBuffer : RenderBuffer;
}
```

`TContext` 是 backend 的"画布句柄"——WPF 是 `DrawingContext`,Skia 是 `SKCanvas`,D2D 是 `ID2D1RenderTarget`,GL 是 `IGLContext`,等等。

### 现状 (`Renderers/RenderProvider.cs`)

```csharp
public sealed class WpfRenderProvider : IRendererProvider<DrawingContext>
{
    private readonly WpfDrawingRenderer _drawingRenderer = new();
    private readonly WpfRasterRenderer  _rasterRenderer  = new();

    public IRenderer<TBuffer, DrawingContext>? GetRenderer<TBuffer>() where TBuffer : RenderBuffer
    {
        if (typeof(TBuffer) == typeof(DrawingBuffer)) return (IRenderer<TBuffer, DrawingContext>)(object)_drawingRenderer;
        if (typeof(TBuffer) == typeof(BitmapBuffer))  return (IRenderer<TBuffer, DrawingContext>)(object)_rasterRenderer;
        return null; // WidgetBuffer 不走 backend,统一在 ChartCell.RenderWidgetLayers 兜底
    }
}
```

---

## 接入新后端的标准动作

假设要接入"FooGfx"后端(SkiaSharp 历史曾接入又移除,见下方"历史教训"。可选: Direct2D / OpenGL / 重新引入 SkiaSharp):

### 1. 决定 TContext

后端的"画布句柄"。例:
- D2D: `ID2D1RenderTarget`
- GL: 自定义 `GLContext` 包装 + FBO 句柄
- SkiaSharp: `SKCanvas`

### 2. 实现 `IRenderer<DrawingBuffer, TContext>` (矢量渲染器)

按 [Renderers/DrawingRenderer.cs](Renderers/DrawingRenderer.cs) 的范式重写一份。这是大头。要做:

- 实现 `Render(DrawingBuffer buffer, TContext ctx)`,遍历 `buffer.Commands` 按 `DrawOp` 分发
- **画刷 / 笔缓存**:把 `IHevoBrush` / `HevoPen` 翻译成后端原生类型,缓存避免每帧 new
- **像素对齐**:**必须沿用** [Renderers/PixelSnap.cs](Renderers/PixelSnap.cs) 的 `SnapEndpoints` / `InsideStroke` / `Vertex`,所有 stroke 顶点 snap 到 `.5/.0`,跨 backend 同源,保证业务侧渲染像素级一致
- **DrawOp 完整覆盖**:DrawLine / DrawRectangle / DrawRoundedRectangle / DrawEllipse / DrawPolyline / DrawLineSegments / DrawRectangles / DrawGeometry / DrawText / DrawImage / DrawVideo / PushClip / PushOpacity / PushTransform / Pop。漏一个就有 layer 不工作。(`PushGuidelineSet` 已删除,详见 [06.WPF 渲染规约.md](06.WPF%20渲染规约.md) §3.3)
- **0-GC 热路径**:`Render` 每帧调用,内部循环不允许 LINQ / box / 临时 List 分配

### 3. 实现 `IRenderer<BitmapBuffer, TContext>` (位图渲染器)

参考 [Renderers/WpfRasterRenderer.cs](Renderers/WpfRasterRenderer.cs)。把 `BitmapBuffer.PixelData`(IntPtr + Stride + Width + Height)绘制到 backend context。

### 4. 实现 `IRendererProvider<TContext>`

参考 `WpfRenderProvider`,按 buffer 类型分发到 #2 / #3。

### 5. 集成进 ChartCell

这是最微妙的一步。当前 ChartCell 是写死的 WPF backend(用 `_wpfProvider` 字段、`_drawingCanvas` UIElement、`RenderOpen()` 拿 DrawingContext)。

接入新 backend 有两种思路:

**思路 A:Hybrid(并存,不同 Layer 走不同 backend)**

加新字段 + 新 host:
```csharp
private readonly FooGfxRenderProvider _fooProvider = new();
private readonly FooGfxElement _fooElement = new();   // FooGfx 的 UIElement 寄主
private readonly List<IChartLayer> _fooLayers = new();
```

`ChartLayer` 上加新 `RenderMode`(或者复用现有 `Hardware`):
```csharp
public enum RenderMode { Software, Hardware, FooGfx }
```

`ChartCell.AddUnmanagedLayer` 路由器加新分支,Mode==FooGfx 进 `_fooLayers`。Invalidate 阶段 `_fooElement.InvalidateVisual()` 触发后端重绘,在后端 paint surface event 里跑 `buffer.Execute(_fooProvider, fooCanvas)`。

**思路 B:Switch(整体替换 WPF)**

少见但更彻底。把 `_drawingCanvas` 换成新后端的 host,所有 Software Layer 改走新 backend。这等于把 backend 抽象的优势抹掉,不推荐,除非新后端在所有维度都吊打 WPF。

---

## 历史教训(Skia 的痛)

接 Skia 之前已有完整排查记录：**SkiaSharp.Views.WPF 在 fullscreen 特定尺寸下 fill rect / stroke 路径都出现 ±1 列错位**,跨多种规避方案都复现,最终全员退回 WPF。

接入新 backend 必须先做的验证(避免重蹈覆辙):

1. **像素扫描测试**:写一个带 5+ 个 vertical grid 的最小 demo,在 fullscreen / 普通窗口 / 多 DPI / 父级 UniformGrid 列除不尽这 4 个尺寸下,放大镜逐列扫色,确认每条线像素列数一致
2. **冷热分离 perf 验证**:跑 10k+ K 线 + 持续 hover,FrameDiag (`[FrameGap]`) 必须稳定 ~16ms,gap 不能漂到 30ms+
3. **bitmap 对接验证**:`BitmapBuffer` 的 IntPtr/Stride 转 backend 原生 image 那一段最容易出 stride / 字节序的坑,要做单元测试覆盖 BGRA8888 + 各种宽度对齐
4. **clip / opacity / transform 栈**:`PushClip` / `PushOpacity` / `PushTransform` / `Pop` 必须严格栈平衡,backend 内部 save/restore 跟外部调用对应

---

## 路由扩展点速查

| 文件 | 改动 |
|---|---|
| `Renderers/RenderProvider.cs` | 加 `FooGfxRenderProvider` |
| `Renderers/FooGfxDrawingRenderer.cs` | 新建,实现 `IRenderer<DrawingBuffer, TContext>` |
| `Renderers/FooGfxRasterRenderer.cs` | 新建,实现 `IRenderer<BitmapBuffer, TContext>` |
| `Core/ChartCell.cs` | 新 backend host UIElement + 新 layer registry + 路由分支 |
| `Abstractions/ChartLayer.cs` | 如新增 `RenderMode` 枚举值 |
| `*.csproj` | 加新 backend 的 NuGet / native dll 依赖 |

WidgetBuffer 不走任何 backend,统一在 `ChartCell.RenderWidgetLayers` 走 `_interactionCanvas` 控件池——新增 backend 不需要碰这条路径。

---

## 不要做的事

- ❌ 不要在 `IRendererProvider.GetRenderer` 内部 new 渲染器实例。每帧一次 new 会把 0-GC 优化全部毁掉
- ❌ 不要在 `IRenderer.Render` 内分配 List / 临时数组 / 闭包。命令热路径是 60Hz × N 命令,任何分配都会进 LOH 或 Gen0 频繁 GC
- ❌ 不要绕开 `PixelSnap`。让新 backend "用自己的 snap 逻辑"会导致跟 WPF backend 渲染的 layer 像素错位 1px,业务侧 debug 极痛苦
- ❌ 不要在 backend 实现里直接读业务 trait(如 CandleData)。所有数据都已在 Layer.OnUpdate 阶段编码进 RenderBuffer 命令,backend 只消费命令、不感知业务语义

---

## 位图回放后端(RenderMode.Bitmap)

不是新接一个图形库,而是给同一套 `DrawCmd` 加第二种回放方式:框架自己把指令光栅化进图层的 `WriteableBitmap`,
图层的 DrawingVisual 里只挂"裁剪 + 一张位图"三条 RenderData。图层代码、Feature、蓝图都不用改。

### 怎么切

```csharp
// 单个图层(构造后任何时候都可以改,下一帧生效,不重录图层)
var candles = new CandleLayer { Mode = RenderMode.Bitmap };
candles.Mode = RenderMode.Software;   // 切回矢量

// 整张图统一切(null = 各图层按自己的 Mode)
chart.RenderModeOverride = RenderMode.Bitmap;
```

建议只给指令多、每帧整层重录的图层开(K 线、折线、柱、散点);坐标轴、十字光标这类指令少的图层留在矢量,文字更清晰。

### 文件

| 文件 | 作用 |
|---|---|
| `Renderers/Raster/RasterSurface.cs` | 预乘 BGRA32 像素目标(绑定 WriteableBitmap.BackBuffer),裁剪框、脏区、src-over 混合 |
| `Renderers/Raster/ScanlineFiller.cs` | 扫描线多边形填充,像素中心采样(= `EdgeMode.Aliased`),非零 / 奇偶 |
| `Renderers/Raster/Stroker.cs` | 折线描边展开(线端 / 拐角 / 虚线,语义同 WPF Pen,DashCap 默认 Square) |
| `Renderers/Raster/RasterTextRenderer.cs` | 字形蒙版缓存(GlyphTypeface 能覆盖时)+ 整串 FormattedText 兜底(中文回退字体等) |
| `Renderers/Raster/RasterDrawingRenderer.cs` | `IRenderer<DrawingBuffer / BitmapBuffer, RasterSurface>`,DrawOp 全覆盖(DrawVideo 除外) |
| `Renderers/Raster/RasterRenderProvider.cs` | `IRendererProvider<RasterSurface>` |
| `Renderers/Raster/BitmapLayerSurface.cs` | 每个位图图层的 WriteableBitmap:只增不减 + 1/8 余量、低于一半收缩;只清 / 只提交画过的区域 |
| `Core/ChartCell.cs` `RenderVisualLayers` | 按 `RenderModeOverride ?? layer.Mode` 分发;策略刚切换的那一帧不脏也重新上屏 |

### 跟 WPF 矢量路径的差异

- 像素对齐沿用 `PixelSnap`,覆盖规则同 `EdgeMode.Aliased`:水平 / 垂直线、矩形逐像素一致(`RasterRendererTests` 截图对比)。
- `PushOpacity` 逐图元乘透明度(WPF 是整组合成后再乘,组内图元重叠处会略深)。
- 渐变只取首尾两个色标;ImageBrush / VisualBrush 不支持(该图元跳过)。
- 文字:字形落在整像素上(WPF 亚像素定位),宽度用 advance 之和;标签框位置可能差 1 像素。
- 含 `DrawVideo` 的帧自动回退矢量。
- 内存:每个位图图层 ≈ 宽 × 高 × 8 字节(WriteableBitmap 前后台各一份),1920×1080 约 16MB。
- 光栅化在 UI 线程做,WPF 渲染线程省掉对应的几何处理;评估时看分配和 `--latency-probe --renderer=` 端到端延迟,不要只看 UI 线程帧耗时。
