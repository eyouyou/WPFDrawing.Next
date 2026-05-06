using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using System.Windows;

namespace Hevo.Drawing.LowCodeDemo
{
    public partial class DemoWindow : Window
    {
        public DemoWindow()
        {
            // Bootstrap must happen before any LowCodeDemoView / DashboardWorkspace is constructed.
            // LowCodeDemoView also calls Initialize() — it's idempotent, so double-calling is fine.
            GraphViewerBootstrap.Initialize();
            BuiltinRegistration.RegisterAssemblyOf<SinWaveDataSource>();

            InitializeComponent();
        }
    }
}
