using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassLauncher.Chrome;
using GlassLauncher.Dialogs;
using GlassLauncher.Models;
using GlassLauncher.Services;

namespace GlassLauncher;

public partial class MainWindow : GlassWindow
{
    private readonly ObservableCollection<AppEntry> _entries = new();
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _autoScrollTimer;

    // 外部拖入（资源管理器）的插入位置
    private int _insertIndex = -1;

    // 手势状态
    private Point _pressPoint;            // 按下时在网格里的位置
    private Point _pointerInLayer;        // 指针最新位置（DragLayer 坐标，滚动时不变）
    private AppEntry? _pressedEntry;

    // 内部拖拽排序
    private bool _dragActive;
    private int _dragFrom = -1;
    private int _dragTo = -1;
    private Vector _grabOffset;            // 按下点相对图标中心的偏移，全程不变，保证“贴手”
    private DragGhost? _ghost;
    private readonly SlotShifter _slots = new();

#if DEBUG
    // 仅调试用：合成拖拽期间屏蔽真实鼠标事件，否则光标恰好压在窗口上时
    // 一个没有按键的 MouseMove 就会把探针的手势取消掉
    private bool _synthetic;

    // 仅调试用：统计真实拖拽时 MouseMove 的到达间隔，用来判断“卡”到底是事件稀疏还是渲染慢
    private readonly System.Diagnostics.Stopwatch _dragClock = new();
    private double _lastMoveMs;
    private int _moveCount;
    private double _maxMoveGap;
#endif
    private double _autoScrollDelta;

    // 光学玻璃：抓背后像素 + CPU 合成，替代 DWM 的 Acrylic
    private readonly OpticalGlassSurface _glass;

    // 冻结帧防抖：程序化尺寸变化（最大化、编程改大小）时事件只发一两次，
    // 防抖后补抓一帧即可；交互拖动/缩放走下面的 live 模式，不经过这里
    private readonly DispatcherTimer _refreezeTimer;

    // 正在拖动/缩放窗口（模态循环里）。这个状态期间 RefreezeSoon 必须让路：
    // LocationChanged/SizeChanged 在模态循环里也会冒出来，防止重复抓屏互相干扰
    private bool _inModalMove;

    // 背景抓屏的垫边（DIP）：抓屏区域比窗口四周各大一圈，显示时 1:1 映射。
    // 这圈垫边有两个用途 —— 模糊采样在图像边缘会拿到透明而发暗（把淡出区推到窗外），
    // 以及给"帧随窗动"的拖动方式留出模糊淡出的余地。必须与 XAML 里 FrozenSurface 的
    // Margin=-48 严格一致，否则背景内容会被缩放/平移，与桌面错位。
    private const double BackdropPad = 48;

    // 冻结帧抓屏只在静止时跑（拖动期间让路排队），SetWindowDisplayAffinity
    // 的开→抓→关不与拖动重叠
    private bool _freezeBusy;

    // 排除（重新）开启的时刻（TickCount64 毫秒）。GetExclusion 只反映"开关状态"，
    // 不反映 DWM 是否已落实 —— 刚开就抓，抓到的会混着窗口自己（鬼影帧）。
    // FreezeBackdrop 依据它补足生效等待。
    private long _exclusionOnTick;

    // 截屏排除是否处于"常开"状态（永久排除策略：启动即开，只有模态循环临时关）。
    // 常开时抓帧零切换零等待；异常掉开过才需要重开 + 预热
    private bool _exclusionHeld;

    // 抓帧代数：每次进入拖动递增。FreezeBackdrop 在途时（等待/抓取中）拖动开始，
    // 在途结果作废 —— 不作废的话，一帧"拖动前位置"的过时画面会在拖动开始瞬间淡入显示
    private int _captureGeneration;

    // 排除生效延迟：SetWindowDisplayAffinity 打开后 DWM 要过一两只合成帧才真正
    // 把窗口从抓屏结果里摘掉，立刻抓会把窗口自己抓进背景（模糊后就是背景鬼影）
    private const int ExclusionSettleMs = 60;
    private const int LiveWarmupFrames = 4;

    // FreezeBackdrop 忙时到达的补帧请求标记：忙完必须补跑一次（旧实现直接丢弃）
    private bool _freezeQueued;
    private bool _freezeQueuedInstant;

    // live 背景：拖动/缩放期间每帧实抓（排除在进入模态循环时开一次、松手锚定后
    // 由 FreezeBackdrop 收尾关掉，中间反复 BitBlt 不再开关），背景像真玻璃一样跟着桌面走。
    // BitBlt 在 LiveGrab 的后台线程执行，UI 线程每帧只做几何提交与上屏
    private LiveGrab? _liveGrab;
    private bool _liveGrabActive;

    // 位移预测：本帧抓的内容要等下一次合成才上屏，窗口在那之前还会移动约一帧的量。
    // 把冻结层按上一帧的实际位移前移，抵消"背景慢半拍"的游动
    private Point? _livePrevOrigin;

    // live 拖动的帧耗时统计（松手时打一条日志；耗时为后台 BitBlt 的耗时）
    private int _liveFrames;
    private double _liveTotalMs;
    private double _liveMaxMs;

    // 关闭动画播完的放行标记（见 Closing 拦截）
    private bool _closeAnimated;

    // 托盘常驻：平时窗口 Hide() 到托盘，快捷键 / 左键点托盘唤出，
    // 只有托盘右键菜单的“退出”才真正结束进程（_exiting=true）
    private Chrome.TrayIcon? _tray;
    private bool _exiting;

    // 搜索过滤激活中：视图只是主列表的子集，容器索引 ≠ 主列表索引，
    // 排序拖拽按“容器索引即主列表索引”假设工作，此时必须禁用
    private bool _filterActive;

    // 用户设置（settings.json），见 SettingsDialog / ApplySettings
    private LauncherSettings _settings = new();

    private void RefreezeSoon()
    {
        if (_inModalMove)
        {
            return;
        }

        _refreezeTimer.Stop();
        _refreezeTimer.Start();
    }

    /// <summary>
    /// 描边层圆角与 DWM 裁剪同步：Normal 时 8px（DWMWCP_ROUND 的系统半径），
    /// 最大化时 DWM 自动变直角，这里也跟着变，否则描边弧会在四角切出小缺口。
    /// </summary>
    private void UpdateLayerCorner()
    {
        bool maximized = WindowState == WindowState.Maximized;
        EdgeLayer.CornerRadius = maximized ? default : new CornerRadius(8);
        InnerLineLayer.CornerRadius = maximized ? default : new CornerRadius(7);
        InnerLineLayer.Margin = maximized ? default : new Thickness(1);
    }

    /// <summary>
    /// 挂 Win32 消息钩子：拖动/缩放窗口是 Win32 的模态循环，WPF 的事件和计时器
    /// 在循环里照常触发，只有 WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE 能精确标记"松手"。
    /// 顺手把托盘和全局快捷键也注册在这 —— 两者都需要窗口句柄。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var source = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
        source?.AddHook(WndProc);

        InitTray();
        ApplyHotkeySetting();

