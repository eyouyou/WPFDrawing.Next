using System.Reflection;

namespace Hevo.Charting.LowCode
{
    public abstract class DataProcessor : IComputeNode
    {
        // 显式实现接口，把“脏活”藏起来
        // 显式实现接口，隐藏初始化细节
        void IComputeNode.Initialize(Dictionary<string, string> mappings, Dictionary<string, object> activePorts)
        {
            var props = this.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                            .Where(p => p.GetCustomAttribute<PortAttribute>() != null);

            foreach (var prop in props)
            {
                // 如果构造函数没赋值
                if (prop.GetValue(this) != null) continue;

                // 尝试从映射表找 ID，再从黑板找实例
                if (mappings.TryGetValue(prop.Name, out string? portId) && activePorts.TryGetValue(portId, out var port))
                {
                    prop.SetValue(this, port);
                }
            }
            OnInitialized();
        }

        protected virtual void OnInitialized() { }
        public abstract void Execute(DataBlackboard board);
    }
}
