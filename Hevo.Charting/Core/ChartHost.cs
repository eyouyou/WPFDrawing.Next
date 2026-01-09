using System.Windows;
using System.Windows.Controls;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// WPF 壳控件：XAML 层用 Schema 属性挂 Schema，内部委托给 ChartCell。
    /// Schema 的可暂停生命周期（Phase 11 / §H）由 ChartCell 自己承担，
    /// ChartHost 不重复挂事件——用户直接 `new ChartCell { Template = schema }` 也能享受自动暂停。
    /// </summary>
    public class ChartHost : ContentControl
    {
        public static readonly DependencyProperty SchemaProperty =
            DependencyProperty.Register(nameof(Schema), typeof(ChartSchema), typeof(ChartHost),
                new PropertyMetadata(null, OnSchemaChanged));

        public ChartSchema? Schema
        {
            get => (ChartSchema?)GetValue(SchemaProperty);
            set => SetValue(SchemaProperty, value);
        }

        private readonly ChartCell _cell = new();

        public ChartHost() => Content = _cell;

        private static void OnSchemaChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ChartHost)d)._cell.Template = (ChartSchema?)e.NewValue;
        }
    }
}
