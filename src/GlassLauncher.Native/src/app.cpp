#include "app.h"

#include <d2d1helper.h>
#include <dwmapi.h>
#include <shellapi.h>
#include <windowsx.h>
#include <tlhelp32.h>
#include <shlobj.h>
#include <psapi.h>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <algorithm>

#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "psapi.lib")
#pragma comment(lib, "ole32.lib")

namespace gl {

namespace {

constexpr UINT WM_TRAYICON = WM_APP + 1;
constexpr UINT_PTR TIMER_DRAG = 1;
constexpr UINT_PTR TIMER_PERF = 2;
constexpr int HOTKEY_ID = 0xC0DE;

constexpr float PadDip = 48.0f;
constexpr float TitleDip = 44.0f;
constexpr float PanelMargin = 18.0f;
constexpr float ItemW = 112.0f;
constexpr float ItemH = 122.0f;
constexpr float ItemStride = 120.0f;
constexpr float GridLeft = 32.0f;
constexpr float GridTopDip = 92.0f;

float ClientWidthDip(HWND hwnd, float dpi)
{
    RECT rc = {};
    GetClientRect(hwnd, &rc);
    return (rc.right - rc.left) / dpi;
}

float ClientHeightDip(HWND hwnd, float dpi)
{
    RECT rc = {};
    GetClientRect(hwnd, &rc);
    return (rc.bottom - rc.top) / dpi;
}

bool IEquals(const std::wstring& a, const wchar_t* b)
{
    return _wcsicmp(a.c_str(), b) == 0;
}

std::wstring Trim(const std::wstring& s)
{
    size_t b = s.find_first_not_of(L" \t");
    size_t e = s.find_last_not_of(L" \t");
    return b == std::wstring::npos ? L"" : s.substr(b, e - b + 1);
}

bool ParseHotkey(const std::wstring& text, UINT* mods, UINT* vk)
{
    *mods = 0;
    *vk = 0;

    std::vector<std::wstring> parts;
    std::wstring current;
    for (wchar_t c : text)
    {
        if (c == L'+')
        {
            parts.push_back(current);
            current.clear();
        }
        else
        {
            current.push_back(c);
        }
    }
    parts.push_back(current);

    for (size_t i = 0; i < parts.size(); i++)
    {
        std::wstring p = Trim(parts[i]);
        if (p.empty()) return false;

        if (i + 1 < parts.size())
        {
            if (IEquals(p, L"ctrl") || IEquals(p, L"control")) *mods |= MOD_CONTROL;
            else if (IEquals(p, L"alt")) *mods |= MOD_ALT;
            else if (IEquals(p, L"shift")) *mods |= MOD_SHIFT;
            else if (IEquals(p, L"win")) *mods |= MOD_WIN;
            else return false;
        }
        else
        {
            if (p.size() == 1)
            {
                wchar_t c = (wchar_t)towupper(p[0]);
                if (c >= L'A' && c <= L'Z') { *vk = (UINT)c; continue; }
                if (c >= L'0' && c <= L'9') { *vk = (UINT)c; continue; }
                return false;
            }

            if ((p[0] == L'F' || p[0] == L'f') && p.size() <= 3)
            {
                int n = _wtoi(p.c_str() + 1);
                if (n >= 1 && n <= 24) { *vk = VK_F1 + (UINT)(n - 1); continue; }
            }
            return false;
        }
    }

    if (*vk == 0) return false;
    if (*mods == 0 && !(*vk >= VK_F1 && *vk <= VK_F24)) return false;
    return true;
}

bool SystemUsesLightTheme()
{
    DWORD value = 1;
    DWORD size = sizeof(value);
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER,
            L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
            0, KEY_READ, &key) == ERROR_SUCCESS)
    {
        RegQueryValueExW(key, L"AppsUseLightTheme", nullptr, nullptr, (LPBYTE)&value, &size);
        RegCloseKey(key);
    }
    return value != 0;
}

std::wstring LogDirectory()
{
    wchar_t buf[MAX_PATH] = {};
    DWORD n = GetEnvironmentVariableW(L"GLASSLAUNCHER_LOG_DIR", buf, MAX_PATH);
    if (n > 0 && n < MAX_PATH) return buf;
    return Store::DataDirectory();
}

DWORD CountProcessThreads()
{
    DWORD pid = GetCurrentProcessId();
    DWORD count = 0;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return 0;
    THREADENTRY32 entry = {};
    entry.dwSize = sizeof(entry);
    if (Thread32First(snapshot, &entry))
    {
        do
        {
            if (entry.th32OwnerProcessID == pid) count++;
        } while (Thread32Next(snapshot, &entry));
    }
    CloseHandle(snapshot);
    return count;
}

} // namespace

