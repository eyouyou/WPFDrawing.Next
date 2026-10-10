using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hevo.Charting;
using Hevo.Charting.LowCode;

namespace Hevo.Drawing.LowCodeDemo
{
    [PortGroup("SinWavePorts")]
    public readonly struct SinWavePoint
    {
        [Port] public int Index { get; init; }
        // X 轴用的"时间"轴 —— 跟 Index 一一对应,double 类型方便接 Crosshair/Tooltip 的
        // DataPort<ReadOnlyMemory<double>> XAxisDataPort(int 类型不匹配会被蓝图侧 RegisterPortType 拒)。
        [Port] public double Time { get; init; }
        [Port] public double Value { get; init; }
    }

    /// <summary>
    /// 纯合成数据源：用正弦波填充，无任何网络/登录依赖。
    /// 上下文字符串仅用于触发加载，内容不影响数据。
    /// </summary>
    public class SinWaveDataSource : ReactiveDataSource<SinWaveDataSource, string, SinWavePoint>
    {
        public const int Points = 200;
        public override int LogicalLength => Points;

        protected override Task<int> OnFetchAsync(string? context, CancellationToken token)
        {
            var items = new List<SinWavePoint>(Points);
            for (int i = 0; i < Points; i++)
                items.Add(new SinWavePoint
                {
                    Index = i,
                    Time = i,
                    Value = Math.Sin(i * 2 * Math.PI / Points) * 100 + 200,
                });
            UpdateBuffer(buf => { buf.Clear(); buf.AddRange(items); });
            return Task.FromResult(Points);
        }
    }
}
