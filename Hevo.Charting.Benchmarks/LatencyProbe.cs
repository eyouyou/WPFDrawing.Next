using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Hevo.Charting.Core;
using Hevo.Charting.DevTools;

namespace Hevo.Charting.Benchmarks
{
    /// <summary>
    /// 输入到画面的端到端延迟 + WPF 渲染线程合成上屏时间(render-probe 只测 UI 线程 CPU 管线,这里补后半段)。
    /// <para>
    /// 用法(Windows,Release,本机直接显示,不要远程桌面;测量期间别碰鼠标,约 30 秒):
    /// <c>dotnet run -c Release --project Hevo.Charting.Benchmarks -- --latency-probe [--samples=150] [--bars=2000] [--out=latency-probe.csv]</c>
    /// </para>
    /// <para>
    /// 做法:置顶窗口里放一张 K 线图,后台线程用 SendInput 注入真实的系统鼠标输入(移动 → 十字光标,滚轮 → 缩放),
    /// 然后不停用 GDI BitBlt 抓屏幕上绘图区里的两行像素,直到像素变了 —— 屏幕抓到的是 DWM 合成后的桌面,
    /// 所以这是"输入进系统 → 画面真的变了"的时间。中间用两个 UI 线程钩子把总时长切成三段:
    /// </para>
    /// <list type="number">
    /// <item>输入 → UI 线程收到(Window.PreviewMouseMove / PreviewMouseWheel):系统输入队列 + Dispatcher 调度。</item>
    /// <item>UI 线程收到 → 这一帧在 UI 线程做完(<see cref="IncrementalRenderProbe.FrameRendered"/>):
    ///   写黑板 → 等下一个 CompositionTarget.Rendering → ProjectAll / 图层录制 / 交给 WPF。</item>
    /// <item>帧做完 → 屏幕像素变化:WPF 渲染线程合成 + Present + DWM 合成到桌面(含抓屏轮询粒度,报告里单列)。</item>
    /// </list>
    /// </summary>
    internal static class LatencyProbe
    {
        private enum Kind { Move, Wheel }

        private sealed record Sample(Kind Kind, int Index, double InputMs, double UiFrameMs, double PresentMs, double TotalMs, bool Timeout);

        // 跨线程共享的时间戳(Stopwatch ticks,0 = 未到)
        private sealed class Marks
        {
            public volatile bool Armed;
            public long T1, T2;
            public void Arm() { Interlocked.Exchange(ref T1, 0); Interlocked.Exchange(ref T2, 0); Armed = true; }
        }

