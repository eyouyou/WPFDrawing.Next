using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using Hevo.Charting.PythonNet;

namespace Hevo.Drawing.LowCodeDemo
{
    /// <summary>
    /// §D2.5.E2E Python 编辑器 —— AvalonEdit 嵌入式代码编辑器(行号 / 折叠 / Python 着色 /
    /// Tab 缩进自动 / 搜索 Ctrl+F)+ 实时 SyntaxError 检测(debounce 400ms 调 Python compile()
    /// 抓行列号,BackgroundRenderer 画错误行红色高亮)+ 保存时 DryRunImports 浮出 import 错误。
    /// </summary>
    public partial class PyEditorView : UserControl
    {
        private string? _currentFile;
        private string _diskContent = "";        // 当前文件最近一次读盘的内容
        private readonly DispatcherTimer _syntaxDebounce;
        private readonly ErrorMarkerRenderer _errorRenderer = new();
        private bool _suppressTextChanged;        // 程序设置 Text 时不触发 syntax check

        public PyEditorView()
        {
            InitializeComponent();

            // AvalonEdit:加载 Python 内建语法着色 + 错误行高亮 BackgroundRenderer
            var py = HighlightingManager.Instance.GetDefinitionByExtension(".py");
            if (py != null) txtCode.SyntaxHighlighting = py;
            txtCode.TextArea.TextView.BackgroundRenderers.Add(_errorRenderer);

            _syntaxDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _syntaxDebounce.Tick += (_, __) =>
            {
                _syntaxDebounce.Stop();
                RunSyntaxCheck();
            };

            txtCode.TextChanged += (_, __) =>
            {
                if (_suppressTextChanged) return;
                _syntaxDebounce.Stop();
                _syntaxDebounce.Start();
            };

            btnNew.Click       += (_, __) => OnNew();
            btnDelete.Click    += (_, __) => OnDelete();
            btnRefresh.Click   += (_, __) => RefreshFileList();
            btnSave.Click      += (_, __) => SaveCurrent(reload: false);
            btnSaveReload.Click+= (_, __) => SaveCurrent(reload: true);
            btnRevert.Click    += (_, __) => RevertCurrent();
            lstFiles.SelectionChanged += (_, __) => OnFileSelected();

            txtCode.PreviewKeyDown += OnCodePreviewKeyDown;

            Loaded += (_, __) => RefreshFileList();
            // 切到 PyEditor tab 时自动刷新文件列表(避免别处部署了新 .py 但本面板缓存旧列表)
            IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is bool b && b) RefreshFileList();
            };
        }

        private void OnCodePreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
            if (e.Key == Key.S)
            {
                SaveCurrent(reload: false);
                e.Handled = true;
            }
            else if (e.Key == Key.R)
            {
                SaveCurrent(reload: true);
                e.Handled = true;
            }
        }

        // ───────── 文件列表 ─────────────────────────────────────────────

        private ObservableCollection<PyFileEntry> _fileEntries = new();

        private void RefreshFileList()
        {
            try
            {
                DemoPythonHost.EnsureInitialized();
            }
            catch (Exception ex)
            {
                AppendLog($"[初始化] ❌ DemoPythonHost.EnsureInitialized 失败:{ex.Message}");
                AppendLog("Python 解释器没起来 — 可能 Python312/ 缺失或 dll 找不到。" +
                          "回到 Python 指标 tab 点 ① 看具体错。");
                return;
            }

            var dir = DemoPythonHost.IndicatorsDir;
            if (!Directory.Exists(dir))
            {
                AppendLog($"indicators 目录不存在:{dir}");
                return;
            }

            var prevSelName = (lstFiles.SelectedItem as PyFileEntry)?.Name;
            var disabled = DemoPythonHost.ReadDisabledSet();
            var files = Directory.EnumerateFiles(dir, "*.py", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 重建 ObservableCollection,挂 PropertyChanged 监听 IsEnabled 的切换
            foreach (var e in _fileEntries) e.PropertyChanged -= OnFileEntryToggled;
            _fileEntries = new ObservableCollection<PyFileEntry>();
            foreach (var n in files)
            {
                var entry = new PyFileEntry(n!, !disabled.Contains(n!));
                entry.PropertyChanged += OnFileEntryToggled;
                _fileEntries.Add(entry);
            }
            lstFiles.ItemsSource = _fileEntries;

            // 复原选中
            var restored = _fileEntries.FirstOrDefault(e => e.Name == prevSelName)
                        ?? _fileEntries.FirstOrDefault();
            if (restored != null) lstFiles.SelectedItem = restored;

            int enabled = files.Count - disabled.Count(d => files.Contains(d));
            txtStatus.Text = $"indicators/ 含 {files.Count} 个 .py 文件 ({enabled} 启用 / {files.Count - enabled} 禁用) — {dir}";
            txtStatus.Foreground = Brushes.Gray;
        }

        private void OnFileEntryToggled(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not PyFileEntry entry) return;
            if (e.PropertyName != nameof(PyFileEntry.IsEnabled)) return;

            // 持久化禁用清单 + 立即热生效
            var disabled = DemoPythonHost.ReadDisabledSet();
            if (entry.IsEnabled) disabled.Remove(entry.Name);
            else disabled.Add(entry.Name);
            DemoPythonHost.WriteDisabledSet(disabled);
            DemoPythonHost.LoadEnabledIndicators();   // 重扫一次,新启用的 import,被禁用的 Unregister

            entry.RefreshColor();
            AppendLog($"[切换] {entry.Name} → {(entry.IsEnabled ? "启用" : "禁用")}");
        }

        private void OnFileSelected()
        {
            var selected = (lstFiles.SelectedItem as PyFileEntry)?.Name;
            if (string.IsNullOrEmpty(selected))
            {
                _currentFile = null;
                _diskContent = "";
                txtCurrentFile.Text = "(未选择文件)";
                SetEditorText("");
                _errorRenderer.Clear(txtCode);
                return;
            }
            _currentFile = Path.Combine(DemoPythonHost.IndicatorsDir, selected!);
            try
            {
                _diskContent = File.ReadAllText(_currentFile, Encoding.UTF8);
                SetEditorText(_diskContent);
                txtCurrentFile.Text = selected!;
                txtStatus.Text = $"已加载:{_currentFile} ({_diskContent.Length} 字节)";
                txtStatus.Foreground = Brushes.Gray;
                RunSyntaxCheck();   // 加载即检查一次
            }
            catch (Exception ex)
            {
                AppendLog($"[加载] ❌ 读 {selected} 失败:{ex.Message}");
            }
        }

        private void SetEditorText(string text)
        {
            _suppressTextChanged = true;
            try { txtCode.Text = text; }
            finally { _suppressTextChanged = false; }
        }

        // ───────── 实时语法检查(A:Python compile 抓 SyntaxError) ──────

        private void RunSyntaxCheck()
        {
            if (_currentFile == null)
            {
                _errorRenderer.Clear(txtCode);
                return;
            }
            PythonSyntaxResult result;
            try
            {
                var fname = Path.GetFileName(_currentFile) ?? "<editor>";
                result = DemoPythonHost.SyntaxCheck(txtCode.Text, fname);
            }
            catch (Exception ex)
            {
                // SyntaxCheck 自身崩(不该发生 — Python 没起来),退化为 ok 不打扰
                txtStatus.Text = $"⚠ SyntaxCheck 失败: {ex.Message}";
                txtStatus.Foreground = Brushes.Orange;
                return;
            }

            if (result.Ok)
            {
                _errorRenderer.Clear(txtCode);
                var lines = txtCode.Document.LineCount;
                txtStatus.Text = $"✓ 语法 OK — {lines} 行,{txtCode.Text.Length} 字符";
                txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
            }
            else
            {
                int? line = result.Line;
                int? col = result.Column;
                _errorRenderer.SetError(txtCode, line, col);
                var locStr = line.HasValue ? $"行 {line}" + (col.HasValue ? $" 列 {col}" : "") : "?";
                txtStatus.Text = $"❌ {result.ExceptionType}: {result.Message}  ({locStr})";
                txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));
            }
        }

        // ───────── 保存 / 重载 ─────────────────────────────────────────

        private void SaveCurrent(bool reload)
        {
            if (_currentFile == null)
            {
                AppendLog("[保存] 未选择文件。");
                return;
            }
            try
            {
                var content = txtCode.Text ?? "";
                File.WriteAllText(_currentFile, content, new UTF8Encoding(false));
                _diskContent = content;
                AppendLog($"[保存] ✓ {Path.GetFileName(_currentFile)} ({content.Length} B)");

                if (reload)
                {
                    ReloadModule(_currentFile);
                }
                else
                {
                    txtStatus.Text = $"已保存:{Path.GetFileName(_currentFile)} ({content.Length} 字节)";
                    txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[保存] ❌ {ex.GetType().Name}: {ex.Message}");
            }
        }

        // B:保存时调 DryRunImports 把 import / NameError / 装饰器签名等"运行时才知道"的错误浮出来
        private void ReloadModule(string filePath)
        {
            var fname = Path.GetFileName(filePath);
            try
            {
                DemoPythonHost.Registry.UnregisterBySourceFile(filePath);

                var diags = DemoPythonHost.Registry.DryRunImports(DemoPythonHost.IndicatorsDir);
                var thisDiag = diags.FirstOrDefault(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                if (thisDiag != null && !thisDiag.Success)
                {
                    AppendLog($"[重载] ✗ {fname} import 失败:{thisDiag.Error}");
                    if (!string.IsNullOrEmpty(thisDiag.PythonTraceback))
                        AppendLog(thisDiag.PythonTraceback);

                    // import 错误也尝试解析 lineno 画到编辑器上(正则简单匹配 'line N')
                    if (TryParseLineFromTraceback(thisDiag.PythonTraceback ?? thisDiag.Error ?? "", out var line))
                        _errorRenderer.SetError(txtCode, line, null);

                    txtStatus.Text = $"❌ {fname} 重载失败 — 看下方 traceback";
                    txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));
                    return;
                }

                if (thisDiag != null)
                {
                    var hs = string.Join(", ", thisDiag.Handlers.Select(h => h.Name));
                    AppendLog($"[重载] ✓ {fname}:{thisDiag.Handlers.Count} handlers — [{hs}]");
                }

                DemoPythonHost.Registry.AutoDiscoverDirectory(DemoPythonHost.IndicatorsDir);
                _errorRenderer.Clear(txtCode);
                txtStatus.Text = $"✅ {fname} 已保存 + 热重载";
                txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
            }
            catch (PythonDiagnosticsException pex)
            {
                AppendLog($"[重载] ❌ Python {pex.PythonExceptionType}: {pex.Message}");
                AppendLog(pex.PythonTraceback);
                txtStatus.Text = $"❌ {fname} 重载抛 Python 异常";
                txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));
            }
            catch (Exception ex)
            {
                AppendLog($"[重载] ❌ {ex.GetType().Name}: {ex.Message}");
                txtStatus.Text = $"❌ {fname} 重载抛 .NET 异常";
                txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));
            }
        }

        private static bool TryParseLineFromTraceback(string text, out int line)
        {
            line = 0;
            if (string.IsNullOrEmpty(text)) return false;
            // Python traceback 格式:File "...", line 42, in ...
            var m = System.Text.RegularExpressions.Regex.Match(text, @"line\s+(\d+)");
            if (!m.Success) return false;
            return int.TryParse(m.Groups[1].Value, out line);
        }

        private void RevertCurrent()
        {
            if (_currentFile == null) return;
            try
            {
                _diskContent = File.ReadAllText(_currentFile, Encoding.UTF8);
                SetEditorText(_diskContent);
                AppendLog($"[还原] {Path.GetFileName(_currentFile)} 已重新从磁盘读取");
                RunSyntaxCheck();
            }
            catch (Exception ex)
            {
                AppendLog($"[还原] ❌ {ex.Message}");
            }
        }

        // ───────── 新建 / 删除 ─────────────────────────────────────────

        private void OnNew()
        {
            var dlg = new NewFileDialog { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.FileName)) return;

            var name = dlg.FileName!.Trim();
            if (!name.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) name += ".py";
            var path = Path.Combine(DemoPythonHost.IndicatorsDir, name);
            if (File.Exists(path))
            {
                MessageBox.Show(Window.GetWindow(this), $"{name} 已存在", "新建文件",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var template =
                "\"\"\"" + name + " — 新建空文件。\"\"\"\n" +
                "import numpy as np\n" +
                "from hevo_indicators import register\n\n" +
                "@register('my_handler', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')\n" +
                "def my_handler(close):\n" +
                "    arr = np.asarray(close, dtype=np.float64)\n" +
                "    return arr  # TODO 写你的指标\n";
            try
            {
                File.WriteAllText(path, template, new UTF8Encoding(false));
                RefreshFileList();
                var newSel = _fileEntries.FirstOrDefault(e => e.Name == name);
                if (newSel != null) lstFiles.SelectedItem = newSel;
                AppendLog($"[新建] ✓ {name}");
            }
            catch (Exception ex)
            {
                AppendLog($"[新建] ❌ {ex.Message}");
            }
        }

        private void OnDelete()
        {
            var selected = (lstFiles.SelectedItem as PyFileEntry)?.Name;
            if (string.IsNullOrEmpty(selected)) return;

            var ans = MessageBox.Show(Window.GetWindow(this),
                $"删除 {selected}?", "删除文件",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ans != MessageBoxResult.Yes) return;

            var path = Path.Combine(DemoPythonHost.IndicatorsDir, selected!);
            try
            {
                DemoPythonHost.Registry.UnregisterBySourceFile(path);
                File.Delete(path);
                AppendLog($"[删除] ✓ {selected}");
                RefreshFileList();
            }
            catch (Exception ex)
            {
                AppendLog($"[删除] ❌ {ex.Message}");
            }
        }

        // ───────── 杂项 ────────────────────────────────────────────────

        private void AppendLog(string s)
        {
            txtOutput.AppendText(s + Environment.NewLine);
            txtOutput.ScrollToEnd();
        }
    }

    /// <summary>
    /// AvalonEdit BackgroundRenderer:在指定行画半透明红色高亮 + 红色虚线下划线代理"squiggle"。
    /// 编辑器调 <see cref="SetError"/> / <see cref="Clear"/> 切状态后强制 redraw。
    /// </summary>
    internal sealed class ErrorMarkerRenderer : IBackgroundRenderer
    {
        private int? _line;
        private int? _column;

        public KnownLayer Layer => KnownLayer.Selection;

        public void SetError(ICSharpCode.AvalonEdit.TextEditor editor, int? line, int? column)
        {
            _line = line;
            _column = column;
            editor.TextArea.TextView.InvalidateLayer(Layer);
        }

        public void Clear(ICSharpCode.AvalonEdit.TextEditor editor)
        {
            if (_line == null && _column == null) return;
            _line = null;
            _column = null;
            editor.TextArea.TextView.InvalidateLayer(Layer);
        }

        public void Draw(TextView textView, System.Windows.Media.DrawingContext drawingContext)
        {
            if (_line == null) return;
            if (textView.VisualLines.Count == 0) return;

            int targetLine = _line.Value;
            foreach (var vl in textView.VisualLines)
            {
                if (vl.FirstDocumentLine.LineNumber != targetLine) continue;

                // 行整宽红色半透明背景
                double y = vl.VisualTop - textView.VerticalOffset;
                var fillBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xEF, 0x53, 0x50));
                fillBrush.Freeze();
                var rect = new Rect(0, y, textView.ActualWidth, vl.Height);
                drawingContext.DrawRectangle(fillBrush, null, rect);

                // 行底红色实线 underline 代理"squiggle"(简化版,无波浪)
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50)), 1.5);
                pen.Freeze();
                drawingContext.DrawLine(pen, new Point(0, y + vl.Height - 1), new Point(textView.ActualWidth, y + vl.Height - 1));
                break;
            }
        }
    }

    /// <summary>
    /// PyEditor 文件列表的行模型 —— 文件名 + 启用/禁用 toggle。WPF 双向绑定 IsEnabled,
    /// PropertyChanged 事件触发 PyEditorView.OnFileEntryToggled 持久化 + 热重载。
    /// </summary>
    public sealed class PyFileEntry : INotifyPropertyChanged
    {
        public string Name { get; }
        private bool _isEnabled;
        public bool IsEnabled
        {
            get => _isEnabled;
            set { if (_isEnabled != value) { _isEnabled = value; OnPropertyChanged(); RefreshColor(); } }
        }
        private Brush _color = Brushes.LightGray;
        public Brush Color { get => _color; private set { _color = value; OnPropertyChanged(); } }

        public PyFileEntry(string name, bool isEnabled)
        {
            Name = name;
            _isEnabled = isEnabled;
            RefreshColor();
        }

        public void RefreshColor()
        {
            Color = _isEnabled
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD0, 0xD8, 0xE0))   // 亮(启用)
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x68, 0x70));  // 灰(禁用)
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));
    }

    /// <summary>新建文件用的迷你输入框 —— 不引第三方对话框库。</summary>
    internal class NewFileDialog : Window
    {
        private readonly TextBox _tb;

        public string? FileName { get; private set; }

        public NewFileDialog()
        {
            Title = "新建 Python 文件";
            Width = 360;
            Height = 140;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x26, 0x2C));
            ResizeMode = ResizeMode.NoResize;

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lbl = new TextBlock
            {
                Text = "文件名(可省略 .py 后缀):",
                Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0xC8, 0xD0)),
                Margin = new Thickness(0, 0, 0, 6),
            };
            Grid.SetRow(lbl, 0);
            grid.Children.Add(lbl);

            _tb = new TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x0F, 0x12)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xE0, 0xE8)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
                Padding = new Thickness(6),
                FontFamily = new FontFamily("Consolas"),
            };
            _tb.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
                else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            };
            Grid.SetRow(_tb, 1);
            grid.Children.Add(_tb);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            var btnOk = new Button
            {
                Content = "确定",
                Padding = new Thickness(16, 4, 16, 4),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x4A, 0x6E)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
            };
            btnOk.Click += (_, __) => Accept();
            var btnCancel = new Button
            {
                Content = "取消",
                Padding = new Thickness(16, 4, 16, 4),
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3C, 0x44)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0xC8, 0xD0)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3C, 0x44)),
            };
            btnCancel.Click += (_, __) => Close();
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            Grid.SetRow(btnPanel, 2);
            grid.Children.Add(btnPanel);

            Content = grid;
            Loaded += (_, __) => _tb.Focus();
        }

        private void Accept()
        {
            var name = _tb.Text?.Trim();
            if (string.IsNullOrEmpty(name)) return;
            FileName = name;
            DialogResult = true;
            Close();
        }
    }
}
