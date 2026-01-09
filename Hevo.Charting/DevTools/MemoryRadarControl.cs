using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Hevo.Charting.DevTools
{
    /// <summary>
    /// 💥 Hevo 物理内存与视口迷你图监控器
    /// </summary>
    public class MemoryRadarControl : Grid
    {
        private readonly Rectangle _totalMemoryBar;
        private readonly Rectangle _viewportThumb;
        private readonly TextBlock _statsText;

        private DataBlackboard? _monitoredBoard;
        private ViewportPorts? _vp;

        public MemoryRadarControl()
        {
            this.Height = 60;
            this.Margin = new Thickness(10);

            // 1. 底色条：代表物理总容量 (LogicalLength)
            _totalMemoryBar = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
                RadiusX = 4,
                RadiusY = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            // 2. 高亮滑块：代表屏幕视口 (ActiveRange)
            _viewportThumb = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(180, 0, 122, 204)), // 半透明蓝色
                RadiusX = 4,
                RadiusY = 4,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            // 3. 状态文字
            _statsText = new TextBlock
            {
                Foreground = Brushes.LightGray,
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            var trackGrid = new Grid { Margin = new Thickness(0, 0, 0, 20) };
            trackGrid.Children.Add(_totalMemoryBar);
            trackGrid.Children.Add(_viewportThumb);
            trackGrid.Children.Add(_statsText);

            this.Children.Add(trackGrid);

            // 监听自身的尺寸变化，以重绘滑块
            this.SizeChanged += (s, e) => UpdateRadar();
        }

        /// <summary>
        /// 💥 将监控器挂载到图表的黑板上
        /// </summary>
        public void Attach(DataBlackboard board, ViewportPorts vp)
        {
            if (_monitoredBoard != null)
            {
                _monitoredBoard.OnPortUpdated -= Blackboard_OnPortUpdated;
            }

            _monitoredBoard = board;
            _vp = vp;

            if (_monitoredBoard != null)
            {
                // 监听黑板数据变化
                _monitoredBoard.OnPortUpdated += Blackboard_OnPortUpdated;
                UpdateRadar();
            }
        }

        public void Detach()
        {
            if (_monitoredBoard != null)
            {
                _monitoredBoard.OnPortUpdated -= Blackboard_OnPortUpdated;
                _monitoredBoard = null;
            }
        }

        private void Blackboard_OnPortUpdated(object port)
        {
            if (_vp == null) return;

            // 💥 只关心池子大小和视口位置的变化
            if (ReferenceEquals(port, _vp.LogicalLength) || ReferenceEquals(port, _vp.ActiveRange))
            {
                // 切回 UI 线程更新雷达控件
                Dispatcher.InvokeAsync(UpdateRadar, DispatcherPriority.Render);
            }
        }

        private int _lastLength = -1;

        private void UpdateRadar()
        {
            if (_monitoredBoard == null || _vp == null || this.ActualWidth == 0) return;
            using (_monitoredBoard.AcquireReadLock())
            {
                int logicalLength = _monitoredBoard.Read(_vp.LogicalLength);
                var activeRange = _monitoredBoard.Read(_vp.ActiveRange);

                if (logicalLength <= 0 || !activeRange.IsValid) return;

                // ==========================================
                // 💥 视觉特效：当池子扩大时 (网络数据回来了)，让底条闪烁一下绿光！
                // ==========================================
                if (_lastLength != -1 && logicalLength > _lastLength)
                {
                    FlashMemoryExpansion();
                }
                _lastLength = logicalLength;

                // ==========================================
                // 💥 映射算法：逻辑坐标 -> 迷你图物理坐标
                // ==========================================
                double trackWidth = this.ActualWidth;

                // 视口占总数据的百分比
                double widthPct = activeRange.Span / logicalLength;
                // 视口左侧在总数据中的偏移百分比
                double leftPct = activeRange.Min / logicalLength;

                // 严防越界溢出视觉
                widthPct = Math.Clamp(widthPct, 0.01, 1.0);
                leftPct = Math.Clamp(leftPct, 0, 1.0 - widthPct);

                _viewportThumb.Width = trackWidth * widthPct;
                _viewportThumb.Margin = new Thickness(trackWidth * leftPct, 0, 0, 0);

                // 更新文字 readout
                _statsText.Text = $"物理池: {logicalLength} 根 | 视口: [{activeRange.Min:F1} ~ {activeRange.Max:F1}] | 占比: {widthPct:P1}";
            }
        }

        private void FlashMemoryExpansion()
        {
            // 数据进来时，背景条瞬间变成绿色，然后 300 毫秒恢复深灰
            _totalMemoryBar.Fill = new SolidColorBrush(Color.FromRgb(40, 150, 60));

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (s, e) =>
            {
                _totalMemoryBar.Fill = new SolidColorBrush(Color.FromRgb(45, 45, 50));
                timer.Stop();
            };
            timer.Start();
        }
    }
}
