using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.DevTools;

namespace Hevo.Charting.Renderers.Raster
{
    /// <summary>
    /// 位图后端调度中心:跟 <see cref="WpfRenderProvider"/> 平行,同一个 <see cref="LayerBuffer"/>
    /// 用 <c>buffer.Execute(provider, surface)</c> 回放到像素表面。WidgetBuffer 不走后端(返回 null)。
    /// </summary>
    internal sealed class RasterRenderProvider : IRendererProvider<RasterSurface>
    {
        private readonly RasterDrawingRenderer _renderer;

        public RasterRenderProvider(RenderDiagnostics? diagnostics = null)
        {
            _renderer = new RasterDrawingRenderer(diagnostics);
        }

        public IRenderer<TBuffer, RasterSurface>? GetRenderer<TBuffer>() where TBuffer : RenderBuffer
        {
            if (typeof(TBuffer) == typeof(DrawingBuffer)) return (IRenderer<TBuffer, RasterSurface>)(object)_renderer;
            if (typeof(TBuffer) == typeof(BitmapBuffer)) return (IRenderer<TBuffer, RasterSurface>)(object)_renderer;
            return null;
        }

        public void ClearCaches() => _renderer.ClearCaches();
    }
}
