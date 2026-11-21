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

    /// <summary>抓取屏幕上一块矩形（物理像素），返回 BGRA32（自上而下）。失败返回 null。</summary>
    internal static byte[]? Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 12_000_000)
        {
            return null;
        }

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
                    Width = width,
                    Height = -height, // 负高度 = 自上而下
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

            // CAPTUREBLT：连分层/合成窗口一起抓，否则部分浮层会缺失
            if (!BitBlt(memory, 0, 0, width, height, screen, x, y, SRCCOPY | CAPTUREBLT))
            {
                return null;
            }

            GdiFlush();

            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            // BitBlt 不写 alpha，统一补成不透明，否则贴到 WPF 上会整片透明
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
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

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();
}
