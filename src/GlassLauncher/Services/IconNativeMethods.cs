using System;
using System.Runtime.InteropServices;

namespace GlassLauncher.Services;

/// <summary>
/// 图标提取用到的 shell / user32 函数。全部是系统 API，不引入第三方包。
/// </summary>
internal static class IconNativeMethods
{
    /// <summary>文件信息结构，SHGetFileInfo 用。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEINFO
    {
        internal IntPtr hIcon;
        internal int iIcon;
        internal uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        internal string szTypeName;
    }

    internal const uint SHGFI_ICON = 0x000000100;
    internal const uint SHGFI_LARGEICON = 0x000000000;
    internal const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    /// <summary>
    /// 按索引提取图标，并可指定尺寸（LOWORD 大图标边长、HIWORD 小图标边长）。
    /// 相比 ExtractIconEx 的关键优势：能要到 256px，且同样尊重索引。
    /// 返回 S_OK(0) 或 S_FALSE(1)，S_FALSE 表示没有对应索引、返回了默认图标。
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHDefExtractIconW(
        string pszIconFile,
        int iIndex,
        uint uFlags,
        out IntPtr phiconLarge,
        out IntPtr phiconSmall,
        uint nIconSize);

    /// <summary>经典按索引提取，大图标固定为 SM_CXICON（通常 32px）。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint ExtractIconExW(
        string lpszFile,
        int nIconIndex,
        IntPtr[]? phiconLarge,
        IntPtr[]? phiconSmall,
        uint nIcons);

    /// <summary>保底：拿 shell 为该路径显示的默认图标（32px 量级）。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern IntPtr SHGetFileInfoW(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr hObject);

    /// <summary>把大小塞进一个 uint：低 16 位大图标、高 16 位小图标。</summary>
    internal static uint PackIconSize(int large, int small) => (uint)((large & 0xFFFF) | ((small & 0xFFFF) << 16));

    // ── 第 4 级：shell 项目图像（能拿到 256px，文件夹/驱动器/UWP 靠这个） ──

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        internal int cx;
        internal int cy;

        internal SIZE(int side)
        {
            cx = side;
            cy = side;
        }
    }

    /// <summary>SIIGBF_ICONONLY：要图标而不是缩略图。</summary>
    internal const uint SIIGBF_ICONONLY = 0x00000004;

    /// <summary>SIIGBF_BIGGERSIZEOK：允许返回比请求更大的图，由调用方缩。</summary>
    internal const uint SIIGBF_BIGGERSIZEOK = 0x00000001;

    /// <summary>
    /// IShellItemImageFactory：Vista 起 shell 提供的"按路径要图"的接口。
    /// 相比 SHGetFileInfo 的关键差别是尺寸不受系统小图标列表限制，可以要 256px。
    /// </summary>
    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        int GetImage(SIZE size, uint flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    internal static extern void SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);
}
