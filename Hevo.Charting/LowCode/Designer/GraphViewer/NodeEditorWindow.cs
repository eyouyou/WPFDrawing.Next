using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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

        private readonly Node _node;
        private readonly Type _runtimeType;
        private readonly Dictionary<string, Func<object?>> _readers = new();

        public NodeEditorWindow(Node node)
        {
            _node = node;
            // 蓝图侧不依赖类型实例,这里只用反射查 metadata。Resolve 失败时 Type=null 走只读模式。
            _runtimeType = TryResolveType(node.TypeName) ?? typeof(object);

            Title = $"编辑 — {node.Title}";
            Width = 460; Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x29));

            BuildUi();
        }

        private static Type? TryResolveType(string typeName)
        {
            try { return ComponentRegistry.Resolve(typeName); }
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
            var fields = new StackPanel();
            scroll.Content = fields;
            grid.Children.Add(scroll); Grid.SetRow(scroll, 1);

            BuildFields(fields);

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

        private void BuildFields(StackPanel host)
        {
            // 1. 反射拿候选属性。规则:public 实例 + 可写 (init 也算) + 类型是可编辑标量。
            //    DataPort<T> 不算 (走连线,不在编辑器里改);ChartFeature 内部状态 (Phase / InstanceId) 不算。
            var props = _runtimeType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                    .Where(IsEditableScalar)
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

        // PortBindings 字段(ScalarMappings / VectorMappings)按字典编辑——每行 "字段名 → portId"。
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

            // 默认走 TextBox + 类型转换 (走 TypeConverter 兜底,跟 SmartActivator 一致)。
            // NaN / Infinity 这种"程序员默认值"对编辑器用户毫无意义,而且回写后 JSON 序列化会炸,
            // 一律按空串显示;用户留空 → CoerceTextValue 返回 null → SmartActivator.SafeChangeType
            // 对非 nullable double 写 null 会失败,被静默吞,属性保持目标对象初始值。
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
            // record / DTO 主构造可重组的类型 → 走配置组编辑器(AxisStyleTrait / LineStyle / FieldMeta 等)
            if (IsConfigGroup(t)) return true;
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
            var newProps = new Dictionary<string, object?>(_node.Properties);
            foreach (var kv in _readers)
            {
                newProps[kv.Key] = kv.Value();
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
