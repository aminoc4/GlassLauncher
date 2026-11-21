using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace GlassLauncher.Chrome;

/// <summary>
/// 系统托盘图标。WPF 没有内置 NotifyIcon，这里直接走 Shell_NotifyIcon：
/// 回调消息由一个 message-only 窗口（HWND_MESSAGE）接收，它不占任务栏、不可见，
/// 用 HwndSource 创建省去手写 RegisterClass/CreateWindowEx。
///
/// 右键菜单用原生 HMENU（TrackPopupMenu + TPM_RETURNCMD）而不是 WPF ContextMenu：
/// WPF 的 Popup 依赖 PlacementTarget 可见，而托盘菜单最常见的时机恰恰是主窗口
/// 已经 Hide() 到托盘的时候 —— 那时 WPF 菜单大概率弹不出来。
/// 菜单前必须 SetForegroundWindow，否则点菜单外区域它不会收起（MSDN 明文要求）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    // 回调消息用 WM_APP 区间，避开系统保留的 WM_USER 区间
    private const uint WmTrayCallback = 0x8000 + 7;   // WM_APP + 7

    private const uint WmLbuttonUp = 0x0202;
    private const uint WmRbuttonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;

    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NifMessage = 0x0001;
    private const uint NifIcon = 0x0002;
    private const uint NifTip = 0x0004;

    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;

    private const int CmdOpen = 1;
    private const int CmdSettings = 2;
    private const int CmdExit = 3;

    public event Action? LeftClick;
    public event Action? MenuOpen;
    public event Action? MenuSettings;
    public event Action? MenuExit;

    private readonly HwndSource _source;
    private readonly uint _taskbarCreatedMessage;
    private readonly Icon _icon;
    private bool _added;
    private bool _disposed;

    public TrayIcon(Icon icon, string tooltip)
    {
        _icon = icon;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        var parameters = new HwndSourceParameters("GlassLauncher.Tray")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE：message-only 窗口
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        AddOrUpdate(tooltip);
    }

    private void AddOrUpdate(string tooltip)
    {
        var data = new NotifyIconData
        {
            CbSize = Marshal.SizeOf<NotifyIconData>(),
            HWnd = _source.Handle,
            UId = 1,
            UFlags = NifMessage | NifIcon | NifTip,
            UCallbackMessage = WmTrayCallback,
            HIcon = _icon.Handle,
            SzTip = tooltip,
        };

        if (!_added)
        {
            Shell_NotifyIcon(NimAdd, ref data);
            _added = true;
        }
        else
        {
            Shell_NotifyIcon(NimModify, ref data);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)WmTrayCallback)
        {
            uint mouseMessage = (uint)lParam & 0xFFFF;

            switch (mouseMessage)
            {
                case WmLbuttonUp:
                    LeftClick?.Invoke();
                    handled = true;
                    break;

                case WmRbuttonUp:
                case WmContextMenu:
                    ShowMenu();
                    handled = true;
                    break;
            }
        }
        else if (msg == (int)_taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            // 资源管理器重启后托盘会被清空，重新添加
            _added = false;
            AddOrUpdate("Glass Launcher");
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        // TrackPopupMenu 的前置条件：先把自己设为前台窗口，否则点菜单外它不收起
        SetForegroundWindow(_source.Handle);

        IntPtr menu = CreatePopupMenu();
        AppendMenu(menu, MfString, CmdOpen, "打开启动器");
        AppendMenu(menu, MfString, CmdSettings, "设置…");
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, MfString, CmdExit, "退出");
        SetMenuDefaultItem(menu, CmdOpen, false);

        GetCursorPos(out Point cursor);

        int command = TrackPopupMenu(menu, TpmReturnCmd | TpmRightButton, cursor.X, cursor.Y,
            0, _source.Handle, IntPtr.Zero);
        DestroyMenu(menu);

        switch (command)
        {
            case CmdOpen:
                MenuOpen?.Invoke();
                break;
            case CmdSettings:
                MenuSettings?.Invoke();
                break;
            case CmdExit:
                MenuExit?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_added)
        {
            var data = new NotifyIconData
            {
                CbSize = Marshal.SizeOf<NotifyIconData>(),
                HWnd = _source.Handle,
                UId = 1,
            };
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }

        _icon.Dispose();
        _source.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int CbSize;
        public IntPtr HWnd;
        public uint UId;
        public uint UFlags;
        public uint UCallbackMessage;
        public IntPtr HIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string SzTip;

        public uint DwState;
        public uint DwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string SzInfo;

        public uint UVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string SzInfoTitle;

        public uint DwInfoFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? item);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, bool byPosition);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y,
        int reserved, IntPtr hWnd, IntPtr reserved2);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}
