using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media;

namespace Hevo.Charting.Benchmarks
{
    /// <summary>
    /// 测量环境记录 + 标准化。render-probe / latency-probe 开头都打一遍,结果文件里也带上,
    /// 不同机器、远程桌面 vs 本机直显的数据才能放在一起比。
    /// </summary>
    internal sealed record ProbeEnvironment(
        string Machine,
        string Os,
        string Runtime,
        string Cpu,
        int LogicalCores,
        string Gpu,
        int RenderTier,
        bool RemoteSession,
        bool SoftwareRenderingForced,
        double RefreshHz,
        double DpiScale,
        string GcMode,
        bool ReleaseBuild,
        bool DebuggerAttached)
    {
        /// <summary>GPU 渲染路径的前提:Tier 2 且不在远程桌面里(远程桌面下 WPF 退回软件渲染)。</summary>
        public bool HardwareRendering => RenderTier >= 2 && !RemoteSession && !SoftwareRenderingForced;

        public static ProbeEnvironment Capture()
        {
            bool release =
#if DEBUG
                false;
#else
                true;
#endif
            return new ProbeEnvironment(
                Machine: Environment.MachineName,
                Os: RuntimeInformation.OSDescription,
                Runtime: RuntimeInformation.FrameworkDescription,
                Cpu: CpuName(),
                LogicalCores: Environment.ProcessorCount,
                Gpu: GpuName(),
                RenderTier: RenderCapability.Tier >> 16,
                RemoteSession: Native.GetSystemMetrics(Native.SM_REMOTESESSION) != 0,
                SoftwareRenderingForced: RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly,
                RefreshHz: QueryRefreshRate(),
                DpiScale: QueryDpiScale(),
                GcMode: (GCSettings.IsServerGC ? "Server" : "Workstation") + "/" + GCSettings.LatencyMode,
                ReleaseBuild: release,
                DebuggerAttached: Debugger.IsAttached);
        }

        /// <summary>
        /// 标准化:进程 High 优先级、当前线程 Highest。不改 GC 模式(要测的就是默认配置下的分配和回收)。
        /// </summary>
        public static void Standardize()
        {
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch { /* 没权限就算了 */ }
            try { System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest; } catch { }
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"[env] {Machine} | {Os} | {Runtime} | CPU {Cpu} ({LogicalCores} 线程)"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"[env] GPU {Gpu} | WPF RenderTier {RenderTier} | 远程会话 {(RemoteSession ? "是" : "否")} | " +
                $"刷新率 {RefreshHz:F1} Hz | DPI 缩放 {DpiScale:P0} | GC {GcMode}"));
            sb.Append("[env] 渲染路径: ").Append(HardwareRendering ? "GPU 硬件渲染" : "软件渲染");
            if (!ReleaseBuild) sb.Append(" | ⚠ Debug 构建");
            if (DebuggerAttached) sb.Append(" | ⚠ 挂着调试器");
            if (RemoteSession) sb.Append(" | ⚠ 远程桌面:WPF 走软件渲染,合成/上屏数字不代表本机直显");
            return sb.ToString();
        }

        private static string CpuName()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "?";
            }
            catch { return "?"; }
        }

        private static string GpuName()
        {
            try
            {
                var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
                for (uint i = 0; Native.EnumDisplayDevices(null, i, ref dd, 0); i++)
                {
                    if ((dd.StateFlags & Native.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0) return dd.DeviceString;
                    dd.cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>();
                }
            }
            catch { }
            return "?";
        }

        private static double QueryRefreshRate()
        {
            try
            {
                var info = new Native.DWM_TIMING_INFO { cbSize = (uint)Marshal.SizeOf<Native.DWM_TIMING_INFO>() };
                if (Native.DwmGetCompositionTimingInfo(IntPtr.Zero, ref info) == 0 && info.rateRefresh.uiDenominator != 0)
                    return (double)info.rateRefresh.uiNumerator / info.rateRefresh.uiDenominator;
            }
            catch { }
            return double.NaN;
        }

        private static double QueryDpiScale()
        {
            try
            {
                IntPtr hdc = Native.GetDC(IntPtr.Zero);
                try { return Native.GetDeviceCaps(hdc, Native.LOGPIXELSX) / 96.0; }
                finally { Native.ReleaseDC(IntPtr.Zero, hdc); }
            }
            catch { return double.NaN; }
        }
    }

    internal static class Native
    {
        public const int SM_REMOTESESSION = 0x1000;
        public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
        public const int LOGPIXELSX = 88;
        public const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
        public const int SRCCOPY = 0x00CC0020;

        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr hdc, int index);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int x1, int y1, int rop);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO info);

        [DllImport("dwmapi.dll")]
        public static extern int DwmFlush();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] inputs, int cbSize);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct UNSIGNED_RATIO { public uint uiNumerator; public uint uiDenominator; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct DWM_TIMING_INFO
        {
            public uint cbSize;
            public UNSIGNED_RATIO rateRefresh;
            public ulong qpcRefreshPeriod;
            public UNSIGNED_RATIO rateCompose;
            public ulong qpcVBlank;
            public ulong cRefresh;
            public uint cDXRefresh;
            public ulong qpcCompose;
            public ulong cFrame;
            public uint cDXPresent;
            public ulong cRefreshFrame;
            public ulong cFrameSubmitted;
            public uint cDXPresentSubmitted;
            public ulong cFrameConfirmed;
            public uint cDXPresentConfirmed;
            public ulong cRefreshConfirmed;
            public uint cDXRefreshConfirmed;
            public ulong cFramesLate;
            public uint cFramesOutstanding;
            public ulong cFrameDisplayed;
            public ulong qpcFrameDisplayed;
            public ulong cRefreshFrameDisplayed;
            public ulong cFrameComplete;
            public ulong qpcFrameComplete;
            public ulong cFramePending;
            public ulong qpcFramePending;
            public ulong cFramesDisplayed;
            public ulong cFramesComplete;
            public ulong cFramesPending;
            public ulong cFramesAvailable;
            public ulong cFramesDropped;
            public ulong cFramesMissed;
            public ulong cRefreshNextDisplayed;
            public ulong cRefreshNextPresented;
            public ulong cRefreshesDisplayed;
            public ulong cRefreshesPresented;
            public ulong cRefreshStarted;
            public ulong cPixelsReceived;
            public ulong cPixelsDrawn;
            public ulong cBuffersEmpty;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount;
            public uint biCompression; public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
            public uint biClrUsed; public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

        public const uint INPUT_MOUSE = 0;
        public const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_WHEEL = 0x0800,
                          MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx; public int dy; public int mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        // INPUT 联合体里最大的成员是 MOUSEINPUT(x64 下 32 字节),只用鼠标,直接内联。
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public MOUSEINPUT mi; }
    }
}
