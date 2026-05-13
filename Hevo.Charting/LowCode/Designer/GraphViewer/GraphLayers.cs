using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using System.Numerics;
using System.Windows.Media;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// GraphViewer 调色板。统一在此处注册画刷/画笔常量,
    /// 业务想换皮直接改这里——layer 不需要改一行。
    /// </summary>
    internal static class GraphPalette
    {
        public static readonly IHevoBrush CanvasBg     = new HevoSolidBrush(Color.FromRgb(0x1E, 0x1F, 0x22));
        public static readonly IHevoBrush GridBrush    = new HevoSolidBrush(Color.FromArgb(0x40, 0x55, 0x55, 0x55));

        public static readonly IHevoBrush NodeFillSrc  = new HevoSolidBrush(Color.FromRgb(0x2A, 0x44, 0x5C));
        public static readonly IHevoBrush NodeFillTrait = new HevoSolidBrush(Color.FromRgb(0x4C, 0x3D, 0x55));
        public static readonly IHevoBrush NodeFillFeat = new HevoSolidBrush(Color.FromRgb(0x35, 0x40, 0x35));

        public static readonly HevoPen NodeBorder      = new(new HevoSolidBrush(Color.FromRgb(0x78, 0x84, 0x90)), 1.0);
        public static readonly HevoPen NodeBorderSel   = new(new HevoSolidBrush(Color.FromRgb(0xFF, 0xC1, 0x07)), 2.0);

        public static readonly IHevoBrush HeaderFill   = new HevoSolidBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
        public static readonly IHevoBrush TitleText    = new HevoSolidBrush(Colors.White);
        public static readonly IHevoBrush PortText     = new HevoSolidBrush(Color.FromRgb(0xC0, 0xC8, 0xD0));

        public static readonly IHevoBrush PortInput    = new HevoSolidBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
        public static readonly IHevoBrush PortOutput   = new HevoSolidBrush(Color.FromRgb(0xFF, 0xB7, 0x4D));
        public static readonly HevoPen   PortBorder    = new(new HevoSolidBrush(Color.FromRgb(0x10, 0x10, 0x10)), 1.0);

        // Edge 现在画在 Node 之上,用更细的线 + 略高的对比度,既不过分压制节点视觉,又保证可见性。
        public static readonly HevoPen EdgePen         = new(new HevoSolidBrush(Color.FromRgb(0xCD, 0xD8, 0xE0)), 1.5);
        // Edge 描边(衬底):稍粗一点的深色半透明笔,保证经过浅色节点头部时仍清晰可见。
        public static readonly HevoPen EdgeOutlinePen  = new(new HevoSolidBrush(Color.FromArgb(0xB0, 0x10, 0x14, 0x1A)), 3.0);
        public static readonly HevoPen RubberValid     = new(new HevoSolidBrush(Color.FromRgb(0x66, 0xBB, 0x6A)), 2.0);
        public static readonly HevoPen RubberInvalid   = new(new HevoSolidBrush(Color.FromRgb(0xEF, 0x53, 0x50)), 2.0,
                                                              DashArray: new double[] { 4, 3 });

        public static readonly HevoPen   BoxSelectPen  = new(new HevoSolidBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)), 1.0,
                                                              DashArray: new double[] { 3, 3 });
        public static readonly IHevoBrush BoxSelectFill = new HevoSolidBrush(Color.FromArgb(0x33, 0x4F, 0xC3, 0xF7));

        public static readonly IHevoBrush  TooltipBg     = new HevoSolidBrush(Color.FromArgb(0xF0, 0x1A, 0x1B, 0x1F));
        public static readonly HevoPen     TooltipBorder = new(new HevoSolidBrush(Color.FromRgb(0x55, 0x5C, 0x66)), 1.0);
        public static readonly IHevoBrush  TooltipText   = new HevoSolidBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
        public static readonly IHevoBrush  TooltipDimText = new HevoSolidBrush(Color.FromRgb(0x9E, 0xA8, 0xB0));

        // 拖动节点的对齐辅助线:鲜艳青色 + 虚线,1px,跟节点边框拉开识别度。
        public static readonly HevoPen AlignGuidePen = new(new HevoSolidBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)), 1.0,
                                                            DashArray: new double[] { 4, 3 });

        // Minimap:背景半透明深色 + 边框,内部节点用低饱和填充。
        public static readonly IHevoBrush MinimapBg     = new HevoSolidBrush(Color.FromArgb(0xCC, 0x14, 0x16, 0x1B));
        public static readonly HevoPen    MinimapBorder = new(new HevoSolidBrush(Color.FromRgb(0x55, 0x5C, 0x66)), 1.0);
        public static readonly IHevoBrush MinimapNodeFill = new HevoSolidBrush(Color.FromArgb(0xAA, 0x90, 0xA4, 0xAE));
        public static readonly HevoPen    MinimapViewport = new(new HevoSolidBrush(Color.FromRgb(0xFF, 0xC1, 0x07)), 1.5);

        public static readonly IHevoTypeface Typeface = new HevoTypeface("Segoe UI", FontWeight: 500);
    }

    /// <summary>
    /// 画布背景层:纯色底 + 缩放感知的网格。
    /// 仅监听 GraphRenderTrait;CanvasTransform 变化(平移/缩放)时引擎引用判脏触发重绘。
    /// </summary>
    internal sealed class GraphCanvasLayer : ChartLayer
    {
        public GraphCanvasLayer()
        {
            Name = "GraphCanvasLayer";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Background;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var t = data.Get<GraphRenderTrait>();
            var size = data.Get<ViewportSizeTrait>();
            if (t == null || size == null) return;

            float w = (float)size.Width;
            float h = (float)size.Height;
            draw.DrawRectangle(GraphPalette.CanvasBg, null, new HevoRect(0, 0, w, h));

            var tr = t.State.Transform;

            // 1) 类别泳道 —— 节点 Y 范围聚合成横幅 (画布坐标 → 屏幕坐标)。
            // 横向铺满当前画布,Y 范围 = 该 category 所有节点的 [minY, maxY+H] 合并。
            // 业务自由拖动节点 → 泳道实时跟随;空 category 不渲染。
            var bands = ComputeCategoryBands(t.State);
            foreach (var (category, top, bottom) in bands)
            {
                float screenTop    = top    * tr.Scale + tr.OffsetY;
                float screenBottom = bottom * tr.Scale + tr.OffsetY;
                if (screenBottom < 0 || screenTop > h) continue;
                screenTop    = Math.Max(screenTop,    0);
                screenBottom = Math.Min(screenBottom, h);
                var fill = new HevoSolidBrush(FeatureCategoryStyle.ZoneFill(category));
                draw.DrawRectangle(fill, null, new HevoRect(0, screenTop, w, screenBottom - screenTop));

                // 类别标签贴左上角
                var labelBrush = new HevoSolidBrush(FeatureCategoryStyle.ZoneLabel(category));
                draw.DrawText(FeatureCategoryStyle.DisplayName(category), GraphPalette.Typeface,
                    labelBrush, 11f,
                    new HevoPoint(8f, screenTop + 4f),
                    TextAlignX.Left, TextAlignY.Top);
            }

            // 2) 网格:间距随 zoom 扩缩,屏幕步长保持在 [16, 64] 内
            float baseStep = 50f;
            float screenStep = baseStep * tr.Scale;
            while (screenStep < 16f) screenStep *= 2f;
            while (screenStep > 64f) screenStep /= 2f;

            float ox = ((tr.OffsetX % screenStep) + screenStep) % screenStep;
            float oy = ((tr.OffsetY % screenStep) + screenStep) % screenStep;

            for (float x = ox; x < w; x += screenStep)
                draw.DrawLine(new HevoPen(GraphPalette.GridBrush, 1.0, IsAntialias: false),
                              new HevoPoint(x, 0), new HevoPoint(x, h));
            for (float y = oy; y < h; y += screenStep)
                draw.DrawLine(new HevoPen(GraphPalette.GridBrush, 1.0, IsAntialias: false),
                              new HevoPoint(0, y), new HevoPoint(w, y));
        }

        /// <summary>
        /// 把当前画布上同 category 的节点聚成 Y 范围带子。带子之间用一点 padding,UI 看起来不挤。
        /// 每个 category 的 band 范围 = min(node.Y) - pad ~ max(node.Y + node.Height) + pad。
        /// </summary>
        private static IEnumerable<(FeatureCategory category, float top, float bottom)> ComputeCategoryBands(GraphState state)
        {
            const float pad = 14f;
            var groups = new Dictionary<FeatureCategory, (float min, float max)>();
            foreach (var n in state.Nodes)
            {
                if (n.Category == FeatureCategory.Unknown) continue;
                if (!groups.TryGetValue(n.Category, out var range))
                {
                    groups[n.Category] = (n.Position.Y - pad, n.Position.Y + n.Size.Y + pad);
                }
                else
                {
                    groups[n.Category] = (
                        Math.Min(range.min, n.Position.Y - pad),
                        Math.Max(range.max, n.Position.Y + n.Size.Y + pad));
                }
            }
            foreach (var kv in groups)
                yield return (kv.Key, kv.Value.min, kv.Value.max);
        }
    }

    /// <summary>
    /// 节点层:渲染所有节点 (圆角矩形 + 标题栏 + 端口圆点 + 端口标签)。
    /// 全程 PushTransform 一次,后续按画布坐标系直接画——把 pan/zoom 交给底层渲染器。
    /// </summary>
    internal sealed class GraphNodeLayer : ChartLayer
    {
        public GraphNodeLayer()
        {
            Name = "GraphNodeLayer";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Main;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var t = data.Get<GraphRenderTrait>();
            if (t == null) return;
            var s = t.State;
            var tr = s.Transform;
            var matrix = new Matrix3x2(tr.Scale, 0, 0, tr.Scale, tr.OffsetX, tr.OffsetY);
            using var _ = draw.PushTransform(matrix);

            foreach (var node in s.Nodes)
            {
                var bounds = node.GetBounds();
                var fill = node.Kind switch
                {
                    NodeKind.DataSource => GraphPalette.NodeFillSrc,
                    NodeKind.Trait => GraphPalette.NodeFillTrait,
                    _ => GraphPalette.NodeFillFeat,
                };
                var border = s.SelectedNodeIds.Contains(node.Id) ? GraphPalette.NodeBorderSel : GraphPalette.NodeBorder;
                draw.DrawRoundedRectangle(fill, border, bounds, 6f, 6f);

                // 标题栏底色
                var headerRect = new HevoRect(bounds.X, bounds.Y, bounds.Width, 24f);
                draw.DrawRectangle(GraphPalette.HeaderFill, null, headerRect);

                // 标题文字
                draw.DrawText(node.Title, GraphPalette.Typeface, GraphPalette.TitleText, 12f,
                              new HevoPoint(bounds.X + 8f, bounds.Y + 4f),
                              TextAlignX.Left, TextAlignY.Top);

                DrawPorts(draw, node, isInput: true);
                DrawPorts(draw, node, isInput: false);
            }
        }

        private static void DrawPorts(IDrawingSink draw, Node node, bool isInput)
        {
            var ports = isInput ? node.InputPorts : node.OutputPorts;
            var fill = isInput ? GraphPalette.PortInput : GraphPalette.PortOutput;
            for (int i = 0; i < ports.Count; i++)
            {
                var port = ports[i];
                var c = node.GetPortPosition(port);
                if (port.IsArray)
                {
                    // 扇入端口画方块,跟单端口圆点拉开视觉差。
                    draw.DrawRectangle(fill, GraphPalette.PortBorder,
                        new HevoRect(c.X - 5f, c.Y - 5f, 10f, 10f));
                }
                else
                {
                    draw.DrawEllipse(fill, GraphPalette.PortBorder, c, 5f, 5f);
                }

                var labelX = isInput ? c.X + 10f : c.X - 10f;
                var alignX = isInput ? TextAlignX.Left : TextAlignX.Right;
                var label  = port.IsArray ? $"{port.Name} []" : port.Name;
                draw.DrawText(label, GraphPalette.Typeface, GraphPalette.PortText, 10f,
                              new HevoPoint(labelX, c.Y),
                              alignX, TextAlignY.Center);
            }
        }
    }

    /// <summary>
    /// 连线层:贝塞尔曲线连接 from-output 到 to-input。
    /// 用 Geometry SVG path,稳定 0-GC——cubic 控制点按节点距离半量水平偏移即可。
    /// </summary>
    internal sealed class GraphEdgeLayer : ChartLayer
    {
        public GraphEdgeLayer()
        {
            Name = "GraphEdgeLayer";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Main;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var t = data.Get<GraphRenderTrait>();
            if (t == null) return;
            var s = t.State;
            var tr = s.Transform;
            var matrix = new Matrix3x2(tr.Scale, 0, 0, tr.Scale, tr.OffsetX, tr.OffsetY);
            using var _ = draw.PushTransform(matrix);

            // 同 source-port 多次出线时,按出线序号微微纵向偏移,避免曲线 100% 重叠看起来像一根。
            // 缓存 (fromNodeId, fromPortId) → 已绘制次数。
            var siblingCounter = new Dictionary<(string, string), int>();
            foreach (var edge in s.Edges)
            {
                var from = s.FindNode(edge.FromNodeId);
                var to = s.FindNode(edge.ToNodeId);
                if (from == null || to == null) continue;
                var fp = from.FindPort(edge.FromPortId, isInput: false);
                var tp = to.FindPort(edge.ToPortId, isInput: true);
                if (fp == null || tp == null) continue;

                var p0 = from.GetPortPosition(fp);
                var p3 = to.GetPortPosition(tp);

                int siblingIdx = siblingCounter.GetValueOrDefault((edge.FromNodeId, edge.FromPortId), 0);
                siblingCounter[(edge.FromNodeId, edge.FromPortId)] = siblingIdx + 1;

                // 避障:扫所有非端点节点,挡道的把控制点 Y 推开。
                float vAvoid = ComputeAvoidanceVOffset(p0, p3, s.Nodes, edge.FromNodeId, edge.ToNodeId);

                // 双笔:深色描边在底层兜底"过节点也能看到",上层亮线表达连接关系。
                DrawBezier(draw, p0, p3, GraphPalette.EdgeOutlinePen, siblingIdx, vAvoid);
                DrawBezier(draw, p0, p3, GraphPalette.EdgePen,        siblingIdx, vAvoid);
            }
        }

        /// <summary>
        /// 直线连接 from→to 在中段是否被某个非端点节点 AABB 阻挡;返回需要施加的 Y 偏移
        /// (>0 = 把曲线腹部往下推,<0 = 往上推)。多障碍叠加取绝对值最大方向。
        /// 简化判定:1) 节点 AABB 在 X 方向跟边的 [minX, maxX] 重叠,
        ///         2) 节点中心 Y 与"端点 Y 线性插值在该 X 处的值"距离小于安全垫 → 算阻挡。
        /// </summary>
        private static float ComputeAvoidanceVOffset(
            HevoPoint p0, HevoPoint p3, IReadOnlyList<Node> nodes, string fromId, string toId)
        {
            float minX = Math.Min(p0.X, p3.X);
            float maxX = Math.Max(p0.X, p3.X);
            float dx = p3.X - p0.X;
            float pushUp = 0f, pushDown = 0f;

            foreach (var n in nodes)
            {
                if (n.Id == fromId || n.Id == toId) continue;
                var b = n.GetBounds();
                if (b.X + b.Width < minX || b.X > maxX) continue;

                // 直线在节点中心 X 处的预期 Y
                float nodeMidX = b.X + b.Width / 2f;
                float t = Math.Abs(dx) < 1e-3f ? 0.5f : Math.Clamp((nodeMidX - p0.X) / dx, 0f, 1f);
                float lineYAtNode = p0.Y + (p3.Y - p0.Y) * t;
                float nodeMidY = b.Y + b.Height / 2f;
                float halfH = b.Height / 2f + 16f;  // 16px 安全垫

                if (Math.Abs(lineYAtNode - nodeMidY) > halfH) continue;  // 不挡道

                // 决定推哪个方向:朝远离节点中心的方向。推力 = 把端点连线推到节点边外所需 Y。
                if (lineYAtNode <= nodeMidY)
                {
                    // 直线在节点中心或上方 → 把曲线往上推到节点上方
                    float need = (nodeMidY - halfH) - lineYAtNode;  // ≤0
                    if (need < pushUp) pushUp = need;
                }
                else
                {
                    float need = (nodeMidY + halfH) - lineYAtNode;  // ≥0
                    if (need > pushDown) pushDown = need;
                }
            }

            // 同时被上下挤的少见,先按"推力大的方向"走;限幅避免曲线腹部飞太远
            float deflection = Math.Abs(pushDown) >= Math.Abs(pushUp) ? pushDown : pushUp;
            return Math.Clamp(deflection, -180f, 180f);
        }

        // 走原生 <see cref="DrawingExtensions.DrawCubicBezier"/> —— WPF 用 StreamGeometry.BezierTo 渲染,
        // 比"自己采样成 N 段 polyline"既丝滑又便宜:N 段法在长边上每段都是直线,折角能用肉眼看见;
        // 原生 cubic bezier 走 GPU 抗锯齿曲线管线,任何长度都丝滑无折。
        //
        // <para>控制点策略:</para>
        // <list type="bullet">
        ///   <item>正向边 (b.X >= a.X):标准向右伸出 + 向左伸入,水平切线接入端口。</item>
        ///   <item>反向边 (b.X &lt; a.X):水平 dx 按节点距离反推,加大力度避免控制点穿过端点形成 S 太紧。</item>
        ///   <item>陡边 (|dy| > |dx|*1.5):dx 拉大避免视觉直角。</item>
        ///   <item><c>siblingIdx</c> ≥ 1:同一 source 的第 N 条边纵向偏移 ±8*N,防止彻底重叠。</item>
        ///   <item><c>vAvoid</c> ≠ 0:避障偏移,直接加到控制点 Y 上让曲线腹部绕开障碍节点。</item>
        /// </list>
        public static void DrawBezier(IDrawingSink draw, HevoPoint a, HevoPoint b, HevoPen pen, int siblingIdx = 0, float vAvoid = 0f)
        {
            float dxAbs = Math.Abs(b.X - a.X);
            float dyAbs = Math.Abs(b.Y - a.Y);
            float dx;
            if (b.X >= a.X)
            {
                dx = Math.Max(60f, dxAbs * 0.5f);
                if (dyAbs > dxAbs * 1.5f) dx = Math.Max(dx, dyAbs * 0.45f);
            }
            else
            {
                dx = Math.Max(120f, dyAbs * 0.6f + dxAbs * 0.5f);
            }

            // 同 source 的 sibling:从 0 开始,每多一条往下错 8px(交替正负避免单边堆积)
            float siblingOffset = siblingIdx == 0 ? 0f
                : ((siblingIdx % 2 == 1 ? 1 : -1) * ((siblingIdx + 1) / 2) * 8f);

            // 避障 + sibling 共同形成控制点 Y 偏移
            float vOff = siblingOffset + vAvoid;
            var c1 = new HevoPoint(a.X + dx, a.Y + vOff);
            var c2 = new HevoPoint(b.X - dx, b.Y + vOff);
            draw.DrawCubicBezier(pen, a, c1, c2, b);
        }
    }

    /// <summary>
    /// 选中高亮层:已选节点的外发光边框 + 框选橡皮筋。
    /// 跟节点层分开,频繁切换选中时只重画这一层。
    /// </summary>
    internal sealed class GraphSelectionLayer : ChartLayer
    {
        public GraphSelectionLayer()
        {
            Name = "GraphSelectionLayer";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Selection;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var t = data.Get<GraphRenderTrait>();
            if (t == null) return;
            var s = t.State;

            if (s.BoxSelection != null)
            {
                var rect = s.Transform.CanvasToScreen(s.BoxSelection.ToRect());
                draw.DrawRectangle(GraphPalette.BoxSelectFill, GraphPalette.BoxSelectPen, rect);
            }
        }
    }

    // GraphMinimapLayer 已合并到通用 Hevo.Charting.Layers.MinimapLayer ——
    // GraphSchema.PushTraitToLayers 自己负责把 GraphState 翻译成 MinimapTrait(ContentBounds + ContentItems + Viewport)。
    // 旧的 ComputeContentBounds 静态方法搬到 GraphSchema.ComputeNodesAabb,跟 ComputeMinimapMapping 共用。

    /// <summary>
    /// 交互预览层:橡皮筋拖出连线 + 端口悬停 tooltip。
    /// 落在 Interaction 槽位,单独一个 _overlayCanvas,跟主图 dirty 隔离 ——
    /// hover 这种高频事件不会触发主画布 (节点/连线) 的重画。
    /// </summary>
    internal sealed class GraphPreviewLayer : ChartLayer
    {
        public GraphPreviewLayer()
        {
            Name = "GraphPreviewLayer";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Interaction;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var t = data.Get<GraphRenderTrait>();
            var size = data.Get<ViewportSizeTrait>();
            if (t == null || size == null) return;
            var s = t.State;

            // 1. 橡皮筋 + 对齐辅助线(画布坐标系,需要 transform)
            if (s.RubberBand != null || s.AlignmentGuides != null)
            {
                var tr = s.Transform;
                var matrix = new Matrix3x2(tr.Scale, 0, 0, tr.Scale, tr.OffsetX, tr.OffsetY);
                using var _ = draw.PushTransform(matrix);

                if (s.RubberBand != null)
                {
                    var from = s.FindNode(s.RubberBand.FromNodeId);
                    if (from != null)
                    {
                        var fp = from.FindPort(s.RubberBand.FromPortId, isInput: false);
                        if (fp != null)
                        {
                            var p0 = from.GetPortPosition(fp);
                            var pen = s.RubberBand.IsValidTarget ? GraphPalette.RubberValid : GraphPalette.RubberInvalid;
                            GraphEdgeLayer.DrawBezier(draw, p0, s.RubberBand.MousePosCanvas, pen);
                        }
                    }
                }

                if (s.AlignmentGuides != null)
                {
                    // 把屏幕边界反算到画布坐标,作为辅助线的两端
                    float canvasLeft   = -tr.OffsetX / tr.Scale;
                    float canvasRight  = ((float)size.Width - tr.OffsetX) / tr.Scale;
                    float canvasTop    = -tr.OffsetY / tr.Scale;
                    float canvasBottom = ((float)size.Height - tr.OffsetY) / tr.Scale;
                    foreach (var x in s.AlignmentGuides.VerticalLines)
                        draw.DrawLine(GraphPalette.AlignGuidePen, new HevoPoint(x, canvasTop), new HevoPoint(x, canvasBottom));
                    foreach (var y in s.AlignmentGuides.HorizontalLines)
                        draw.DrawLine(GraphPalette.AlignGuidePen, new HevoPoint(canvasLeft, y), new HevoPoint(canvasRight, y));
                }
            }

            // 2. 端口悬停 tooltip(屏幕坐标系,无需 transform)
            if (s.HoveredPort != null)
            {
                var node = s.FindNode(s.HoveredPort.NodeId);
                var port = node?.FindPort(s.HoveredPort.PortId, s.HoveredPort.IsInput);
                if (node != null && port != null)
                {
                    DrawPortTooltip(draw, port, s.HoveredPort.AnchorScreen);
                }
            }
        }

        // 端口悬停浮窗:显示 [in/out] PortName : DataType + 描述行(可选)。
        // 排版用 DrawText 多次绘制(每行一次),底色用 DrawRectangle 兜底。
        // 绘制位置以 anchor (端口圆点屏幕坐标) 为锚,自动避免出画布。
        private static void DrawPortTooltip(IDrawingSink draw, Port port, HevoPoint anchor)
        {
            string head = port.IsInput ? "◀ Input  " : "▶ Output ";
            string line1 = head + port.Name;
            string line2 = port.DataTypeName;
            string? line3 = string.IsNullOrEmpty(port.Description) ? null : port.Description;

            const float pad = 6f, lineH = 14f, fontSize = 11f;
            // 估算宽度:取最长行 × 平均字宽
            int maxLen = Math.Max(Math.Max(line1.Length, line2.Length), line3?.Length ?? 0);
            float w = pad * 2 + maxLen * 6.4f;
            float h = pad * 2 + (line3 != null ? lineH * 3 : lineH * 2);

            // 锚点放在端口右上方;input 端口往左挪
            float ox = port.IsInput ? anchor.X - w - 12f : anchor.X + 12f;
            float oy = anchor.Y - h - 8f;
            if (oy < 4f) oy = anchor.Y + 12f; // 顶部不够就往下放

            var rect = new HevoRect(ox, oy, w, h);
            draw.DrawRoundedRectangle(GraphPalette.TooltipBg, GraphPalette.TooltipBorder, rect, 4f, 4f);

            float tx = rect.X + pad;
            float ty = rect.Y + pad;
            var headBrush = port.IsInput ? GraphPalette.PortInput : GraphPalette.PortOutput;
            draw.DrawText(line1, GraphPalette.Typeface, headBrush, fontSize,
                new HevoPoint(tx, ty), TextAlignX.Left, TextAlignY.Top);
            draw.DrawText(line2, GraphPalette.Typeface, GraphPalette.TooltipDimText, fontSize - 1f,
                new HevoPoint(tx, ty + lineH), TextAlignX.Left, TextAlignY.Top);
            if (line3 != null)
            {
                draw.DrawText(line3, GraphPalette.Typeface, GraphPalette.TooltipText, fontSize - 1f,
                    new HevoPoint(tx, ty + lineH * 2), TextAlignX.Left, TextAlignY.Top);
            }
        }
    }
}
