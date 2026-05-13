using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// §D2.X picker UX —— 列出 <see cref="BlueprintHandlerRegistry.EnumerateAllHandlers"/> 里的全部
    /// handler(单 + 多输入混排),用户挑一个;调用方拿到 handler 名 + inputs 列表(单输入为 null)
    /// 喂给 <see cref="NodeFactory.ApplyHandlerSelection"/> 物化到节点。
    ///
    /// <para>
    /// 调用入口:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>添加新节点</b>:LowCodeDemoView.PickAndAddNode 选 ComputeFeature/HandlerFeature/PlotFeature 后弹本窗</item>
    ///   <item><b>编辑既有节点</b>:NodeEditorWindow Handler 区"选择..."按钮弹本窗</item>
    /// </list>
    ///
    /// <para>
    /// 选 "跳过" / Esc 关掉 = DialogResult=false,调用方据此判断"用户没选"。
    /// </para>
    /// </summary>
    public sealed class HandlerPickerWindow : Window
    {
        public string? SelectedHandlerName { get; private set; }
        public IReadOnlyList<string>? SelectedInputNames { get; private set; }   // null = 单输入 handler

        private readonly ListBox _list = new()
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1B, 0x1F)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE6, 0xEC)),
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
        };

        public HandlerPickerWindow(
            string featureTypeName,
            IEnumerable<(string Name, IReadOnlyList<string>? Inputs)> handlers,
            string? currentHandlerName = null)
        {
            Title = $"选择 handler — {featureTypeName}";
            Width = 540; Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x29));

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var hint = new TextBlock
            {
                Text = "选一个 Python 指标 handler。多输入 handler 选完会自动展开 Inputs 端口槽 + 填 InputOrder;\n"
                     + "单输入 handler 只设 Compute/Indicator/Handler 字符串,不展开端口。\n"
                     + "切换 handler 时,**旧 handler 的 Inputs.* 输入端口连线会被清掉**(端口名变了)。",
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xBE, 0xC5)),
                Margin = new Thickness(12, 10, 12, 8),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            };
            grid.Children.Add(hint); Grid.SetRow(hint, 0);

            _list.Margin = new Thickness(8, 0, 8, 8);
            _list.MouseDoubleClick += (_, __) => Confirm();
            _list.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Confirm(); e.Handled = true; }
                else if (e.Key == Key.Escape) { DialogResult = false; Close(); e.Handled = true; }
            };
            grid.Children.Add(_list); Grid.SetRow(_list, 1);

            // 分两段排:多输入(M args)在前,单输入在后;每段内字母序。
            var all = handlers.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var multi = all.Where(h => h.Inputs is { Count: > 0 }).ToList();
            var single = all.Where(h => h.Inputs is null or { Count: 0 }).ToList();
            int currentIdx = -1;

            if (multi.Count == 0 && single.Count == 0)
            {
                _list.Items.Add(new ListBoxItem
                {
                    Content = "(BlueprintHandlerRegistry 没有任何已注册 handler。先确认 .py 落盘 + AutoDiscover 已跑过。)",
                    IsHitTestVisible = false,
                    Focusable = false,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50)),
                    Padding = new Thickness(8, 8, 8, 8),
                });
            }
            else
            {
                if (multi.Count > 0)
                {
                    AddSectionHeader($"━━ 多输入 handler ({multi.Count}) ━━", 0xCE93D8);
                    foreach (var h in multi)
                    {
                        bool isCurrent = string.Equals(h.Name, currentHandlerName, StringComparison.Ordinal);
                        if (isCurrent) currentIdx = _list.Items.Count;
                        var inputsStr = string.Join(", ", h.Inputs!);
                        _list.Items.Add(new ListBoxItem
                        {
                            Content = $"  {(isCurrent ? "★ " : "  ")}{h.Name}    inputs=[{inputsStr}]    ({h.Inputs!.Count} args)",
                            Tag = (h.Name, h.Inputs),
                            Padding = new Thickness(8, 4, 8, 4),
                            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE6, 0xEC)),
                        });
                    }
                }
                if (single.Count > 0)
                {
                    AddSectionHeader($"━━ 单输入 handler ({single.Count}) ━━", 0x4FC3F7);
                    foreach (var h in single)
                    {
                        bool isCurrent = string.Equals(h.Name, currentHandlerName, StringComparison.Ordinal);
                        if (isCurrent) currentIdx = _list.Items.Count;
                        _list.Items.Add(new ListBoxItem
                        {
                            Content = $"  {(isCurrent ? "★ " : "  ")}{h.Name}",
                            Tag = (h.Name, (IReadOnlyList<string>?)null),
                            Padding = new Thickness(8, 4, 8, 4),
                            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE6, 0xEC)),
                        });
                    }
                }
                _list.SelectedIndex = currentIdx >= 0 ? currentIdx : SkipPastFirstHeader();
            }

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8, 0, 8, 8),
            };
            var skip = new Button { Content = "跳过", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(4, 0, 0, 0) };
            skip.Click += (_, __) => { DialogResult = false; Close(); };
            var ok = new Button { Content = "确定", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(4, 0, 0, 0) };
            ok.Click += (_, __) => Confirm();
            btnPanel.Children.Add(skip);
            btnPanel.Children.Add(ok);
            grid.Children.Add(btnPanel); Grid.SetRow(btnPanel, 2);

            Content = grid;
            Loaded += (_, __) => _list.Focus();
        }

        private void AddSectionHeader(string text, uint colorRgb)
        {
            _list.Items.Add(new ListBoxItem
            {
                Content = text,
                IsHitTestVisible = false,
                Focusable = false,
                Padding = new Thickness(8, 6, 8, 4),
                Foreground = new SolidColorBrush(Color.FromRgb((byte)((colorRgb >> 16) & 0xFF), (byte)((colorRgb >> 8) & 0xFF), (byte)(colorRgb & 0xFF))),
                FontWeight = FontWeights.SemiBold,
            });
        }

        // 跳过 section header 选第一个真实 handler 项
        private int SkipPastFirstHeader()
        {
            for (int i = 0; i < _list.Items.Count; i++)
                if (_list.Items[i] is ListBoxItem li && li.Tag != null) return i;
            return 0;
        }

        private void Confirm()
        {
            if (_list.SelectedItem is ListBoxItem li && li.Tag is ValueTuple<string, IReadOnlyList<string>?> tup)
            {
                SelectedHandlerName = tup.Item1;
                SelectedInputNames  = tup.Item2;
                DialogResult = true;
                Close();
            }
        }
    }
}
