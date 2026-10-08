using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Hevo.Charting.Benchmarks
{
    /// <summary>
    /// render-probe --alloc-types:进程内订阅运行时的 GCAllocationTick 事件(每分配约 100KB 采样一次,
    /// 记下触发采样的那个对象的类型和 SOH / LOH),按类型汇总,定位"分配/帧"里大头是谁。
    /// 只统计 UI 线程(帧本身和脚本输入都在 UI 线程上)。采样是统计意义的:字节数按"采样间隔"累加,
    /// 小类型可能漏采,LOH 对象几乎每个都会触发采样。
    /// </summary>
    internal sealed class AllocTypeSampler : EventListener
    {
        private const int GCAllocationTickId = 10;
        private const EventKeywords GcKeyword = (EventKeywords)0x1;

        private EventSource? _runtime;
        private readonly object _lock = new();
        private readonly Dictionary<(string Type, bool Large), Bucket> _buckets = new();
        private int _uiThreadId;
        private bool _active;

        private sealed class Bucket
        {
            public long Ticks;
            public long SampledBytes;
            public long MaxObjectSize;
        }

        public sealed record Row(string Type, bool Large, long Ticks, long SampledBytes, long MaxObjectSize);

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime") _runtime = source;
        }

        /// <summary>在 UI 线程调用:开始统计当前线程的分配采样。</summary>
        public void Begin()
        {
            lock (_lock)
            {
                _buckets.Clear();
                _uiThreadId = GetCurrentThreadId();
                _active = true;
            }
            if (_runtime != null) EnableEvents(_runtime, EventLevel.Verbose, GcKeyword);
        }

        /// <summary>停止统计并返回按采样字节数降序的汇总。调用前先泵一下消息,让 EventPipe 把缓冲里的事件派发完。</summary>
        public List<Row> End()
        {
            if (_runtime != null) DisableEvents(_runtime);
            lock (_lock)
            {
                _active = false;
                return _buckets
                    .Select(kv => new Row(kv.Key.Type, kv.Key.Large, kv.Value.Ticks, kv.Value.SampledBytes, kv.Value.MaxObjectSize))
                    .OrderByDescending(r => r.SampledBytes)
                    .ToList();
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventId != GCAllocationTickId || e.Payload == null || e.PayloadNames == null) return;
            if (e.OSThreadId != _uiThreadId) return;

            long amount = 0, objectSize = 0;
            int kind = 0;
            string type = "?";
            for (int i = 0; i < e.PayloadNames.Count; i++)
            {
                switch (e.PayloadNames[i])
                {
                    case "AllocationAmount64": amount = Convert.ToInt64(e.Payload[i], CultureInfo.InvariantCulture); break;
                    case "AllocationKind": kind = Convert.ToInt32(e.Payload[i], CultureInfo.InvariantCulture); break;
                    case "TypeName": type = e.Payload[i] as string ?? "?"; break;
                    case "ObjectSize": objectSize = Convert.ToInt64(e.Payload[i], CultureInfo.InvariantCulture); break;
                }
            }

            lock (_lock)
            {
                if (!_active) return;
                var key = (type, kind == 1);
                if (!_buckets.TryGetValue(key, out var b)) _buckets[key] = b = new Bucket();
                b.Ticks++;
                b.SampledBytes += amount;
                if (objectSize > b.MaxObjectSize) b.MaxObjectSize = objectSize;
            }
        }

        public static string Format(string title, List<Row> rows, int frames, int top = 10)
        {
            var sb = new StringBuilder();
            long total = rows.Sum(r => r.SampledBytes);
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"#### {title}(采样合计 {total / 1024.0 / Math.Max(1, frames):F1} KB/帧,{frames} 帧)"));
            sb.AppendLine();
            sb.AppendLine("| 类型 | 堆 | 采样次数 | 采样字节/帧(KB) | 占比 | 最大单对象(KB) |");
            sb.AppendLine("|---|---|---:|---:|---:|---:|");
            foreach (var r in rows.Take(top))
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| `{r.Type}` | {(r.Large ? "LOH" : "SOH")} | {r.Ticks} | {r.SampledBytes / 1024.0 / Math.Max(1, frames):F1} | " +
                    $"{(total > 0 ? r.SampledBytes * 100.0 / total : 0):F1}% | {r.MaxObjectSize / 1024.0:F1} |"));
            }
            return sb.ToString();
        }
    }
}
