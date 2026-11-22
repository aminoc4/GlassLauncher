using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GlassLauncher.Models;

namespace GlassLauncher.Services;

/// <summary>
/// 设置的本地持久化（settings.json，与 launcher-data.json 同目录）。
/// 写入策略与 LauncherStore 一致：临时文件 + 原子替换，防半截 JSON。
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string FilePath { get; } = Path.Combine(LauncherStore.DataDirectory, "settings.json");

    /// <summary>读取设置。文件缺失返回默认值；损坏时也返回默认值（设置丢了可以重调，应用列表不能）。</summary>
    public static LauncherSettings Load(out string? error)
    {
        error = null;

        try
        {
            if (!File.Exists(FilePath))
            {
                return new LauncherSettings();
            }

            string json = File.ReadAllText(FilePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new LauncherSettings();
            }

            LauncherSettings? settings = JsonSerializer.Deserialize<LauncherSettings>(json, ReadOptions);
            if (settings is null)
            {
                return new LauncherSettings();
            }

            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            return new LauncherSettings();
        }
    }

    /// <summary>原子写入设置。失败时返回 false 并给出原因。</summary>
    public static bool Save(LauncherSettings settings, out string? error)
    {
        error = null;

        try
        {
            Directory.CreateDirectory(LauncherStore.DataDirectory);

            string json = JsonSerializer.Serialize(settings, WriteOptions);
            string tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(FilePath))
            {
                try
                {
                    File.Replace(tempPath, FilePath, null, ignoreMetadataErrors: true);
                }
                catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
                {
                    File.Copy(tempPath, FilePath, overwrite: true);
                    File.Delete(tempPath);
                }
            }
            else
            {
                File.Move(tempPath, FilePath);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }
}
