using Hevo.Charting;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 测试用数据项 —— 简单 POCO 即可,反射只看属性。
    /// </summary>
    public class TestDataItem
    {
        public double Value { get; set; }
        public DateTime Time { get; set; }
    }

    /// <summary>
    /// 最小可用的 DataSource —— 仅满足"继承 DataSource&lt;,&gt; + public 无参 ctor + LogicalLength"。
    /// 不喂数据,Stream 永远 idle。蓝图 dry-run / round-trip 测试用,不实际跑数据流。
    /// </summary>
    public class TestDataSource : BufferedDataSource<TestDataSource, TestDataItem>
    {
        public override int LogicalLength => 0;
    }

    /// <summary>
    /// SoA 块式 TItem —— 字段直接是 <see cref="ReadOnlyMemory{T}"/>(对齐 KLineDataBlock 形态),
    /// 而非行式 POCO 的标量字段。验证 NodeFactory 反射 TItem 时也能识别 ROM&lt;X&gt;。
    /// </summary>
    public class SoaTestDataBlock
    {
        public ReadOnlyMemory<double> Closes { get; set; }
    }

    public class SoaTestDataSource : BufferedDataSource<SoaTestDataSource, SoaTestDataBlock>
    {
        public override int LogicalLength => 0;
    }
}
