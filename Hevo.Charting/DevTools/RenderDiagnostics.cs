using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Hevo.Charting.DevTools
{
    // 渲染管线只读快照。所有计数在 UI 线程单独累加，无 Interlocked。
    // 业务通过 ChartCell.GetDiagnostics() 拿取。
    public readonly record struct DiagnosticsSnapshot(
        long FrameCount,
        long LastFrameDrawCmds,
        long TotalDrawCmds,
        long PaintCacheHits,
        long PaintCacheMisses,
        long FormattedTextHits,
        long FormattedTextMisses,
        TimeSpan LastFrameRenderCost,
        double PaintCacheHitRate,
        double FormattedTextHitRate)
    {
        public string ToOverlayText() =>
            $"Frame:{FrameCount}  DrawCmd(last/Σ):{LastFrameDrawCmds}/{TotalDrawCmds}\n" +
            $"Paint hit:{PaintCacheHits}/{PaintCacheHits + PaintCacheMisses} ({PaintCacheHitRate:P1})\n" +
            $"Text hit:{FormattedTextHits}/{FormattedTextHits + FormattedTextMisses} ({FormattedTextHitRate:P1})\n" +
            $"Render cost:{LastFrameRenderCost.TotalMilliseconds:F2}ms";
    }

    // per-ChartCell 计数收集器。
    // 单 UI 线程递增 → 不需要 Interlocked / lock。
    // 数值溢出 64 位的概率 ≈ 0（每秒 60 帧 × 永久运行也只到几百亿）。
    //
    // 帧聚合模型：ChartCell.Invalidate 流程对每个 dirty layer 各调一次 WpfDrawingRenderer.Render，
    // 所以 renderer 通过 OnLayerRender 累加到当前帧；ChartCell 在帧首尾调用 BeginFrame / EndFrame
    // 收口为对外可见的 LastFrame*。
    internal sealed class RenderDiagnostics
    {
        public long FrameCount;
        public long LastFrameDrawCmds;
        public long TotalDrawCmds;
        public long PaintCacheHits;
        public long PaintCacheMisses;
        public long FormattedTextHits;
        public long FormattedTextMisses;

        // Stopwatch 高精度 ticks，Snapshot 时换算 TimeSpan
        public long LastFrameRenderStopwatchTicks;

        // 当前帧累加器（BeginFrame 清零 → renderer 累加 → EndFrame 拍快照）
        private long _frameDrawCmds;
        private long _frameTicks;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnPaintCacheHit() => PaintCacheHits++;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnPaintCacheMiss() => PaintCacheMisses++;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnFormattedTextHit() => FormattedTextHits++;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnFormattedTextMiss() => FormattedTextMisses++;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnLayerRender(long stopwatchTicks, int drawCmdCount)
        {
            _frameTicks += stopwatchTicks;
            _frameDrawCmds += drawCmdCount;
        }

        public void BeginFrame()
        {
            _frameTicks = 0;
            _frameDrawCmds = 0;
        }

        public void EndFrame()
        {
            FrameCount++;
            LastFrameRenderStopwatchTicks = _frameTicks;
            LastFrameDrawCmds = _frameDrawCmds;
            TotalDrawCmds += _frameDrawCmds;
        }

        public DiagnosticsSnapshot Snapshot()
        {
            long paintTotal = PaintCacheHits + PaintCacheMisses;
            long textTotal = FormattedTextHits + FormattedTextMisses;
            // Stopwatch.Frequency 是 ticks/秒；TimeSpan.TicksPerSecond = 10_000_000
            double seconds = LastFrameRenderStopwatchTicks / (double)Stopwatch.Frequency;
            var cost = TimeSpan.FromTicks((long)(seconds * TimeSpan.TicksPerSecond));
            return new DiagnosticsSnapshot(
                FrameCount,
                LastFrameDrawCmds,
                TotalDrawCmds,
                PaintCacheHits,
                PaintCacheMisses,
                FormattedTextHits,
                FormattedTextMisses,
                cost,
                paintTotal > 0 ? PaintCacheHits / (double)paintTotal : 0.0,
                textTotal > 0 ? FormattedTextHits / (double)textTotal : 0.0);
        }
    }
}
