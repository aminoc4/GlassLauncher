using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using GlassLauncher.Services;

namespace GlassLauncher.Chrome;

/// <summary>玻璃基底类型。</summary>
public enum GlassBackdrop
{
    /// <summary>不使用系统材质，窗口为不透明面板。</summary>
    None,

    /// <summary>Mica（仅 Windows 11）。</summary>
    Mica,

    /// <summary>Acrylic 毛玻璃（Win11 走系统 DWMSBT_TRANSIENTWINDOW）。</summary>
    Acrylic,

    /// <summary>
    /// 带 tint 的 Acrylic：走 ACCENT_ENABLE_ACRYLICBLURBEHIND，染色里的 alpha 由我们控制。
    /// 和 <see cref="Acrylic"/> 的区别只在这张 tint 上，用来做“通透度”对照。
    /// </summary>
    TintedAcrylic,

    /// <summary>
    /// 自绘光学玻璃：不向 DWM 要任何材质，窗口保持全透明，
    /// 由 <see cref="OpticalGlassSurface"/> 抓取背后的真实像素再自己合成。
    /// 好处是失焦不会退化成灰板，还能做出真正的边缘折射。
    /// </summary>
    Optical,

    /// <summary>按当前系统自动选择：Win11 → Mica，Win10 → Acrylic。</summary>
    Auto
}

/// <summary>
/// 无边框、圆角、可原生缩放的玻璃窗口基类。
///
/// 分工：
///   · 非客户区命中测试 / 边缘 8px 缩放 / 标题区拖动 / 双击最大化 → .NET 内置 <see cref="System.Windows.Shell.WindowChrome"/>，
///     没有一行 WM_* 消息处理代码。
///   · 窗口圆角与玻璃基底 → <see cref="BackdropService"/>（DWM / 组合属性）。
///   · 顶部 <see cref="CaptionHeight"/> 高度内自动成为拖动区与双击最大化区，
///     内部按钮只需设置 WindowChrome.IsHitTestVisibleInChrome="True" 即可恢复可点击。
/// </summary>
public class GlassWindow : Window
{
    // 系统不接受材质时的降级底色（老系统 / 远程桌面），明暗各一套
    private static readonly SolidColorBrush LightFallbackBrush = new(Color.FromRgb(0xF4, 0xF6, 0xFA));
    private static readonly SolidColorBrush DarkFallbackBrush = new(Color.FromRgb(0x20, 0x20, 0x24));

    private readonly WindowChrome _chrome;
    private IntPtr _hwnd = IntPtr.Zero;

