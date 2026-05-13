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

        // 暴露给 TopologyInspectorControl 用的 wrapper —— 同 ExtractPorts 但 public。
        // 直接把私有方法改 public 怕回归测试有别处依赖私有签名,所以新加 public 方法委托过去。
        public static List<IDataPort> ExtractPortsPublic(object target) => ExtractPorts(target);

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

        // ─── port id → owner feature id ───
        // 静态扫描期(TopologyScanner.Scan)记录"这根 port 是哪个 feature 声明的",
        // BuildPortDisplayMap 拿 writer 为空时回退到 owner,给孤儿 port(declared 但没被读写过的)
        // 添加 "Owner › PortName" 前缀,user 不必猜端口属于谁。
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> PortOwner = new();

        // ConcurrentDictionary.AddOrUpdate 的工厂闭包做成 static field,跨调用复用。
        // 没标 static 的 lambda 即使没捕获也可能每次 new(看编译器 codegen),显式 static 锁死。
        private static readonly Func<(string, string), int, int> s_incrementFactory = static (_, count) => count + 1;

        // ─── 关键事件时间线(永久层)───────────────────────────────────────────
        // 用户右键打开 inspector 是滞后行为,他想看的是"从 schema compose 到现在到底发生了啥"。
        // LinkHits / LastHitTicks 只能告诉他 "现在累计 1500 次命中、最近 15ms 前命中过",
        // 说不出 "T+5s feature 成功 composed,T+8s Latest 第一次写,T+22s 之后 Latest 就没再写过" 这种时序。
        // 所以单独再开一个 ring buffer 记关键事件,跟高频 Read/Write 命中分开装,后者将来另起 Activity 层。

        public enum EventKind
        {
            ComposeStart,         // schema BuildAndActivatePipeline 开始
            FeatureComposed,      // 单个 feature 成功 InternalCompose 后
            FeatureShortCircuit,  // 单个 feature OnCompose 内提前 return(委托缺 / spec 缺等,框架记 detect 点)
            ComposeEnd,           // 全部 feature compose 完成
            FirstPortWrite,       // 端口第一次被写入(下游可见数据的瞬间;后续重复 write 进 Activity 层不刷这)
            HandlerMissing,       // 蓝图引用了但 registry 未注册的 handler 名(throw 之前埋,inspector 仍可见)
            DataSourcePublish,    // DataSource.Stream 推一次 snapshot(LoadAsync 完成 / 心跳 RefreshAsync 等的下游入口)
        }

        public readonly struct TimelineEvent
        {
            /// <summary>相对 Tracer 创建那一刻的 Stopwatch ticks 偏移,UI 渲染时 / Frequency = 秒。</summary>
            public readonly long TicksFromStart;
            public readonly EventKind Kind;
            public readonly string TargetId;
            public readonly string? Summary;
            public TimelineEvent(long ticks, EventKind kind, string target, string? summary)
            {
                TicksFromStart = ticks; Kind = kind; TargetId = target; Summary = summary;
            }
        }

        private readonly long _startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        public long StartTicks => _startTicks;
        public long ElapsedTicks => System.Diagnostics.Stopwatch.GetTimestamp() - _startTicks;

        private const int KEY_EVENTS_CAP = 500;
        public readonly System.Collections.Concurrent.ConcurrentQueue<TimelineEvent> KeyEvents = new();

        // 跟踪每根 port 是否已经记过 FirstPortWrite —— 后续 RecordWrite 重复触发不再进 KeyEvents。
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _seenPortFirstWrite = new();

        // ─── 高频活动层 ─── (per-port write/read 计数 + bucketed 时间序列)
        // 每根 port 一个 cumulative 计数,RecordWrite/Read 路径上 ConcurrentDictionary.AddOrUpdate 一次,
        // 实测 sub-100ns,DEBUG 路径承受得住 60Hz × 100 port。Inspector 每帧调 SampleActivity 把
        // 累计差量塞进 ring buffer,渲染时按时间轴铺开。
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _portWriteCount = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _portReadCount  = new();

        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, ActivityRing> PortActivity = new();

        public sealed class ActivityRing
        {
            // 240 桶 × 500ms = 120 秒窗口。开 inspector 晚于此就丢老桶 ——
            // 用户看不到 2 分钟之前的活动,但稳态调试够用。需要更长再调 CAP 或 BucketMs。
            public const int CAP = 240;
            public const int BucketMs = 500;

            // 每桶记 (相对 schema start 的 ticks, 该桶 500ms 内 写入次数, 读取次数)。
            // ring buffer:Head 指向下一个待写位置,Count 累计有效条数(到 CAP 后恒等于 CAP)。
            public readonly long[] BucketTicks = new long[CAP];
            public readonly int[] WriteDelta   = new int[CAP];
            public readonly int[] ReadDelta    = new int[CAP];
            public int Head;
            public int Count;

            public int LastTotalWrites;
            public int LastTotalReads;

            // 用 Stopwatch ticks 计数,跟 _startTicks 同源。
            public long LastSampleTicks;
        }

        // SampleActivity 内部限流:多次调用同一帧只生成一条桶。
        private long _lastActivitySampleTicks = 0;
        private static readonly long s_bucketTicks =
            ActivityRing.BucketMs * System.Diagnostics.Stopwatch.Frequency / 1000;

        /// <summary>
        /// Inspector 每帧调一次。内部按 BucketMs 限流 ——
        /// 距离上次采样不到 500ms 直接 no-op,避免重复 enqueue 同一桶。
        /// 60Hz × 这一段也就 ~微秒级 dictionary scan,纯 DEBUG 路径不在乎。
        /// </summary>
        public void SampleActivity()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _lastActivitySampleTicks < s_bucketTicks) return;
            _lastActivitySampleTicks = now;
            long elapsed = now - _startTicks;

            // 把所有出现过的 port id 取并集,确保每根都生成桶 —— 即使本桶 read/write delta 都是 0。
            // 桶位 BucketTicks 必须每帧都更新,渲染时才能区分"安静的桶"和"丢失的桶"。
            var seen = new HashSet<string>();
            foreach (var k in _portWriteCount.Keys) seen.Add(k);
            foreach (var k in _portReadCount.Keys)  seen.Add(k);

            foreach (var pid in seen)
            {
                var ring = PortActivity.GetOrAdd(pid, static _ => new ActivityRing());
                int wcur = _portWriteCount.GetValueOrDefault(pid);
                int rcur = _portReadCount.GetValueOrDefault(pid);

                ring.WriteDelta[ring.Head]  = wcur - ring.LastTotalWrites;
                ring.ReadDelta[ring.Head]   = rcur - ring.LastTotalReads;
                ring.BucketTicks[ring.Head] = elapsed;

                ring.LastTotalWrites = wcur;
                ring.LastTotalReads  = rcur;
                ring.LastSampleTicks = now;

                ring.Head = (ring.Head + 1) % ActivityRing.CAP;
                if (ring.Count < ActivityRing.CAP) ring.Count++;
            }
        }

        /// <summary>
        /// 记一条关键事件(永不 evict,直到 cap 才 dequeue 最旧)。线程安全,埋点零分配除入队本身。
        /// </summary>
        public void RecordKeyEvent(EventKind kind, string targetId, string? summary = null)
        {
            var ticks = System.Diagnostics.Stopwatch.GetTimestamp() - _startTicks;
            KeyEvents.Enqueue(new TimelineEvent(ticks, kind, targetId, summary));
            // ConcurrentQueue 没有 cap,手动 trim;允许临时超过 cap 几条(并发场景),不严格。
            while (KeyEvents.Count > KEY_EVENTS_CAP) KeyEvents.TryDequeue(out _);
        }

        /// <summary>读出当前所有关键事件副本(按入队顺序,即时间序)。Inspector 渲染前调一次。</summary>
        public TimelineEvent[] DumpKeyEvents() => KeyEvents.ToArray();

        /// <summary>事件类别 → 简短标签(UI 时间线列表用)。</summary>
        public static string EventKindShort(EventKind k) => k switch
        {
            EventKind.ComposeStart        => "Compose 开始",
            EventKind.ComposeEnd          => "Compose 完成",
            EventKind.FeatureComposed     => "Feature 接入",
            EventKind.FeatureShortCircuit => "Feature 短路",
            EventKind.FirstPortWrite      => "首次写入",
            EventKind.HandlerMissing      => "Handler 缺失",
            EventKind.DataSourcePublish   => "数据源 Publish",
            _                             => k.ToString(),
        };

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

            // 高频 port 累计计数 —— SampleActivity 取差量打桶。
            _portReadCount.AddOrUpdate(fullId, 1, static (_, c) => c + 1);

            // 端口被读 → 端口节点亮一下;feature 也算"参与命中"。
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            LastHitTicks[fullId] = now;
            LastHitTicks[fId] = now;
        }

        // port id → (displayName, T 类型名, 最近一次写入的 value)。RecordWriteWithValue 路径上更新。
        // 缓存这份让 Inspector dump 时跟 board 生命周期解耦 —— 蓝图常见 one-shot 数据源 push 完
        // 订阅链 disposed → boardStream 触发 _pipe.Dispose → board.Dispose,但 schema 不会自己把 _latestBoard
        // 清成 null,Inspector 从 board 读到的是悬挂 disposed 引用。改从 tracer 这边读最后已知值,绕开 lifecycle 坑。
        public readonly ConcurrentDictionary<string, (string DisplayName, string TypeName, object? Value)> LastValues = new();

        /// <summary>
        /// RecordWrite 的强类型重载:除了原拓扑/计数逻辑,还把 value 缓存进 LastValues。
        /// DEBUG ForceWrite 路径调,box value(值类型)在 DEBUG 路径可接受。
        /// </summary>
        public void RecordWriteWithValue<T>(DataPort<T> port, T value)
        {
            if (port == null) return;
            RecordWrite(port);
            LastValues[port.Id] = (port.DisplayName, typeof(T).Name, value);
        }

        public void RecordWrite(object port)
        {
            var caller = _currentCaller.Value;
            if (port == null) return;

            ResolvePortIds(port, out var fullId, out var displayName);
            RegisterNode(fullId, displayName, 1);

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            LastHitTicks[fullId] = now;

            // 高频 port 累计计数 —— SampleActivity 取差量打桶。
            _portWriteCount.AddOrUpdate(fullId, 1, static (_, c) => c + 1);

            // 端口第一次写 → 关键事件层留一笔。后续重复 write 走原 LinkHits / LastHitTicks 路径,
            // 不再进 KeyEvents 避免被 60Hz 写入刷爆。
            if (_seenPortFirstWrite.TryAdd(fullId, 0))
            {
                RecordKeyEvent(EventKind.FirstPortWrite, fullId, displayName);
            }

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
