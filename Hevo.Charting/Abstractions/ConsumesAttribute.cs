
namespace Hevo.Charting.Abstractions
{
    /// <summary>
    /// 【框架底层契约 - 严禁修改名称】
    /// 配合 Source Generator 使用。用于声明图层被动依赖的特质。
    /// 生成器强依赖此类的类名 "ConsumesAttribute"，重命名将导致编译时契约生成失败！
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class ConsumesAttribute : Attribute // sealed 防止被继承篡改
    {
        public ConsumesAttribute(Type traitType) { }
    }
}
