using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using GlassLauncher.Models;

namespace GlassLauncher.Services;

/// <summary>启动结果。需要区分“用户取消了 UAC”和“真的起不来”，前者不能标记为失效。</summary>
public enum LaunchResult
{
    Started,

    /// <summary>用户在 UAC 提权弹窗点了取消。</summary>
    Cancelled,

    Failed,
}

/// <summary>启动、提权启动、在资源管理器中定位。</summary>
public static class LaunchService
{
    /// <summary>ERROR_CANCELLED：用户在 UAC 弹窗点了“否”。</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// 启动条目。
    /// UseShellExecute 必须为 true：.lnk 解析出的目标常常依赖 shell 关联（例如 pythonw.exe + 脚本参数），
    /// 设为 false 时 Process.Start 会直接按可执行文件处理，很多情况下起不来。
    /// </summary>
    public static LaunchResult TryLaunch(AppEntry entry, bool asAdministrator, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(entry.TargetPath))
        {
            error = "条目没有目标路径";
            return LaunchResult.Failed;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = entry.TargetPath,
                UseShellExecute = true,
            };

            if (!entry.IsUrl)
            {
                if (!string.IsNullOrWhiteSpace(entry.Arguments))
                {
                    startInfo.Arguments = entry.Arguments;
                }

                string workingDirectory = entry.WorkingDirectory;
                if (string.IsNullOrWhiteSpace(workingDirectory))
                {
                    workingDirectory = Path.GetDirectoryName(entry.TargetPath) ?? string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
                {
                    startInfo.WorkingDirectory = workingDirectory;
                }
            }

            if (asAdministrator)
            {
                startInfo.Verb = "runas";
            }

            Process.Start(startInfo);
            return LaunchResult.Started;
        }
        catch (Win32Exception ex)
        {
            if (ex.NativeErrorCode == ErrorCancelled)
            {
                return LaunchResult.Cancelled;
            }

            error = ex.Message;
            return LaunchResult.Failed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FileNotFoundException)
        {
            error = ex.Message;
            return LaunchResult.Failed;
        }
    }

    /// <summary>在资源管理器中定位目标；目标是网址时用默认浏览器打开。</summary>
    public static bool TryReveal(AppEntry entry, out string? error)
    {
        error = null;

        try
        {
            if (entry.IsUrl)
            {
                Process.Start(new ProcessStartInfo(entry.TargetPath) { UseShellExecute = true });
                return true;
            }

            string path = entry.TargetPath;

            if (Directory.Exists(path))
            {
                Process.Start("explorer.exe", $"\"{path}\"");
                return true;
            }

            if (File.Exists(path))
            {
                // explorer 对 /select 的参数格式很挑，引号必须只包路径
                Process.Start("explorer.exe", $"/select,\"{path}\"");
                return true;
            }

            // 目标已失效时退到它所在的目录，至少让用户看到文件去哪了
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Process.Start("explorer.exe", $"\"{directory}\"");
                return true;
            }

            error = "目标与所在目录都不存在";
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }
}
