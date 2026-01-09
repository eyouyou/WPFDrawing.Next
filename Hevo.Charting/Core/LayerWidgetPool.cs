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

        public LayerWidgetPool(Canvas interactionCanvas)
        {
            _interactionCanvas = interactionCanvas;
        }

        /// <summary>
        /// 核心魔法：根据图层发来的 N 条指令，同步池子里的控件
        /// </summary>
        public void Sync(IReadOnlyList<WidgetCommand> commands)
        {
            // 1. 扩容：如果指令数 > 池子现有的控件数，造几个新的补充进去
            while (_pool.Count < commands.Count)
            {
                var cp = new ContentPresenter
                {
                    IsHitTestVisible = false // 默认穿透，交由内部 DataTemplate 决定
                };
                _interactionCanvas.Children.Add(cp);
                _pool.Add(cp);
            }

            // 2. 更新与激活：应用当前的 N 条指令
            for (int i = 0; i < commands.Count; i++)
            {
                var cp = _pool[i];
                var cmd = commands[i];

                cp.Visibility = Visibility.Visible;
                cp.Content = cmd.ViewModel;

                // 💥 1. 只负责定位！
                Canvas.SetLeft(cp, cmd.Bounds.X);
                Canvas.SetTop(cp, cmd.Bounds.Y);

                // 💥 2. 彻底干掉强行赋值宽高！
                // 只有当业务层真的传了特定的强制尺寸（比如覆盖全屏的蒙版），我们才去设。
                // 对于悬浮窗，如果它的尺寸是根据内容自适应的，强行设 Width 会触发浮点截断 Bug！
                // cp.Width = cmd.Bounds.Width;   <-- 删掉！
                // cp.Height = cmd.Bounds.Height; <-- 删掉！

                // 优雅的替代方案：清除尺寸限制，让 ContentPresenter 开启 Auto 模式自然撑开
                cp.Width = double.NaN;
                cp.Height = double.NaN;
            }

            // 3. 回收：把多余的控件隐藏起来，供下一帧复用
            for (int i = commands.Count; i < _pool.Count; i++)
            {
                _pool[i].Visibility = Visibility.Collapsed;
                _pool[i].Content = null; // 解除 ViewModel 引用，防止内存泄漏
            }
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
        }
    }
}