using System;

namespace GlassLauncher.Models;

/// <summary>主题策略。</summary>
public enum ThemeMode
{
    FollowSystem,
    Light,
    Dark,
}

/// <summary>
/// 启动器用户设置。独立于 launcher-data.json 存 settings.json，
/// 两份文件互不牵连：动设置不会重写应用列表，动列表也不会碰设置。
/// </summary>
public class LauncherSettings
{
    public int SettingsVersion { get; set; } = 1;

    /// <summary>主题：跟随系统 / 强制浅色 / 强制深色。</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.FollowSystem;

    /// <summary>背景模糊半径（GPU 高斯）。12 是实测验收值。</summary>
    public double BlurRadius { get; set; } = 12;

    /// <summary>窗口置顶（启动器类应用的常见需求）。</summary>
    public bool Topmost { get; set; }

    /// <summary>启动时后台检查失效条目（打上感叹角标）。</summary>
    public bool CheckBrokenOnStart { get; set; } = true;

    /// <summary>
    /// 显示/隐藏启动器的全局快捷键，格式 "Ctrl+Alt+G"（修饰键在前）。
    /// 默认 Alt+G —— 原本想用 PowerToys Run 同款的 Alt+Space，但它在本机
    /// （以及其他装了窗口管理类工具的机器）常被占走，注册失败等于没有快捷键。
    /// 被占用时启动会提示，可在设置里换成任意组合。
    /// </summary>
    public string Hotkey { get; set; } = "Alt+G";

    /// <summary>读回旧文件后夹紧越界值，防止手改 JSON 出极端参数。</summary>
    public void Normalize()
    {
        BlurRadius = Math.Clamp(BlurRadius, 2, 32);
    }
}
