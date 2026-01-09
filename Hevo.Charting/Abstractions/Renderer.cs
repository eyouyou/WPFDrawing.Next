using Hevo.Charting.Core;

namespace Hevo.Charting.Abstractions
{
    /// <summary>
    /// 渲染模式：在 AddLayer 时显式指定
    /// </summary>
    public enum RenderMode
    {
        /// <summary>
        /// WPF CPU 渲染 (清晰、兼容性好)
        /// </summary>
        Software,
        /// <summary>
        /// D3D GPU 渲染 (高性能)
        /// </summary>
        Hardware
    }

    /// <summary>
    /// 终极无状态渲染器：数据(Buffer)与画布(Context)在此交汇
    /// </summary>
    public interface IRenderer<in TBuffer, in TContext> where TBuffer : RenderBuffer
    {
        void Render(TBuffer buffer, TContext context);
    }

    /// <summary>
    /// 3. 服务定位器：RenderContext 实现此接口，Buffer 通过它寻找渲染器
    /// </summary>
    public interface IRenderServiceProvider
    {
        TService? GetService<TService>() where TService : class;
    }

    /// <summary>
    /// 渲染器提供者：负责在渲染域内，将不同的 Buffer 路由给对应的武器
    /// </summary>
    public interface IRendererProvider<in TContext>
    {
        IRenderer<TBuffer, TContext>? GetRenderer<TBuffer>() where TBuffer : RenderBuffer;
    }

}
