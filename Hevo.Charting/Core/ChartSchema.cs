using System.Windows;
using System.Windows.Documents;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// c# 基础template类型
    /// </summary>
    public class ChartSchema : ChartTemplate
    {
        /// <summary>
        /// 组合编排和装饰
        /// 编排可以多个
        /// 装饰只能是一个
        /// </summary>
        public ChartAspect Aspect { get; protected set; } = ChartAspect.Empty;
        public ChartSchema(ChartAspect? aspect = null)
        {
            if (aspect != null)
            {
                Aspect = aspect;
            }
            // 它只负责产生一个“容器”
            var factory = new FrameworkElementFactory(typeof(AdornerDecorator));
            factory.Name = "PART_Root";
            VisualTree = factory;
        }

        // ==========================================
        // 💥 引擎生命周期钩子：替代原先单一的 Aspect.Compose
        // ==========================================

        /// <summary>
        /// 编排可以多个：图表引擎在 OnApplyTemplate 时调用的核心入口。
        /// 子类 (如 ReactiveSchema) 将在此处遍历挂载所有的 Feature 组件。
        /// </summary>
        public virtual void ComposeAll(ChartCell chart, RenderContext ctx)
        {
            // 基类默认只挂载装饰器的图层（如果有的话）
            Aspect.Compose(chart, ctx);
        }

        public virtual void DecomposeAll(ChartCell chart, RenderContext ctx)
        {
            // 💥 架构对称：既然 ComposeAll 调了 Aspect.Compose，卸载时也必须通知 Aspect 进行相应的资源销毁！
            Aspect?.Decompose(chart, ctx);
        }
    }
}
