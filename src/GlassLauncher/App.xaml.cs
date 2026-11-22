using System;
using System.Threading;
using System.Windows;
using GlassLauncher.Services;

namespace GlassLauncher;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 托盘常驻应用最怕双实例（开机自启 + 手动双击各来一份）：
        // 已有实例在跑时直接退出，不弹窗打扰
        _singleInstanceMutex = new Mutex(true, @"Local\GlassLauncher.SingleInstance", out bool createdNew);

        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        // 必须早于 StartupUri 创建窗口：窗口里的 StaticResource 在解析时就要拿到正确颜色
        bool dark = ThemeService.IsSystemDark();

#if DEBUG
        // 仅调试版：GLASSLAUNCHER_THEME=dark|light 强制明暗，便于不改系统设置就验证两套配色
        string? probe = Environment.GetEnvironmentVariable("GLASSLAUNCHER_THEME");
        if (string.Equals(probe, "dark", StringComparison.OrdinalIgnoreCase))
        {
            dark = true;
        }
        else if (string.Equals(probe, "light", StringComparison.OrdinalIgnoreCase))
        {
            dark = false;
        }
#endif

        ThemeService.Apply(dark);

        // 全局异常留痕：async void（如 FreezeBackdrop）里抛出的异常会到这里，
        // 不记的话"窗口凭空消失/行为诡异"根本没法从日志定性。
        //
        // ⚠ 必须设 e.Handled = true：不设的话 WPF 会把异常重新抛到调度线程，
        // 线程终止 → 消息泵停摆 → 窗口变成"点不动的幽灵窗口"（任务管理器显示"未响应"），
        // 然后整个进程才消失。用户感受到的是"卡死后崩溃"，而日志里只有一行异常。
        // 曾经 FreezeBackdrop 的可选参数触发的 TargetParameterCountException 就是这样把
        // 整个程序带走的（见 REVIEW-FINDINGS P0-1/P0-2）。
        DispatcherUnhandledException += (_, e) =>
        {
            Chrome.Diag.Log($"DispatcherUnhandledException: {e.Exception}");
            e.Handled = true;
        };

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
