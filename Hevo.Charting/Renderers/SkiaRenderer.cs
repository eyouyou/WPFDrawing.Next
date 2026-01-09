using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using SkiaSharp;
using System.Windows;
using System.Windows.Media;

namespace Hevo.Charting.Renderers
{
    /// <summary>
    /// 💥 跨平台主题解析器契约
    /// </summary>
    public interface IThemeResolver
    {
        SKColor GetColor(string resourceKey);
        string GetFontFamily(string resourceKey);

        // 💥 新增：多语言翻译能力！
        string Translate(string resourceKey);
    }

    public class WpfThemeResolver : IThemeResolver
    {
        public SKColor GetColor(string resourceKey)
        {
            // 1. 去 WPF 全局/局部资源里捞
            var resource = Application.Current.TryFindResource(resourceKey);

            // 2. 翻译 Color
            if (resource is Color wpfColor)
            {
                return new SKColor(wpfColor.R, wpfColor.G, wpfColor.B, wpfColor.A);
            }

            // 3. 翻译 SolidColorBrush (防止 XAML 里配错)
            if (resource is SolidColorBrush brush)
            {
                return new SKColor(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A);
            }

            // 💥 防御性编程：如果找不到 Key，返回一个极其刺眼的品红色(Magenta)
            // 这样在开发阶段，你一眼就能看出哪里漏配了资源！
            return SKColors.Magenta;
        }

        public string GetFontFamily(string resourceKey)
        {
            var resource = Application.Current.TryFindResource(resourceKey);

            if (resource is FontFamily ff) return ff.Source;
            if (resource is string str) return str;

            return "Arial"; // 终极保底字体
        }

        // 💥 实现多语言翻译
        public string Translate(string resourceKey)
        {
            var resource = Application.Current.TryFindResource(resourceKey);
            // 找到就返回翻译文本，找不到就直接把 Key 弹出来兜底
            return resource is string str ? str : resourceKey;
        }
    }

    // ==========================================
    // 💥 终极 OCP 架构：Skia 渲染策略注册表
    // ==========================================
    public static class SkiaRenderRegistry
    {
        // 核心字典：记录 [画刷类型 -> SKPaint 组装逻辑]
        private static readonly Dictionary<Type, Action<SKPaint, IHevoBrush, IThemeResolver>> _brushHandlers = new();

        static SkiaRenderRegistry()
        {
            // 1. 注册纯色
            RegisterBrush<HevoSolidBrush>((paint, b, resolver) =>
                paint.Color = new SKColor(b.Color.R, b.Color.G, b.Color.B, b.Color.A));

            // 2. 注册资源色 (回调给 IThemeResolver 解析)
            RegisterBrush<HevoResourceBrush>((paint, b, resolver) =>
                paint.Color = resolver.GetColor(b.ResourceKey));

            // 3. 注册线性渐变 (转换为 SKShader)
            RegisterBrush<HevoLinearGradientBrush>((paint, b, resolver) =>
            {
                paint.Shader = SKShader.CreateLinearGradient(
                    new SKPoint((float)b.StartPoint.X, (float)b.StartPoint.Y),
                    new SKPoint((float)b.EndPoint.X, (float)b.EndPoint.Y),
                    new[] { new SKColor(b.StartColor.R, b.StartColor.G, b.StartColor.B, b.StartColor.A),
                            new SKColor(b.EndColor.R, b.EndColor.G, b.EndColor.B, b.EndColor.A) },
                    null, SKShaderTileMode.Clamp);
            });
        }

        public static void RegisterBrush<TBrush>(Action<SKPaint, TBrush, IThemeResolver> handler) where TBrush : IHevoBrush
        {
            _brushHandlers[typeof(TBrush)] = (paint, b, resolver) => handler(paint, (TBrush)b, resolver);
        }

        public static void ConfigurePaint(SKPaint paint, IHevoBrush brush, IThemeResolver resolver)
        {
            if (_brushHandlers.TryGetValue(brush.GetType(), out var handler))
                handler.Invoke(paint, brush, resolver);
            else
                throw new NotSupportedException($"[Skia引擎] 未找到画刷 {brush.GetType().Name} 的解析策略，请先调用 SkiaRenderRegistry.RegisterBrush 注册！");
        }
    }

