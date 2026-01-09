using Hevo.Charting.Abstractions;
using Hevo.Charting.Renderers;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 复合图层：物理上是一个 Visual，逻辑上包含多个 IChartLayer
    /// 共享同一个dc
    /// </summary>
    public class CompositeChartLayer : ChartLayer, IChartLayer
    {
        public CompositeChartLayer(ChartLayerType level)
        {
            Level = level;
        }

        // 复用 IChartLayer 作为子节点！
        // 内部持有的子层列表 (它们只是逻辑单元，不挂载到 VisualTree)
        private readonly List<IChartLayer> _children = new();

        /// <summary>
        /// 添加一个子层。
        /// 子层可以是任何标准的 IChartLayer 实现 (如 LineLayer, BarLayer 等)。
        /// 注意：添加的顺序决定了绘制顺序 (Z-Order)。
        /// </summary>
        public void AddChild(IChartLayer child)
        {
            _children.Add(child);
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink sink, WidgetBuffer widgetSink)
        {
            // 1. 强转 Sink 为 Buffer，以便使用 Append 功能
            // 因为 VisualElement 传进来的是它自己的 _buffer，所以这个强转是安全的
            var masterBuffer = (DrawingBuffer)sink;

            // 1. 尝试获取查找器能力
            // 如果 data 是由 RenderContext 创建的，它一定实现了 IChildDataProvider
            var provider = data as IChildDataProvider;

            foreach (var child in _children)
            {
                // 2. 为子层获取专属数据
                IVisualData childData;

                if (provider != null)
                {
                    // 从 Context 中精准查找该 child 的配置 (Local + Global)
                    childData = provider.GetChildData(child) ?? data;
                }
                else
                {
                    // 降级策略：如果没有 provider (极少见)，就透传父级 data
                    childData = data;
                }

                // 3. 让子层使用它自己的数据干活
                child.Update(childData);
                masterBuffer.Append(child.Buffer);
            }
        }
    }
}
