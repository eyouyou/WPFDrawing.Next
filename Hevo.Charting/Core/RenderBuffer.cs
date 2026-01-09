using Hevo.Charting.Abstractions;
using Hevo.Charting.Renderers;
using System.Windows;

namespace Hevo.Charting.Core
{
    public abstract class RenderBuffer
    {
        public abstract void Clear();
        // 核心：强类型上下文传递的自我执行
        public abstract void Execute<TContext>(IRendererProvider<TContext> provider, TContext context);
    }

    /// <summary>
    /// 绘制buffer
    /// </summary>
    public sealed class DrawingBuffer : RenderBuffer, IDrawingSink
    {
        internal readonly List<DrawCmd> Commands = new(2048);

        public void Push(DrawCmd cmd) => Commands.Add(cmd);
        public override void Clear() => Commands.Clear();

        public override void Execute<TContext>(IRendererProvider<TContext> provider, TContext context)
        {
            var renderer = provider.GetRenderer<DrawingBuffer>();
            renderer?.Render(this, context);
        }

        /// <summary>
        /// 将另一个 Buffer 的指令无缝拼接到当前 Buffer 后面
        /// 这就是实现“共享 DC”的关键：物理上合并指令流
        /// </summary>
        public void Append(RenderBuffer other)
        {
            if (other is DrawingBuffer otherDb && otherDb.Commands.Count > 0)
            {
                // 极速内存拷贝 (Zero-GC if capacity is enough)
                Commands.AddRange(otherDb.Commands);
            }
        }
    }

    /// <summary>
    /// 光栅像素缓冲 (百万散点/热力图利器)
    /// </summary>
    public sealed class BitmapBuffer : RenderBuffer
    {
        public IntPtr PixelData { get; private set; }
        public int PixelWidth { get; private set; }
        public int PixelHeight { get; private set; }
        public int Stride { get; private set; }

        public void UpdateData(IntPtr pixelData, int width, int height, int stride)
        {
            PixelData = pixelData; PixelWidth = width; PixelHeight = height; Stride = stride;
        }

        public override void Clear() { PixelData = IntPtr.Zero; }

        public override void Execute<TContext>(IRendererProvider<TContext> provider, TContext context)
        {
            var renderer = provider.GetRenderer<BitmapBuffer>();
            renderer?.Render(this, context);
        }
    }

    public readonly record struct WidgetCommand(object ViewModel, Rect Bounds);

    public sealed class WidgetBuffer : RenderBuffer
    {
        private readonly List<WidgetCommand> _commands = new();
        public IReadOnlyList<WidgetCommand> Commands => _commands;
        public bool IsEmpty => _commands.Count == 0;

        public void UpdateLayout(Rect bounds, object vm) => _commands.Add(new WidgetCommand(vm, bounds));
        public override void Clear() => _commands.Clear();

        public override void Execute<TContext>(IRendererProvider<TContext> provider, TContext context)
        {
            var renderer = provider.GetRenderer<WidgetBuffer>();
            renderer?.Render(this, context);
        }
    }

    public sealed class LayerBuffer : RenderBuffer
    {
        public DrawingBuffer Drawing { get; } = new();
        public BitmapBuffer Bitmap { get; } = new();
        public WidgetBuffer Widget { get; } = new();

        public override void Clear() { Drawing.Clear(); Bitmap.Clear(); Widget.Clear(); }

        public override void Execute<TContext>(IRendererProvider<TContext> provider, TContext context)
        {
            if (Drawing.Commands.Count > 0) Drawing.Execute(provider, context);
            if (Bitmap.PixelData != IntPtr.Zero) Bitmap.Execute(provider, context);
            if (!Widget.IsEmpty) Widget.Execute(provider, context);
        }
    }
}
