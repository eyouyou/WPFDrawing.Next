using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using System.Diagnostics;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// 💥 万能引脚监听雷达：哪里不懂挂哪里！
    /// </summary>
    public class ConsoleDebuggerFeature : ChartFeature
    {
        // 放到最后执行
        public override FeaturePhase Phase => FeaturePhase.Interaction;

        // 你想监听的那个引脚
        public object TargetPort { get; init; } = null!;
        public string Label { get; init; } = "Debug";

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            // 💥 完美利用我们刚写好的 0-GC 精确制导 Watch！
            // 只要 TargetPort 被写入，并且值真的变了，这里就会触发！
            flow.WatchAsync(new[] { TargetPort }, board =>
            {
                using (board.AcquireReadLock())
                {
                    // 用反射暴力去黑板里捞值（反正是调试专用，不在乎这点性能）
                    dynamic? val = board.GetType().GetMethod("Read")?
                                       .MakeGenericMethod(TargetPort.GetType().GetGenericArguments()[0])
                                       .Invoke(board, new[] { TargetPort });

                    Debug.WriteLine($"[雷达 {Label}] 捕获到变动! 当前值: {val}");
                }
            });

        }

        protected override void OnProject(FeatureContext ctx) { }
    }
}
