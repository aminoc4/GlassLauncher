using System;
using System.IO;

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

    internal static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GlassLauncher",
        "glass.log");

    private static readonly bool Verbose = ResolveVerbose();

    internal static void Log(string message)
    {
        if (!Verbose)
        {
            return;
        }

        try
        {
            string? dir = Path.GetDirectoryName(LogPath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            lock (Gate)
            {
                RotateIfNeeded();
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 诊断日志失败不影响主流程
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

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxLogBytes)
            {
                return;
            }

            string rolled = LogPath + ".1";
            File.Copy(LogPath, rolled, overwrite: true);
            File.WriteAllText(LogPath, string.Empty);
        }
        catch
        {
            // 轮转失败就继续写原文件
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
            if (File.Exists(LogPath))
            {
                File.Delete(LogPath);
            }
        }
        catch
        {
            // ignore
        }
    }
}