        // 截屏排除永久常开（启动时设一次，之后全生命周期不再切换）。
        // 每次开关都是一次 DWM 表面重建 = 一次整窗闪烁 —— 常开后全链路零切换：
        //   · 抓帧（静态/松手锚定）：零切换直接抓；
        //   · 标题拖动：非模态自定义拖动（见 WM_NCLBUTTONDOWN），排除开着 + 非模态
        //     移动实测不会触发 DWM 停止呈现（消失 bug 只咬模态循环）；
        //   · 缩放边缘（WM_ENTERSIZEMOVE 模态循环）：例外 —— 进循环前临时关掉、
        //     出循环恢复（见 WndProc_DragStarted / DragEnded）。
        // 代价：用户自己的截图/录屏里没有本窗口（对启动器可接受）。
        IntPtr hwndExclusion = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _exclusionHeld = hwndExclusion != IntPtr.Zero && ScreenGrab.TrySetExclusion(hwndExclusion, true);
        _exclusionOnTick = Environment.TickCount64;
        Diag.Log($"permanent exclusion held={_exclusionHeld}");
    }

    private void InitTray()
    {
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream;
            var icon = new System.Drawing.Icon(stream);

            _tray = new Chrome.TrayIcon(icon, "Glass Launcher — 左键 打开/隐藏，右键 菜单");
            _tray.LeftClick += ToggleLauncherWindow;
            _tray.MenuOpen += ShowFromTray;
            _tray.MenuSettings += () =>
            {
                ShowFromTray();
                // 设置对话框要等窗口重新可见、重新激活之后再弹，否则焦点会打回托盘
                Dispatcher.BeginInvoke(() => OnSettingsClick(this, new RoutedEventArgs()),
                    DispatcherPriority.Background);
            };
            _tray.MenuExit += ExitApplication;
        }
        catch (Exception ex)
        {
            Diag.Log($"托盘初始化失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 全局快捷键注册（设置改了之后也会重新走这里）。
    /// MOD_NOREPEAT：按住不放只触发一次，不然按住 Ctrl+Alt+G 会疯狂开关窗口。
    /// </summary>
    private void ApplyHotkeySetting()
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        UnregisterHotKey(hwnd, HotkeyId);

        if (!HotkeyHelper.TryParse(_settings.Hotkey, out uint modifiers, out uint virtualKey))
        {
            return;
        }

        if (!RegisterHotKey(hwnd, HotkeyId, modifiers | HotkeyHelper.ModNoRepeat, virtualKey))
        {
            ShowStatus($"快捷键 {_settings.Hotkey} 注册失败，可能被其他程序占用，可到设置里换一个");
        }
    }

    private const int HotkeyId = 0xC0DE; // 本窗口内唯一的热键 id

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processIdOut);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // ── 非模态拖动（自定义拖动，绕开 Windows 的模态移动循环）──
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    // Win32 的释放鼠标捕获（类里另有同名 WPF 辅助方法 ReleaseCapture，故改名；
    // DLL 入口点仍是 ReleaseCapture）
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "ReleaseCapture")]
    private static extern bool ReleaseWin32Capture();

    private const uint SwpNosize = 0x0001;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private const int VkLbutton = 0x01;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_NCLBUTTONDOWN = 0x00A1;
        const int WM_NCLBUTTONDBLCLK = 0x00A3;
        const int HTCAPTION = 2;
        const int WM_EXITSIZEMOVE = 0x0232;
        const int WM_HOTKEY = 0x0312;

        switch (msg)
        {
            case WM_ENTERSIZEMOVE:
                _inModalMove = true;
                _refreezeTimer.Stop();
                WndProc_DragStarted();
                StartDragSampler();
                break;

            case WM_EXITSIZEMOVE:
                _inModalMove = false;
                WndProc_DragEnded();
                // 松手后再采样 2 秒，看看窗口最后落在什么状态
                StopDragSamplerAfterDelay();
                break;

            case WM_NCLBUTTONDOWN:
                // 标题区按下：不走 WindowChrome → DefWindowProc 的模态移动循环。
                // ⚠ 这台 Windows（build 26300 Insider）上"排除开着 + 模态拖动"会让
                // DWM 停止呈现整个窗口（拖动消失），非模态的 SetWindowPos 连续移动
                // 走的是另一条呈现路径，实测不触发。改为手动拖动（见 StartManualDrag）。
                if ((int)wParam == HTCAPTION && WindowState == WindowState.Normal)
                {
                    StartManualDrag();
                    handled = true;
                }

                break;

            case WM_NCLBUTTONDBLCLK:
                // 双击标题栏（最大化/还原）走的是默认处理，但按住时可能已经进了手动拖动。
                // 这里先把手动拖动收掉，再把消息交回系统，否则 _manualDrag 会带着最大化状态
                // 继续用 SetWindowPos 搬窗口。
                if ((int)wParam == HTCAPTION)
                {
                    AbortWindowDrag("titlebar double-click");
                }

                break;

            case WM_HOTKEY:
                if (wParam.ToInt32() == HotkeyId)
                {
                    ToggleLauncherWindow();
                    handled = true;
                }

                break;
        }

        return IntPtr.Zero;
    }

    // ── 非模态（手动）拖动 ─────────────────────────────────────
    // 模态移动循环（WM_ENTERSIZEMOVE）+ 截屏排除 = DWM 停止呈现（拖动消失），
    // 所以标题拖动改为：拦截 WM_NCLBUTTONDOWN/HTCAPTION，用 15ms 轮询
    // GetCursorPos + SetWindowPos 连续移动窗口（不进模态循环），live 抓屏照常。
    private bool _manualDrag;
    private NativePoint _manualDragCursorStart;
    private Point _manualDragWindowStart; // 拖动开始时窗口左上角（物理 px）
    private DispatcherTimer? _manualDragTimer;

    /// <summary>
    /// 开始手动拖动：记录起点，启动 live 背景 + 轮询计时器。
    /// 只处理 Normal 状态（最大化时仍走系统模态路径，保持双击还原等行为）。
    /// </summary>
    private void StartManualDrag()
    {
        // 不可见时别启动：否则 Hide 到托盘后，15ms 的轮询还在给隐藏窗口 SetWindowPos，
        // 窗口会在看不见的地方跟着鼠标跑，_inModalMove 也会卡在 true 让后续抓帧全部让路
        if (_manualDrag || !IsVisible || WindowState != WindowState.Normal)
        {
            return;
        }

        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !ScreenGrab.GetWindowRect(hwnd, out ScreenGrab.Rect rect))
        {
            return;
        }

        if (!GetCursorPos(out _manualDragCursorStart))
        {
            return;
        }

        _manualDragWindowStart = new Point(rect.Left, rect.Top);
        _manualDrag = true;
        _inModalMove = true; // 与模态拖动一致：让 RefreezeSoon/FreezeBackdrop 让路
        Diag.Log($"manual drag start rect=({rect.Left},{rect.Top})");
        _ = ReleaseWin32Capture();

        // 手动拖动没有 WM_ENTERSIZEMOVE，live 由这里直接驱动
        StartDragRide();

        _manualDragTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _manualDragTimer.Tick -= ManualDragTick;
        _manualDragTimer.Tick += ManualDragTick;
        _manualDragTimer.Start();
    }

    private void ManualDragTick(object? sender, EventArgs e)
    {
        // 左键已松开 → 收尾（轮询方案不需要鼠标捕获，按键状态即真相）
        if ((GetAsyncKeyState(VkLbutton) & 0x8000) == 0)
        {
            EndManualDrag();
            return;
        }

        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetCursorPos(out NativePoint cursor))
        {
            EndManualDrag();
            return;
        }

        int nx = (int)_manualDragWindowStart.X + cursor.X - _manualDragCursorStart.X;
        int ny = (int)_manualDragWindowStart.Y + cursor.Y - _manualDragCursorStart.Y;
        SetWindowPos(hwnd, IntPtr.Zero, nx, ny, 0, 0, SwpNosize | SwpNozorder | SwpNoactivate);
    }

    private void EndManualDrag()
    {
        if (!_manualDrag)
        {
            return;
        }

        _manualDrag = false;
        _manualDragTimer?.Stop();
        _inModalMove = false;

        // 停 live。松手的视觉连续性靠"精确对齐 + 原子换帧"，绝不能加动画：
        //  - 最后一帧 live 帧带着对齐偏移（内容抓于 O_c，窗口此刻在 W，偏移 s=(O_c−W)/dpi），
        //    任何"回落到 0"的动画都会把已对齐的背景推歪 —— 那本身就是闪烁；
        //  - 静态帧几毫秒后就到，中途动画必然被硬杀，等于自己制造一次跳变。
        if (_liveGrabActive)
        {
            CompositionTarget.Rendering -= OnLiveFrame;
            _liveGrabActive = false;

            // 精确对齐：窗口已停，用真实终点重算最后一帧的偏移
            // （拖动中偏移是按"抓取瞬间 vs 当前"算的，末帧上屏后窗口可能还挪了最后一点）。
            // 修正后最后一帧与桌面像素级重合；随后静态帧（偏移恒 0）原子替换，
            // 两个状态完全一样 —— 松手零闪烁。锚定推导：内容抓于 O_c、以偏移 s
            // 显示在窗口 W 上时，屏幕对齐条件为 s = (O_c − W) / dpi。
            IntPtr hwndEnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (_livePrevOrigin is Point lastOrigin && hwndEnd != IntPtr.Zero
                && ScreenGrab.GetWindowRect(hwndEnd, out ScreenGrab.Rect endRect))
            {
                DpiScale dpiEnd = VisualTreeHelper.GetDpi(this);
                FrozenShift.BeginAnimation(TranslateTransform.XProperty, null);
                FrozenShift.BeginAnimation(TranslateTransform.YProperty, null);
                FrozenShift.X = (lastOrigin.X - endRect.Left) / dpiEnd.DpiScaleX;
                FrozenShift.Y = (lastOrigin.Y - endRect.Top) / dpiEnd.DpiScaleY;
                Diag.Log($"drag end align dx={FrozenShift.X:F1} dy={FrozenShift.Y:F1}");
            }

            if (_liveFrames > 0)
            {
                Diag.Log($"Live drag frames={_liveFrames} avg={_liveTotalMs / _liveFrames:F1}ms max={_liveMaxMs:F1}ms");
            }
        }

        // 立即重新锚定桌面（排除常开，抓帧零切换零等待；instant=直接换帧不走淡入）
        FreezeBackdrop(instant: true);
    }

    /// <summary>
    /// 把"正在拖窗口"的状态硬性收掉，不做重新锚定（调用方要么马上隐藏、要么马上退出）。
    ///
    /// 为什么需要它：手动拖动是 15ms 轮询 + SetWindowPos，没有鼠标捕获，所以
    /// "隐藏到托盘 / 关窗 / 双击标题栏最大化"这些路径都不会自然产生鼠标抬起，
    /// 轮询会继续给一个隐藏的窗口改位置，`_inModalMove` 也会卡在 true，
    /// 让之后的 RefreezeSoon/FreezeBackdrop 永远让路（背景停在旧位置）。
    /// </summary>
    private void AbortWindowDrag(string reason)
    {
        if (!_manualDrag)
        {
            return;
        }

        Diag.Log($"window drag aborted reason={reason}");
        _manualDrag = false;
        _manualDragTimer?.Stop();
        _inModalMove = false;

        if (_liveGrabActive)
        {
            CompositionTarget.Rendering -= OnLiveFrame;
            _liveGrabActive = false;
        }

        // 排队中的补抓也作废：它们带着旧的几何/instant 语义，会让唤出后的第一帧错位
        _freezeQueued = false;
        _freezeQueuedInstant = false;

        FrozenShift.BeginAnimation(TranslateTransform.XProperty, null);
        FrozenShift.BeginAnimation(TranslateTransform.YProperty, null);
        FrozenShift.X = 0;
        FrozenShift.Y = 0;
    }

    /// <summary>
    /// 手动（非模态）拖动开始：启动 live 抓屏，背景实时跟着桌面流动。
    /// 排除永久常开（见 OnSourceInitialized），这里零切换零预热直接开抓 ——
    /// 非模态移动 + 排除开着实测不会触发 DWM 停止呈现（消失 bug 只咬模态循环）。
    /// </summary>
    private void StartDragRide()
    {
        // 交叉淡入若正在进行，立刻定格到新帧：拖动期间不能悬着半透明的新帧层
        CommitFrozenFade();

        // 上一轮松手的回落动画清掉，偏移归零
        FrozenShift.BeginAnimation(TranslateTransform.XProperty, null);
        FrozenShift.BeginAnimation(TranslateTransform.YProperty, null);
        FrozenShift.X = 0;
        FrozenShift.Y = 0;

        // 作废在途抓帧：此刻若 FreezeBackdrop 正在等待/抓取，结果作废
        // （它抓到的是拖动前的画面，贴进来就是一帧错位）
        _captureGeneration++;

        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero
            || Backdrop is GlassBackdrop.Optical or GlassBackdrop.Mica or GlassBackdrop.None)
        {
            return;
        }

        // 排除永久常开：已开就零切换零预热直接抓；只有异常掉开过才需要重开+预热
        bool exclusionFresh = EnsureExclusion();
        if (!_exclusionHeld)
        {
            return;
        }

        _liveGrab ??= new LiveGrab();
        _liveGrab.ResetWarmup(exclusionFresh ? LiveWarmupFrames : 0);
        _liveGrab.FrameReady -= OnLiveFrameReady;
        _liveGrab.FrameReady += OnLiveFrameReady;
        _liveGrabActive = true;
        _livePrevOrigin = null;
        _liveFrames = 0;
        _liveTotalMs = 0;
        _liveMaxMs = 0;
        CompositionTarget.Rendering += OnLiveFrame;
    }

    /// <summary>
    /// 进入模态循环（只剩缩放边缘 / 最大化拖动会走到这 —— 标题拖动已被手动拖动
    /// 拦截，见 WM_NCLBUTTONDOWN）。
    ///
    /// ⚠ 模态循环 + 排除开着 = DWM 停止呈现窗口（拖动消失，这台 build 26300 的
    /// 系统级故障）。所以进模态循环的第一件事是把排除临时关掉，冻结帧随窗动，
    /// 出循环（DragEnded）再把排除恢复常开。
    /// </summary>
    private void WndProc_DragStarted()
    {
        // 交叉淡入若正在进行，立刻定格到新帧
        CommitFrozenFade();

        // 帧随窗动：偏移归零，冻结帧与窗口 1:1 对齐一起移动
        FrozenShift.BeginAnimation(TranslateTransform.XProperty, null);
        FrozenShift.BeginAnimation(TranslateTransform.YProperty, null);
        FrozenShift.X = 0;
        FrozenShift.Y = 0;

        // 作废在途抓帧
        _captureGeneration++;

        // 关排除：以实查为准（缓存可能和真实状态漂移）
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && ScreenGrab.GetExclusion(hwnd) == ScreenGrab.WDA_EXCLUDEFROMCAPTURE)
        {
            ScreenGrab.TrySetExclusion(hwnd, false);
            _exclusionHeld = ScreenGrab.GetExclusion(hwnd) == ScreenGrab.WDA_EXCLUDEFROMCAPTURE;
            Diag.Log($"modal drag: exclusion released held={_exclusionHeld}");
        }

        // 不开 live：模态循环里排除必须关，而抓屏依赖排除 —— 冻结帧顶上
    }

    /// <summary>
    /// 模态循环结束（缩放/最大化拖动松手）：排除恢复常开，然后立即重新锚定桌面。
    /// 手动拖动的收尾不走这里（EndManualDrag 直接调 FreezeBackdrop —— 排除
    /// 全程常开，一次都不用切）。
    /// </summary>
    private void WndProc_DragEnded()
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && ScreenGrab.GetExclusion(hwnd) != ScreenGrab.WDA_EXCLUDEFROMCAPTURE)
        {
            if (ScreenGrab.TrySetExclusion(hwnd, true))
            {
                _exclusionHeld = true;
                _exclusionOnTick = Environment.TickCount64;
                Diag.Log("modal drag ended: exclusion restored");
            }
            else
            {
                _exclusionHeld = false;
                Diag.Log("modal drag ended: exclusion restore FAILED");
            }
        }

        // 立即重抓锚定（不走 120ms 防抖）。排除刚恢复的话 FreezeBackdrop
        // 会按 _exclusionOnTick 补足生效等待
        FreezeBackdrop();
    }

    /// <summary>
    /// live 模式每帧的 UI 侧入口：只算几何并提交给后台抓帧线程（微秒级），
    /// 绝不做任何阻塞工作 —— BitBlt 的 ~10ms 在 LiveGrab 的工作线程里，
    /// 模态拖动循环不被堵住，窗口移动和背景更新才能同时跑满合成节拍。
    /// </summary>
    private void OnLiveFrame(object? sender, EventArgs e)
    {
        if (!_liveGrabActive)
        {
            return;
        }

        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || ActualWidth < 40 || ActualHeight < 40)
        {
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Point originPx = PointToScreen(new Point(0, 0));
        int width = (int)Math.Ceiling((ActualWidth + BackdropPad * 2) * dpi.DpiScaleX);
        int height = (int)Math.Ceiling((ActualHeight + BackdropPad * 2) * dpi.DpiScaleY);

        _liveGrab!.Submit(
            (int)Math.Round(originPx.X - BackdropPad * dpi.DpiScaleX),
            (int)Math.Round(originPx.Y - BackdropPad * dpi.DpiScaleY),
            width, height, originPx, dpi);
    }

    /// <summary>
    /// 后台抓帧完成（UI 线程回调）：上屏 + 位移对齐。
    ///
    /// 对齐条件与 EndManualDrag 里完全相同：<c>s = (O_capture − W_now) / dpi</c>。
    /// 本帧内容抓于 <paramref name="originPx"/>，而它上屏时窗口还在移动，所以必须用
    /// 【当前】窗口原点来算，不能用"上一帧的位移量"——后者符号是反的（窗口往右走，
    /// 内容要往左补），会把背景推成两帧滞后，松手对齐的那一刻再跳回来。
    /// </summary>
    private void OnLiveFrameReady(WriteableBitmap bitmap, Point originPx, DpiScale dpi)
    {
        if (!_liveGrabActive)
        {
            return;
        }

        FrozenSurface.Source = bitmap;
        FrozenSurfaceAlt.Source = null;
        FrozenSurfaceAlt.Opacity = 0;
        FrozenBackdropHost.Visibility = Visibility.Visible;

        Point windowNow = PointToScreen(new Point(0, 0));
        DpiScale dpiNow = VisualTreeHelper.GetDpi(this);

        FrozenShift.BeginAnimation(TranslateTransform.XProperty, null);
        FrozenShift.BeginAnimation(TranslateTransform.YProperty, null);
        FrozenShift.X = (originPx.X - windowNow.X) / dpiNow.DpiScaleX;
        FrozenShift.Y = (originPx.Y - windowNow.Y) / dpiNow.DpiScaleY;

        // 松手时要用"最后上屏那一帧的抓取原点"重算真实终点偏移，这里记的就是它
        _livePrevOrigin = originPx;

        double ms = _liveGrab!.LastGrabMs;
        _liveFrames++;
        _liveTotalMs += ms;
        _liveMaxMs = Math.Max(_liveMaxMs, ms);
    }

    /// <summary>
    /// 强制 DWM 重建本窗口的重定向表面：连续开关一次截屏排除。
    ///
    /// 为什么要做：托盘 Hide()/Show() 循环会把窗口的 DWM 表面留在"脏"状态 ——
    /// 脏表面 + 排除开着进入模态拖动 = DWM 停止呈现整个窗口（一拖整个消失，
    /// 松手排除关掉才回来；v0.1 没有 Hide/Show 所以从不出事）。
    /// 开关排除正好是已知能强制 DWM 重建表面的手段（它也是"闪烁"的来源 ——
    /// 所以必须趁唤出的淡入动画期间做，窗口本来就半透明，重建视觉上不可见）。
    /// </summary>
    private void RebuildDwmSurface()
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        bool wasExcluded = ScreenGrab.GetExclusion(hwnd) == ScreenGrab.WDA_EXCLUDEFROMCAPTURE;
        bool toggled = ScreenGrab.TrySetExclusion(hwnd, !wasExcluded);
        bool restored = ScreenGrab.TrySetExclusion(hwnd, wasExcluded);

        // 关键：这里刚把排除"关掉又打开"，DWM 要 1~2 个合成帧才真正落实新的亲和性。
        // 必须重置计时，否则紧接着排队的 FreezeBackdrop 会读到旧的 _exclusionOnTick、
        // 以为"早就生效了"，跳过 60ms 等待直接抓 —— 抓进来的背景里带着本窗口自己（鬼影帧）。
        _exclusionOnTick = Environment.TickCount64;
        _exclusionHeld = ScreenGrab.GetExclusion(hwnd) == ScreenGrab.WDA_EXCLUDEFROMCAPTURE;

        Diag.Log($"dwm surface rebuilt toggle={toggled} restore={restored} held={_exclusionHeld}");
    }

    /// <summary>
    /// 保证截屏排除处于开启状态，并把 <c>_exclusionHeld</c> / <c>_exclusionOnTick</c>
    /// 同步成"实查到的真实状态"。
    ///
    /// 之前 StartDragRide 用缓存的 <c>_exclusionHeld</c> 判断、FreezeBackdrop 每次实查
    /// GetExclusion，两者一旦漂移（RebuildDwmSurface 恢复失败、DWM 重启、远程会话切换），
    /// 拖动期间就会抓到自己而且完全没有日志。现在两边都走这里。
    /// </summary>
    /// <returns>true 表示"本次是刚打开的"（调用方需要跑预热帧）。</returns>
    private bool EnsureExclusion()
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            _exclusionHeld = false;
            return false;
        }

        if (ScreenGrab.GetExclusion(hwnd) == ScreenGrab.WDA_EXCLUDEFROMCAPTURE)
        {
            if (!_exclusionHeld)
            {
                Diag.Log("exclusion 实际开着但缓存是 false，已同步（不重新打开，避免白闪一次）");
                _exclusionHeld = true;
            }

            return false;
        }

        bool wasHeld = _exclusionHeld;
        if (!ScreenGrab.TrySetExclusion(hwnd, true))
        {
            _exclusionHeld = false;
            Diag.Log("EnsureExclusion 失败：SetWindowDisplayAffinity 拒绝");
            return false;
        }

        _exclusionHeld = true;
        _exclusionOnTick = Environment.TickCount64;

        if (wasHeld)
        {
            Diag.Log("exclusion 被系统掉开了，已重新打开（需要预热帧）");
        }

        return true;
    }

    public MainWindow()
    {
        InitializeComponent();

        // 光学玻璃：只有显式把 Backdrop 设成 Optical 时才会真正工作（见 SyncGlassMode）
        _glass = new OpticalGlassSurface(this, GlassSurface)
        {
            Radius = CornerRadius.TopLeft,
            Band = 26,
            Amplitude = 17,
        };

        AppItems.ItemsSource = _entries;
        _entries.CollectionChanged += (_, _) =>
        {
            UpdateEmptyState();
        };

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusBar.Visibility = Visibility.Collapsed;
        };

        // 拖到列表上下沿时自动滚动。用 16ms 的独立节拍，而不是“鼠标移动才滚”，
        // 因为指针停在边缘不动时也应该继续滚（和 macOS 一致）。
        _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _autoScrollTimer.Tick += (_, _) => AutoScrollTick();

        LoadFromStore();

        // 设置：加载 → 应用（主题/模糊/置顶）。应用列表与设置两份文件互不牵连
        _settings = SettingsStore.Load(out string? settingsError);
        if (settingsError is not null)
        {
            ShowStatus($"设置读取失败，已用默认值：{settingsError}");
        }

        ApplySettings();

        UpdateEmptyState();

        Closed += (_, _) =>
        {
            ClearDragFlags();
            AbortWindowDrag("closed");
            _liveGrab?.Dispose();
            _liveGrab = null;
            _tray?.Dispose();
            Persist();
        };

        // 关闭拦截（托盘常驻语义）：
        //   · Alt+F4 / 系统关机等 → 窗口不退出，播消失动画后 Hide() 到托盘；
        //   · 托盘右键“退出”(_exiting=true) → 播完 mac 收场动画（缩到 0.94 + 淡出）再真关。
        Closing += (_, e) =>
        {
            Diag.Log($"Closing closeAnimated={_closeAnimated} exiting={_exiting}");

            if (_closeAnimated)
            {
                return;
            }

            e.Cancel = true;

            if (_exiting)
            {
                PlayCloseThenClose();
            }
            else
            {
                HideToTray();
            }
        };

        // 兜底：拖拽中窗口失活（Alt+Tab 切走、UAC 弹窗抢焦点），必须把手势收回来，
        // 否则图标会一直悬在半空、条目也一直停在隐藏状态
        Deactivated += (_, _) => CancelGesture();

        // 冻结层全时制：DWM 实时 Acrylic 的模糊浓度是系统定死的（实测聚焦态糊成一坨），
        // 而冻结帧的模糊（Radius=12）才是理想效果 —— 所以聚焦和失焦统一显示冻结帧。
        // 附带收益：失焦灰板被不透明的冻结帧盖死，且两种状态像素完全一致，切换零跳变。
        //
        // 抓屏时机（详见 FreezeBackdrop / WndProc）：
        //   · 静止时（绝大多数时间）：什么都不抓，复用现有冻结帧；
        //   · 拖动/缩放中：绝不抓屏也不碰 DisplayAffinity —— 帧随窗动，
        //     松手后 RefreezeSoon 重新锚定桌面（build 26300 上排除+拖动 = DWM 停止呈现）；
        //   · 程序化尺寸/位置变化（最大化等）：防抖后补抓一帧。
        _refreezeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _refreezeTimer.Tick += (_, _) =>
        {
            _refreezeTimer.Stop();
            FreezeBackdrop();
        };
        SizeChanged += (_, _) => RefreezeSoon();
        // 程序化移动（SetWindowPos、保存的位置恢复等）不走模态循环，live 模式接不到，
        // 靠这个补一帧；交互拖动期间 _inModalMove=true，这里会自动让路
        LocationChanged += (_, _) => RefreezeSoon();
        // 拖到另一个 DPI 的显示器（125%↔100%）时窗口逻辑尺寸会变，冻结帧的
        // 像素映射全得重来，也补一帧（LocationChanged 通常也会触发，这里兜底）
        DpiChanged += (_, e) =>
        {
            // 实测日志里会收到大量 "1.25 -> 1.25" 的假事件：它们会触发一次
            // ApplyCornerMode(force)（= SWP_FRAMECHANGED 全量非客户区重算）+ 一次重抓，
            // 纯属白干。只有 DPI 真的变了才有几何需要重算。
            if (e.OldDpi.DpiScaleX == e.NewDpi.DpiScaleX && e.OldDpi.DpiScaleY == e.NewDpi.DpiScaleY)
            {
                return;
            }

            Diag.Log($"DpiChanged {e.OldDpi.DpiScaleX:F2} -> {e.NewDpi.DpiScaleX:F2} inModalMove={_inModalMove}");
            RefreezeSoon();
        };

        // 可见性哨兵：Hide/Show 的每一次变化都留痕，"窗口凭空消失"从日志定性
        IsVisibleChanged += (_, e) =>
        {
            Diag.Log($"IsVisibleChanged -> {e.NewValue} at {Environment.StackTrace.Split('\n').Skip(3).FirstOrDefault()?.Trim()}");
        };
        // 最大化时 DWM 会去掉圆角裁剪，描边层的 8px 弧会在四角切出小缺口，同步改直角
        StateChanged += (_, _) =>
        {
            UpdateLayerCorner();

            // 状态一变（最大化/还原/最小化），手动拖动的前提就变了，收掉
            if (WindowState != WindowState.Normal)
            {
                AbortWindowDrag("state changed");
            }
        };

        // 入场动效：等这一帧布局完成，容器才拿得到
        Loaded += (_, _) =>
        {
            // 上次退出时的位置可能停在屏幕外，先拉回来再播入场
            EnsureOnScreen();
            Dispatcher.BeginInvoke(PlayEntrance, DispatcherPriority.Loaded);
            // 第一帧背景：入场淡入会把它接住，不会白屏。
            // ⚠ 必须用无参 lambda 包一层：FreezeBackdrop 有可选参数（bool instant = false），
            // 而 Dispatcher.BeginInvoke(Delegate, …) 内部走 Delegate.DynamicInvoke —— 反射调用
            // 不认可选参数，直接传方法组会抛 TargetParameterCountException。
            // 这个坑曾经让程序"启动 0.3 秒后无响应然后消失"（见 REVIEW-FINDINGS P0-1）。
            Dispatcher.BeginInvoke((Action)(() => FreezeBackdrop()), DispatcherPriority.Loaded);

            // 设置里开了“启动时检查失效条目”才跑；检查是后台 IO，晚一拍不碍事
            if (_settings.CheckBrokenOnStart)
            {
                Dispatcher.BeginInvoke(RunBrokenCheckAsync, DispatcherPriority.Background);
            }
        };

        // Esc 分三级：拖拽中→放弃拖拽；搜索有字→清空搜索；其余→收到托盘
        // （macOS 启动台按 Esc 就是收起，这里对齐）
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
            {
                return;
            }

            if (_dragActive)
            {
                EndDrag(commit: false);
            }
            else if (SearchBox.Text.Length > 0)
            {
                SearchBox.Clear();
            }
            else
            {
                HideToTray();
            }

            e.Handled = true;
        };

