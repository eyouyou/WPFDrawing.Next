using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Hevo.Drawing.LowCodeDemo
{
    /// <summary>
    /// 通知中心 UI —— 订阅 <see cref="MockNotifier.NotificationFired"/>,marshal 到 Dispatcher 后追加到 DataGrid。
    /// 加载时先回填 <see cref="MockNotifier.History"/> 历史快照,跨 Tab 切换不丢上下文。
    /// </summary>
    public partial class NotificationCenterView : UserControl
    {
        // 后备集合 —— DataGrid 通过 ItemsSource 绑定;Add 必须在 UI 线程。
        private readonly ObservableCollection<NotificationEvent> _items = new();

        // 用 CollectionView 装一层做 channel 过滤(checkbox 切换时 Refresh,无需 rebind)。
        private readonly System.ComponentModel.ICollectionView _view;

        public NotificationCenterView()
        {
            InitializeComponent();

            _view = CollectionViewSource.GetDefaultView(_items);
            _view.Filter = obj =>
            {
                if (obj is not NotificationEvent ev) return true;
                return ev.Kind switch
                {
                    NotificationKind.Email    => cbShowEmail?.IsChecked    == true,
                    NotificationKind.DingTalk => cbShowDingTalk?.IsChecked == true,
                    NotificationKind.Webhook  => cbShowWebhook?.IsChecked  == true,
                    NotificationKind.Sms      => cbShowSms?.IsChecked      == true,
                    NotificationKind.Alert    => cbShowAlert?.IsChecked    == true,
                    NotificationKind.Order    => cbShowOrder?.IsChecked    == true,
                    _ => true,
                };
            };
            grid.ItemsSource = _view;

            // TabControl 默认会在切换 Tab 时把当前内容从可视树剥离 → 触发 Unloaded;切回时触发 Loaded
            // 但不重跑 ctor。所以 subscribe / 回填必须放 Loaded,否则切走一次就再也收不到通知。
            Loaded   += OnLoaded;
            Unloaded += OnUnloaded;

            btnClear.Click += (_, __) =>
            {
                MockNotifier.Clear();
                _items.Clear();   // MockNotifier.Clear 也会 fire 一条"已清空" → 走 OnNotificationFired 落入 _items,
                                   // 这里再清一遍,确保 UI 只剩"已清空"那一条作为视觉反馈。
                UpdateStatus();
            };

            cbShowEmail.Checked    += (_, __) => _view.Refresh();
            cbShowEmail.Unchecked  += (_, __) => _view.Refresh();
            cbShowDingTalk.Checked   += (_, __) => _view.Refresh();
            cbShowDingTalk.Unchecked += (_, __) => _view.Refresh();
            cbShowWebhook.Checked    += (_, __) => _view.Refresh();
            cbShowWebhook.Unchecked  += (_, __) => _view.Refresh();
            cbShowSms.Checked        += (_, __) => _view.Refresh();
            cbShowSms.Unchecked      += (_, __) => _view.Refresh();
            cbShowAlert.Checked      += (_, __) => _view.Refresh();
            cbShowAlert.Unchecked    += (_, __) => _view.Refresh();
            cbShowOrder.Checked      += (_, __) => _view.Refresh();
            cbShowOrder.Unchecked    += (_, __) => _view.Refresh();
        }

        private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            // 用 History 全量重建 _items —— MaxHistory=1000 上限,代价可控。
            // 这样保证:① 首次加载能看到先跑的策略历史;② 切走/切回期间产生的消息也补得上。
            _items.Clear();
            foreach (var ev in MockNotifier.History) _items.Add(ev);
            UpdateStatus();

            MockNotifier.NotificationFired += OnNotificationFired;
        }

        private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
        {
            MockNotifier.NotificationFired -= OnNotificationFired;
        }

        private void OnNotificationFired(NotificationEvent ev)
        {
            // Python 调用 send_xxx 时在 Python indicator 解析线程 fire,UI mutate 必须 marshal 到 Dispatcher。
            // 用 BeginInvoke 不阻塞 Python 调用(返回 immediately)。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _items.Add(ev);
                UpdateStatus();
                if (cbAutoScroll?.IsChecked == true && grid.Items.Count > 0)
                {
                    grid.ScrollIntoView(grid.Items[grid.Items.Count - 1]);
                }
            }));
        }

        private void UpdateStatus()
        {
            txtStatus.Text = $"共 {_items.Count} 条通知 / 历史上限 1000 条 / 渠道过滤生效后可见 {grid.Items.Count} 条";
        }
    }

    /// <summary>把 <see cref="NotificationKind"/> 翻成显示标签(含 emoji)。</summary>
    public sealed class NotificationKindToLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is NotificationKind k ? k switch
            {
                NotificationKind.Email    => "✉ Email",
                NotificationKind.DingTalk => "📱 DingTalk",
                NotificationKind.Webhook  => "🪝 Webhook",
                NotificationKind.Sms      => "💬 SMS",
                NotificationKind.Alert    => "⚠ Alert",
                NotificationKind.Order    => "💰 Order",
                _ => k.ToString(),
            } : "";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>把 <see cref="NotificationKind"/> 翻成 SolidColorBrush(渠道颜色编码)。</summary>
    public sealed class NotificationKindToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush _email    = new(Color.FromRgb(0x4F, 0xC3, 0xF7));
        private static readonly SolidColorBrush _dingtalk = new(Color.FromRgb(0x66, 0xBB, 0x6A));
        private static readonly SolidColorBrush _webhook  = new(Color.FromRgb(0xCE, 0x93, 0xD8));
        private static readonly SolidColorBrush _sms      = new(Color.FromRgb(0xFF, 0xB7, 0x4D));
        private static readonly SolidColorBrush _alert    = new(Color.FromRgb(0x8A, 0x92, 0x9C));
        private static readonly SolidColorBrush _order    = new(Color.FromRgb(0xEF, 0x53, 0x50));
        private static readonly SolidColorBrush _default  = new(Color.FromRgb(0xC0, 0xC8, 0xD0));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is NotificationKind k ? k switch
            {
                NotificationKind.Email    => _email,
                NotificationKind.DingTalk => _dingtalk,
                NotificationKind.Webhook  => _webhook,
                NotificationKind.Sms      => _sms,
                NotificationKind.Alert    => _alert,
                NotificationKind.Order    => _order,
                _ => _default,
            } : (object)_default;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
