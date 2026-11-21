using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GlassLauncher.Chrome;

/// <summary>
/// 拖动/缩放窗口期间的每帧抓屏。
///
/// 架构：UI 线程的 CompositionTarget.Rendering 只负责算几何 + 提交请求（微秒级，
/// 不阻塞模态拖动循环），真正的 BitBlt 在后台线程执行，完成后经 Dispatcher
/// 回 UI 线程 WritePixels 上屏。这样 UI 线程每帧的开销只有位图上传（~1ms），
/// 模态循环不再被 10ms 的 BitBlt 堵住 —— 这是"拖动一卡一卡"的根源。
///
/// GDI 资源（screen DC、memory DC、DIB section）全部在工作线程创建和使用
/// （GDI DC 有线程亲和性），全程零托管分配；BitBlt 结果用指针直接灌进
/// WriteableBitmap，Bgr32 与 Bgra32 显示等价（都按不透明处理）且免 alpha 修补。
///
/// 必须与静态路径（ScreenGrab.Capture）完全同规格：全分辨率 + CAPTUREBLT。
/// 试过 50% 降采样（COLORONCOLOR 点采样），把背景细文字打碎成灰块，不可接受。
///
/// 尺寸按 32px 分桶：交互缩放时尺寸每帧都在变，跨桶才重建 DIB/位图。
/// </summary>
internal sealed class LiveGrab : IDisposable
{
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    private readonly Thread _worker;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Dispatcher _dispatcher;
    private readonly Stopwatch _grabClock = new();
    private volatile bool _running;
    private bool _started;

    // UI 线程写入、工作线程读取的帧请求（lock 保护）
    private readonly object _gate = new();
    private bool _pending;
    private int _reqX, _reqY, _reqWidth, _reqHeight;
    private Point _reqOrigin;
    private double _reqDpiX, _reqDpiY;

    // 工作线程专属的 GDI 资源
    private IntPtr _screenDc;
    private IntPtr _memoryDc;
    private IntPtr _previous;
    private IntPtr _dib;
    private IntPtr _bits;
    private int _width;
    private int _height;

    // 仅 UI 线程访问
    private WriteableBitmap? _bitmap;

    /// <summary>预热帧数：排除刚开 DWM 还没落实，前几帧会抓到自己，跳过。</summary>
    private int _warmupLeft;

    /// <summary>最近一次 BitBlt 耗时（毫秒），诊断用。</summary>
    public double LastGrabMs { get; private set; }

    /// <summary>
    /// 一帧就绪（UI 线程回调）：参数为上好屏的位图、这帧抓取时窗口原点（物理 px）
    /// 和 DPI —— 回调里用前后两帧原点差做位移预测。
    /// </summary>
    public event Action<WriteableBitmap, Point, DpiScale>? FrameReady;

    public LiveGrab()
    {
        _dispatcher = Dispatcher.CurrentDispatcher; // 构造发生在 UI 线程
        _worker = new Thread(PumpLoop) { IsBackground = true, Name = "GlassLauncher.LiveGrab" };
    }

