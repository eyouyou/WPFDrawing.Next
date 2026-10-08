using System.Collections.Generic;
using Hevo.Charting.Abstractions;

namespace Hevo.Charting.DevTools
{
    // 增量渲染对照开关 + 计数器,只给测量工具(Hevo.Charting.Benchmarks --render-probe)和单测用。
    //
    // 默认全关:关着时 ProjectAll / SubmitSync 的行为跟没有本类时逐行一致,只多一次静态字段读。
    // 打开某个开关 = 人为关掉对应那一层增量机制,作为前后对比的对照组:
    //   ForceFullPass         → Feature 侧失效:每帧都当作环境纪元变化,所有 Feature 重投影,UsePort 全部视为变脏
    //   BypassBagShortCircuit → 只去掉 RenderContext.SubmitSync 的 Bag 级短路,图层仍做引用比对(验证短路只是优化)
    //   ForceLayerRedraw      → Layer 侧失效:已发现的图层每帧都标脏重录
    //
    // 计数器在 UI 线程单线程累加,不做 Interlocked;多 ChartCell 并行跑时数值只作参考。
    internal static class IncrementalRenderProbe
    {
        public static bool ForceFullPass;
        public static bool BypassBagShortCircuit;
        public static bool ForceLayerRedraw;

        /// <summary>ProjectAll 实际调用 Feature.Project 的累计次数。</summary>
        public static long FeatureProjections;

        /// <summary>
        /// BypassBagShortCircuit 打开时:本帧 Bag 没有任何提交(短路本会跳过),引用比对却判脏的次数。
        /// 不为 0 说明短路和引用比对结论不一致,<see cref="ShortCircuitMissDetail"/> 记录是哪个图层、哪个 Trait。
        /// </summary>
        public static long ShortCircuitMisses;

        /// <summary>键:"图层名(类型) Trait类型: 上次看到的对象 → 当前对象";值:次数。</summary>
        public static readonly Dictionary<string, long> ShortCircuitMissDetail = new();

        internal static void RecordShortCircuitMiss(ChartLayer layer, VisualDataBag liveLocal, VisualDataBag liveGlobal)
        {
            ShortCircuitMisses++;
            foreach (var kvp in layer.DependencyTracker.TrackedRefs)
            {
                object? current = liveLocal.GetById(kvp.Key) ?? liveGlobal.GetById(kvp.Key);
                if (ReferenceEquals(kvp.Value, current)) continue;

                string trait = (kvp.Value ?? current)?.GetType().Name ?? $"#{kvp.Key}";
                string key = $"{layer.Name}({layer.GetType().Name}) {trait}: " +
                             $"{(kvp.Value == null ? "null" : "旧对象")} → {(current == null ? "null" : "新对象")}";
                ShortCircuitMissDetail.TryGetValue(key, out var n);
                ShortCircuitMissDetail[key] = n + 1;
                return;
            }
        }

        public static void Reset()
        {
            ForceFullPass = false;
            BypassBagShortCircuit = false;
            ForceLayerRedraw = false;
            FeatureProjections = 0;
            ShortCircuitMisses = 0;
            ShortCircuitMissDetail.Clear();
        }
    }
}
