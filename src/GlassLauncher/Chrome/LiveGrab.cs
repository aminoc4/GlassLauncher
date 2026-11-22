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
/// 必须与静态路径（ScreenGrab.Capture）完全同规格：同倍率降采样 + CAPTUREBLT，且
/// **抓取矩形必须与请求区域等比** —— 显示端是 Stretch=Fill，位图尺寸与静态帧一样
/// 都只是"屏幕区域的降采样版本"，任何多余/缺失的像素都会被缩放，背景就和桌面/静态帧
/// 对不齐（松手换帧时看得见）。
/// 抓取尺寸与静态帧一样按 BackdropDownscale 降采样（StretchBlt HALFTONE 面积平均）：
/// 数据量降到 1/4，同时让模糊的中间表面小 4 倍。用点采样会把桌面细文字打碎成噪点，
/// 模糊后就是一层脏灰 —— 这正是以前"降采样背景发脏"的原因。
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

    /// <summary>UI 线程请求"进入长空闲"：工作线程下次醒来时把 GDI 资源还给系统。</summary>
    private volatile bool _releaseRequested;

    // UI 线程写入、工作线程读取的帧请求（lock 保护）
    private readonly object _gate = new();
    private bool _pending;
    private int _reqX, _reqY;
    private int _reqSourceWidth, _reqSourceHeight;
    private int _reqWidth, _reqHeight;
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

    // 上传缓冲池：工作线程借、UI 线程归还（WritePixels 之后）。
    // 上限 2：每块是降采样后的整窗（585×410×4 ≈ 0.92MB；满分辨率才是 3.8MB），
    // 留多了就是白占内存；UI 跟不上时宁可多借一次新数组（临时垃圾），也不让池子无上限长大。
    private const int MaxPooledBuffers = 2;
    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _spareBuffers = new();

    // 仅 UI 线程访问
    private WriteableBitmap? _bitmap;

    /// <summary>预热帧数：排除刚开 DWM 还没落实，前几帧会抓到自己，跳过。</summary>
    private int _warmupLeft;

    /// <summary>最近一次 BitBlt 耗时（毫秒），诊断用。</summary>
    public double LastGrabMs { get; private set; }

    /// <summary>
    /// 一帧就绪（UI 线程回调）：上好屏的位图、这帧抓取时窗口原点（物理 px）、
    /// 抓取源区域的满分辨率尺寸（物理 px，用于几何记账）和 DPI。
    /// </summary>
    public event Action<WriteableBitmap, Point, int, int, DpiScale>? FrameReady;

    public LiveGrab()
    {
        _dispatcher = Dispatcher.CurrentDispatcher; // 构造发生在 UI 线程
        _worker = new Thread(PumpLoop) { IsBackground = true, Name = "GlassLauncher.LiveGrab" };
    }

    /// <summary>
    /// UI 线程调用：提交一帧抓取请求（几何由调用方算好）。多次提交自动合并。
    /// source* 是屏幕上的物理像素区域，width/height 是降采样后的位图尺寸
    /// （与静态冻结帧同规格，否则松手换帧会看出清晰度差异）。
    /// </summary>
    public void Submit(int x, int y, int sourceWidth, int sourceHeight, int width, int height,
        Point originPx, DpiScale dpi)
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
            _reqSourceWidth = sourceWidth;
            _reqSourceHeight = sourceHeight;
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

    /// <summary>
    /// UI 线程调用：进入长空闲（隐藏到托盘、停止使用背景抓帧）时释放 GDI 资源、
    /// 位图与缓冲池。工作线程本身保留，下次 Submit 直接复用；下次拖动会自动重建
    /// （PumpLoop 里 _memoryDc == 0 的分支）。托盘常驻应用的大部分时间都在隐藏态，
    /// 这些资源（DIB 0.92MB + 位图/纹理 + 池块 + 3 个 GDI 对象）没有理由一直占着。
    /// </summary>
    public void ReleaseIdle()
    {
        if (!_started)
        {
            return;
        }

        lock (_gate)
        {
            _pending = false;
        }

        _bitmap = null;
        while (_spareBuffers.TryDequeue(out _))
        {
        }

        _releaseRequested = true;
        _signal.Set();
    }

    private void PumpLoop()
    {
        while (_running)
        {
            _signal.WaitOne();
            if (!_running)
            {
                break;
            }

            // 空闲释放请求：把 GDI 资源还给系统，然后继续处理（可能同时有一个新的抓帧请求）。
            // 不能在这里 continue —— 那会把已经排队的请求晾着，而信号量不会再响。
            if (_releaseRequested)
            {
                _releaseRequested = false;
                ReleaseGdi();
            }

            int x, y, w, h, sourceW, sourceH;
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
                sourceW = _reqSourceWidth;
                sourceH = _reqSourceHeight;
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

            // 尺寸必须【精确】等于请求区域。
            // 曾经按 32px 桶向上取整（避免交互缩放时每帧重建 DIB），但桶尺寸比请求大 0~31px，
            // 而显示端 FrozenSurface 是 Stretch=Fill + Margin=-48 —— 多出来的像素会被压缩回目标尺寸，
            // 背景整体缩放约 1.2%，与桌面/静态帧在右下角错位可达 14px，松手换帧时就会跳一下。
            // 现在 live 只在"移动窗口"时用（尺寸不变，不会反复重建），缩放走模态路径不经过这里。
            if (_memoryDc == IntPtr.Zero || w != _width || h != _height)
            {
                if (!RecreateDib(w, h))
                {
                    continue;
                }
            }

            _grabClock.Restart();

            // CAPTUREBLT：连分层/合成窗口一起抓 —— 必须与静态路径一致，
            // 否则拖动前后背景内容会对不上（分层浮窗时有时无）。
            // 源区域按物理像素等比缩到目标尺寸（HALFTONE 面积平均，不是点采样）：
            // 抓取和上传的数据量降到 1/4，模糊的中间表面也跟着小 4 倍。
            bool blitted;

            if (sourceW != _width || sourceH != _height)
            {
                _ = SetStretchBltMode(_memoryDc, HALFTONE);
                _ = SetBrushOrgEx(_memoryDc, 0, 0, IntPtr.Zero);
                blitted = StretchBlt(_memoryDc, 0, 0, _width, _height,
                    _screenDc, x, y, sourceW, sourceH, SRCCOPY | CAPTUREBLT);
            }
            else
            {
                blitted = BitBlt(_memoryDc, 0, 0, _width, _height, _screenDc, x, y, SRCCOPY | CAPTUREBLT);
            }

            if (!blitted)
            {
                continue;
            }

            GdiFlush();
            LastGrabMs = _grabClock.Elapsed.TotalMilliseconds;

            // 把 DIB 的像素拷进托管缓冲，再交 UI 线程 WritePixels 上屏。
            //
            // ⚠ 这段曾经整段丢失（ab4e894 重构时漏掉的），表现是 BitBlt 抓得很好、
            // 但 WriteableBitmap 从来没被写入过 —— 拖动时背景整片变成空位图（黑底 + 白纱 = 一片灰）。
            // 缓冲从池里借，UI 上完屏归还，避免每帧分配几 MB 托管数组（会全压在 LOH 上）。
            int bytes = _width * _height * 4;
            byte[] buffer = RentBuffer(bytes);
            Marshal.Copy(_bits, buffer, 0, bytes);

            var capturedOrigin = origin;
            var capturedDpi = new DpiScale(dpiX, dpiY);
            int capturedWidth = _width;
            int capturedHeight = _height;
            int capturedSourceWidth = sourceW;
            int capturedSourceHeight = sourceH;
            _dispatcher.BeginInvoke(() => UploadAndRaise(
                buffer, capturedWidth, capturedHeight, capturedSourceWidth, capturedSourceHeight,
                capturedOrigin, capturedDpi));
        }

        ReleaseGdi();
    }

    /// <summary>借一块上传缓冲：优先复用 UI 线程归还的，尺寸不匹配就新分配。</summary>
    private byte[] RentBuffer(int bytes)
    {
        if (_spareBuffers.TryDequeue(out byte[]? pooled) && pooled.Length == bytes)
        {
            return pooled;
        }

        return new byte[bytes];
    }

    /// <summary>UI 线程：把像素写进 WriteableBitmap、归还缓冲，然后通知订阅者。</summary>
    private void UploadAndRaise(byte[] pixels, int width, int height, int sourceWidth, int sourceHeight,
        Point originPx, DpiScale dpi)
    {
        WriteableBitmap? bitmap = _bitmap;

        if (bitmap is null || bitmap.PixelWidth != width || bitmap.PixelHeight != height)
        {
            // WriteableBitmap 是 WPF 对象，只能在 UI 线程创建
            bitmap = new WriteableBitmap(width, height, dpi.DpiScaleX * 96, dpi.DpiScaleY * 96,
                PixelFormats.Bgr32, null);
            _bitmap = bitmap;
        }

        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);

        // 归还缓冲（尺寸不匹配的旧缓冲直接丢弃，让 GC 收；池子有上限，别越攒越多）
        if (pixels.Length == width * height * 4 && _spareBuffers.Count < MaxPooledBuffers)
        {
            _spareBuffers.Enqueue(pixels);
        }

        FrameReady?.Invoke(bitmap, originPx, sourceWidth, sourceHeight, dpi);
    }

    /// <summary>工作线程重建 GDI 资源（尺寸精确等于请求区域）。失败返回 false。</summary>
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
        // 注意：不要在这里动 _bitmap。它是 UI 线程独有的字段（CreateBitmap 在 UI 线程写），
        // 从工作线程置 null 属于跨线程写同一字段，虽然不会立刻出事，但会把"谁拥有它"搞乱。

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

    private const int HALFTONE = 4;

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr dst, int x, int y, int width, int height,
        IntPtr src, int srcX, int srcY, int srcWidth, int srcHeight, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);

    [DllImport("gdi32.dll")]
    private static extern void GdiFlush();
}