int LauncherApp::Run(HINSTANCE instance)
{
    instance_ = instance;
    startTick_ = GetTickCount64();

    // 单实例：已有一个在跑就直接退出（托盘常驻应用最怕双开）
    HANDLE mutex = CreateMutexW(nullptr, TRUE, L"Local\\GlassLauncher.Native.SingleInstance");
    if (mutex && GetLastError() == ERROR_ALREADY_EXISTS)
    {
        CloseHandle(mutex);
        return 0;
    }

    std::wstring logDir = LogDirectory();
    CreateDirectoryW(logDir.c_str(), nullptr);
    std::wstring logPath = logDir + L"\\native.log";
    logFile_ = _wfopen(logPath.c_str(), L"a");
    Log("startup begin pid=%lu", GetCurrentProcessId());

    LoadData();
    if (!CreateMainWindow())
    {
        Log("CreateMainWindow failed");
        return 1;
    }

    // 1) 窗口还没显示：先抓一份干净桌面（不含自己）
    backdrop_.CapturePixels(hwnd_, dpi_, settings_.blurRadius);

    // 2) 先显示窗口，再建 D2D 渲染目标（HwndRenderTarget 在可见窗口上最稳）
    ShowWindow(hwnd_, SW_SHOW);
    UpdateWindow(hwnd_);

    wchar_t noExclusion[8] = {};
    if (GetEnvironmentVariableW(L"GL_NATIVE_NO_EXCLUSION", noExclusion, 8) == 0)
    {
        BOOL ok = SetWindowDisplayAffinity(hwnd_, 0x00000011 /* WDA_EXCLUDEFROMCAPTURE */);
        Log("exclusion set=%d", ok ? 1 : 0);
    }
    else
    {
        Log("exclusion disabled for capture");
    }

    if (!renderer_.Initialize(hwnd_, dpi_))
    {
        Log("renderer init failed");
        return 1;
    }
    rendererReady_ = true;

    LoadIcons();
    backdrop_.Upload(renderer_);

    wchar_t dumpPath[MAX_PATH] = {};
    DWORD dumpLen = GetEnvironmentVariableW(L"GL_NATIVE_DUMP_BACKDROP", dumpPath, MAX_PATH);
    if (dumpLen > 0 && dumpLen < MAX_PATH)
    {
        Log("backdrop dump %ls ok=%d", dumpPath, backdrop_.DumpBmp(dumpPath) ? 1 : 0);
    }

    InvalidateRect(hwnd_, nullptr, TRUE);

    InitTray();
    ApplyHotkey();
    ApplyTopmost();
    SetForegroundWindow(hwnd_);

    if (GetEnvironmentVariableW(L"GL_NATIVE_PERF", noExclusion, 8) > 0)
    {
        perfTimer_ = SetTimer(hwnd_, TIMER_PERF, 1000, nullptr);
    }

    Log("startup window-ready %llu ms", GetTickCount64() - startTick_);

    MSG msg = {};
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    if (hotkeyOk_) UnregisterHotKey(hwnd_, HOTKEY_ID);
    RemoveTray();
    if (rendererReady_)
    {
        for (auto* icon : icons_) if (icon) icon->Release();
        icons_.clear();
        backdrop_.Release();
        renderer_.Release();
    }
    if (logFile_)
    {
        Log("exit");
        fclose(logFile_);
        logFile_ = nullptr;
    }
    if (mutex) CloseHandle(mutex);
    return 0;
}

bool LauncherApp::CreateMainWindow()
{
    HINSTANCE inst = instance_;

    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.style = CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc = WndProcStatic;
    wc.hInstance = inst;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
    wc.hbrBackground = CreateSolidBrush(dark_ ? RGB(0x20, 0x20, 0x24) : RGB(0xF4, 0xF6, 0xFA));
    wc.lpszClassName = L"GlassLauncherNative";
    if (!RegisterClassExW(&wc))
    {
        return false;
    }

    dpi_ = (float)GetDpiForSystem() / 96.0f;
    int w = (int)(widthDip_ * dpi_ + 0.5f);
    int h = (int)(heightDip_ * dpi_ + 0.5f);

    RECT work = {};
    SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0);
    int x = work.left + (work.right - work.left - w) / 2;
    int y = work.top + (work.bottom - work.top - h) / 2;

    // 无标题栏自定义框架：WS_POPUP + WS_THICKFRAME（保留 DWM 阴影），客户区铺满
    hwnd_ = CreateWindowExW(0, wc.lpszClassName, L"应用程序",
        WS_POPUP | WS_THICKFRAME, x, y, w, h, nullptr, nullptr, inst, this);
    if (!hwnd_)
    {
        return false;
    }

    int pref = 2; // DWMWCP_ROUND
    DwmSetWindowAttribute(hwnd_, 33, &pref, sizeof(pref));
    UpdateWindowRegion();

    // WS_OVERLAPPEDWINDOW 默认有最小尺寸约束，原版是 680×460 DIP
    return true;
}

