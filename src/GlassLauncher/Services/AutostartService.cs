using Microsoft.Win32;

namespace GlassLauncher.Services;

/// <summary>
/// 开机自动启动：HKCU\...\Run 写值，普通用户权限即可，不需要管理员。
/// 可执行文件路径用 Environment.ProcessPath（.NET 5+ 专用，比 Assembly.Location 对
/// 单文件发布友好 —— 单文件发布时后者为空）。
/// </summary>
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GlassLauncher";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (enabled)
            {
                string? exePath = Environment.ProcessPath;

                if (string.IsNullOrEmpty(exePath))
                {
                    return;
                }

                key.SetValue(ValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 注册表写不进去（企业策略锁定等）就静默放弃，不打断设置流程
        }
    }
}
