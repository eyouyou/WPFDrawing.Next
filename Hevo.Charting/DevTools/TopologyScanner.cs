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
                var fName = CleanName(f.GetType().Name);
                bool isSensor = fName.Contains("Interaction");

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

        public readonly ConcurrentDictionary<string, int> LinkHits = new();
        public readonly ConcurrentDictionary<string, (string Name, int Tier)> NodeMetadata = new();

        public const string PIPE_ID = "PIPE";

        public TopologyTracer() { NodeMetadata[PIPE_ID] = ("DataPipe (数据源)", 0); }

        // 运行时进入作用域
        public static IDisposable EnterScope(object? caller)
        {
            var prev = _currentCaller.Value;
            _currentCaller.Value = caller;
            return new DisposeAction(() => _currentCaller.Value = prev);
        }

        // 💥 新增：装配期播撒气味
        public static IDisposable EnterSetupScope(object caller)
        {
            var prev = SetupContext.Value;
            SetupContext.Value = caller;
            return new DisposeAction(() => SetupContext.Value = prev);
        }

        public void RecordRead(object port)
        {
            var caller = _currentCaller.Value;
            if (caller == null || port == null) return;

            GetCallerInfo(caller, out string fId, out string fName, out int fTier);

            string fullId = (port as IDataPort)?.Id ?? $"P_{RuntimeHelpers.GetHashCode(port)}";
            string displayName = fullId.Contains('_') ? fullId.Split('_')[0] : fullId;

            RegisterNode(fullId, displayName, 1);
            LinkHits.AddOrUpdate($"{fullId}|{fId}", 1, (_, count) => count + 1);
        }

        public void RecordWrite(object port)
        {
            var caller = _currentCaller.Value;
            if (port == null) return;

            string fullId = (port as IDataPort)?.Id ?? $"P_{RuntimeHelpers.GetHashCode(port)}";
            string displayName = fullId.Contains('_') ? fullId.Split('_')[0] : fullId;
            RegisterNode(fullId, displayName, 1);

            if (caller != null)
            {
                GetCallerInfo(caller, out string fId, out string fName, out int fTier);
                RegisterNode(fId, fName, fTier);
                LinkHits.AddOrUpdate($"{fId}|{fullId}", 1, (_, count) => count + 1);
            }
            else
            {
                LinkHits.AddOrUpdate($"{PIPE_ID}|{fullId}", 1, (_, count) => count + 1);
            }
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
                name = caller.GetType().Name.Replace("Feature", "").Split('`')[0];
                tier = name.Contains("Interaction") ? 0 : 2;
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
                var parts = kvp.Key.Split('|');
                if (parts.Length != 2) continue;

                activeNodeIds.Add(parts[0]);
                activeNodeIds.Add(parts[1]);
                links.Add(new TopoLink { Src = parts[0], Tgt = parts[1], HitCount = kvp.Value });
            }

            foreach (var id in activeNodeIds)
            {
                if (NodeMetadata.TryGetValue(id, out var meta))
                    nodes.Add(new TopoNode { Id = id, Label = meta.Name, Tier = meta.Tier });
            }

            return (nodes, links);
        }

        private class DisposeAction : IDisposable
        {
            private readonly Action _action;
            public DisposeAction(Action action) => _action = action;
            public void Dispose() => _action();
        }
#endif
    }
}
