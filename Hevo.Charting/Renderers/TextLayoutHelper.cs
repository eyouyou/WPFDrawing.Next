namespace Hevo.Charting.Renderers
{
    /// <summary>
    /// 纯几何文本布局计算:把 (锚点, 测量尺寸, padding, 对齐) 解算为 (背景框, 文本起点)。
    /// WPF/Skia 两侧 DrawText 路径共用,避免对齐与 padding 公式漂移。
    /// </summary>
    public readonly struct TextLayoutResult
    {
        public readonly float BoxX;
        public readonly float BoxY;
        public readonly float BoxWidth;
        public readonly float BoxHeight;
        public readonly float TextX;
        public readonly float TextY;

        public TextLayoutResult(float boxX, float boxY, float boxW, float boxH, float textX, float textY)
        {
            BoxX = boxX; BoxY = boxY; BoxWidth = boxW; BoxHeight = boxH;
            TextX = textX; TextY = textY;
        }
    }

    public static class TextLayoutHelper
    {
        /// <param name="anchorX">DrawText 命令传入的 origin.X</param>
        /// <param name="anchorY">DrawText 命令传入的 origin.Y</param>
        /// <param name="textWidth">测量得到的文字宽度(不含 padding)</param>
        /// <param name="textHeight">测量得到的文字高度(不含 padding)</param>
        /// <param name="paddingX">左右内边距</param>
        /// <param name="paddingY">上下内边距</param>
        /// <param name="alignX">水平锚点</param>
        /// <param name="alignY">垂直锚点</param>
        /// <returns>背景框矩形 + 文本起点(顶左对齐;Skia 调用方需在 TextY 上再叠加 -metrics.Ascent 转为基线坐标)</returns>
        public static TextLayoutResult Compute(
            float anchorX, float anchorY,
            float textWidth, float textHeight,
            float paddingX, float paddingY,
            TextAlignX alignX, TextAlignY alignY)
        {
            float boxW = textWidth + paddingX * 2f;
            float boxH = textHeight + paddingY * 2f;

            float x = anchorX;
            float y = anchorY;

            if (alignX == TextAlignX.Center) x -= boxW * 0.5f;
            else if (alignX == TextAlignX.Right) x -= boxW;

            if (alignY == TextAlignY.Center) y -= boxH * 0.5f;
            else if (alignY == TextAlignY.Bottom) y -= boxH;

            return new TextLayoutResult(x, y, boxW, boxH, x + paddingX, y + paddingY);
        }
    }
}
