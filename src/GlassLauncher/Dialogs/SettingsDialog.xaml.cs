using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GlassLauncher.Models;
using GlassLauncher.Services;

namespace GlassLauncher.Dialogs;

/// <summary>
/// 设置对话框：所有改动“立即生效 + 立即落盘”，没有确定/取消的概念 ——
/// 开关和滑杆本来就没有“取消”语义，分阶段保存只会制造“改了没生效”的错觉。
/// 每次改动通过 <see cref="_onChanged"/> 回给主窗口实时应用。
/// </summary>
public partial class SettingsDialog : Chrome.GlassWindow
{
    private readonly LauncherSettings _settings;
    private readonly Action<LauncherSettings> _onChanged;

    // 控件初始化会触发 Checked/ValueChanged，用这个标记挡住“加载即保存”的回环
    private bool _loading;

    public SettingsDialog(LauncherSettings settings, Action<LauncherSettings> onChanged)
    {
        // 必须先于 InitializeComponent：XAML 解析期间 Slider 的初值就会触发一次
        // ValueChanged（BlurValue 在 Slider 之前构建，非空守卫拦不住），此刻
        // _settings 还是 null，先崩在设置页第一次打开上
        _settings = settings;
        _onChanged = onChanged;
        _loading = true;

        InitializeComponent();
        _loading = false;

        VersionText.Text = "Glass Launcher v"
            + (Assembly.GetExecutingAssembly().GetName().Version is { } v
                ? $"{v.Major}.{v.Minor}.{v.Build}"
                : "?.?.?");

        Loaded += (_, _) =>
        {
            _loading = true;

            switch (_settings.Theme)
            {
                case ThemeMode.Light:
                    ThemeLight.IsChecked = true;
                    break;
                case ThemeMode.Dark:
                    ThemeDark.IsChecked = true;
                    break;
                default:
                    ThemeFollow.IsChecked = true;
                    break;
            }

            BlurSlider.Value = _settings.BlurRadius;
            FpsSlider.Value = _settings.CaptureFps;
            ReduceBlurCheck.IsChecked = _settings.ReduceBlurWhileDragging;
            TopmostCheck.IsChecked = _settings.Topmost;
            BrokenCheck.IsChecked = _settings.CheckBrokenOnStart;
            HotkeyBox.Text = _settings.Hotkey;
            AutostartCheck.IsChecked = AutostartService.IsEnabled();

            _loading = false;
        };
    }

    /// <summary>改动落盘 + 实时应用。加载期被 _loading 挡住。</summary>
    private void Commit()
    {
        if (_loading)
        {
            return;
        }

        _settings.Normalize();

        if (!SettingsStore.Save(_settings, out string? error))
        {
            MessageBox.Show(this, $"保存设置失败：{error}", "Glass Launcher",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _onChanged(_settings);
    }

    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        _settings.Theme =
            (ThemeDark.IsChecked == true) ? ThemeMode.Dark
            : (ThemeLight.IsChecked == true) ? ThemeMode.Light
            : ThemeMode.FollowSystem;
        Commit();
    }

    private void OnBlurChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BlurValue is null)
        {
            return;
        }

        // 数值标签任何时候都刷新；但 _loading（XAML 解析 / Loaded 回填）期间的
        // 事件不能写回 _settings：解析时 WPF 会把默认值 0 收敛到最小值 4 并触发
        // 一次 ValueChanged，不拦的话用户的真实配置（12）就被 4 悄悄顶掉了
        BlurValue.Text = ((int)Math.Round(e.NewValue)).ToString();

        if (_loading)
        {
            return;
        }

        _settings.BlurRadius = e.NewValue;
        Commit();
    }

    /// <summary>抓屏速率：拖动时背景每秒抓几帧（15–120）。</summary>
    private void OnFpsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FpsValue is null)
        {
            return;
        }

        FpsValue.Text = ((int)Math.Round(e.NewValue)).ToString() + " fps";

        if (_loading)
        {
            return;
        }

        _settings.CaptureFps = e.NewValue;
        Commit();
    }

    private void OnReduceBlurChanged(object sender, RoutedEventArgs e)
    {
        if (ReduceBlurCheck is null || _loading)
        {
            return;
        }

        _settings.ReduceBlurWhileDragging = ReduceBlurCheck.IsChecked == true;
        Commit();
    }

    private void OnTopmostChanged(object sender, RoutedEventArgs e)
    {
        _settings.Topmost = TopmostCheck.IsChecked == true;
        Commit();
    }

    private void OnBrokenCheckChanged(object sender, RoutedEventArgs e)
    {
        _settings.CheckBrokenOnStart = BrokenCheck.IsChecked == true;
        Commit();
    }

    /// <summary>
    /// 快捷键捕获：输入框只读，所有按键在这里截住转成文本。
    /// Alt 在 WPF 里走 Key.System，要取 SystemKey 才是真实按键。
    /// 按下的组合立即生效（ApplySettings 里会重新注册热键）。
    /// </summary>
    private void OnHotkeyKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;

        System.Windows.Input.Key key = e.Key == System.Windows.Input.Key.System
            ? e.SystemKey
            : e.Key;
        string? text = HotkeyHelper.TryFromKeyboard(key, System.Windows.Input.Keyboard.Modifiers);

        if (text is null)
        {
            // 无效组合：不动已有设置，输入框闪一下提示（清空再还原，视觉上一次抖动）
            HotkeyBox.Text = string.Empty;
            HotkeyBox.Text = _settings.Hotkey;
            return;
        }

        HotkeyBox.Text = text;
        _settings.Hotkey = text;
        Commit();
    }

    private void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        AutostartService.SetEnabled(AutostartCheck.IsChecked == true);
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(LauncherStore.DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LauncherStore.DataDirectory}\""));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开文件夹：{ex.Message}", "Glass Launcher",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClearIconCache(object sender, RoutedEventArgs e)
    {
        string cacheDir = Path.Combine(LauncherStore.DataDirectory, "icons");

        if (!Directory.Exists(cacheDir))
        {
            MessageBox.Show(this, "图标缓存本来就是空的。", "Glass Launcher",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(cacheDir, "*.png"))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"清除失败：{ex.Message}", "Glass Launcher",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show(this, "图标缓存已清除，重启启动器后会自动重新生成。", "Glass Launcher",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();

    private void OnHeaderMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
