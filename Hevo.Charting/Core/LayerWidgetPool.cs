using System.Windows;
using System.Windows.Controls;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 图层专属的 WPF 控件池。实现 1 Layer -> N Widgets 的极速复用。
    /// </summary>
    internal class LayerWidgetPool
    {
        private readonly Canvas _interactionCanvas;

        // 缓存池：保存属于这个图层的所有物理控件
        private readonly List<ContentPresenter> _pool = new();

        // 上一帧 active widget 数,用来只在边界变化时切换 Visibility — DependencyProperty 设值
        // 即使值不变也要走 WPF 内部 coercion / 失效逻辑,稳态 active 数不变时省一整轮 DP set。
        private int _activeCount = 0;

        public LayerWidgetPool(Canvas interactionCanvas)
        {
            _interactionCanvas = interactionCanvas;
        }

        /// <summary>
        /// 核心魔法：根据图层发来的 N 条指令，同步池子里的控件
        /// </summary>
        public void Sync(IReadOnlyList<WidgetCommand> commands)
        {
            int newActive = commands.Count;
            int prevActive = _activeCount;

            // 1. 扩容：如果指令数 > 池子现有的控件数，造几个新的补充进去
            //    新建的 ContentPresenter Width/Height 默认就是 double.NaN(自适应),
            //    因此只在创建时一次性配置 IsHitTestVisible,后续每帧不再重设尺寸。
            while (_pool.Count < newActive)
            {
                var cp = new ContentPresenter
                {
                    IsHitTestVisible = false // 默认穿透，交由内部 DataTemplate 决定
                };
                _interactionCanvas.Children.Add(cp);
                _pool.Add(cp);
            }

            // 2. 更新当前 [0, newActive) 的指令
            //    Visibility 只在"原本是隐藏"的位置(i >= prevActive)上切回 Visible,稳态零 DP 改动。
            for (int i = 0; i < newActive; i++)
            {
                var cp = _pool[i];
                var cmd = commands[i];

                if (i >= prevActive) cp.Visibility = Visibility.Visible;

                cp.Content = cmd.ViewModel;

                // 只负责定位 — Canvas 的 Left/Top 是 attached DP,WPF 内部值相等会自动短路。
                Canvas.SetLeft(cp, cmd.Bounds.X);
                Canvas.SetTop(cp, cmd.Bounds.Y);
            }

            // 3. 回收：仅处理上一帧用到、本帧不再用的范围 [newActive, prevActive)
            //    稳态(active 数不变)时这段循环根本不进。
            for (int i = newActive; i < prevActive; i++)
            {
                _pool[i].Visibility = Visibility.Collapsed;
                _pool[i].Content = null; // 解除 ViewModel 引用，防止内存泄漏
            }

            _activeCount = newActive;
        }

        /// <summary>
        /// 图层被移除时，销毁池子里所有的物理控件
        /// </summary>
        public void Destroy()
        {
            foreach (var cp in _pool)
            {
                _interactionCanvas.Children.Remove(cp);
            }
            _pool.Clear();
            _activeCount = 0;
        }
    }
}