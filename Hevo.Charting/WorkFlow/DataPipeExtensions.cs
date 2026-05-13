using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.DevTools;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.WorkFlow
{
    public static class DataPipeExtensions
    {
        // ==========================================
        // 🚀 SoA 轨道：列直连 (替代曾经的 LinkSoAViewportStream)
        //   - 不切片：消费者拿到完整列 Memory，自己用 ActiveRange clip
        //   - 不写 Offset：world 索引 == array 下标，不再有两套坐标系
        //   - 0 拷贝：KLineDataBlock 已改成 T[] 容器，列 Memory 直接暴露给端口
        //   - 不再 Watch viewport：viewport 变化的反应链条由消费者自己 UsePort(ActiveRange) 接管
        // ==========================================
        public static IWorkflow<DataBlackboard> LinkSoAColumnStream<TSource, TBlock>(
                    this DataPipeBuilder<TSource, TBlock> builder,
                    Action<SoAColumnConfigurator<TBlock>> configure)
                    where TSource : BufferedDataSource<TSource, TBlock>
        {
            var cfg = new SoAColumnConfigurator<TBlock>(builder.InternalPipe);
            configure(cfg);

            builder.InternalPipe.AddIngestor(new ColumnPublishIngestor<TBlock>(cfg.Publishers));
            return builder.Seal();
        }

        /// <summary>
        /// SoA 列流的"裸 pipe"重载 —— 给蓝图自定义 <see cref="LowCode.Designer.IPipelinePolicy"/>
        /// 用,该路径拿到的是 <see cref="UniversalDataPipe{TItem}"/> 而非 fluent <c>DataPipeBuilder</c>。
        /// 不返回 <c>IWorkflow&lt;DataBlackboard&gt;</c>(policy 不需要 BindTo,管线由 DynamicChartSchema 接管),
        /// 仅注册列发布 ingestor 即可。
        /// </summary>
        public static void LinkSoAColumnStream<TBlock>(
                    this UniversalDataPipe<TBlock> pipe,
                    Action<SoAColumnConfigurator<TBlock>> configure)
        {
            var cfg = new SoAColumnConfigurator<TBlock>(pipe);
            configure(cfg);
            pipe.AddIngestor(new ColumnPublishIngestor<TBlock>(cfg.Publishers));
        }
    }

    /// <summary>
    /// SoA 列发布配置器：业务声明"哪个 block 字段对应哪个端口"。
    /// </summary>
    public class SoAColumnConfigurator<TBlock>
    {
        private readonly UniversalDataPipe<TBlock> _pipe;
        internal List<IColumnPublisher<TBlock>> Publishers { get; } = new();

        internal SoAColumnConfigurator(UniversalDataPipe<TBlock> pipe)
        {
            _pipe = pipe;
        }

        internal UniversalDataPipe<TBlock> InternalPipe => _pipe;

        /// <summary>
        /// 把 block 的某个 ReadOnlyMemory 列绑定到目标端口（0 拷贝直连）。
        /// </summary>
        public SoAColumnConfigurator<TBlock> Map<TValue>(
            DataPort<ReadOnlyMemory<TValue>> targetPort,
            Func<TBlock, ReadOnlyMemory<TValue>> selector)
        {
            Publishers.Add(new ColumnPublisher<TBlock, TValue>(targetPort, selector));
            return this;
        }

        /// <summary>
        /// 在 SoA 管线中插入前置算子（如序列变换、派生计算）。
        /// </summary>
        public SoAColumnConfigurator<TBlock> Plug(IDataIngestor<TBlock> ingestor)
        {
            _pipe.AddIngestor(ingestor);
            return this;
        }
    }

    internal interface IColumnPublisher<TBlock>
    {
        void Publish(TBlock block, DataBlackboard board);
    }

    internal class ColumnPublisher<TBlock, TValue> : IColumnPublisher<TBlock>
    {
        private readonly DataPort<ReadOnlyMemory<TValue>> _targetPort;
        private readonly Func<TBlock, ReadOnlyMemory<TValue>> _selector;

        public ColumnPublisher(DataPort<ReadOnlyMemory<TValue>> port, Func<TBlock, ReadOnlyMemory<TValue>> selector)
        {
            _targetPort = port; _selector = selector;
        }

        public void Publish(TBlock block, DataBlackboard board)
        {
            // ForceWrite：每次都推一次。下游 dirty 由 board 的版本机制管，不在此判断。
            board.ForceWrite(_targetPort, _selector(block));
        }
    }

    internal class ColumnPublishIngestor<TBlock> : IDataIngestor<TBlock>
    {
        private readonly List<IColumnPublisher<TBlock>> _publishers;

        public ColumnPublishIngestor(List<IColumnPublisher<TBlock>> publishers)
        {
            _publishers = publishers;
        }

        public void Process(DataSnapshot<TBlock> snapshot, DataBlackboard board)
        {
            if (snapshot.Count == 0) return;

            // SoA 中 TBlock 是全量对象，取第一个即可
            TBlock block = snapshot.AsSpan()[0];

#if DEBUG
            using var scope = TopologyTracer.EnterScope(null);
#endif
            for (int i = 0; i < _publishers.Count; i++) _publishers[i].Publish(block, board);
        }
    }
}
