using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GlassLauncher.Models;

namespace GlassLauncher.Services;

/// <summary>失效检测结果。</summary>
public readonly record struct BrokenCheckResult(AppEntry Entry, bool IsBroken);

/// <summary>
/// 启动时批量检查 TargetPath 是否还存在。
///
/// 必须放在 Task.Run 里：File.Exists 遇到网络路径（\\server\share\app.exe）会阻塞到 TCP 超时，
/// 几十个条目串行检查能把界面冻住好几秒。
/// </summary>
public static class BrokenChecker
{
    public static Task<IReadOnlyList<BrokenCheckResult>> CheckAsync(IReadOnlyList<AppEntry> entries)
    {
        // 快照，避免检查过程中集合被拖拽改动
        var snapshot = new AppEntry[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            snapshot[i] = entries[i];
        }

        return Task.Run<IReadOnlyList<BrokenCheckResult>>(() =>
        {
            var results = new List<BrokenCheckResult>(snapshot.Length);

            foreach (AppEntry entry in snapshot)
            {
                // 网址不需要检查文件是否存在
                if (entry.IsUrl)
                {
                    results.Add(new BrokenCheckResult(entry, false));
                    continue;
                }

                results.Add(new BrokenCheckResult(entry, !PathExists(entry.TargetPath)));
            }

            return results;
        });
    }

    public static bool PathExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (File.Exists(path))
            {
                return true;
            }

            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
