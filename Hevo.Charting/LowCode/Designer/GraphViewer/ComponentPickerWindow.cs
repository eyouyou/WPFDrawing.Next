using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// 极简组件选择对话框:列出 ComponentRegistry 里属于某 Kind 的所有 .NET Type,
    /// 用户双击或回车确认。结果存 <see cref="SelectedType"/>,DialogResult=true 表示已选。
    /// 实时按关键字过滤(Type.Name + 命名空间)。
    /// </summary>
    public sealed class ComponentPickerWindow : Window
    {
        public Type? SelectedType { get; private set; }

        private readonly ListBox _list = new()
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1B, 0x1F)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE6, 0xEC)),
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
        };
        private readonly TextBox _search = new()
        {
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x1A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE6, 0xEC)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x35, 0x3A)),
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 13,
        };

        private readonly IReadOnlyList<Type> _all;

        public ComponentPickerWindow(string title, IReadOnlyList<Type> candidates)
        {
            Title = title;
            Width = 480; Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x29));

            _all = candidates;

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _search.Margin = new Thickness(8, 8, 8, 6);
            _search.TextChanged += (_, __) => RefreshList(_search.Text);
            _search.PreviewKeyDown += SearchOnKey;
            grid.Children.Add(_search); Grid.SetRow(_search, 0);

            _list.Margin = new Thickness(8, 0, 8, 6);
            _list.MouseDoubleClick += (_, __) => Confirm();
            _list.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Confirm(); e.Handled = true; }
                else if (e.Key == Key.Escape) { DialogResult = false; Close(); e.Handled = true; }
            };
            grid.Children.Add(_list); Grid.SetRow(_list, 1);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8, 0, 8, 8),
            };
            var ok = new Button { Content = "确定", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(4, 0, 0, 0) };
            ok.Click += (_, __) => Confirm();
            var cancel = new Button { Content = "取消", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(4, 0, 0, 0) };
            cancel.Click += (_, __) => { DialogResult = false; Close(); };
            btnPanel.Children.Add(cancel);
            btnPanel.Children.Add(ok);
            grid.Children.Add(btnPanel); Grid.SetRow(btnPanel, 2);

            Content = grid;
            RefreshList("");
            Loaded += (_, __) => _search.Focus();
        }

        private void SearchOnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down)
            {
                _list.Focus();
                if (_list.Items.Count > 0 && _list.SelectedIndex < 0) _list.SelectedIndex = 0;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                Confirm();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false; Close(); e.Handled = true;
            }
        }

        private void RefreshList(string filter)
        {
            _list.Items.Clear();
            string f = (filter ?? "").Trim();

            // 按 FeatureCategory 分组,每组前一行 header (灰底高亮),组内按字母排。
            var grouped = _all
                .Where(t =>
                    string.IsNullOrEmpty(f)
                    || t.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                    || (t.FullName ?? "").Contains(f, StringComparison.OrdinalIgnoreCase))
                .GroupBy(FeatureCategoryRegistry.Resolve)
                .OrderBy(g => (int)g.Key);

            foreach (var grp in grouped)
            {
                _list.Items.Add(new ListBoxItem
                {
                    Content = $"━━ {FeatureCategoryStyle.DisplayName(grp.Key)} ━━",
                    IsHitTestVisible = false,
                    Focusable = false,
                    Padding = new Thickness(8, 6, 8, 4),
                    Foreground = new SolidColorBrush(FeatureCategoryStyle.ZoneLabel(grp.Key)),
                    FontWeight = FontWeights.SemiBold,
                });
                foreach (var t in grp.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    _list.Items.Add(new ListBoxItem
                    {
                        Content = $"   {t.Name}    [{t.FullName}]",
                        Tag = t,
                        Padding = new Thickness(8, 4, 8, 4),
                        Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE6, 0xEC)),
                    });
                }
            }

            // 默认选中第一个真实条目 (跳过 header)
            for (int i = 0; i < _list.Items.Count; i++)
            {
                if (_list.Items[i] is ListBoxItem li && li.Tag != null)
                {
                    _list.SelectedIndex = i;
                    break;
                }
            }
        }

        private void Confirm()
        {
            if (_list.SelectedItem is ListBoxItem li && li.Tag is Type t)
            {
                SelectedType = t;
                DialogResult = true;
                Close();
            }
        }
    }
}
