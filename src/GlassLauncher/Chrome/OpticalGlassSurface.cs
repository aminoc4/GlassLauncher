using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassLauncher.Services;

namespace GlassLauncher.Chrome;

/// <summary>
/// 把一块 <see cref="Image"/> 变成“能看见窗外”的光学玻璃。
///
/// 生命周期：窗口刚创建（还没显示）时抓一次背景 —— 这时不需要排除自身；
/// 之后每次移动 / 改尺寸 / 换 DPI 再抓，抓之前在极短时间里打开截屏排除，
/// 抓完立刻关掉，这样用户自己截图时窗口依然是可见的。
///
/// 抓取与合成都在后台线程做，UI 线程只负责最后贴一张冻结过的位图。
/// </summary>
public sealed class OpticalGlassSurface : IDisposable
{
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;

    private readonly Window _window;
    private readonly Image _target;
    private readonly DispatcherTimer _debounce;
    private HwndSource? _source;
    private int _version;
    private bool _attached;
    private bool _disposed;
    private bool _inSizeMove;
    private bool _running;
    private bool _pending;

    public OpticalGlassSurface(Window window, Image target)
    {
        _window = window;
        _target = target;

        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(90),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RefreshAsync();
        };
    }

    /// <summary>圆角半径，必须和 XAML 里的视觉圆角一致。</summary>
    public double Radius { get; set; } = 20;

    /// <summary>边缘透镜带宽度（DIP）。</summary>
    public double Band { get; set; } = 26;

    /// <summary>透镜最大位移（DIP）。</summary>
    public double Amplitude { get; set; } = 17;

    public bool Enabled { get; set; } = true;

    public void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;

        // SourceInitialized 时窗口已有句柄、但还没显示：此刻抓到的就是干净的桌面
        _window.SourceInitialized += OnSourceInitialized;
        _window.Loaded += OnLoaded;
        _window.SizeChanged += OnGeometryChanged;
        _window.LocationChanged += OnGeometryChanged;
        _window.DpiChanged += OnDpiChanged;
        _window.Closed += OnClosed;

        if (new WindowInteropHelper(_window).Handle != IntPtr.Zero)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>立刻重画一次（主题变化等）。</summary>
    public void Refresh() => _ = RefreshAsync();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        _ = RefreshAsync();
    }

    /// <summary>
    /// 拖动 / 缩放窗口时系统会进入一个模态循环（WM_ENTERSIZEMOVE），期间每帧都重抓重算
    /// 会直接把拖动拖成一卡一卡。这里改成：交互期间一帧都不算，松手（WM_EXITSIZEMOVE）补一帧。
    /// 交互过程中位图跟着窗口一起移动，视觉上就是“玻璃是一张冻住的快照”，反而更像真玻璃。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_ENTERSIZEMOVE)
        {
            _inSizeMove = true;
            _debounce.Stop();
        }
        else if (msg == WM_EXITSIZEMOVE)
        {
            _inSizeMove = false;
            _debounce.Stop();
            _ = RefreshAsync();
        }

        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void OnGeometryChanged(object? sender, EventArgs e)
    {
        if (_inSizeMove)
        {
            return;
        }

        _debounce.Stop();
        _debounce.Start();
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e) => _ = RefreshAsync();

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    /// <summary>合并并发请求：正在算的时候再来的请求只记一个标记，算完再补一次。</summary>
    private async Task RefreshAsync()
    {
        if (!Enabled || _disposed || _inSizeMove)
        {
            return;
        }

        if (_running)
        {
            _pending = true;
            return;
        }

        _running = true;
        try
        {
            await RenderCoreAsync();
        }
        finally
        {
            _running = false;

            if (_pending)
            {
                _pending = false;
                _ = RefreshAsync();
            }
        }
    }

    private async Task RenderCoreAsync()
    {
        int version = ++_version;

        IntPtr hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero || !ScreenGrab.GetWindowRect(hwnd, out ScreenGrab.Rect rect))
        {
            return;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(_window);
        double scale = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;

        int pad = (int)Math.Ceiling((Band + Amplitude + 6) * scale);
        int captureWidth = width + (pad * 2);
        int captureHeight = height + (pad * 2);

        // 窗口可见时必须先把自己从截屏里排除，否则抓到的是自己（会看到递归的窗口）
        bool exclude = _window.IsVisible;
        GlassOpticsOptions options = ThemeService.IsDarkMode ? GlassOpticsOptions.Dark : GlassOpticsOptions.Light;

        long started = Environment.TickCount64;
        byte[]? pixels;
        try
        {
            if (exclude && !ScreenGrab.TrySetExclusion(hwnd, true))
            {
                Diag.Log("Optics: 无法排除自身截屏，放弃本次抓取");
                return;
            }

            if (exclude)
            {
                // 给 DWM 一两帧时间把排除生效，否则可能仍抓到自己
                await Task.Delay(24).ConfigureAwait(true);
            }

            pixels = await Task.Run(() =>
                ScreenGrab.Capture(rect.Left - pad, rect.Top - pad, captureWidth, captureHeight)).ConfigureAwait(true);
        }
        finally
        {
            if (exclude)
            {
                _ = ScreenGrab.TrySetExclusion(hwnd, false);
            }
        }

        if (pixels is null)
        {
            Diag.Log($"Optics: 抓取失败 rect=({rect.Left},{rect.Top},{width}x{height})");
            return;
        }

        long captured = Environment.TickCount64;

        double radius = Radius;
        double band = Band;
        double amplitude = Amplitude;

        byte[] output = await Task.Run(() => GlassOptics.Render(
            pixels,
            captureWidth,
            captureHeight,
            width,
            height,
            pad,
            pad,
            scale,
            options with { Radius = radius, Band = band, Amplitude = amplitude })).ConfigureAwait(true);

        if (_disposed || version != _version)
        {
            return;
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96 * scale,
            96 * scale,
            PixelFormats.Bgra32,
            null,
            output,
            width * 4);

        bitmap.Freeze();
        _target.Source = bitmap;

        long done = Environment.TickCount64;
        Diag.Log($"Optics: {width}x{height} scale={scale:0.###} pad={pad} dark={ThemeService.IsDarkMode} "
            + $"capture={captured - started}ms render={done - captured}ms");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounce.Stop();

        _source?.RemoveHook(WndProc);
        _source = null;

        _window.SourceInitialized -= OnSourceInitialized;
        _window.Loaded -= OnLoaded;
        _window.SizeChanged -= OnGeometryChanged;
        _window.LocationChanged -= OnGeometryChanged;
        _window.DpiChanged -= OnDpiChanged;
        _window.Closed -= OnClosed;
    }
}
