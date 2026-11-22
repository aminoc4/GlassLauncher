#if DEBUG
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassLauncher.Chrome;

namespace GlassLauncher;

/// <summary>
/// 仅 DEBUG 编译的回归/性能探针。全部由环境变量开启，正常启动完全不参与：
///   GLASSLAUNCHER_PERF=1                每秒写一行 PERF（ws/private/managed/GDI/线程）
///   GLASSLAUNCHER_AUTODRAG=dx,dy,ms,n,gap 合成窗口拖动（走与真实拖动相同的代码路径）
///   GLASSLAUNCHER_TRAY_CYCLE=n          自动隐藏/唤出 n 次（验证 Hide/Show 稳定性）
///   GLASSLAUNCHER_DUMP_BACKDROP=ms      延迟 ms 后把背景层渲染成 PNG（核对几何/模糊）
///
/// 目的：让 CHARTER 第六节的回归测试可以半自动跑（拖动 → 托盘循环 → 日志断言），
/// 而不是每次都靠人手拖、靠肉眼判断"有没有闪"。
/// </summary>
public partial class MainWindow
{
    private static readonly Stopwatch HarnessClock = Stopwatch.StartNew();
    private DispatcherTimer? _perfTimer;
    private int _perfDragTicks;
    private long _perfDragStartMs;

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint flags);

    private static bool HarnessEnabled(string name) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

    private void StartDiagnosticHarness()
    {
        if (Environment.GetEnvironmentVariable("GLASSLAUNCHER_PERF") == "1")
        {
            _perfTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _perfTimer.Tick += (_, _) => SamplePerf();
            _perfTimer.Start();
            Diag.Log($"PERF probe started pid={Environment.ProcessId}");
        }

        if (Environment.GetEnvironmentVariable("GLASSLAUNCHER_AUTODRAG") is { Length: > 0 } spec)
        {
            int delay = int.TryParse(Environment.GetEnvironmentVariable("GLASSLAUNCHER_AUTODRAG_DELAY"), out int d)
                ? d
                : 4000;
            var first = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
            first.Tick += (_, _) =>
            {
                first.Stop();
                RunAutoDrag(spec);
            };
            first.Start();
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("GLASSLAUNCHER_TRAY_CYCLE"), out int cycles)
            && cycles > 0)
        {
            RunTrayCycle(cycles);
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("GLASSLAUNCHER_DUMP_BACKDROP"), out int dumpAt)
            && dumpAt > 0)
        {
            var dump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(dumpAt) };
            dump.Tick += (_, _) =>
            {
                dump.Stop();
                DumpBackdrop("rest");
            };
            dump.Start();
        }
    }

    private void SamplePerf()
    {
        using Process self = Process.GetCurrentProcess();
        self.Refresh();
        Diag.Log($"PERF t={HarnessClock.Elapsed.TotalSeconds:F1}s "
            + $"ws={self.WorkingSet64 / 1048576.0:F1}MB private={self.PrivateMemorySize64 / 1048576.0:F1}MB "
            + $"managed={GC.GetTotalMemory(false) / 1048576.0:F1}MB "
            + $"handles={self.HandleCount} threads={self.Threads.Count} "
            + $"gdi={GetGuiResources(self.Handle, 0)} user={GetGuiResources(self.Handle, 1)} "
            + $"visible={IsVisible} gc0={GC.CollectionCount(0)} gc2={GC.CollectionCount(2)}");
    }

    /// <summary>合成窗口拖动："dx,dy[,durationMs[,repeats[,gapMs]]]"。走真实路径（StartDragRide/EndManualDrag）。</summary>
    private void RunAutoDrag(string spec)
    {
        string[] parts = spec.Split(',');
        int dx = parts.Length > 0 && int.TryParse(parts[0], out int a) ? a : 200;
        int dy = parts.Length > 1 && int.TryParse(parts[1], out int b) ? b : 80;
        int duration = parts.Length > 2 && int.TryParse(parts[2], out int c) ? c : 1500;
        int repeat = parts.Length > 3 && int.TryParse(parts[3], out int e) ? e : 1;
        int gap = parts.Length > 4 && int.TryParse(parts[4], out int f) ? f : 700;

        IntPtr hwnd = _hwnd;
        if (hwnd == IntPtr.Zero || !ScreenGrab.GetWindowRect(hwnd, out ScreenGrab.Rect rect))
        {
            Diag.Log("PERF autodrag: no window");
            return;
        }

        int left = rect.Left;
        int top = rect.Top;
        _manualDrag = true;
        _inModalMove = true;
        _perfDragTicks = 0;
        _perfDragStartMs = HarnessClock.ElapsedMilliseconds;
        Diag.Log($"PERF autodrag start dx={dx} dy={dy} dur={duration}ms rect=({left},{top})");
        ApplyBlurRadius();
        StartDragRide();

        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / duration);
            double ease = 1 - Math.Pow(1 - t, 2.2);
            SetWindowPos(hwnd, IntPtr.Zero, left + (int)(dx * ease), top + (int)(dy * ease), 0, 0,
                SwpNosize | SwpNozorder | SwpNoactivate);
            _perfDragTicks++;

            if (t < 1.0)
            {
                return;
            }

            timer.Stop();
            double secs = Math.Max(0.001, (HarnessClock.ElapsedMilliseconds - _perfDragStartMs) / 1000.0);
            Diag.Log($"PERF autodrag ticks={_perfDragTicks} ticksPerSec={_perfDragTicks / secs:F1} "
                + $"liveFrames={_liveFrames} avg={(_liveFrames == 0 ? 0 : _liveTotalMs / _liveFrames):F1}ms "
                + $"duration={sw.ElapsedMilliseconds}ms");
            EndManualDrag();
            SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0, SwpNosize | SwpNozorder | SwpNoactivate);
            Diag.Log("PERF autodrag done");

            repeat--;
            if (repeat > 0)
            {
                var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(gap) };
                wait.Tick += (_, _) =>
                {
                    wait.Stop();
                    RunAutoDrag(spec);
                };
                wait.Start();
            }
            else
            {
                Diag.Log("PERF autodrag all done");
            }
        };
        timer.Start();
    }

    /// <summary>托盘 Hide/Show 循环（等价于托盘左键/热键切换），每轮间隔 1.2s。</summary>
    private void RunTrayCycle(int remaining)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        timer.Tick += (_, _) =>
        {
            remaining--;
            Diag.Log($"PERF tray cycle remaining={remaining} visible={IsVisible}");
            ToggleLauncherWindow();
            if (remaining <= 0)
            {
                timer.Stop();
                Diag.Log("PERF tray cycle done");
            }
        };
        timer.Start();
    }

    /// <summary>把真正上屏的背景层（含 BlurEffect）渲染成 PNG，用来肉眼核对几何/模糊/清晰度。</summary>
    private void DumpBackdrop(string tag)
    {
        try
        {
            int w = (int)Math.Ceiling(ActualWidth);
            int h = (int)Math.Ceiling(ActualHeight);
            if (w < 40 || h < 40)
            {
                return;
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(Shell);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            string dir = Environment.GetEnvironmentVariable("GLASSLAUNCHER_LOG_DIR") ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"backdrop-{tag}.png");
            using (FileStream fs = File.Create(path))
            {
                encoder.Save(fs);
            }

            Diag.Log($"PERF dump {path} {w}x{h}");
        }
        catch (Exception ex)
        {
            Diag.Log($"PERF dump failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
#endif
