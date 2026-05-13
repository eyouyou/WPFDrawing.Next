using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hevo.Charting;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.Converters;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Hevo.Charting.LowCode.Designer.GraphViewer.Wrappers;
using Hevo.Charting.PythonNet;

namespace Hevo.Drawing.LowCodeDemo
{
    public partial class LowCodeDemoView : UserControl
    {
        // graph editor schema:
        // GraphEditorSchema 跟 KLineSchema / AttentionSchema 等量齐观 —— 直接 new 一个,
        // 内部持有 BlueprintDataSource(state source-of-truth)+ 单一 GraphInteractionFeature(跟 chart 侧 ChartInteractionFeature 同款单类模式)。
        // 消费者读 _schema.DataSource.State / 订阅 _schema.Interaction.NodeEditRequested 即可。
        private GraphEditorSchema? _schema;

        // 全局共享一份 options —— 蓝图导入/导出走同一份 schema 才能保证 Color / IHevoBrush /
        // NaN / Infinity 等坑点全程对齐。详见低代码.md §8.10 + 优化方案 §7。
        private static readonly JsonSerializerOptions _jsonOptions = BlueprintJsonOptions.Default;

        /// <summary>
        /// 可选。BlueprintLauncher.LaunchEx 的 configureDataSource 透传 —— 收到 dsInstance 在
        /// BlueprintRunner 装配之前同步执行。典型场景:特定 DS 类型需要业务侧手工 SwitchContext。
        /// 节点化协议后(2026-05),DS 的运行时 context 由 DataSourceModel.DefaultContext 字段承载,
        /// 解析交由蓝图节点编辑器,顶层 tbContext 文本框已撤掉;这个 callback 留给非 context 类的 setup。
        /// </summary>
        public Action<object>? ConfigureDataSource { get; set; }

        /// <summary>
        /// 可选。覆盖默认的 <c>DemoPythonHost.Registry</c>。业务侧自带 handler 注册表
        /// (典型:KLine 用 <c>ScannerPythonHost.Registry</c> 装 buy_signals_ma_cross)时设此项,
        /// 跳过 demo 内置 bollinger_upper / pine_macd 路径,免得跨 Python runtime 互踩。
        /// </summary>
        public BlueprintHandlerRegistry? HandlersOverride { get; set; }

        /// <summary>
        /// 是否显示右侧 JSON 预览栏。默认 true(独立 demo 用)。
        /// 业务嵌入 BlueprintEditorPanel 这种宿主自己有 chart 预览的场景:设 false 收起 JSON 栏,
        /// 把空间让给 inline preview;落盘 / 调试 走 PersistencePath JSON 文件即可。
        /// </summary>
        public bool ShowJsonPreview
        {
            get => jsonPreviewPane?.Visibility == System.Windows.Visibility.Visible;
            set
            {
                if (jsonPreviewPane == null || jsonColumn == null) return;
                jsonPreviewPane.Visibility = value ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                // ColumnDefinition 设 0 + Visibility=Collapsed 双管齐下,确保右栏物理空间为 0,
                // 避免画布右侧仍空 380px 看着像 padding。
                jsonColumn.Width = value ? new System.Windows.GridLength(380) : new System.Windows.GridLength(0);
            }
        }

        /// <summary>
        /// 可选。蓝图 JSON 持久化文件路径。设置时:
        /// <list type="bullet">
        ///   <item>文件存在 → 立刻读出来覆盖当前画布(替代默认 sample / 业务 SetInitialGraph 的内容)</item>
        ///   <item>文件不存在 → 保留当前画布,首次保存时自动创建文件 + 父目录</item>
        ///   <item>StateChanged 后自动 debounce 500ms 写一次磁盘(避免拖动时疯狂 IO)</item>
        /// </list>
        /// 业务侧典型用法:KLineDetailWindow 把它设到 <c>%LOCALAPPDATA%/.../kline_detail.json</c>,
        /// 用户编辑画布后下次开窗自动恢复。
        /// </summary>
        public string? PersistencePath
        {
            get => _persistencePath;
            set
            {
                _persistencePath = value;
                // framework-init 路径:加载 .draft / PersistencePath 内容覆盖当前画布,触发的 StateChanged
                // 不应该被视为"用户编辑画布",否则启动期 500ms 后会 fire BlueprintStabilized →
                // 宿主 ApplyToCell → BlueprintRunner.Run 重装 schema → 旧 DS 被 Dispose → 业务侧
                // Stream 订阅悬空。用 _initialLoadInProgress 抑制 debounce start。
                _initialLoadInProgress = true;
                try { TryLoadFromPersistence(); }
                finally { _initialLoadInProgress = false; }
            }
        }
        private string? _persistencePath;

        // framework init 期间(SetInitialGraph / PersistencePath 加载)的 StateChanged 不算"用户编辑",
        // 不该 fire BlueprintStabilized 导致宿主重装 schema。只在用户真实拖拽画布时才走 debounce + stabilize。
        private bool _initialLoadInProgress;

