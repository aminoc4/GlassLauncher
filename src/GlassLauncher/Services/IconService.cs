using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassLauncher.Chrome;
using GlassLauncher.Models;

namespace GlassLauncher.Services;

/// <summary>
/// 图标提取与本地缓存。
///
/// 回退链（顺序就是优先级，按"能否按索引取到足够大的图"排序）：
///   1. IconLocation 指向 .ico 文件 → IconBitmapDecoder 直接解码，挑最接近目标尺寸的帧
///      （能读到 PNG 压缩的 256px 帧，shell 的旧接口读不到）
///   2. SHDefExtractIcon(path, index, 256)  按索引 + 可指定尺寸，主力
///   3. ExtractIconEx(path, index)          按索引，但大图标只有 SM_CXICON（100% DPI 下 32px）
///   4. 对 TargetPath 自身重复 2 → 3（IconLocation 缺失或失效时）
///   5. SHGetFileInfo(SHGFI_ICON|LARGEICON) 保底，只有 32px 量级
///   6. 全失败返回 null，界面显示首字母占位块
///
/// 注意：SHGetFileInfo 放在最后是有意的 —— 它是"shell 显示什么就给什么"，
/// 既不能按索引取，也给不了大尺寸，只适合兜底。
/// </summary>
public static class IconService
{
    /// <summary>缓存分辨率。显示尺寸 60 DIP，125% DPI 下约 75 物理像素，256px 足够清晰。</summary>
    private const int RequestedSize = 256;

    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GlassLauncher",
        "icons");

    /// <summary>为条目准备图标：优先读缓存，缓存缺失时才真正提取并落盘。</summary>
    public static ImageSource? LoadOrCreate(AppEntry entry)
    {
        string cachePath = string.IsNullOrWhiteSpace(entry.IconCachePath)
            ? Path.Combine(CacheDirectory, entry.Id.ToString("N") + ".png")
            : entry.IconCachePath;

        if (File.Exists(cachePath))
        {
            ImageSource? cached = TryLoadPng(cachePath);
            if (cached is not null)
            {
                entry.IconCachePath = cachePath;
                return cached;
            }
        }

        ImageSource? extracted = Extract(entry, RequestedSize);
        if (extracted is null)
        {
            return null;
        }

        if (TrySavePng(extracted, cachePath))
        {
            entry.IconCachePath = cachePath;
        }

        return extracted;
    }

    /// <summary>按回退链提取图标，全部失败返回 null。</summary>
    public static ImageSource? Extract(AppEntry entry, int size)
    {
        foreach ((string path, int index) in CandidateSources(entry))
        {
            ImageSource? icon = ExtractFrom(path, index, size);
            if (icon is not null)
            {
                return icon;
            }
        }

        // 第 4 级：shell 项目图像，能拿到 256px（文件夹、驱动器、UWP 必走这条）
        ImageSource? shellItem = TryShellItemImage(entry.TargetPath, size);
        if (shellItem is not null)
        {
            Diag.Log($"Icon[4 shell-item-image] {entry.TargetPath} {Describe(shellItem)}");
            return shellItem;
        }

        // 保底：shell 默认图标（不能按索引，尺寸也上不去）
        ImageSource? shellDefault = ExtractShellDefault(entry.TargetPath, entry.IsUrl);
        Diag.Log(shellDefault is null
            ? $"Icon[6 全部失败] {entry.TargetPath}"
            : $"Icon[5 shell-default] {entry.TargetPath} {Describe(shellDefault)}");

        return shellDefault;
    }

    /// <summary>生成"要按哪个文件的第几个图标去取"的候选序列（去重、保持优先级）。</summary>
    private static System.Collections.Generic.IEnumerable<(string Path, int Index)> CandidateSources(AppEntry entry)
    {
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(entry.IconLocation))
        {
            (string path, int index) = ParseIconLocation(entry.IconLocation);
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path + "#" + index) && File.Exists(path))
            {
                yield return (path, index);
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.TargetPath) && File.Exists(entry.TargetPath))
        {
            if (seen.Add(entry.TargetPath + "#0"))
            {
                yield return (entry.TargetPath, 0);
            }
        }
    }

    /// <summary>单个文件 + 索引的提取：.ico 走解码器，其余走 SHDefExtractIcon → ExtractIconEx。</summary>
    private static ImageSource? ExtractFrom(string path, int index, int size)
    {
        if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
        {
            ImageSource? decoded = TryDecodeIcoFile(path, size);
            if (decoded is not null)
            {
                Diag.Log($"Icon[1 ico-decode] {path} {Describe(decoded)}");
                return decoded;
            }
        }

        ImageSource? viaShell = TrySHDefExtractIcon(path, index, size);
        if (viaShell is not null)
        {
            Diag.Log($"Icon[2 SHDefExtractIcon] {path}#{index} {Describe(viaShell)}");
            return viaShell;
        }

        ImageSource? viaLegacy = TryExtractIconEx(path, index);
        if (viaLegacy is not null)
        {
            Diag.Log($"Icon[3 ExtractIconEx] {path}#{index} {Describe(viaLegacy)}");
            return viaLegacy;
        }

        return null;
    }

    private static string Describe(ImageSource image) =>
        image is BitmapSource bitmap ? $"{bitmap.PixelWidth}x{bitmap.PixelHeight}" : image.GetType().Name;

    /// <summary>"C:\x\app.exe,3" / "%SystemRoot%\system32\shell32.dll,-12" → (路径, 索引)。</summary>
    internal static (string Path, int Index) ParseIconLocation(string iconLocation)
    {
        string value = Environment.ExpandEnvironmentVariables(iconLocation.Trim().Trim('"'));

        int comma = value.LastIndexOf(',');
        if (comma > 0 && int.TryParse(value[(comma + 1)..].Trim(), out int index))
        {
            return (value[..comma].Trim().Trim('"'), index);
        }

        return (value, 0);
    }

    /// <summary>第 1 级：直接解码 .ico，挑最接近目标尺寸的帧。</summary>
    private static ImageSource? TryDecodeIcoFile(string path, int size)
    {
        try
        {
            var decoder = new IconBitmapDecoder(
                new Uri(path, UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            // 优先取不小于目标尺寸的最小帧，避免放大；没有就取最大帧
            BitmapFrame frame = decoder.Frames
                .OrderBy(f => f.PixelWidth)
                .FirstOrDefault(f => f.PixelWidth >= size)
                ?? decoder.Frames.OrderByDescending(f => f.PixelWidth).First();

            BitmapSource source = frame.CloneCurrentValue();
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>第 2 级：SHDefExtractIcon，按索引且能要到 256px。</summary>
    private static ImageSource? TrySHDefExtractIcon(string path, int index, int size)
    {
        IntPtr large = IntPtr.Zero;
        IntPtr small = IntPtr.Zero;

        try
        {
            int hr = IconNativeMethods.SHDefExtractIconW(
                path,
                index,
                0,
                out large,
                out small,
                IconNativeMethods.PackIconSize(size, 16));

            // S_OK=0 有该索引；S_FALSE=1 没有该索引时给了默认图标，同样可用
            if (hr is not (0 or 1))
            {
                return null;
            }

            return FromHIcon(large) ?? FromHIcon(small);
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseIcon(large);
            ReleaseIcon(small);
        }
    }

    /// <summary>第 3 级：ExtractIconEx，按索引但大图标只有 32px 量级。</summary>
    private static ImageSource? TryExtractIconEx(string path, int index)
    {
        var large = new IntPtr[1];
        var small = new IntPtr[1];

        try
        {
            uint count = IconNativeMethods.ExtractIconExW(path, index, large, small, 1);
            if (count == 0)
            {
                return null;
            }

            return FromHIcon(large[0]) ?? FromHIcon(small[0]);
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseIcon(large[0]);
            ReleaseIcon(small[0]);
        }
    }

    /// <summary>第 4 级：IShellItemImageFactory，任何 shell 项目（含文件夹）都能要到 256px。</summary>
    private static ImageSource? TryShellItemImage(string path, int size)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return null;
        }

        IntPtr hBitmap = IntPtr.Zero;

        try
        {
            Guid iid = typeof(IconNativeMethods.IShellItemImageFactory).GUID;
            IconNativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out object item);

            if (item is not IconNativeMethods.IShellItemImageFactory factory)
            {
                return null;
            }

            int hr = factory.GetImage(
                new IconNativeMethods.SIZE(size),
                IconNativeMethods.SIIGBF_ICONONLY | IconNativeMethods.SIIGBF_BIGGERSIZEOK,
                out hBitmap);

            if (hr != 0 || hBitmap == IntPtr.Zero)
            {
                return null;
            }

            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                _ = IconNativeMethods.DeleteObject(hBitmap);
            }
        }
    }

    /// <summary>第 5 级：shell 默认图标，仅保底。</summary>
    private static ImageSource? ExtractShellDefault(string targetPath, bool isUrl)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return null;
        }

        var info = new IconNativeMethods.SHFILEINFO();
        uint flags = IconNativeMethods.SHGFI_ICON | IconNativeMethods.SHGFI_LARGEICON;

        string queryPath = targetPath;
        uint attributes = 0;

        // 目标不存在（失效条目）或网络地址时，让 shell 按扩展名/类型给个通用图标，
        // 否则 SHGetFileInfo 会直接失败、连兜底图都没有
        if (isUrl || !File.Exists(targetPath) && !Directory.Exists(targetPath))
        {
            flags |= IconNativeMethods.SHGFI_USEFILEATTRIBUTES;
            attributes = IconNativeMethods.FILE_ATTRIBUTE_NORMAL;

            if (isUrl)
            {
                queryPath = Path.ChangeExtension(targetPath, ".url");
            }
        }

        try
        {
            IntPtr result = IconNativeMethods.SHGetFileInfoW(
                queryPath,
                attributes,
                ref info,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<IconNativeMethods.SHFILEINFO>(),
                flags);

            if (result == IntPtr.Zero)
            {
                return null;
            }

            return FromHIcon(info.hIcon);
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseIcon(info.hIcon);
        }
    }

    /// <summary>HICON → WPF 位图（冻结以便跨线程与缓存）。</summary>
    private static ImageSource? FromHIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    private static void ReleaseIcon(IntPtr hIcon)
    {
        if (hIcon != IntPtr.Zero)
        {
            _ = IconNativeMethods.DestroyIcon(hIcon);
        }
    }

    private static ImageSource? TryLoadPng(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static bool TrySavePng(ImageSource image, string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create((BitmapSource)image));

            using FileStream stream = File.Create(path);
            encoder.Save(stream);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
