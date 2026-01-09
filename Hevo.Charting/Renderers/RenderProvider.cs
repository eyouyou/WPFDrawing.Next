using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using SkiaSharp;
using System.Windows.Media;

namespace Hevo.Charting.Renderers
{
    /// <summary>
    /// WPF 调度中心
    /// </summary>
    public sealed class WpfRenderProvider : IRendererProvider<DrawingContext>, IDisposable
    {
        private readonly WpfDrawingRenderer _drawingRenderer = new();
        private readonly WpfRasterRenderer _rasterRenderer = new();

        public IRenderer<TBuffer, DrawingContext>? GetRenderer<TBuffer>() where TBuffer : RenderBuffer
        {
            if (typeof(TBuffer) == typeof(DrawingBuffer)) return (IRenderer<TBuffer, DrawingContext>)(object)_drawingRenderer;
            if (typeof(TBuffer) == typeof(BitmapBuffer)) return (IRenderer<TBuffer, DrawingContext>)(object)_rasterRenderer;
            return null;
        }
        public void Dispose() => _drawingRenderer.Dispose();
    }


    /// <summary>
    /// Skia 调度中心
    /// </summary>
    public sealed class SkiaRenderProvider : IRendererProvider<SKCanvas>, IDisposable
    {
        private readonly SkiaDrawingRenderer _drawingRenderer = new(new WpfThemeResolver());
        private readonly SkiaRasterRenderer _rasterRenderer = new();

        public IRenderer<TBuffer, SKCanvas>? GetRenderer<TBuffer>() where TBuffer : RenderBuffer
        {
            if (typeof(TBuffer) == typeof(DrawingBuffer)) return (IRenderer<TBuffer, SKCanvas>)(object)_drawingRenderer;
            if (typeof(TBuffer) == typeof(BitmapBuffer)) return (IRenderer<TBuffer, SKCanvas>)(object)_rasterRenderer;
            return null;
        }
        public void Dispose() => _drawingRenderer.Dispose();
    }
}
