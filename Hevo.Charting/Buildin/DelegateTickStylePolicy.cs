using Hevo.Charting.Abstractions;

namespace Hevo.Charting.Buildin
{
    /// <summary>
    /// 💥 万能修饰器：用 Lambda 解决 90% 的样式定制需求，拒绝类爆炸！
    /// </summary>
    public class DelegateTickStylePolicy : ITickStylePolicy
    {
        private readonly Func<double, string> _formatter;
        private readonly Func<double, IHevoBrush?>? _brushSelector;
        private readonly Func<double, bool>? _baseLineSelector;

        public DelegateTickStylePolicy(
            Func<double, string> formatter,
            Func<double, IHevoBrush?>? brushSelector = null,
            Func<double, bool>? baseLineSelector = null)
        {
            _formatter = formatter;
            _brushSelector = brushSelector;
            _baseLineSelector = baseLineSelector;
        }

        public string FormatLabel(double value) => _formatter(value);
        public IHevoBrush? GetOverrideBrush(double value) => _brushSelector?.Invoke(value);
        public bool IsBaseLine(double value) => _baseLineSelector?.Invoke(value) ?? false;
    }
}
