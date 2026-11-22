using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GlassLauncher.Models;

namespace GlassLauncher.Services;

/// <summary>
/// 启动器数据的本地持久化。
///
/// 写入用“临时文件 → File.Replace”的原子替换：直接 WriteAllText 一旦写到一半进程被杀，
/// 留下的是半截 JSON，下次启动解析直接失败，整个启动器变空。File.Replace 在 NTFS 上是原子的。
/// </summary>
public static class LauncherStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // 不把中文转成 \uXXXX，数据文件要能直接读
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>数据与图标缓存共用的根目录。</summary>
    public static string DataDirectory { get; } = ResolveDataDirectory();

    /// <summary>%LocalAppData%\GlassLauncher\launcher-data.json</summary>
    public static string DataFilePath { get; } = Path.Combine(DataDirectory, "launcher-data.json");

    /// <summary>读取全部条目，按 SortOrder 还原顺序。失败时返回空列表并给出原因，不抛异常。</summary>
    public static List<AppEntry> Load(out string? error)
    {
        error = null;

        if (!File.Exists(DataFilePath))
        {
            return new List<AppEntry>();
        }

        try
        {
            string json = File.ReadAllText(DataFilePath, Encoding.UTF8);
            return ParseOrEmpty(json);
        }
        catch (JsonException ex)
        {
            // 主文件坏了：绝不能直接返回空列表 —— 之后任何一次 Persist() 都会用空列表
            // 把用户仅有的数据覆盖掉（旧实现就是这样静默丢数据的）。
            // 流程：先留一份现场，再尝试 .bak；备份可用就扶正它。
            string? preserved = TryPreserveCorrupt();
            List<AppEntry>? recovered = TryRecoverFromBackup();

            if (recovered is { Count: > 0 })
            {
                error = $"数据文件损坏，已从备份恢复 {recovered.Count} 项"
                    + (preserved is null ? string.Empty : $"（损坏文件已留存：{preserved}）");
                return recovered;
            }

            error = $"数据文件损坏且备份不可用：{ex.Message}"
                + (preserved is null ? string.Empty : $"（损坏文件已留存：{preserved}）");
            return new List<AppEntry>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            return new List<AppEntry>();
        }
    }

    private static List<AppEntry> ParseOrEmpty(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<AppEntry>();
        }

        List<AppEntry>? list = JsonSerializer.Deserialize<List<AppEntry>>(json, ReadOptions);
        if (list is null)
        {
            return new List<AppEntry>();
        }

        // 空 Id 的条目无法定位图标缓存，补一个
        foreach (AppEntry entry in list)
        {
            if (entry.Id == Guid.Empty)
            {
                entry.Id = Guid.NewGuid();
            }
        }

        return list.OrderBy(e => e.SortOrder).ToList();
    }

    /// <summary>把损坏的主文件复制一份留证（不删原文件，用户还有手工抢救的机会）。</summary>
    private static string? TryPreserveCorrupt()
    {
        try
        {
            string path = DataFilePath + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            File.Copy(DataFilePath, path, overwrite: true);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>用上一次原子替换留下的 .bak 恢复；成功时顺手把主文件扶正。</summary>
    private static List<AppEntry>? TryRecoverFromBackup()
    {
        try
        {
            string backupPath = DataFilePath + ".bak";
            if (!File.Exists(backupPath))
            {
                return null;
            }

            List<AppEntry> recovered = ParseOrEmpty(File.ReadAllText(backupPath, Encoding.UTF8));
            if (recovered.Count == 0)
            {
                return null;
            }

            File.Copy(backupPath, DataFilePath, overwrite: true);
            return recovered;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>原子写入全部条目。失败时返回 false 并给出原因。</summary>
    public static bool Save(IReadOnlyList<AppEntry> entries, out string? error)
    {
        error = null;

        try
        {
            Directory.CreateDirectory(DataDirectory);

            string json = JsonSerializer.Serialize(entries, WriteOptions);
            string tempPath = DataFilePath + ".tmp";
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(DataFilePath))
            {
                try
                {
                    // NTFS 原子替换，同时留一份 .bak 以备恢复
                    File.Replace(tempPath, DataFilePath, DataFilePath + ".bak", ignoreMetadataErrors: true);
                }
                catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
                {
                    // 非 NTFS（U 盘 / 某些网络盘）不支持 Replace，退化为覆盖
                    File.Copy(tempPath, DataFilePath, overwrite: true);
                    File.Delete(tempPath);
                }
            }
            else
            {
                File.Move(tempPath, DataFilePath);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string ResolveDataDirectory()
    {
        // 调试期允许把数据目录指到别处，便于用独立数据文件验证持久化而不污染真实数据
#if DEBUG
        string? overrideDir = Environment.GetEnvironmentVariable("GLASSLAUNCHER_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return overrideDir;
        }
#endif
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlassLauncher");
    }
}
