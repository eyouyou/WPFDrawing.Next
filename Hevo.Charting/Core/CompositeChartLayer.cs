using Hevo.Charting.Abstractions;
using Hevo.Charting.Renderers;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 复合图层:物理上是一个 DrawingVisual,逻辑上包含多个 IChartLayer 子节点。
    ///
    /// 缓冲模型(关键不变量):
    /// - **本层**(CompositeChartLayer)继承自 ChartLayer,持有自己的 _frontBuffer/_backBuffer 双缓冲。
    ///   渲染时 swap、UI 同步消费的就是这一对 buffer。
    /// - **每个子层**(添加进 _children 的 IChartLayer)各自也持有 LayerBuffer(若是 ChartLayer 子类)。
    ///   子层 Update 时把指令写入 *自己的* back buffer,然后通过 Buffer 属性返回 front。
    /// - 父层在 OnUpdate 中执行 child.Update(...) 强制子层各自 swap,然后 masterBuffer.Append(child.Buffer)
    ///   把子层 front buffer 的指令流复制(AddRange)到本层 back buffer。
    /// - 因此最终物理输出是父层的 front buffer;子层的 buffer 是中间产物,不直接被 ChartCell 渲染。
    ///
    /// 注意:子层不应被 ChartCell.AddUnmanagedLayer 注册(否则会被双重渲染)。子层只通过 AddChild 加入。
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
