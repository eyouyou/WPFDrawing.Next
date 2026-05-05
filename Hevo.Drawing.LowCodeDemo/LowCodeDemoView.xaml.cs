using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hevo.Charting;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Hevo.Charting.LowCode.Designer.GraphViewer.Wrappers;

namespace Hevo.Drawing.LowCodeDemo
{
    public partial class LowCodeDemoView : UserControl
    {
        private GraphSchema? _graphSchema;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        /// <summary>
        /// 可选。把工具栏文本框里的字符串转换为数据源的运行时上下文。
        /// 不设置时直接把字符串传给 BlueprintLauncher（匹配 LoadAsync(string) 签名）。
        /// </summary>
        public Func<string, object?>? ContextParser { get; set; }

        public LowCodeDemoView()
        {
            InitializeComponent();

            // 全部初始化放 ctor。挪到 Loaded 会在 TabControl 切 tab 时反复触发 ——
            // 画布每次切回都被重置成 sample,Click handler 也会重复订阅(单击 N 次开 N 个预览窗口)。
            GraphViewerBootstrap.Initialize();
            BuiltinRegistration.RegisterAssemblyOf<SinWaveDataSource>();

            _graphSchema = new GraphSchema { State = BuildSampleGraph() };
            _graphSchema.StateChanged += (newState, _) => RefreshJsonPreview(newState);
            _graphSchema.NodeEditRequested += OnNodeEditRequested;
            lowCodeHost.Schema = _graphSchema;
            RefreshJsonPreview(_graphSchema.State);

            btnAddDataSource.Click += (_, __) => PickAndAddNode(NodeFactory.Kind.DataSource, "选择数据源");
            btnAddTrait.Click      += (_, __) => PickAndAddNode(NodeFactory.Kind.Trait,      "选择 Trait");
            btnAddFeature.Click    += (_, __) => PickAndAddNode(NodeFactory.Kind.Feature,    "选择 Feature");

            btnExportJson.Click += (_, __) => RefreshJsonPreview(_graphSchema!.State);

            btnClear.Click += (_, __) =>
                _graphSchema!.ApplyUserEdit(s => GraphState.Empty with { Transform = s.Transform });

            btnImportJson.Click += (_, __) =>
            {
                if (_graphSchema == null) return;
                var text = jsonPreview.Text;
                if (string.IsNullOrWhiteSpace(text))
                {
                    MessageBox.Show(Window.GetWindow(this), "右侧 JSON 预览框是空的，没东西可导入。", "导入 JSON",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                try
                {
                    var bp = JsonSerializer.Deserialize<ChartBlueprint>(text, _jsonOptions);
                    if (bp == null) throw new Exception("反序列化得到 null。");
                    _graphSchema.ApplyUserEdit(_ => GraphDeserializer.FromBlueprint(bp));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Window.GetWindow(this), $"JSON 解析失败：{ex.Message}", "导入 JSON",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            btnAutoLayout.Click += (_, __) =>
            {
                if (_graphSchema == null) return;
                _graphSchema.ApplyUserEdit(s =>
                {
                    var nodes = s.Nodes.ToList();
                    var edges = s.Edges.ToList();
                    AutoLayout.Apply(nodes, edges);
                    return s with { Nodes = nodes };
                });
            };

            btnRunBlueprint.Click += (_, __) =>
            {
                if (_graphSchema == null) return;
                var bp = GraphSerializer.ToBlueprint(_graphSchema.State);

                object? context = null;
                var text = tbContext.Text?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    if (ContextParser != null)
                    {
                        try { context = ContextParser(text); }
                        catch (Exception ex)
                        {
                            MessageBox.Show(Window.GetWindow(this), $"上下文解析失败：{ex.Message}", "运行蓝图",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                    }
                    // ContextParser 返回 null(典型:Security.FromString 抛了被吞)时回退到原始字符串。
                    // 让 SinWaveDataSource 这种吃 string 的数据源在 ContextParser 解析失败时仍可跑。
                    context ??= text;
                }

                var err = BlueprintLauncher.Launch(bp, Window.GetWindow(this), dataSourceContext: context);
                if (err != null)
                    MessageBox.Show(Window.GetWindow(this), err, "蓝图运行失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            };
        }

        private void OnNodeEditRequested(Node node)
        {
            if (_graphSchema == null) return;
            var dlg = new NodeEditorWindow(node) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.Result != null)
                _graphSchema.ApplyUserEdit(s => s.WithNode(dlg.Result));
        }

        private void PickAndAddNode(NodeFactory.Kind kind, string title)
        {
            if (_graphSchema == null) return;
            var candidates = NodeFactory.ListByKind(kind);
            if (candidates.Count == 0)
            {
                MessageBox.Show(Window.GetWindow(this),
                    $"ComponentRegistry 里暂无 {kind} 类型可选。\n请确认对应程序集已通过 BuiltinRegistration.RegisterAssemblyOf<...>() 登记。",
                    "无可用组件", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var picker = new ComponentPickerWindow(title, candidates) { Owner = Window.GetWindow(this) };
            if (picker.ShowDialog() != true || picker.SelectedType == null) return;

            var actualWidth  = lowCodeHost.ActualWidth  > 0 ? lowCodeHost.ActualWidth  : 800;
            var actualHeight = lowCodeHost.ActualHeight > 0 ? lowCodeHost.ActualHeight : 500;
            var screenCenter = new HevoPoint((float)actualWidth / 2f, (float)actualHeight / 2f);
            var canvasCenter = _graphSchema.State.Transform.ScreenToCanvas(screenCenter);

            var sized = NodeFactory.CreateNode(picker.SelectedType, new HevoPoint(0, 0));
            var pos   = new HevoPoint(canvasCenter.X - sized.Size.X / 2, canvasCenter.Y - sized.Size.Y / 2);
            _graphSchema.ApplyUserEdit(s => s.WithNode(sized with { Position = pos }));
        }

        private void RefreshJsonPreview(GraphState state)
        {
            var bp   = GraphSerializer.ToBlueprint(state);
            var json = JsonSerializer.Serialize(bp, _jsonOptions);
            jsonPreview.Text = json;
        }

        /// <summary>
        /// 用外部构造的 GraphState 替换初始画布(直接写,不进 Undo 栈)。
        /// 宿主可在构造之后调用,用业务侧 sample 替换默认 SinWave sample。
        /// </summary>
        public void SetInitialGraph(GraphState state)
        {
            if (_graphSchema == null) return;
            _graphSchema.State = state;
        }

        private static GraphState BuildSampleGraph()
        {
            const float colStep = 260f, col0 = 60f;
            float Col(int i) => col0 + colStep * i;

            const float laneDS       =  60f;
            const float laneEnv      = 240f;
            const float laneAxes     = 440f;
            const float laneSeries   = 600f;
            const float laneInteract = 800f;

            var ds = NodeFactory.CreateNode(typeof(SinWaveDataSource), new HevoPoint(Col(0), laneDS));

            var grid         = NodeFactory.CreateNode(typeof(GridLayoutFeature),         new HevoPoint(Col(0), laneEnv));
            var decor        = NodeFactory.CreateNode(typeof(PlotAreaDecorFeature),      new HevoPoint(Col(1), laneEnv));
            var vpManager    = NodeFactory.CreateNode(typeof(ViewportManagerFeature),    new HevoPoint(Col(2), laneEnv));
            var scale        = NodeFactory.CreateNode(typeof(UniversalAutoScaleFeature), new HevoPoint(Col(3), laneEnv));
            var viewportNode = NodeFactory.CreateViewportNode(                           new HevoPoint(Col(4), laneEnv));
            var scaleTrait   = MakeTraitNode("ScaleStrategyTrait", "LineMode",
                position: new HevoPoint(Col(5), laneEnv));

            decor.Properties[nameof(PlotAreaDecorFeature.BackgroundBrush)] =
                (IHevoBrush)new HevoSolidBrush(Color.FromRgb(0x14, 0x16, 0x1B));
            decor.Properties[nameof(PlotAreaDecorFeature.BorderStyle)] =
                LineStyle.Create(Color.FromArgb(0x55, 0xB0, 0xBE, 0xC5), thickness: 1.0);

            var yAxis = NodeFactory.CreateNode(typeof(AxisFeature), new HevoPoint(Col(4), laneAxes));
            yAxis.Properties[nameof(AxisFeature.AxisStyle)] =
                AxisStyleTrait.Create(AxisPlacement.Right, Color.FromRgb(0xB0, 0xBE, 0xC5), fontSize: 11.0);

            var line = NodeFactory.CreateNode(typeof(LineSeriesFeature), new HevoPoint(Col(4), laneSeries));

            var interact  = NodeFactory.CreateNode(typeof(ChartInteractionFeature),      new HevoPoint(Col(1), laneInteract));
            var crosshair = NodeFactory.CreateNode(typeof(CrosshairDoubleFeature),       new HevoPoint(Col(2), laneInteract));
            var tooltip   = NodeFactory.CreateNode(typeof(TooltipDoubleWidgetFeature),   new HevoPoint(Col(3), laneInteract));
            var header    = NodeFactory.CreateNode(typeof(UniversalHeaderFeature),       new HevoPoint(Col(4), laneInteract));

            var nodes = new[] { ds, scaleTrait, grid, decor, vpManager, scale, viewportNode, yAxis, line, interact, crosshair, tooltip, header };

            var edges = new List<Edge>();

            TryWire(edges, ds, "LogicalLength", viewportNode, "LogicalLength");

            TryWire(edges, ds, "Value", line,  nameof(LineSeriesFeature.DataPort));
            TryWire(edges, ds, "Value", scale, nameof(UniversalAutoScaleFeature.ValuePorts));

            TryWire(edges, scale, nameof(UniversalAutoScaleFeature.YRangePort), line,  nameof(LineSeriesFeature.YRangePort));
            TryWire(edges, scale, nameof(UniversalAutoScaleFeature.YRangePort), yAxis, nameof(AxisFeature.RangePort));

            TryWire(edges, interact, nameof(ChartInteractionFeature.PointerHitPort), crosshair, nameof(CrosshairDoubleFeature.HitStatePort));
            TryWire(edges, interact, nameof(ChartInteractionFeature.PointerHitPort), tooltip,   nameof(TooltipDoubleWidgetFeature.HitStatePort));
            TryWire(edges, interact, nameof(ChartInteractionFeature.PointerHitPort), header,    nameof(UniversalHeaderFeature.HitStatePort));

            TryWire(edges, ds, "Value", crosshair, nameof(CrosshairDoubleFeature.XAxisDataPort));
            TryWire(edges, ds, "Value", tooltip,   nameof(TooltipDoubleWidgetFeature.XAxisDataPort));

            TryWire(edges, viewportNode, "LogicalLength", header, nameof(UniversalHeaderFeature.LogicalLengthPort));

            return new GraphState(
                Nodes: nodes,
                Edges: edges,
                SelectedNodeIds: new HashSet<string>(),
                Transform: CanvasTransform.Identity,
                RubberBand: null,
                BoxSelection: null);
        }

        /// <summary>
        /// 端口对端口的安全连线:类型不匹配/端口不存在直接跳过,不抛。
        /// public static —— 给宿主侧自行拼装 sample graph 复用。
        /// </summary>
        public static void TryWire(List<Edge> edges, Node from, string fromPortId, Node to, string toPortId)
        {
            var fromPort = from.OutputPorts.FirstOrDefault(p => p.Id == fromPortId);
            var toPort   = to.InputPorts.FirstOrDefault(p => p.Id == toPortId);
            if (fromPort == null || toPort == null) return;

            string fromType = fromPort.DataTypeName;
            string toType   = toPort.IsArray && toPort.DataTypeName.EndsWith("[]")
                ? toPort.DataTypeName[..^2]
                : toPort.DataTypeName;
            bool typeOk = fromType == toType || fromType == "object" || toType == "object";
            if (!typeOk) return;

            edges.Add(new Edge(
                Id: Guid.NewGuid().ToString("N").Substring(0, 8),
                FromNodeId: from.Id, FromPortId: fromPortId,
                ToNodeId:   to.Id,   ToPortId:   toPortId));
        }

        /// <summary>
        /// 构造一个 Trait 节点(无端口,只在 ChartBlueprint.InitialTraits 里挂 Preset)。
        /// public static —— 给宿主侧自行拼装 sample graph 复用。
        /// </summary>
        public static Node MakeTraitNode(string typeName, string preset, HevoPoint position)
        {
            FeatureCategory category = FeatureCategory.Trait;
            try
            {
                var t = ComponentRegistry.Resolve(typeName);
                category = FeatureCategoryRegistry.Resolve(t);
            }
            catch { }

            return new Node(
                Id: Guid.NewGuid().ToString("N").Substring(0, 8),
                TypeName: typeName,
                Title: typeName,
                Kind: NodeKind.Trait,
                Position: position,
                Size: new HevoPoint(200f, 50f),
                InputPorts: Array.Empty<Port>(),
                OutputPorts: Array.Empty<Port>(),
                Properties: new() { ["Preset"] = preset },
                Category: category);
        }
    }
}
