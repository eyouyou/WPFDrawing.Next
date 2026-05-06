using Hevo.Charting.LowCode.Designer.GraphViewer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §4 严格类型语义校验:GraphSchema.AreTypeNamesCompatible 的纯单元测。
    /// 覆盖 nullable 归一 / 数组 fan-in / object 通配三类规则。
    /// </summary>
    public sealed class PortsCompatibleTests
    {
        // 类型名相等 → 兼容(原 MVP 路径回归)
        [Fact]
        public void Compatible_SameTypeName()
        {
            Assert.True(GraphSchema.AreTypeNamesCompatible("RealRange", "RealRange", toIsArray: false));
        }

        // object 通配 —— 任一端 → 兼容
        [Theory]
        [InlineData("object", "RealRange")]
        [InlineData("RealRange", "object")]
        [InlineData("object", "object")]
        public void Compatible_ObjectWildcard(string from, string to)
        {
            Assert.True(GraphSchema.AreTypeNamesCompatible(from, to, toIsArray: false));
        }

        // nullable 归一 —— RealRange ↔ RealRange?(运行期端口都是 boxed,nullable 是编译期注解)
        [Theory]
        [InlineData("RealRange", "RealRange?")]
        [InlineData("RealRange?", "RealRange")]
        [InlineData("RealRange?", "RealRange?")]
        public void Compatible_StripsNullableSuffix(string from, string to)
        {
            Assert.True(GraphSchema.AreTypeNamesCompatible(from, to, toIsArray: false));
        }

        // 数组 fan-in:目标 T[] 接受单源 T(PortBindings 协议本就聚合多源)
        [Fact]
        public void Compatible_ArrayFanIn_AcceptsSingleSource()
        {
            Assert.True(GraphSchema.AreTypeNamesCompatible(
                "ReadOnlyMemory<double>", "ReadOnlyMemory<double>[]", toIsArray: true));
        }

        // 数组 fan-in 同时考虑 nullable:T 可拉到 T?[] 数组
        [Fact]
        public void Compatible_ArrayFanIn_StripsNullableOnElement()
        {
            Assert.True(GraphSchema.AreTypeNamesCompatible(
                "RealRange", "RealRange?[]", toIsArray: true));
        }

        // toIsArray=false 时 [] 后缀不当数组处理 —— 防御:NodeFactory 写出 IsArray 跟 [] 后缀总是同步,
        //   但有人手撸节点时 IsArray=false + 类型名带 [],规则要拒掉避免误兼容
        [Fact]
        public void NotCompatible_BracketSuffixWithoutIsArrayFlag()
        {
            Assert.False(GraphSchema.AreTypeNamesCompatible(
                "RealRange", "RealRange[]", toIsArray: false));
        }

        // 完全不同的类型 → 不兼容(回归)
        [Fact]
        public void NotCompatible_DifferentTypes()
        {
            Assert.False(GraphSchema.AreTypeNamesCompatible("int", "RealRange", toIsArray: false));
        }

        // 数组元素类型不一致 → 不兼容
        [Fact]
        public void NotCompatible_ArrayFanIn_ElementMismatch()
        {
            Assert.False(GraphSchema.AreTypeNamesCompatible(
                "int", "RealRange[]", toIsArray: true));
        }
    }
}