void LauncherApp::LoadData()
{
    std::wstring error;
    Store::LoadEntries(entries_, &error);
    settings_ = Store::LoadSettings();
    if (!error.empty())
    {
        LogW(error.c_str());
    }
    Log("loaded entries=%zu dataDir=%ls", entries_.size(), Store::DataDirectory().c_str());
    ApplyTheme();
}

void LauncherApp::ApplyTheme()
{
    dark_ = settings_.theme == ThemeMode::Dark
        || (settings_.theme == ThemeMode::FollowSystem && !SystemUsesLightTheme());
    colors_ = dark_ ? ThemeColors::Dark() : ThemeColors::Light();
}

void LauncherApp::ApplyTopmost()
{
    if (!hwnd_) return;
    SetWindowPos(hwnd_, settings_.topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}

void LauncherApp::LoadIcons()
{
    icons_.assign(entries_.size(), nullptr);
    int loaded = 0;
    for (size_t i = 0; i < entries_.size(); i++)
    {
        const AppEntry& e = entries_[i];
        ID2D1Bitmap* bitmap = nullptr;
        if (!e.iconCachePath.empty())
        {
            bitmap = renderer_.LoadPng(e.iconCachePath);
        }
        if (!bitmap && !e.targetPath.empty())
        {
            bitmap = renderer_.LoadIconFromPath(e.targetPath);
        }
        icons_[i] = bitmap;
        if (bitmap) loaded++;
    }
    Log("icons loaded=%d/%zu", loaded, entries_.size());
}

// ── 绘制 ──────────────────────────────────────────────────────

void LauncherApp::Paint()
{
    if (!rendererReady_) return;
    ID2D1HwndRenderTarget* rt = renderer_.Target();
    if (!rt) return;

    widthDip_ = ClientWidthDip(hwnd_, dpi_);
    heightDip_ = ClientHeightDip(hwnd_, dpi_);

    rt->BeginDraw();
    // HwndRenderTarget 的默认 DPI = 桌面 DPI（本机 120），坐标已经按 DIP 自动放大；
    // 这里必须保持 Identity，不能再乘一次 dpi_（否则整窗布局被放大 1.25²）。
    rt->SetTransform(D2D1::Matrix3x2F::Identity());

    if (GetEnvironmentVariableW(L"GL_NATIVE_DEBUG_RED", nullptr, 0) > 0)
    {
        renderer_.FillRect(D2D1::RectF(0, 0, widthDip_, heightDip_), D2D1::ColorF(1, 0, 0));
        rt->EndDraw();
        return;
    }

    DrawBackdrop();

    renderer_.FillRect(D2D1::RectF(0, 0, widthDip_, heightDip_), colors_.wash);

    D2D1_RECT_F panel = D2D1::RectF(PanelMargin, 0.0f, widthDip_ - PanelMargin, heightDip_);
    renderer_.FillRoundRect(panel, 16.0f, colors_.panelSurface);

    DrawSearchBar();
    DrawPill();
    DrawItems();
    DrawEmptyHint();

    renderer_.StrokeRoundRect(panel, 16.0f, colors_.panelEdge, 1.0f);
    renderer_.StrokeRoundRect(D2D1::RectF(0.5f, 0.5f, widthDip_ - 0.5f, heightDip_ - 0.5f),
        8.0f, colors_.windowEdge, 1.0f);
    renderer_.FillRect(D2D1::RectF(22.0f, 0.5f, widthDip_ - 22.0f, 1.5f), colors_.specular);

    DrawStatusBar();

    HRESULT hr = rt->EndDraw();
    static bool loggedFirstPaint = false;
    if (!loggedFirstPaint)
    {
        loggedFirstPaint = true;
        RECT wr = {};
        RECT cr = {};
        GetWindowRect(hwnd_, &wr);
        GetClientRect(hwnd_, &cr);
        RECT item0 = ItemRect(0);
        Log("first paint dip=%.0fx%.0f win=%ld,%ld,%ld,%ld client=%ldx%ld dpi=%.3f cols=%d item0=%ld,%ld endDraw=0x%08lX backdrop=%d",
            widthDip_, heightDip_, wr.left, wr.top, wr.right, wr.bottom,
            cr.right - cr.left, cr.bottom - cr.top, dpi_, Columns(),
            item0.left, item0.top, (unsigned long)hr, backdrop_.Valid() ? 1 : 0);
    }
    if (hr == D2DERR_RECREATE_TARGET)
    {
        Log("D2D device lost, recreating");
        // 设备丢失：位图/目标全部作废，下一帧前重建
        for (auto* icon : icons_) if (icon) icon->Release();
        icons_.clear();
        backdrop_.Release();
        renderer_.Release();
        rendererReady_ = renderer_.Initialize(hwnd_, dpi_);
        if (rendererReady_)
        {
            LoadIcons();
            RebuildBackdrop();
        }
        InvalidateRect(hwnd_, nullptr, FALSE);
    }
}

void LauncherApp::DrawBackdrop()
{
    ID2D1HwndRenderTarget* rt = renderer_.Target();
    if (!rt) return;

    if (backdrop_.Valid())
    {
        D2D1_RECT_F dest = D2D1::RectF(-PadDip, -PadDip, widthDip_ + PadDip, heightDip_ + PadDip);
        rt->DrawBitmap(backdrop_.Bitmap(), dest, 1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
    }
    else
    {
        renderer_.FillRect(D2D1::RectF(0, 0, widthDip_, heightDip_),
            dark_ ? D2D1::ColorF(0.13f, 0.14f, 0.17f) : D2D1::ColorF(0.91f, 0.93f, 0.97f));
    }
}

void LauncherApp::DrawChrome() {}

void LauncherApp::DrawSearchBar()
{
    float cx = widthDip_ * 0.5f;
    D2D1_RECT_F pill = D2D1::RectF(cx - 130.0f, 7.0f, cx + 130.0f, 37.0f);
    renderer_.FillRoundRect(pill, 15.0f, colors_.searchSurface);
    renderer_.StrokeRoundRect(pill, 15.0f, colors_.searchBorder, 1.0f);

    renderer_.DrawText(L"\uE721", D2D1::RectF(cx - 118.0f, 7.0f, cx - 98.0f, 37.0f),
        renderer_.GlyphFormat(), colors_.searchGlyph);
    renderer_.DrawText(L"搜索", D2D1::RectF(cx - 94.0f, 7.0f, cx + 118.0f, 37.0f),
        renderer_.SearchFormat(), colors_.searchGlyph);
}

void LauncherApp::DrawPill()
{
    renderer_.FillRoundRect(D2D1::RectF(20.0f, 44.0f, 72.0f, 66.0f), 9.5f,
        D2D1::ColorF(1.0f, 1.0f, 1.0f, 0.4f));
    renderer_.DrawTextCentered(L"全部", D2D1::RectF(20.0f, 44.0f, 72.0f, 66.0f),
        renderer_.NameFormat(), colors_.textPrimary);
}

RECT LauncherApp::ItemRect(int index) const
{
    int cols = std::max(1, Columns());
    int col = index % cols;
    int row = index / cols;
    float x = GridLeft + (float)col * ItemStride;
    float y = GridTopDip - scroll_ + (float)row * ItemH;
    RECT r = {};
    r.left = (LONG)(x * dpi_);
    r.top = (LONG)(y * dpi_);
    r.right = (LONG)((x + ItemW) * dpi_);
    r.bottom = (LONG)((y + ItemH) * dpi_);
    return r;
}

int LauncherApp::Columns() const
{
    float available = widthDip_ - PanelMargin * 2 - 14.0f - 6.0f;
    return std::max(1, (int)(available / ItemStride));
}

float LauncherApp::ScrollMax() const
{
    int cols = std::max(1, Columns());
    int rows = (int)((entries_.size() + cols - 1) / cols);
    float content = rows * ItemH;
    float viewport = heightDip_ - GridTopDip - 16.0f;
    return std::max(0.0f, content - viewport);
}

int LauncherApp::HitTestItems(POINT clientDip) const
{
    if ((float)clientDip.y < GridTopDip) return -1;
    if ((float)clientDip.x < GridLeft - 8.0f) return -1;

    int cols = std::max(1, Columns());
    int row = (int)(((float)clientDip.y - GridTopDip + scroll_) / ItemH);
    int col = (int)(((float)clientDip.x - GridLeft) / ItemStride);
    if (col < 0 || col >= cols || row < 0) return -1;
    int index = row * cols + col;
    return index < (int)entries_.size() ? index : -1;
}

void LauncherApp::DrawItems()
{
    ID2D1HwndRenderTarget* rt = renderer_.Target();
    if (!rt) return;

    rt->PushAxisAlignedClip(D2D1::RectF(PanelMargin + 1.0f, GridTopDip,
        widthDip_ - PanelMargin - 1.0f, heightDip_ - 8.0f), D2D1_ANTIALIAS_MODE_ALIASED);

    for (size_t i = 0; i < entries_.size(); i++)
    {
        RECT r = ItemRect((int)i);
        float x = (float)r.left / dpi_;
        float y = (float)r.top / dpi_;
        if (y > heightDip_ + 8.0f) continue;
        if (y + ItemH < GridTopDip - 8.0f) continue;

        bool hover = ((int)i == hover_);
        D2D1_RECT_F tile = D2D1::RectF(x + 22.0f, y, x + 90.0f, y + 68.0f);
        renderer_.FillRoundRect(tile, 17.0f, hover ? colors_.hoverPlate : colors_.iconPlate);
        renderer_.StrokeRoundRect(tile, 17.0f, hover ? colors_.hoverBorder : colors_.iconPlateBorder, 1.0f);

        D2D1_RECT_F iconRect = D2D1::RectF(tile.left + 6.0f, tile.top + 6.0f, tile.left + 62.0f, tile.top + 62.0f);
        if (icons_[i])
        {
            rt->DrawBitmap(icons_[i], iconRect, 1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
        }
        else
        {
            std::wstring initial = entries_[i].displayName.empty()
                ? L"?" : entries_[i].displayName.substr(0, 1);
            renderer_.DrawTextCentered(initial, D2D1::RectF(tile.left, tile.top, tile.right, tile.bottom),
                renderer_.InitialFormat(), colors_.initialText);
        }

        renderer_.DrawText(entries_[i].displayName,
            D2D1::RectF(x + 2.0f, y + 76.0f, x + ItemW - 2.0f, y + 98.0f),
            renderer_.NameFormat(), colors_.textPrimary, DWRITE_TEXT_ALIGNMENT_CENTER, true);
    }

    rt->PopAxisAlignedClip();
}

void LauncherApp::DrawEmptyHint()
{
    if (!entries_.empty()) return;
    renderer_.DrawTextCentered(L"把程序或快捷方式拖进来",
        D2D1::RectF(0, heightDip_ * 0.5f - 24, widthDip_, heightDip_ * 0.5f),
        renderer_.SearchFormat(), colors_.textPrimary);
    renderer_.DrawTextCentered(L"支持 .lnk / .url / .exe / 文件夹",
        D2D1::RectF(0, heightDip_ * 0.5f, widthDip_, heightDip_ * 0.5f + 28),
        renderer_.SmallFormat(), colors_.textSecondary);
}

void LauncherApp::DrawStatusBar()
{
    if (!showStatus_) return;
    D2D1_RECT_F rect = D2D1::RectF(PanelMargin + 12, heightDip_ - 48, widthDip_ - PanelMargin - 12, heightDip_ - 16);
    renderer_.FillRoundRect(rect, 9.0f, dark_ ? D2D1::ColorF(0.16f, 0.17f, 0.2f, 0.95f)
                                            : D2D1::ColorF(1.0f, 1.0f, 1.0f, 0.97f));
    renderer_.DrawText(statusText_, D2D1::RectF(rect.left + 10, rect.top, rect.right - 10, rect.bottom),
        renderer_.SmallFormat(), dark_ ? colors_.warning : D2D1::ColorF(0.7f, 0.15f, 0.12f));
}

void LauncherApp::RebuildBackdrop()
{
    if (!rendererReady_ || !hwnd_) return;
    ULONGLONG t0 = GetTickCount64();
    if (backdrop_.Capture(hwnd_, dpi_, settings_.blurRadius, renderer_))
    {
        Log("backdrop %dx%d in %llu ms", backdrop_.WidthPx(), backdrop_.HeightPx(), GetTickCount64() - t0);
    }
    else
    {
        Log("backdrop capture failed");
    }
}

void LauncherApp::UpdateWindowRegion()
{
    if (!hwnd_) return;
    RECT rc = {};
    GetClientRect(hwnd_, &rc);
    if (rc.right <= 0 || rc.bottom <= 0) return;
    int radius = (int)(16.0f * dpi_);
    HRGN region = CreateRoundRectRgn(0, 0, rc.right + 1, rc.bottom + 1, radius * 2, radius * 2);
    if (region)
    {
        int ok = SetWindowRgn(hwnd_, region, TRUE);
        static bool logged = false;
        if (!logged)
        {
            logged = true;
            Log("window region %ldx%ld radius=%d set=%d err=%lu", rc.right, rc.bottom, radius, ok, ok ? 0UL : GetLastError());
        }
    }
}

void LauncherApp::ScheduleRepaint()
{
    InvalidateRect(hwnd_, nullptr, FALSE);
}

// ── 交互 ──────────────────────────────────────────────────────

void LauncherApp::OnMouseMove(POINT clientDip)
{
    int hit = HitTestItems(clientDip);
    if (hit != hover_)
    {
        hover_ = hit;
        ScheduleRepaint();
    }
}

void LauncherApp::OnLeftDown(POINT clientDip)
{
    float x = (float)clientDip.x;
    float y = (float)clientDip.y;

    // 搜索胶囊位置：M1 只占位，不进入拖动
    float cx = widthDip_ * 0.5f;
    if (y < TitleDip && x >= cx - 130.0f && x <= cx + 130.0f)
    {
        return;
    }

    if (y < TitleDip)
    {
        StartWindowDrag();
        return;
    }

    press_ = HitTestItems(clientDip);
    if (press_ >= 0)
    {
        SetCapture(hwnd_);
    }
}

void LauncherApp::OnLeftUp(POINT clientDip)
{
    ReleaseCapture();

    int hit = HitTestItems(clientDip);
    if (press_ >= 0 && hit == press_)
    {
        LaunchEntry(press_);
    }
    press_ = -1;
    ScheduleRepaint();
}

void LauncherApp::OnMouseWheel(int delta)
{
    float maxScroll = ScrollMax();
    scroll_ -= (float)delta / 120.0f * 60.0f;
    if (scroll_ < 0) scroll_ = 0;
    if (scroll_ > maxScroll) scroll_ = maxScroll;
    ScheduleRepaint();
}

void LauncherApp::StartWindowDrag()
{
    if (windowDrag_) return;
    windowDrag_ = true;
    GetCursorPos(&dragCursorStart_);
    GetWindowRect(hwnd_, &dragWindowStart_);
    dragTimer_ = SetTimer(hwnd_, TIMER_DRAG, 8, nullptr);
    SetCapture(hwnd_);
}

void LauncherApp::TickWindowDrag()
{
    if (!windowDrag_) return;
    POINT p = {};
    GetCursorPos(&p);
    int dx = p.x - dragCursorStart_.x;
    int dy = p.y - dragCursorStart_.y;
    SetWindowPos(hwnd_, nullptr, dragWindowStart_.left + dx, dragWindowStart_.top + dy, 0, 0,
        SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
    {
        StopWindowDrag();
    }
}

void LauncherApp::StopWindowDrag()
{
    if (!windowDrag_) return;
    windowDrag_ = false;
    if (dragTimer_) { KillTimer(hwnd_, TIMER_DRAG); dragTimer_ = 0; }
    ReleaseCapture();
    Log("window drag end rect=(%ld,%ld)", dragWindowStart_.left, dragWindowStart_.top);
    RebuildBackdrop();
    ScheduleRepaint();
}

void LauncherApp::LaunchEntry(int index)
{
    if (index < 0 || index >= (int)entries_.size()) return;
    const AppEntry& e = entries_[index];
    if (e.targetPath.empty()) return;

    HINSTANCE result = ShellExecuteW(hwnd_, L"open", e.targetPath.c_str(),
        e.arguments.empty() ? nullptr : e.arguments.c_str(),
        e.workingDirectory.empty() ? nullptr : e.workingDirectory.c_str(),
        SW_SHOWNORMAL);

    if ((INT_PTR)result <= 32)
    {
        statusText_ = L"启动失败：" + e.displayName;
        showStatus_ = true;
        LogW(statusText_.c_str());
        SetTimer(hwnd_, 100, 5000, nullptr);
        ScheduleRepaint();
    }
    else
    {
        Log("launch ok index=%d", index);
        HideLauncher();
    }
}

// ── 托盘/显示 ────────────────────────────────────────────────

void LauncherApp::ToggleVisible()
{
    if (!IsWindowVisible(hwnd_) || IsIconic(hwnd_))
    {
        ShowLauncher();
    }
    else
    {
        HideLauncher();
    }
}

void LauncherApp::ShowLauncher()
{
    // 先抓背景再显示：窗口不可见时抓到的是干净的桌面
    RebuildBackdrop();
    ShowWindow(hwnd_, SW_SHOW);
    SetForegroundWindow(hwnd_);
    ScheduleRepaint();
    Log("show");
}

void LauncherApp::HideLauncher()
{
    if (windowDrag_) StopWindowDrag();
    ShowWindow(hwnd_, SW_HIDE);
    Log("hide");
}

void LauncherApp::InitTray()
{
    tray_.cbSize = sizeof(tray_);
    tray_.hWnd = hwnd_;
    tray_.uID = 1;
    tray_.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
    tray_.uCallbackMessage = WM_TRAYICON;
    tray_.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
    wcscpy_s(tray_.szTip, L"Glass Launcher — 左键 打开/隐藏，右键 菜单");
    trayAdded_ = Shell_NotifyIconW(NIM_ADD, &tray_) != FALSE;
    Log("tray added=%d", trayAdded_ ? 1 : 0);
}

void LauncherApp::RemoveTray()
{
    if (trayAdded_)
    {
        Shell_NotifyIconW(NIM_DELETE, &tray_);
        trayAdded_ = false;
    }
}

void LauncherApp::ShowTrayMenu()
{
    HMENU menu = CreatePopupMenu();
    AppendMenuW(menu, MF_STRING, 1, IsWindowVisible(hwnd_) ? L"隐藏" : L"打开");
    AppendMenuW(menu, MF_STRING | MF_GRAYED, 2, L"设置…");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, 3, L"退出");

    POINT pt = {};
    GetCursorPos(&pt);
    SetForegroundWindow(hwnd_);
    int cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.x, pt.y, 0, hwnd_, nullptr);
    DestroyMenu(menu);
    PostMessage(hwnd_, WM_NULL, 0, 0);

    if (cmd == 1) ToggleVisible();
    else if (cmd == 3)
    {
        exiting_ = true;
        DestroyWindow(hwnd_);
    }
}

void LauncherApp::ApplyHotkey()
{
    UINT mods = 0, vk = 0;
    if (!ParseHotkey(settings_.hotkey, &mods, &vk))
    {
        Log("hotkey parse failed: %ls", settings_.hotkey.c_str());
        return;
    }

    if (hotkeyOk_)
    {
        UnregisterHotKey(hwnd_, HOTKEY_ID);
        hotkeyOk_ = false;
    }

    hotkeyMods_ = mods | MOD_NOREPEAT;
    hotkeyVk_ = vk;
    hotkeyOk_ = RegisterHotKey(hwnd_, HOTKEY_ID, hotkeyMods_, hotkeyVk_) != FALSE;
    Log("hotkey registered=%d mods=0x%X vk=0x%X", hotkeyOk_ ? 1 : 0, hotkeyMods_, hotkeyVk_);
}

// ── 窗口过程 ──────────────────────────────────────────────────

LRESULT CALLBACK LauncherApp::WndProcStatic(HWND hwnd, UINT msg, WPARAM w, LPARAM l)
{
    LauncherApp* self = nullptr;
    if (msg == WM_NCCREATE)
    {
        auto* cs = reinterpret_cast<CREATESTRUCTW*>(l);
        self = reinterpret_cast<LauncherApp*>(cs->lpCreateParams);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
    }
    else
    {
        self = reinterpret_cast<LauncherApp*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    }

    if (self)
    {
        return self->WndProc(hwnd, msg, w, l);
    }
    return DefWindowProcW(hwnd, msg, w, l);
}

LRESULT LauncherApp::WndProc(HWND hwnd, UINT msg, WPARAM w, LPARAM l)
{
    hwnd_ = hwnd;

    switch (msg)
    {
        case WM_NCCALCSIZE:
            // 客户区铺满整个窗口（自绘标题栏）
            if (w)
            {
                auto* params = reinterpret_cast<NCCALCSIZE_PARAMS*>(l);
                RECT wr = {};
                GetWindowRect(hwnd, &wr);
                params->rgrc[0] = wr;
                return 0;
            }
            break;

        case WM_NCHITTEST:
        {
            POINT p = {GET_X_LPARAM(l), GET_Y_LPARAM(l)};
            POINT c = p;
            ScreenToClient(hwnd, &c);
            // 只有标题区可以拖；其余全部交给客户区自己处理（M1 不做边缘缩放）
            if (c.y >= 0 && (float)c.y / dpi_ < TitleDip)
            {
                return HTCAPTION;
            }
            return HTCLIENT;
        }

        case WM_NCLBUTTONDOWN:
            if (w == HTCAPTION)
            {
                StartWindowDrag();
                return 0;
            }
            break;

        case WM_PAINT:
        {
            PAINTSTRUCT ps = {};
            BeginPaint(hwnd, &ps);
            if (GetEnvironmentVariableW(L"GL_NATIVE_DEBUG_GDI", nullptr, 0) > 0)
            {
                RECT rc = {};
                GetClientRect(hwnd, &rc);
                HBRUSH brush = CreateSolidBrush(RGB(0, 200, 0));
                FillRect(ps.hdc, &rc, brush);
                DeleteObject(brush);
            }
            else
            {
                Paint();
            }
            EndPaint(hwnd, &ps);
            return 0;
        }

        case WM_ERASEBKGND:
            // 渲染器就绪后画面由 D2D 全权覆盖；就绪前让类背景刷顶一下，避免启动黑闪
            if (rendererReady_) return 1;
            break;

        case WM_SIZE:
            if (rendererReady_)
            {
                RECT rc = {};
                GetClientRect(hwnd, &rc);
                if (rc.right > 0 && rc.bottom > 0)
                {
                    renderer_.Resize((UINT)(rc.right - rc.left), (UINT)(rc.bottom - rc.top), dpi_);
                }
                if (scroll_ > ScrollMax()) scroll_ = ScrollMax();
                UpdateWindowRegion();
                ScheduleRepaint();
            }
            return 0;

        case WM_DPICHANGED:
        {
            dpi_ = (float)HIWORD(w) / 96.0f;
            RECT* suggested = reinterpret_cast<RECT*>(l);
            SetWindowPos(hwnd, nullptr, suggested->left, suggested->top,
                suggested->right - suggested->left, suggested->bottom - suggested->top,
                SWP_NOZORDER | SWP_NOACTIVATE);
            if (rendererReady_)
            {
                RECT rc = {};
                GetClientRect(hwnd, &rc);
                renderer_.Resize((UINT)(rc.right - rc.left), (UINT)(rc.bottom - rc.top), dpi_);
            }
            RebuildBackdrop();
            UpdateWindowRegion();
            ScheduleRepaint();
            return 0;
        }

        case WM_MOUSEMOVE:
        {
            POINT p = {GET_X_LPARAM(l), GET_Y_LPARAM(l)};
            POINT dip = {(LONG)(p.x / dpi_), (LONG)(p.y / dpi_)};
            OnMouseMove(dip);
            return 0;
        }

        case WM_LBUTTONDOWN:
        {
            POINT p = {GET_X_LPARAM(l), GET_Y_LPARAM(l)};
            POINT dip = {(LONG)(p.x / dpi_), (LONG)(p.y / dpi_)};
            OnLeftDown(dip);
            return 0;
        }

        case WM_LBUTTONUP:
        {
            POINT p = {GET_X_LPARAM(l), GET_Y_LPARAM(l)};
            POINT dip = {(LONG)(p.x / dpi_), (LONG)(p.y / dpi_)};
            OnLeftUp(dip);
            return 0;
        }

        case WM_MOUSEWHEEL:
            OnMouseWheel(GET_WHEEL_DELTA_WPARAM(w));
            return 0;

        case WM_KEYDOWN:
            if (w == VK_ESCAPE)
            {
                HideLauncher();
                return 0;
            }
            break;

        case WM_TIMER:
            if (w == TIMER_DRAG)
            {
                TickWindowDrag();
                return 0;
            }
            if (w == TIMER_PERF)
            {
                LogPerfTick();
                return 0;
            }
            if (w == 100)
            {
                KillTimer(hwnd, 100);
                showStatus_ = false;
                ScheduleRepaint();
                return 0;
            }
            break;

        case WM_HOTKEY:
            if (w == HOTKEY_ID)
            {
                ToggleVisible();
                return 0;
            }
            break;

        case WM_TRAYICON:
            if (LOWORD(l) == WM_LBUTTONUP || LOWORD(l) == WM_LBUTTONDBLCLK)
            {
                ToggleVisible();
            }
            else if (LOWORD(l) == WM_RBUTTONUP || LOWORD(l) == WM_CONTEXTMENU)
            {
                ShowTrayMenu();
            }
            return 0;

        case WM_CLOSE:
            // 关闭 = 隐藏到托盘；只有托盘菜单"退出"才真退
            if (exiting_) break;
            HideLauncher();
            return 0;

        case WM_DESTROY:
            RemoveTray();
            PostQuitMessage(0);
            return 0;
    }

    return DefWindowProcW(hwnd, msg, w, l);
}

// ── 日志/诊断 ────────────────────────────────────────────────

void LauncherApp::Log(const char* fmt, ...)
{
    if (!logFile_) return;
    SYSTEMTIME st = {};
    GetLocalTime(&st);
    fprintf(logFile_, "%02u:%02u:%02u.%03u  ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    va_list args;
    va_start(args, fmt);
    vfprintf(logFile_, fmt, args);
    va_end(args);
    fputc('\n', logFile_);
    fflush(logFile_);
}

void LauncherApp::LogW(const wchar_t* text)
{
    if (!logFile_ || !text) return;
    Log("%ls", text);
}

void LauncherApp::LogPerfTick()
{
    PROCESS_MEMORY_COUNTERS_EX pmc = {};
    pmc.cb = sizeof(pmc);
    GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&pmc), sizeof(pmc));

    DWORD handles = 0;
    GetProcessHandleCount(GetCurrentProcess(), &handles);
    DWORD threads = CountProcessThreads();

    Log("PERF t=%.1fs ws=%.1fMB private=%.1fMB handles=%lu threads=%lu gdi=%lu user=%lu",
        (GetTickCount64() - startTick_) / 1000.0,
        pmc.WorkingSetSize / 1048576.0,
        pmc.PagefileUsage / 1048576.0,
        handles,
        threads,
        GetGuiResources(GetCurrentProcess(), 0),
        GetGuiResources(GetCurrentProcess(), 1));
}

} // namespace gl
