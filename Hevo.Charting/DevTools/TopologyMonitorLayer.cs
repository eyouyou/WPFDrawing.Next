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

        public TopologyInspectorControl(ChartCell cell)
        {
            this.Focusable = true;
            this.Cursor = Cursors.Hand; // 提示可点击
            this.Background = Brushes.Transparent;
            this.AddVisualChild(_visual);

#if DEBUG
            _tracer = TracerRegistry.Get(cell.Template);

            var staticNodes = TopologyScanner.Scan(cell);
            _nodes = staticNodes;
#endif
            CompositionTarget.Rendering += (s, e) =>
            {
#if DEBUG
                if (_tracer != null)
                {
                    var (dynNodes, dynLinks) = _tracer.DumpTopology();
                    MergeDynamicBlood(dynNodes, dynLinks);
                }
#endif
                Render();
            };
        }

        // Ctrl+E 把当前拓扑(节点+连线+命中数+平均成本)拷到剪贴板 + 写到 Debug 输出,方便贴 issue / diff。
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
#if DEBUG
            if (_tracer != null && e.Key == Key.E && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                var json = _tracer.DumpJson();
                try { Clipboard.SetText(json); } catch { /* clipboard 偶发被独占,忽略 */ }
                Debug.WriteLine("=== Topology Snapshot (also copied to clipboard) ===");
                Debug.WriteLine(json);
                e.Handled = true;
            }
#endif
        }

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

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(this);
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
                    newY = Math.Clamp(newY, HeaderHeight, Math.Max(HeaderHeight, ActualHeight - LegendHeight - _dragNode.Bounds.Height));
                    _dragNode.Bounds = new Rect(newX, newY, _dragNode.Bounds.Width, _dragNode.Bounds.Height);
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (IsMouseCaptured) ReleaseMouseCapture();

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

        // 顶部留出给 Tier 表头,底部留出给颜色图例。
        // LayoutNodes 内的节点垂直分布也按这个工作区收缩,避免节点压在表头/图例上。
        // LegendHeight 必须 ≥ DrawLegend 实际占用(4 行 × 16 + pad 10 = 74),否则节点跟图例重叠。
        private const double HeaderHeight = 28.0;
        private const double LegendHeight = 90.0;

        private void Render()
        {
            if (ActualWidth < 10) return;

            using var dc = _visual.RenderOpen();
            dc.DrawRectangle(_bgBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));

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

                var ft = new FormattedText(node.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _font, 12, isSelected ? Brushes.White : Brushes.LightGray, 1.0);
                dc.DrawText(ft, new Point(node.Bounds.Left + 10, node.Bounds.Top + (node.Bounds.Height - ft.Height) / 2));

                // Per-feature 性能徽章:tier 2 + 有采样数据时,右上角小字显示平均 OnProject 耗时。
                // 颜色阈值: <0.3ms 海绿(健康) / <1ms 黄色(关注) / >=1ms 橘红(瓶颈嫌疑)
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

                dc.Pop();
            }

            // 颜色图例(右下角小卡片) — 让 heat 颜色含义可被新人一眼读懂。
            DrawLegend(dc);
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
            if (!_tracer.LastHitTicks.TryGetValue(nodeId, out long lastTicks)) return 0;
            double elapsedSec = (nowTicks - lastTicks) / (double)Stopwatch.Frequency;
            if (elapsedSec >= PulseDurationSec) return 0;
            return 1 - elapsedSec / PulseDurationSec;
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

            // 减掉表头/图例占的高度,节点只在 [HeaderHeight, ActualHeight - LegendHeight] 之间分布。
            double availTop = HeaderHeight;
            double availH = ActualHeight - HeaderHeight - LegendHeight;
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
