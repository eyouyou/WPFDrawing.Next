using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.DevTools;
using System.Windows.Media;

namespace Hevo.Charting.Renderers
{
    /// <summary>
    /// WPF 调度中心
    /// </summary>
    public sealed class WpfRenderProvider : IRendererProvider<DrawingContext>, IDisposable
    {
        private readonly WpfDrawingRenderer _drawingRenderer;
        private readonly WpfRasterRenderer _rasterRenderer = new();

        public WpfRenderProvider() : this(null) { }

        // diagnostics 可空：业务侧不需要诊断时全 null，hot loop 内每分支一次 null 检查可忽略。
        internal WpfRenderProvider(RenderDiagnostics? diagnostics)
        {
            _drawingRenderer = new WpfDrawingRenderer(diagnostics);
        }

        public IRenderer<TBuffer, DrawingContext>? GetRenderer<TBuffer>() where TBuffer : RenderBuffer
        {
            if (typeof(TBuffer) == typeof(DrawingBuffer)) return (IRenderer<TBuffer, DrawingContext>)(object)_drawingRenderer;
            if (typeof(TBuffer) == typeof(BitmapBuffer)) return (IRenderer<TBuffer, DrawingContext>)(object)_rasterRenderer;
            return null;
        }

        /// <summary>
        /// 注入真实 DPI(由 ChartCell 在 Loaded / DpiChanged 时同步调用)。
        /// 影响 FormattedText 的 PixelsPerDip,跨屏拖动 / 高 DPI 屏字体不再糊。
        /// </summary>
        public void UpdateDpi(double pixelsPerDip)
        {
            if (pixelsPerDip > 0) _drawingRenderer.PixelsPerDip = pixelsPerDip;
        }

        public void Dispose() => _drawingRenderer.Dispose();
    }
}
