using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hevo.Charting.Renderers
{
    /// <summary>
    /// 2. WPF 光栅渲染器
    /// </summary>
    public sealed class WpfRasterRenderer : IRenderer<BitmapBuffer, DrawingContext>
    {
        private WriteableBitmap? _writeableBitmap;

        public void Render(BitmapBuffer buffer, DrawingContext dc)
        {
            if (buffer.PixelData == IntPtr.Zero) return;

            if (_writeableBitmap == null || _writeableBitmap.PixelWidth != buffer.PixelWidth || _writeableBitmap.PixelHeight != buffer.PixelHeight)
            {
                _writeableBitmap = new WriteableBitmap(buffer.PixelWidth, buffer.PixelHeight, 96, 96, PixelFormats.Pbgra32, null);
            }

            _writeableBitmap.WritePixels(
                new Int32Rect(0, 0, buffer.PixelWidth, buffer.PixelHeight),
                buffer.PixelData,
                buffer.PixelHeight * buffer.Stride,
                buffer.Stride);

            dc.DrawImage(_writeableBitmap, new Rect(0, 0, buffer.PixelWidth, buffer.PixelHeight));
        }
    }
}
