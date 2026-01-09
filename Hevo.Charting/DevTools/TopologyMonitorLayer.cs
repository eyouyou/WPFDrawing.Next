using Hevo.Charting.Core;
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

        private void MergeDynamicBlood(List<TopoNode> dynNodes, List<TopoLink> dynLinks)
        {
            foreach (var dn in dynNodes)
                if (!_nodes.Any(n => n.Id == dn.Id)) _nodes.Add(dn);

            foreach (var dLink in dynLinks)
            {
                var existing = _links.FirstOrDefault(l => l.Src == dLink.Src && l.Tgt == dLink.Tgt);
                if (existing == null)
                {
                    existing = new TopoLink { Src = dLink.Src, Tgt = dLink.Tgt, CurrentHeat = 0.5 };
                    _links.Add(existing);
                }
                existing.LastDelta = dLink.HitCount - existing.LastHitCount;
                existing.LastHitCount = dLink.HitCount;
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
        }

        // ==========================================
        // 💥 交互核心：点击锁定与取消
        // ==========================================
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(this);
            var clickedNode = _nodes.FirstOrDefault(n => n.Bounds.Contains(pos));

            if (clickedNode != null)
            {
                // 如果点的是已经选中的，就取消选中；否则切换选中
                _selectedId = _selectedId == clickedNode.Id ? null : clickedNode.Id;
            }
            else
            {
                // 点击空白处，清空选中状态
                _selectedId = null;
            }
            base.OnMouseLeftButtonDown(e);
        }

        private void Render()
        {
            if (ActualWidth < 10 || _nodes.Count == 0) return;
            LayoutNodes();

            using var dc = _visual.RenderOpen();
            dc.DrawRectangle(_bgBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));

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

                // 💥 渲染时压低无关管线的透明度
                dc.PushOpacity(isDimmed ? 0.1 : 1.0);
                dc.DrawGeometry(null, pipePen, geo);

                // 如果处于暗淡状态，就不画粒子了，避免视觉杂乱
                if (!isDimmed)
                {
                    DrawFlowParticle(dc, p1, p4, cpX, link.ParticleOffset, heat, isTargeted);
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

                Brush borderBrush = isSelected ? Brushes.Cyan : Brushes.DimGray;
                double borderThickness = isSelected ? 2.0 : 1.2;

                dc.DrawRoundedRectangle(isSelected ? Brushes.DarkSlateBlue : _nodeBrush,
                    new Pen(borderBrush, borderThickness), node.Bounds, 4, 4);

                var ft = new FormattedText(node.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _font, 12, isSelected ? Brushes.White : Brushes.LightGray, 1.0);
                dc.DrawText(ft, new Point(node.Bounds.Left + 10, node.Bounds.Top + (node.Bounds.Height - ft.Height) / 2));

                dc.Pop();
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

            double colW = ActualWidth / tiers.Count;
            for (int i = 0; i < tiers.Count; i++)
            {
                var colNodes = tiers[i].ToList();
                double x = (i + 0.5) * colW;
                double stepY = ActualHeight / (colNodes.Count + 1);
                for (int j = 0; j < colNodes.Count; j++)
                {
                    colNodes[j].Bounds = new Rect(x - 70, (j + 1) * stepY - 15, 140, 30);
                }
            }
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _visual;
    }

    public class TopoNode { public string Id { get; set; } = ""; public string Label { get; set; } = ""; public int Tier { get; set; } public Rect Bounds { get; set; } }
    public class TopoLink
    {
        public string Src { get; set; } = "";
        public string Tgt { get; set; } = "";
        public int HitCount { get; set; }
        public int LastHitCount { get; set; }
        public int LastDelta { get; set; }
        public double CurrentHeat { get; set; }
        public double ParticleOffset { get; set; }
    }
}
