using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.Converters;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Hevo.Charting.PythonNet;

namespace Hevo.Drawing.LowCodeDemo
{
    public partial class DemoWindow : Window
    {
        // (Workaround 已删除 —— Phase 1 完成后 GraphSerializer/Deserializer 用 Port.BindingId 保留
        // scope-qualified binding 字符串,roundtrip 不再摧毁 "dashboard:linkedHit" 这种跨 cell 共享语义。
        // 见 plan_binding_first_class.md §2.1.5。)

        public DemoWindow()
        {
            // Bootstrap must happen before any LowCodeDemoView / DashboardWorkspace is constructed.
            // LowCodeDemoView also calls Initialize() — it's idempotent, so double-calling is fine.
            GraphViewerBootstrap.Initialize();
            BuiltinRegistration.RegisterAssemblyOf<SinWaveDataSource>();   // demo 程序集:SinWave + MockKLineDataSource

            // §D3.7 注册 ComputeFeature / HandlerFeature 蓝图节点(画布 picker / Resolve 用)。
            // 即使本进程没启 PythonNetRuntime,蓝图节点元数据也得在(蓝图导入时 ComponentRegistry.Resolve 才不报错)。
            PythonNetBootstrap.Register();

            InitializeComponent();

            // Dashboard 编辑器 tab 装一个 3-cell 默认 dashboard(主图 K 线 + 副图成交量 + 副图 bb_breakout 策略),
            // 让用户打开就能看到 dashboard 多 cell 协作的全套形态。
            TryLoadDefaultDashboard();

            // 默认 SpawnPreviewWindow 模式不会传 BlueprintHandlerRegistry 给 LaunchEx,
            // strategy cell 的 PlotFeature(IndicatorName='bb_breakout') 会因为 ResolveHandlerReferences
            // 找不到 handler 注册表抛 InvalidOperationException。
            // 改 InvokeRunHandler 模式:demo 自己接 RunRequested,把 DemoPythonHost.Registry 喂给每个 cell,
            // 再 LaunchEx + 弹预览窗口(复刻原 RunDashboard 的 SpawnPreviewWindow 路径,但带 handlers)。
            dashboardWorkspace.RunMode = RunButtonMode.InvokeRunHandler;
            dashboardWorkspace.RunRequested += OnDashboardRunRequested;
        }

        private void OnDashboardRunRequested(Dashboard dashboard)
        {
            try
            {
                // 每次开预览,把进程级 mock K 线 timeline 重置回零 —— 用户期望
                // "运行 Dashboard"就像 TradingView 重新放策略一样从头跑一遍。
                MockKLineDataSource.Reset();

                var dry = DashboardLauncher.DryRun(dashboard);
                if (dry.Error != null)
                {
                    MessageBox.Show(this, $"DryRun 失败:{dry.Error}", "运行 Dashboard",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                BlueprintHandlerRegistry? registry = null;
                try { registry = DemoPythonHost.Registry; }
                catch (Exception ex)
                {
                    MessageBox.Show(this,
                        $"Python 运行时启动失败,strategy cell 的 bb_breakout 节点会跑不起来:\n{ex.Message}",
                        "Python 不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                // Handlers 是 Dictionary<cellId, registry>。给每个 cell 都注册(冗余但简洁)——
                // 没有 Python 节点的 cell 拿到也不害,只在 ResolveHandlerReferences 真碰到 Delegate 属性时才查表。
                Dictionary<string, BlueprintHandlerRegistry>? handlersMap = null;
                if (registry != null)
                {
                    handlersMap = new Dictionary<string, BlueprintHandlerRegistry>();
                    foreach (var cell in dashboard.Cells)
                        handlersMap[cell.Id] = registry;
                }

                var opts = new DashboardLaunchOptions
                {
                    Owner = this,
                    Handlers = handlersMap,
                };

                var result = DashboardLauncher.LaunchEx(dashboard, opts);
                if (result.Error != null)
                {
                    MessageBox.Show(this, $"装配失败:{result.Error}", "运行 Dashboard",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string diagSuffix = result.Diagnostics.Count > 0
                    ? $"  ⚠ {result.Diagnostics.Count} 条诊断(控制台)"
                    : string.Empty;

                new Window
                {
                    Title  = $"Dashboard 预览 — {dashboard.Cells.Count} cells{diagSuffix}",
                    Width  = 1200,
                    Height = 800,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner  = this,
                    Content = result.Dashboard,
                    Background = System.Windows.Media.Brushes.Black,
                }.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"运行 Dashboard 失败:{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                    "运行 Dashboard", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TryLoadDefaultDashboard()
        {
            try
            {
                var json = LoadEmbeddedDashboardJson();
                if (string.IsNullOrEmpty(json)) return;
                var dashboard = JsonSerializer.Deserialize<Dashboard>(json, BlueprintJsonOptions.Default);
                if (dashboard == null || dashboard.Cells.Count == 0) return;
                dashboardWorkspace.LoadDashboard(dashboard);
            }
            catch (Exception ex)
            {
                // demo 启动期不应该因为默认 dashboard 加载失败崩溃 —— 静默退化成空 dashboard,
                // 用户仍然可以手动 ImportJson 任意 .dashboard.json 文件。
                Console.WriteLine($"[DemoWindow] 默认 dashboard 加载失败:{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string? LoadEmbeddedDashboardJson()
        {
            // csproj 把 Assets/default_kline_dashboard.json 标了 EmbeddedResource,
            // 默认资源名 = "<AssemblyName>.<Path with dots>",这里 = "Hevo.Drawing.LowCodeDemo.Assets.default_kline_dashboard.json"
            const string resourceName = "Hevo.Drawing.LowCodeDemo.Assets.default_kline_dashboard.json";
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