    // 0 = 交给 DWM，1 = 光学模式（DWM 不裁角），-1 = 还没应用过
    private int _cornerModeApplied = -1;

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius),
        typeof(CornerRadius),
        typeof(GlassWindow),
        new PropertyMetadata(new CornerRadius(16), OnCornerRadiusChanged));

    public static readonly DependencyProperty CaptionHeightProperty = DependencyProperty.Register(
        nameof(CaptionHeight),
        typeof(double),
        typeof(GlassWindow),
        new PropertyMetadata(56d, OnCaptionHeightChanged));

    public static readonly DependencyProperty BackdropProperty = DependencyProperty.Register(
        nameof(Backdrop),
        typeof(GlassBackdrop),
        typeof(GlassWindow),
        new PropertyMetadata(GlassBackdrop.Auto, OnBackdropChanged));

    public static readonly DependencyProperty TintColorProperty = DependencyProperty.Register(
        nameof(TintColor),
        typeof(Color),
        typeof(GlassWindow),
        new PropertyMetadata(Color.FromArgb(0xCC, 0xF4, 0xF6, 0xFA), OnBackdropChanged));

    public GlassWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        AllowsTransparency = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        _chrome = new WindowChrome
        {
            CaptionHeight = CaptionHeight,
            ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(0),
            // -1 = 让 WPF 通过 DwmExtendFrameIntoClientArea(-1) 把玻璃框架铺满客户区。
            // 手工调 DwmExtendFrameIntoClientArea 试过，WPF 的合成会整块不绘制，窗口全透。
            GlassFrameThickness = new Thickness(-1),
            NonClientFrameEdges = NonClientFrameEdges.None,
            UseAeroCaptionButtons = false
        };

        WindowChrome.SetWindowChrome(this, _chrome);
    }

    /// <summary>窗口圆角半径。Win11 交给 DWM；Win10 会退化为区域裁剪。</summary>
    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>顶部拖动区高度（同时是该区域的标题栏高度）。</summary>
    public double CaptionHeight
    {
        get => (double)GetValue(CaptionHeightProperty);
        set => SetValue(CaptionHeightProperty, value);
    }

    /// <summary>玻璃基底类型。</summary>
    public GlassBackdrop Backdrop
    {
        get => (GlassBackdrop)GetValue(BackdropProperty);
        set => SetValue(BackdropProperty, value);
    }

    /// <summary>Windows 10 Acrylic 的染色色（含 Alpha）。</summary>
    public Color TintColor
    {
        get => (Color)GetValue(TintColorProperty);
        set => SetValue(TintColorProperty, value);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;

        // 关键：客户区合成背景必须是透明，否则系统材质会被 WPF 的底色整块盖住
        if (PresentationSource.FromVisual(this) is HwndSource source && source.CompositionTarget is not null)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }

        ApplyBackdropOverride();
        RefreshGlass();

        // 部分 Windows 版本要求窗口真正可见后才接受材质，首次显示完再补一次
        Loaded += OnFirstLoaded;

        SizeChanged += (_, _) => ApplyCornerMode();
        StateChanged += (_, _) => ApplyCornerMode();
        // 实测会收到大量 "1.25 -> 1.25" 的假事件：每次都会走 ApplyCornerMode(force: true)
        // → SWP_FRAMECHANGED 全量非客户区重算（还可能连带一次 DWM 表面重建）。
        // 只有 DPI 真的变了才需要重算非客户区。
        DpiChanged += (_, e) =>
        {
            if (e.OldDpi.DpiScaleX == e.NewDpi.DpiScaleX
                && e.OldDpi.DpiScaleY == e.NewDpi.DpiScaleY)
            {
                return;
            }

            ApplyCornerMode(force: true);
        };
    }

    /// <summary>
    /// 实拍对照用：GLASSLAUNCHER_BACKDROP=mica|acrylic|tint|optical|none。
    /// 必须放在 OnSourceInitialized 而不是构造函数里 —— XAML 上的 Backdrop="Acrylic"
    /// 会在 InitializeComponent 阶段覆盖构造函数写进去的值。
    /// </summary>
    private void ApplyBackdropOverride()
    {
        switch (Environment.GetEnvironmentVariable("GLASSLAUNCHER_BACKDROP")?.Trim().ToLowerInvariant())
        {
            case "mica":
                Backdrop = GlassBackdrop.Mica;
                break;
            case "acrylic":
                Backdrop = GlassBackdrop.Acrylic;
                break;
            case "tint":
            case "accent":
                Backdrop = GlassBackdrop.TintedAcrylic;
                break;
            case "optical":
                Backdrop = GlassBackdrop.Optical;
                break;
            case "none":
                Backdrop = GlassBackdrop.None;
                break;
        }
    }

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        RefreshGlass();
    }

    /// <summary>重新应用材质与圆角（主题/染色改动后调用）。</summary>
    public void RefreshGlass()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        GlassBackdrop kind = Backdrop == GlassBackdrop.Auto
            ? (BackdropService.IsWindows11 ? GlassBackdrop.Mica : GlassBackdrop.Acrylic)
            : Backdrop;

        if (kind == GlassBackdrop.Optical)
        {
            // 不向 DWM 要材质：窗口像素完全由我们那张玻璃位图决定，
            // 包括圆角（超椭圆 alpha），所以也必须关掉 DWM 的圆角裁剪，否则会被切成 8px 圆弧。
            Background = Brushes.Transparent;
            ApplyCornerMode(force: true);
            Diag.Log($"RefreshGlass kind=Optical（自绘，DWM 材质关闭）size={ActualWidth}x{ActualHeight}");
            return;
        }

        bool applied = BackdropService.Apply(_hwnd, kind, TintColor, ThemeService.IsDarkMode);

        // 系统不接受材质时（老系统 / 某些远程桌面会话）退化为不透明面板，避免全黑窗口
        Background = applied
            ? Brushes.Transparent
            : (ThemeService.IsDarkMode ? DarkFallbackBrush : LightFallbackBrush);
        Diag.Log($"RefreshGlass kind={kind} applied={applied} background={(applied ? "Transparent" : "Fallback")} size={ActualWidth}x{ActualHeight} state={WindowState}");

        ApplyCornerMode();
    }

    /// <summary>圆角由谁负责：光学模式交给我们自己的 alpha，其余交给 DWM。</summary>
    /// <remarks>
    /// 必须记住上一次的模式：Win11 的圆角属性每次应用都会顺带发一次 SWP_FRAMECHANGED，
    /// 而 SizeChanged 在拖动缩放时每帧都触发 —— 每帧重算非客户区正是“拖动一卡一卡”的元凶之一。
    /// </remarks>
    private void ApplyCornerMode(bool force = false)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        GlassBackdrop kind = Backdrop == GlassBackdrop.Auto
            ? (BackdropService.IsWindows11 ? GlassBackdrop.Mica : GlassBackdrop.Acrylic)
            : Backdrop;

        bool optical = kind == GlassBackdrop.Optical;
        if (!force && _cornerModeApplied == (optical ? 1 : 0))
        {
            return;
        }

        _cornerModeApplied = optical ? 1 : 0;

        if (optical)
        {
            BackdropService.ApplyNoRound(_hwnd);
        }
        else
        {
            BackdropService.ApplyCorner(this, CornerRadius.TopLeft);
        }
    }

    private static void OnCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GlassWindow window)
        {
            window.ApplyCornerMode();
        }
    }

    private static void OnCaptionHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GlassWindow window)
        {
            window._chrome.CaptionHeight = (double)e.NewValue;
        }
    }

    private static void OnBackdropChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        (d as GlassWindow)?.RefreshGlass();
    }
}
