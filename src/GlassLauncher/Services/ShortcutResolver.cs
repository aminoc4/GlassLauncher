using System;
using System.IO;
using System.Text;
using GlassLauncher.Models;
using Lnk;

namespace GlassLauncher.Services;

/// <summary>
/// 把拖入的路径解析成 <see cref="AppEntry"/>。
///
/// 支持四类输入：
///   · .lnk  → Lnk 包纯托管解析（不碰 COM / WScript.Shell），取 TargetPath / Arguments / WorkingDirectory / IconLocation
///   · .url  → 按 INI 读 [InternetShortcut] 的 URL
///   · 目录   → 直接作为目标（打开资源管理器）
///   · 其他文件（.exe 等）→ 文件自身作为目标
///
/// 注意：这里只做“解析”，不做图标提取（阶段 3）与持久化（阶段 4）。
/// </summary>
public static class ShortcutResolver
{
    static ShortcutResolver()
    {
        // Lnk 包内部用 Encoding.GetEncoding(1252) 解析 ANSI 字段。
        // .NET Core 默认不带非 Unicode 代码页，不注册就会对所有真实 .lnk 抛 NotSupportedException。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>尝试把路径解析成条目。失败时返回 false 并给出原因，不抛异常。</summary>
    public static bool TryCreate(string sourcePath, out AppEntry entry, out string error)
    {
        entry = new AppEntry();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            error = "路径为空";
            return false;
        }

        string cleaned = CleanPath(sourcePath);

        if (!File.Exists(cleaned) && !Directory.Exists(cleaned))
        {
            error = "路径不存在：" + cleaned;
            return false;
        }

        entry = new AppEntry
        {
            Id = Guid.NewGuid(),
            SourcePath = cleaned
        };

        string ext = Path.GetExtension(cleaned).ToLowerInvariant();

        try
        {
            switch (ext)
            {
                case ".lnk":
                    return TryFromLnkFile(cleaned, entry, out error);

                case ".url":
                    return TryFromUrlFile(cleaned, entry, out error);

                default:
                    return TryFromPlainPath(cleaned, entry, out error);
            }
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + "：" + ex.Message;
            return false;
        }
    }

    /// <summary>解析 .lnk：目标、参数、工作目录、图标位置。</summary>
    private static bool TryFromLnkFile(string lnkPath, AppEntry entry, out string error)
    {
        error = string.Empty;

        var file = new LnkFile(File.ReadAllBytes(lnkPath), lnkPath);

        string? target = FirstNonEmpty(
            SafeGet(() => file.LocalPath),
            SafeGet(() => file.CommonPath),
            ResolveRelative(SafeGet(() => file.RelativePath), Path.GetDirectoryName(lnkPath)));

        if (string.IsNullOrWhiteSpace(target))
        {
            error = "快捷方式没有可用的目标路径";
            return false;
        }

        target = CleanPath(target);

        entry.TargetPath = target;
        entry.Arguments = SafeGet(() => file.Arguments) ?? string.Empty;
        entry.WorkingDirectory = CleanPath(SafeGet(() => file.WorkingDirectory) ?? string.Empty);
        entry.IconLocation = SafeGet(() => file.IconLocation) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(entry.WorkingDirectory))
        {
            entry.WorkingDirectory = GuessWorkingDirectory(target);
        }

        entry.DisplayName = Path.GetFileNameWithoutExtension(lnkPath);

        return true;
    }

    /// <summary>解析 .url：本质是 INI，取 URL= 一行。</summary>
    private static bool TryFromUrlFile(string urlPath, AppEntry entry, out string error)
    {
        error = string.Empty;

        string? url = null;
        foreach (string line in File.ReadAllLines(urlPath))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
            {
                url = trimmed[4..].Trim();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "网络快捷方式里没有 URL";
            return false;
        }

        entry.TargetPath = url;
        entry.DisplayName = Path.GetFileNameWithoutExtension(urlPath);
        return true;
    }

    /// <summary>目录或其他文件：自身即目标。</summary>
    private static bool TryFromPlainPath(string path, AppEntry entry, out string error)
    {
        error = string.Empty;

        if (Directory.Exists(path))
        {
            entry.TargetPath = path;
            entry.WorkingDirectory = path;
            entry.DisplayName = new DirectoryInfo(path).Name;
            return true;
        }

        entry.TargetPath = path;
        entry.WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty;
        entry.DisplayName = Path.GetFileNameWithoutExtension(path);
        return true;
    }

    /// <summary>去掉引号、展开 %SystemRoot% 这类环境变量、规整路径。</summary>
    public static string CleanPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string value = path.Trim().Trim('"');
        value = Environment.ExpandEnvironmentVariables(value);

        try
        {
            if (value.Contains(':') && !value.StartsWith(@"\\", StringComparison.Ordinal))
            {
                value = Path.GetFullPath(value);
            }
        }
        catch
        {
            // 含非法字符时保留原样，交给后续存在性检查报错
        }

        return value;
    }

    /// <summary>把 .lnk 里的相对路径按快捷方式所在目录还原成绝对路径。</summary>
    private static string? ResolveRelative(string? relative, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(relative) || string.IsNullOrWhiteSpace(baseDirectory))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(baseDirectory, relative));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>没有工作目录时，按目标类型猜一个合理的默认值。</summary>
    private static string GuessWorkingDirectory(string target)
    {
        if (Directory.Exists(target))
        {
            return target;
        }

        return Path.GetDirectoryName(target) ?? string.Empty;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Lnk 包在字段缺失时会抛异常，逐字段兜住。</summary>
    private static string? SafeGet(Func<string?> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return null;
        }
    }
}
