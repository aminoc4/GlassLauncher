using System;
using System.IO;

namespace GlassLauncher.Chrome;

/// <summary>临时诊断日志：只在 DEBUG 编译下写文件，用于确认 DWM 调用是否被系统接受。</summary>
internal static class Diag
{
    private static readonly object Gate = new();

    internal static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GlassLauncher",
        "glass.log");

    internal static void Log(string message)
    {
        // DEBUG 下始终写；Release 下只有在需要现场取证时才用 GLASSLAUNCHER_LOG=1 打开
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
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 诊断日志失败不影响主流程
        }
    }

    private static readonly bool Verbose =
#if DEBUG
        true;
#else
        Environment.GetEnvironmentVariable("GLASSLAUNCHER_LOG") == "1";
#endif

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