#if DEBUG
        ApplyBackdropProbe();
        SeedFromEnvironment();
        Loaded += (_, _) => RunDebugProbes();
#endif

        SyncGlassMode();
    }

    /// <summary>
    /// 光学玻璃与 XAML 描边只能二选一：
    ///   · 光学模式：位图自带模糊、棱和高光 → 关掉 XAML 那几层，否则叠成双线；
    ///   · Acrylic 模式：位图不存在 → 必须把 XAML 那几层显示回来，玻璃的“棱”全靠它们。
    ///
    /// 另外光学模式下每次抓图都要临时开关截屏排除，那一下必然闪一帧。
    /// 所以它不再是默认路径，只有 Backdrop 被显式设成 Optical 才启用。
    /// </summary>
    private void SyncGlassMode()
    {
        bool optical = Backdrop == GlassBackdrop.Optical;

        _glass.Enabled = optical;

        Visibility layers = optical ? Visibility.Collapsed : Visibility.Visible;
        SheenLayer.Visibility = layers;
        EdgeLayer.Visibility = layers;
        InnerLineLayer.Visibility = layers;
        SpecularLayer.Visibility = layers;

        if (optical)
        {
            _glass.Attach();
            _glass.Refresh();
        }
        else
        {
            // 切回 DWM 材质时必须清掉位图，否则整块 Image 会把 Acrylic 盖死
            GlassSurface.Source = null;
        }
    }

    /// <summary>
    /// 背景冻结：DWM 的系统 Acrylic 有两个绕不开的毛病 —— 失焦退化成不透明的灰板
    /// （系统写死的行为），以及聚焦态的实时模糊浓度过高（同样不可调）。
    /// 唯一出路是干脆不让 DWM 的模糊露脸：抓一帧真实背景（截屏排除保证抓到的是
    /// "我们盖住的那片"，不是我们自己），GPU 高斯模糊后全程盖在上面。
    ///
    /// 抓屏时机：启动后第一帧（Loaded）+ 松手锚定（EndManualDrag /
    /// WndProc_DragEnded）+ 程序化尺寸/位置变化（防抖）。只在【静止】时抓。
    ///
    /// instant：松手（live 刚停）路径专用。此刻底上显示的就是实时桌面（已精确
    /// 对齐），静态帧与它完全重合 —— 直接换 Source，交叉淡入反而是错位重影
    /// （"失焦闪一下"的元凶）。其余路径（托盘唤出、尺寸变化、模态松手）底下
    /// 的帧是过时的，仍走淡入。
    ///
    /// 排除开关策略（关键，直接影响"闪一下"）：静态期排除常开（OnSourceInitialized
    /// 设一次），所以这里大多数时候【零切换】直接抓 —— 每次开关都是一次 DWM 表面
    /// 重建，用户看到的就是整窗闪一下。只有松手路径（拖动中关过）才需要开一次。
    /// 抓完【不关】，保持开启直到下一次拖动。
    ///
    /// 排除刚开时要等 60ms（DWM 过一两只合成帧才真正落实排除，立刻抓会把窗口自己
    /// 抓进背景）。已常开时无需等待。
    /// </summary>
    private async void FreezeBackdrop(bool instant = false)
    {
        // 光学模式自己就是真背景；Mica 失焦不退化；没有基底更不用管
        if (Backdrop is GlassBackdrop.Optical or GlassBackdrop.Mica or GlassBackdrop.None)
        {
            return;
        }

        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !IsVisible || ActualWidth < 40 || ActualHeight < 40)
        {
            // 隐藏/最小化时抓屏没有意义：抓到的是"窗口不在那儿"的桌面，
            // 唤出后会显示一张和窗口位置无关的旧帧。ShowFromTray 会自己补抓。
            return;
        }

        if (_freezeBusy)
        {
            // 忙时不能丢请求：记一次，当前帧抓完用【新位置】补跑（instant 一并保留）
            _freezeQueued = true;
            _freezeQueuedInstant |= instant;
            return;
        }

        if (_inModalMove)
        {
            // 拖动期间冻结帧让路：抓屏不能发生在模态拖动里
            _freezeQueued = true;
            _freezeQueuedInstant |= instant;
            return;
        }

        _freezeBusy = true;

        try
        {
            // 以实查结果为准补开排除（顺带修掉"以为开着其实没开"的日志盲区）
            EnsureExclusion();
            if (!_exclusionHeld)
            {
                return;
            }

            // 排除刚（重）开的话，DWM 要过一两只合成帧才真正落实，补足等待。
            // 只看开关状态就抓 = 把窗口自己抓进背景（鬼影帧）。
            // 松手路径上排除从拖动开始就一直开着，elapsed 远超 60ms，这里零等待直接抓。
            long sinceOn = Environment.TickCount64 - _exclusionOnTick;
            if (sinceOn < ExclusionSettleMs)
            {
                await Task.Delay((int)(ExclusionSettleMs - sinceOn));

                // await 之后窗口可能已经被隐藏/关闭：这里的 this 已经不能随便用，
                // 否则 VisualTreeHelper/PointToScreen 会抛异常 —— async void 里抛出去
                // 只能靠全局处理器兜，那是"卡死后崩溃"的配方。复查一遍再继续。
                if (new System.Windows.Interop.WindowInteropHelper(this).Handle == IntPtr.Zero || !IsVisible)
                {
                    Diag.Log("freeze aborted (window hidden/closed while waiting for exclusion)");
                    return;
                }
            }

            int generation = _captureGeneration;

            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            Point origin = PointToScreen(new Point(0, 0));
            // 与显示端同套几何：抓屏区域比窗口四周各大 BackdropPad，
            // 显示端 Margin=-48 + Stretch=Fill 恰好 1:1 映射，背景与桌面像素级对齐
            int width = (int)Math.Ceiling((ActualWidth + BackdropPad * 2) * dpi.DpiScaleX);
            int height = (int)Math.Ceiling((ActualHeight + BackdropPad * 2) * dpi.DpiScaleY);

            byte[]? pixels = ScreenGrab.Capture(
                (int)Math.Round(origin.X - BackdropPad * dpi.DpiScaleX),
                (int)Math.Round(origin.Y - BackdropPad * dpi.DpiScaleY),
                width, height);

            if (pixels is null)
            {
                return;
            }

            if (generation != _captureGeneration)
            {
                // 等待/抓取期间拖动开始了：这帧作废（位置是拖动前的过时画面）
                Diag.Log("freeze result discarded (drag started mid-capture)");
                return;
            }

            var bitmap = new WriteableBitmap(width, height, dpi.DpiScaleX * 96, dpi.DpiScaleY * 96,
                PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);

            // 静态帧没有位移预测，偏移必须归零（live 松手的回落动画可能还没走完）
            FrozenShift.BeginAnimation(TranslateTransform.XProperty, null);
            FrozenShift.BeginAnimation(TranslateTransform.YProperty, null);
            FrozenShift.X = 0;
            FrozenShift.Y = 0;

            ShowFrozenFrame(bitmap, instant);
            Diag.Log($"freeze ok {width}x{height} instant={instant} settle={sinceOn}ms px={pixels.Length / 4}");
        }
        catch (Exception ex)
        {
            // async void：任何漏网的异常都不能交给全局处理器（那里只能记一笔，
            // 用户看到的是"窗口卡死"）。这里就地记下并放行。
            Diag.Log($"FreezeBackdrop 失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // 排除永久常开：抓完不关（每次开关 = 一次表面重建 = 一次整窗闪烁）。
            // 唯一的例外是模态循环（缩放/最大化拖动），由 WndProc_DragStarted/DragEnded 管理

            _freezeBusy = false;

            // 排队的那次补跑：此刻窗口已停稳，用最新位置抓（instant 标志一并带回）
            if (_freezeQueued)
            {
                _freezeQueued = false;
                bool queuedInstant = _freezeQueuedInstant;
                _freezeQueuedInstant = false;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                    (Action)(() => FreezeBackdrop(queuedInstant)));
            }
        }
    }

    /// <summary>
    /// 把新冻结帧上屏。instant（松手路径）：底上的 live 帧已与新帧重合，直接换
    /// Source，零动画。否则交叉淡入：新帧放到 FrozenSurfaceAlt 上从 0 淡入到 1，
    /// 完成后换到底层的 Source、清空淡入层。首帧（底下还没有内容）直接上。
    /// </summary>
    private void ShowFrozenFrame(WriteableBitmap bitmap, bool instant = false)
    {
        if (instant || FrozenBackdropHost.Visibility != Visibility.Visible || FrozenSurface.Source is null)
        {
            // 在途淡入先定格（杀动画、归并内容），再直接换帧 —— 任何残留动画
            // 都会在下一拍把 Opacity 改回去，造成闪层
            CommitFrozenFade();
            FrozenSurface.Source = bitmap;
            FrozenBackdropHost.Visibility = Visibility.Visible;
            return;
        }

        // 上一轮淡入还没结束又来了新帧：先定格上一轮，再从当前底帧重新淡入
        CommitFrozenFade();

        FrozenSurfaceAlt.Source = bitmap;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        fade.Completed += (_, _) => CommitFrozenFade();
        FrozenSurfaceAlt.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// 把淡入层定格到底层：alt 的内容交给主层，alt 清空归零。
    /// 拖动开始、新一轮淡入开始、淡入完成时都要走一遍。
    /// </summary>
    private void CommitFrozenFade()
    {
        FrozenSurfaceAlt.BeginAnimation(OpacityProperty, null);

        if (FrozenSurfaceAlt.Source is not null)
        {
            FrozenSurface.Source = FrozenSurfaceAlt.Source;
            FrozenSurfaceAlt.Source = null;
        }

        FrozenSurfaceAlt.Opacity = 0;
    }

    // ── 托盘常驻 ────────────────────────────────────────────────

    // 拖动期采样器（行车记录仪）：120ms 一拍记录 rect/透明度/缩放/前台窗口，
    // 每 3 拍抓一张屏幕存图。"窗口凭空消失"复现时靠它定性是 off-screen、
    // 内容塌了还是被压到别人后面。
    // 只在 DEBUG 编入（Release 里这几个字段不参与编译，避免"从未赋值"这类噪音警告）
#if DEBUG
    private DispatcherTimer? _dragSampler;
    private int _dragSamplerTicks;
    private int _sampleSeq;
#endif

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECTlp rect);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECTlp
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private void StartDragSampler()
    {
#if DEBUG
        _dragSamplerTicks = 0;

        if (_dragSampler is null)
        {
            _dragSampler = new DispatcherTimer(DispatcherPriority.Send)
            {
                Interval = TimeSpan.FromMilliseconds(120),
            };
            _dragSampler.Tick += (_, _) => SampleDragState();
        }

        _dragSampler.Start();
        SampleDragState();
#endif
    }

    private void StopDragSamplerAfterDelay()
    {
#if DEBUG
        // 立刻停会导致最后一拍看不到松手后的状态；延 2 秒停
        var t = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _dragSampler?.Stop();
            Diag.Log("sampler stopped");
        };
        t.Start();