    /// <summary>UI 线程调用：提交一帧抓取请求（几何由调用方算好）。多次提交自动合并。</summary>
    public void Submit(int x, int y, int width, int height, Point originPx, DpiScale dpi)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 12_000_000)
        {
            return;
        }

        lock (_gate)
        {
            _pending = true;
            _reqX = x;
            _reqY = y;
            _reqWidth = width;
            _reqHeight = height;
            _reqOrigin = originPx;
            _reqDpiX = dpi.DpiScaleX;
            _reqDpiY = dpi.DpiScaleY;
        }

        if (!_started)
        {
            _started = true;
            _running = true;
            _worker.Start();
        }

        _signal.Set();
    }

    /// <summary>每轮拖动开始时调用：重新武装预热帧计数（排除刚开，前几帧带自己）。</summary>
    public void ResetWarmup(int frames) => _warmupLeft = frames;

    private void PumpLoop()
    {
        while (_running)
        {
            _signal.WaitOne();
            if (!_running)
            {
                break;
            }

            int x, y, w, h;
            Point origin;
            double dpiX, dpiY;

            lock (_gate)
            {
                if (!_pending)
                {
                    continue;
                }

                _pending = false;
                x = _reqX;
                y = _reqY;
                w = _reqWidth;
                h = _reqHeight;
                origin = _reqOrigin;
                dpiX = _reqDpiX;
                dpiY = _reqDpiY;
            }

            // 预热期：排除刚开 DWM 还没落实，BitBlt 会抓到自己，直接跳过。
            // 背景继续显示拖动开始前的冻结帧，几十毫秒的滞后完全不可感知
            if (_warmupLeft > 0)
            {
                _warmupLeft--;
                continue;
            }

            int bucketW = Bucket(w);
            int bucketH = Bucket(h);

            if (_memoryDc == IntPtr.Zero || bucketW != _width || bucketH != _height)
            {
                if (!RecreateDib(bucketW, bucketH))
                {
                    continue;
                }

                // WriteableBitmap 是 WPF 对象，必须在 UI 线程创建。
                // 先排创建、再排上屏，Dispatcher 的 FIFO 顺序保证不会用错尺寸
                _dispatcher.BeginInvoke(() => CreateBitmap(bucketW, bucketH));
            }

            _grabClock.Restart();

            // CAPTUREBLT：连分层/合成窗口一起抓 —— 必须与静态路径一致，
            // 否则拖动前后背景内容会对不上（分层浮窗时有时无）
            if (!BitBlt(_memoryDc, 0, 0, _width, _height, _screenDc, x, y, SRCCOPY | CAPTUREBLT))
            {
                continue;
            }

            GdiFlush();
            LastGrabMs = _grabClock.Elapsed.TotalMilliseconds;

            // 上屏转交 UI 线程（WritePixels 是 WPF 调用）。闭包只捕获值类型，零大对象分配
            var capturedOrigin = origin;
            var capturedDpi = new DpiScale(dpiX, dpiY);
            _dispatcher.BeginInvoke(() => RaiseFrame(capturedOrigin, capturedDpi));
        }

        ReleaseGdi();
    }

    private static int Bucket(int v) => (v + 31) / 32 * 32;

    private void CreateBitmap(int width, int height)
    {
        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
    }

    private void RaiseFrame(Point originPx, DpiScale dpi)
    {
        var bitmap = _bitmap;
        if (bitmap is null)
        {
            return;
        }

        FrameReady?.Invoke(bitmap, originPx, dpi);
    }

    /// <summary>工作线程重建 GDI 资源（DIB section 按 32px 桶尺寸）。失败返回 false。</summary>
    private bool RecreateDib(int width, int height)
    {
        ReleaseGdi();
        _width = width;
        _height = height;

        _screenDc = GetDC(IntPtr.Zero);
        if (_screenDc == IntPtr.Zero)
        {
            return false;
        }

        _memoryDc = CreateCompatibleDC(_screenDc);
        if (_memoryDc == IntPtr.Zero)
        {
            ReleaseGdi();
            return false;
        }

        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height, // 负高度 = 自上而下
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            },
        };

        _dib = CreateDIBSection(_screenDc, ref info, 0, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero || _bits == IntPtr.Zero)
        {
            ReleaseGdi();
            return false;
        }

        _previous = SelectObject(_memoryDc, _dib);
        if (_previous == IntPtr.Zero || _previous == new IntPtr(-1))
        {
            ReleaseGdi();
            return false;
        }

        return true;
    }

    /// <summary>释放 GDI 资源。必须在创建它们的线程（工作线程）上调用。</summary>
    private void ReleaseGdi()
    {
        _bitmap = null;

        if (_memoryDc != IntPtr.Zero && _previous != IntPtr.Zero && _previous != new IntPtr(-1))
        {
            _ = SelectObject(_memoryDc, _previous);
        }

        if (_dib != IntPtr.Zero)
        {
            _ = DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }

        if (_memoryDc != IntPtr.Zero)
        {
            _ = DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }

        if (_screenDc != IntPtr.Zero)
        {
            _ = ReleaseDC(IntPtr.Zero, _screenDc);
            _screenDc = IntPtr.Zero;
        }

        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        _running = false;
        _signal.Set();
        _ = _worker.Join(500); // 工作线程退出前自己释放 GDI（线程亲和）
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public int Colors;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, uint rop);

    [DllImport("gdi32.dll")]
    private static extern void GdiFlush();
}
