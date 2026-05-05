using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;

namespace Hevo.Charting.Buildin
{
    /// <summary>
    /// 💥 万能修饰器：用 Lambda 解决 90% 的样式定制需求，拒绝类爆炸！
    /// </summary>
    public class DelegateTickStylePolicy : ITickStylePolicy
    {
        private readonly Func<double, string> _formatter;
        private readonly Func<double, IHevoBrush?>? _brushSelector;
        private readonly Func<double, LineStyle?>? _styleSelector;

        public DelegateTickStylePolicy(
            Func<double, string> formatter,
            Func<double, IHevoBrush?>? brushSelector = null,
            Func<double, LineStyle?>? styleSelector = null)
        {
            _formatter = formatter;
            _brushSelector = brushSelector;
            _styleSelector = styleSelector;
        }

        public string FormatLabel(double value) => _formatter(value);
        public IHevoBrush? GetOverrideBrush(double value) => _brushSelector?.Invoke(value);
        public LineStyle? GetOverrideStyle(double value) => _styleSelector?.Invoke(value);
    }
}
