using System;
using System.IO;
using System.Text;

namespace GlassLauncher.Chrome;

/// <summary>
/// 诊断日志。
///
/// 发布版默认也写（之前只在 DEBUG / 显式环境变量下写，结果现场只有一行"程序挂了"，
/// 完全没有定性线索——那次启动崩溃就是这么变成悬案的）。写入量很小（只有状态切换、
/// 拖动收尾这类事件），并且带 1MB 轮转，不会把磁盘写满。
/// 不想留日志可以设 GLASSLAUNCHER_LOG=0。
/// </summary>
internal static class Diag
{
    private const long MaxLogBytes = 1024 * 1024;

    private static readonly object Gate = new();

    internal static readonly string LogPath = ResolveLogPath();

    /// <summary>
    /// 日志位置。测试/便携场景可用 GLASSLAUNCHER_LOG_DIR 指到别处，
    /// 避免污染用户真实目录（回归测试要求在沙箱/临时目录里也能拿到日志）。
    /// </summary>
    private static string ResolveLogPath()
    {
        string? dir = Environment.GetEnvironmentVariable("GLASSLAUNCHER_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            return Path.Combine(dir, "glass.log");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlassLauncher",
            "glass.log");
    }

    private static readonly bool Verbose = ResolveVerbose();

    // 目录只在第一次写时建一次；文件大小在内存里累计（写一行就查一次 FileInfo 是每次
    // 两条元数据 IO，而 Log 有一半调用发生在 UI 线程的拖动收尾路径上）
    private static bool _directoryReady;
    private static bool _sizeKnown;
    private static long _writtenBytes;

    internal static void Log(string message)
    {
        if (!Verbose)
        {
            return;
        }

        try
        {
            string line = string.Concat(DateTime.Now.ToString("HH:mm:ss.fff"), "  ", message, Environment.NewLine);

            // 上限按 UTF-8 字节算（日志里有大量中文，按字符算会导致实际文件 1.5–3 倍于上限）
            int bytes = Encoding.UTF8.GetByteCount(line);

            lock (Gate)
            {
                if (!_sizeKnown)
                {
                    _sizeKnown = true;
                    _writtenBytes = new FileInfo(LogPath) is { Exists: true } info ? info.Length : 0;
                }

                if (_writtenBytes + bytes > MaxLogBytes)
                {
                    Rotate();
                }

                AppendWithRetry(line, bytes);
            }
        }
        catch
        {
            // 诊断日志失败不影响主流程
        }
    }

    /// <summary>
    /// 写一行并累计字节数。目录缓存失效（运行中被清理、权限瞬变）时重建目录重试一次，
    /// 不能因为一次 DirectoryNotFoundException 就让之后的日志永久静默。
    /// </summary>
    private static void AppendWithRetry(string line, int bytes)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!_directoryReady)
                {
                    string? dir = Path.GetDirectoryName(LogPath);
                    if (dir is not null)
                    {
                        Directory.CreateDirectory(dir);
                    }

                    _directoryReady = true;
                }

                File.AppendAllText(LogPath, line);
                _writtenBytes += bytes;
                return;
            }
            catch (Exception ex) when (attempt == 0 && (ex is IOException or UnauthorizedAccessException))
            {
                _directoryReady = false;
            }
        }
    }

    private static bool ResolveVerbose()
    {
        // 显式关闭优先（排查"日志里有隐私"的用户）
        if (Environment.GetEnvironmentVariable("GLASSLAUNCHER_LOG") == "0")
        {
            return false;
        }

        return true;
    }

    private static void Rotate()
    {
        try
        {
            File.Copy(LogPath, LogPath + ".1", overwrite: true);
            File.WriteAllText(LogPath, string.Empty);
            _writtenBytes = 0;
        }
        catch
        {
            // 轮转失败就继续写原文件（只是会超过上限，不影响功能）
        }
    }

    internal static void Reset()
    {
        if (!Verbose)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                if (File.Exists(LogPath))
                {
                    File.Delete(LogPath);
                }

                _writtenBytes = 0;
                _sizeKnown = true;
            }
        }
        catch
        {
            // ignore
        }
    }
}