        // 自动落盘草稿路径:跟在正式版本路径后面 ".draft" 后缀。
        // 自动落盘 500ms debounce 写到这,只有用户主动点"保存"才把它 commit 到正式版本(<see cref="CommitDraft"/>)。
        // 这样"恢复默认"等破坏性动作只会 dirty 草稿,不会无声覆盖磁盘上的正式版;用户对正式版本有完全控制。
        // PersistencePath 没设时为 null,跟自动落盘一并禁用。
        private string? DraftPath => string.IsNullOrEmpty(_persistencePath) ? null : _persistencePath + ".draft";

        /// <summary>
        /// 嵌入模式开关。
        /// <para>
        /// 默认 false(独立模式):工具栏的"运行蓝图"按钮按原 LaunchEx 路径 —— 把当前画布编译成 ChartBlueprint
        /// 在新预览 Window 中跑(<see cref="BlueprintLauncher"/>),适合 LowCodeDemo 自带的 demo tab。
        /// </para>
        /// <para>
        /// 设 true(嵌入模式):宿主(<see cref="Hevo.Drawing.MultiStockDemo.Views.BlueprintEditorPanel"/> 之类)
        /// 已经接管了 chart 渲染,LaunchEx 那种"另开窗口"语义反而干扰用户。这时按钮文案变"保存",
        /// Click 触发立即落盘(无需等 500ms debounce)。运行蓝图的事交给宿主走 GraphChanged 实时联动。
        /// </para>
        /// </summary>
        public bool IsEmbedded
        {
            get => _isEmbedded;
            set
            {
                if (_isEmbedded == value) return;
                _isEmbedded = value;
                ApplyEmbeddedMode();
            }
        }
        private bool _isEmbedded;

        // 防抖计时器:StateChanged 频繁触发(每帧拖动),debounce 到 500ms 后写一次磁盘。
        private readonly DispatcherTimer _saveDebounce = new(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };

