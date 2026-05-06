using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode.Designer.Converters;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// §D1.3 DashboardWorkspace — 多 cell 蓝图编辑器。
    /// 持 N 个 GraphViewer（各自一个 GraphSchema），垂直 Grid 按 HeightRatio 分配高度。
    /// 工具栏动作（添加节点、自动布局等）作用于当前激活 cell；
    /// Dashboard 级动作（导出 JSON、运行 Dashboard）跨所有 cell。
    /// zero-link 单 cell 时行为等价于 LowCodeDemoView。
    /// </summary>
    public sealed class DashboardWorkspace : UserControl
    {
        // ── per-cell state ────────────────────────────────────────────────────

        private sealed class CellSlot
        {
            public required string Id { get; set; }
            public DashboardCellRole Role { get; set; } = DashboardCellRole.Pane;
            public double HeightRatio { get; set; } = 1;
            public GraphSchema Schema { get; } = new GraphSchema();
            public ChartHost Host { get; } = new ChartHost();

            // pre-built visual elements (created once, re-parented on rebuild)
            public Border? RootBorder { get; set; }
            public TextBlock? HeaderLabel { get; set; }
            public Button? TabButton { get; set; }

            public void RefreshLabels()
            {
                if (HeaderLabel != null)
                    HeaderLabel.Text = $"{Id}  [{RoleText(Role)}]  ×{HeightRatio:0.##}";
                if (TabButton != null)
                    TabButton.Content = $"{Id} ({RoleShort(Role)})";
            }
        }

        // ── fields ────────────────────────────────────────────────────────────

        private readonly List<CellSlot> _cells = new();
        private int _activeIndex = -1;

        private readonly Grid _outerGrid = new();
        private readonly StackPanel _tabStrip;
        private readonly Grid _cellsGrid = new();
        private readonly TextBox _jsonBox;

        // toolbar buttons
        private readonly Button _btnAddDs     = Btn("+ 数据源");
        private readonly Button _btnAddTrait  = Btn("+ Trait");
        private readonly Button _btnAddFeat   = Btn("+ Feature");
        private readonly Button _btnAutoLay   = Btn("自动布局");
        private readonly Button _btnClearCell = Btn("清空 Cell");
        private readonly Button _btnAddCell   = Btn("＋ 加 Cell");
        private readonly Button _btnDelCell   = Btn("－ 删 Cell");
        private readonly Button _btnExport    = Btn("导出 JSON");
        private readonly Button _btnImport    = Btn("导入 JSON");
        private readonly Button _btnRun       = Btn("运行 Dashboard");
        private readonly TextBox _tbCtx       = CtxBox();

        private static readonly JsonSerializerOptions _json = BlueprintJsonOptions.Default;

        /// <summary>业务侧可注入"字符串 → 运行时上下文"转换器（同 LowCodeDemoView.ContextParser）。</summary>
        public Func<string, object?>? ContextParser { get; set; }

        // ── ctor ──────────────────────────────────────────────────────────────

        public DashboardWorkspace()
        {
            Background = Brush(0x1A, 0x1B, 0x1F);

            // row 0: toolbar | row 1: tab strip | row 2: cells + JSON side-by-side
            _outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });

            // toolbar
            var toolbar = BuildToolbar();
            Grid.SetRow(toolbar, 0); Grid.SetColumnSpan(toolbar, 2);
            _outerGrid.Children.Add(toolbar);

            // tab strip
            _tabStrip = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Background = Brush(0x1E, 0x20, 0x26),
                Height = 32,
            };
            var tabScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
                Content = _tabStrip,
            };
            Grid.SetRow(tabScroll, 1); Grid.SetColumnSpan(tabScroll, 2);
            _outerGrid.Children.Add(tabScroll);

            // cells grid — no ScrollViewer: star rows fill height proportionally
            Grid.SetRow(_cellsGrid, 2); Grid.SetColumn(_cellsGrid, 0);
            _outerGrid.Children.Add(_cellsGrid);

            // JSON panel (right)
            _jsonBox = new TextBox
            {
                Background = Brush(0x14, 0x15, 0x1A),
                Foreground = Brush(0xE0, 0xE6, 0xEC),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                IsReadOnly = true,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                AcceptsReturn = true,
                TextWrapping  = TextWrapping.NoWrap,
                Margin = new Thickness(6, 0, 6, 6),
            };
            var jsonPanel = BuildJsonPanel();
            Grid.SetRow(jsonPanel, 2); Grid.SetColumn(jsonPanel, 1);
            _outerGrid.Children.Add(jsonPanel);

            Content = _outerGrid;

            // wire toolbar
            _btnAddDs.Click     += (_, __) => PickAndAdd(NodeFactory.Kind.DataSource, "选择数据源");
            _btnAddTrait.Click  += (_, __) => PickAndAdd(NodeFactory.Kind.Trait, "选择 Trait");
            _btnAddFeat.Click   += (_, __) => PickAndAdd(NodeFactory.Kind.Feature, "选择 Feature");
            _btnAutoLay.Click   += (_, __) => AutoLayoutActive();
            _btnClearCell.Click += (_, __) => ClearActive();
            _btnAddCell.Click   += (_, __) => AddCell($"cell{_cells.Count + 1}", DashboardCellRole.Pane, 1);
            _btnDelCell.Click   += (_, __) => DeleteActive();
            _btnExport.Click    += (_, __) => ExportJson();
            _btnImport.Click    += (_, __) => ImportJson();
            _btnRun.Click       += (_, __) => RunDashboard();

            // initial cells: Master (3) + one Pane (1)
            AddCell("main",  DashboardCellRole.Master, 3);
            AddCell("pane1", DashboardCellRole.Pane,   1);
        }

        // ── toolbar ───────────────────────────────────────────────────────────

        private StackPanel BuildToolbar()
        {
            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Background  = Brush(0x24, 0x26, 0x2C),
            };
            void Add(UIElement e) => bar.Children.Add(e);
            void Sep() => Add(new Separator { Margin = new Thickness(6, 4, 6, 4) });

            Add(_btnAddDs); Add(_btnAddTrait); Add(_btnAddFeat); Sep();
            Add(_btnAutoLay); Add(_btnClearCell); Sep();
            Add(_btnAddCell); Add(_btnDelCell); Sep();
            Add(_btnExport); Add(_btnImport); Sep();
            Add(new TextBlock
            {
                Text = "上下文:",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush(0xC0, 0xC8, 0xD0),
                Margin = new Thickness(4, 0, 4, 0),
            });
            Add(_tbCtx); Add(_btnRun);

            Add(new TextBlock
            {
                Text = "  左键拖节点 / 右键平移 / 滚轮缩放 / 连线 / Del 删 / Ctrl+Z 撤销",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush(0x70, 0x78, 0x80),
                FontSize = 11,
                Margin = new Thickness(10, 0, 0, 0),
            });

            return bar;
        }

        private Grid BuildJsonPanel()
        {
            var panel = new Grid { Background = Brush(0x20, 0x22, 0x29) };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var lbl = new TextBlock
            {
                Text = "Dashboard JSON",
                Foreground = Brush(0xC0, 0xC8, 0xD0),
                Margin = new Thickness(8, 8, 8, 4),
                FontSize = 13,
            };
            Grid.SetRow(lbl, 0);
            panel.Children.Add(lbl);

            Grid.SetRow(_jsonBox, 1);
            panel.Children.Add(_jsonBox);

            return panel;
        }

        // ── cell management ───────────────────────────────────────────────────

        private void AddCell(string id, DashboardCellRole role, double heightRatio)
        {
            var slot = new CellSlot { Id = id, Role = role, HeightRatio = heightRatio };
            slot.Schema.StateChanged      += (_, __) => RefreshJson();
            slot.Schema.NodeEditRequested += node   => OnNodeEdit(slot, node);

            // build per-slot visuals once
            slot.RootBorder = BuildCellRoot(slot);
            slot.TabButton  = BuildTabButton(slot);

            _cells.Add(slot);
            RebuildGrid();
            Activate(_cells.Count - 1);
        }

        private void DeleteActive()
        {
            if (_activeIndex < 0 || _cells.Count == 0) return;
            if (_cells.Count == 1)
            {
                Msg("Dashboard 至少需要一个 cell，无法删除。"); return;
            }
            var slot = _cells[_activeIndex];
            if (slot.Role == DashboardCellRole.Master
                && _cells.Count(c => c.Role == DashboardCellRole.Master) == 1)
            {
                if (MessageBox.Show(Window.GetWindow(this),
                        $"Cell '{slot.Id}' 是唯一的 Master，删除后 Dashboard 无主图。确认？",
                        "删除 Master",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }

            // detach from grid before removing
            DetachHost(slot);
            _cells.RemoveAt(_activeIndex);
            RebuildGrid();
            Activate(Math.Min(_activeIndex, _cells.Count - 1));
        }

        // ── grid rebuild ──────────────────────────────────────────────────────

        private void RebuildGrid()
        {
            _cellsGrid.Children.Clear();
            _cellsGrid.RowDefinitions.Clear();
            _tabStrip.Children.Clear();

            for (int i = 0; i < _cells.Count; i++)
            {
                var slot = _cells[i];

                _cellsGrid.RowDefinitions.Add(new RowDefinition
                {
                    Height    = new GridLength(slot.HeightRatio, GridUnitType.Star),
                    MinHeight = 100,
                });

                // refresh labels in case Id/Role/HeightRatio changed
                slot.RefreshLabels();

                Grid.SetRow(slot.RootBorder!, i);
                _cellsGrid.Children.Add(slot.RootBorder!);

                if (slot.TabButton != null)
                    _tabStrip.Children.Add(slot.TabButton);
            }

            // "+" quick-add in tab strip
            var addTab = Btn("＋");
            addTab.Margin  = new Thickness(4, 5, 0, 5);
            addTab.Padding = new Thickness(8, 1, 8, 1);
            addTab.Click  += (_, __) => AddCell($"cell{_cells.Count + 1}", DashboardCellRole.Pane, 1);
            _tabStrip.Children.Add(addTab);

            ApplyActiveHighlight();
        }

        // ── per-cell visuals (built once per slot) ────────────────────────────

        private Border BuildCellRoot(CellSlot slot)
        {
            // inner grid: row 0 = header bar, row 1 = ChartHost
            var inner = new Grid();
            inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = BuildCellHeader(slot);
            Grid.SetRow(header, 0);
            inner.Children.Add(header);

            slot.Host.Schema = slot.Schema;
            Grid.SetRow(slot.Host, 1);
            inner.Children.Add(slot.Host);

            var root = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 2),
                BorderBrush     = Brush(Colors.Transparent),
                Child           = inner,
            };

            // clicking anywhere in the header area selects this cell
            header.MouseLeftButtonDown += (_, __) => Activate(IndexOf(slot));
            return root;
        }

        private Border BuildCellHeader(CellSlot slot)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(6, 0, 6, 0),
            };

            // role color dot
            var dot = new Ellipse
            {
                Width  = 8, Height = 8,
                Fill   = new SolidColorBrush(RoleColor(slot.Role)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            // dot needs to update on role change — re-bound in RefreshLabels via header rebuild is complex;
            // instead we bind Fill to the slot at rebuild time (acceptable since Edit dialog calls RebuildGrid)
            panel.Children.Add(dot);

            var lbl = new TextBlock
            {
                Text              = $"{slot.Id}  [{RoleText(slot.Role)}]  ×{slot.HeightRatio:0.##}",
                Foreground        = Brush(0xC0, 0xC8, 0xD0),
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily        = new FontFamily("Segoe UI"),
                FontSize          = 11,
            };
            slot.HeaderLabel = lbl;
            panel.Children.Add(lbl);

            var editBtn = SmallBtn("编辑…", 52);
            editBtn.Margin = new Thickness(8, 0, 0, 0);
            editBtn.Click += (_, __) => { Activate(IndexOf(slot)); ShowCellEdit(slot); };
            panel.Children.Add(editBtn);

            var delBtn = SmallBtn("×", 22);
            delBtn.Margin     = new Thickness(4, 0, 0, 0);
            delBtn.Foreground = Brush(0xFF, 0x70, 0x70);
            delBtn.Click += (_, __) => { Activate(IndexOf(slot)); DeleteActive(); };
            panel.Children.Add(delBtn);

            return new Border
            {
                Background = Brush(0x1E, 0x20, 0x26),
                Padding    = new Thickness(0, 4, 0, 4),
                Child      = panel,
            };
        }

        private Button BuildTabButton(CellSlot slot)
        {
            var tab = new Button
            {
                Content         = $"{slot.Id} ({RoleShort(slot.Role)})",
                Margin          = new Thickness(2, 5, 0, 5),
                Padding         = new Thickness(10, 1, 10, 1),
                Background      = Brush(0x2A, 0x2C, 0x34),
                Foreground      = Brush(0xC0, 0xC8, 0xD0),
                BorderBrush     = Brush(0x3A, 0x3C, 0x44),
                BorderThickness = new Thickness(1),
                FontFamily      = new FontFamily("Segoe UI"),
                FontSize        = 12,
            };
            tab.Click += (_, __) => Activate(IndexOf(slot));
            return tab;
        }

        // ── activation + highlight ────────────────────────────────────────────

        private void Activate(int index)
        {
            if (index < 0 || index >= _cells.Count) return;
            _activeIndex = index;
            ApplyActiveHighlight();
        }

        private void ApplyActiveHighlight()
        {
            var activeBlue = Color.FromRgb(0x4F, 0xC3, 0xF7);
            for (int i = 0; i < _cells.Count; i++)
            {
                bool on = i == _activeIndex;
                var slot = _cells[i];

                if (slot.RootBorder != null)
                    slot.RootBorder.BorderBrush = new SolidColorBrush(on ? activeBlue : Colors.Transparent);

                if (slot.TabButton != null)
                {
                    slot.TabButton.Background = new SolidColorBrush(on
                        ? Color.FromRgb(0x1A, 0x4A, 0x6E)
                        : Color.FromRgb(0x2A, 0x2C, 0x34));
                    slot.TabButton.Foreground = new SolidColorBrush(on
                        ? activeBlue
                        : Color.FromRgb(0xC0, 0xC8, 0xD0));
                }
            }
        }

        // ── node editing ──────────────────────────────────────────────────────

        private void OnNodeEdit(CellSlot slot, Node node)
        {
            var dlg = new NodeEditorWindow(node) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.Result != null)
                slot.Schema.ApplyUserEdit(s => s.WithNode(dlg.Result));
        }

        private void PickAndAdd(NodeFactory.Kind kind, string title)
        {
            if (_activeIndex < 0 || _activeIndex >= _cells.Count) return;
            var slot = _cells[_activeIndex];

            var candidates = NodeFactory.ListByKind(kind);
            if (candidates.Count == 0) { Msg($"ComponentRegistry 里暂无 {kind} 类型。"); return; }

            var picker = new ComponentPickerWindow(title, candidates) { Owner = Window.GetWindow(this) };
            if (picker.ShowDialog() != true || picker.SelectedType == null) return;

            var w = slot.Host.ActualWidth  > 0 ? slot.Host.ActualWidth  : 800;
            var h = slot.Host.ActualHeight > 0 ? slot.Host.ActualHeight : 300;
            var screenCenter = new HevoPoint((float)w / 2f, (float)h / 2f);
            var canvasCenter = slot.Schema.State.Transform.ScreenToCanvas(screenCenter);

            var node = NodeFactory.CreateNode(picker.SelectedType, new HevoPoint(0, 0));
            var pos  = new HevoPoint(canvasCenter.X - node.Size.X / 2, canvasCenter.Y - node.Size.Y / 2);
            slot.Schema.ApplyUserEdit(s => s.WithNode(node with { Position = pos }));
        }

        private void AutoLayoutActive()
        {
            ActiveSlot()?.Schema.ApplyUserEdit(s =>
            {
                var nodes = s.Nodes.ToList();
                var edges = s.Edges.ToList();
                AutoLayout.Apply(nodes, edges);
                return s with { Nodes = nodes };
            });
        }

        private void ClearActive()
        {
            ActiveSlot()?.Schema.ApplyUserEdit(s => GraphState.Empty with { Transform = s.Transform });
        }

        // ── Dashboard JSON ────────────────────────────────────────────────────

        private Dashboard ToDashboard()
        {
            var d = new Dashboard();
            foreach (var slot in _cells)
                d.Cells.Add(new DashboardCell
                {
                    Id          = slot.Id,
                    Role        = slot.Role,
                    HeightRatio = slot.HeightRatio,
                    Blueprint   = GraphSerializer.ToBlueprint(slot.Schema.State),
                });
            return d;
        }

        private void RefreshJson()
        {
            _jsonBox.Text = JsonSerializer.Serialize(ToDashboard(), _json);
        }

        private void ExportJson() => RefreshJson();

        private void ImportJson()
        {
            var text = _jsonBox.Text;
            if (string.IsNullOrWhiteSpace(text)) { Msg("JSON 面板是空的，没有内容可导入。"); return; }
            try
            {
                var d = JsonSerializer.Deserialize<Dashboard>(text, _json)
                    ?? throw new Exception("反序列化结果为 null。");
                if (d.Cells.Count == 0) throw new Exception("Dashboard 无 cell。");

                // detach all existing hosts before clearing
                foreach (var s in _cells) DetachHost(s);
                _cells.Clear();

                foreach (var cell in d.Cells)
                {
                    var slot = new CellSlot { Id = cell.Id, Role = cell.Role, HeightRatio = cell.HeightRatio };
                    slot.Schema.StateChanged      += (_, __) => RefreshJson();
                    slot.Schema.NodeEditRequested += node => OnNodeEdit(slot, node);
                    slot.Schema.State  = GraphDeserializer.FromBlueprint(cell.Blueprint);
                    slot.RootBorder    = BuildCellRoot(slot);
                    slot.TabButton     = BuildTabButton(slot);
                    _cells.Add(slot);
                }

                RebuildGrid();
                Activate(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), $"导入失败：{ex.Message}",
                    "导入 JSON", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ── Run Dashboard ─────────────────────────────────────────────────────

        private void RunDashboard()
        {
            var dashboard = ToDashboard();

            var dry = DashboardLauncher.DryRun(dashboard);
            if (dry.Error != null)
            {
                MessageBox.Show(Window.GetWindow(this),
                    $"DryRun 失败：{dry.Error}", "运行 Dashboard",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // build data-source context per cell (same context for all cells, typical demo usage)
            string? ctxText = _tbCtx.Text?.Trim();
            object? context = null;
            if (!string.IsNullOrEmpty(ctxText))
            {
                if (ContextParser != null)
                {
                    try { context = ContextParser(ctxText); }
                    catch (Exception ex)
                    {
                        MessageBox.Show(Window.GetWindow(this), $"上下文解析失败：{ex.Message}", "运行 Dashboard",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                context ??= ctxText;
            }

            var opts = new DashboardLaunchOptions
            {
                Owner             = Window.GetWindow(this),
                DataSourceContexts = context != null
                    ? dashboard.Cells.ToDictionary(c => c.Id, _ => context)
                    : null,
            };

            var result = DashboardLauncher.LaunchEx(dashboard, opts);
            if (result.Error != null)
            {
                MessageBox.Show(Window.GetWindow(this),
                    $"装配失败：{result.Error}", "运行 Dashboard",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // show diagnostics summary if any warnings
            string diagSuffix = result.Diagnostics.Count > 0
                ? $"\n\n⚠ {result.Diagnostics.Count} 条诊断（详见控制台）"
                : string.Empty;

            new Window
            {
                Title = $"Dashboard 预览 — {dashboard.Cells.Count} cells{diagSuffix}",
                Width  = 1200,
                Height = 800,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner   = Window.GetWindow(this),
                Content = result.Dashboard,
                Background = Brush(0x1A, 0x1B, 0x1F),
            }.Show();
        }

        // ── cell config dialog ────────────────────────────────────────────────

        private void ShowCellEdit(CellSlot slot)
        {
            var dlg = new Window
            {
                Title  = $"配置 Cell — {slot.Id}",
                Width  = 360, Height = 270,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner       = Window.GetWindow(this),
                ResizeMode  = ResizeMode.NoResize,
                Background  = Brush(0x1A, 0x1B, 0x1F),
            };

            var body = new StackPanel { Margin = new Thickness(16) };
            var tbId    = DlgField(body, "Cell Id:", slot.Id);
            var cbRole  = DlgCombo<DashboardCellRole>(body, "Role:",
                [DashboardCellRole.Master, DashboardCellRole.Pane, DashboardCellRole.Raw], slot.Role);
            var tbRatio = DlgField(body, "高度比 (HeightRatio):", slot.HeightRatio.ToString("0.##"));

            var okBtn = Btn("确定"); okBtn.Margin = new Thickness(0, 12, 0, 0);
            okBtn.Click += (_, __) =>
            {
                if (string.IsNullOrWhiteSpace(tbId.Text))
                { MessageBox.Show(dlg, "Id 不能为空。", "配置 Cell", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                if (!double.TryParse(tbRatio.Text, out var ratio) || ratio <= 0)
                { MessageBox.Show(dlg, "高度比必须是正数。", "配置 Cell", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

                slot.Id          = tbId.Text.Trim();
                slot.Role        = (DashboardCellRole)cbRole.SelectedItem!;
                slot.HeightRatio = ratio;
                dlg.DialogResult = true;
                dlg.Close();
            };
            body.Children.Add(okBtn);
            dlg.Content = body;

            if (dlg.ShowDialog() == true)
            {
                // rebuild dot color for new role
                var newRoot = BuildCellRoot(slot);
                slot.RootBorder = newRoot;
                var newTab = BuildTabButton(slot);
                slot.TabButton = newTab;

                RebuildGrid();
                Activate(_activeIndex);
                RefreshJson();
            }
        }

        // ── helpers ───────────────────────────────────────────────────────────

        private CellSlot? ActiveSlot() =>
            _activeIndex >= 0 && _activeIndex < _cells.Count ? _cells[_activeIndex] : null;

        private int IndexOf(CellSlot slot) => _cells.IndexOf(slot);

        private void Msg(string text) =>
            MessageBox.Show(Window.GetWindow(this), text, "DashboardWorkspace",
                MessageBoxButton.OK, MessageBoxImage.Information);

        private static void DetachHost(CellSlot slot)
        {
            // ChartHost must be removed from its parent before being re-added elsewhere
            if (slot.Host.Parent is Panel p)
                p.Children.Remove(slot.Host);
            else if (slot.Host.Parent is Border b && b.Child == slot.Host)
                b.Child = null;
            else if (slot.Host.Parent is ContentControl cc)
                cc.Content = null;
            else if (slot.Host.Parent is Grid g)
                g.Children.Remove(slot.Host);
        }

        private static SolidColorBrush Brush(byte r, byte g, byte b) =>
            new(Color.FromRgb(r, g, b));
        private static SolidColorBrush Brush(Color c) =>
            new(c);

        private static Color RoleColor(DashboardCellRole r) => r switch
        {
            DashboardCellRole.Master => Color.FromRgb(0x4F, 0xC3, 0xF7),
            DashboardCellRole.Pane   => Color.FromRgb(0xA5, 0xD6, 0xA7),
            DashboardCellRole.Raw    => Color.FromRgb(0xB0, 0xBE, 0xC5),
            _                        => Colors.Gray,
        };

        private static string RoleText(DashboardCellRole r) => r switch
        {
            DashboardCellRole.Master => "Master 主图",
            DashboardCellRole.Pane   => "Pane 副图",
            DashboardCellRole.Raw    => "Raw 独立",
            _                        => r.ToString(),
        };

        private static string RoleShort(DashboardCellRole r) => r switch
        {
            DashboardCellRole.Master => "主",
            DashboardCellRole.Pane   => "副",
            DashboardCellRole.Raw    => "独",
            _                        => "?",
        };

        private static Button Btn(string text, double w = 0)
        {
            var b = new Button
            {
                Content         = text,
                Margin          = new Thickness(6, 4, 0, 4),
                Padding         = new Thickness(10, 4, 10, 4),
                Background      = new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x34)),
                Foreground      = new SolidColorBrush(Color.FromRgb(0xE0, 0xE6, 0xEC)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0x3A, 0x3C, 0x44)),
                BorderThickness = new Thickness(1),
                FontFamily      = new FontFamily("Segoe UI"),
                FontSize        = 12,
            };
            if (w > 0) b.Width = w;
            return b;
        }

        private static Button SmallBtn(string text, double w = 0)
        {
            var b = Btn(text, w);
            b.FontSize = 10;
            b.Padding  = new Thickness(4, 1, 4, 1);
            b.Margin   = new Thickness(0);
            return b;
        }

        private static TextBox CtxBox() => new()
        {
            Width  = 150,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin  = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(6, 2, 6, 2),
            Background  = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x1A)),
            Foreground  = new SolidColorBrush(Color.FromRgb(0xE0, 0xE6, 0xEC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x35, 0x3A)),
            Text = "USZA300059",
        };

        private static TextBox DlgField(StackPanel parent, string label, string value)
        {
            parent.Children.Add(new TextBlock
            {
                Text       = label,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0xC8, 0xD0)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 12,
                Margin     = new Thickness(0, 8, 0, 2),
            });
            var tb = new TextBox
            {
                Text        = value,
                Background  = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x1A)),
                Foreground  = new SolidColorBrush(Color.FromRgb(0xE0, 0xE6, 0xEC)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x35, 0x3A)),
                Padding     = new Thickness(4, 2, 4, 2),
                FontFamily  = new FontFamily("Segoe UI"),
                FontSize    = 12,
            };
            parent.Children.Add(tb);
            return tb;
        }

        private static ComboBox DlgCombo<T>(StackPanel parent, string label, IEnumerable<T> items, T selected)
        {
            parent.Children.Add(new TextBlock
            {
                Text       = label,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0xC8, 0xD0)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 12,
                Margin     = new Thickness(0, 8, 0, 2),
            });
            var cb = new ComboBox
            {
                Background  = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x1A)),
                Foreground  = new SolidColorBrush(Color.FromRgb(0xE0, 0xE6, 0xEC)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x35, 0x3A)),
                FontFamily  = new FontFamily("Segoe UI"),
                FontSize    = 12,
            };
            foreach (var item in items) cb.Items.Add(item!);
            cb.SelectedItem = selected;
            parent.Children.Add(cb);
            return cb;
        }
    }
}
