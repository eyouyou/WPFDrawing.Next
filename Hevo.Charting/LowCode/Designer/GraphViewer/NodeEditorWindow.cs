using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.LowCode.Designer.Converters;
using Hevo.Charting.WorkFlow;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// 节点属性编辑器:针对 Node.TypeName 解析出来的 .NET Type,反射枚举可注入的标量属性 (string / 数值 /
    /// 枚举 / bool / Color),自动按类型选合适控件 (TextBox / CheckBox / ComboBox)。
    /// 双击节点时打开,确定后把改动写回 <see cref="Node.Properties"/> 字典。蓝图序列化端只看 Properties。
    /// </summary>
    public sealed class NodeEditorWindow : Window
    {
        public Node? Result { get; private set; }

        private readonly BlueprintHandlerRegistry? _handlers;
        private Node _node;   // §D2.X "重新配置多输入"按钮会就地 mutate(替换 InputPorts/Properties),Confirm 时一起返
        private readonly Type _runtimeType;
        private readonly Dictionary<string, Func<object?>> _readers = new();
        private StackPanel? _fieldsPanel;   // BuildUi 创建一次,§D2.X 重配后 RefreshFieldsPanel 清空 + 重建

        public NodeEditorWindow(Node node, BlueprintHandlerRegistry? handlers = null)
        {
            _node = node;
            _handlers = handlers;
            // 蓝图侧不依赖类型实例,这里只用反射查 metadata。Resolve 失败时 Type=null 走只读模式。
            _runtimeType = TryResolveType(node.TypeName) ?? typeof(object);

            Title = $"编辑 — {node.Title}";
            Width = 460; Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x29));

            BuildUi();

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { DialogResult = false; Close(); e.Handled = true; }
            };
        }

        private Type? TryResolveType(string typeName)
        {
            try
            {
                var t = ComponentRegistry.Resolve(typeName);
                // 开放泛型 sentinel(Composite<>):编辑器无法在不知道蓝图全文的情况下闭合 TItem ——
                // node-wrap 模式下 TItem 由"哪些 DS 被 UpstreamRefs 引用进来"决定,这条信息在 GraphState
                // 边集合里,不在 _node.Properties 里。编辑器拿不到闭合类型只能放过(IsGenericTypeDefinition 时返 null),
                // 由 BuildFields 跳过 properties 反射展开;运行期由 GraphDeserializer.ResolveCompositeTypeForGraph
                // 走完整蓝图视野的反推。
                return t.IsGenericTypeDefinition ? null : t;
            }
            catch { return null; }
        }

        private void BuildUi()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new StackPanel { Margin = new Thickness(12, 12, 12, 6) };
            header.Children.Add(MakeText($"类型: {_node.TypeName}", 13, FontWeights.SemiBold, 0xE0E6ECu));
            header.Children.Add(MakeText($"ID: {_node.Id}", 11, FontWeights.Normal, 0x8A949Cu));
            grid.Children.Add(header); Grid.SetRow(header, 0);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(8, 0, 8, 6),
            };
            _fieldsPanel = new StackPanel();
            scroll.Content = _fieldsPanel;
            grid.Children.Add(scroll); Grid.SetRow(scroll, 1);

            BuildFields(_fieldsPanel);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8, 4, 8, 8),
            };
            var ok = new Button { Content = "确定", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(4, 0, 0, 0) };
            ok.Click += (_, __) => Confirm();
            var cancel = new Button { Content = "取消", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(4, 0, 0, 0) };
            cancel.Click += (_, __) => { DialogResult = false; Close(); };
            btnPanel.Children.Add(cancel);
            btnPanel.Children.Add(ok);
            grid.Children.Add(btnPanel); Grid.SetRow(btnPanel, 2);

            Content = grid;
        }

        /// <summary>§D2.X 重配 multi-input 后调:清掉 readers 缓存 + 清空 fields 面板 + 重新 BuildFields。</summary>
        private void RefreshFieldsPanel()
        {
            if (_fieldsPanel == null) return;
            _readers.Clear();
            _fieldsPanel.Children.Clear();
            BuildFields(_fieldsPanel);
        }

        private void BuildFields(StackPanel host)
        {
            // §D2.X 任何 Feature 有 Delegate 属性(ComputeFeature/HandlerFeature/PlotFeature)→
            // 插一个"Handler 配置"区块,显示当前 handler 名 + inputs(若有)+ "选择..." 按钮。
            // 单输入 / 多输入统一走 HandlerPickerWindow + NodeFactory.ApplyHandlerSelection。
            var delegateProp = _runtimeType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => typeof(Delegate).IsAssignableFrom(p.PropertyType));
            if (delegateProp != null)
            {
                BuildHandlerSection(host, delegateProp);
            }

            // 1. 反射拿候选属性。规则:public 实例 + 可写 (init 也算) + 类型是可编辑标量。
            //    DataPort<T> 不算 (走连线,不在编辑器里改);ChartFeature 内部状态 (Phase / InstanceId) 不算。
            //    §D2.X Delegate 属性已在顶部 BuildHandlerSection 渲染了 picker,这里跳过避免双显示。
            //    InputOrder 也由 picker 同步管理(单/多输入切换时自动设),不让用户手撸 string[] 出错。
            var props = _runtimeType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                    .Where(IsEditableScalar)
                                    .Where(p => !typeof(Delegate).IsAssignableFrom(
                                        Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType))
                                    .Where(p => p.Name != "InputOrder")
                                    .OrderBy(p => p.DeclaringType == _runtimeType ? 0 : 1) // 自身定义的优先
                                    .ThenBy(p => p.Name)
                                    .ToList();

            // 2. DataSource 节点的 ScalarMappings / VectorMappings 也想编辑——它们以
            //    Dictionary<string,string> 形式存在 Properties 里,单独处理。
            if (_node.Properties.ContainsKey("ScalarMappings") || _node.Properties.ContainsKey("VectorMappings"))
            {
                if (_node.Properties.TryGetValue("ScalarMappings", out var s) && s is IDictionary<string, string> sm)
                    BuildMappingEditor(host, "ScalarMappings", sm);
                if (_node.Properties.TryGetValue("VectorMappings", out var v) && v is IDictionary<string, string> vm)
                    BuildMappingEditor(host, "VectorMappings", vm);
            }

            // Composite<TItem> 派生节点:上游由画布连线表达(从 DS.Stream 拖到 composite.Upstreams 端口),
            // GraphSerializer 把这些 edges 翻译成 DataSourceModel.UpstreamRefs。编辑器里没有"inline upstream spec"概念。

            if (props.Count == 0 && _readers.Count == 0)
            {
                host.Children.Add(MakeText("(无可编辑属性) 该节点没有简单标量字段;复杂字段需在 JSON 里手写。",
                    12, FontWeights.Normal, 0xA0A8B0u, italic: true));
                return;
            }

            foreach (var p in props)
            {
                BuildOneField(host, p);
            }
        }

        /// <summary>
        /// §D2.X Handler 配置区块(单 + 多输入统一)。包含:
        /// - 当前 handler 名 + inputs 列表(若展开过 child input ports)
        /// - "选择 handler..." 按钮 → 弹 HandlerPickerWindow,选了就 ApplyHandlerSelection + RefreshFieldsPanel
        /// </summary>
        private void BuildHandlerSection(StackPanel host, PropertyInfo delegateProp)
        {
            var box = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x24, 0x30)),
                Padding = new Thickness(10),
                Margin = new Thickness(8, 8, 8, 4),
                CornerRadius = new CornerRadius(3),
            };
            var sp = new StackPanel();
            box.Child = sp;

            sp.Children.Add(MakeText($"§D2.X Handler 配置 — {delegateProp.Name}", 12, FontWeights.SemiBold, 0x4FC3F7u));

            string? currentHandler = null;
            if (_node.Properties.TryGetValue(delegateProp.Name, out var v) && v is string s)
                currentHandler = s;

            sp.Children.Add(MakeText(
                currentHandler != null
                    ? $"当前 handler: {currentHandler}"
                    : "(未设置 handler — 选 \"选择 handler...\" 挑一个)",
                11, FontWeights.Normal, 0xE0E6ECu));

            // 当前 inputs(从展开的 child port 名拿,无则按"单输入" / "未展开"提示)
            var dictFields = NodeFactory.ListInputDictFields(_runtimeType);
            if (dictFields.Count > 0)
            {
                var prefix = dictFields[0].FieldName + ".";
                var currentInputs = _node.InputPorts
                    .Where(p => p.Id.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(p => p.Id.Substring(prefix.Length))
                    .ToList();
                sp.Children.Add(MakeText(
                    currentInputs.Count > 0
                        ? $"当前 inputs: [{string.Join(", ", currentInputs)}]  ({currentInputs.Count} args)  — 多输入模式"
                        : "(未展开 input 槽 — 单输入 handler 走 InputPort 字段连线)",
                    11, FontWeights.Normal, 0xB0BEC5u));
            }

            var btn = new Button
            {
                Content = "选择 handler...",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            btn.Click += (_, __) => OnReconfigureHandler(currentHandler);
            sp.Children.Add(btn);

            if (_handlers == null)
            {
                sp.Children.Add(MakeText(
                    "(NodeEditor 没拿到 BlueprintHandlerRegistry —— 按钮不可用。宿主需用 new NodeEditorWindow(node, handlers) 二参 ctor。)",
                    10.5, FontWeights.Normal, 0xEF5350u, italic: true));
                btn.IsEnabled = false;
            }

            host.Children.Add(box);
        }

        private void OnReconfigureHandler(string? currentHandler)
        {
            if (_handlers == null) return;
            var picker = new HandlerPickerWindow(
                _node.TypeName,
                _handlers.EnumerateAllHandlers(),
                currentHandler)
            {
                Owner = this,
            };
            if (picker.ShowDialog() != true || picker.SelectedHandlerName == null) return;

            _node = NodeFactory.ApplyHandlerSelection(
                _node, _runtimeType, picker.SelectedHandlerName, picker.SelectedInputNames);
            RefreshFieldsPanel();
        }

        // ===== 接口实例选择器 =====
        // 用途:trait/feature 的 ctor 参数或属性是接口类型(如 ScaleStrategyTrait 的 IScale DomainScale/ValueScale)
        //       时,蓝图无法 JSON 表达接口构造,只能从已有的 public static 单例里挑一个。
        // 实现:扫接口所在程序集所有实现类型,收集每个类的 public static 字段/属性 (类型可赋值给该接口),
        //       展示为 "TypeName.MemberName" 格式的下拉。覆盖大多数业务 Scale / Strategy 等"预设单例"模式。
        // 缓存:接口 → 候选实例列表,在 _interfaceInstanceCache 里按需懒加载。
        private static readonly Dictionary<Type, List<(string Name, object Instance)>> _interfaceInstanceCache = new();

        private static List<(string Name, object Instance)> EnumerateInterfaceInstances(Type interfaceType)
        {
            if (_interfaceInstanceCache.TryGetValue(interfaceType, out var cached)) return cached;
            var result = new List<(string, object)>();
            const BindingFlags F = BindingFlags.Public | BindingFlags.Static;
            // 接口所在程序集就够了(Hevo.Charting),业务自带 IScale 子类极罕见。
            // 真有此需求时这里改成全 AppDomain 扫即可。
            foreach (var implType in interfaceType.Assembly.GetTypes())
            {
                if (!interfaceType.IsAssignableFrom(implType)) continue;
                if (implType.IsInterface || implType.IsAbstract) continue;
                foreach (var f in implType.GetFields(F))
                {
                    if (!interfaceType.IsAssignableFrom(f.FieldType)) continue;
                    var v = f.GetValue(null);
                    if (v != null) result.Add(($"{implType.Name}.{f.Name}", v));
                }
                foreach (var p in implType.GetProperties(F))
                {
                    if (p.GetMethod == null) continue;
                    if (!interfaceType.IsAssignableFrom(p.PropertyType)) continue;
                    var v = p.GetValue(null);
                    if (v != null) result.Add(($"{implType.Name}.{p.Name}", v));
                }
            }
            result.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
            _interfaceInstanceCache[interfaceType] = result;
            return result;
        }

        private static FrameworkElement MakeInterfaceInstancePicker(Type interfaceType, object? current, out Func<object?> reader)
        {
            var instances = EnumerateInterfaceInstances(interfaceType);
            if (instances.Count == 0)
            {
                reader = () => current;
                return MakeText($"[{interfaceType.Name}] 找不到任何 public static 单例,无法配置。",
                    11, FontWeights.Normal, 0xA0A8B0u, italic: true);
            }
            // 当前值 → 在候选列表里按引用相等找选中项,失败回落到第一个
            int selectedIdx = 0;
            for (int i = 0; i < instances.Count; i++)
            {
                if (ReferenceEquals(instances[i].Instance, current)) { selectedIdx = i; break; }
            }
            var combo = new ComboBox
            {
                ItemsSource = instances.Select(x => x.Name).ToList(),
                SelectedIndex = selectedIdx,
            };
            reader = () =>
            {
                int idx = combo.SelectedIndex;
                return idx >= 0 && idx < instances.Count ? instances[idx].Instance : null;
            };
            return combo;
        }

        // §9.3 通用多态 picker:converter 自带 Kinds 列表 → 下拉 + 嵌套子属性编辑器递归编辑选中 kind 的字段。
        // 跟 MakeInterfaceInstancePicker 的差别:不要求 impl 自挂 public static 单例;带参实现(FixedWindowZoomStrategy
        // 等)也能进下拉,改完字段后 picker 反射 SetValue 把 sub-readers 的值刷到 factory new 出来的 instance 上。
        private static readonly Dictionary<Type, IPolymorphicJsonConverter?> _polyConverterCache
            = new();

        private static IPolymorphicJsonConverter? FindPolymorphicConverter(Type targetType)
        {
            if (_polyConverterCache.TryGetValue(targetType, out var cached)) return cached;
            // BlueprintJsonOptions.Default 是序列化的 single source of truth,所有 polymorphic converter
            // 一定在它的 Converters 列表里(注册路径就一条)。OfType + FirstOrDefault 一次 O(n) 查找,n≈5,缓存后零开销。
            IPolymorphicJsonConverter? hit = null;
            foreach (var c in BlueprintJsonOptions.Default.Converters)
            {
                if (c is IPolymorphicJsonConverter poly && poly.TargetType == targetType)
                {
                    hit = poly;
                    break;
                }
            }
            _polyConverterCache[targetType] = hit;
            return hit;
        }

        private FrameworkElement MakePolymorphicPicker(IPolymorphicJsonConverter conv, object? current, out Func<object?> reader)
        {
            var kinds = conv.Kinds;
            if (kinds.Count == 0)
            {
                reader = () => current;
                return MakeText($"[{conv.TargetType.Name}] converter Kinds 为空,无法配置。",
                    11, FontWeights.Normal, 0xA0A8B0u, italic: true);
            }

            var stack = new StackPanel();
            var combo = new ComboBox { ItemsSource = kinds.Select(k => k.Label).ToList() };
            // 嵌套子属性编辑器容器 —— 用户在 combo 改 kind 时清空 + 用 factory 默认实例重建
            var subPanel = new StackPanel { Margin = new Thickness(12, 4, 0, 0) };
            stack.Children.Add(combo);
            stack.Children.Add(subPanel);

            // 当前值 → 按 IsInstanceOfType 匹配 kind(支持子类),失败回落到第一个
            int selectedIdx = 0;
            if (current != null)
            {
                for (int i = 0; i < kinds.Count; i++)
                {
                    if (kinds[i].ImplType.IsInstanceOfType(current)) { selectedIdx = i; break; }
                }
            }

            var subReaders = new Dictionary<string, Func<object?>>(StringComparer.Ordinal);

            void Rebuild(object instance)
            {
                subPanel.Children.Clear();
                subReaders.Clear();
                foreach (var prop in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!IsEditableScalar(prop)) continue;
                    object? currentVal;
                    try { currentVal = prop.GetValue(instance); } catch { continue; }

                    var fieldBox = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
                    fieldBox.Children.Add(MakeText(prop.Name, 11, FontWeights.SemiBold, 0xC0C8D0u));
                    // 递归调 MakeEditorByType —— 子属性可以是任何已支持类型(原子标量 / 嵌套 ConfigGroup /
                    // 甚至嵌套 polymorphic,如 HistoryAwareZoomStrategy.DuringPaging:IZoomStrategy)
                    var subEditor = MakeEditorByType(prop.PropertyType, currentVal, out var subReader);
                    fieldBox.Children.Add(subEditor);
                    subPanel.Children.Add(fieldBox);
                    subReaders[prop.Name] = subReader;
                }
            }

            // 先设 SelectedIndex 再挂事件 —— 否则 ComboBox 的 SelectedIndex setter 会 fire SelectionChanged,
            // 把"用 current 重建" overwrite 成"用 factory 默认重建",WindowBars=200 退回 100。
            combo.SelectedIndex = selectedIdx;
            Rebuild(current ?? kinds[selectedIdx].Factory());
            combo.SelectionChanged += (_, _) =>
            {
                int idx = combo.SelectedIndex;
                if (idx < 0 || idx >= kinds.Count) return;
                // 用户改 kind → 重建,起始字段值 = factory 默认。原 current 已无意义(类型不匹配新 kind)。
                Rebuild(kinds[idx].Factory());
            };

            reader = () =>
            {
                int idx = combo.SelectedIndex;
                if (idx < 0 || idx >= kinds.Count) return current;
                var instance = kinds[idx].Factory();
                // 用反射把 sub-readers 的值刷到 instance —— init-only 属性 reflection SetValue 仍可写穿透。
                // get-only 属性 (如 StaticBrushResolver<double>.ConstantBrush) prop.CanWrite=false,
                // 已经在 IsEditableScalar 阶段被过滤,不会进 subReaders 字典,无需在这里再防。
                foreach (var kv in subReaders)
                {
                    var prop = instance.GetType().GetProperty(kv.Key);
                    if (prop == null || !prop.CanWrite) continue;
                    try { prop.SetValue(instance, kv.Value()); }
                    catch (Exception ex)
                    {
                        // 子 reader 返回不兼容类型时 SetValue 抛 ArgumentException 等 —— 容忍并保留 factory 默认值。
                        System.Diagnostics.Debug.WriteLine(
                            $"[PolymorphicPicker] SetValue {prop.Name} on {instance.GetType().Name} 失败: {ex.Message}");
                    }
                }
                return instance;
            };
            return stack;
        }

        // 单个属性编辑控件;reader 把当前 UI 值翻译回原始类型,Confirm 时写入 Properties。
        private void BuildOneField(StackPanel host, PropertyInfo prop)
        {
            var box = new StackPanel { Margin = new Thickness(8, 8, 8, 4) };

            var label = MakeText(prop.Name, 12, FontWeights.SemiBold, 0xC0C8D0u);
            box.Children.Add(label);

            // 注释:[Description] 优先,辅以 PrettyTypeName
            var desc = prop.GetCustomAttribute<DescriptionAttribute>()?.Description;
            var typeStr = NodeFactory.PrettyTypeName(prop.PropertyType);
            box.Children.Add(MakeText(desc != null ? $"{typeStr}  —  {desc}" : typeStr,
                10.5, FontWeights.Normal, 0x8A949Cu));

            // 当前值 (优先 Properties 里的覆盖,缺失时 fall back 到运行时实例的默认值)
            object? currentValue = _node.Properties.TryGetValue(prop.Name, out var v)
                ? v
                : TryGetDefault(prop);

            FrameworkElement editor = MakeEditor(prop, currentValue, out var reader);
            editor.Margin = new Thickness(0, 4, 0, 0);
            box.Children.Add(editor);
            host.Children.Add(box);

            _readers[prop.Name] = reader;
        }

        // ScalarMappings / VectorMappings 按字典编辑——每行 "字段名 → portId"。
        // PortBindings 不在编辑器里直接编辑 (它由画布连线反推,见 GraphSerializer.ToBlueprint)。
        private void BuildMappingEditor(StackPanel host, string title, IDictionary<string, string> dict)
        {
            var box = new StackPanel { Margin = new Thickness(8, 12, 8, 4) };
            box.Children.Add(MakeText(title, 12, FontWeights.SemiBold, 0xC0C8D0u));
            box.Children.Add(MakeText("Key = 数据源字段名;Value = 全局 portId (蓝图运行时的连线锚点)。",
                10.5, FontWeights.Normal, 0x8A949Cu));

            var rowsPanel = new StackPanel();
            var inputs = new List<(TextBox key, TextBox value)>();

            void AddRow(string k, string v)
            {
                var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var keyTb = MakeTextBox(k);
                var valTb = MakeTextBox(v);
                row.Children.Add(keyTb); Grid.SetColumn(keyTb, 0);
                row.Children.Add(valTb); Grid.SetColumn(valTb, 2);
                rowsPanel.Children.Add(row);
                inputs.Add((keyTb, valTb));
            }

            foreach (var kv in dict) AddRow(kv.Key, kv.Value);

            var addBtn = new Button { Content = "+ 添加映射", Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            addBtn.Click += (_, __) => AddRow("", "");

            box.Children.Add(rowsPanel);
            box.Children.Add(addBtn);
            host.Children.Add(box);

            _readers[title] = () =>
            {
                var d = new Dictionary<string, string>();
                foreach (var (k, val) in inputs)
                {
                    var keyText = k.Text?.Trim() ?? "";
                    if (keyText.Length == 0) continue;
                    d[keyText] = val.Text?.Trim() ?? "";
                }
                return d;
            };
        }

        // Inline Upstreams 编辑器已移除 —— Composite 上游统一由画布拖线(node-wrap):
        // 从 DS.Stream 拖到 composite.Upstreams 端口,GraphSerializer 把这些 edges 翻译成 DataSourceModel.UpstreamRefs。


        // 类型 → 控件;reader 是"把 UI 拿出来翻译成 prop.PropertyType 兼容值"的闭包。
        private FrameworkElement MakeEditor(PropertyInfo prop, object? value, out Func<object?> reader)
            => MakeEditorByType(prop.PropertyType, value, out reader);

        // 按类型派编辑器(可递归)。MakeEditor 在节点级入口包了一层 PropertyInfo,实现复用此处。
        private FrameworkElement MakeEditorByType(Type t, object? value, out Func<object?> reader)
        {
            var underlying = Nullable.GetUnderlyingType(t) ?? t;

            if (underlying == typeof(bool))
            {
                var cb = new CheckBox
                {
                    IsChecked = value is bool b ? b : false,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE6, 0xEC)),
                    Content = "true",
                };
                reader = () => cb.IsChecked == true;
                return cb;
            }
            if (underlying.IsEnum)
            {
                var combo = new ComboBox { ItemsSource = Enum.GetValues(underlying), SelectedItem = value };
                reader = () => combo.SelectedItem;
                return combo;
            }
            if (underlying == typeof(Color))
            {
                return MakeColorEditor(value as Color?, out reader);
            }
            if (underlying == typeof(ChartLength))
            {
                return MakeChartLengthEditor(value as ChartLength?, out reader);
            }
            if (typeof(IHevoBrush).IsAssignableFrom(underlying))
            {
                // 两路:HevoSolidBrush(选 Color) / HevoResourceBrush(填 Key)。
                return MakeBrushEditor(value as IHevoBrush, out reader);
            }
            // §9.3 通用多态 picker:查 BlueprintJsonOptions 注册的 IPolymorphicJsonConverter,
            // 命中即用"kind 下拉 + 嵌套子属性编辑器"渲染。覆盖 IZoomStrategy / IHevoString / IBrushResolver<double> 等。
            // 必须在 ConfigGroup / InterfaceInstancePicker 之前 —— 这两条是更通用的 fallback。
            {
                var polyConv = FindPolymorphicConverter(underlying);
                if (polyConv != null)
                    return MakePolymorphicPicker(polyConv, value, out reader);
            }
            if (IsConfigGroup(underlying))
            {
                // record / 含主构造的 struct:把 ctor params 当成"配置组"展开,层层钻进编辑。
                return MakeConfigGroupEditor(underlying, value, out reader);
            }
            if (underlying.IsInterface && EnumerateInterfaceInstances(underlying).Count > 0)
            {
                // 接口类型,扫程序集里所有 public static 单例展示为下拉。
                // 典型:ScaleStrategyTrait 的 IScale DomainScale / ValueScale → CategoryScale.Edge / LinearScale.Instance / ...
                return MakeInterfaceInstancePicker(underlying, value, out reader);
            }
            if (typeof(Delegate).IsAssignableFrom(underlying))
            {
                // 协议扩展 §K:Delegate 字段在蓝图层用 string handler 名表达。
                // value 大概率是 string(从 Properties 字典)或 null;万一是 Delegate 实例显示占位符。
                string initial = value switch
                {
                    string s => s,
                    Delegate => "<已绑定委托,改写下方文本框换成 handler 名>",
                    _ => "",
                };
                var tb = MakeTextBox(initial);
                reader = () =>
                {
                    var s = tb.Text?.Trim() ?? "";
                    return string.IsNullOrEmpty(s) ? null : s;
                };
                return tb;
            }

            // 默认走 TextBox + 类型转换 (走 TypeConverter 兜底,跟 SmartActivator 一致)。
            // NaN / Infinity 这种"程序员默认值"对编辑器用户毫无意义,而且回写后 JSON 序列化会炸,
            // 一律按空串显示;用户留空 → CoerceTextValue 返回 null → Confirm 路径检测到 null 删 key
            // (见 Confirm 内注释)→ InjectProperties 不 fire setter,init 默认值保留。
            // 这条链条 fail-fast 友好 —— 不再依赖 SmartActivator silent-swallow 兜底。
            var text = MakeTextBox(IsSpecialFloat(value) ? "" : (value?.ToString() ?? ""));
            reader = () => CoerceTextValue(text.Text, underlying);
            return text;
        }

        private static bool IsSpecialFloat(object? v) => v switch
        {
            double d when double.IsNaN(d) || double.IsInfinity(d) => true,
            float f when float.IsNaN(f) || float.IsInfinity(f) => true,
            _ => false,
        };

        // ===== 配置组(Record / 结构化值类型)展开编辑 =====
        // 适用对象:
        //   1. record class / record struct (有"主构造函数")
        //   2. 普通 class / struct,只有一个公共构造函数,且每个 ctor 参数都能找到同名 public 属性
        //
        // 核心思路:用主构造函数的 ParameterInfo 列表当 schema,递归 MakeEditorByType 派子编辑器,
        // 提交时按 ctor 重新 new 出新实例。
        private static bool IsConfigGroup(Type t)
        {
            if (t.IsPrimitive || t.IsEnum) return false;
            if (t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(TimeSpan))
                return false;
            // 排除前面已经特别处理的几个
            if (t == typeof(Color) || t == typeof(Hevo.Charting.Core.ChartLength)) return false;
            if (typeof(IHevoBrush).IsAssignableFrom(t)) return false;

            return PrimaryCtor(t) != null;
        }

        private static ConstructorInfo? PrimaryCtor(Type t)
        {
            var ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            if (ctors.Length == 0) return null;
            // 优先挑参数最多的 ctor (record 的 primary ctor / 业务类的核心 ctor 通常是)
            var ctor = ctors.OrderByDescending(c => c.GetParameters().Length).First();
            // 每个参数必须有同名 public 属性 (record / DTO 模式)
            foreach (var p in ctor.GetParameters())
            {
                if (string.IsNullOrEmpty(p.Name)) return null;
                var prop = t.GetProperty(p.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop == null) return null;
            }
            return ctor;
        }

        private FrameworkElement MakeConfigGroupEditor(Type type, object? value, out Func<object?> reader)
        {
            var ctor = PrimaryCtor(type);
            if (ctor == null)
            {
                reader = () => value;
                return MakeText($"[无可用主构造] {type.Name}", 12, FontWeights.Normal, 0xA0A8B0u, italic: true);
            }

            // 蓝图 JSON round-trip 后 Properties 里的复合对象会变成 JsonElement / Dictionary<string,object?>,
            // 不是 type 的实例 —— 直接 prop.GetValue(value) 会抛 TargetException。一律降级到"无当前值",
            // 子字段走类型默认显示,用户可重新填一遍再 ctor.Invoke 重组出真正的 type 实例。
            if (value != null && !type.IsInstanceOfType(value)) value = null;

            var border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x35, 0x3A)),
                BorderThickness = new Thickness(1, 0, 0, 0),
                Margin = new Thickness(8, 2, 0, 4),
                Padding = new Thickness(8, 2, 0, 2),
            };
            var stack = new StackPanel();
            border.Child = stack;

            stack.Children.Add(MakeText(type.Name, 11, FontWeights.SemiBold, 0x90A4AEu, italic: true));

            var paramReaders = new List<(ParameterInfo p, Func<object?> r)>();
            foreach (var pi in ctor.GetParameters())
            {
                if (string.IsNullOrEmpty(pi.Name)) continue;   // PrimaryCtor 已经过滤,这里只是再确认一遍
                var prop = type.GetProperty(pi.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                object? current = value != null && prop != null ? prop.GetValue(value) : null;

                var row = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
                row.Children.Add(MakeText(pi.Name, 11.5, FontWeights.SemiBold, 0xC0C8D0u));
                var subDesc = prop?.GetCustomAttribute<DescriptionAttribute>()?.Description;
                if (!string.IsNullOrEmpty(subDesc))
                    row.Children.Add(MakeText(subDesc, 10.5, FontWeights.Normal, 0x8A949Cu));

                var subEditor = MakeEditorByType(pi.ParameterType, current, out var subReader);
                row.Children.Add(subEditor);
                stack.Children.Add(row);

                paramReaders.Add((pi, subReader));
            }

            reader = () =>
            {
                var args = new object?[paramReaders.Count];
                for (int i = 0; i < paramReaders.Count; i++)
                {
                    var raw = paramReaders[i].r();
                    var pt = paramReaders[i].p.ParameterType;
                    args[i] = raw ?? (paramReaders[i].p.HasDefaultValue ? paramReaders[i].p.DefaultValue : DefaultOf(pt));
                }
                try { return ctor.Invoke(args); }
                catch { return value; } // 构造失败保留原值,UI 不崩
            };
            return border;
        }

        private static object? DefaultOf(Type t)
            => t.IsValueType ? Activator.CreateInstance(t) : null;

        // ===== Color 编辑控件 =====
        private static FrameworkElement MakeColorEditor(Color? initial, out Func<object?> reader)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            var tb = MakeTextBox(initial.HasValue ? $"#{initial.Value.R:X2}{initial.Value.G:X2}{initial.Value.B:X2}" : "");
            var preview = new Border
            {
                Margin = new Thickness(6, 0, 0, 0),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x5C, 0x66)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(initial ?? Colors.Transparent),
            };
            tb.TextChanged += (_, __) =>
            {
                if (TryParseColor(tb.Text, out var col))
                    preview.Background = new SolidColorBrush(col);
            };
            grid.Children.Add(tb); Grid.SetColumn(tb, 0);
            grid.Children.Add(preview); Grid.SetColumn(preview, 1);
            reader = () => string.IsNullOrWhiteSpace(tb.Text) ? null : (TryParseColor(tb.Text, out var c) ? c : (object?)null);
            return grid;
        }

        // ===== ChartLength 编辑控件 (UnitType + Value 二元组) =====
        private static FrameworkElement MakeChartLengthEditor(ChartLength? initial, out Func<object?> reader)
        {
            var unit = initial?.UnitType ?? ChartUnitType.Pixel;
            var val = initial?.Value ?? 0;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var combo = new ComboBox { ItemsSource = Enum.GetValues(typeof(ChartUnitType)), SelectedItem = unit };
            var tb = MakeTextBox(val.ToString("R"));
            grid.Children.Add(combo); Grid.SetColumn(combo, 0);
            grid.Children.Add(tb);    Grid.SetColumn(tb, 2);

            reader = () =>
            {
                var u = combo.SelectedItem is ChartUnitType uu ? uu : ChartUnitType.Pixel;
                if (!double.TryParse(tb.Text, out var v)) v = 0;
                return u switch
                {
                    ChartUnitType.Pixel => ChartLength.Pixel(v),
                    ChartUnitType.Star  => ChartLength.Star(v),
                    ChartUnitType.Auto  => ChartLength.Auto(),
                    _                   => ChartLength.Pixel(v),
                };
            };
            return grid;
        }

        // ===== IHevoBrush 编辑控件 =====
        // 两路:
        //   1. 纯色 (HevoSolidBrush) — 直接选颜色
        //   2. 资源 (HevoResourceBrush) — 写资源 Key (运行时由 WpfRenderRegistry 解析为 DynamicResource)
        // (LinearGradient 等其他子类不在此处理,业务 JSON 自己写。)
        private enum BrushKind { None, Solid, Resource }

        private static FrameworkElement MakeBrushEditor(IHevoBrush? initial, out Func<object?> reader)
        {
            // 推断当前 brush 属于哪一类
            BrushKind kind0 = initial switch
            {
                HevoSolidBrush     => BrushKind.Solid,
                HevoResourceBrush  => BrushKind.Resource,
                null               => BrushKind.None,
                _                  => BrushKind.None, // 不识别的子类按 None 处理 (清空)
            };
            Color initialColor = initial is HevoSolidBrush s ? s.Color : Colors.Gray;
            string initialKey  = initial is HevoResourceBrush r ? r.ResourceKey : "";

            var box = new StackPanel();
            var combo = new ComboBox
            {
                ItemsSource = Enum.GetValues(typeof(BrushKind)),
                SelectedItem = kind0,
                Margin = new Thickness(0, 0, 0, 4),
            };
            box.Children.Add(combo);

            // 两套子编辑器,按 combo 选择动态显隐
            var solidPanel = new ContentControl();
            var resourcePanel = new ContentControl();
            box.Children.Add(solidPanel);
            box.Children.Add(resourcePanel);

            var colorEditor = MakeColorEditor(initialColor, out var colorReader);
            solidPanel.Content = colorEditor;

            var keyTb = MakeTextBox(initialKey);
            var resPanel = new StackPanel();
            resPanel.Children.Add(MakeText("资源 Key (DynamicResource 名,如 \"BrushKeys.F1\")",
                10.5, FontWeights.Normal, 0x8A949Cu));
            resPanel.Children.Add(keyTb);
            resourcePanel.Content = resPanel;

            void Refresh()
            {
                var sel = combo.SelectedItem is BrushKind k ? k : BrushKind.None;
                solidPanel.Visibility    = sel == BrushKind.Solid    ? Visibility.Visible : Visibility.Collapsed;
                resourcePanel.Visibility = sel == BrushKind.Resource ? Visibility.Visible : Visibility.Collapsed;
            }
            combo.SelectionChanged += (_, __) => Refresh();
            Refresh();

            reader = () =>
            {
                var sel = combo.SelectedItem is BrushKind k ? k : BrushKind.None;
                switch (sel)
                {
                    case BrushKind.Solid:
                        var v = colorReader();
                        return v is Color c ? new HevoSolidBrush(c) : (object?)null;
                    case BrushKind.Resource:
                        var key = keyTb.Text?.Trim() ?? "";
                        return key.Length == 0 ? null : new HevoResourceBrush(key);
                    default:
                        return null;
                }
            };
            return box;
        }

        private static object? CoerceTextValue(string text, Type targetType)
        {
            text = text?.Trim() ?? "";
            if (text.Length == 0) return null;
            try
            {
                var converter = TypeDescriptor.GetConverter(targetType);
                if (converter.CanConvertFrom(typeof(string))) return converter.ConvertFromString(text);
                return Convert.ChangeType(text, targetType);
            }
            catch { return text; } // 留原文,保存时让 SmartActivator 自己再转一遍
        }

        private static string ColorToHex(object? v)
        {
            if (v is Color c) return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            if (v is string s) return s;
            return "";
        }

        private static bool TryParseColor(string text, out Color c)
        {
            try
            {
                var converted = ColorConverter.ConvertFromString(text?.Trim());
                if (converted is Color cc) { c = cc; return true; }
            }
            catch { }
            c = default;
            return false;
        }

        private static bool IsEditableScalar(PropertyInfo p)
        {
            // 必须可写。init 也算 (init 受编译器约束,反射 SetValue 仍可)。
            if (!p.CanWrite) return false;

            // DataPort<T> 走连线不走编辑器;DataPort<T>[] 数组扇入也走连线
            if (p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DataPort<>)) return false;
            if (p.PropertyType.IsArray)
            {
                var elem = p.PropertyType.GetElementType();
                if (elem != null && elem.IsGenericType && elem.GetGenericTypeDefinition() == typeof(DataPort<>)) return false;
            }

            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (t == typeof(string)) return true;
            if (t == typeof(bool)) return true;
            if (t == typeof(Color)) return true;
            if (t == typeof(ChartLength)) return true;        // env feature 布局参数
            if (typeof(IHevoBrush).IsAssignableFrom(t)) return true; // 笔刷(SolidBrush / ResourceBrush)
            if (t.IsEnum) return true;
            if (t.IsPrimitive) return true;       // int/long/float/double/byte/char etc.
            if (t == typeof(decimal)) return true;
            if (t == typeof(DateTime) || t == typeof(TimeSpan)) return true;
            // 协议扩展 §K:Delegate 字段在蓝图层用 "string handler 名" 表达,
            // 加载时 ResolveHandlerReferences 翻译为已注册委托。编辑器走 string TextBox。
            if (typeof(Delegate).IsAssignableFrom(t)) return true;
            // record / DTO 主构造可重组的类型 → 走配置组编辑器(AxisStyleTrait / LineStyle / FieldMeta 等)
            if (IsConfigGroup(t)) return true;
            // §9.3 接口/抽象类有 polymorphic converter 注册 → 走通用多态 picker (kind 下拉 + 嵌套子编辑器)。
            // 优先级高于"扫静态单例"路径,带参实现也能进下拉。
            if (FindPolymorphicConverter(t) != null) return true;
            // 接口类型且扫得到 public static 单例 → 走接口实例选择器
            // (典型 IScale: CategoryScale.Edge / LinearScale.Instance / ...)
            if (t.IsInterface && EnumerateInterfaceInstances(t).Count > 0) return true;
            return false;
        }

        // init-only 反射 SetValue 在 .NET 5+ 仍可写穿透——此处仅用 GetValue 拿默认值,无副作用。
        private static object? TryGetDefault(PropertyInfo p)
        {
            try
            {
                var inst = Activator.CreateInstance(p.DeclaringType!);
                return p.GetValue(inst);
            }
            catch { return null; }
        }

        private void Confirm()
        {
            // 写回 Properties:reader 给的值原样塞进字典 (Color / Enum / 数值 全部保留原始 .NET 类型,
            // 序列化阶段交给 SmartActivator + System.Text.Json 再做一次 normalize)。
            //
            // 💥 null reader 值删 key,而不是写 null。
            //   场景:用户清空数值文本框 → CoerceTextValue 返回 null → 表示"恢复默认"。
            //   旧做法:写 null 进 Properties,加载时 InjectProperties 调 CoerceValue(null, double) → null →
            //          setter 把 null unbox 到 double 抛 NRE → 外层 swallow → 默认值保留(全链条 silent error 兜底)。
            //   新做法:Properties 干脆不带这个 key,装配期 InjectProperties 根本不 fire setter,init 默认值天然保留。
            //          跟 ComponentRegistry.InjectProperties 的 fail-fast 契约对齐 ——
            //          再有 null → 非 Nullable 值类型 出现就是真错,立刻抛。
            var newProps = new Dictionary<string, object?>(_node.Properties);
            foreach (var kv in _readers)
            {
                var v = kv.Value();
                if (v == null) newProps.Remove(kv.Key);
                else newProps[kv.Key] = v;
            }
            Result = _node with { Properties = newProps };
            DialogResult = true;
            Close();
        }

        private static TextBlock MakeText(string text, double fontSize, FontWeight weight, uint argbRgb, bool italic = false)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                FontWeight = weight,
                FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
                Foreground = new SolidColorBrush(Color.FromRgb((byte)((argbRgb >> 16) & 0xFF), (byte)((argbRgb >> 8) & 0xFF), (byte)(argbRgb & 0xFF))),
                TextWrapping = TextWrapping.Wrap,
            };
        }

        private static TextBox MakeTextBox(string initial)
        {
            return new TextBox
            {
                Text = initial,
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x1A)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE6, 0xEC)),
                CaretBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x35, 0x3A)),
                Padding = new Thickness(6, 3, 6, 3),
            };
        }
    }
}
