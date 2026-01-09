using Hevo.Charting.Abstractions;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.WorkFlow
{
    // ==========================================
    // 💥 1. 语义化角色定义
    // ==========================================
    public enum DataFlowRole
    {
        /// <summary>
        /// 基础源流 (阶段 1：网络原始数据注入与基础映射)
        /// </summary>
        Primary,

        /// <summary>
        /// 派生计算流 (阶段 2：依赖基础流的纯数学运算，如 MA/MACD 插件)
        /// </summary>
        Derived
    }

    // ==========================================
    // 💥 2. 数据流绑定描述符
    // ==========================================
    public sealed class DataFlowBinding
    {
        public required Action<DataBlackboard> Injector { get; init; }
        public IWorkflow<object>? Trigger { get; init; }
        public DataFlowRole Role { get; init; } = DataFlowRole.Primary;
        public string? FlowName { get; init; } // 供 Debug 拓扑图使用
    }

    // ==========================================
    // 💥 3. 终极数据流宿主契约
    // ==========================================
    public interface IDataFlowHost
    {
        void AttachDataFlow(DataFlowBinding binding);
    }

    // ==========================================
    // 💥 统一管线调度器 (PipelineDispatcher)
    // ==========================================
    internal class PipelineDispatcher : IDataFlowHost, IDisposable
    {
        private readonly List<Action<DataBlackboard>> _primaryInjectors = new();
        private readonly List<Action<DataBlackboard>> _derivedComputers = new();
        private readonly IDisposableHost _lifecycleHost;
        private readonly Action _onPulse;
        private readonly List<IDisposable> _triggerSubscriptions = new();

        public PipelineDispatcher(IDisposableHost lifecycleHost, Action onPulse)
        {
            _lifecycleHost = lifecycleHost;
            _onPulse = onPulse;
        }

        public bool HasFlows => _primaryInjectors.Count > 0 || _derivedComputers.Count > 0;

        public void AttachDataFlow(DataFlowBinding binding)
        {
            if (binding.Role == DataFlowRole.Derived) _derivedComputers.Add(binding.Injector);
            else _primaryInjectors.Add(binding.Injector);

            if (binding.Trigger != null)
            {
                var subscription = binding.Trigger.Subscribe(_ => _onPulse());
                _triggerSubscriptions.Add(subscription);
                _lifecycleHost.RegisterDisposable(subscription);
            }
        }

        public void Execute(DataBlackboard board)
        {
            ExecuteInjection(board);
            ExecuteComputation(board);
        }

        public void ExecuteInjection(DataBlackboard board)
        {
            foreach (var inj in _primaryInjectors) inj(board);
        }

        public void ExecuteComputation(DataBlackboard board)
        {
            foreach (var comp in _derivedComputers) comp(board);
        }

        public void Clear()
        {
            _primaryInjectors.Clear();
            _derivedComputers.Clear();
            _triggerSubscriptions.Clear();
        }

        public void Dispose()
        {
            Clear();
        }
    }
}
