using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using SkiaSharp;

namespace Hevo.Charting.Renderers
{
    /// <summary>
    /// 2. Skia 光栅渲染器 
    /// </summary>
    public sealed class SkiaRasterRenderer : IRenderer<BitmapBuffer, SKCanvas>
    {
        public void Render(BitmapBuffer buffer, SKCanvas canvas)
        {
            if (buffer.PixelData == IntPtr.Zero) return;

            var info = new SKImageInfo(buffer.PixelWidth, buffer.PixelHeight, SKColorType.Bgra8888);
            using var pixmap = new SKPixmap(info, buffer.PixelData, buffer.Stride);
            using var image = SKImage.FromPixels(pixmap);

            canvas.DrawImage(image, 0, 0);
        }
    }
}