        public LowCodeDemoView()
        {
            InitializeComponent();

            // 全部初始化放 ctor。挪到 Loaded 会在 TabControl 切 tab 时反复触发 ——
            // 画布每次切回都被重置成 sample,Click handler 也会重复订阅(单击 N 次开 N 个预览窗口)。
            GraphViewerBootstrap.Initialize();
            BuiltinRegistration.RegisterAssemblyOf<SinWaveDataSource>();
            // §D2.5.E2E ComputeFeature / HandlerFeature 进 ComponentRegistry + 类别表 ——
            // GraphViewer picker 能选到,蓝图 JSON 里 "TypeName": "ComputeFeature" 能 Resolve。
            // 仅注册 type,Python 解释器懒启 (Run Blueprint 点击时才 EnsureInitialized)。
            PythonNetBootstrap.Register();

            _schema = new GraphEditorSchema();
            _initialLoadInProgress = true;
            try { _schema.DataSource.State = BuildSampleGraph(); }
            finally { _initialLoadInProgress = false; }
            _schema.DataSource.StateChanged += (newState, oldState) =>
            {
                RefreshJsonPreview(newState);
                // framework init 期间 SetInitialGraph / PersistencePath 加载触发的 StateChanged 不算用户编辑,
                // skip debounce 避免 500ms 后 fire stabilized → 宿主 ApplyToCell 重装 schema 的副作用。
                if (_initialLoadInProgress) return;
                // 没人订阅 BlueprintStabilized 也没设 PersistencePath 的实例
                // (默认 demo TabControl 三 tab)零开销 —— 直接 return,不动 timer。
                if (string.IsNullOrEmpty(_persistencePath) && BlueprintStabilized == null) return;
                // GraphInteractionFeature 把所有视觉/数据态都揉进 DataSource.State 写。这里只在
                // **结构变化**时启动 debounce → 触发宿主 ApplyToCell 重装 schema:
                //   - Edges 增删改 / Nodes 增删 / 任一节点的 Properties / 端口集合 / TypeName / Id 变化
                // 纯视觉态(Position 拖节点 / Size / Title / Transform 平移缩放 / Hover / 选中 /
                // RubberBand / BoxSelection / AlignmentGuides)不重装 —— 这些动作不影响 ChartBlueprint
                // 序列化结果,也不会改变 schema 拓扑,重装毫无意义反而把 push 订阅 / K 线 seed 全弄丢。
                if (!IsSchemaRelevantChange(newState, oldState)) return;
                _saveDebounce.Stop();
                _saveDebounce.Start();
            };
            _schema.Interaction.NodeEditRequested += OnNodeEditRequested;
            lowCodeHost.Schema = _schema;
            RefreshJsonPreview(_schema.DataSource.State);

            _saveDebounce.Tick += (_, __) =>
            {
                _saveDebounce.Stop();
                TrySaveToPersistence();
                BlueprintStabilized?.Invoke();
            };

            btnAddDataSource.Click += (_, __) => PickAndAddNode(NodeFactory.Kind.DataSource, "选择数据源");
            btnAddTrait.Click      += (_, __) => PickAndAddNode(NodeFactory.Kind.Trait,      "选择 Trait");
            btnAddFeature.Click    += (_, __) => PickAndAddNode(NodeFactory.Kind.Feature,    "选择 Feature");

            btnExportJson.Click += (_, __) => SaveBlueprintInteractive();
            btnImportJson.Click += (_, __) => OpenBlueprintInteractive();

            btnClear.Click += (_, __) =>
                _schema!.DataSource.ApplyUserEdit(s => GraphState.Empty with { Transform = s.Transform });

            btnAutoLayout.Click += (_, __) =>
            {
                if (_schema == null) return;
                _schema.DataSource.ApplyUserEdit(s =>
                {
                    var nodes = s.Nodes.ToList();
                    var edges = s.Edges.ToList();
                    AutoLayout.Apply(nodes, edges);
                    return s with { Nodes = nodes };
                });
            };

            btnRunBlueprint.Click += (_, __) =>
            {
                if (_schema == null) return;
                var bp = GraphSerializer.ToBlueprint(_schema.DataSource.State);

                // §D2.5.E2E 蓝图含 ComputeFeature 时,handler registry 必须由 DemoPythonHost 提供;
                // 蓝图不含 Python 节点时也无害(ResolveHandlerReferences 只对 Delegate 类型属性查表)。
                // 失败(Python DLL 找不到等)→ 退化成 null,让蓝图能跑但 PyCompute silent skip + console warn。
                // 业务侧提供 HandlersOverride(如 KLineDetailWindow 走 ScannerPythonHost.Registry)直接用,
                // 跳过 demo 内置 Python 路径。
                BlueprintHandlerRegistry? handlers = HandlersOverride;
                if (handlers == null)
                {
                    try { handlers = DemoPythonHost.Registry; }
                    catch (Exception ex)
                    {
                        MessageBox.Show(Window.GetWindow(this),
                            $"Python 运行时启动失败,蓝图里如果有 PyCompute / PyHandler 节点会被跳过:\n{ex.Message}",
                            "Python 不可用",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

                // 节点化协议:DS 的运行时 context 由 DataSourceModel.DefaultContext 在节点里承载,
                // BlueprintLauncher 不再需要顶层 dataSourceContext —— BlueprintRunner 装配时 framework
                // 自动按 leaf DS 的 DefaultContext 调 LoadAsync。dataSourceContext 参数留空。
                var result = BlueprintLauncher.LaunchEx(bp, Window.GetWindow(this),
                    dataSourceContext: null, configureDataSource: ConfigureDataSource, handlers: handlers);
                if (result.Error != null)
                {
                    MessageBox.Show(Window.GetWindow(this), result.Error, "蓝图运行失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else if (result.Diagnostics.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"=== DryRun 诊断 ({result.Diagnostics.Count}) ===");
                    foreach (var d in result.Diagnostics)
                        sb.AppendLine($"[{d.Severity}] {d.Code} | {d.FeatureTypeName}.{d.PortName} -- {d.Message}");
                    MessageBox.Show(Window.GetWindow(this), sb.ToString(), "蓝图诊断",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            };
        }

        private void OnNodeEditRequested(Node node)
        {
            if (_schema == null) return;
            // NodeEditor 多输入区"重新配置"按钮要个 BlueprintHandlerRegistry 枚举候选 handler。
            // 业务侧(MultiStockDemo 等)已经通过 HandlersOverride 给定了自己的 registry +
            // 自己的 PythonNetRuntime,这里就不能再回退去拉 DemoPythonHost.Registry —— 那会
            // 在已初始化的 CPython 进程上再 new 一个 runtime 设 DLL 路径,
            // 抛 "This property must be set before runtime is initialized"。
            // 没 override 时(LowCodeDemo 本身的 demo tab)才用内置的 DemoPythonHost。
            BlueprintHandlerRegistry? handlers = HandlersOverride;
            if (handlers == null)
            {
                try { handlers = DemoPythonHost.Registry; }
                catch { /* Python 离线时 NodeEditor 仍可用,只是"重新配置"按钮枚举不到 handler */ }
            }
            var dlg = new NodeEditorWindow(node, handlers) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.Result != null)
            {
                var updated = dlg.Result;
                _schema.DataSource.ApplyUserEdit(s =>
                {
                    s = s.WithNode(updated);
                    // §D2.X 重新配置多输入指标会替换 InputPorts(child input 槽换名)→ 旧端口对应的连线
                    // 变 dangling,运行时 PortBindings 反射会跳过(找不到端口),但画布上残留视觉错觉。
                    // 这里主动剪掉:任何指向 updated.Id 但 ToPortId 不在 updated.InputPorts 里的边。
                    var validInputIds = new HashSet<string>(updated.InputPorts.Select(p => p.Id), StringComparer.Ordinal);
                    var prunedEdges = s.Edges
                        .Where(e => e.ToNodeId != updated.Id || validInputIds.Contains(e.ToPortId))
                        .ToList();
                    if (prunedEdges.Count != s.Edges.Count)
                        s = s with { Edges = prunedEdges };
                    return s;
                });
            }
        }

        private void PickAndAddNode(NodeFactory.Kind kind, string title)
        {
            if (_schema == null) return;
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
            var canvasCenter = _schema!.DataSource.State.Transform.ScreenToCanvas(screenCenter);

            var sized = NodeFactory.CreateNode(picker.SelectedType, new HevoPoint(0, 0));

            // §D2.X picker UX:节点有 Delegate 属性(ComputeFeature/HandlerFeature/PlotFeature)→
            // 弹"选择 handler"二级窗,单 / 多输入混排,选一个自动 ApplyHandlerSelection 物化。
            // 跳过 / Esc → 节点保持初始态,后续双击编辑器也能改。
            sized = TryAttachHandler(sized, picker.SelectedType);

            var pos   = new HevoPoint(canvasCenter.X - sized.Size.X / 2, canvasCenter.Y - sized.Size.Y / 2);
            var placed = sized with { Position = pos };
            _schema.DataSource.ApplyUserEdit(s => AutoWireOnAdd(s.WithNode(placed), placed));
        }

        /// <summary>
        /// §D2.X 统一 handler picker 入口(单 + 多输入)。流程:
        /// 1. Feature 没 Delegate 属性 → 直接返回原 node(普通装饰 Feature 走旧路径,零侵入)
        /// 2. 弹 <see cref="HandlerPickerWindow"/>,列出 <see cref="DemoPythonHost.Registry"/> 全部 handler
        /// 3. 用户选了 → 走 <see cref="NodeFactory.ApplyHandlerSelection"/>:
        ///    单输入 handler → 只设 Compute/Indicator/Handler 字符串
        ///    多输入 handler → 额外展开 Inputs 端口槽 + InputOrder
        /// 4. 跳过 / Esc → 返回原 node(用户后续双击编辑器再选)
        /// </summary>
        private Node TryAttachHandler(Node node, Type featureType)
        {
            var hasDelegate = featureType
                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Any(p => typeof(Delegate).IsAssignableFrom(p.PropertyType));
            if (!hasDelegate) return node;

            var registry = DemoPythonHost.Registry;
            var picker = new HandlerPickerWindow(featureType.Name, registry.EnumerateAllHandlers())
            {
                Owner = Window.GetWindow(this),
            };
            if (picker.ShowDialog() != true || picker.SelectedHandlerName == null) return node;

            return NodeFactory.ApplyHandlerSelection(node, featureType, picker.SelectedHandlerName, picker.SelectedInputNames);
        }

        /// <summary>
        /// 智能自动接线 —— 添加节点时,如果它有 ROM&lt;double&gt; 输出 / YRangePort 输入,自动连到现有
        /// UniversalAutoScale 节点。避免"加 PyCompute 后忘接 AutoScale → 指标在画布外被裁掉"这类
        /// 黑屏坑(参 低代码.md §8 + DryRun 警告 BP_OUTPUT_NOT_SCALED)。
        /// <para>
        /// 重复连边自动去重(检查 from-to 对)。多 AutoScale 时只连第一个。
        /// </para>
        /// </summary>
        private static GraphState AutoWireOnAdd(GraphState s, Node added)
        {
            var autoScale = s.Nodes.FirstOrDefault(n =>
                n.TypeName.EndsWith("AutoScaleFeature", StringComparison.OrdinalIgnoreCase));
            if (autoScale == null) return s;

            var edges = s.Edges.ToList();
            int auto = 0;

            // ① 新节点的 ROM<double> output → AutoScale.ValuePorts(fan-in 数组)
            var valuePorts = autoScale.InputPorts.FirstOrDefault(p =>
                string.Equals(p.Name, "ValuePorts", StringComparison.Ordinal));
            if (valuePorts != null)
            {
                foreach (var op in added.OutputPorts)
                {
                    if (op.DataTypeName != "ReadOnlyMemory<double>") continue;
                    if (HasEdge(edges, added.Id, op.Id, autoScale.Id, valuePorts.Id)) continue;
                    edges.Add(NewEdge(added.Id, op.Id, autoScale.Id, valuePorts.Id));
                    auto++;
                }
            }

            // ② AutoScale.YRangePort → 新节点的 RealRange 输入(典型 YRangePort)
            var yRangeOut = autoScale.OutputPorts.FirstOrDefault(p =>
                string.Equals(p.Name, "YRangePort", StringComparison.Ordinal));
            if (yRangeOut != null)
            {
                foreach (var ip in added.InputPorts)
                {
                    if (ip.DataTypeName != "RealRange") continue;
                    if (!string.Equals(ip.Name, "YRangePort", StringComparison.Ordinal)) continue;
                    if (HasEdge(edges, autoScale.Id, yRangeOut.Id, added.Id, ip.Id)) continue;
                    edges.Add(NewEdge(autoScale.Id, yRangeOut.Id, added.Id, ip.Id));
                    auto++;
                }
            }

            return auto > 0 ? s with { Edges = edges } : s;
        }

        private static bool HasEdge(List<Edge> edges, string fromNode, string fromPort, string toNode, string toPort)
        {
            foreach (var e in edges)
            {
                if (e.FromNodeId == fromNode && e.FromPortId == fromPort
                 && e.ToNodeId == toNode && e.ToPortId == toPort)
                    return true;
            }
            return false;
        }

        private static Edge NewEdge(string fromNode, string fromPort, string toNode, string toPort)
            => new Edge(
                Id: Guid.NewGuid().ToString("N").Substring(0, 8),
                FromNodeId: fromNode, FromPortId: fromPort,
                ToNodeId:   toNode,   ToPortId:   toPort);

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
            if (_schema == null) return;
            // framework init:宿主灌入业务侧 sample 不算"用户编辑",抑制 debounce stabilize fire,
            // 避免启动期宿主 ApplyToCell 把刚装好的 schema 拆掉重装。
            _initialLoadInProgress = true;
            try { _schema.DataSource.State = state; }
            finally { _initialLoadInProgress = false; }
        }

        /// <summary>
        /// 返回当前画布对应的 ChartBlueprint(GraphSerializer.ToBlueprint 结果)。
        /// 业务侧拿去 inline apply / 自家持久化 / RPC 上传 都行。
        /// 画布未初始化时返回空蓝图(DataSource=null)。
        /// </summary>
        public ChartBlueprint GetCurrentBlueprint()
        {
            if (_schema == null) return new ChartBlueprint();
            return GraphSerializer.ToBlueprint(_schema.DataSource.State);
        }

        /// <summary>
        /// 画布稳定 500ms 后触发(跟 PersistencePath 落盘同 debounce 频率)——
        /// 业务侧 KLineDetailWindow 接它做"用户停手即 inline apply",省去手点"应用"。
        /// 不订阅时 = 仅显式按钮触发应用,不会 silent 重画 inline preview。
        /// </summary>
        public event Action? BlueprintStabilized;

        // 判定 GraphState 变化是否影响序列化后的 ChartBlueprint(= 是否需要宿主重装 schema)。
        // ★ 结构性变化(返 true):Edges 增删改 / Nodes 增删 / Node.Properties / Node 端口 / TypeName / Id
        // ☐ 视觉态变化(返 false):Position / Size / Title / Kind / Category / 以及 GraphState 上的
        //   Transform / SelectedNodeIds / RubberBand / BoxSelection / HoveredPort / AlignmentGuides
        // 拖节点 / 鼠标 hover / pan&zoom / 选中 / reparent layout 重算 都属视觉态,放过不触发 debounce。
        private static bool IsSchemaRelevantChange(GraphState newState, GraphState oldState)
        {
            // Edges 任意变化(增/删/改连线)= record `with { Edges = ... }` 换引用 → 重装
            if (!ReferenceEquals(newState.Edges, oldState.Edges)) return true;
            // Nodes 数量变化(增/删节点)
            if (newState.Nodes.Count != oldState.Nodes.Count) return true;
            // 逐节点判定:拖节点会换 Node 引用(record `with { Position = ... }`),但 Properties /
            // InputPorts / OutputPorts 引用没动,据此过滤"仅 Position/Size/Title 变化"的拖拽场景。
            for (int i = 0; i < newState.Nodes.Count; i++)
            {
                var n = newState.Nodes[i];
                var o = oldState.Nodes[i];
                if (ReferenceEquals(n, o)) continue;  // 整节点 record 没变,快跳
                if (n.Id != o.Id) return true;
                if (n.TypeName != o.TypeName) return true;
                if (!ReferenceEquals(n.Properties, o.Properties)) return true;
                if (!ReferenceEquals(n.InputPorts, o.InputPorts)) return true;
                if (!ReferenceEquals(n.OutputPorts, o.OutputPorts)) return true;
                // Title / Kind / Category / Position / Size 是视觉态,不重装
            }
            return false;
        }

        // PersistencePath 设值时调一次:文件存在 → 读 JSON 反序列化 → 覆盖当前画布。
        // 加载顺序:.draft(用户上次未保存的草稿,优先) → PersistencePath(上次保存的正式版) → 不动(保持工厂)。
        // 任意一层加载失败都会 fallback 下一层,保证用户不会因为一个文件损坏完全失去数据。
        // 直接走 _schema.DataSource.State 赋值(不进 Undo 栈),跟 SetInitialGraph 同语义。
        private void TryLoadFromPersistence()
        {
            if (string.IsNullOrEmpty(_persistencePath) || _schema == null) return;
            // 优先尝试 .draft —— 用户上次未保存的修改不应该丢
            if (TryLoadGraphFromFile(DraftPath, label: ".draft")) return;
            // fallback 正式版本
            TryLoadGraphFromFile(_persistencePath, label: "PersistencePath");
        }

        // 单文件加载尝试:成功返回 true,失败 / 文件不在 / 内容空都返回 false 让 caller fallback。
        // 只 catch 不抛 —— 持久化 bug 不应该把整个 demo 拖崩。
        private bool TryLoadGraphFromFile(string? path, string label)
        {
            if (string.IsNullOrEmpty(path) || _schema == null) return false;
            if (!File.Exists(path)) return false;
            try
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return false;
                var bp = JsonSerializer.Deserialize<ChartBlueprint>(json, _jsonOptions);
                if (bp == null) return false;
                _schema.DataSource.State = GraphDeserializer.FromBlueprint(bp);
                // 节点化协议(2026-05):DefaultContext 已经在 GraphDeserializer.FromBlueprint
                // 里通过 node.Properties["DefaultContext"] 还原到 DS 节点的 Properties 里,
                // 用户双击 DS 节点即可在节点编辑器里改 —— 不再需要回填顶层 tbContext。
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LowCodeDemoView] {label} 加载失败({path}):{ex.Message}");
                return false;
            }
        }

        // 嵌入 / 独立 模式切换工具栏按钮可见性。
        //   独立模式:"运行蓝图"(LaunchEx 另开预览)+ "打开" 都显示 —— demo 完整玩法。
        //   嵌入模式:"运行蓝图" / "打开" 隐藏 —— 渲染 + graph 来源由宿主管,这两个按钮会跟宿主语义打架。
        private void ApplyEmbeddedMode()
        {
            var hidden = System.Windows.Visibility.Collapsed;
            var shown  = System.Windows.Visibility.Visible;
            btnRunBlueprint.Visibility = _isEmbedded ? hidden : shown;
            btnImportJson.Visibility   = _isEmbedded ? hidden : shown;
            // 嵌入模式 PersistencePath 必有 → "保存"立即 commit 草稿;独立模式首次会弹 dialog 选位置。
            btnExportJson.ToolTip = _isEmbedded
                ? "把当前画布固化到 PersistencePath(commit 草稿到正式版本)"
                : "保存当前画布到 JSON 文件。首次弹出选择对话框,记住路径后无打扰落盘";
        }

        /// <summary>
        /// 智能保存:把草稿(.draft)固化到正式 PersistencePath。
        /// <para>
        /// 没 PersistencePath(独立模式首次)→ 弹 SaveFileDialog 选位置;
        /// 有 PersistencePath → 直接 <see cref="CommitDraft"/>:写正式版 + 删 .draft。
        /// </para>
        /// </summary>
        private void SaveBlueprintInteractive()
        {
            if (_schema == null) return;

            if (string.IsNullOrEmpty(_persistencePath))
            {
                var defaultDir = EnsureDefaultBlueprintDir();
                string defaultName = "blueprint";

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "保存蓝图",
                    Filter = "蓝图 JSON (*.json)|*.json|所有文件 (*.*)|*.*",
                    DefaultExt = ".json",
                    InitialDirectory = defaultDir,
                    FileName = defaultName + ".json",
                    OverwritePrompt = true,
                };
                if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

                _persistencePath = dlg.FileName;
                // 跟 setter 走的 TryLoadFromPersistence 不同 —— 这里是新选的空文件路径,不要回头读。
            }

            CommitDraft();
            _saveDebounce.Stop(); // 已经手动 commit,清掉队列里待写的草稿
        }

        /// <summary>
        /// 智能打开:弹 OpenFileDialog 选 JSON 文件,反序列化覆盖当前画布,并把该路径作为后续 PersistencePath。
        /// 嵌入模式下不应使用(宿主已经设了 PersistencePath,加载外部文件会覆盖宿主的 graph 语义)——
        /// <see cref="ApplyEmbeddedMode"/> 在嵌入模式下会把按钮隐藏掉。
        /// </summary>
        private void OpenBlueprintInteractive()
        {
            if (_schema == null) return;

            var defaultDir = EnsureDefaultBlueprintDir();
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "打开蓝图",
                Filter = "蓝图 JSON (*.json)|*.json|所有文件 (*.*)|*.*",
                DefaultExt = ".json",
                InitialDirectory = string.IsNullOrEmpty(_persistencePath) ? defaultDir : Path.GetDirectoryName(_persistencePath) ?? defaultDir,
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

            try
            {
                var json = File.ReadAllText(dlg.FileName);
                var bp = JsonSerializer.Deserialize<ChartBlueprint>(json, _jsonOptions)
                         ?? throw new Exception("反序列化得到 null。");
                _schema.DataSource.ApplyUserEdit(_ => GraphDeserializer.FromBlueprint(bp));
                // 加载成功后,这个文件就是新的"当前文件",后续保存写回这。
                // 注意:不走 PersistencePath setter 是为了避开 setter 内 TryLoadFromPersistence(刚加载完的内容会被它再读一次,无害但多余)。
                _persistencePath = dlg.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), $"加载失败:{ex.Message}", "打开蓝图",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // 默认蓝图目录:LocalAppData/Hevo.Drawing.LowCodeDemo/blueprints/。dialog 首次弹出 / OpenFileDialog 兜底用。
        private static string EnsureDefaultBlueprintDir()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Hevo.Drawing.LowCodeDemo", "blueprints");
            Directory.CreateDirectory(dir);
            return dir;
        }

        // debounce timer tick:把当前画布序列化成蓝图 JSON 写到 .draft(草稿,不动正式版本)。
        // 用户主动点"保存"时 → CommitDraft 把草稿固化到 PersistencePath 正式路径并删 .draft。
        // 父目录不存在自动创建一层。
        private void TrySaveToPersistence()
        {
            var draftPath = DraftPath;
            if (string.IsNullOrEmpty(draftPath) || _schema == null) return;
            try
            {
                var dir = Path.GetDirectoryName(draftPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var bp = GraphSerializer.ToBlueprint(_schema.DataSource.State);
                var json = JsonSerializer.Serialize(bp, _jsonOptions);
                File.WriteAllText(draftPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LowCodeDemoView] .draft 保存失败({draftPath}):{ex.Message}");
            }
        }

        /// <summary>
        /// 把当前画布**直接**写到正式 PersistencePath,然后删除 .draft 草稿。
        /// 不依赖 .draft 是否存在 —— 直接序列化当前画布写正式路径,保证即便草稿丢了/损坏了/timer 还没跑,
        /// 用户点"保存"也能正确把当前内容固化。
        /// </summary>
        private void CommitDraft()
        {
            if (string.IsNullOrEmpty(_persistencePath) || _schema == null) return;
            try
            {
                var dir = Path.GetDirectoryName(_persistencePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var bp = GraphSerializer.ToBlueprint(_schema.DataSource.State);
                var json = JsonSerializer.Serialize(bp, _jsonOptions);
                File.WriteAllText(_persistencePath, json);

                // 草稿已经被正式版本超越,删之。下次自动落盘还会重新生成 .draft。
                var draftPath = DraftPath;
                if (!string.IsNullOrEmpty(draftPath) && File.Exists(draftPath))
                {
                    try { File.Delete(draftPath); }
                    catch (Exception ex) { Console.WriteLine($"[LowCodeDemoView] .draft 删除失败({draftPath}):{ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LowCodeDemoView] PersistencePath commit 失败({_persistencePath}):{ex.Message}");
            }
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
            // 节点化协议:DS 节点必须有 Properties["DefaultContext"] —— 否则 BlueprintRunner.StartLoading
            // 静默跳过 LoadAsync,首屏数据永不发出,cell 永远卡在 IsLoading=true。
            // 任何非空串都行,SinWaveDataSource.OnFetchAsync 不读 context 内容。
            ds.Properties["DefaultContext"] = "demo";

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

            var lineRaw = NodeFactory.CreateNode(typeof(LineSeriesFeature), new HevoPoint(Col(3), laneSeries));
            lineRaw.Properties[nameof(LineSeriesFeature.LayerName)] = "Raw";
            lineRaw.Properties[nameof(LineSeriesFeature.Style)] =
                LineStyle.Create(Color.FromArgb(0x66, 0x4F, 0xC3, 0xF7), thickness: 1.0);

            // §D2.5.E2E PyCompute 节点 —— 把 SinWave.Value 喂给 Python bollinger_upper,输出布林上轨给下面那条线。
            // Properties["Compute"] 是 string,蓝图加载阶段 ResolveHandlerReferences 翻译成
            // Func<ROM<double>, ROM<double>>(从 DemoPythonHost.Registry 查表)。
            // handler 来自 complex_indicators.py(纯 numpy 实现),签名 (ROM<double>) -> ROM<double>。
            var pyMa = NodeFactory.CreateNode(typeof(ComputeFeature), new HevoPoint(Col(4), laneSeries));
            pyMa.Properties[nameof(ComputeFeature.Compute)] = "bollinger_upper";

            var lineMa = NodeFactory.CreateNode(typeof(LineSeriesFeature), new HevoPoint(Col(5), laneSeries));
            lineMa.Properties[nameof(LineSeriesFeature.LayerName)] = "BB Upper";
            lineMa.Properties[nameof(LineSeriesFeature.Style)] =
                LineStyle.Create(Color.FromRgb(0xFF, 0xB7, 0x4D), thickness: 2.0);

            // §D2.6 Pine 风味:PyPlot 节点 = 一个文件 (pine_demo.py @indicator pine_macd) 自动出 line+bar 混合 series。
            // 选 pine_macd(line ×2 + bar ×1)同时验证 PlotFeature 的 LineLayer / BarLayer 双路径,
            // 也是 §F SmartActivator setter 防注入护栏修复后必跑的回归用例。
            var pyPlot = NodeFactory.CreateNode(typeof(PlotFeature), new HevoPoint(Col(6), laneSeries));
            pyPlot.Properties[nameof(PlotFeature.IndicatorName)] = "pine_macd";
            pyPlot.Properties[nameof(PlotFeature.Indicator)]     = "pine_macd";
            // 反射拿不到 indicator 的 series names —— 它来自 Python 装饰器元数据,运行期才知道。
            // sample graph 在编辑器里要画连线,需先把 pyPlot 的 SeriesOutputs.{name} 输出槽物化出来。
            // pine_macd: macd(line) / signal(line) / hist(bar),都是 ROM<double>。
            pyPlot = NodeFactory.ExpandOutputSlots(pyPlot,
                nameof(PlotFeature.SeriesOutputs),
                new[] { "macd", "signal", "hist" },
                typeof(ReadOnlyMemory<double>));

            var interact  = NodeFactory.CreateNode(typeof(ChartInteractionFeature),      new HevoPoint(Col(1), laneInteract));
            var crosshair = NodeFactory.CreateNode(typeof(CrosshairDoubleFeature),       new HevoPoint(Col(2), laneInteract));
            var tooltip   = NodeFactory.CreateNode(typeof(TooltipDoubleWidgetFeature),   new HevoPoint(Col(3), laneInteract));
            var header    = NodeFactory.CreateNode(typeof(UniversalHeaderFeature),       new HevoPoint(Col(4), laneInteract));

            var nodes = new[] { ds, scaleTrait, grid, decor, vpManager, scale, viewportNode, yAxis,
                                lineRaw, pyMa, lineMa, pyPlot, interact, crosshair, tooltip, header };

            var edges = new List<Edge>();

            TryWire(edges, ds, "LogicalLength", viewportNode, "LogicalLength");

            // raw sin wave → 浅蓝色 LineSeries(底图)
            TryWire(edges, ds, "Value", lineRaw, nameof(LineSeriesFeature.DataPort));
            // raw → PyCompute → MA20 LineSeries(橙色加粗)
            TryWire(edges, ds, "Value", pyMa, nameof(ComputeFeature.InputPort));
            TryWire(edges, pyMa, nameof(ComputeFeature.OutputPort), lineMa, nameof(LineSeriesFeature.DataPort));
            // AutoScale.ValuePorts 是 fan-in 数组 —— 必须把所有"想计入 Y 范围的"端口都接进来。
            // 只接 raw 时,如果 PyCompute 输出 RSI(0-100)/ MACD(可负)这种独立尺度的指标,
            // 整个 Y 范围会按 raw 锁死,指标线被裁掉看不见 (低代码.md §8.6 同款"output 端口漏接"坑)。
            TryWire(edges, ds, "Value", scale, nameof(UniversalAutoScaleFeature.ValuePorts));
            TryWire(edges, pyMa, nameof(ComputeFeature.OutputPort), scale, nameof(UniversalAutoScaleFeature.ValuePorts));

            TryWire(edges, scale, nameof(UniversalAutoScaleFeature.YRangePort), lineRaw, nameof(LineSeriesFeature.YRangePort));
            TryWire(edges, scale, nameof(UniversalAutoScaleFeature.YRangePort), lineMa,  nameof(LineSeriesFeature.YRangePort));
            // PyPlot 走相同的 YRange 共享协议(自管 N 个 layers,都 publish 同一份 yRange)
            TryWire(edges, ds,    "Value",                                       pyPlot, nameof(PlotFeature.InputPort));
            TryWire(edges, scale, nameof(UniversalAutoScaleFeature.YRangePort), pyPlot, nameof(PlotFeature.YRangePort));
            // PyPlot 内部 N series 各自走 SeriesOutputs.{name} 一根独立 bar-aligned 端口 —— 每条都接进 AutoScale
            // fan-in,让 Y 范围包含 MACD/Signal/Hist 的极值。Bollinger / RSI / MACD 这类独立尺度指标必须接,
            // 不接会被画到 raw 量程外裁掉。
            TryWire(edges, pyPlot, "SeriesOutputs.macd",   scale, nameof(UniversalAutoScaleFeature.ValuePorts));
            TryWire(edges, pyPlot, "SeriesOutputs.signal", scale, nameof(UniversalAutoScaleFeature.ValuePorts));
            TryWire(edges, pyPlot, "SeriesOutputs.hist",   scale, nameof(UniversalAutoScaleFeature.ValuePorts));
            TryWire(edges, scale, nameof(UniversalAutoScaleFeature.YRangePort), yAxis, nameof(AxisFeature.RangePort));

            TryWire(edges, interact, nameof(ChartInteractionFeature.PointerHitPort), crosshair, nameof(CrosshairDoubleFeature.HitStatePort));
            TryWire(edges, interact, nameof(ChartInteractionFeature.PointerHitPort), tooltip,   nameof(TooltipDoubleWidgetFeature.HitStatePort));
            TryWire(edges, interact, nameof(ChartInteractionFeature.PointerHitPort), header,    nameof(UniversalHeaderFeature.HitStatePort));

            // X 轴数据:用 SinWavePoint.Time(double 序号)而不是 Value(价格)——
            // 接 Value 会让 Tooltip 第一行 "Time" 字段显示价格本身,跟下方 series 的 Y 值看着完全一样,反直觉。
            TryWire(edges, ds, "Time", crosshair, nameof(CrosshairDoubleFeature.XAxisDataPort));
            TryWire(edges, ds, "Time", tooltip,   nameof(TooltipDoubleWidgetFeature.XAxisDataPort));

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