#endif
    }

#if DEBUG
    private void SampleDragState()
    {
        _dragSamplerTicks++;

        try
        {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            GetWindowRect(hwnd, out RECTlp r);

            IntPtr fg = GetForegroundWindow();
            var fgTitle = new System.Text.StringBuilder(128);
            _ = GetWindowText(fg, fgTitle, 128);

            Diag.Log($"SAMPLE#{_dragSamplerTicks} rect=({r.Left},{r.Top},{r.Right},{r.Bottom}) "
                + $"wpf=({Left:F0},{Top:F0} {ActualWidth:F0}x{ActualHeight:F0}) "
                + $"opacity={Shell.Opacity:F2} scale={ShellScale.ScaleX:F2} vis={IsVisible} "
                + $"state={WindowState} backdrop={FrozenBackdropHost.Visibility} fg='{fgTitle}'");

            // 每 3 拍存一张整屏截图
            if (_dragSamplerTicks % 3 == 0)
            {
                _sampleSeq++;
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GlassLauncher", "samples");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, $"drag{_sampleSeq:D3}.png");

                // 用物理分辨率抓整个虚拟桌面（SystemParameters 给的是 DIP，要乘 DPI）
                DpiScale samplerDpi = VisualTreeHelper.GetDpi(this);
                int vx = (int)Math.Round(SystemParameters.VirtualScreenLeft * samplerDpi.DpiScaleX);
                int vy = (int)Math.Round(SystemParameters.VirtualScreenTop * samplerDpi.DpiScaleY);
                int vw = (int)Math.Round(SystemParameters.VirtualScreenWidth * samplerDpi.DpiScaleX);
                int vh = (int)Math.Round(SystemParameters.VirtualScreenHeight * samplerDpi.DpiScaleY);

                // 尺寸必须与 CopyFromScreen 的目标矩形一致，否则会留黑边/裁掉一截
                using var bmp = new System.Drawing.Bitmap(Math.Max(1, vw), Math.Max(1, vh));
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(vx, vy, 0, 0, bmp.Size);
                }

                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                Diag.Log($"SAMPLE shot -> {path}");
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"SAMPLE error: {ex.Message}");
        }
    }
