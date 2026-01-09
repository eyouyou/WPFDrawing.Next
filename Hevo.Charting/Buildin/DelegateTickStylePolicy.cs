using Hevo.Charting.Abstractions;

namespace Hevo.Charting.Buildin
{
    /// <summary>
    /// 💥 万能修饰器：用 Lambda 解决 90% 的样式定制需求，拒绝类爆炸！
    /// </summary>
    public class DelegateTickStylePolicy<TDomain> : ITickStylePolicy<TDomain>
    {
        private readonly Func<TDomain, string> _formatter;
        private readonly Func<TDomain, IHevoBrush?>? _brushSelector;
        private readonly Func<TDomain, bool>? _baseLineSelector;

        public DelegateTickStylePolicy(
            Func<TDomain, string> formatter,
            Func<TDomain, IHevoBrush?>? brushSelector = null,
            Func<TDomain, bool>? baseLineSelector = null)
        {
            _formatter = formatter;
            _brushSelector = brushSelector;
            _baseLineSelector = baseLineSelector;
        }

        public string FormatLabel(TDomain value) => _formatter(value);
        public IHevoBrush? GetOverrideBrush(TDomain value) => _brushSelector?.Invoke(value);
        public bool IsBaseLine(TDomain value) => _baseLineSelector?.Invoke(value) ?? false;
    }
}