        public static int Run(string[] args)
        {
            int samples = int.Parse(ProbeOptions.Get(args, "--samples=") ?? "150", CultureInfo.InvariantCulture);
            int bars = int.Parse(ProbeOptions.Get(args, "--bars=") ?? "2000", CultureInfo.InvariantCulture);
            string outPath = ProbeOptions.Get(args, "--out=") ?? "latency-probe.csv";

            int exit = 0;
            var thread = new Thread(() =>
            {
                try { exit = RunOnUiThread(samples, bars, outPath); }
                catch (Exception ex) { Console.Error.WriteLine(ex); exit = 99; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return exit;
        }

        private static int RunOnUiThread(int samplesPerKind, int bars, string outPath)
        {
            ProbeEnvironment.Standardize();
            var env = ProbeEnvironment.Capture();
            Console.WriteLine(env.Describe());
            if (env.RemoteSession)
                Console.WriteLine("[latency-probe] ⚠ 当前是远程桌面会话:抓到的是远程会话的桌面,数字不代表本机显示器。");
            Console.WriteLine("[latency-probe] 测量期间请不要移动鼠标、不要切换窗口。");

            var opt = new ProbeOptions();
            using var rig = ProbeRig.Create(opt, bars, 1, extraCapacity: 0);
            if (rig == null) return 99;
            var window = rig.Window;
            var cell = rig.Charts[0].Cell;
            window.Topmost = true;
            window.Activate();
            RenderProbe.Pump(500);

            HevoRect plot;
            using (var ctx = cell.CreateContext()) plot = ctx.GetPlotArea();
            var canvas = cell.DrawingCanvas;
            Point ToScreen(double x, double y) => canvas.PointToScreen(new Point(x, y));

            var moveTargets = new[]
            {
                ToScreen(plot.Left + plot.Width * 0.3, plot.Top + plot.Height * 0.15),
                ToScreen(plot.Left + plot.Width * 0.7, plot.Top + plot.Height * 0.15),
            };
            var stripLeft = ToScreen(plot.Left + 1, plot.Top + plot.Height * 0.5);
            var stripRight = ToScreen(plot.Right - 1, plot.Top + plot.Height * 0.5);
            var row2 = ToScreen(plot.Left + 1, plot.Top + plot.Height * 0.85);
            int x0 = (int)Math.Round(stripLeft.X);
            int width = Math.Max(1, (int)Math.Round(stripRight.X) - x0);
            int[] rows = { (int)Math.Round(stripLeft.Y), (int)Math.Round(row2.Y) };

            var marks = new Marks();
            window.PreviewMouseMove += (_, _) => Stamp(marks, ref marks.T1);
            window.PreviewMouseWheel += (_, _) => Stamp(marks, ref marks.T1);
            IncrementalRenderProbe.FrameRendered = _ =>
            {
                if (Interlocked.Read(ref marks.T1) != 0) Stamp(marks, ref marks.T2);
            };

            var results = new List<Sample>();
            double captureMs = double.NaN;
            Exception? workerError = null;
            var done = new DispatcherFrame();
            var worker = new Thread(() =>
            {
                try { captureMs = Measure(samplesPerKind, moveTargets, x0, width, rows, marks, results); }
                catch (Exception ex) { workerError = ex; }
                finally { window.Dispatcher.InvokeAsync(() => done.Continue = false); }
            }) { IsBackground = true, Priority = ThreadPriority.Highest };
            worker.Start();
            Dispatcher.PushFrame(done); // UI 线程继续跑消息循环,输入和渲染照常进行
            IncrementalRenderProbe.FrameRendered = null;

            if (workerError != null) { Console.Error.WriteLine(workerError); return 99; }
            Report(env, results, captureMs, outPath);
            return 0;
        }

        private static void Stamp(Marks m, ref long slot)
        {
            if (!m.Armed) return;
            Interlocked.CompareExchange(ref slot, Stopwatch.GetTimestamp(), 0);
        }

        // 后台测量线程:注入输入 → 轮询抓屏直到像素变化。返回单次抓屏的中位耗时(ms),即检测粒度。
        private static double Measure(int samplesPerKind, Point[] moveTargets, int x0, int width, int[] rows,
                                      Marks marks, List<Sample> results)
        {
            using var strip = new ScreenStrip(x0, width, rows);
            var rng = new Random(20261008);
            var captureCosts = new List<double>();

            SendMove(moveTargets[0]);
            Thread.Sleep(500);

            foreach (var kind in new[] { Kind.Move, Kind.Wheel })
            {
                for (int i = 0; i < samplesPerKind; i++)
                {
                    // 上一次画面落定 + 随机抖动,让输入时刻跟 VSync 相位错开,得到的是延迟分布而不是固定相位
                    Thread.Sleep(80 + rng.Next(0, 17));
                    var baseline = strip.Capture();

                    marks.Arm();
                    long t0 = Stopwatch.GetTimestamp();
                    if (kind == Kind.Move) SendMove(moveTargets[(i + 1) % 2]);
                    else SendWheel(i % 2 == 0 ? -120 : 120); // 先缩小再放大,来回

                    long t3 = 0;
                    while (true)
                    {
                        long c0 = Stopwatch.GetTimestamp();
                        var now = strip.Capture();
                        long c1 = Stopwatch.GetTimestamp();
                        captureCosts.Add(Ms(c0, c1));
                        if (!now.AsSpan().SequenceEqual(baseline)) { t3 = c1; break; }
                        if (Ms(t0, c1) > 500) break;
                    }
                    Thread.Sleep(5); // T2 可能晚于像素变化被发现之前写入,稍等一下再读
                    marks.Armed = false;
                    long t1 = Interlocked.Read(ref marks.T1), t2 = Interlocked.Read(ref marks.T2);
                    results.Add(new Sample(kind, i,
                        InputMs: t1 != 0 ? Ms(t0, t1) : double.NaN,
                        UiFrameMs: t1 != 0 && t2 != 0 ? Ms(t1, t2) : double.NaN,
                        PresentMs: t2 != 0 && t3 != 0 ? Ms(t2, t3) : double.NaN,
                        TotalMs: t3 != 0 ? Ms(t0, t3) : double.NaN,
                        Timeout: t3 == 0));
                }
                // 滚轮来回次数为偶数时视口回到原位;移动后再测滚轮时光标停在 moveTargets 之一
            }
            return Stats.Percentile(captureCosts, 0.5);
        }

        private static double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;

        private static void SendMove(Point screen)
        {
            int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN), vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN), vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            var input = new Native.INPUT
            {
                type = Native.INPUT_MOUSE,
                mi = new Native.MOUSEINPUT
                {
                    dx = (int)Math.Round((screen.X - vx) * 65535.0 / Math.Max(1, vw - 1)),
                    dy = (int)Math.Round((screen.Y - vy) * 65535.0 / Math.Max(1, vh - 1)),
                    dwFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK,
                },
            };
            Send(input);
        }

        private static void SendWheel(int delta) => Send(new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            mi = new Native.MOUSEINPUT { mouseData = delta, dwFlags = Native.MOUSEEVENTF_WHEEL },
        });

