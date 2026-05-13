using Hevo.Charting.Core;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Hevo.Charting.DevTools
{
    // ==========================================
    // 💥 4. 终极 UI 控制器 (高级引脚梳理 + 点击聚焦)
    // ==========================================
    public class TopologyInspectorControl : Panel
    {
        private readonly TopologyTracer? _tracer;
        // 保留 cell 引用而不是 schema —— Inspector 寿命可能跨过 schema 替换(蓝图 reload / 主图 unload),
        // 每次 dump 时从 cell.Template 取**当前**的 schema,才能拿到活的 board 和最新 values。
        // _schemaAtConstruction 仅用于 _meta 段标记 schemaReplaced,提示用户上下文已切换。
        private readonly ChartCell _cell;
        private readonly ReactiveSchema? _schemaAtConstruction;
        private readonly DrawingVisual _visual = new();
        private List<TopoNode> _nodes = new();
        private List<TopoLink> _links = new();

        // 💥 交互升级：从 Hover 改为 Click 锁定
        private string? _selectedId;

        // 环路检测缓存:cyclic links 的 (src, tgt) 集合;只在 _links 数量变化时重算 Tarjan SCC,避免每帧 O(V+E)。
        private readonly HashSet<(string Src, string Tgt)> _cyclicLinks = new();
        private int _lastCycleAnalysisLinkCount = -1;

        // 节点最近被命中的"脉冲"持续时间(秒) — 写入瞬间 outline 高亮,继而平滑衰减。
        private const double PulseDurationSec = 0.4;

        private readonly Brush _bgBrush = new SolidColorBrush(Color.FromRgb(12, 12, 16)); // 加深底色，让霓虹灯更亮
        private readonly Brush _nodeBrush = new SolidColorBrush(Color.FromRgb(35, 35, 45));
        private readonly Typeface _font = new Typeface("Consolas");

        // 持有所属 Window 的引用,Unloaded 时反订阅 PreviewKeyDown,避免 Inspector 关闭后还吃事件。
        private Window? _hostWindow;

        public TopologyInspectorControl(ChartCell cell)
        {
            this.Focusable = true;
            this.Cursor = Cursors.Hand; // 提示可点击
            this.Background = Brushes.Transparent;
            this.AddVisualChild(_visual);

            _cell = cell;
            _schemaAtConstruction = cell.Template as ReactiveSchema;

            // Ctrl+E 走 Window 级 PreviewKeyDown,跳过 Panel 焦点问题 ——
            // Panel 即便 Focusable=true,点击也不会自动转移键盘焦点(WPF 默认行为),
            // 之前 OnKeyDown 永远收不到 Ctrl+E。挂到 Window 上,焦点在哪都能触发。
            this.Loaded += OnInspectorLoaded;
            this.Unloaded += OnInspectorUnloaded;

#if DEBUG
            _tracer = TracerRegistry.Get(cell.Template);

            var staticNodes = TopologyScanner.Scan(cell);
            _nodes = staticNodes;

            // 把静态扫描出的节点(尤其是 port)登记进 NodeMetadata —— 后续 friendly name map
            // (BuildFeatureNameMap / BuildPortDisplayMap)从 NodeMetadata 取数据。
            // 没这一步,只有运行时被 Read/Write 过的端口才进 NodeMetadata,静态 scan 出来但还没动数据的 port
            // 在画布上显示原始 Label,用户右键开 inspector 那一瞬看到的就是 ugly 名字。
            if (_tracer != null)
            {
                foreach (var n in staticNodes)
                    _tracer.NodeMetadata.TryAdd(n.Id, (n.Label, n.Tier));

                // 记录 port → 声明它的 owner —— 给孤儿 port(declared 但没读写)在
                // BuildPortDisplayMap 里加 "Owner › PortName" 前缀,用户一眼能看出这根 port 是谁的。
                //
                // 顺序:schema 自有 port 必须**先注册**(TryAdd 是 no-op-on-conflict)。
                // ChartFeature.Viewport 字段透传 schema 的 ViewportPorts,任何 feature 通过 ExtractPorts
                // 都能"看到" Viewport.UserRange / ActiveRange / LogicalLength —— 第一个被处理的 feature
                // 会冒充成 Viewport.* 的 owner,而 Viewport.* 实际归 schema。所以先 schema 锁定 Viewport.* / LocalHit,
                // 再让 features 认领它们独有的端口。
                if (cell.Template is ReactiveSchema rs)
                {
                    // (a) schema 自有 port —— LocalHit / Viewport.* 由 SchemaContext 持有,不属任何 feature。
                    // owner 用合成 id "F_Schema",NodeMetadata 里也补一条 ("Schema", Tier 2),featureNameMap
                    // 拿这个 id 直接显示 "Schema"。LocalHit 在 standalone 模式下显示成 "Schema › LocalHit (孤)" ——
                    // 用户知道它是 schema 自带 hit 桥,只在 dashboard 联动才被覆盖共享。
                    const string SchemaOwnerId = "F_Schema";
                    _tracer.NodeMetadata.TryAdd(SchemaOwnerId, ("Schema", 2));
                    foreach (var port in TopologyScanner.ExtractPortsPublic(rs))
                        _tracer.PortOwner.TryAdd(port.Id, SchemaOwnerId);

                    // (b) feature 自有 port —— Viewport.* 已被 (a) 锁了,这里 TryAdd 自动 no-op。
                    foreach (var feature in rs.ListFeatures())
                    {
                        var fid = $"F_{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(feature)}";
                        foreach (var port in TopologyScanner.ExtractPortsPublic(feature))
                            _tracer.PortOwner.TryAdd(port.Id, fid);
                    }
                }
            }
#endif
            CompositionTarget.Rendering += (s, e) =>
            {
#if DEBUG
                if (_tracer != null)
                {
                    // 高频活动层:每帧调一次,内部 500ms 限流 —— 把 _portWriteCount 累计差量塞进 ring buffer。
                    _tracer.SampleActivity();
                    var (dynNodes, dynLinks) = _tracer.DumpTopology();
                    MergeDynamicBlood(dynNodes, dynLinks);
                }
#endif
                Render();
            };
        }

        private void OnInspectorLoaded(object sender, RoutedEventArgs e)
        {
            _hostWindow = Window.GetWindow(this);
            if (_hostWindow != null)
                _hostWindow.PreviewKeyDown += OnHostWindowPreviewKeyDown;
        }

        private void OnInspectorUnloaded(object sender, RoutedEventArgs e)
        {
            if (_hostWindow != null)
            {
                _hostWindow.PreviewKeyDown -= OnHostWindowPreviewKeyDown;
                _hostWindow = null;
            }
        }

        private void OnHostWindowPreviewKeyDown(object sender, KeyEventArgs e) => TryHandleSnapshotShortcut(e);

        // Ctrl+E 把当前拓扑(节点+连线+命中数+平均成本)拷到剪贴板 + 写到 Debug 输出,方便贴 issue / diff。
        // 同时保留 OnKeyDown 走焦点路径 —— 万一以后 Panel 真拿到了焦点,这条路径仍然能用。
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            TryHandleSnapshotShortcut(e);
        }

        private void TryHandleSnapshotShortcut(KeyEventArgs e)
        {
#if DEBUG
            if (_tracer != null && e.Key == Key.E && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                // 拓扑结构(Tracer 维护)+ port 当前值(Board 维护)+ _meta 诊断段拼成同一份 JSON。
                // Inspector 寿命跨 schema(template 切换),dump 时把 schema/board 状态打出来,
                // 看到 ports=[] 时能直接从 _meta 判断:board 没了 / 桶空 / cast 失败,不用回头加日志。
                var topoJson = _tracer.DumpJson();
                var meta = BuildMetaJson();
                var portsJson = TryDumpPortValuesJson();

                // topoJson 末尾是 "}",在 } 前面塞 _meta + 可选 ports 段。
                var trimmed = topoJson.TrimEnd();
                string json;
                if (trimmed.EndsWith("}"))
                {
                    var body = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();
                    var sb = new System.Text.StringBuilder(body.Length + meta.Length + 256);
                    sb.Append(body).Append(",\n  ").Append(meta);
                    if (portsJson != null) sb.Append(",\n  ").Append(portsJson);
                    sb.Append("\n}");
                    json = sb.ToString();
                }
                else
                {
                    json = topoJson; // 防御:格式异常 fallback
                }

                try { Clipboard.SetText(json); } catch { /* clipboard 偶发被独占,忽略 */ }
                Debug.WriteLine("=== Topology Snapshot (also copied to clipboard) ===");
                Debug.WriteLine(json);
                e.Handled = true;
            }
#endif
        }

#if DEBUG
        // 输出 "_meta": { schemaType, schemaHash, boardAvailable, boardDisposed, boardHash, totalEntries, portEntries, nonPortEntries }
        // 当 "ports" 是空数组时,这个段能直接定位是哪一步出问题:
        //   boardAvailable=false → schema._latestBoard 是 null(管线没 push 过 / 不是 ReactiveSchema)
        //   boardAvailable=true & totalEntries=0 → 数据写在别的 board(典型 dashboard 多 board 场景)
        //   totalEntries>0 & portEntries=0 → board 里 key 不是 IDataPort,过滤被筛掉(异常情形)
        private string BuildMetaJson()
        {
            var sb = new System.Text.StringBuilder(256);
            sb.Append("\"_meta\": { ");

            // 每次 dump 都从 cell.Template 拿当前 schema,不依赖 ctor 时缓存的引用。
            // 蓝图 reload 后 cell.Template 是新 schema,values 取新的;旧 schema 还在 _schemaAtConstruction
            // 里,用来标记 schemaReplaced 提示用户拓扑数据(_tracer 是 ctor 时绑的)对应的是上一代 schema。
            var currentSchema = _cell.Template as ReactiveSchema;
            string schemaType = currentSchema?.GetType().Name ?? "null";
            int schemaHash = currentSchema != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(currentSchema) : 0;
            bool replaced = !ReferenceEquals(currentSchema, _schemaAtConstruction);
            sb.Append("\"schemaType\": ").Append(JsonEscape(schemaType))
              .Append(", \"schemaHash\": ").Append(schemaHash)
              .Append(", \"schemaReplaced\": ").Append(replaced ? "true" : "false");

            var board = currentSchema?.CurrentBoard;
            if (board == null)
            {
                sb.Append(", \"boardAvailable\": false");
            }
            else
            {
                int boardHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(board);
                sb.Append(", \"boardAvailable\": true")
                  .Append(", \"boardDisposed\": ").Append(board.IsDisposed ? "true" : "false")
                  .Append(", \"boardHash\": ").Append(boardHash);
                try
                {
                    var (total, port, nonPort) = board.DumpMemoryStats();
                    sb.Append(", \"totalEntries\": ").Append(total)
                      .Append(", \"portEntries\": ").Append(port)
                      .Append(", \"nonPortEntries\": ").Append(nonPort);
                }
                catch (ObjectDisposedException) { sb.Append(", \"statsError\": \"disposed\""); }
            }
            // tracer 这条路独立于 board lifecycle —— 即便 board.IsDisposed,LastValues 仍然有效。
            // ports 段就是从这个字段渲染的,这里把 count 暴露出来辅助 debug。
            int cachedValues = _tracer?.LastValues.Count ?? 0;
            sb.Append(", \"tracerCachedValues\": ").Append(cachedValues);

            sb.Append(" }");
            return sb.ToString();
        }

        // 把当前 port values 序列化成 "\"ports\": [...]" 片段(不含外层 {});
        // tracer 不可达就返回 null。数据走 tracer.LastValues(写入路径上同步缓存),
        // 跟 board lifecycle 完全解耦 —— 蓝图 one-shot 数据源 push 完 board 立即被 dispose 也无妨,
        // 只要 Inspector 还活着就能拿到最后已知 values。
        private string? TryDumpPortValuesJson()
        {
            if (_tracer == null) return null;
            var values = _tracer.LastValues;
            // ConcurrentDictionary 的 ToArray 是快照,迭代期间被并发写也安全。
            var rows = new List<(string Id, string DisplayName, string TypeName, object? Value)>(values.Count);
            foreach (var kv in values)
                rows.Add((kv.Key, kv.Value.DisplayName, kv.Value.TypeName, kv.Value.Value));
            var sb = new System.Text.StringBuilder(256 + rows.Count * 96);
            sb.Append("\"ports\": [");
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var r = rows[i];
                sb.Append("\n    { \"id\": ").Append(JsonEscape(r.Id))
                  .Append(", \"name\": ").Append(JsonEscape(r.DisplayName))
                  .Append(", \"type\": ").Append(JsonEscape(r.TypeName))
                  .Append(", \"value\": ").Append(JsonEscape(FormatValue(r.Value)))
                  .Append(" }");
            }
            sb.Append("\n  ]");
            return sb.ToString();
        }

        // 值 → 字符串:基本类型直接 ToString,集合给个 "Count=N" 摘要避免 dump 出几兆数据,
        // 其它类型走 ToString。Inspector 是给人看的,完整保真不是目标。
        private static string FormatValue(object? v)
        {
            if (v == null) return "null";
            if (v is string s) return s;
            if (v is System.Collections.ICollection col) return $"<{v.GetType().Name} Count={col.Count}>";
            return v.ToString() ?? "null";
        }

        private static string JsonEscape(string s)
        {
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
#endif

        private void MergeDynamicBlood(List<TopoNode> dynNodes, List<TopoLink> dynLinks)
        {
            foreach (var dn in dynNodes)
                if (!_nodes.Any(n => n.Id == dn.Id)) _nodes.Add(dn);

            long sampleNow = Stopwatch.GetTimestamp();
            foreach (var dLink in dynLinks)
            {
                var existing = _links.FirstOrDefault(l => l.Src == dLink.Src && l.Tgt == dLink.Tgt);
                if (existing == null)
                {
                    existing = new TopoLink { Src = dLink.Src, Tgt = dLink.Tgt, CurrentHeat = 0.5, LastSampleTicks = sampleNow };
                    _links.Add(existing);
                }
                existing.LastDelta = dLink.HitCount - existing.LastHitCount;

                // 实时流速:Δhits / Δseconds → EMA 平滑(0.7 旧 + 0.3 新)。
                // 比直接显示总累计数有用 — 数据流停了 RecentRate 几秒内自然趋零,UI 一眼能看出。
                if (existing.LastSampleTicks > 0 && existing.LastDelta > 0)
                {
                    double dtSec = (sampleNow - existing.LastSampleTicks) / (double)Stopwatch.Frequency;
                    if (dtSec > 0.001)
                    {
                        double instRate = existing.LastDelta / dtSec;
                        existing.RecentRate = existing.RecentRate * 0.7 + instRate * 0.3;
                    }
                }
                else if (existing.LastDelta == 0)
                {
                    // 没有新增命中 → 慢慢衰减 RecentRate,大约 1.5s 趋零。
                    existing.RecentRate *= 0.92;
                    if (existing.RecentRate < 0.5) existing.RecentRate = 0;
                }
                existing.LastHitCount = dLink.HitCount;
                existing.LastSampleTicks = sampleNow;
            }

            foreach (var link in _links)
            {
                if (link.LastDelta > 0)
                    link.CurrentHeat = Math.Min(1.0, link.CurrentHeat + link.LastDelta * 0.15);
                else
                    link.CurrentHeat = Math.Max(0.0, link.CurrentHeat - 0.03);

                link.ParticleOffset += 0.002 + (link.CurrentHeat * 0.018);
                if (link.ParticleOffset > 1.0) link.ParticleOffset -= 1.0;
                link.LastDelta = 0;
            }

            // 拓扑结构变化才重算环路,稳态零开销。Tarjan SCC O(V+E) 一次跑完。
            if (_links.Count != _lastCycleAnalysisLinkCount)
            {
                RecomputeCycles();
                _lastCycleAnalysisLinkCount = _links.Count;
            }
        }

        // Tarjan 强连通分量算法 — 非递归实现避免大图 stack overflow,但实际节点数 < 200 不是问题。
        // 这里直接用递归版本简洁。SCC size > 1 或自环 都判为环路。
        private void RecomputeCycles()
        {
            _cyclicLinks.Clear();
            if (_links.Count == 0) return;

            // 自环:src == tgt 直接判定。
            foreach (var link in _links)
            {
                if (link.Src == link.Tgt) _cyclicLinks.Add((link.Src, link.Tgt));
            }

            // 邻接表
            var adj = new Dictionary<string, List<string>>();
            foreach (var link in _links)
            {
                if (!adj.TryGetValue(link.Src, out var list))
                {
                    list = new List<string>();
                    adj[link.Src] = list;
                }
                list.Add(link.Tgt);
            }

            // Tarjan 状态
            var indexMap = new Dictionary<string, int>();
            var lowlink = new Dictionary<string, int>();
            var onStack = new HashSet<string>();
            var stack = new Stack<string>();
            int idx = 0;
            var multiSccs = new List<HashSet<string>>();

            void StrongConnect(string v)
            {
                indexMap[v] = idx;
                lowlink[v] = idx;
                idx++;
                stack.Push(v);
                onStack.Add(v);

                if (adj.TryGetValue(v, out var neighbors))
                {
                    foreach (var w in neighbors)
                    {
                        if (!indexMap.ContainsKey(w))
                        {
                            StrongConnect(w);
                            lowlink[v] = Math.Min(lowlink[v], lowlink[w]);
                        }
                        else if (onStack.Contains(w))
                        {
                            lowlink[v] = Math.Min(lowlink[v], indexMap[w]);
                        }
                    }
                }

                if (lowlink[v] == indexMap[v])
                {
                    var scc = new HashSet<string>();
                    while (true)
                    {
                        var w = stack.Pop();
                        onStack.Remove(w);
                        scc.Add(w);
                        if (w == v) break;
                    }
                    if (scc.Count > 1) multiSccs.Add(scc);
                }
            }

            foreach (var node in _nodes)
            {
                if (!indexMap.ContainsKey(node.Id)) StrongConnect(node.Id);
            }

            // 标记环路连线:src 和 tgt 都属于同一 SCC 才算。
            foreach (var scc in multiSccs)
            {
                foreach (var link in _links)
                {
                    if (scc.Contains(link.Src) && scc.Contains(link.Tgt))
                        _cyclicLinks.Add((link.Src, link.Tgt));
                }
            }
        }

        // ==========================================
        // 💥 交互核心:点击选中 / 拖拽 pin / 右键 unpin
        // 拖拽阈值: 鼠标移动 > 3 px 才算 drag,否则按 click 处理(选中切换)。
        // ==========================================
        private TopoNode? _dragNode;
        private Point _dragStartPos;
        private Point _dragNodeStartTopLeft;
        private bool _draggedPastThreshold;
        private const double DragThreshold = 3.0;

        // 检测点击是否落在时间线 panel 区域(用于切换 drag-pan vs drag-node)
        private bool IsInTimelineArea(Point pos)
        {
            double timelineTop    = ActualHeight - TimelineHeight - LegendHeight;
            double timelineBottom = ActualHeight - LegendHeight;
            return pos.Y >= timelineTop && pos.Y < timelineBottom;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(this);

            // 时间线区:双击复位到跟随模式,单击+拖拽 = 平移历史视窗
            if (IsInTimelineArea(pos))
            {
                if (e.ClickCount >= 2)
                {
                    _timelineViewOffsetTicks = 0;
                    e.Handled = true;
                    base.OnMouseLeftButtonDown(e);
                    return;
                }
                _timelineDragActive = true;
                _timelineDragStart = pos;
                CaptureMouse();
                base.OnMouseLeftButtonDown(e);
                return;
            }

            var node = _nodes.FirstOrDefault(n => n.Bounds.Contains(pos));

            _dragNode = node;
            _dragStartPos = pos;
            _draggedPastThreshold = false;
            if (node != null)
            {
                _dragNodeStartTopLeft = new Point(node.Bounds.X, node.Bounds.Y);
                CaptureMouse();
            }
            base.OnMouseLeftButtonDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            // 时间线拖拽:用 px → ticks 反推 offset
            if (_timelineDragActive && e.LeftButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(this);
                double dx = pos.X - _timelineDragStart.X;
                _timelineDragStart = pos;

                if (_tracer != null)
                {
                    long elapsed = _tracer.ElapsedTicks;
                    long viewSpan = 60 * Stopwatch.Frequency;  // 跟 DrawTimeline 同款固定 60s 窗
                    long maxOffset = Math.Max(0, elapsed - viewSpan);
                    if (maxOffset > 0)  // 数据够长才能 pan,否则 maxOffset=0 直接 clamp 死 0
                    {
                        double xWidth = Math.Max(50, ActualWidth - 8 - TimelineLabelLaneW);
                        long deltaTicks = (long)(dx / xWidth * viewSpan);
                        // grab-and-pan 语义:拖右 = 内容跟着鼠标往右走 = 视窗左移 = 看更老的
                        _timelineViewOffsetTicks = Math.Clamp(
                            _timelineViewOffsetTicks + deltaTicks, 0, maxOffset);
                    }
                }
                base.OnMouseMove(e);
                return;
            }

            if (_dragNode != null && e.LeftButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(this);
                double dx = pos.X - _dragStartPos.X;
                double dy = pos.Y - _dragStartPos.Y;

                if (!_draggedPastThreshold && (Math.Abs(dx) + Math.Abs(dy)) > DragThreshold)
                {
                    _draggedPastThreshold = true;
                    _dragNode.IsPinned = true; // 跨过阈值即被认定为"用户主动调位",pin 住
                }

                if (_draggedPastThreshold)
                {
                    var newX = _dragNodeStartTopLeft.X + dx;
                    var newY = _dragNodeStartTopLeft.Y + dy;
                    // 保持在视口内,不让节点跑到画布外面去
                    newX = Math.Clamp(newX, 0, Math.Max(0, ActualWidth - _dragNode.Bounds.Width));
                    newY = Math.Clamp(newY, HeaderHeight, Math.Max(HeaderHeight, ActualHeight - LegendHeight - TimelineHeight - _dragNode.Bounds.Height));
                    _dragNode.Bounds = new Rect(newX, newY, _dragNode.Bounds.Width, _dragNode.Bounds.Height);
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (IsMouseCaptured) ReleaseMouseCapture();

            if (_timelineDragActive)
            {
                _timelineDragActive = false;
                base.OnMouseLeftButtonUp(e);
                return;
            }

            if (_dragNode != null && !_draggedPastThreshold)
            {
                // 阈值内未越过 → 视为单击:切换选中
                _selectedId = _selectedId == _dragNode.Id ? null : _dragNode.Id;
            }
            else if (_dragNode == null && !_draggedPastThreshold)
            {
                // 空白处单击:清空选中
                _selectedId = null;
            }
            // 越过阈值的拖拽:位置已在 MouseMove 实时更新,这里不再做事

            _dragNode = null;
            _draggedPastThreshold = false;
            base.OnMouseLeftButtonUp(e);
        }


        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(this);
            var node = _nodes.FirstOrDefault(n => n.Bounds.Contains(pos));
            if (node != null)
            {
                // 右键单个节点:解除该节点的 pin,下一帧 LayoutNodes 把它收回自动布局位置
                node.IsPinned = false;
            }
            else
            {
                // 右键空白:批量解除所有 pin
                foreach (var n in _nodes) n.IsPinned = false;
            }
            e.Handled = true;
            base.OnMouseRightButtonDown(e);
        }

        // 顶部留出给 Tier 表头,底部叠加 时间线 + 颜色图例。
        // 整体布局自上而下:Header(表头) → 拓扑画布 → Timeline(时间线) → Legend(图例)。
        // LayoutNodes 内的节点垂直分布按 ActualHeight - HeaderHeight - TimelineHeight - LegendHeight 收缩。
        private const double HeaderHeight   = 28.0;
        private const double TimelineHeight = 280.0;
        private const double LegendHeight   = 90.0;

        // 时间线交互状态:可拖拽平移(_timelineDragActive)。横轴域 = [0, max(now, 桶最老 ticks)],
        // 用户拖时把视窗左右滑动 _timelineViewOffsetTicks。
        private bool _timelineDragActive;
        private Point _timelineDragStart;
        private long _timelineViewOffsetTicks; // 0 = 跟随 now;>0 = 向左滚显历史

        // 当前 Render 帧用的 friendly name 表 —— DrawTimeline + 拓扑画布共享,保持两边对齐。
        private Dictionary<string, string> _frameNodeDisplay = new();

        private void Render()
        {
            if (ActualWidth < 10) return;

            using var dc = _visual.RenderOpen();
            dc.DrawRectangle(_bgBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));

            // 帧首先算 friendly 名表,后续 topology 画布 + timeline 都从这里查。
#if DEBUG
            if (_tracer != null)
            {
                var featureNamesNow = BuildFeatureNameMap();
                var portWritersNow = BuildPortWriterMap(featureNamesNow);
                var portDisplayNow = BuildPortDisplayMap(portWritersNow, featureNamesNow);
                _frameNodeDisplay = new Dictionary<string, string>(featureNamesNow.Count + portDisplayNow.Count);
                foreach (var kv in featureNamesNow) _frameNodeDisplay[kv.Key] = kv.Value;
                foreach (var kv in portDisplayNow) _frameNodeDisplay[kv.Key] = kv.Value;
            }
#endif

            // 空态:还没扫到任何节点 / 任何连线 → 给 dev 一个明确的"在等数据"信号,
            // 避免误以为是渲染坏了。
            if (_nodes.Count == 0)
            {
                var emptyFt = new FormattedText(
                    "等待数据流入… (尚未捕获 Feature / 引脚)",
                    CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    _font, 13, Brushes.DimGray, 1.0);
                dc.DrawText(emptyFt, new Point((ActualWidth - emptyFt.Width) / 2, (ActualHeight - emptyFt.Height) / 2));
                return;
            }

            LayoutNodes();
            DrawTierHeaders(dc);

            // 帧时间戳:节点脉冲根据它跟 _tracer.LastHitTicks 的差值算淡入淡出。
            long now = Stopwatch.GetTimestamp();

            // ==========================================
            // 💥 高级算法：智能引脚梳理 (Port Spreading)
            // 根据连接目标的高低，自动把线条均匀分布在节点的右侧和左侧边缘
            // ==========================================
            var srcGroups = _links.GroupBy(l => l.Src).ToDictionary(g => g.Key, g => g.OrderBy(l => _nodes.FirstOrDefault(n => n.Id == l.Tgt)?.Bounds.Top ?? 0).ToList());
            var tgtGroups = _links.GroupBy(l => l.Tgt).ToDictionary(g => g.Key, g => g.OrderBy(l => _nodes.FirstOrDefault(n => n.Id == l.Src)?.Bounds.Top ?? 0).ToList());

            // 绘制管线
            foreach (var link in _links)
            {
                var src = _nodes.FirstOrDefault(n => n.Id == link.Src);
                var tgt = _nodes.FirstOrDefault(n => n.Id == link.Tgt);
                if (src == null || tgt == null) continue;

                // 焦点状态判断
                bool isTargeted = _selectedId != null && (link.Src == _selectedId || link.Tgt == _selectedId);
                bool isDimmed = _selectedId != null && !isTargeted; // 没被选中的统统变暗

                // 💥 动态计算出口与入口的垂直坐标
                var outLinks = srcGroups.GetValueOrDefault(link.Src) ?? new List<TopoLink> { link };
                var inLinks = tgtGroups.GetValueOrDefault(link.Tgt) ?? new List<TopoLink> { link };

                int outIdx = outLinks.IndexOf(link);
                int inIdx = inLinks.IndexOf(link);

                // 让出入口在节点的边缘均匀散开
                double outY = src.Bounds.Top + (src.Bounds.Height / (outLinks.Count + 1)) * (outIdx + 1);
                double inY = tgt.Bounds.Top + (tgt.Bounds.Height / (inLinks.Count + 1)) * (inIdx + 1);

                Point p1 = new Point(src.Bounds.Right, outY);
                Point p4 = new Point(tgt.Bounds.Left, inY);

                // 💥 优化贝塞尔曲线：更有张力的S型曲线 (Sankey Style)
                double dx = Math.Abs(p4.X - p1.X);
                double dy = Math.Abs(p4.Y - p1.Y);
                // 增加 Y 轴补偿，使得跨度很大的线条弧度更加饱满平滑
                double cpX = Math.Max(50, dx * 0.45 + dy * 0.15);

                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(p1, false, false);
                    ctx.BezierTo(new Point(p1.X + cpX, p1.Y), new Point(p4.X - cpX, p4.Y), p4, true, false);
                }

                double heat = link.CurrentHeat;
                double thickness = isTargeted ? 3.0 : 1.0 + (heat * 2.5); // 选中时强制加粗
                Brush pipeColor = heat > 0.8 ? Brushes.Tomato : (heat > 0.3 ? Brushes.Cyan : new SolidColorBrush(Color.FromArgb(90, 100, 150, 255)));

                Pen pipePen = new Pen(pipeColor, thickness);
                pipePen.Freeze();

                // 读 vs 写:src 是 port (tier 1) 时是"port → feature"语义,即被读取;否则是写入。
                // 读连线给空心箭头,写连线给实心箭头,一眼分清方向。
                bool isRead = src.Tier == 1;

                // 💥 渲染时压低无关管线的透明度
                dc.PushOpacity(isDimmed ? 0.1 : 1.0);

                // 环路标记:框架里有合法的循环依赖(比如 ViewportManager ↔ ChartInteraction
                // 互写 UserRange/ActiveRange,靠 WriteIfChanged 去重),不是 bug。所以用琥珀色而非红色,
                // 表达"这是个 loop"而不是"这是错误"。Pen 加宽 4px 当 halo 用,识别度足够。
                if (_cyclicLinks.Contains((link.Src, link.Tgt)))
                {
                    var cycleBrush = new SolidColorBrush(Color.FromArgb(120, 255, 180, 90));
                    cycleBrush.Freeze();
                    var cyclePen = new Pen(cycleBrush, thickness + 4);
                    cyclePen.Freeze();
                    dc.DrawGeometry(null, cyclePen, geo);
                }

                dc.DrawGeometry(null, pipePen, geo);

                // 端点箭头:Bezier 在 t=1 处的切线方向 ≈ p4 - cp2 (cp2 = p4.X - cpX, p4.Y)
                DrawArrow(dc, p4, new Point(p4.X - cpX, p4.Y), pipeColor, isTargeted ? 9 : 7, hollow: isRead);

                // 如果处于暗淡状态，就不画粒子了，避免视觉杂乱
                if (!isDimmed)
                {
                    DrawFlowParticle(dc, p1, p4, cpX, link.ParticleOffset, heat, isTargeted);
                }

                // 选中节点关联的连线在中点显示 命中速率 + 累计数。
                // 速率比单纯总数更直观地说明"这条链路现在还在不在动"。
                if (isTargeted && link.LastHitCount > 0)
                {
                    DrawLinkHitCount(dc, p1, p4, cpX, link, pipeColor);
                }

                dc.Pop();
            }

            // 绘制节点
            foreach (var node in _nodes)
            {
                bool isSelected = node.Id == _selectedId;
                // 只要不是选中状态，且当前有节点被选中，就被视为暗淡
                bool isDimmed = _selectedId != null && !isSelected && !_links.Any(l => (l.Src == _selectedId && l.Tgt == node.Id) || (l.Tgt == _selectedId && l.Src == node.Id));

                dc.PushOpacity(isDimmed ? 0.2 : 1.0); // 没选中的节点也变暗

                // 写入脉冲:节点最近被命中过,边缘画一圈渐淡的高亮 outline。
                // 比线条粒子更直观地告诉人"这个节点刚收到数据"。
                double pulse = ComputePulseStrength(node.Id, now);
                if (pulse > 0 && !isDimmed)
                {
                    double pad = 3 + pulse * 5;
                    var pulseRect = new Rect(node.Bounds.X - pad, node.Bounds.Y - pad,
                                             node.Bounds.Width + pad * 2, node.Bounds.Height + pad * 2);
                    byte alpha = (byte)(pulse * 220);
                    var pulseBrush = new SolidColorBrush(Color.FromArgb(alpha, 120, 220, 255));
                    pulseBrush.Freeze();
                    var pulsePen = new Pen(pulseBrush, 2);
                    pulsePen.Freeze();
                    dc.DrawRoundedRectangle(null, pulsePen, pulseRect, 6, 6);
                }

                Brush borderBrush = isSelected ? Brushes.Cyan : Brushes.DimGray;
                double borderThickness = isSelected ? 2.0 : 1.2;

                dc.DrawRoundedRectangle(isSelected ? Brushes.DarkSlateBlue : _nodeBrush,
                    new Pen(borderBrush, borderThickness), node.Bounds, 4, 4);

                // Pin 指示:被用户拖拽过的节点左上角画琥珀小点,提示这个位置是手动定的、不会被自动布局重置
                if (node.IsPinned)
                {
                    var pinDot = new SolidColorBrush(Color.FromRgb(255, 200, 100));
                    pinDot.Freeze();
                    dc.DrawEllipse(pinDot, null, new Point(node.Bounds.Left + 5, node.Bounds.Top + 5), 2.5, 2.5);
                }

                // 优先用帧 friendly 名表(同名 feature 加 hash / port 走 writer 前缀 + 重名消歧),
                // 跟下面时间线 panel 的 port 名严格对齐。表里没有就回退原 Label。
                string displayLabel = _frameNodeDisplay.GetValueOrDefault(node.Id) ?? node.Label;
                var ft = new FormattedText(displayLabel, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _font, 12, isSelected ? Brushes.White : Brushes.LightGray, 1.0);
                dc.DrawText(ft, new Point(node.Bounds.Left + 10, node.Bounds.Top + (node.Bounds.Height - ft.Height) / 2));

#if DEBUG
                // Per-feature 性能徽章:tier 2 + 有采样数据时,右上角小字显示平均 OnProject 耗时。
                // 颜色阈值: <0.3ms 海绿(健康) / <1ms 黄色(关注) / >=1ms 橘红(瓶颈嫌疑)
                // 💥 TopologyTracer.FeatureCost 仅在 DEBUG 编译条件下声明,Release 不存在该字段 ——
                //    整段徽章逻辑包在 #if DEBUG 里跟字段定义对齐,否则 Release 编译挂掉。
                if (node.Tier == 2 && _tracer != null
                    && _tracer.FeatureCost.TryGetValue(node.Id, out var cost) && cost.Samples > 0)
                {
                    double avgMs = (cost.TotalTicks / (double)cost.Samples) / Stopwatch.Frequency * 1000.0;
                    string badge = avgMs < 0.05 ? "<.05" : avgMs.ToString("F2");
                    Brush badgeColor = avgMs >= 1.0 ? Brushes.OrangeRed
                                       : avgMs >= 0.3 ? Brushes.Khaki
                                       : Brushes.LightSeaGreen;
                    var badgeFt = new FormattedText(
                        badge + "ms",
                        CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        _font, 9, badgeColor, 1.0);
                    dc.DrawText(badgeFt, new Point(node.Bounds.Right - badgeFt.Width - 4, node.Bounds.Top + 2));
                }
#endif

                dc.Pop();
            }

            // 关键事件时间线 —— compose 起止 / 单 feature 接入 / 端口首次写 / handler 缺失 等永久层事件,
            // 给"右键打开 inspector 是滞后行为,想看从启动到现在发生过啥"这个用户场景。
            DrawTimeline(dc);

            // 颜色图例(右下角小卡片) — 让 heat 颜色含义可被新人一眼读懂。
            DrawLegend(dc);
        }

        // ──── 时间线 panel(横轴 = 时间,纵向分:key events 行 + per-port 活动柱)──────────
        // 自上而下分 4 个区:
        //   1. 标题栏(高度 22) —— "⏱ 时间线" + 当前时长 + 视窗范围
        //   2. 时间轴刻度(高度 18) —— 主刻度 + label
        //   3. Key events 行(高度 18) —— 6 类事件不同颜色的圆点
        //   4. Per-port 活动柱区(剩余) —— 每根 port 一行,横向铺 240 桶活动强度
        // 横轴域 [vMin, vMax] 默认 [0, ElapsedTicks],拖拽 _timelineViewOffsetTicks 可往老滑;
        // 鼠标滚轮 zoom 暂未实装,等用户提了再加。
        private const double TimelineLabelLaneW = 220.0; // 左侧 port 名称列宽(留够 "MarketScanner › ChangePercent" 这种 writer-prefix 长名)
        private const double TimelineHeaderH    = 22.0;
        private const double TimelineAxisH      = 18.0;
        private const double TimelineKeyEventsH = 18.0;
        private const double TimelinePortLaneH  = 18.0;  // 行高从 14 → 18,字立得起来 + zebra 视觉分得开

        private void DrawTimeline(DrawingContext dc)
        {
#if DEBUG
            if (_tracer == null) return;

            double top    = ActualHeight - TimelineHeight - LegendHeight;
            double bottom = ActualHeight - LegendHeight;
            var area = new Rect(0, top, ActualWidth, TimelineHeight);

            // 整块底色 + 顶部分隔
            var panelBg = new SolidColorBrush(Color.FromRgb(18, 18, 22));
            dc.DrawRectangle(panelBg, null, area);
            var sepPen = new Pen(new SolidColorBrush(Color.FromRgb(40, 40, 48)), 1);
            dc.DrawLine(sepPen, new Point(0, top), new Point(ActualWidth, top));

            long elapsed = _tracer.ElapsedTicks;
            if (elapsed <= 0) return;

            // 视窗宽度 = 固定 60s。窗口随 offset 整体平移:
            //   - elapsed <= viewSpan(早期):数据全装得下,显示 [0, elapsed],平移无意义
            //   - elapsed >  viewSpan(晚期):
            //       offset=0 跟随 now    → 视窗 [elapsed - viewSpan, elapsed]
            //       offset>0 看更老的    → 视窗 [elapsed - viewSpan - offset, elapsed - offset]
            //       offset 上限 = elapsed - viewSpan(滑到最左,T+0s 贴左)
            long viewSpan = 60 * Stopwatch.Frequency;       // 60s 默认窗口宽度,兼顾密度跟全局感
            long maxOffset = Math.Max(0, elapsed - viewSpan);
            _timelineViewOffsetTicks = Math.Clamp(_timelineViewOffsetTicks, 0, maxOffset);

            long vMax, vMin;
            if (elapsed <= viewSpan)
            {
                // 数据少,全显:窗口 [0, elapsed],平移此时无效。
                vMin = 0;
                vMax = Math.Max(elapsed, Stopwatch.Frequency); // 防 vMax-vMin=0 出现的除 0
            }
            else
            {
                vMax = elapsed - _timelineViewOffsetTicks;
                vMin = vMax - viewSpan;
            }

            double xLeft = TimelineLabelLaneW;
            double xRight = ActualWidth - 8;
            double xWidth = Math.Max(50, xRight - xLeft);

            // ── 1. 标题栏 ──────────────────────────────────────────────────────
            double secElapsed = elapsed / (double)Stopwatch.Frequency;
            double secViewMax = vMax / (double)Stopwatch.Frequency;
            double secViewMin = vMin / (double)Stopwatch.Frequency;
            string follow = _timelineViewOffsetTicks == 0 ? " · 跟随" : " · 历史回看";
            var titleFt = new FormattedText(
                $"⏱ 时间线   总时长 T+{secElapsed:F1}s   视窗 T+{secViewMin:F1}s ~ T+{secViewMax:F1}s{follow}   (左拖平移)",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                _font, 12, new SolidColorBrush(Color.FromRgb(200, 210, 220)), 1.0);
            dc.DrawText(titleFt, new Point(8, top + 4));

            double yAxis = top + TimelineHeaderH;
            double yKeyEvents = yAxis + TimelineAxisH;
            double yPortsTop = yKeyEvents + TimelineKeyEventsH;
            double yPortsBottom = bottom;

            // ── 2. 时间轴刻度 ──────────────────────────────────────────────────
            DrawTimelineAxis(dc, xLeft, xRight, yAxis, vMin, vMax);

            // ── 3. Key events 行 ──────────────────────────────────────────────
            DrawTimelineKeyEvents(dc, xLeft, xRight, yKeyEvents, vMin, vMax);

            // ── 4. Per-port 活动柱 ─────────────────────────────────────────────
            DrawTimelinePortLanes(dc, xLeft, xRight, yPortsTop, yPortsBottom, vMin, vMax);
#endif
        }

        private void DrawTimelineAxis(DrawingContext dc, double xLeft, double xRight, double yTop,
                                      long vMin, long vMax)
        {
            double laneW = xRight - xLeft;
            double rangeSec = (vMax - vMin) / (double)Stopwatch.Frequency;
            // 选个差不多的主刻度间隔(目标 6~10 个刻度)
            double step = PickAxisStep(rangeSec / 8);
            double stepTicks = step * Stopwatch.Frequency;
            var tickPen = new Pen(new SolidColorBrush(Color.FromRgb(60, 65, 75)), 1);
            var axisLabelBrush = new SolidColorBrush(Color.FromRgb(140, 150, 160));

            // 底线
            dc.DrawLine(tickPen, new Point(xLeft, yTop + TimelineAxisH - 1),
                                  new Point(xRight, yTop + TimelineAxisH - 1));

            double startSec = Math.Ceiling((vMin / (double)Stopwatch.Frequency) / step) * step;
            for (double s = startSec; s <= rangeSec + vMin / (double)Stopwatch.Frequency + 0.001; s += step)
            {
                long t = (long)(s * Stopwatch.Frequency);
                double x = xLeft + (t - vMin) / (double)(vMax - vMin) * laneW;
                if (x < xLeft || x > xRight) continue;
                dc.DrawLine(tickPen, new Point(x, yTop + 4), new Point(x, yTop + TimelineAxisH - 1));
                var lbl = new FormattedText($"T+{s:F0}s",
                    CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _font, 10,
                    axisLabelBrush, 1.0);
                dc.DrawText(lbl, new Point(x + 2, yTop + 1));
            }
        }

        // 选 1/2/5/10/30/60/120/300 这种"人能读出来"的刻度间隔。
        private static double PickAxisStep(double approxStepSec)
        {
            double[] candidates = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
            foreach (var c in candidates) if (c >= approxStepSec) return c;
            return 600;
        }

        private void DrawTimelineKeyEvents(DrawingContext dc, double xLeft, double xRight, double y,
                                           long vMin, long vMax)
        {
            if (_tracer == null) return;
            var events = _tracer.DumpKeyEvents();
            double laneW = xRight - xLeft;

            // 左侧标签
            var lblBrush = new SolidColorBrush(Color.FromRgb(180, 190, 200));
            var lbl = new FormattedText("Key Events",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _font, 11, lblBrush, 1.0);
            dc.DrawText(lbl, new Point(8, y + 2));

            double cy = y + TimelineKeyEventsH / 2.0;
            foreach (var ev in events)
            {
                if (ev.TicksFromStart < vMin || ev.TicksFromStart > vMax) continue;
                double x = xLeft + (ev.TicksFromStart - vMin) / (double)(vMax - vMin) * laneW;
                var (_, brush) = TimelineKindStyle(ev.Kind);
                dc.DrawEllipse(brush, null, new Point(x, cy), 4, 4);
            }
        }

        // 排序 + 渲染 per-port 活动柱:
        //  - 只显示 PortActivity 里 Count > 0 的 port
        //  - 按 FirstPortWrite 时间戳升序(老的在上)
        //  - 每根 port 一行 14px,显示左侧 port 名 + 右侧的 240 桶活动柱
        //  - 柱高 = 该桶的 WriteDelta 占该 port 全程 max 的比例;颜色 = 蓝渐变(冷=低活动)
        //  - 整行最右几桶若 WriteDelta=0(超过 5 桶 = 2.5s)→ port 名显黄色("⚠ 静默"提示)
        private void DrawTimelinePortLanes(DrawingContext dc, double xLeft, double xRight,
                                           double yTop, double yBottom, long vMin, long vMax)
        {
            if (_tracer == null) return;
            double laneW = xRight - xLeft;
            double availH = yBottom - yTop;
            int maxLanes = Math.Max(1, (int)(availH / TimelinePortLaneH));

            // 收集 port → first-write timestamp(从 KeyEvents 找,没找到的退化用 0)
            var firstWrite = new Dictionary<string, long>();
            foreach (var ev in _tracer.DumpKeyEvents())
            {
                if (ev.Kind == TopologyTracer.EventKind.FirstPortWrite && !firstWrite.ContainsKey(ev.TargetId))
                    firstWrite[ev.TargetId] = ev.TicksFromStart;
            }

            var lanes = _tracer.PortActivity
                .Where(kv => kv.Value.Count > 0)
                .OrderBy(kv => firstWrite.GetValueOrDefault(kv.Key, long.MaxValue))
                .Take(maxLanes)
                .ToList();

            if (lanes.Count == 0)
            {
                var emptyFt = new FormattedText(
                    "(尚无 port 活动 —— schema compose 后等数据流过来)",
                    CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _font, 11,
                    new SolidColorBrush(Color.FromRgb(110, 115, 125)), 1.0);
                dc.DrawText(emptyFt, new Point(8, yTop + 4));
                return;
            }

            long now = Stopwatch.GetTimestamp() - _tracer.StartTicks;
            long quietThresholdTicks = (long)(2.5 * Stopwatch.Frequency); // 2.5s 没新写就算静默

            // _frameNodeDisplay 在 Render() 头部已经算好(featureName + portWriter + portDisplay 三层去重管线),
            // 下面查表直接用,不重复算。

            // 行间区分配色:zebra 双色底,选中浅蓝,静默橙红 outline
            var zebraOdd  = new SolidColorBrush(Color.FromRgb(22, 22, 28));
            var zebraEven = new SolidColorBrush(Color.FromRgb(28, 28, 36));
            var labelColBg = new SolidColorBrush(Color.FromRgb(20, 22, 28));
            var laneSepPen = new Pen(new SolidColorBrush(Color.FromArgb(35, 100, 110, 130)), 1);

            for (int i = 0; i < lanes.Count; i++)
            {
                var pid = lanes[i].Key;
                var ring = lanes[i].Value;
                double y = yTop + i * TimelinePortLaneH;

                long lastWriteTicksFromStart = ring.LastSampleTicks - _tracer.StartTicks;
                bool isQuiet = (now - lastWriteTicksFromStart) > quietThresholdTicks
                          && ring.WriteDelta[(ring.Head - 1 + TopologyTracer.ActivityRing.CAP) % TopologyTracer.ActivityRing.CAP] == 0;
                bool selected = _selectedId != null && pid == _selectedId;

                // ── 行底色:三层叠 ──
                //   1) zebra 底色(隔行不同灰度,基础视觉锚)
                //   2) 左侧 label 列单独底色,跟右侧活动柱区视觉切分
                //   3) 选中态 蓝半透明覆盖(整行)
                dc.DrawRectangle(i % 2 == 0 ? zebraEven : zebraOdd, null,
                    new Rect(0, y, ActualWidth, TimelinePortLaneH));
                dc.DrawRectangle(labelColBg, null,
                    new Rect(0, y, TimelineLabelLaneW, TimelinePortLaneH));
                if (selected)
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(60, 100, 180, 255)), null,
                        new Rect(0, y, ActualWidth, TimelinePortLaneH));
                }

                // 行底分隔线(细 1px,半透明) —— 行多时一眼能数清
                dc.DrawLine(laneSepPen, new Point(0, y + TimelinePortLaneH - 0.5),
                                         new Point(ActualWidth, y + TimelinePortLaneH - 0.5));

                // ── 左侧 port 名 ──
                // 走 _frameNodeDisplay(已经 writer 前缀 + 重名 hash 后缀都处理过)。
                string label = _frameNodeDisplay.GetValueOrDefault(pid) ?? FriendlyPortName(pid);
                if (label.Length > 30) label = label.Substring(0, 29) + "…";
                Brush nameBrush = isQuiet
                    ? new SolidColorBrush(Color.FromRgb(240, 190, 80))   // 黄:静默
                    : (selected
                        ? new SolidColorBrush(Color.FromRgb(160, 210, 255))  // 选中:浅蓝
                        : new SolidColorBrush(Color.FromRgb(210, 218, 226)));
                string prefix = isQuiet ? "⚠ " : "  ";
                var nameFt = new FormattedText(prefix + label,
                    CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _font, 11, nameBrush, 1.0);
                dc.DrawText(nameFt, new Point(8, y + (TimelinePortLaneH - nameFt.Height) / 2));

                // label 列右边竖分隔
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(80, 80, 90, 110)), 1),
                    new Point(TimelineLabelLaneW, y),
                    new Point(TimelineLabelLaneW, y + TimelinePortLaneH));

                // 计算该 port 全程 max(用于柱高归一化)
                int maxDelta = 0;
                for (int b = 0; b < ring.Count; b++)
                {
                    if (ring.WriteDelta[b] > maxDelta) maxDelta = ring.WriteDelta[b];
                }
                if (maxDelta == 0) continue; // 全无写入,只显示静默标签,跳过画柱

                // 渲染桶。Head 指向下一个空位,真实数据是 Head-Count..Head-1。
                int start = (ring.Head - ring.Count + TopologyTracer.ActivityRing.CAP) % TopologyTracer.ActivityRing.CAP;
                for (int k = 0; k < ring.Count; k++)
                {
                    int b = (start + k) % TopologyTracer.ActivityRing.CAP;
                    long bt = ring.BucketTicks[b];
                    if (bt < vMin || bt > vMax) continue;
                    int wd = ring.WriteDelta[b];
                    if (wd == 0) continue;
                    double x = xLeft + (bt - vMin) / (double)(vMax - vMin) * laneW;
                    double h = (TimelinePortLaneH - 4) * (wd / (double)maxDelta);
                    double bw = Math.Max(2, laneW / Math.Max(40, ring.Count));
                    if (bw > 4) bw = 4;
                    var heat = wd / (double)maxDelta;
                    var bar = new SolidColorBrush(Color.FromArgb(
                        (byte)(150 + heat * 105),
                        (byte)(80 + heat * 50),
                        (byte)(140 + heat * 80),
                        (byte)(220)));
                    dc.DrawRectangle(bar, null,
                        new Rect(x - bw/2, y + TimelinePortLaneH - 2 - h, bw, h));
                }
            }
        }

        // 类别 → (icon, 颜色画刷)。颜色跟 legend 风格一致:绿=正常 / 黄=警告 / 红=错误 / 蓝=数据流 / 紫=DataSource publish。
        private static (string Icon, SolidColorBrush Brush) TimelineKindStyle(TopologyTracer.EventKind k) => k switch
        {
            TopologyTracer.EventKind.ComposeStart        => ("●", new SolidColorBrush(Color.FromRgb(100, 200, 255))),
            TopologyTracer.EventKind.ComposeEnd          => ("◆", new SolidColorBrush(Color.FromRgb(100, 200, 255))),
            TopologyTracer.EventKind.FeatureComposed     => ("✓", new SolidColorBrush(Color.FromRgb(120, 200, 120))),
            TopologyTracer.EventKind.FeatureShortCircuit => ("⚠", new SolidColorBrush(Color.FromRgb(240, 190,  80))),
            TopologyTracer.EventKind.FirstPortWrite      => ("▶", new SolidColorBrush(Color.FromRgb(140, 180, 230))),
            TopologyTracer.EventKind.HandlerMissing      => ("✗", new SolidColorBrush(Color.FromRgb(240,  90,  90))),
            // 紫色菱形 —— DataSource Publish 是数据流入 schema 的最上游事件,跟下游 FirstPortWrite 区分开
            TopologyTracer.EventKind.DataSourcePublish   => ("◈", new SolidColorBrush(Color.FromRgb(200, 140, 230))),
            _                                            => ("·", new SolidColorBrush(Color.FromRgb(200, 200, 200))),
        };

        // target id 优先查 NodeMetadata 取展示名,查不到回退原 id(handler / scheme name 等没节点的 case)。
        // disambiguateBy:为通用端口名(OutputPort / InputPort 等)去重时,
        //   传一个"上游 feature 名 → port"的 prefix。null = 不加前缀,直接用 stripped 名。
        private string? ResolveDisplayName(string id, string? writerHint = null)
        {
#if DEBUG
            string baseName = (_tracer != null && _tracer.NodeMetadata.TryGetValue(id, out var meta))
                ? FriendlyPortName(meta.Name)
                : FriendlyPortName(id);

            // 通用名加 writer 前缀。WellKnown(VP_Xxx, Plot › Xxx)已经有上下文,不重复加。
            if (writerHint != null && IsAmbiguousPortName(baseName))
                return $"{writerHint} › {baseName}";
            return baseName;
#else
            return FriendlyPortName(id);
#endif
        }

        // 这些名字 strip 之后跟 feature 输出端口名重名,单独看认不出谁是谁,需要 writer 前缀消歧。
        private static bool IsAmbiguousPortName(string n) =>
            n == "OutputPort" || n == "InputPort" || n == "DataPort"
            || n == "HitStatePort"
            || n == "ValuePorts" || n == "YRangePort" || n == "RangePort"
            || n == "PointerHitPort" || n == "XAxisDataPort" || n == "BaseLinePort"
            || n == "DomainDataPort" || n == "ReferencePort" || n == "RawExtremaPort"
            || n.StartsWith("Inputs.", StringComparison.Ordinal)   // Compute / Plot 多输入子端口
            || n.StartsWith("SeriesOutputs.", StringComparison.Ordinal);  // PlotFeature 子序列输出端口

        // 反扫 LinkHits 一次,得到 portId → writer feature display name 的映射。
        // 写入者 = LinkHits 里 (Tier ≥ 0 的 src, port 自身) 的命中数最多的那个 src。
        // 多写入者时优先选 Feature(Tier 2),退而求其次 DataPipe(Tier 0)。
        // featureNameMap 已经把 feature 名做过去重(同名 → 加 hash 后缀),所以 writer 端到这里也唯一。
        private Dictionary<string, string> BuildPortWriterMap(Dictionary<string, string> featureNameMap)
        {
            var map = new Dictionary<string, string>();
#if DEBUG
            if (_tracer == null) return map;
            // (port id → 候选 writer + 命中数)
            var bestPerPort = new Dictionary<string, (string srcId, int hits, int srcTier)>();
            foreach (var kv in _tracer.LinkHits)
            {
                var (src, tgt) = kv.Key;
                if (!_tracer.NodeMetadata.TryGetValue(tgt, out var tgtMeta) || tgtMeta.Tier != 1) continue;
                int srcTier = _tracer.NodeMetadata.TryGetValue(src, out var sm) ? sm.Tier : -1;
                if (!bestPerPort.TryGetValue(tgt, out var prev)
                    || (srcTier == 2 && prev.srcTier != 2)             // Feature 优先
                    || (srcTier == prev.srcTier && kv.Value > prev.hits))
                {
                    bestPerPort[tgt] = (src, kv.Value, srcTier);
                }
            }
            foreach (var kv in bestPerPort)
            {
                if (featureNameMap.TryGetValue(kv.Value.srcId, out var displayName))
                    map[kv.Key] = displayName;
                else if (_tracer.NodeMetadata.TryGetValue(kv.Value.srcId, out var srcMeta))
                    map[kv.Key] = ShortenWriterName(srcMeta.Name);   // fallback,DataPipe 等非 feature
            }
#endif
            return map;
        }

        private static string ShortenWriterName(string name)
        {
            // "MarketScannerDataSource" → "MarketScanner"(去 DataSource 后缀;前面注册时已经 strip "Feature" 了,这里 strip "DataSource")
            if (name.EndsWith("DataSource", StringComparison.Ordinal))
                return name.Substring(0, name.Length - "DataSource".Length);
            return name;
        }

        // 第三层:port 最终 display name。
        // 规则:
        //   - 起手 FriendlyPortName(strip hash 前/后缀,VP_X → Viewport.X 等)
        //   - 通用名(OutputPort / InputPort / DataPort 等)加 "WriterFeature › OutputPort" 前缀
        //   - 没 writer 但有 owner(典型:孤儿 output port,如 PlotFeature.HitStatePort 在没 scatter
        //     children 的场景下没人写也没人读)→ 加 "Owner › PortName (孤儿)" 前缀,user 一眼看出归属
        //   - 完成 1+2+3 之后仍有重名(典型:多个 DataSource 都有 Latest 字段)→ 加 [<port id 前 4 位>] 后缀
        private Dictionary<string, string> BuildPortDisplayMap(
            Dictionary<string, string> portWriters,
            Dictionary<string, string> featureNames)
        {
            var initial = new Dictionary<string, string>();
#if DEBUG
            if (_tracer == null) return initial;
            foreach (var kv in _tracer.NodeMetadata)
            {
                if (kv.Value.Tier != 1) continue;
                string baseName = FriendlyPortName(kv.Value.Name);
                if (portWriters.TryGetValue(kv.Key, out var w) && IsAmbiguousPortName(baseName))
                {
                    baseName = $"{w} › {baseName}";
                }
                else if (!portWriters.ContainsKey(kv.Key)
                         && _tracer.PortOwner.TryGetValue(kv.Key, out var ownerId)
                         && featureNames.TryGetValue(ownerId, out var ownerName))
                {
                    // 孤儿 port:有 owner 但完全没 writer。加 "(孤)" 标记。
                    // 这条规则不限 IsAmbiguousPortName —— LocalHit / BoxZoomState 这种本来就唯一的名字
                    // 也加 owner 前缀,因为用户对它们的归属同样模糊(schema 自有 vs feature 自有 vs framework auto)。
                    baseName = $"{ownerName} › {baseName} (孤)";
                }
                initial[kv.Key] = baseName;
            }
            // 检测重名,加 hash 后缀
            var nameCounts = new Dictionary<string, int>();
            foreach (var v in initial.Values)
                nameCounts[v] = nameCounts.GetValueOrDefault(v) + 1;
            var final = new Dictionary<string, string>();
            foreach (var kv in initial)
            {
                if (nameCounts[kv.Value] > 1)
                {
                    // port id 前 4 位足够区分 —— 蓝图持久化路径下 id 是 hash 前缀,DataPort ctor 路径下是字段名
                    string idHint = kv.Key.Length >= 4 ? kv.Key[..4] : kv.Key;
                    final[kv.Key] = $"{kv.Value}[{idHint}]";
                }
                else
                {
                    final[kv.Key] = kv.Value;
                }
            }
            return final;
#else
            return initial;
#endif
        }

        // 扫 NodeMetadata 一次给所有 feature 算去重后的 display name。
        // ChartFeature 自带 InstanceId 后缀(LineSeries#3F2A),通常已唯一;
        // 但 DataSource 没 InstanceId,多实例同 type 时 NodeMetadata 里就是 N 个同名条目。
        // 检测同名 ≥ 2 的,统一加 [F_xxxx] 后 4 位 hash 消歧。
        private Dictionary<string, string> BuildFeatureNameMap()
        {
            var map = new Dictionary<string, string>();
#if DEBUG
            if (_tracer == null) return map;

            // 第一遍:按 ShortenWriterName 计数
            var nameCounts = new Dictionary<string, int>();
            foreach (var kv in _tracer.NodeMetadata)
            {
                if (kv.Value.Tier != 2) continue;
                var n = ShortenWriterName(kv.Value.Name);
                nameCounts[n] = nameCounts.GetValueOrDefault(n) + 1;
            }
            // 第二遍:同名 ≥ 2 的加 hash 后缀
            foreach (var kv in _tracer.NodeMetadata)
            {
                if (kv.Value.Tier != 2) continue;
                var shortened = ShortenWriterName(kv.Value.Name);
                if (nameCounts[shortened] > 1)
                {
                    // F_<hash> id → 取最后 4 位
                    string idSuffix = kv.Key.Length >= 4 ? kv.Key[^4..] : kv.Key;
                    map[kv.Key] = $"{shortened}[{idSuffix}]";
                }
                else
                {
                    map[kv.Key] = shortened;
                }
            }
#endif
            return map;
        }

        // 把"机器友好"的 port id / displayName 翻成"人类友好":
        //   "91a5a79d_Latest"                                  → "Latest"
        //   "91a5a79d_Latest_3F2A1B7C"  (DataPort ctor 加 GUID 后缀)→ "Latest"
        //   "VP_LogicalLength"                                 → "Viewport.LogicalLength"
        //   "Plot.scatter_amount_ratio.scatter_amount_ratio"   → "Plot › scatter_amount_ratio"(同名只显一次)
        //   "F_xxxxx"                                          → 原样(feature 节点,NodeMetadata 通常已给 Name)
        //   "OutputPort_3F2A1B7C"                              → "OutputPort"
        private static string FriendlyPortName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;

            // VP_X → Viewport.X
            if (raw.StartsWith("VP_", StringComparison.Ordinal))
                return "Viewport." + raw.Substring(3);

            // Plot.A.B —— A == B 时合并 (PlotFeature 单 spec 同名场景,如 "Plot.scatter.scatter")
            if (raw.StartsWith("Plot.", StringComparison.Ordinal))
            {
                var parts = raw.Split('.');
                if (parts.Length == 3 && parts[1] == parts[2]) return $"Plot › {parts[1]}";
                if (parts.Length >= 3) return $"Plot › {parts[1]}.{string.Join('.', parts, 2, parts.Length - 2)}";
                return raw;
            }

            // 剥末尾 GUID 后缀:_XXXXXXXX(8 hex)
            int lastUnderscore = raw.LastIndexOf('_');
            if (lastUnderscore > 0 && raw.Length - lastUnderscore - 1 == 8 && IsHex(raw, lastUnderscore + 1, 8))
                raw = raw.Substring(0, lastUnderscore);

            // 剥前缀 hex_(8 hex + 下划线):蓝图持久化路径下 GraphSerializer 给端口加 nodeId hash 前缀
            if (raw.Length > 9 && raw[8] == '_' && IsHex(raw, 0, 8))
                raw = raw.Substring(9);

            return raw;
        }

        private static bool IsHex(string s, int start, int len)
        {
            for (int i = start; i < start + len; i++)
            {
                char c = s[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            }
            return true;
        }

        // ==========================================
        // 💥 数据流可视化辅助
        // ==========================================

        // 末端箭头 — 设计要求:
        //   1. tip 从 p4 向 from 方向回退 2px,不压在节点边框上(原版直接 tip = p4 看起来糊在边上)
        //   2. 长宽比 2:1 而不是接近 1:1 等边三角形(等边看着是方块)
        //   3. 写连线 = 实心三角(▶);读连线 = 空心 chevron(>),只画两笔不填充
        //   4. chevron 用 round line cap + round join,边缘平滑
        private static void DrawArrow(DrawingContext dc, Point tip, Point from, Brush fill, double size, bool hollow)
        {
            double dx = tip.X - from.X, dy = tip.Y - from.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.5) return;
            double ux = dx / len, uy = dy / len;
            double px = -uy, py = ux; // 90° 法线

            // tip 拉回 2px,跟节点边留出小空隙
            const double tipPullback = 2.0;
            Point arrowTip = new Point(tip.X - ux * tipPullback, tip.Y - uy * tipPullback);

            // 2:1 长宽比 — base 距 tip 沿轴向 size,两侧法向各偏 size*0.4
            double half = size * 0.4;
            Point baseLeft = new Point(arrowTip.X - ux * size + px * half, arrowTip.Y - uy * size + py * half);
            Point baseRight = new Point(arrowTip.X - ux * size - px * half, arrowTip.Y - uy * size - py * half);

            if (hollow)
            {
                // 读箭头:chevron 风格,base→tip 两笔,无填充
                var stroke = new Pen(fill, 1.6)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                };
                stroke.Freeze();
                var geo = new StreamGeometry();
                using (var c = geo.Open())
                {
                    c.BeginFigure(baseLeft, false, false);
                    c.LineTo(arrowTip, true, false);
                    c.LineTo(baseRight, true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(null, stroke, geo);
            }
            else
            {
                // 写箭头:实心三角,顺时针 tip→right→left
                var geo = new StreamGeometry();
                using (var c = geo.Open())
                {
                    c.BeginFigure(arrowTip, true, true);
                    c.LineTo(baseRight, true, false);
                    c.LineTo(baseLeft, true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(fill, null, geo);
            }
        }

        // Bezier 中点处画 命中速率 + 累计数 — 选中节点时所有相关连线浮出。
        // 关键可读性细节:
        //  - 文字一律白色,不跟着 heat 颜色变 dim blue(原来低 heat 时 dim blue 字 + 深色 pill 几乎看不见)
        //  - pill bg 加大到 240 alpha + 12px 横向 padding + pipe 颜色边框,远观也能跟它对应的连线对得上
        //  - 字号 12 而非 11,更易读
        private void DrawLinkHitCount(DrawingContext dc, Point p1, Point p4, double cpX, TopoLink link, Brush color)
        {
            Point cp1 = new Point(p1.X + cpX, p1.Y);
            Point cp2 = new Point(p4.X - cpX, p4.Y);
            const double t = 0.5, u = 0.5;
            double mx = u * u * u * p1.X + 3 * u * u * t * cp1.X + 3 * u * t * t * cp2.X + t * t * t * p4.X;
            double my = u * u * u * p1.Y + 3 * u * u * t * cp1.Y + 3 * u * t * t * cp2.Y + t * t * t * p4.Y;

            string text;
            if (link.RecentRate >= 1)
            {
                // 流速 ≥ 1/s → 显示 "23/s · 1.2k" 双指标格式
                int rateInt = (int)Math.Round(link.RecentRate);
                text = $"{HumanCount(rateInt)}/s · {HumanCount(link.LastHitCount)}";
            }
            else
            {
                // 流速极低或停了 → 只显示累计总数
                text = HumanCount(link.LastHitCount);
            }

            var ft = new FormattedText(text,
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _font, 12, Brushes.White, 1.0);
            double pillW = ft.Width + 14;
            double pillH = ft.Height + 6;
            var pillBg = new SolidColorBrush(Color.FromArgb(240, 12, 16, 22));
            pillBg.Freeze();
            var borderPen = new Pen(color, 1.5);
            borderPen.Freeze();
            dc.DrawRoundedRectangle(pillBg, borderPen,
                new Rect(mx - pillW / 2, my - pillH / 2, pillW, pillH), 4, 4);
            dc.DrawText(ft, new Point(mx - ft.Width / 2, my - ft.Height / 2));
        }

        private static string HumanCount(int n)
        {
            if (n < 1000) return n.ToString();
            if (n < 10_000) return (n / 1000.0).ToString("F1") + "k";
            if (n < 1_000_000) return (n / 1000) + "k";
            return (n / 1_000_000.0).ToString("F1") + "M";
        }

        private double ComputePulseStrength(string nodeId, long nowTicks)
        {
            if (_tracer == null) return 0;
#if DEBUG
            // 💥 TopologyTracer.LastHitTicks 仅 DEBUG 编译声明,Release 永远拿不到 hit → 直接返回 0,
            //    脉冲特效消失,符合"DevTools 在 Release 上是 no-op"的整体语义。
            if (!_tracer.LastHitTicks.TryGetValue(nodeId, out long lastTicks)) return 0;
            double elapsedSec = (nowTicks - lastTicks) / (double)Stopwatch.Frequency;
            if (elapsedSec >= PulseDurationSec) return 0;
            return 1 - elapsedSec / PulseDurationSec;
#else
            return 0;
#endif
        }

        // Tier 名称映射 — 数字 tier 对人不友好。
        private static string TierLabel(int tier) => tier switch
        {
            0 => "源 / 交互",
            1 => "引脚",
            2 => "特征",
            _ => $"Tier {tier}",
        };

        private void DrawTierHeaders(DrawingContext dc)
        {
            // 跟 LayoutNodes 用同一份 tier 切片,保证表头列与节点列严格对齐。
            var tierKeys = _nodes.GroupBy(n => n.Tier).OrderBy(g => g.Key).Select(g => g.Key).ToList();
            if (tierKeys.Count == 0) return;

            double colW = ActualWidth / tierKeys.Count;
            var headerBrush = Brushes.LightSteelBlue;
            for (int i = 0; i < tierKeys.Count; i++)
            {
                var ft = new FormattedText(
                    TierLabel(tierKeys[i]),
                    CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    _font, 12, headerBrush, 1.0);
                double cx = (i + 0.5) * colW;
                dc.DrawText(ft, new Point(cx - ft.Width / 2, 6));
            }

            // 表头下分隔线
            var sepPen = new Pen(new SolidColorBrush(Color.FromArgb(60, 180, 200, 220)), 1);
            sepPen.Freeze();
            dc.DrawLine(sepPen, new Point(0, HeaderHeight - 1), new Point(ActualWidth, HeaderHeight - 1));
        }

        private void DrawLegend(DrawingContext dc)
        {
            // 卡片定位在右下,不挡节点。
            //   3 行 heat 颜色 + 1 行环路指示器,让琥珀色的 halo 不再是"未知含义".
            const double pad = 10;
            const double lineH = 16;
            const double dotR = 4;
            string[] labels = { "冷 (低命中)", "温 (常驻)", "热 (高频)", "环路依赖 (合法)" };
            Brush[] dots = {
                new SolidColorBrush(Color.FromArgb(180, 100, 150, 255)),
                Brushes.Cyan,
                Brushes.Tomato,
                new SolidColorBrush(Color.FromArgb(220, 255, 180, 90))
            };
            foreach (var b in dots) if (b is SolidColorBrush sb && sb.CanFreeze) sb.Freeze();

            double cardW = 130;
            double cardH = lineH * labels.Length + pad;
            double cardX = ActualWidth - cardW - pad;
            double cardY = ActualHeight - cardH - pad;

            var bg = new SolidColorBrush(Color.FromArgb(180, 24, 26, 30));
            bg.Freeze();
            var border = new Pen(new SolidColorBrush(Color.FromArgb(80, 180, 200, 220)), 1);
            border.Freeze();
            dc.DrawRoundedRectangle(bg, border, new Rect(cardX, cardY, cardW, cardH), 4, 4);

            for (int i = 0; i < labels.Length; i++)
            {
                double rowY = cardY + pad / 2 + i * lineH + lineH / 2;
                dc.DrawEllipse(dots[i], null, new Point(cardX + pad, rowY), dotR, dotR);
                var ft = new FormattedText(labels[i], CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    _font, 11, Brushes.LightGray, 1.0);
                dc.DrawText(ft, new Point(cardX + pad + dotR + 6, rowY - ft.Height / 2));
            }
        }

        private void DrawFlowParticle(DrawingContext dc, Point p1, Point p4, double cpX, double offset, double heat, bool isTargeted)
        {
            Point p2 = new Point(p1.X + cpX, p1.Y);
            Point p3 = new Point(p4.X - cpX, p4.Y);
            double[] offsets = { offset, (offset + 0.33) % 1.0, (offset + 0.66) % 1.0 };

            Brush particleBrush = heat > 0.8 ? Brushes.OrangeRed : (isTargeted ? Brushes.White : Brushes.LightCyan);
            double radius = isTargeted ? 4.5 : 2.0 + (heat * 2.0); // 选中时粒子变大发白光

            foreach (var t in offsets)
            {
                double u = 1 - t;
                double x = u * u * u * p1.X + 3 * u * u * t * p2.X + 3 * u * t * t * p3.X + t * t * t * p4.X;
                double y = u * u * u * p1.Y + 3 * u * u * t * p2.Y + 3 * u * t * t * p3.Y + t * t * t * p4.Y;
                dc.DrawEllipse(particleBrush, null, new Point(x, y), radius, radius);
            }
        }

        private void LayoutNodes()
        {
            var tiers = _nodes.GroupBy(n => n.Tier).OrderBy(g => g.Key).ToList();
            if (tiers.Count == 0) return;

            // 减掉表头/时间线/图例占的高度,节点只在 [HeaderHeight, ActualHeight - TimelineHeight - LegendHeight] 之间分布。
            double availTop = HeaderHeight;
            double availH = ActualHeight - HeaderHeight - TimelineHeight - LegendHeight;
            if (availH < 60) availH = ActualHeight; // 极小高度兜底:让出一切给节点

            double colW = ActualWidth / tiers.Count;
            for (int i = 0; i < tiers.Count; i++)
            {
                // 只对未 pin 的节点重新分配位置,被用户拖动过的节点保持其 Bounds。
                var colNodes = tiers[i].Where(n => !n.IsPinned).ToList();
                if (colNodes.Count == 0) continue;
                double x = (i + 0.5) * colW;
                double stepY = availH / (colNodes.Count + 1);
                for (int j = 0; j < colNodes.Count; j++)
                {
                    colNodes[j].Bounds = new Rect(x - 70, availTop + (j + 1) * stepY - 15, 140, 30);
                }
            }
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _visual;
    }

    public class TopoNode
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public int Tier { get; set; }
        public Rect Bounds { get; set; }

        // 用户拖动后置 true,LayoutNodes 不再覆盖它的位置;右键单击节点解除。
        public bool IsPinned { get; set; }
    }
    public class TopoLink
    {
        public string Src { get; set; } = "";
        public string Tgt { get; set; } = "";
        public int HitCount { get; set; }
        public int LastHitCount { get; set; }
        public int LastDelta { get; set; }
        public double CurrentHeat { get; set; }
        public double ParticleOffset { get; set; }

        // 速率追踪 — 累计 hit 数对人没用(看不出"现在还在不在动"),
        // 用 Stopwatch 间隔算实时 hits/sec 并 EMA 平滑,UI 可显示"23/s"反映当前流速。
        public long LastSampleTicks { get; set; }
        public double RecentRate { get; set; }
    }
}
