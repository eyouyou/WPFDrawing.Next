namespace Hevo.Charting.Abstractions
{
    /// <summary>
    /// 框架内浮点比较的统一容差常量。
    /// <para>
    /// 历史:这些值原先散落在 6+ 个 TickStrategy / Interaction / TickProvider 文件里,
    /// 同语义却用了 1e-9 / 1e-6 / 1e-5 不同字面量,review 时容易看走眼。统一收口避免后续漂移。
    /// </para>
    /// <para>
    /// <b>设计原则</b>:浮点判等(无论被减数是 0、anchor、还是 before.Min)是同一件事 —
    /// 因为浮点累加误差通常远小于 1e-12,而真不等时差值至少是 step / 像素量级(≥ 1e-3),
    /// 用 1e-9 既不会误判相等、也不会漏判不等。统一一个常量即可。
    /// 而 <see cref="ManipulationScale"/> 是另一码事 — 过滤 WPF 触屏物理抖动,不是浮点精度问题,独立保留。
    /// </para>
    /// </summary>
    public static class MathTolerance
    {
        /// <summary>
        /// 浮点相等判定容差(1e-9)。所有"a ≈ b"判断统一用这个,无论 a/b 是 0、anchor、还是 RealRange 端点。
        /// 典型用法:Tick 累加 step 时上界比较 / "-0.0000000001" 修正 / RealRange 撞墙判定 / 视口未变退帧。
        /// </summary>
        public const double NumericEqual = 1e-9;

        /// <summary>
        /// WPF Manipulation Scale.X 偏离 1.0 才视为缩放手势的阈值(1e-4)。
        /// 平移期间 Scale 经常 0.9999~1.0001 抖动,过紧会把平移误判成微缩放。
        /// 跟浮点精度无关,纯粹是 UI 物理抖动过滤。
        /// </summary>
        public const double ManipulationScale = 1e-4;
    }
}
