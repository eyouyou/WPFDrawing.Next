using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Renderers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Hevo.Charting.Layers
{
    public static class TooltipPositionCalculator
    {
        /// <summary>
        /// 💥 智能避让算法：计算 Tooltip 的最终左上角坐标 (纯 float 极速运算)
        /// </summary>
        /// <param name="anchor">锚点 (通常是十字光标交叉点)</param>
        /// <param name="width">Tooltip 的真实物理宽度</param>
        /// <param name="height">Tooltip 的真实物理高度</param>
        /// <param name="plotArea">图表的安全绘图区域</param>
        /// <param name="mode">用户配置的停靠模式</param>
        /// <param name="offset">与光标的间距偏移量</param>
        public static HevoPoint Calc(HevoPoint anchor, float width, float height, HevoRect plotArea, TooltipPositionMode mode, HevoPoint offset)
        {
            float x = anchor.X;
            float y = anchor.Y;

            // 智能模式：动态计算剩余空间，决定最终模式
            if (mode == TooltipPositionMode.Auto)
            {
                bool fitRight = (x + offset.X + width) <= plotArea.Right;
                bool fitBottom = (y + offset.Y + height) <= plotArea.Bottom;

                // 优先停靠右下角，右边放不下就放左边，下面放不下就放上面
                mode = fitRight ?
                       (fitBottom ? TooltipPositionMode.BottomRight : TooltipPositionMode.TopRight) :
                       (fitBottom ? TooltipPositionMode.BottomLeft : TooltipPositionMode.TopLeft);
            }

            // 根据最终模式，计算出左上角绘制起点
            return mode switch
            {
                TooltipPositionMode.TopLeft => new HevoPoint(x - width - offset.X, y - height - offset.Y),
                TooltipPositionMode.TopRight => new HevoPoint(x + offset.X, y - height - offset.Y),
                TooltipPositionMode.BottomLeft => new HevoPoint(x - width - offset.X, y + offset.Y),
                TooltipPositionMode.BottomRight => new HevoPoint(x + offset.X, y + offset.Y),
                _ => new HevoPoint(x + offset.X, y + offset.Y)
            };
        }
    }

    public record TooltipWidgetTrait(
                HevoPoint AnchorPos,                  // 光标锚点
                ReadOnlyMemory<TooltipRow> Rows,      // 内存切片，0-GC 传递数据
                IHevoBrush Background,
                double CornerRadius,
                bool IsVisible,
                TooltipPositionMode PositionMode,     // 停靠模式
                HevoPoint Offset,                     // 偏移量
                HevoRect PlotArea                     // 绘图区边界
            ) : IVisualTrait;

    public partial class TooltipWidgetLayer : ChartLayer
    {
        private readonly Border _widgetContainer;
        private readonly StackPanel _panel;

        // ==========================================
        // 💥 极限性能：UI 控件池 (Object Pool)
        // 彻底杜绝在 OnUpdate 里 new 任何 UI 元素！
        // ==========================================
        private class UIRowCache
        {
            public StackPanel Container { get; } = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            public Ellipse ColorDot { get; } = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            public TextBlock TitleBlock { get; } = new TextBlock { Foreground = Brushes.LightGray, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            public TextBlock ValueBlock { get; } = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

            public UIRowCache()
            {
                Container.Children.Add(ColorDot);
                Container.Children.Add(TitleBlock);
                Container.Children.Add(ValueBlock);
            }
        }

        private readonly List<UIRowCache> _uiPool = new(16); // 预分配池

        public TooltipWidgetLayer()
        {
            _panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10) };

            _widgetContainer = new Border
            {
                Child = _panel,
                CornerRadius = new CornerRadius(6),
                IsHitTestVisible = false,
                Background = new SolidColorBrush(Color.FromArgb(230, 30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), // 用微发光的边框替代阴影
                BorderThickness = new Thickness(1),
                // 💥 救命黑科技 1：强制 WPF 在 Measure 阶段就将尺寸取整到物理像素！
                UseLayoutRounding = true,
                // 💥 救命黑科技 2：强迫边缘对齐设备像素网格，绝不产生虚边和溢出截断！
                SnapsToDevicePixels = true
            };
        }

        private Size _cachedSize = Size.Empty;
        private HevoPoint _lastAnchor = new HevoPoint(-1f, -1f); // 💥 使用 HevoPoint

        // 💥 严格遵守渲染管线：极速复用与排版
        protected override void OnUpdate(IVisualData data, IDrawingSink drawSink, WidgetBuffer widgetSink)
        {
            var trait = data.Get<TooltipWidgetTrait>();

            // 如果数据为空或隐藏，直接把容器移出视口即可 (或者发空的 bounds)
            if (trait == null || !trait.IsVisible || trait.Rows.Length == 0) return;

            // 1. 设置背景色 (走注册表静态解析，因为 Widget 自身不参与 DynamicResource 树绑定)
            _widgetContainer.Background = WpfRenderRegistry.CreateBrush(trait.Background) ?? Brushes.DarkGray;

            var span = trait.Rows.Span;

            // ==========================================
            // 💥 2. UI 控件极速池化复用
            // ==========================================
            // 确保池子里的控件足够多
            while (_uiPool.Count < span.Length)
            {
                var newRow = new UIRowCache();
                _uiPool.Add(newRow);
                _panel.Children.Add(newRow.Container);
            }

            // 更新数据并控制可见性
            for (int i = 0; i < _uiPool.Count; i++)
            {
                var ui = _uiPool[i];
                if (i < span.Length)
                {
                    var rowData = span[i];
                    ui.Container.Visibility = Visibility.Visible;

                    // 💥 多态解析：将 IHevoString 解析为物理字符
                    ui.TitleBlock.Text = WpfRenderRegistry.ResolveString(rowData.Name) + ":";
                    ui.ValueBlock.Text = rowData.Value;

                    // 颜色解析
                    if (rowData.ValueBrush != null)
                        ui.ColorDot.Fill = WpfRenderRegistry.CreateBrush(rowData.ValueBrush);
                }
                else
                {
                    // 隐藏多余的控件，不从视觉树中 Remove，避免重排开销！
                    ui.Container.Visibility = Visibility.Collapsed;
                }
            }

            // ==========================================
            // 💥 3. 核心黑科技：强制提前测量 (Measure) 悬浮窗尺寸
            // ==========================================
            // 告诉 WPF 容器：“假设你有无限大的空间，请告诉我你想要多大尺寸？”
            if (_cachedSize == Size.Empty || Math.Abs(trait.AnchorPos.X - _lastAnchor.X) > 1.0f)
            {
                _widgetContainer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                _cachedSize = _widgetContainer.DesiredSize;
                _lastAnchor = trait.AnchorPos;
            }

            // 💥 从纯 float 数学域获取左上角位置
            HevoPoint topLeft = TooltipPositionCalculator.Calc(
                anchor: trait.AnchorPos,
                width: (float)_cachedSize.Width,
                height: (float)_cachedSize.Height,
                plotArea: trait.PlotArea,
                mode: trait.PositionMode,
                offset: trait.Offset
            );

            // 提交给 Widget 队列 (这里与 WPF 进行最后一次物理映射交接)
            var bounds = new Rect(topLeft.X, topLeft.Y, _cachedSize.Width, _cachedSize.Height);
            widgetSink.UpdateLayout(bounds, _widgetContainer);
        }
    }
}
