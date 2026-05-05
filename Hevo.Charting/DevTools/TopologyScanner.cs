using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Hevo.Charting.DevTools
{
    // ==========================================
    // 1. 静态骨架扫描器 (负责在启动时画出基础架构)
    // ==========================================
    public static class TopologyScanner
    {
        // 💥 签名改了：只返回 Nodes，不再返回任何 Links！
        public static List<TopoNode> Scan(ChartCell cell)
        {
            var nodes = new List<TopoNode>();
            var schema = cell.Template as ReactiveSchema;
            if (schema == null) return nodes;

            // 1. 数据管线源头
            nodes.Add(new TopoNode { Id = "PIPE", Label = "DataPipe (源)", Tier = 0 });

            // 2. 扫描 Schema 里的原生引脚
            var schemaPorts = ExtractPorts(schema);
            foreach (var p in schemaPorts)
            {
                if (nodes.All(n => n.Id != p.Id))
                    nodes.Add(new TopoNode { Id = p.Id, Label = p.DisplayName, Tier = 1 });
            }

            // 3. 扫描所有的 Features 以及它们内部偷偷 new 的引脚
            foreach (var f in schema.Features)
            {
                var fId = $"F_{RuntimeHelpers.GetHashCode(f)}";
                var baseName = CleanName(f.GetType().Name);
                // 同类型多实例(LineSeriesFeature × N)在图谱里原本完全无法区分,
                // 拼上 InstanceId 前 4 位让多实例可视化分得清。
                var fName = f is ChartFeature cf && !string.IsNullOrEmpty(cf.InstanceId)
                    ? $"{baseName}#{cf.InstanceId.Substring(0, Math.Min(4, cf.InstanceId.Length))}"
                    : baseName;
                bool isSensor = baseName.Contains("Interaction");

                // 把 Feature 作为节点加进去
                nodes.Add(new TopoNode { Id = fId, Label = fName, Tier = isSensor ? 0 : 2 });

                var fPorts = ExtractPorts(f);
                foreach (var p in fPorts)
                {
                    if (nodes.All(n => n.Id != p.Id))
                        nodes.Add(new TopoNode { Id = p.Id, Label = p.DisplayName, Tier = 1 });
                }
            }

            // 💥 彻底干掉所有猜连线的代码（isOutput, links.Add 等），图上再也不会有假线！
            return nodes;
        }

        private static List<IDataPort> ExtractPorts(object target)
        {
            var res = new List<IDataPort>();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var members = target.GetType().GetFields(flags).Cast<MemberInfo>()
                          .Concat(target.GetType().GetProperties(flags));

            foreach (var m in members)
            {
                object? v = m is FieldInfo fi ? fi.GetValue(target) : ((PropertyInfo)m).GetValue(target);
                if (v is IDataPort dp) res.Add(dp);
                else if (v != null && v.GetType().Name == "ViewportPorts") res.AddRange(ExtractPorts(v));
            }
            return res;
        }

        private static string CleanName(string name) => name.Replace("Feature", "").Split('`')[0];
    }

    // ==========================================
    // 2. 隔空贴符注册表 (黑魔法)
    // ==========================================
    public static class TracerRegistry
    {
#if DEBUG
        private static readonly ConditionalWeakTable<object, TopologyTracer> _attachments = new();
        public static void Attach(object target, TopologyTracer tracer) { if (target != null && tracer != null) _attachments.AddOrUpdate(target, tracer); }
        public static TopologyTracer? Get(object target) { return target != null && _attachments.TryGetValue(target, out var tracer) ? tracer : null; }
#endif
    }

    // ==========================================
    // 3. 动态血液追踪器 (记录真实读写)
    // ==========================================
    public class TopologyTracer
    {
#if DEBUG
        // 💥 运行时专用
        private static readonly AsyncLocal<object?> _currentCaller = new();

        // 💥 新增：装配期(Compose)专用空气！
        public static readonly AsyncLocal<object?> SetupContext = new();

        // 复合键 (源, 目标) 用 ValueTuple<string,string>，避免每次 Record 拼接 "{src}|{tgt}" 字符串。
        // 字符串 hash + equals 转为 tuple 字段级,DEBUG 下 hot 路径(每次 Read/Write)零字符串分配。
        public readonly ConcurrentDictionary<(string Src, string Tgt), int> LinkHits = new();
        public readonly ConcurrentDictionary<string, (string Name, int Tier)> NodeMetadata = new();

        // 节点最近一次被命中(读或写)的 Stopwatch 时间戳,UI 用来做"刚收到数据"短暂脉冲。
        public readonly ConcurrentDictionary<string, long> LastHitTicks = new();

        // 每个 Feature 节点的累积 OnProject 成本(Stopwatch ticks)与样本数,UI 算 EMA 平均渲染耗时。
        // 由 ChartFeature.Project 在 DEBUG 路径喂入,纯统计用,不参与决策。
        public readonly ConcurrentDictionary<string, (long TotalTicks, int Samples)> FeatureCost = new();

        public const string PIPE_ID = "PIPE";

        // ConcurrentDictionary.AddOrUpdate 的工厂闭包做成 static field,跨调用复用。
        // 没标 static 的 lambda 即使没捕获也可能每次 new(看编译器 codegen),显式 static 锁死。
        private static readonly Func<(string, string), int, int> s_incrementFactory = static (_, count) => count + 1;

        public TopologyTracer() { NodeMetadata[PIPE_ID] = ("DataPipe (数据源)", 0); }

        // 运行时进入作用域 — 返回 readonly struct,using 直接走 duck-typed Dispose,零堆分配。
        public static ScopeToken EnterScope(object? caller)
        {
            var prev = _currentCaller.Value;
            _currentCaller.Value = caller;
            return new ScopeToken(prev);
        }

        // 💥 新增：装配期播撒气味
        public static SetupScopeToken EnterSetupScope(object caller)
        {
            var prev = SetupContext.Value;
            SetupContext.Value = caller;
            return new SetupScopeToken(prev);
        }

        public void RecordRead(object port)
        {
            var caller = _currentCaller.Value;
            if (caller == null || port == null) return;

            GetCallerInfo(caller, out string fId, out string fName, out int fTier);

            // port.DisplayName 已经是要展示的纯名,跳过 Split('_')/Contains('_') 的字符串扫描和数组分配。
            ResolvePortIds(port, out var fullId, out var displayName);

            RegisterNode(fullId, displayName, 1);
            LinkHits.AddOrUpdate((fullId, fId), 1, s_incrementFactory);

            // 端口被读 → 端口节点亮一下;feature 也算"参与命中"。
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            LastHitTicks[fullId] = now;
            LastHitTicks[fId] = now;
        }

        public void RecordWrite(object port)
        {
            var caller = _currentCaller.Value;
            if (port == null) return;

            ResolvePortIds(port, out var fullId, out var displayName);
            RegisterNode(fullId, displayName, 1);

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            LastHitTicks[fullId] = now;

            if (caller != null)
            {
                GetCallerInfo(caller, out string fId, out string fName, out int fTier);
                RegisterNode(fId, fName, fTier);
                LinkHits.AddOrUpdate((fId, fullId), 1, s_incrementFactory);
                LastHitTicks[fId] = now;
            }
            else
            {
                LinkHits.AddOrUpdate((PIPE_ID, fullId), 1, s_incrementFactory);
                LastHitTicks[PIPE_ID] = now;
            }
        }

        /// <summary>
        /// 记录某个 ChartFeature 一帧 OnProject 的耗时(Stopwatch ticks)。
        /// 由 ChartFeature.Project 在 DEBUG 路径调用,UI 用 EMA 平均生成 "x.x ms" 标签。
        /// </summary>
        public void RecordFeatureCost(object feature, long stopwatchTicks)
        {
            if (feature == null) return;
            var fId = $"F_{RuntimeHelpers.GetHashCode(feature)}";
            FeatureCost.AddOrUpdate(fId,
                (stopwatchTicks, 1),
                (_, prev) => (prev.TotalTicks + stopwatchTicks, prev.Samples + 1));
        }

        /// <summary>
        /// 把当前拓扑(节点 + 连线 + 命中数 + 平均耗时)序列化成 JSON,便于离线分析、diff 或贴 issue。
        /// 故意手写而不引第三方序列化库 — DEBUG 路径不该新增运行时依赖。
        /// </summary>
        public string DumpJson()
        {
            var sb = new System.Text.StringBuilder(1024);
            sb.Append("{\n  \"nodes\": [");
            bool first = true;
            foreach (var kv in NodeMetadata)
            {
                if (!first) sb.Append(',');
                first = false;
                long ms = 0;
                if (FeatureCost.TryGetValue(kv.Key, out var cost) && cost.Samples > 0)
                {
                    double seconds = (cost.TotalTicks / (double)cost.Samples) / System.Diagnostics.Stopwatch.Frequency;
                    ms = (long)(seconds * 1_000_000); // microseconds
                }
                sb.Append("\n    { \"id\": ").Append(JsonString(kv.Key))
                  .Append(", \"label\": ").Append(JsonString(kv.Value.Name))
                  .Append(", \"tier\": ").Append(kv.Value.Tier)
                  .Append(", \"avg_us\": ").Append(ms)
                  .Append(" }");
            }
            sb.Append("\n  ],\n  \"links\": [");
            first = true;
            foreach (var kv in LinkHits)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("\n    { \"src\": ").Append(JsonString(kv.Key.Src))
                  .Append(", \"tgt\": ").Append(JsonString(kv.Key.Tgt))
                  .Append(", \"hits\": ").Append(kv.Value)
                  .Append(" }");
            }
            sb.Append("\n  ]\n}");
            return sb.ToString();
        }

        private static string JsonString(string s)
        {
            // 极简 JSON 转义:够覆盖 port id / feature 名,不打算支持任意字符。
            var sb = new System.Text.StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append(' ');
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        public void RecordSubscribe(object port, object feature)
        {
            using var scope = EnterScope(feature);
            RecordRead(port);
        }

        // 💥 新增重载：支持字符串直接订阅追踪
        public void RecordSubscribe(object port, string featureName)
        {
            using var scope = EnterScope(featureName);
            RecordRead(port);
        }

        // 直接吃 IDataPort.Id / IDataPort.DisplayName,IDataPort 实现里这俩字段都是构造时一次性算好的字符串。
        // 非 IDataPort 的 fallback 仍走 hashcode 拼接,但这条路径在框架内基本不会触发。
        private static void ResolvePortIds(object port, out string fullId, out string displayName)
        {
            if (port is IDataPort dp)
            {
                fullId = dp.Id;
                displayName = dp.DisplayName;
            }
            else
            {
                fullId = $"P_{RuntimeHelpers.GetHashCode(port)}";
                displayName = fullId;
            }
        }

        // 💥 智能提取器：判断装在 _currentCaller 里的是字符串还是真对象
        private static void GetCallerInfo(object caller, out string id, out string name, out int tier)
        {
            if (caller is string s)
            {
                // 💥 修复 DataPipe 的 ID 识别！
                if (s == PIPE_ID)
                {
                    id = PIPE_ID; name = "DataPipe (数据源)"; tier = 0;
                    return;
                }
                name = s.Replace("Feature", "");
                id = $"F_{name}";
                tier = name.Contains("Interaction") ? 0 : 2;
            }
            else
            {
                // 💥 真对象的 HashCode 生成
                id = $"F_{RuntimeHelpers.GetHashCode(caller)}";
                var baseName = caller.GetType().Name.Replace("Feature", "").Split('`')[0];
                // 跟 TopologyScanner 保持一致:同类型多实例的 label 拼上 InstanceId 前 4 位。
                if (caller is Core.ChartFeature cf && !string.IsNullOrEmpty(cf.InstanceId))
                {
                    name = $"{baseName}#{cf.InstanceId.Substring(0, Math.Min(4, cf.InstanceId.Length))}";
                }
                else
                {
                    name = baseName;
                }
                tier = baseName.Contains("Interaction") ? 0 : 2;
            }
        }

        private void RegisterNode(string id, string name, int tier)
        {
            if (!NodeMetadata.ContainsKey(id))
            {
                NodeMetadata.TryAdd(id, (name, tier));
            }
        }

        public (List<TopoNode> Nodes, List<TopoLink> Links) DumpTopology()
        {
            var nodes = new List<TopoNode>();
            var links = new List<TopoLink>();
            var activeNodeIds = new HashSet<string>();

            foreach (var kvp in LinkHits)
            {
                var (src, tgt) = kvp.Key;
                activeNodeIds.Add(src);
                activeNodeIds.Add(tgt);
                links.Add(new TopoLink { Src = src, Tgt = tgt, HitCount = kvp.Value });
            }

            foreach (var id in activeNodeIds)
            {
                if (NodeMetadata.TryGetValue(id, out var meta))
                    nodes.Add(new TopoNode { Id = id, Label = meta.Name, Tier = meta.Tier });
            }

            return (nodes, links);
        }

        // readonly struct + duck-typed Dispose,using 走 constrained call,栈上初始化无堆分配。
        // 替代旧 `class DisposeAction + Action 闭包`(每次 EnterScope 两个对象)。
        public readonly struct ScopeToken : IDisposable
        {
            private readonly object? _prev;
            internal ScopeToken(object? prev) { _prev = prev; }
            public void Dispose() => _currentCaller.Value = _prev;
        }

        public readonly struct SetupScopeToken : IDisposable
        {
            private readonly object? _prev;
            internal SetupScopeToken(object? prev) { _prev = prev; }
            public void Dispose() => SetupContext.Value = _prev;
        }
#endif
    }
}
