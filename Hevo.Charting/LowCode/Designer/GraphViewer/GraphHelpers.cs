using System.Windows;
using System.Windows.Input;
using Hevo.Charting.Core;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// 端口数据类型兼容性判定工具。原 GraphSchema.AreTypeNamesCompatible 静态方法搬出独立。
    /// 由 GraphPortLinkFeature(连线时校验)和 PortsCompatibleTests(纯单元测试)共用。
    /// </summary>
    public static class GraphTypeCompatibility
    {
        /// <summary>
        /// 端口数据类型兼容判定。规则:
        /// <list type="bullet">
        ///   <item><c>"object"</c> 通配 —— 任一端是 object,直接兼容(蓝图层多态兜底);</item>
        ///   <item>nullable 归一 —— 去掉末尾 <c>?</c>(nullable 是 .NET 编译期注解,运行期端口都是 boxed object,
        ///         <c>RealRange</c> 跟 <c>RealRange?</c> 应视为同一根线);</item>
        ///   <item>数组 fan-in 接收单源 —— 目标 <see cref="Port.IsArray"/> 时,允许源类型等于目标元素类型
        ///         (即 <c>T</c> 可拉到 <c>T[]</c>,因为 PortBindings 协议本就是数组聚合多源)。</item>
        /// </list>
        /// 严格 .NET 类型语义(协变 / 数值宽化 / generic 实参变换)未覆盖 —— 字符串相等已足以接住实际场景,
        /// 真踩到不兼容时由后端 GetOrCreatePort 的运行时严格化(§7.4.1)兜底拒掉。
        /// </summary>
        public static bool AreCompatible(string fromName, string toName, bool toIsArray)
        {
            if (fromName == "object" || toName == "object") return true;

            string from = StripNullableSuffix(fromName);
            string to   = StripNullableSuffix(toName);
            if (from == to) return true;

            // 数组 fan-in:目标 T[] 接受单源 T。toName 已包含 "[]" 后缀(由 NodeFactory 拼出),
            // 取掉后跟 from 比;同时 toIsArray 校验防止误把"巧合 string 末尾是 []"的标量当数组。
            if (toIsArray && to.EndsWith("[]") && StripNullableSuffix(to.Substring(0, to.Length - 2)) == from)
                return true;

            return false;
        }

        private static string StripNullableSuffix(string name)
            => name.EndsWith("?") ? name.Substring(0, name.Length - 1) : name;
    }

    /// <summary>
    /// graph 几何工具。原 GraphSchema.ComputeNodesAabb 静态方法搬出独立。
    /// 由交互 features(minimap 命中)和 GraphMinimapFeature(渲染)共用。
    /// </summary>
    public static class GraphGeometry
    {
        /// <summary>
        /// 计算节点列表的 AABB。从旧 GraphMinimapLayer.ComputeContentBounds 挪过来,
        /// minimap 命中映射 + 渲染都基于这一份。
        /// </summary>
        public static (float x, float y, float w, float h) ComputeNodesAabb(IReadOnlyList<Node> nodes)
        {
            if (nodes.Count == 0) return (0f, 0f, 1f, 1f);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var n in nodes)
            {
                var b = n.GetBounds();
                if (b.X < minX) minX = b.X;
                if (b.Y < minY) minY = b.Y;
                if (b.X + b.Width > maxX) maxX = b.X + b.Width;
                if (b.Y + b.Height > maxY) maxY = b.Y + b.Height;
            }
            return (minX, minY, Math.Max(1f, maxX - minX), Math.Max(1f, maxY - minY));
        }
    }

    /// <summary>
    /// Minimap 几何参数。<see cref="GraphMinimapFeature"/>(渲染)与 <see cref="GraphMinimapInteractionFeature"/>(命中)
    /// 共享同一份 —— GraphEditorSchema 在装配时把同一个实例 init 注入两边,改尺寸只动一处。
    /// </summary>
    /// <param name="Width">浮窗宽度(像素)。</param>
    /// <param name="Height">浮窗高度(像素)。</param>
    /// <param name="Margin">浮窗距 chart 右下角的边距。</param>
    /// <param name="Padding">浮窗内边距(content 区与边框间隙)。</param>
    public readonly record struct MinimapGeometry(float Width, float Height, float Margin, float Padding)
    {
        public static MinimapGeometry Default { get; } = new(200f, 140f, 12f, 6f);
    }

    /// <summary>
    /// 共享交互工具(命中、坐标转换、minimap 几何)。Phase 3.2 拆出 6 个交互 feature 后,
    /// 命中 / 几何 / 鼠标坐标提取这些纯函数都集中在这,避免每个 feature 复刻一份。
    /// </summary>
    internal static class GraphInteractionHelpers
    {
        public readonly record struct HitResult(Node? Node, Port? Port, bool IsInput);

        /// <summary>
        /// 画布坐标系下的命中检测。倒序遍历(后画的在上),先测端口再测节点本体。
        /// 返回 <c>(null, null, false)</c> 表示空白命中。
        /// </summary>
        public static HitResult HitTest(GraphState s, HevoPoint canvasPt)
        {
            const float portRadius = 8f;
            for (int i = s.Nodes.Count - 1; i >= 0; i--)
            {
                var node = s.Nodes[i];
                foreach (var p in node.OutputPorts)
                {
                    var c = node.GetPortPosition(p);
                    if (Distance(canvasPt, c) <= portRadius)
                        return new HitResult(node, p, false);
                }
                foreach (var p in node.InputPorts)
                {
                    var c = node.GetPortPosition(p);
                    if (Distance(canvasPt, c) <= portRadius)
                        return new HitResult(node, p, true);
                }
                if (node.GetBounds().Contains(canvasPt))
                    return new HitResult(node, null, false);
            }
            return new HitResult(null, null, false);
        }

        public static float Distance(HevoPoint a, HevoPoint b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>WPF MouseEventArgs 转画布像素坐标。</summary>
        public static HevoPoint Pt(MouseEventArgs e, IInputElement el)
        {
            var p = e.GetPosition(el);
            return new HevoPoint((float)p.X, (float)p.Y);
        }

        /// <summary>
        /// 当前 minimap 浮窗几何与缩放因子(命中 + 拖动复用)。
        /// 返回 false 表示节点为空 / 窗口未 layout,调用方应短路。
        /// 几何含义:
        /// <list type="bullet">
        ///   <item><c>mx, my</c>:浮窗左上角屏幕坐标(右下角对齐 + Margin 偏移);</item>
        ///   <item><c>ox, oy, scale</c>:画布点 → 浮窗内屏幕点的仿射变换 <c>screen = canvas * scale + (ox, oy)</c>;</item>
        ///   <item><c>winW, winH</c>:chart 当前 ActualWidth / ActualHeight。</item>
        /// </list>
        /// </summary>
        public static bool ComputeMinimapMapping(
            ChartCell chart, GraphState s, MinimapGeometry g,
            out float mx, out float my, out float ox, out float oy, out float scale, out float winW, out float winH)
        {
            mx = my = ox = oy = scale = winW = winH = 0f;
            if (chart == null || s.Nodes.Count == 0) return false;
            winW = (float)chart.ActualWidth;
            winH = (float)chart.ActualHeight;
            if (winW <= 0 || winH <= 0) return false;
            mx = winW - g.Width - g.Margin;
            my = winH - g.Height - g.Margin;
            var (bbX, bbY, bbW, bbH) = GraphGeometry.ComputeNodesAabb(s.Nodes);
            float drawAreaW = g.Width - 2 * g.Padding;
            float drawAreaH = g.Height - 2 * g.Padding;
            scale = Math.Min(drawAreaW / Math.Max(bbW, 1f), drawAreaH / Math.Max(bbH, 1f));
            ox = mx + g.Padding + (drawAreaW - bbW * scale) / 2f - bbX * scale;
            oy = my + g.Padding + (drawAreaH - bbH * scale) / 2f - bbY * scale;
            return true;
        }
    }
}
