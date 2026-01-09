
using System.Runtime.CompilerServices;

namespace Hevo.Charting.LowCode
{
    /// <summary>
    /// [统一引脚标签] 
    /// 标记在 TItem 属性上 -> 导出为 Column 流
    /// 标记在 Context 属性上 -> 导出为标量环境值
    /// 标记在 Node 属性上 -> 定义为逻辑插槽
    /// 通过该attribute对feature的引脚进行监听变化 变化了则执行project
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public class PortAttribute : Attribute
    {
        public string? Id { get; }
        public PortAttribute([CallerMemberName] string id = "")
        {
            Id = id;
        }
    }

    /// <summary>
    /// 贴在业务实体上，指明生成的引脚组名称
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
    public class PortGroupAttribute : Attribute
    {
        public string GroupName { get; }
        public PortGroupAttribute(string groupName) => GroupName = groupName;
    }
}
