using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Features
{
    // 💥 框架级大一统极值策略枚举 (彻底脱离业务语境)
    // 凡是涉及到 Y 轴极值推演的数学模型，必须在这里收口，严禁在外围散装 new 各种 ScaleFeature！
    public enum AutoScaleStrategy
    {
        /// <summary> 常规缩放，紧贴数据极值 + Padding </summary>
        Normal,
        /// <summary> 强制包含 0 轴 (多用于普通柱状图) </summary>
        IncludeZero,
        /// <summary> 围绕 0 轴绝对对称 (多用于 MACD、差值、同比等需要正负对称的场景) </summary>
        SymmetricZero,
        /// <summary> 围绕特定参考值绝对对称 (需传入 ReferencePort，多用于分时图围绕昨收价) </summary>
        SymmetricReference,
        /// <summary> 财务悬浮基准线 (数据远离 0 轴时，将基准点悬浮于极值一半的位置，并留出空隙) </summary>
        ZeroWithSpace
    }

    public static partial class AutoScaleFeatureExtensions
    {
        // 💥 高内聚语法糖：将极值大一统特征挂载到 Environment 域中
        // Viewport 由 ChartFeature.InternalCompose 从 ViewportManagerFeature.Ports 自动注入（L6 / §B.2.6），外部不再显式传 vp。
        public static EnvironmentBuilder SetupAutoScale(
            this EnvironmentBuilder builder,
            DataPort<RealRange> yRangePort,
            DataPort<ReadOnlyMemory<double>>[] valuePorts,
            double paddingRatio = 0.05,
            AutoScaleStrategy strategy = AutoScaleStrategy.Normal,
            DataPort<double>? referencePort = null,
            DataPort<double>? baseLinePort = null, // 💥 架构红线：保持 DoubleRange 纯洁性，基准线必须走独立引脚
            DataPort<RealRange>? rawExtremaPort = null) // 视口内未经扩展的真实 high/low，给 AnchoredTick 使用
        {
            builder.Canvas.Add(new UniversalAutoScaleFeature
            {
                ValuePorts = valuePorts,
                YRangePort = yRangePort,
                PaddingRatio = paddingRatio,
                Strategy = strategy,
                ReferencePort = referencePort,
                BaseLinePort = baseLinePort,
                RawExtremaPort = rawExtremaPort
            });
            return builder;
        }
    }

    /// <summary>
    /// 💥 宇宙级极值算子：基于视口 (Viewport) 动态切片计算极值，完美支持全场拖拽缩放。
    /// <para>
    /// <b>典型用法</b>:用 <see cref="AutoScaleFeatureExtensions.SetupAutoScale"/> DSL 装配,例如分时图围绕昨收价对称:
    /// <code>
    /// env.SetupAutoScale(
    ///     yRangePort: ports.PriceRange,
    ///     valuePorts: new[] { ports.Price, ports.Avg },
    ///     strategy:   AutoScaleStrategy.SymmetricReference,
    ///     referencePort: ports.PrevClose);
    /// </code>
    /// </para>
    /// <para>
    /// <b>数据流</b>:Viewport.ActiveRange + ValuePorts 任一变化 → Watch 在写入链同步算 min/max → 按 Strategy 修剪 → 写回 YRangePort / BaseLinePort / RawExtremaPort。
    /// </para>
    /// </summary>
    // 📌 历史 race 修复记录（2026-04-23）：
    //   TimeShareSchema 首帧 / resize 首帧偶发线画出 plotArea 外。
    //   根因：WatchAsync 的 YRangePort 计算跑在线程池，UI 首帧渲染没抢在其后，
    //   Line/Axis 读到默认/旧 YRange → 下一帧才由 NotifyPort 修正。
    //   修复（方案 A）：WatchAsync → Watch，沿数据写入链同步计算，UI tick 开始前 YRangePort 必已就位。
    //   计算为纯 CPU O(N) min/max（亚毫秒），不阻塞 UI。
    public class UniversalAutoScaleFeature : ChartFeature
    {
        // 💥 铁律：执行阶段必须在 ScalePre(50)，确保在真实投影 (Project) 发生前算好极值
        public override FeaturePhase Phase => (FeaturePhase)50;

        /// <summary>参与极值统计的所有数据列(K 线 OHLC、副指标等同口径堆叠)。视口内逐列扫 min/max。</summary>
        public DataPort<ReadOnlyMemory<double>>[] ValuePorts { get; init; } = null!;

        /// <summary>计算结果输出端口(写回 RealRange)。下游 Axis / Series 共用同一根。</summary>
        [PortDirection(PortDirection.Output)]
        public DataPort<RealRange> YRangePort { get; init; } = null!;

        /// <summary>对称参考值端口。仅 <see cref="AutoScaleStrategy.SymmetricReference"/> 用 —— 分时图传昨收价。</summary>
        public DataPort<double>? ReferencePort { get; init; }

        /// <summary>💥 基准线输出端口(0 / 昨收价 / 半轴悬浮等)。柱状图等"从基线向上/下画"的图层消费。架构红线:必须独立端口,不可塞回 RealRange 破坏其纯度。</summary>
        [PortDirection(PortDirection.Output)]
        public DataPort<double>? BaseLinePort { get; init; }

        /// <summary>视口内未经对称/padding 扩展的真实 high/low 输出端口。供 AnchoredNiceTick 把当日极值标在 Y 轴上。</summary>
        [PortDirection(PortDirection.Output)]
        public DataPort<RealRange>? RawExtremaPort { get; init; }

        /// <summary>极值上下扩展比例(默认 5%),防止数据贴死画板上下边缘。</summary>
        public double PaddingRatio { get; init; } = 0.05;

        /// <summary>极值数学策略:Normal / IncludeZero / SymmetricZero / SymmetricReference / ZeroWithSpace。</summary>
        public AutoScaleStrategy Strategy { get; init; } = AutoScaleStrategy.Normal;

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            // 响应式拦截：视口跨度或底层数据变化时才唤醒极值计算
            var listenPorts = new List<object> { Viewport.ActiveRange };
            listenPorts.AddRange(ValuePorts);
            if (ReferencePort != null) listenPorts.Add(ReferencePort);

            // 方案 A（首帧残影修复）：WatchAsync → Watch，沿数据写入链同步完成 Y 量程计算，
            // 避免 UI 首帧抢在线程池 compute 之前读到默认/旧的 YRangePort。
            // 计算为纯 CPU min/max 扫描（240~4000 op 数量级），亚毫秒级，无 I/O，UI 不会被阻塞。
            flow.Watch(listenPorts.ToArray(), board =>
            {
                using (board.AcquireUpgradeableReadLock())
                {
                    var xRange = board.Read(Viewport.ActiveRange);

                    // 💥 0-GC 短路拦截：视口无效直接退帧
                    if (!xRange.IsValid) return;

                    double globalMax = double.MinValue;
                    double globalMin = double.MaxValue;
                    bool hasValidData = false;

                    // 删 Slicer 后世界索引 == 数组下标，直接 clip 到 ActiveRange
                    int localStart = (int)Math.Max(0, Math.Floor(xRange.Min));
                    int localEndMax = (int)Math.Ceiling(xRange.Max);

                    // 💥 0-GC O(N) 极速视口切片遍历
                    foreach (var valuePort in ValuePorts)
                    {
                        var span = board.Read(valuePort).Span;
                        if (span.Length == 0) continue;

                        int end = Math.Min(span.Length - 1, localEndMax);
                        if (localStart > end) continue;

                        for (int i = localStart; i <= end; i++)
                        {
                            double val = span[i];
                            if (double.IsNaN(val)) continue;

                            if (val > globalMax) globalMax = val;
                            if (val < globalMin) globalMin = val;
                            hasValidData = true;
                        }
                    }

                    if (!hasValidData) return;

                    double finalMin = globalMin;
                    double finalMax = globalMax;
                    double baseLine = double.NaN;
                    double padding = 0;

                    // 💥 核心数学变换：根据大一统枚举策略，对极值进行二次修剪
                    switch (Strategy)
                    {
                        case AutoScaleStrategy.Normal:
                            padding = (finalMax - finalMin) * PaddingRatio;
                            if (padding == 0) padding = finalMax == 0 ? 1 : Math.Abs(finalMax * 0.01);
                            finalMin -= padding;
                            finalMax += padding;
                            break;

                        case AutoScaleStrategy.IncludeZero:
                            if (finalMax < 0) finalMax = 0;
                            if (finalMin > 0) finalMin = 0;
                            padding = (finalMax - finalMin) * PaddingRatio;
                            if (padding == 0) padding = finalMax == 0 ? 1 : Math.Abs(finalMax * 0.01);
                            // 💥 如果原本就是 0 轴，不加 Padding 防止悬空
                            finalMin = finalMin == 0 ? 0 : finalMin - padding;
                            finalMax = finalMax == 0 ? 0 : finalMax + padding;
                            baseLine = 0;
                            break;

                        case AutoScaleStrategy.SymmetricZero:
                            double absMax = Math.Max(Math.Abs(finalMin), Math.Abs(finalMax));
                            padding = absMax * PaddingRatio;
                            if (padding == 0) padding = 1;
                            finalMax = absMax + padding;
                            finalMin = -absMax - padding;
                            baseLine = 0;
                            break;

                        case AutoScaleStrategy.SymmetricReference:
                            if (ReferencePort != null)
                            {
                                double refVal = board.Read(ReferencePort);
                                if (!double.IsNaN(refVal))
                                {
                                    // 💥 分时图核心：计算相对昨收价的最大偏离绝对值
                                    double diff = Math.Max(Math.Abs(finalMin - refVal), Math.Abs(finalMax - refVal));
                                    padding = diff * PaddingRatio;
                                    if (padding == 0) padding = refVal * 0.01;
                                    finalMin = refVal - diff - padding;
                                    finalMax = refVal + diff + padding;
                                    baseLine = refVal;
                                }
                            }
                            break;

                        case AutoScaleStrategy.ZeroWithSpace:
                            // 💥 财务图核心：给正负值留出 1/10 的空隙，并将基准线设在中心
                            double location = Math.Max(Math.Abs(finalMin), Math.Abs(finalMax)) / 10.0;
                            finalMin -= location;
                            finalMax += location;

                            if (globalMin > 0)
                            {
                                baseLine = globalMin / 2.0;
                                finalMin = globalMin / 2.0;
                            }
                            else if (globalMax < 0)
                            {
                                baseLine = globalMax / 2.0;
                                finalMax = globalMax / 2.0;
                            }
                            else
                            {
                                baseLine = 0;
                            }
                            break;
                    }

                    // 💥 原子级写回黑板
                    using (board.AcquireWriteLock())
                    {
                        // 1. 写入绝对物理量程 (DoubleRange 结构体保持纯洁，仅含 Min/Max)
                        board.WriteIfChanged(YRangePort, new RealRange(finalMin, finalMax));

                        // 2. 如果外界传入了 BaseLinePort 且算出了有效基准，单独推入辅助引脚
                        if (BaseLinePort != null && !double.IsNaN(baseLine))
                        {
                            board.WriteIfChanged(BaseLinePort, baseLine);
                        }

                        // 3. 视口内的真实 high/low（未经对称扩展），供 AnchoredTick 消费
                        if (RawExtremaPort != null)
                        {
                            board.WriteIfChanged(RawExtremaPort, new RealRange(globalMin, globalMax));
                        }
                    }
                }
            });

        }
        protected override void OnProject(FeatureContext ctx) { }
    }
}