#endif

    /// <summary>
    /// 托盘左键 / 全局快捷键共用：可见就收起，不可见（或最小化）就唤出。
    /// </summary>
    private void ToggleLauncherWindow()
    {
        Diag.Log($"Toggle visible={IsVisible} state={WindowState}");

        if (!IsVisible)
        {
            ShowFromTray();
        }
        else if (WindowState == WindowState.Minimized)
        {
            // 最小化的窗口点托盘应该是唤回，而不是再收一次
            WindowState = WindowState.Normal;
            Activate();
        }
        else
        {
            HideToTray();
        }
    }

    /// <summary>
    /// 收到托盘：先播 mac 收场动画（缩到 0.94 + 淡出），播完才真的 Hide()。
    /// Hide 之后立刻把 Shell 的动画值复位 —— 动画是 HoldEnd 的，不复位的话
    /// 下次唤出的第一帧就是透明缩小的残影。
    /// </summary>
    private void HideToTray(string reason = "toggle")
    {
        Diag.Log($"HideToTray reason={reason} visible={IsVisible} inModalMove={_inModalMove} dragActive={_dragActive}");

        if (!IsVisible)
        {
            return;
        }

        // 手动拖动期间隐藏：先把手势收掉，否则隐藏的窗口会继续跟着鼠标跑
        AbortWindowDrag("hide to tray");
        CancelGesture();

        // 中断位移预测的回落动画并把偏移清零：带着非零偏移隐藏的话，
        // 唤出时补帧万一被吞，旧帧就是整体错位的（背景错位的来源之一）
        FrozenShift.BeginAnimation(TranslateTransform.XProperty, null);
        FrozenShift.BeginAnimation(TranslateTransform.YProperty, null);
        FrozenShift.X = 0;
        FrozenShift.Y = 0;

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        var shrinkX = new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        var shrinkY = new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };

        fade.Completed += (_, _) =>
        {
            Diag.Log("HideToTray fade completed -> Hide()");
            Hide();
            ResetShellTransform();
        };

        Shell.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrinkX, HandoffBehavior.SnapshotAndReplace);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrinkY, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// 唤出/启动时把窗口拉回可视区：窗口可以被正常地拖到屏幕边缘外（Windows 原生行为），
    /// 但托盘唤出如果原位显示，一个停在屏幕外的窗口看起来就是“消失了”。
    /// 规则：可见部分不足 260x180（且窗口本身比这大）就把位置夹回最近显示器的工作区内。
    /// </summary>
    private void EnsureOnScreen()
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        GetWindowRect(hwnd, out RECTlp r);

        var monitor = new MONITORINFOEXW
        {
            CbSize = MarshalSizeOfMonitorInfo(),
        };
        IntPtr hMonitor = MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST);

        if (!GetMonitorInfo(hMonitor, ref monitor))
        {
            return;
        }

        RectW work = monitor.RcWork;
        int width = r.Right - r.Left;
        int height = r.Bottom - r.Top;
        int visibleWidth = Math.Min(r.Right, work.Right) - Math.Max(r.Left, work.Left);
        int visibleHeight = Math.Min(r.Bottom, work.Bottom) - Math.Max(r.Top, work.Top);

        // 可见部分不足窗口自身的 60% 就算"停丢了"：启动器是个整体面板，
        // 露一条边（1/3 宽）对用户来说和消失没有区别
        int minVisibleX = (int)(width * 0.6);
        int minVisibleY = (int)(height * 0.6);

        bool tooFarOut = visibleWidth < Math.Min(minVisibleX, width)
            || visibleHeight < Math.Min(minVisibleY, height);

        if (!tooFarOut)
        {
            return;
        }

        int targetLeft = Math.Clamp(r.Left, work.Left - width + minVisibleX, work.Right - minVisibleX);
        int targetTop = Math.Clamp(r.Top, work.Top - height + minVisibleY, work.Bottom - minVisibleY);

        // 用物理像素直接 SetWindowPos，而不是换算成 DIP 去写 Left/Top：
        // Window.Left/Top 是 DIP，但"1 DIP 等于多少物理像素"取决于窗口当前所在（或即将去往）
        // 的显示器。多显示器不同缩放时，用当前窗口 DPI 换算会偏 ~20%。
        SetWindowPos(hwnd, IntPtr.Zero, targetLeft, targetTop, 0, 0,
            SwpNosize | SwpNozorder | SwpNoactivate);

        Diag.Log($"EnsureOnScreen: ({r.Left},{r.Top}) -> ({targetLeft},{targetTop}) "
            + $"work=({work.Left},{work.Top},{work.Right},{work.Bottom})");
    }

    private const int MONITOR_DEFAULTTONEAREST = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECTlp rect, int flags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEXW info);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RectW
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public int CbSize;
        public RectW RcMonitor;
        public RectW RcWork;
        public uint DwFlags;

        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string SzDevice;
    }

    private static int MarshalSizeOfMonitorInfo() => System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEXW>();

    /// <summary>
    /// 从托盘唤出：mac 出现动画 —— 整窗淡入 + 从 1.05 缩回 1，图标再依次弹入
    /// （与首次启动共用 PlayEntrance），然后补抓一帧背景。
    /// </summary>
    private void ShowFromTray()
    {
        Diag.Log($"ShowFromTray visible={IsVisible} state={WindowState} inModalMove={_inModalMove}");

        if (IsVisible)
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        ResetShellTransform();
        Show();
        EnsureOnScreen();
        ForceForeground();

        // 洗掉 Hide/Show 留下的脏 DWM 表面（消失 bug 的毒源，见 RebuildDwmSurface）。
        // 趁下面 300ms 淡入动画窗口还半透明时做，表面重建视觉上不可见
        Dispatcher.BeginInvoke(RebuildDwmSurface, DispatcherPriority.Loaded);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease };
        var growX = new DoubleAnimation(1.05, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease };
        var growY = new DoubleAnimation(1.05, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease };

        Shell.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, growX, HandoffBehavior.SnapshotAndReplace);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, growY, HandoffBehavior.SnapshotAndReplace);

        Dispatcher.BeginInvoke(PlayEntrance, DispatcherPriority.Loaded);
        // 同上：Fire-and-forget 的时机不能直接传带可选参数的方法组
        Dispatcher.BeginInvoke((Action)(() => FreezeBackdrop()), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 强制前台。Window.Activate() 在“其他程序是前台”时会失败 —— Windows 不允许
    /// 后台进程随便抢焦点（实测复现：从别的应用按快捷键，窗口 Show 了但被压在
    /// 别人下面，看起来就是“唤不起来”）。
    /// 绕法是经典三步：临时把输入队列和当前前台窗口接在一起（AttachThreadInput），
    /// 我们就“有资格”设置前台了，然后 BringWindowToTop + SetForegroundWindow。
    /// </summary>
    private void ForceForeground()
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        IntPtr foreground = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        uint currentThread = GetCurrentThreadId();

        bool attached = foreground != IntPtr.Zero
            && foregroundThread != 0
            && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            Activate();
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    /// <summary>托盘右键“退出”：落盘一次，播收场动画后真关（Closing 里 _exiting 放行）。</summary>
    private void ExitApplication()
    {
        _exiting = true;
        Persist();
        Close();
    }

    /// <summary>把 Shell 的透明度/缩放动画全部清掉并复位到 1（隐藏后复位的公共步骤）。</summary>
    private void ResetShellTransform()
    {
        Shell.BeginAnimation(OpacityProperty, null);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Shell.Opacity = 1;
        ShellScale.ScaleX = 1;
        ShellScale.ScaleY = 1;
    }

    // ── 动效 ────────────────────────────────────────────────────
    //
    // macOS 启动台的“活”来自三件事，这里逐条对上：
    //   1) 打开时图标不是一起出现，而是从左上到右下依次弹入（stagger + 轻微过冲）；
    //   2) 悬停是弹簧，不是线性放大（轻微冲过再回落，见 XAML 里的 ElasticEase）；
    //   3) 点下去有一记回弹，告诉你“收到了”，而不是等程序启动才有反应。
    //
    // 全部只动 RenderTransform / Opacity，不碰布局，40 多个图标同时跑也不掉帧。

    /// <summary>关闭动画：整窗缩到 0.94 + 淡出（200ms），播完真关。</summary>
    private void PlayCloseThenClose()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        var shrinkX = new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        var shrinkY = new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };

        fade.Completed += (_, _) =>
        {
            _closeAnimated = true;
            Close();
        };

        Shell.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrinkX, HandoffBehavior.SnapshotAndReplace);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrinkY, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>入场：图标按顺序弹入。集合越大间隔越小，避免 40 个图标排到两秒后。</summary>
    private void PlayEntrance()
    {
        int count = _entries.Count;
        if (count == 0)
        {
            return;
        }

        AppItems.UpdateLayout();

        double stagger = Math.Min(0.020, 0.42 / count);
        var grow = new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut };

        for (int i = 0; i < count; i++)
        {
            if (AppItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container
                || FindTemplatePart(container, "Root") is not FrameworkElement root
                || root.RenderTransform is not ScaleTransform scale)
            {
                continue;
            }

            // 基准值先摆好：FillBehavior=Stop 时动画结束会回到基准，回到 1 才不会有一下闪。
            root.Opacity = 1;
            scale.ScaleX = 1;
            scale.ScaleY = 1;

            var begin = TimeSpan.FromSeconds(i * stagger);

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                BeginTime = begin,
                FillBehavior = FillBehavior.Stop,
            };

            var pop = new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(340))
            {
                BeginTime = begin,
                EasingFunction = grow,
                FillBehavior = FillBehavior.Stop,
            };

            root.BeginAnimation(OpacityProperty, fade);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
    }

    /// <summary>点下去的确认感：放大 14% 再弹回，全程 260ms。</summary>
    private void PlayLaunchBounce(AppEntry entry)
    {
        if (AppItems.ItemContainerGenerator.ContainerFromItem(entry) is not FrameworkElement container
            || FindTemplatePart(container, "Root") is not FrameworkElement root
            || root.RenderTransform is not ScaleTransform scale)
        {
            return;
        }

        var bounce = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.14, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))
            , new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(270))
            , new CubicEase { EasingMode = EasingMode.EaseOut }));

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
    }

    /// <summary>在模板里按 x:Name 找元素（DataTemplate 里的 Root / RootScale 不在命名域里，找不到名字）。</summary>
    private static FrameworkElement? FindTemplatePart(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && element.Name == name)
        {
            return element;
        }

        int children = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < children; i++)
        {
            FrameworkElement? found = FindTemplatePart(VisualTreeHelper.GetChild(root, i), name);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    // ── 持久化 ──────────────────────────────────────────────────

    private void LoadFromStore()
    {
        List<AppEntry> loaded = LauncherStore.Load(out string? error);

        foreach (AppEntry entry in loaded)
        {
            entry.Icon = IconService.LoadOrCreate(entry);
            _entries.Add(entry);
        }

        if (error is not null)
        {
            ShowStatus($"读取数据失败：{error}");
        }
    }

    /// <summary>写回磁盘。顺序变化后务必先 Renumber 再保存，否则重开后顺序会乱。</summary>
    private void Persist()
    {
        Renumber();

        if (!LauncherStore.Save(_entries, out string? error) && error is not null)
        {
            ShowStatus($"保存失败：{error}");
        }
    }

    /// <summary>启动时异步检查失效条目，结果回到 UI 线程再改属性。</summary>
    private async void RunBrokenCheckAsync()
    {
        if (_entries.Count == 0)
        {
            return;
        }

        IReadOnlyList<BrokenCheckResult> results =
            await BrokenChecker.CheckAsync(new List<AppEntry>(_entries).AsReadOnly());

        bool changed = false;
        foreach (BrokenCheckResult result in results)
        {
            if (result.Entry.IsBroken != result.IsBroken)
            {
                result.Entry.IsBroken = result.IsBroken;
                changed = true;
            }
        }

        if (changed)
        {
            Persist();
        }
    }

    // ── 网格内拖拽排序 ──────────────────────────────────────────
    //
    // 这套手势是自己接鼠标事件实现的，没有走 OLE 的 DoDragDrop。原因有三条，都是实测出来的：
    //
    //   1) DoDragDrop 期间鼠标位置只能从 IDropTarget.DragOver 里拿，事件到达频率由系统决定，
    //      预览必然每隔几帧才动一下 —— 这就是“一卡一卡”的主因；
    //   2) 每收到一次 DragOver 都要摸一遍布局（UpdateLayout + 逐项 TranslatePoint），
    //      几十个条目被反复重排，越拖越黏；
    //   3) 截图对比时还发现，OLE 拖放期间系统的拖拽循环会改写光标反馈，
    //      我们再怎么调都是一顿一顿。
    //
    // 改成 MouseMove 里 1:1 跟随之后，指针移动多少像素，图标就走多少像素，
    // 中间没有任何异步环节，手感才和 macOS 启动台一致。
    // 外部文件拖入仍然走 WPF 原生的 DragOver/Drop，两条通道互不干扰。
    //
    // 视觉上也不再画那条蓝色插入线：受影响的一串图标会顺次挪一格，空出的格子就是落点。
    // 位移全部走 RenderTransform 补间，不触发重新布局，所以“让位”是滑过去的而不是跳过去的。

    private const double DragStartThreshold = 6;   // 小于它算单击，大于它才算拖
    private const double AutoScrollEdge = 26;      // 距离上下沿多近开始自动滚动
    private const double AutoScrollStep = 16;      // 每个节拍滚多少像素

    private void OnItemMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
#if DEBUG
        if (_synthetic)
        {
            return;
        }
#endif

        if (sender is not FrameworkElement element || element.DataContext is not AppEntry entry)
        {
            return;
        }

        _pressedEntry = entry;
        _pressPoint = e.GetPosition(AppItems);
        _pointerInLayer = e.GetPosition(DragLayer);

        // 按下就捕获鼠标：手快的时候指针一下子甩出条目范围，
        // 没有捕获就会丢 MouseMove / MouseUp，手势停在半路。
        element.CaptureMouse();
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
#if DEBUG
        if (_synthetic)
        {
            return;
        }
#endif

        if (_pressedEntry is null)
        {
            return;
        }

        // 在别处松手（或者收到了不配对的抬起）时别把状态卡住
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelGesture();
            return;
        }

        _pointerInLayer = e.GetPosition(DragLayer);

