using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassLauncher.Chrome;

/// <summary>
/// 拖动/缩放窗口期间的每帧抓屏。与 <see cref="ScreenGrab"/>（一次性抓一帧，抓完全拆）
/// 不同，这里所有 GDI 资源（screen DC、memory DC、DIB section）建一次、整个拖动
/// 过程反复用，BitBlt 的结果用指针直接灌进 WriteableBitmap —— 全程零分配、零拷贝。
///
/// 必须与静态路径（ScreenGrab.Capture）完全同规格：全分辨率 + CAPTUREBLT。
/// 试过 50% 降采样（COLORONCOLOR 点采样），省不了多少（实测瓶颈是 GPU 回压节流，
/// CPU 抓屏本身很闲），却把背景里的细文字打碎成一坨一坨的灰块 —— 拖一下窗口
/// 背景就"换一个样"，无法接受。Bgr32 与 Bgra32 显示完全等价（都按不透明处理）。
///
/// 尺寸按 32px 分桶：交互缩放时窗口尺寸每帧都在变，不分桶的话每帧都要重建
/// DIB 和位图。分桶后只在跨桶时重建，桶内的几像素差异由
/// Stretch=Fill 吸收（对模糊背景完全不可见）。
/// </summary>
internal sealed class LiveGrab : IDisposable
{
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    private IntPtr _screenDc;
    private IntPtr _memoryDc;
    private IntPtr _previous;
    private IntPtr _dib;
    private IntPtr _bits;
    private int _width;
    private int _height;
    private WriteableBitmap? _bitmap;

    /// <summary>
    /// 确保资源尺寸匹配；跨桶时重建，否则复用。失败返回 null。
    /// </summary>
    public WriteableBitmap? EnsureSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 12_000_000)
        {
            return null;
        }

        // 分桶：向上取整到 32 的倍数
        width = (width + 31) / 32 * 32;
        height = (height + 31) / 32 * 32;

        if (_bitmap is not null && _width == width && _height == height)
        {
            return _bitmap;
        }

        ReleaseGdi();
        _width = width;
        _height = height;

        _screenDc = GetDC(IntPtr.Zero);
        if (_screenDc == IntPtr.Zero)
        {
            return null;
        }

        _memoryDc = CreateCompatibleDC(_screenDc);
        if (_memoryDc == IntPtr.Zero)
        {
            ReleaseGdi();
            return null;
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
            return null;
        }

        _previous = SelectObject(_memoryDc, _dib);
        if (_previous == IntPtr.Zero || _previous == new IntPtr(-1))
        {
            ReleaseGdi();
            return null;
        }

        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        return _bitmap;
    }

    /// <summary>
    /// 抓屏幕 (x, y) 起的一帧并灌进位图。位图实例在尺寸不变时保持不变
    /// （WritePixels 自身会使位图失效并触发重绘）。失败返回 null。
    /// </summary>
    public WriteableBitmap? GrabFrame(int x, int y)
    {
        if (_bitmap is null || _memoryDc == IntPtr.Zero)
        {
            return null;
        }

        // CAPTUREBLT：连分层/合成窗口一起抓 —— 必须与静态路径一致，
        // 否则拖动前后背景内容会对不上（分层浮窗时有时无）
        if (!BitBlt(_memoryDc, 0, 0, _width, _height, _screenDc, x, y, SRCCOPY | CAPTUREBLT))
        {
            return null;
        }

        GdiFlush();
        _bitmap.WritePixels(new Int32Rect(0, 0, _width, _height), _bits, _width * _height * 4, _width * 4);
        return _bitmap;
    }

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
    }

    public void Dispose()
    {
        ReleaseGdi();
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
