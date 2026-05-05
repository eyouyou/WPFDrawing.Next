using System.Runtime.CompilerServices;

namespace Hevo.Charting.Renderers
{
    // 顶点对齐 helper：让 stroke 中心精确落在像素整数 / 半像素位置。
    // 配合 IsAntialias=true 的 stroke paint，AA scanline 算法对 H/V 线给出精确 1 像素覆盖，
    // 对斜线段给出平滑边缘。业务侧不可见——所有像素算术只在 renderer 命令分发循环里发生。
    internal static class PixelSnap
    {
        // 把 stroke 中心放在使其覆盖最少像素的位置：
        // ceil(thickness) 为奇 → 中心落在像素中心 (+0.5)，AA scanline 把单像素覆盖成 100%；
        // ceil(thickness) 为偶 → 中心落在像素边界 (+0)，AA scanline 把整数像素行覆盖成 100%。
        // 关键：必须用 Ceiling 而不是 (int) 截断，否则 thickness=0.5 等 sub-pixel 线宽会被算成偶数。
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float HalfPx(double thickness)
        {
            if (thickness <= 0) return 0f;
            int n = (int)Math.Ceiling(thickness);
            return (n % 2 != 0) ? 0.5f : 0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Vertex(double v, float halfPx)
            => MathF.Round((float)v) + halfPx;

        // 单线段端点 snap：H/V 时**非对称** snap，斜线段两轴都 .5。
        //
        // 为什么 H/V 不能两轴都 .5：1px 水平线 (0,10)→(600,10) 若 snap 成 (0.5, 10.5)→(600.5, 10.5)，
        // Skia stroke 几何 X∈[0.5, 600.5]，AA scanline 给端点列各 50% 覆盖率。淡色 grid 在低对比背景下
        // 端点几乎不可见 → 视觉上"线没画全"。
        //
        // 正解：parallel 轴（线方向）走整数 round，端点像素列 100% 覆盖；perpendicular 轴走 .5 snap，
        // stroke 中心落像素中心 → 整条线全长 100% 覆盖、无端点羽化。
        public static void SnapEndpoints(HevoPoint p1, HevoPoint p2, float halfPx,
            out HevoPoint a, out HevoPoint b)
        {
            // 水平
            if (p1.Y == p2.Y)
            {
                float y = MathF.Round(p1.Y) + halfPx;
                a = new HevoPoint(MathF.Round(p1.X), y);
                b = new HevoPoint(MathF.Round(p2.X), y);
                return;
            }
            // 垂直
            if (p1.X == p2.X)
            {
                float x = MathF.Round(p1.X) + halfPx;
                a = new HevoPoint(x, MathF.Round(p1.Y));
                b = new HevoPoint(x, MathF.Round(p2.Y));
                return;
            }
            // 斜线段：两轴都 .5（标准 vertex snap）
            a = new HevoPoint(Vertex(p1.X, halfPx), Vertex(p1.Y, halfPx));
            b = new HevoPoint(Vertex(p2.X, halfPx), Vertex(p2.Y, halfPx));
        }

        // stroke 中心从外边界向内收缩 halfT，等价于其它 GFX 的 "inside stroke alignment"。
        // 关键：必须先把外边界 round 到设备像素整数（layout 的浮点 rect 直接 +halfT 会让
        // stroke 中心落在浮点位置，AA 横跨 2 像素羽化）。round 之后再 inside-shrink，
        // 奇数 thickness → 中心在半像素 (X.5)；偶数 thickness → 中心在整像素 (X.0)。
        public static HevoRect InsideStroke(HevoRect r, double thickness)
        {
            float left = MathF.Round(r.X);
            float top = MathF.Round(r.Y);
            float right = MathF.Round(r.Right);
            float bottom = MathF.Round(r.Bottom);
            float h = (float)(thickness / 2.0);
            return new HevoRect(
                left + h,
                top + h,
                Math.Max(0f, right - left - 2f * h),
                Math.Max(0f, bottom - top - 2f * h));
        }
    }
}
