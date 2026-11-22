using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace GlassLauncher.Chrome;

/// <summary>
/// 玻璃基底（Mica / Acrylic）与窗口圆角的落地实现。
///
/// 平台差异（重要）：
///   · Windows 11 22000+  → Mica 可用（DWM），圆角由 DWM 负责。
///   · Windows 11 22621+  → Mica/Acrylic 走 DWMWA_SYSTEMBACKDROP_TYPE（新入口）。
///   · Windows 10 1803+   → **没有 Mica**。只能用 SetWindowCompositionAttribute 的
///                          ACCENT_ENABLE_ACRYLICBLURBEHIND 做真实高斯模糊，
///                          圆角 DWM 也不支持，用 SetWindowRgn 裁剪（边缘有轻微锯齿）。
/// </summary>
internal static class BackdropService
{
    /// <summary>Windows 11 及以上（Build 22000+）。</summary>
    internal static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Windows 11 22H2 及以上（Build 22621+），新 backdrop API 起点。</summary>
    internal static bool IsWindows11_22H2 => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>
    /// 应用玻璃基底。返回 true 表示系统确认接受，此时窗口客户区可保持完全透明。
    /// </summary>
    internal static bool Apply(IntPtr hwnd, GlassBackdrop kind, Color tint, bool darkMode)
    {
        if (hwnd == IntPtr.Zero || kind == GlassBackdrop.None)
        {
            return false;
        }

        // Mica/Acrylic 的明暗由系统的 immersive dark mode 决定。
        //
        // 基底深浅实测对比过两轮（详见工作记忆）：
        //   深色基底：蓝壁纸保真好，但白内容被压到 ~136，白纱要开到 70% 才够亮，
        //             结果背景差异被压扁（白/蓝背景只差 5 个灰阶），且失焦冻结反而更亮；
        //   浅色基底：白内容原生 ~250（亮玻璃的关键），蓝壁纸 → (117,137,184)，
        //             配 45% 白纱 → (177,189,213) 粉蓝，亮度色相都压着 mac 的观感。
        // 所以跟随主题：浅色主题浅色基底，暗色主题暗色基底。
        int darkModeHr = ApplyDarkMode(hwnd, darkMode);

        bool result;
        if (IsWindows11)
        {
            // 先把玻璃框架铺满客户区，DWM 才有地方合成材质。
            // 之所以不用 WindowChrome.GlassFrameThickness=-1：WPF 会顺带调 SetWindowRgn，
            // 而窗口一旦有区域，DWM 的圆角（DWMWCP_ROUND）就会被忽略。
            int extendHr = ExtendGlassIntoClientArea(hwnd);
            Diag.Log($"ExtendFrame hr=0x{extendHr:X8}");

            if (kind == GlassBackdrop.TintedAcrylic)
            {
                // 关键：Win11 的 DWMSBT_TRANSIENTWINDOW（系统 Acrylic）用的是系统自己的染色，
                // 完全不读我们的 tint，实测结果是一块几乎不透明的乳白板 —— 这就是“不够通透”的根因。
                // ACCENT_ENABLE_ACRYLICBLURBEHIND 在 Win11 上依然有效，且认 GradientColor 里的 alpha，
                // 所以 Acrylic 一律走带 tint 的旧入口；只有它失败时才退回 DWM 那一套。
                result = ApplyAcrylic10(hwnd, tint);
                Diag.Log($"Acrylic via accent-policy tint=#{tint.A:X2}{tint.R:X2}{tint.G:X2}{tint.B:X2} applied={result}");

                if (!result)
                {
                    result = ApplyMicaOrAcrylic11(hwnd, acrylic: true);
                }
            }
            else
            {
                result = ApplyMicaOrAcrylic11(hwnd, acrylic: kind == GlassBackdrop.Acrylic);
            }
        }
        else
        {
            result = ApplyAcrylic10(hwnd, tint);
        }

        Diag.Log($"Apply kind={kind} build={Environment.OSVersion.Version.Build} themeDark={darkMode} hr=0x{darkModeHr:X8} applied={result}");
        return result;
    }

