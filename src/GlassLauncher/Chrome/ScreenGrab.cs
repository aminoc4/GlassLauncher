using System;
using System.Runtime.InteropServices;

namespace GlassLauncher.Chrome;

/// <summary>
/// 抓取窗口背后那一块屏幕的真实像素（含桌面图标、其他窗口）。
///
/// 之所以能做“真玻璃”：抓之前给本窗口打开 <c>WDA_EXCLUDEFROMCAPTURE</c>，
/// 系统就会把它从抓屏结果里剔掉，于是 BitBlt 拿到的是“它盖住的那片桌面”，
/// 而不是它自己；抓完立刻恢复。这个开关只允许发生在【静止】状态 ——
/// build 26300 上排除开着进入模态拖动会让 DWM 停止呈现整个窗口。
///
/// 这条路完全不经过 DWM 的模糊，所以窗口失焦也不会退化成灰板
/// —— 这正是 Acrylic 那条路解决不了的问题。
/// </summary>
internal static class ScreenGrab
{
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    /// <summary>WDA_EXCLUDEFROMCAPTURE：窗口对捕获 API 不可见（只影响抓屏，不影响显示）。</summary>
    internal const uint WDA_NONE = 0x00000000;
    internal const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    internal static bool TrySetExclusion(IntPtr hwnd, bool exclude)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        return SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    internal static uint GetExclusion(IntPtr hwnd)
    {
        return hwnd != IntPtr.Zero && GetWindowDisplayAffinity(hwnd, out uint affinity) ? affinity : uint.MaxValue;
    }

    /// <summary>
    /// 抓取屏幕上一块矩形（物理像素），返回 BGRA32（自上而下）。失败返回 null。
    ///
    /// 返回值尺寸是 <paramref name="destWidth"/> × <paramref name="destHeight"/>：
    /// 源矩形用 StretchBlt 直接缩放到目标尺寸（HALFTONE，按面积平均，不是点采样）。
    /// 背景层用 1/2 分辨率抓，模糊的中间表面面积就只剩 1/4 —— 满分辨率下两个 BlurEffect
    /// 要吃掉 83MB 私有内存，是程序最大的单项开销。
    ///
    /// patchAlpha=false 时跳过"把 alpha 全刷成 255"的全图循环，调用方必须用
    /// <see cref="PixelFormats.Bgr32"/> 上屏（alpha 被忽略，效果等价）。
    /// 静态冻结帧走这条路，省掉一次全图遍历；光学玻璃要真实 alpha，保持默认 true。
    ///
    /// buffer 传入可复用的像素缓冲（长度够用就原地写）。返回值是**实际使用**的缓冲
    /// （传进来的不够大时会新建），调用方负责保存。
    /// </summary>
    internal static byte[]? Capture(
        int x,
        int y,
        int sourceWidth,
        int sourceHeight,
        int destWidth,
        int destHeight,
        bool patchAlpha = true,
        byte[]? buffer = null)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || destWidth <= 0 || destHeight <= 0
            || (long)sourceWidth * sourceHeight > 12_000_000)
        {
            return null;
        }

        int bytes = destWidth * destHeight * 4;

        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            return null;
        }

        IntPtr memory = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        IntPtr bits = IntPtr.Zero;

        try
        {
            memory = CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero)
            {
                return null;
            }

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = destWidth,
                    Height = -destHeight, // 负高度 = 自上而下
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                }
            };

            // DIB section 直接把内存交给 GDI，省掉一次整屏 GetDIBits 拷贝
            bitmap = CreateDIBSection(screen, ref info, 0, out bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return null;
            }

            previous = SelectObject(memory, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1))
            {
                return null;
            }

            bool scaled = sourceWidth != destWidth || sourceHeight != destHeight;
            bool blitted;

            if (scaled)
            {
                // HALFTONE：按目标像素做面积平均。点采样会把桌面细文字打碎成噪点，
                // 模糊之后就是一层脏灰（这正是以前"降采样背景发脏"的原因）。
                _ = SetStretchBltMode(memory, HALFTONE);
                _ = SetBrushOrgEx(memory, 0, 0, IntPtr.Zero);

                // CAPTUREBLT：连分层/合成窗口一起抓，否则部分浮层会缺失
                blitted = StretchBlt(memory, 0, 0, destWidth, destHeight,
                    screen, x, y, sourceWidth, sourceHeight, SRCCOPY | CAPTUREBLT);
            }
            else
            {
                blitted = BitBlt(memory, 0, 0, destWidth, destHeight, screen, x, y, SRCCOPY | CAPTUREBLT);
            }

            if (!blitted)
            {
                return null;
            }

            GdiFlush();

            byte[] pixels = buffer is not null && buffer.Length >= bytes ? buffer : new byte[bytes];
            Marshal.Copy(bits, pixels, 0, bytes);

            if (patchAlpha)
            {
                // 用 StretchBlt 时 alpha 位同样是垃圾值，统一补成不透明
                for (int i = 3; i < bytes; i += 4)
                {
                    pixels[i] = 255;
                }
            }

            return pixels;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (memory != IntPtr.Zero && previous != IntPtr.Zero && previous != new IntPtr(-1))
            {
                _ = SelectObject(memory, previous);
            }

            if (bitmap != IntPtr.Zero)
            {
                _ = DeleteObject(bitmap);
            }

            if (memory != IntPtr.Zero)
            {
                _ = DeleteDC(memory);
            }

            _ = ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, uint rop);

    private const int HALFTONE = 4;

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(IntPtr dst, int x, int y, int width, int height,
        IntPtr src, int srcX, int srcY, int srcWidth, int srcHeight, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();
}
