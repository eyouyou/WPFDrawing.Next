namespace Hevo.Charting.Buildin
{
    internal static class NiceStepHelper
    {
        // d3 / Highcharts 风格的 nice number 算法收口，避免多策略漂移
        public static double NiceStep(double rough)
        {
            if (rough <= 0) return 0;
            double exp = System.Math.Floor(System.Math.Log10(rough));
            double mag = System.Math.Pow(10, exp);
            double frac = rough / mag;
            // 阈值跟 master 上 TradingViewYAxisMathStrategy 对齐（< 1.5 / < 3 / < 7），
            // 避免边界 roughStep 整体跳到更大一档导致刻度变稀。
            double niceFrac = frac < 1.5 ? 1.0 : frac < 3.0 ? 2.0 : frac < 7.0 ? 5.0 : 10.0;
            return niceFrac * mag;
        }
    }
}