    /// <summary>
    /// 💥 工业级 Skia 极速渲染引擎
    /// </summary>
    public sealed class SkiaDrawingRenderer : IRenderer<DrawingBuffer, SKCanvas>, IDisposable
    {
        private readonly Dictionary<IHevoBrush, SKPaint> _fillCache = new();
        private readonly Dictionary<HevoPen, SKPaint> _strokeCache = new();
        private readonly Dictionary<FontKey, SKFont> _fontCache = new();
        private readonly IThemeResolver _themeResolver;

        public SkiaDrawingRenderer(IThemeResolver themeResolver)
        {
            _themeResolver = themeResolver;
        }

        public void InvalidateTheme()
        {
            foreach (var paint in _fillCache.Values) paint.Dispose();
            foreach (var paint in _strokeCache.Values) paint.Dispose();
            foreach (var font in _fontCache.Values) font.Dispose();
            _fillCache.Clear();
            _strokeCache.Clear();
            _fontCache.Clear();
        }

        public void Render(DrawingBuffer buffer, SKCanvas canvas)
        {
            var cmds = buffer.Commands;
            for (int i = 0; i < cmds.Count; i++)
            {
                var cmd = cmds[i];
                var fillPaint = GetFillPaint(cmd.Brush);
                var strokePaint = GetStrokePaint(cmd.Pen);

                switch (cmd.Op)
                {
                    case DrawOp.DrawLine:
                        if (strokePaint != null) canvas.DrawLine(T(cmd.Payload.P1), T(cmd.Payload.P2), strokePaint);
                        break;
                    case DrawOp.DrawRectangle:
                        if (fillPaint != null) canvas.DrawRect(T(cmd.Payload.RectArea), fillPaint);
                        if (strokePaint != null) canvas.DrawRect(T(cmd.Payload.RectArea), strokePaint);
                        break;
                    case DrawOp.DrawRoundedRectangle:
                        if (fillPaint != null) canvas.DrawRoundRect(T(cmd.Payload.RectArea), cmd.Payload.Val1, cmd.Payload.Val2, fillPaint);
                        if (strokePaint != null) canvas.DrawRoundRect(T(cmd.Payload.RectArea), cmd.Payload.Val1, cmd.Payload.Val2, strokePaint);
                        break;
                    case DrawOp.DrawEllipse:
                        if (fillPaint != null) canvas.DrawOval(cmd.Payload.P1.X, cmd.Payload.P1.Y, cmd.Payload.Val1, cmd.Payload.Val2, fillPaint);
                        if (strokePaint != null) canvas.DrawOval(cmd.Payload.P1.X, cmd.Payload.P1.Y, cmd.Payload.Val1, cmd.Payload.Val2, strokePaint);
                        break;
                    case DrawOp.DrawPolyline:
                        // 🚨 嫌疑人 1：画笔静默为空
                        if (strokePaint == null)
                        {
                            break;
                        }

                        // 💥 彻底修改为 HevoPoint
                        if (cmd.RefData is IList<HevoPoint> polyPts && polyPts.Count > 1)
                        {
                            var firstPt = T(polyPts[0]);

                            // 强制确保是描边模式 (防手抖)
                            strokePaint.Style = SKPaintStyle.Stroke;

                            using var path = new SKPath();
                            path.MoveTo(firstPt);
                            for (int j = 1; j < polyPts.Count; j++)
                            {
                                path.LineTo(T(polyPts[j]));
                            }

                            // 🚨 嫌疑人 4：画布被错误地 Clip (裁切) 到 0x0 了
                            canvas.DrawPath(path, strokePaint);
                        }
                        break;
                    case DrawOp.DrawLineSegments:
                        // 💥 彻底修改为 HevoPoint
                        if (strokePaint != null && cmd.RefData is IList<HevoPoint> segPts && segPts.Count > 1)
                        {
                            using var path = new SKPath();
                            for (int j = 0; j < segPts.Count - 1; j += 2)
                            {
                                path.MoveTo(T(segPts[j]));
                                path.LineTo(T(segPts[j + 1]));
                            }
                            canvas.DrawPath(path, strokePaint);
                        }
                        break;
                    case DrawOp.DrawRectangles:
                        // 💥 彻底修改为 HevoRect
                        if (cmd.RefData is IList<HevoRect> rects)
                        {
                            for (int j = 0; j < rects.Count; j++)
                            {
                                var r = T(rects[j]);
                                if (fillPaint != null) canvas.DrawRect(r, fillPaint);
                                if (strokePaint != null) canvas.DrawRect(r, strokePaint);
                            }
                        }
                        break;
                    case DrawOp.DrawGeometry:
                        if (cmd.RefData is string svg)
                        {
                            using var path = SKPath.ParseSvgPathData(svg);
                            if (fillPaint != null) canvas.DrawPath(path, fillPaint);
                            if (strokePaint != null) canvas.DrawPath(path, strokePaint);
                        }
                        break;
                    case DrawOp.DrawText:
                        if (fillPaint != null && cmd.RefData is TextInfo textInfo)
                        {
                            var font = GetFont(textInfo.Typeface, cmd.Payload.Val1);
                            string actualText = ResolveString(textInfo.Text);
                            if (string.IsNullOrEmpty(actualText)) break;

                            // 1. 极速测量宽度与高度
                            float textWidth = font.MeasureText(actualText, fillPaint);
                            var metrics = font.Metrics;
                            float textHeight = metrics.Descent - metrics.Ascent; // Skia 字体总高度

                            // 💥 2. 计算包围盒尺寸 (文字尺寸 + 内边距)
                            float boxWidth = textWidth + textInfo.PaddingX * 2f;
                            float boxHeight = textHeight + textInfo.PaddingY * 2f;

                            float x = cmd.Payload.P1.X;
                            float y = cmd.Payload.P1.Y;

                            // 💥 3. 锚定修正
                            if (textInfo.AlignX == TextAlignX.Center) x -= boxWidth / 2f;
                            else if (textInfo.AlignX == TextAlignX.Right) x -= boxWidth;

                            if (textInfo.AlignY == TextAlignY.Center) y -= boxHeight / 2f;
                            else if (textInfo.AlignY == TextAlignY.Bottom) y -= boxHeight;

                            // 💥 4. 一波带走：如果配置了背景，先画底框！
                            if (textInfo.BgBrush != null || textInfo.BorderPen != null)
                            {
                                var bgPaint = GetFillPaint(textInfo.BgBrush);
                                var borderPaint = GetStrokePaint(textInfo.BorderPen);
                                var rect = new SKRect(x, y, x + boxWidth, y + boxHeight);

                                if (bgPaint != null) canvas.DrawRect(rect, bgPaint);
                                if (borderPaint != null) canvas.DrawRect(rect, borderPaint);
                            }

                            // 5. 画文字 (💥 注意：Skia 坐标是基线，y 要加上 Padding，再减去负的 Ascent)
                            float textX = x + textInfo.PaddingX;
                            float textY = y + textInfo.PaddingY - metrics.Ascent;

                            canvas.DrawText(actualText, textX, textY, font, fillPaint);
                        }
                        break;
                    case DrawOp.DrawImage:
                        if (cmd.RefData is SKImage skImg) canvas.DrawImage(skImg, T(cmd.Payload.RectArea));
                        break;
                    case DrawOp.PushClip:
                        canvas.Save();
                        canvas.ClipRect(T(cmd.Payload.RectArea));
                        break;
                    case DrawOp.PushOpacity:
                        using (var alphaPaint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(cmd.Payload.Val1 * 255)) })
                        {
                            canvas.SaveLayer(alphaPaint);
                        }
                        break;
                    case DrawOp.PushTransform:
                        var m = cmd.Payload.Transform; // 💥 现在这里是纯净的 System.Numerics.Matrix3x2
                        canvas.Save();
                        // 将 Matrix3x2 映射为 SKMatrix
                        canvas.Concat(new SKMatrix(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, 0, 0, 1));
                        break;
                    case DrawOp.PushGuidelineSet:
                        canvas.Save();
                        if (cmd.Payload.Val1 != 0)
                        {
                            canvas.Translate(cmd.Payload.Val1, cmd.Payload.Val1);
                        }
                        break;
                    case DrawOp.Pop:
                        canvas.Restore();
                        break;
                }
            }
        }

        // ==========================================
        // 💥 极速翻译器 (强制内联，纯 float 的平滑过渡)
        // ==========================================
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private SKPoint T(HevoPoint p) => new SKPoint(p.X, p.Y);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private SKRect T(HevoRect r) => new SKRect(r.Left, r.Top, r.Right, r.Bottom);

        // 💥 字符串解析策略：将意图化为真正的字符
        private string ResolveString(IHevoString s)
        {
            return s switch
            {
                HevoResourceString res => _themeResolver.Translate(res.ResourceKey),
                HevoLiteralString lit => lit.Text,
                _ => string.Empty
            };
        }

        private SKPaint? GetFillPaint(IHevoBrush? desc)
        {
            if (desc == null) return null;
            if (_fillCache.TryGetValue(desc, out var paint)) return paint;

            paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

            // 💥 OCP 改造：去注册表里找！彻底消灭 if/else！
            SkiaRenderRegistry.ConfigurePaint(paint, desc, _themeResolver);

            _fillCache[desc] = paint;
            return paint;
        }

        private SKPaint? GetStrokePaint(HevoPen? desc)
        {
            if (desc == null) return null;
            if (_strokeCache.TryGetValue(desc, out var paint)) return paint;

            paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)desc.Thickness };

            // 💥 OCP 改造：查表绑定画刷！
            SkiaRenderRegistry.ConfigurePaint(paint, desc.Brush, _themeResolver);

            if (desc.DashArray != null && desc.DashArray.Length > 0)
            {
                float[] skDashes = new float[desc.DashArray.Length];
                for (int i = 0; i < desc.DashArray.Length; i++) skDashes[i] = (float)desc.DashArray[i];
                paint.PathEffect = SKPathEffect.CreateDash(skDashes, 0);
            }

            paint.StrokeCap = (SKStrokeCap)desc.LineCap;
            paint.StrokeJoin = (SKStrokeJoin)desc.LineJoin;

            _strokeCache[desc] = paint;
            return paint;
        }

        // 💥 1. 建立真正的字体缓存 Key
        private record FontKey(IHevoTypeface Typeface, float Size);

        private SKFont GetFont(IHevoTypeface desc, float size)
        {
            var key = new FontKey(desc, size);
            if (_fontCache.TryGetValue(key, out var cachedFont)) return cachedFont;

            string familyName = "Arial";
            int weightVal = 400;
            bool isItalic = false;

            if (desc is HevoTypeface ht)
            {
                familyName = ht.FontFamily;
                weightVal = ht.FontWeight;
                isItalic = ht.IsItalic;
            }
            else if (desc is HevoResourceTypeface rtf)
            {
                familyName = _themeResolver.GetFontFamily(rtf.ResourceKey);
                weightVal = rtf.FontWeight;
                isItalic = rtf.IsItalic;
            }

            var weight = weightVal >= 700 ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
            var slant = isItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
            var tf = SKTypeface.FromFamilyName(familyName, weight, SKFontStyleWidth.Normal, slant);

            var font = new SKFont(tf, size) { Edging = SKFontEdging.Antialias };
            _fontCache[key] = font;
            return font;
        }

        public void Dispose()
        {
            foreach (var paint in _fillCache.Values) paint.Dispose();
            foreach (var paint in _strokeCache.Values) paint.Dispose();
            foreach (var font in _fontCache.Values) font.Dispose();

            _fillCache.Clear(); _strokeCache.Clear(); _fontCache.Clear();
        }
    }
}