    /// <summary>把玻璃框架铺满整个客户区。</summary>
    internal static int ExtendGlassIntoClientArea(IntPtr hwnd)
    {
        var margins = new NativeMethods.Margins { Left = -1, Top = -1, Right = -1, Bottom = -1 };
        return NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    /// <summary>圆角：Win11 交给 DWM；Win10 用窗口区域裁剪。</summary>
    internal static void ApplyCorner(Window window, double radius)
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (IsWindows11)
        {
            int preference = radius <= 2 ? NativeMethods.DWMWCP_DONOTROUND : NativeMethods.DWMWCP_ROUND;
            int hr = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));

            // 改完角落偏好必须让 DWM 重算一次非客户区，否则多数情况下圆角不生效
            NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);

            Diag.Log($"Corner attr33 pref={preference} hr=0x{hr:X8} framedchanged");
            return;
        }

        // 最大化时必须清掉裁剪区域，否则会留出圆角缺口
        if (window.WindowState == WindowState.Maximized)
        {
            _ = NativeMethods.SetWindowRgn(hwnd, IntPtr.Zero, true);
            return;
        }

        double scaleX = 1, scaleY = 1;
        if (PresentationSource.FromVisual(window) is not null)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(window);
            scaleX = dpi.DpiScaleX;
            scaleY = dpi.DpiScaleY;
        }

        int width = (int)Math.Round(window.ActualWidth * scaleX);
        int height = (int)Math.Round(window.ActualHeight * scaleY);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        int r = (int)Math.Round(radius * scaleX);
        if (r <= 0)
        {
            _ = NativeMethods.SetWindowRgn(hwnd, IntPtr.Zero, true);
            return;
        }

        IntPtr region = NativeMethods.CreateRoundRectRgn(0, 0, width + 1, height + 1, r * 2, r * 2);
        if (region == IntPtr.Zero)
        {
            return;
        }

        // SetWindowRgn 成功后系统接管 region 所有权，不可再 DeleteObject
        if (NativeMethods.SetWindowRgn(hwnd, region, true) == 0)
        {
            _ = NativeMethods.DeleteObject(region);
        }
    }

    /// <summary>
    /// 光学玻璃模式：圆角由我们自己的 alpha 负责，必须让 DWM 别裁。
    /// 否则 DWM 会按它自己的 ~8px 圆弧切一刀，把我们 20px 的超椭圆角切平。
    /// </summary>
    internal static void ApplyNoRound(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindows11)
        {
            return;
        }

        int preference = NativeMethods.DWMWCP_DONOTROUND;
        int hr = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));

        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);

        Diag.Log($"Corner attr33 pref=DONOTROUND hr=0x{hr:X8}");
    }

    private static int ApplyDarkMode(IntPtr hwnd, bool darkMode)
    {
        int enabled = darkMode ? 1 : 0;
        return NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
    }

    private static bool ApplyMicaOrAcrylic11(IntPtr hwnd, bool acrylic)
    {
        int backdrop = acrylic ? NativeMethods.DWMSBT_TRANSIENTWINDOW : NativeMethods.DWMSBT_MAINWINDOW;

        if (IsWindows11_22H2)
        {
            int hr38 = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            Diag.Log($"Backdrop attr38 value={backdrop} hr=0x{hr38:X8}");

            if (hr38 == 0)
            {
                return true;
            }
        }

        // 21H2 只有 Mica 的旧入口，且没有 Acrylic
        int on = 1;
        int hr = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_MICA_EFFECT, ref on, sizeof(int));
        Diag.Log($"Backdrop attr1029 hr=0x{hr:X8}");
        return hr == 0;
    }

    /// <summary>Windows 10：真实高斯模糊 + 染色（ABGR）。</summary>
    private static bool ApplyAcrylic10(IntPtr hwnd, Color tint)
    {
        var accent = new NativeMethods.AccentPolicy
        {
            AccentState = NativeMethods.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = 2, // 绘制窗口四边
            GradientColor = ToAbgr(tint),
            AnimationId = 0
        };

        int size = Marshal.SizeOf<NativeMethods.AccentPolicy>();
        IntPtr buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(accent, buffer, false);

            var data = new NativeMethods.WindowCompositionAttributeData
            {
                Attribute = NativeMethods.WCA_ACCENT_POLICY,
                Data = buffer,
                SizeOfData = size
            };

            return NativeMethods.SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int ToAbgr(Color c) => (c.A << 24) | (c.B << 16) | (c.G << 8) | c.R;
}