        private static void Send(Native.INPUT input)
        {
            if (Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>()) != 1)
                throw new InvalidOperationException($"SendInput 失败,Win32 错误 {Marshal.GetLastWin32Error()}(UIPI 拦截?请用普通权限运行并保持窗口在前台)");
        }

        private static void Report(ProbeEnvironment env, List<Sample> results, double captureMs, string outPath)
        {
            var md = new StringBuilder();
            md.AppendLine("## latency-probe 结果");
            md.AppendLine();
            md.AppendLine("```");
            md.AppendLine(env.Describe());
            md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"抓屏检测粒度(单次 BitBlt 中位):{captureMs:F2} ms;一帧 = {1000.0 / env.RefreshHz:F2} ms"));
            md.AppendLine("```");
            md.AppendLine();
            md.AppendLine("| 输入 | 阶段 | 中位(ms) | P95(ms) | P99(ms) | 均值(ms) | 样本 |");
            md.AppendLine("|---|---|---:|---:|---:|---:|---:|");
            foreach (var g in results.GroupBy(r => r.Kind))
            {
                string name = g.Key == Kind.Move ? "鼠标移动(十字光标)" : "滚轮(缩放)";
                void Row(string stage, Func<Sample, double> f)
                {
                    var v = g.Select(f).Where(double.IsFinite).ToList();
                    if (v.Count == 0) { md.AppendLine($"| {name} | {stage} | - | - | - | - | 0 |"); return; }
                    md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                        $"| {name} | {stage} | {Stats.Percentile(v, 0.5):F2} | {Stats.Percentile(v, 0.95):F2} | {Stats.Percentile(v, 0.99):F2} | {v.Average():F2} | {v.Count} |"));
                }
                Row("① 输入 → UI 线程收到", s => s.InputMs);
                Row("② UI 收到 → UI 线程帧完成", s => s.UiFrameMs);
                Row("③ 帧完成 → 屏幕像素变化(渲染线程合成 + DWM)", s => s.PresentMs);
                Row("端到端 输入 → 像素", s => s.TotalMs);
                int timeouts = g.Count(s => s.Timeout);
                if (timeouts > 0) md.AppendLine($"| {name} | 超时(500ms 内像素没变) | {timeouts} 次 | | | | |");
            }
            Console.WriteLine(md.ToString());

            var csv = new StringBuilder("kind,index,input_ms,ui_frame_ms,present_ms,total_ms,timeout\n");
            foreach (var s in results)
                csv.Append(string.Create(CultureInfo.InvariantCulture,
                    $"{s.Kind},{s.Index},{s.InputMs:F3},{s.UiFrameMs:F3},{s.PresentMs:F3},{s.TotalMs:F3},{(s.Timeout ? 1 : 0)}\n"));
            if (Path.GetDirectoryName(Path.GetFullPath(outPath)) is { } dir) Directory.CreateDirectory(dir);
            File.WriteAllText(outPath, csv.ToString(), new UTF8Encoding(true));
            var mdPath = Path.ChangeExtension(outPath, ".md");
            File.WriteAllText(mdPath, md.ToString(), new UTF8Encoding(true));
            Console.WriteLine($"[latency-probe] 明细 {Path.GetFullPath(outPath)};汇总 {Path.GetFullPath(mdPath)}");
        }

        /// <summary>从屏幕(DWM 合成后的桌面)抓几行像素。</summary>
        private sealed class ScreenStrip : IDisposable
        {
            private readonly int _x, _w;
            private readonly int[] _rows;
            private readonly IntPtr _screenDc, _memDc, _bitmap, _old, _bits;
            private readonly int[] _buffer;

            public ScreenStrip(int x, int width, int[] rows)
            {
                _x = x; _w = width; _rows = rows;
                _screenDc = Native.GetDC(IntPtr.Zero);
                _memDc = Native.CreateCompatibleDC(_screenDc);
                var bmi = new Native.BITMAPINFO
                {
                    bmiHeader = new Native.BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                        biWidth = width, biHeight = -rows.Length, biPlanes = 1, biBitCount = 32,
                    },
                };
                _bitmap = Native.CreateDIBSection(_memDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
                _old = Native.SelectObject(_memDc, _bitmap);
                _buffer = new int[width * rows.Length];
            }

            public int[] Capture()
            {
                for (int r = 0; r < _rows.Length; r++)
                    Native.BitBlt(_memDc, 0, r, _w, 1, _screenDc, _x, _rows[r], Native.SRCCOPY);
                Marshal.Copy(_bits, _buffer, 0, _buffer.Length);
                return (int[])_buffer.Clone();
            }

            public void Dispose()
            {
                Native.SelectObject(_memDc, _old);
                Native.DeleteObject(_bitmap);
                Native.DeleteDC(_memDc);
                Native.ReleaseDC(IntPtr.Zero, _screenDc);
            }
        }
    }
}
