using Hevo.Charting.Core;

namespace Hevo.Charting.Buildin
{
    public class ThresholdBrushResolver : IBrushResolver<double>
    {
        private readonly double _threshold;
        private readonly IHevoBrush _above, _below, _equal;
        public IHevoBrush DefaultBrush { get; }

        public ThresholdBrushResolver(double threshold, IHevoBrush above, IHevoBrush below, IHevoBrush equal, IHevoBrush? baseBrush = null)
        {
            _threshold = threshold; _above = above; _below = below; _equal = equal;
            DefaultBrush = baseBrush ?? equal;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public IHevoBrush Resolve(in double value)
        {
            if (double.IsNaN(value)) return _equal;
            return value > _threshold ? _above : (value < _threshold ? _below : _equal);
        }
    }
}
