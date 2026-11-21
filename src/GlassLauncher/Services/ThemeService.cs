using System;
using System.Windows;
using Microsoft.Win32;

namespace GlassLauncher.Services;

/// <summary>
/// 跟随系统「应用模式」的明暗主题。
/// 说明：只在启动时读取一次并切换资源字典；运行中改系统主题需重启（阶段 5 做完整玻璃分层时再补实时跟随）。
/// </summary>
public static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>当前是否处于暗色模式。</summary>
    public static bool IsDarkMode { get; private set; }

    /// <summary>读取系统「应用模式」：AppsUseLightTheme = 0 表示暗色。</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue("AppsUseLightTheme") is int useLight)
            {
                return useLight == 0;
            }
        }
        catch
        {
            // 读不到注册表时按亮色处理
        }

        return false;
    }

    /// <summary>应用主题；重复调用会原地替换颜色字典，不会叠加。</summary>
    public static void Apply(bool dark)
    {
        Application? app = Application.Current;
        if (app is null)
        {
            return;
        }

        IsDarkMode = dark;

        var dictionary = new ResourceDictionary
        {
            Source = new Uri(
                dark
                    ? "pack://application:,,,/Resources/Theme.Dark.xaml"
                    : "pack://application:,,,/Resources/Theme.Light.xaml",
                UriKind.Absolute)
        };

        // 原地替换颜色字典：靠 Source 定位，避免 App.xaml 里默认的亮色字典残留并反过来覆盖
        var merged = app.Resources.MergedDictionaries;
        int index = -1;

        for (int i = 0; i < merged.Count; i++)
        {
            string? source = merged[i].Source?.OriginalString;
            if (source is not null
                && (source.EndsWith("Theme.Light.xaml", StringComparison.OrdinalIgnoreCase)
                    || source.EndsWith("Theme.Dark.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                index = i;
                break;
            }
        }

        if (index >= 0)
        {
            merged[index] = dictionary;
        }
        else
        {
            merged.Add(dictionary);
        }
    }
}
