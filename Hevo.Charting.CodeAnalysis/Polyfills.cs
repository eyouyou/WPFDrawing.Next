// 必须放在这个特定的命名空间下
namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    /// <summary>
    /// 这是一个编译器补丁，允许在 .NET Standard 2.0 中使用 C# 的 record 和 init 特性。
    /// 编译器只要看到这个类的存在，就不会再报错。
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}