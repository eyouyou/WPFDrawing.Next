using Hevo.Charting.Core;

namespace Hevo.Charting.Buildin
{
    // 💥 单点事实源：封装格式化逻辑，彻底告别图层内的 string.Format 碎片化代码
    public class SelfJudgementFormatter : IHevoFormatter<double>
    {
        public string Format(double value)
        {
            double absVal = Math.Abs(value);

            // 极小值兜底
            if (absVal < 0.000001) return value.ToString("G4");

            // 业务量级自适应
            if (absVal >= 1_000_000_000_000) return (value / 1_000_000_000_000).ToString("G3") + "万亿";
            if (absVal >= 100_000_000) return (value / 100_000_000).ToString("G4") + "亿";
            if (absVal >= 10_000) return (value / 10_000).ToString("G4") + "万";

            return value.ToString("G4");
        }
    }

    public class PercentFormatter : IHevoFormatter<float>
    {
        public string Format(float value) => (value * 100).ToString("F2") + "%";
    }
}