#if DEBUG
        if (_dragActive)
        {
            double now = _dragClock.Elapsed.TotalMilliseconds;
            double gap = now - _lastMoveMs;
            _lastMoveMs = now;

            if (gap > _maxMoveGap)
            {
                _maxMoveGap = gap;
            }

            _moveCount++;
        }
#endif

        if (!_dragActive)
        {
            Vector delta = e.GetPosition(AppItems) - _pressPoint;

            if (Math.Abs(delta.X) < DragStartThreshold && Math.Abs(delta.Y) < DragStartThreshold)
            {
                return;
            }

            if (!BeginDrag())
            {
                // 格子快照拿不全就退化成普通点击，绝不半拖
                _pressedEntry = null;
                ReleaseCapture();
                return;
            }
        }

        UpdateDrag(_pointerInLayer);
        UpdateAutoScroll(e.GetPosition(GridScroller));
    }

    private void OnItemMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
#if DEBUG
        if (_synthetic)
        {
            return;
        }
#endif

        if (_dragActive)
        {
            EndDrag(commit: true);
            e.Handled = true;
            return;
        }

        AppEntry? pressed = _pressedEntry;
        bool sameItem = sender is FrameworkElement element && ReferenceEquals(element.DataContext, pressed);

        Point release = e.GetPosition(AppItems);
        Vector delta = release - _pressPoint;

        _pressedEntry = null;
        ReleaseCapture();

        if (pressed is null || !sameItem)
        {
            return;
        }

        // 位移超过阈值却没能起拖（比如格子快照失败）→ 什么都不做，避免误启动
        if (Math.Abs(delta.X) >= DragStartThreshold || Math.Abs(delta.Y) >= DragStartThreshold)
        {
            return;
        }

        LaunchEntry(pressed, asAdministrator: false);
    }

    /// <summary>捕获被系统或别的元素抢走时的兜底。</summary>
    private void OnItemLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragActive)
        {
            EndDrag(commit: false);
        }
        else
        {
            _pressedEntry = null;
        }
    }

    private void CancelGesture()
    {
        if (_dragActive)
        {
            EndDrag(commit: false);
            return;
        }

        _pressedEntry = null;
        ReleaseCapture();
    }

    private static void ReleaseCapture() => Mouse.Captured?.ReleaseMouseCapture();

    /// <summary>把光标下的图标“拿起来”：隐藏原位、生成浮起副本、开始记录格子。</summary>
    private bool BeginDrag()
    {
        // 搜索过滤激活时视图是主列表的子集：容器索引（SlotShifter 记的格子、落点）
        // 和主列表索引对不上，硬排序必然乱序。降级为普通点击（单击仍然能启动应用）。
        if (_filterActive)
        {
            return false;
        }

        if (_pressedEntry is not { } entry)
        {
            return false;
        }

        int from = _entries.IndexOf(entry);

        if (from < 0 || !_slots.Capture(AppItems))
        {
            return false;
        }

        Point center = _slots.IconCenterAt(from);

        // 记住“按下点相对图标中心”的偏移，全程不改；
        // 这样从图标角落按下去也不会在起飞瞬间把图标吸到光标正中。
        _grabOffset = _pressPoint - center;

        // 上一轮落位动画还没播完就又开始拖：先把可能残留的隐藏标记清掉
        foreach (AppEntry other in _entries)
        {
            if (other.IsDragging && !ReferenceEquals(other, entry))
            {
                other.IsDragging = false;
            }
        }

        _dragFrom = from;
        _dragTo = from;
        _dragActive = true;

        // 原位隐藏由数据驱动（模板里绑 IsDragging），Move 之后容器重新绑定也不会找错人
        entry.IsDragging = true;

        _ghost = new DragGhost(DragLayer, entry.Icon, entry.Initial, (Brush)FindResource("ItemInitialBrush"));
        _ghost.MoveTo(center);   // 先在原位出现，与被隐藏的图标严丝合缝
        _ghost.Lift();

        _autoScrollDelta = 0;
        _autoScrollTimer.Start();

#if DEBUG
        _dragClock.Restart();
        _lastMoveMs = 0;
        _moveCount = 0;
        _maxMoveGap = 0;
#endif

        return true;
    }

    /// <summary>拖拽中：算落点、让邻居让位、把副本摆到光标下。每次 MouseMove 都会走一遍，必须极轻。</summary>
    private void UpdateDrag(Point pointerInLayer)
    {
        if (!_dragActive || _ghost is null)
        {
            return;
        }

        Point pointerInGrid = DragLayer.TranslatePoint(pointerInLayer, AppItems);
        int to = SlotAt(pointerInGrid);

        if (to != _dragTo)
        {
            _dragTo = to;
            _slots.ApplyGap(_dragFrom, to);
        }

        _ghost.MoveTo(pointerInLayer - _grabOffset);
    }

    /// <summary>
    /// 指针压在哪一格上，就落到哪一格。
    ///
    /// 这里刻意不用“插到某两格之间”的算法：那种算法在指针正好压住某格中心时，
    /// 会因为“左半边=插前面”的判定把落点放到左边一格，松手瞬间图标还要横跳半格。
    /// 大图标网格里，用户认定的落点就是光标底下这一格，取最近中心最符合直觉，
    /// 也让浮起的副本始终正好悬在空出来的格子上。
    /// </summary>
    private int SlotAt(Point position)
    {
        int count = _entries.Count;

        if (count == 0)
        {
            return 0;
        }

        int best = 0;
        double bestDistance = double.MaxValue;

        for (int i = 0; i < count; i++)
        {
            Rect bounds = _slots.RectAt(i);

            if (bounds.IsEmpty)
            {
                continue;
            }

            double dx = position.X - (bounds.Left + (bounds.Width / 2));
            double dy = position.Y - (bounds.Top + (bounds.Height / 2));
            double distance = (dx * dx) + (dy * dy);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>收尾：把条目落到目标格，让副本飞回去淡出。</summary>
    private void EndDrag(bool commit)
    {
        if (!_dragActive)
        {
            return;
        }

#if DEBUG
        Diag.Log($"EndDrag commit={commit} from={_dragFrom} to={_dragTo} moves={_moveCount} "
            + $"maxGap={_maxMoveGap:0.0}ms avgGap={(_moveCount > 1 ? _dragClock.Elapsed.TotalMilliseconds / _moveCount : 0):0.0}ms "
            + $"via={new System.Diagnostics.StackTrace(1, false).GetFrame(0)?.GetMethod()?.Name}");
#endif

        // 先把状态一次性取出来：下面 ReleaseMouseCapture 会同步触发 LostMouseCapture，
        // 可能重入到这里，取完局部量之后随便重入都不会把状态搅乱。
        AppEntry? entry = _pressedEntry;
        int from = _dragFrom;
        int to = _dragTo;
        DragGhost? ghost = _ghost;

        _dragActive = false;
        _dragFrom = -1;
        _dragTo = -1;
        _pressedEntry = null;
        _ghost = null;
        _autoScrollDelta = 0;
        _autoScrollTimer.Stop();

        int landing = commit ? to : from;
        Point landingCenter = AppItems.TranslatePoint(_slots.IconCenterAt(landing), DragLayer);

        if (commit && entry is not null && to != from && to >= 0)
        {
            _entries.Move(from, to);
        }

        // 关键顺序：Move 之后、重新布局之前清位移。
        // 此刻每个容器的真实位置正好等于它刚才被推到的位置，归零不会出现任何跳变。
        _slots.Reset();

        ReleaseCapture();

        if (entry is null || ghost is null)
        {
            if (entry is not null)
            {
                entry.IsDragging = false;
            }

            ghost?.Remove();

            if (commit)
            {
                Persist();
            }

            return;
        }

        // 落定之后才让条目显形，避免副本还在飞、原位已经出现第二个图标
        ghost.Settle(landingCenter, () =>
        {
            entry.IsDragging = false;

            if (commit)
            {
                Persist();
            }
        });
    }

    /// <summary>窗口关掉时把“正在拖拽”的痕迹擦干净，否则下次打开条目会是隐藏的。</summary>
    private void ClearDragFlags()
    {
        foreach (AppEntry entry in _entries)
        {
            entry.IsDragging = false;
        }
    }

    private void UpdateAutoScroll(Point pointerInScroller)
    {
        if (pointerInScroller.Y < AutoScrollEdge)
        {
            _autoScrollDelta = -AutoScrollStep;
        }
        else if (pointerInScroller.Y > GridScroller.ActualHeight - AutoScrollEdge)
        {
            _autoScrollDelta = AutoScrollStep;
        }
        else
        {
            _autoScrollDelta = 0;
        }
    }

    private void AutoScrollTick()
    {
        if (!_dragActive || _autoScrollDelta == 0)
        {
            return;
        }

        double before = GridScroller.VerticalOffset;
        double after = Math.Clamp(before + _autoScrollDelta, 0, GridScroller.ScrollableHeight);

        if (Math.Abs(after - before) < 0.5)
        {
            return;
        }

        GridScroller.ScrollToVerticalOffset(after);

        // 内容滚了，但光标在屏幕上没动 —— 相对内容的位置变了，落点要重算。
        // 格子矩形是按内容坐标缓存的，滚动不影响它们，所以这里不用重建快照。
        UpdateDrag(_pointerInLayer);
    }

    // ── 标题栏按钮 ──────────────────────────────────────────────

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();


    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ── 外部拖入：只处理资源管理器拖过来的文件 ──────────────────
    //
    // 内部排序已经改走自己的鼠标手势（见上一节），不会再进这几个回调，
    // 所以这里不用再区分“是自己在拖还是外面拖进来”。

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        DropHighlight.Visibility = Visibility.Visible;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            _insertIndex = -1;
            HideInsertLine();
            e.Handled = true;
            return;
        }

        _insertIndex = CalculateInsertIndex(e.GetPosition(AppItems));
        ShowInsertLine(_insertIndex);

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        HideInsertLine();
        _insertIndex = -1;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        HideInsertLine();

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            e.Handled = true;
            return;
        }

        int index = _insertIndex < 0 ? _entries.Count : _insertIndex;
        _insertIndex = -1;

        var failures = new List<string>();
        int inserted = 0;
        int duplicated = 0;

        foreach (string path in paths)
        {
            if (!ShortcutResolver.TryCreate(path, out AppEntry entry, out string error))
            {
                failures.Add(error);
                continue;
            }

            if (IndexOfTarget(entry.TargetPath) >= 0)
            {
                duplicated++;
                continue;
            }

            _entries.Insert(Math.Min(index + inserted, _entries.Count), entry);
            entry.Icon = IconService.LoadOrCreate(entry);
            inserted++;
        }

        if (inserted > 0)
        {
            Persist();
            HideStatus();
        }

        if (failures.Count > 0 || duplicated > 0)
        {
            var parts = new List<string>();
            if (duplicated > 0)
            {
                parts.Add($"{duplicated} 个已在列表中，已跳过");
            }

            parts.AddRange(failures);
            ShowStatus(string.Join(" · ", parts));
        }

        e.Handled = true;
    }

    /// <summary>
    /// 把第 from 个条目移动到“插入到第 insertIndex 个位置之前”。
    /// insertIndex 是移除前的坐标，往后移时要减 1，否则会差一格。
    /// </summary>
    private void MoveEntry(int from, int insertIndex)
    {
        if (from < 0 || from >= _entries.Count || insertIndex < 0 || insertIndex > _entries.Count)
        {
            return;
        }

        int to = insertIndex > from ? insertIndex - 1 : insertIndex;
        to = Math.Clamp(to, 0, _entries.Count - 1);

        if (to == from)
        {
            return;
        }

        _entries.Move(from, to);
        Persist();
    }

    // ── 插入位置计算 ────────────────────────────────────────────

    /// <summary>外部拖入用：布局可能刚变过，先同步一次再算。</summary>
    private int CalculateInsertIndex(Point position)
    {
        if (_entries.Count == 0)
        {
            return 0;
        }

        AppItems.UpdateLayout();

        return FindInsertIndex(position, _entries.Count, GetItemBounds);
    }

    /// <summary>
    /// 找到“拖到两个条目之间”对应的索引：先按行定位（落在某行的垂直范围内），
    /// 再按该行的水平中点决定落在前一个还是后一个。
    ///
    /// 格子矩形由调用方提供：内部拖拽传缓存快照（零布局开销），
    /// 外部拖入传实时查询（布局随时可能被窗口尺寸改掉）。
    /// </summary>
    private static int FindInsertIndex(Point position, int count, Func<int, Rect> rectAt)
    {
        for (int i = 0; i < count; i++)
        {
            Rect bounds = rectAt(i);

            if (bounds.IsEmpty)
            {
                continue;
            }

            if (position.Y < bounds.Top)
            {
                return i;
            }

            if (position.Y <= bounds.Bottom && position.X < bounds.Left + (bounds.Width / 2))
            {
                return i;
            }
        }

        return count;
    }

    private Rect GetItemBounds(int index)
    {
        if (AppItems.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
        {
            return Rect.Empty;
        }

        try
        {
            Point origin = container.TranslatePoint(new Point(0, 0), AppItems);
            return new Rect(origin, new Size(container.ActualWidth, container.ActualHeight));
        }
        catch
        {
            return Rect.Empty;
        }
    }

    private void ShowInsertLine(int index)
    {
        if (_entries.Count == 0)
        {
            HideInsertLine();
            return;
        }

        double x;
        double y;

        if (index < _entries.Count)
        {
            Rect bounds = GetItemBounds(index);
            if (bounds.IsEmpty)
            {
                HideInsertLine();
                return;
            }

            x = bounds.Left - 5;
            y = bounds.Top;
        }
        else
        {
            Rect bounds = GetItemBounds(_entries.Count - 1);
            if (bounds.IsEmpty)
            {
                HideInsertLine();
                return;
            }

            x = bounds.Right + 2;
            y = bounds.Top;
        }

        try
        {
            Point point = AppItems.TranslatePoint(new Point(x, y), DropLayer);
            Canvas.SetLeft(InsertLine, point.X);
            Canvas.SetTop(InsertLine, point.Y);
            InsertLine.Visibility = Visibility.Visible;
        }
        catch
        {
            HideInsertLine();
        }
    }

    private void HideInsertLine() => InsertLine.Visibility = Visibility.Collapsed;

    // ── 启动与菜单 ──────────────────────────────────────────────

    private void LaunchEntry(AppEntry entry, bool asAdministrator)
    {
        // 先给确认反馈再真正启动：启动是同步调 ShellExecute，慢的时候这一下会明显“卡”
        PlayLaunchBounce(entry);

        LaunchResult result = LaunchService.TryLaunch(entry, asAdministrator, out string? error);

        switch (result)
        {
            case LaunchResult.Started:
                HideStatus();
                break;

            case LaunchResult.Cancelled:
                // 用户自己取消了 UAC，不是条目的问题
                ShowStatus($"已取消：{entry.DisplayName}");
                break;

            default:
                entry.IsBroken = true;
                Persist();
                ShowStatus($"启动失败：{entry.DisplayName} — {error}");
                break;
        }
    }

    private static AppEntry? GetEntryFromMenu(object sender)
    {
        if (sender is not MenuItem item)
        {
            return null;
        }

        if (item.DataContext is AppEntry direct)
        {
            return direct;
        }

        // 兜底：沿逻辑树往上找 ContextMenu，再取 PlacementTarget
        DependencyObject? current = item;
        for (int depth = 0; depth < 6 && current is not null; depth++)
        {
            if (current is ContextMenu menu && menu.PlacementTarget is FrameworkElement target)
            {
                return target.DataContext as AppEntry;
            }

            current = current is FrameworkElement frameworkElement
                ? frameworkElement.Parent ?? LogicalTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private void OnMenuLaunch(object sender, RoutedEventArgs e)
    {
        if (GetEntryFromMenu(sender) is AppEntry entry)
        {
            LaunchEntry(entry, asAdministrator: false);
        }
    }

    private void OnMenuRunAsAdmin(object sender, RoutedEventArgs e)
    {
        if (GetEntryFromMenu(sender) is AppEntry entry)
        {
            LaunchEntry(entry, asAdministrator: true);
        }
    }

    private void OnMenuRename(object sender, RoutedEventArgs e)
    {
        if (GetEntryFromMenu(sender) is not AppEntry entry)
        {
            return;
        }

        string? name = PromptDialog.Ask(this, "重命名", entry.DisplayName);
        if (!string.IsNullOrWhiteSpace(name) && name != entry.DisplayName)
        {
            entry.DisplayName = name;
            Persist();
        }
    }

    private void OnMenuRetarget(object sender, RoutedEventArgs e)
    {
        if (GetEntryFromMenu(sender) is not AppEntry entry)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "重新指定目标",
            Filter = "程序 (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        entry.TargetPath = dialog.FileName;
        entry.SourcePath = dialog.FileName;
        entry.IconLocation = string.Empty;
        entry.IconCachePath = string.Empty;
        entry.WorkingDirectory = System.IO.Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
        entry.Icon = IconService.LoadOrCreate(entry);
        entry.IsBroken = false;

        Persist();
        ShowStatus($"已重新指定：{entry.DisplayName}");
    }

    private void OnMenuReveal(object sender, RoutedEventArgs e)
    {
        if (GetEntryFromMenu(sender) is not AppEntry entry)
        {
            return;
        }

        if (!LaunchService.TryReveal(entry, out string? error) && error is not null)
        {
            ShowStatus($"无法打开位置：{error}");
        }
    }

    private void OnMenuRemove(object sender, RoutedEventArgs e)
    {
        if (GetEntryFromMenu(sender) is not AppEntry entry)
        {
            return;
        }

        _entries.Remove(entry);
        Persist();
        ShowStatus($"已移除：{entry.DisplayName}");
    }

    // ── 辅助 ────────────────────────────────────────────────────

    private int IndexOfTarget(string targetPath)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (string.Equals(_entries[i].TargetPath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private void Renumber()
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            _entries[i].SortOrder = i;
        }
    }

    private void UpdateEmptyState()
    {
        // 过滤激活时以“视图里还剩几个”为准，否则以主列表为准
        bool visibleEmpty = _filterActive
            ? CollectionViewSource.GetDefaultView(_entries).IsEmpty
            : _entries.Count == 0;

        EmptyHint.Visibility = visibleEmpty ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _filterActive ? "没有匹配的应用" : "把程序或快捷方式拖进来";
        EmptySub.Text = _filterActive
            ? "换个关键词试试，或按 Esc 清空搜索"
            : "支持 .lnk / .url / .exe / 文件夹，可以直接拖到两个图标之间插入";
    }

    // ── 搜索过滤 ────────────────────────────────────────────────

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        bool empty = string.IsNullOrEmpty(SearchBox.Text);
        SearchHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SearchClear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        _filterActive = query.Length > 0;

        ICollectionView view = CollectionViewSource.GetDefaultView(_entries);
        view.Filter = o => !_filterActive
            || (o is AppEntry entry
                && entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));

        // 拖拽手势可能正进行到一半时清空了搜索，手势的索引体系会失效
        if (_dragActive)
        {
            EndDrag(commit: false);
        }

        UpdateEmptyState();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SearchBox.Text.Length > 0)
            {
                SearchBox.Clear();
            }

            e.Handled = true;
        }
    }

    private void OnSearchClearClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    // ── 设置 ────────────────────────────────────────────────────

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_settings, _ => ApplySettings()) { Owner = this };
        dialog.ShowDialog();
    }

    /// <summary>把用户设置落到窗口上：主题、模糊半径、置顶。设置对话框每改一项就调一次。</summary>
    private void ApplySettings()
    {
        bool dark = _settings.Theme switch
        {
            ThemeMode.Light => false,
            ThemeMode.Dark => true,
            _ => ThemeService.IsSystemDark(),
        };

        if (ThemeService.IsDarkMode != dark)
        {
            ThemeService.Apply(dark);
        }

        // 两个冻结层（底帧 + 交叉淡入层）各持一份 BlurEffect，浓度要同步改
        if (FrozenSurface.Effect is BlurEffect blur)
        {
            blur.Radius = _settings.BlurRadius;
        }

        if (FrozenSurfaceAlt.Effect is BlurEffect blurAlt)
        {
            blurAlt.Radius = _settings.BlurRadius;
        }

        Topmost = _settings.Topmost;

        // 快捷键可能被改：解绑旧注册再按新配置绑（文本没变时解绑重绑也无害）
        ApplyHotkeySetting();
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusBar.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void HideStatus()
    {
        _statusTimer.Stop();
        StatusBar.Visibility = Visibility.Collapsed;
    }

#if DEBUG
    /// <summary>
    /// 仅调试版：GLASSLAUNCHER_BACKDROP=mica|acrylic|none 覆盖材质，
    /// 用来在同一个窗口、同一份数据上对比 Mica 与 Acrylic 的观感，不用改 XAML 重编译。
    /// </summary>
    private void ApplyBackdropProbe()
    {
        string? probe = Environment.GetEnvironmentVariable("GLASSLAUNCHER_BACKDROP");
        if (string.IsNullOrWhiteSpace(probe))
        {
            return;
        }

        Backdrop = probe.Trim().ToLowerInvariant() switch
        {
            "mica" => GlassBackdrop.Mica,
            "acrylic" => GlassBackdrop.Acrylic,
            "optical" => GlassBackdrop.Optical,
            "none" => GlassBackdrop.None,
            _ => Backdrop,
        };

        SyncGlassMode();
        Diag.Log($"BackdropProbe → {Backdrop}");
    }

    private void RunDebugProbes()
    {
        RunHoverProbe();
        RunReorderProbe();
        RunBrokenCheckProbe();
        RunDragProbe();
    }

    /// <summary>
    /// 仅调试版：GLASSLAUNCHER_DRAGPROBE="from,to" 用合成指针走一遍完整拖拽手势
    /// （拿起 → 中间逐帧移动 → 悬停 → 落位），全程不碰真实鼠标。
    /// 用来验证“格子让位 / 落点换算 / 落位后顺序落盘”，也方便在悬停那一刻截图看视觉。
    /// </summary>
    private void RunDragProbe()
    {
        string? probe = Environment.GetEnvironmentVariable("GLASSLAUNCHER_DRAGPROBE");
        if (string.IsNullOrWhiteSpace(probe))
        {
            return;
        }

        string[] parts = probe.Split(',');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out int from)
            || !int.TryParse(parts[1], out int to))
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => AnimateProbeDrag(from, to)));
    }

    private void AnimateProbeDrag(int from, int to)
    {
        if (from < 0 || from >= _entries.Count || to < 0 || to >= _entries.Count)
        {
            Diag.Log($"DragProbe 参数越界 from={from} to={to} count={_entries.Count}");
            return;
        }

        Rect slot = GetItemBounds(from);
        if (slot.IsEmpty)
        {
            Diag.Log("DragProbe 取不到条目矩形");
            return;
        }

        var start = new Point(slot.Left + (slot.Width / 2), slot.Top + SlotShifter.IconCenterOffsetY);
        string before = DescribeOrder();

        _synthetic = true;
        _pressedEntry = _entries[from];
        _pressPoint = start;
        _pointerInLayer = AppItems.TranslatePoint(start, DragLayer);

        if (!BeginDrag())
        {
            Diag.Log("DragProbe BeginDrag 失败");
            _pressedEntry = null;
            return;
        }

        Point endLayer = AppItems.TranslatePoint(_slots.IconCenterAt(to), DragLayer);
        Point originLayer = _pointerInLayer;

        const int steps = 42;
        int step = 0;

        var move = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        move.Tick += (_, _) =>
        {
            step++;
            double t = Math.Min(1.0, step / (double)steps);
            double ease = 1 - Math.Pow(1 - t, 3);

            _pointerInLayer = new Point(
                originLayer.X + ((endLayer.X - originLayer.X) * ease),
                originLayer.Y + ((endLayer.Y - originLayer.Y) * ease));

            UpdateDrag(_pointerInLayer);

            if (t < 1)
            {
                return;
            }

            move.Stop();
            Diag.Log($"DragProbe 悬停 from={from} to={to} dragTo={_dragTo} "
                + $"layerChildren={DragLayer.Children.Count} layerSize={DragLayer.ActualWidth}x{DragLayer.ActualHeight} "
                + $"ghost={_ghost?.Describe() ?? "null"} "
                + $"slotCenter={AppItems.TranslatePoint(_slots.IconCenterAt(_dragTo), DragLayer)}");

            // 停在落点上，外部截图脚本正好能拍到“图标浮起 + 邻居已让位”的那一帧
            double holdMs = double.TryParse(Environment.GetEnvironmentVariable("GLASSLAUNCHER_DRAGPROBE_HOLD"), out double ms)
                ? ms
                : 1600;
            var hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(holdMs) };
            hold.Tick += (_, _) =>
            {
                hold.Stop();
                EndDrag(commit: true);
                _synthetic = false;

                var verify = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                verify.Tick += (_, _) =>
                {
                    verify.Stop();
                    Diag.Log($"DragProbe 完成 before=[{before}] after=[{DescribeOrder()}]");
                };
                verify.Start();
            };
            hold.Start();
        };

        move.Start();
    }

    /// <summary>
    /// 仅调试版：GLASSLAUNCHER_REORDER="from,insertIndex"，直接走一遍 MoveEntry 并落盘，
    /// 用来验证“往后移要减 1”的索引换算，以及 JSON 里的 SortOrder 是否正确。
    /// </summary>
    private void RunReorderProbe()
    {
        string? probe = Environment.GetEnvironmentVariable("GLASSLAUNCHER_REORDER");
        if (string.IsNullOrWhiteSpace(probe))
        {
            return;
        }

        string[] parts = probe.Split(',');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out int from)
            || !int.TryParse(parts[1], out int insertIndex))
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            string before = DescribeOrder();
            MoveEntry(from, insertIndex);
            Diag.Log($"ReorderProbe from={from} insert={insertIndex} before=[{before}] after=[{DescribeOrder()}]");
        }));
    }

    /// <summary>仅调试版：GLASSLAUNCHER_BROKEN=1 时立刻跑一次失效检测，便于在日志里看到结果。</summary>
    private void RunBrokenCheckProbe()
    {
        if (Environment.GetEnvironmentVariable("GLASSLAUNCHER_BROKEN") != "1")
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RunBrokenCheckAsync));
    }

    private string DescribeOrder() => string.Join(",", _entries.Select(e => e.DisplayName));

    /// <summary>
    /// 仅调试版：环境变量 GLASSLAUNCHER_HOVER="x,y"（相对网格左上角的 DIP 坐标），
    /// 直接走一遍“拖拽悬停”的插入位置计算并显示指示线，用来验证位置算法。
    /// </summary>
    private void RunHoverProbe()
    {
        string? probe = Environment.GetEnvironmentVariable("GLASSLAUNCHER_HOVER");
        if (string.IsNullOrWhiteSpace(probe))
        {
            return;
        }

        string[] parts = probe.Split(',');
        if (parts.Length != 2
            || !double.TryParse(parts[0], out double x)
            || !double.TryParse(parts[1], out double y))
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            AppItems.UpdateLayout();
            int index = CalculateInsertIndex(new Point(x, y));
            ShowInsertLine(index);
            Diag.Log($"HoverProbe point=({x},{y}) insertIndex={index} count={_entries.Count}");
        }));
    }

    /// <summary>
    /// 仅调试版：从环境变量 GLASSLAUNCHER_SEED 读取若干路径直接入列，
    /// 便于在没有真实拖拽的情况下验证网格与持久化。Release 版不编译这段。
    /// </summary>
    private void SeedFromEnvironment()
    {
        string? seed = Environment.GetEnvironmentVariable("GLASSLAUNCHER_SEED");
        if (string.IsNullOrWhiteSpace(seed))
        {
            return;
        }

        bool added = false;

        foreach (string path in seed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ShortcutResolver.TryCreate(path, out AppEntry entry, out string error))
            {
                if (IndexOfTarget(entry.TargetPath) >= 0)
                {
                    continue;
                }

                entry.Icon = IconService.LoadOrCreate(entry);
                _entries.Add(entry);
                added = true;
            }
            else
            {
                Diag.Log($"Seed 失败 {path}：{error}");
            }
        }

        if (added)
        {
            Persist();
        }
    }
#endif
}
