using Hevo.Charting.Core;
using System.Windows;
using System.Windows.Input;

namespace Hevo.Charting
{
    public static class WorkflowWPFEventExtension
    {
        /// <summary>
        /// 💥 终极 UI 事件路由隧道
        /// 将原生的 WPF 路由事件包装为流，并强制在触发时挂载当前 Feature 的黑板拓扑上下文。
        /// </summary>
        /// <typeparam name="TArgs">WPF 的事件参数类型</typeparam>
        /// <param name="feature">发起事件监听的图表功能组件</param>
        /// <param name="routedEvent">需要挂载的底层路由事件 (如 UIElement.MouseMoveEvent)</param>
        /// <returns>已经被拦截并注入上下文的渲染事件流</returns>
        public static IRenderFlow<TArgs> OnUIEvent<TArgs>(this ChartFeature feature, RoutedEvent routedEvent)
                    where TArgs : RoutedEventArgs
        {
            // 1. 获取原始的 WPF 底层事件流
            var rawFlow = Workflow.OnUIEvent<TArgs>(feature.Chart, routedEvent);

            // 2. 💥 框架级拓扑追踪拦截
            var contextFlow = new WorkflowEngine<TArgs>((next, error) =>
            {
                return rawFlow.Subscribe(
                    val =>
                    {
#if DEBUG
                        // 💥 魔法时刻：开启基于 AsyncLocal 的全局跟踪！
                        // 这个 Scope 内部发生的所有黑板数据读写，均会被计入当前 Feature 的责任链，
                        // 便于追踪脏数据和分析性能瓶颈。
                        using (DevTools.TopologyTracer.EnterScope(feature))
#endif
                        {
                            // 在上下文庇护下推送给业务层订阅者
                            next(val);
                        }
                    },
                    error
                );
            });

            // 3. 收尾：对接图表级联生命周期
            return contextFlow
                   .BindTo(feature.Chart)
                   .OwnedBy(feature);
        }

        // ==========================================
        // 🍬 纯享语法糖：语义化绑定
        // ==========================================

        /// <summary>
        /// 绑定鼠标事件并注入当前 Feature 拓扑上下文
        /// </summary>
        public static IRenderFlow<MouseEventArgs> OnMouse(this ChartFeature feature, RoutedEvent routedEvent)
            => feature.OnUIEvent<MouseEventArgs>(routedEvent);

        /// <summary>
        /// 绑定键盘事件并注入当前 Feature 拓扑上下文
        /// </summary>
        public static IRenderFlow<KeyEventArgs> OnKey(this ChartFeature feature, RoutedEvent routedEvent)
            => feature.OnUIEvent<KeyEventArgs>(routedEvent);

        /// <summary>
        /// 绑定拖拽事件并注入当前 Feature 拓扑上下文
        /// </summary>
        public static IRenderFlow<DragEventArgs> OnDrag(this ChartFeature feature, RoutedEvent routedEvent)
            => feature.OnUIEvent<DragEventArgs>(routedEvent);

        /// <summary>
        /// 绑定触控事件并注入当前 Feature 拓扑上下文
        /// </summary>
        public static IRenderFlow<TouchEventArgs> OnTouch(this ChartFeature feature, RoutedEvent routedEvent)
            => feature.OnUIEvent<TouchEventArgs>(routedEvent);
    }
}

