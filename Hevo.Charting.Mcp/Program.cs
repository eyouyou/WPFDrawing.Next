using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hevo.Charting.Mcp;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        // ⚠️ stdio 协议红线:任何写到 stdout 的字节都会污染 JSON-RPC 帧。
        // .NET 默认 Console.Out 是 stdout —— 本进程内所有日志、警告、调试输出都必须走 stderr。
        // 框架内部偶发的 Console.WriteLine 也会污染 stdout(SmartActivator 注入失败警告等),
        // 启动阶段把 Console.Out 重定向到 Console.Error,运行期 stdout 由 MCP transport 独占。
        var stderrTextWriter = Console.Error;
        Console.SetOut(stderrTextWriter);

        // 框架内置 Feature / Trait / DataSource + GraphViewer wrappers 全登记 —— 否则
        // list_components 是空的,describe_component / validate_blueprint 全报"未注册"。
        // GraphViewerBootstrap 内部含 PortMetadataRegistry direction 标注,DryRun 校验 Output
        // 端口绑定时要查它。WPF 引用在 net8.0-windows 运行时可加载,不实际触发渲染。
        GraphViewerBootstrap.Initialize();

        var builder = Host.CreateApplicationBuilder(args);

        // 全部日志强制走 stderr (LogLevel.Trace 起步,跟 sdk 推荐一致)
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(opt => opt.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
    }
}
