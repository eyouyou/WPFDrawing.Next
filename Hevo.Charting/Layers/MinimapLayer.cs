using System;
using System.Collections.Generic;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using System.Windows.Media;

namespace Hevo.Charting.Layers
{
    /// <summary>
    /// 通用 minimap trait。完全不知道 host 是 graph 编辑器还是 chart —— 把 host 业务概念翻译成
    /// 几何语义("内容包围盒 + 内容子项 + viewport 在内容空间的位置")就能复用 <see cref="MinimapLayer"/>。
    /// <list type="bullet">
    ///   <item>Graph 编辑器:ContentBounds = nodes 的 AABB;ContentItems = nodes 各自 bounds;ViewportInContent = Transform 反算的画布可见区</item>
    ///   <item>Chart:ContentBounds = (0,0,length,1) 数据 X 范围;ContentItems = null(不画细节);ViewportInContent = (Active.Min, 0, span, 1)</item>
    /// </list>
    /// </summary>
    /// <param name="ContentBounds">内容包围盒 —— minimap fit 缩放的源 AABB。</param>
    /// <param name="ContentItems">内容子项(可选)—— graph 用来画每个 node 的小矩形;chart 用 null 不画细节,只剩"viewport 框 + 内容范围"。</param>
    /// <param name="ViewportInContent">viewport 在内容空间(跟 ContentBounds 同坐标系)的矩形。layer 自己缩放投影到浮窗。</param>
    /// <param name="Width">浮窗宽度(px)。</param>
    /// <param name="Height">浮窗高度(px)。</param>
    /// <param name="Margin">浮窗距 chart 右下角的边距(px)。</param>
    /// <param name="Padding">浮窗内部 padding(px)—— 内容跟边框间留白。</param>
    /// <param name="StretchToFit">
    /// <para>true → X/Y 独立缩放,内容铺满浮窗(适合 chart:length 远大于 1,等比例 fit 会让横条极细)。</para>
    /// <para>false(默认)→ 等比例 fit + 居中(graph 行为:节点矩形按真实长宽比缩略)。</para>
    /// </param>
    public record MinimapTrait(
        HevoRect ContentBounds,
        IReadOnlyList<HevoRect>? ContentItems,
        HevoRect ViewportInContent,
        float Width = 200f,
        float Height = 140f,
        float Margin = 12f,
        float Padding = 6f,
        bool StretchToFit = false
    ) : IVisualTrait;

    /// <summary>
    /// 通用 minimap 渲染层。**纯几何变换 + 矩形绘制**,不知道任何 host 业务(graph node / chart series 等)。
    /// host 通过 <see cref="MinimapTrait"/> 喂"内容包围盒 + 内容项 + viewport 矩形",layer 负责:
    /// <list type="number">
    ///   <item>计算右下角浮窗位置</item>
    ///   <item>把内容包围盒缩放 fit(等比 / 拉伸)到浮窗,得到 (scaleX, scaleY, ox, oy)</item>
    ///   <item>用同一组缩放参数把内容项 + viewport 矩形投影到浮窗</item>
    /// </list>
    /// 配色 / 圆角 / 边框跟 GraphViewer 旧版 minimap 视觉一致 —— 用户从 graph 编辑器学来的视觉直接迁移。
    /// </summary>
    public class MinimapLayer : ChartLayer
    {
        // 跟 GraphViewer.GraphPalette 视觉对齐的配色,但写在自家模块,避免跨命名空间引内部细节。
        private static readonly IHevoBrush Bg          = new HevoSolidBrush(Color.FromArgb(0xCC, 0x14, 0x16, 0x1B));
        private static readonly HevoPen    Border      = new(new HevoSolidBrush(Color.FromRgb(0x55, 0x5C, 0x66)), 1.0);
        private static readonly IHevoBrush ContentFill = new HevoSolidBrush(Color.FromArgb(0xAA, 0x90, 0xA4, 0xAE));
        private static readonly HevoPen    ViewportPen = new(new HevoSolidBrush(Color.FromRgb(0xFF, 0xC1, 0x07)), 1.5);

        public MinimapLayer()
        {
            Name = "MinimapLayer";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Interaction;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var t = data.Get<MinimapTrait>();
            var size = data.Get<ViewportSizeTrait>();
            if (t == null || size == null) return;
            if (t.ContentBounds.Width <= 0 || t.ContentBounds.Height <= 0) return;

            // 1. 浮窗位置(右下角)+ 背板
            float winW = (float)size.Width;
            float winH = (float)size.Height;
            float mx = winW - t.Width - t.Margin;
            float my = winH - t.Height - t.Margin;
            var floater = new HevoRect(mx, my, t.Width, t.Height);
            draw.DrawRoundedRectangle(Bg, Border, floater, 4f, 4f);

            // 2. 缩放参数:等比 fit + 居中,或拉伸 fit
            float drawAreaW = t.Width - 2 * t.Padding;
            float drawAreaH = t.Height - 2 * t.Padding;
            float scaleX, scaleY, ox, oy;
            if (t.StretchToFit)
            {
                scaleX = drawAreaW / t.ContentBounds.Width;
                scaleY = drawAreaH / t.ContentBounds.Height;
                ox = mx + t.Padding - t.ContentBounds.X * scaleX;
                oy = my + t.Padding - t.ContentBounds.Y * scaleY;
            }
            else
            {
                float s = Math.Min(drawAreaW / t.ContentBounds.Width, drawAreaH / t.ContentBounds.Height);
                scaleX = scaleY = s;
                ox = mx + t.Padding + (drawAreaW - t.ContentBounds.Width  * s) / 2f - t.ContentBounds.X * s;
                oy = my + t.Padding + (drawAreaH - t.ContentBounds.Height * s) / 2f - t.ContentBounds.Y * s;
            }

            // 3. 内容子项(graph node / 抽稀数据点 / 也可以为 null 跳过)
            if (t.ContentItems != null)
            {
                foreach (var rc in t.ContentItems)
                {
                    var mini = new HevoRect(ox + rc.X * scaleX, oy + rc.Y * scaleY, rc.Width * scaleX, rc.Height * scaleY);
                    draw.DrawRectangle(ContentFill, null, mini);
                }
            }

            // 4. Viewport 框
            var vp = t.ViewportInContent;
            var vpRect = new HevoRect(ox + vp.X * scaleX, oy + vp.Y * scaleY, vp.Width * scaleX, vp.Height * scaleY);
            vpRect = vpRect.Intersect(floater); // clip 防溢出
            if (!vpRect.IsEmpty)
                draw.DrawRectangle(null, ViewportPen, vpRect);
        }
    }
}
