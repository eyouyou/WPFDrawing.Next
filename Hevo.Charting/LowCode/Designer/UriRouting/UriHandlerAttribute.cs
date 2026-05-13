using System;

namespace Hevo.Charting.LowCode.Designer.UriRouting
{
    /// <summary>
    /// 给静态方法贴这个 attribute,被 <see cref="UriDispatcher.AutoDiscoverStatic"/> 反射扫到后注册成 URI handler。
    /// 等价于手写 <c>UriDispatcher.Default.Register(uri, handler)</c>,但消掉业务 Window 里的注册 ceremony,
    /// handler 落在 [UriHandler] 标注的 static 方法上,真正像 ASP.NET MVC Controller。
    ///
    /// <para>
    /// 用法:
    /// </para>
    /// <code>
    /// public static class ScannerHandlers
    /// {
    ///     [UriHandler("hevo://open/kline_detail")]
    ///     public static void OpenKLineDetail(UriArgs args)
    ///     {
    ///         var market = Market.Parse(args.Get("market")!);
    ///         new KLineDetailWindow(...).Show();
    ///     }
    /// }
    ///
    /// // App 启动一次性
    /// UriDispatcher.Default.AutoDiscoverStatic(typeof(ScannerHandlers));
    /// </code>
    ///
    /// <para>
    /// 方法签名约束:<c>void(UriArgs)</c>。其他签名 AutoDiscoverStatic 跳过 + 警告。
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class UriHandlerAttribute : Attribute
    {
        /// <summary>蓝图节点 Events 字段或业务 dispatch 用的 URI 路由 key(<c>scheme://authority/path</c>)。</summary>
        public string UriRoute { get; }

        public UriHandlerAttribute(string uriRoute)
        {
            UriRoute = uriRoute ?? throw new ArgumentNullException(nameof(uriRoute));
        }
    }
}
