#include "app.h"
#include "settings.h"
#include "prompt.h"

#include <d2d1helper.h>
#include <dwmapi.h>
#include <shellapi.h>
#include <windowsx.h>
#include <tlhelp32.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <psapi.h>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <cmath>
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

std::wstring FileStem(const std::wstring& path)
{
    std::wstring name = path;
    size_t slash = name.find_last_of(L"\\/");
    if (slash != std::wstring::npos) name = name.substr(slash + 1);
    size_t dot = name.find_last_of(L'.');
    if (dot != std::wstring::npos && dot > 0) name = name.substr(0, dot);
    return name;
}

bool EndsWithNoCase(const std::wstring& text, const wchar_t* suffix)
{
    size_t n = wcslen(suffix);
    if (text.size() < n) return false;
    return _wcsicmp(text.c_str() + (text.size() - n), suffix) == 0;
}

// .lnk → 真实目标（IShellLink）；.url → InternetShortcut 里的 URL；其余原样返回
bool ResolveDroppedPath(const std::wstring& path, std::wstring& target)
{
    target.clear();
    if (EndsWithNoCase(path, L".lnk"))
    {
        IShellLinkW* link = nullptr;
        if (FAILED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER,
                IID_PPV_ARGS(&link))))
        {
            return false;
        }

        IPersistFile* file = nullptr;
        bool ok = false;
        if (SUCCEEDED(link->QueryInterface(IID_PPV_ARGS(&file))))
        {
            if (SUCCEEDED(file->Load(path.c_str(), STGM_READ)))
            {
                wchar_t buf[MAX_PATH] = {};
                if (SUCCEEDED(link->GetPath(buf, MAX_PATH, nullptr, 0)) && buf[0])
                {
                    target = buf;
                    ok = true;
                }
            }
            file->Release();
        }
        link->Release();
        return ok;
    }

    if (EndsWithNoCase(path, L".url"))
    {
        wchar_t buf[2048] = {};
        GetPrivateProfileStringW(L"InternetShortcut", L"URL", L"", buf, 2048, path.c_str());
        if (buf[0])
        {
            target = buf;
            return true;
        }
        return false;
    }

    if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES) return false;
    target = path;
    return true;
}

} // namespace

// ── 搜索 / 过滤 ──────────────────────────────────────────────

void LauncherApp::RebuildVisible()
{
    visible_.clear();
    if (filter_.empty())
    {
        for (int i = 0; i < (int)entries_.size(); i++) visible_.push_back(i);
        return;
    }

    std::wstring needle = filter_;
    std::transform(needle.begin(), needle.end(), needle.begin(), towlower);
    for (int i = 0; i < (int)entries_.size(); i++)
    {
        std::wstring name = entries_[i].displayName;
        std::transform(name.begin(), name.end(), name.begin(), towlower);
        if (name.find(needle) != std::wstring::npos) visible_.push_back(i);
    }
}

void LauncherApp::CreateSearchEdit()
{
    if (GetEnvironmentVariableW(L"GL_NATIVE_NO_SEARCH_EDIT", nullptr, 0) > 0) return;

    searchEdit_ = CreateWindowExW(0, L"EDIT", L"",
        WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL | ES_LEFT,
        0, 0, 10, 10, hwnd_, (HMENU)1001, instance_, nullptr);
    if (!searchEdit_)
    {
        Log("search edit create failed %lu", GetLastError());
        return;
    }

    SendMessageW(searchEdit_, EM_SETCUEBANNER, TRUE, (LPARAM)L"搜索");
    ApplySearchFont();
    searchEditProc_ = (WNDPROC)SetWindowLongPtrW(searchEdit_, GWLP_WNDPROC, (LONG_PTR)SearchEditProc);
    LayoutChildren();
}

void LauncherApp::ApplySearchFont()
{
    if (!searchEdit_) return;
    if (searchFont_) DeleteObject(searchFont_);
    searchFont_ = CreateFontW(-(int)(13.5f * dpi_), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_TT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
        DEFAULT_PITCH | FF_DONTCARE, L"Microsoft YaHei UI");
    SendMessageW(searchEdit_, WM_SETFONT, (WPARAM)searchFont_, TRUE);
}

void LauncherApp::LayoutChildren()
{
    if (!searchEdit_) return;
    float cx = widthDip_ * 0.5f;
    SetWindowPos(searchEdit_, nullptr,
        (int)((cx - 93.0f) * dpi_), (int)(9.0f * dpi_),
        (int)(185.0f * dpi_), (int)(24.0f * dpi_),
        SWP_NOZORDER | SWP_NOACTIVATE);
}

void LauncherApp::OnSearchChanged()
{
    if (!searchEdit_) return;
    wchar_t buf[256] = {};
    GetWindowTextW(searchEdit_, buf, 256);
    filter_ = buf;
    RebuildVisible();
    scroll_ = 0.0f;
    hover_ = -1;
    ScheduleRepaint();
}

LRESULT CALLBACK LauncherApp::SearchEditProc(HWND hwnd, UINT msg, WPARAM w, LPARAM l)
{
    LauncherApp* self = reinterpret_cast<LauncherApp*>(
        GetWindowLongPtrW(GetParent(hwnd), GWLP_USERDATA));
    if (!self)
    {
        return DefWindowProcW(hwnd, msg, w, l);
    }

    if (msg == WM_KEYDOWN)
    {
        if (w == VK_ESCAPE)
        {
            if (!self->filter_.empty())
            {
                SetWindowTextW(hwnd, L"");
            }
            else
            {
                self->HideLauncher();
            }
            return 0;
        }

        if (w == VK_RETURN)
        {
            if (!self->visible_.empty()) self->LaunchEntry(self->visible_.front());
            return 0;
        }
    }
    else if (msg == WM_CHAR && w == 0x1B)
    {
        return 0; // 吃掉 Esc 的提示音
    }

    return CallWindowProcW(self->searchEditProc_ ? self->searchEditProc_ : DefWindowProcW, hwnd, msg, w, l);
}

// ── 右键菜单 / 条目编辑 ──────────────────────────────────────

void LauncherApp::ShowItemMenu(int index)
{
    if (index < 0 || index >= (int)entries_.size()) return;
    const AppEntry& e = entries_[index];

    HMENU menu = CreatePopupMenu();
    AppendMenuW(menu, MF_STRING, 1, L"打开");
    AppendMenuW(menu, MF_STRING, 2, L"以管理员身份运行");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, 3, L"在资源管理器中打开");
    AppendMenuW(menu, MF_STRING, 4, L"移除");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, 5, L"重命名…");
    AppendMenuW(menu, MF_STRING, 6, L"重新指定目标…");

    POINT pt = {};
    GetCursorPos(&pt);
    SetForegroundWindow(hwnd_);
    menuOpen_ = true;
    int cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.x, pt.y, 0, hwnd_, nullptr);
    menuOpen_ = false;
    DestroyMenu(menu);
    PostMessage(hwnd_, WM_NULL, 0, 0);

    switch (cmd)
    {
        case 1: LaunchEntry(index); break;
        case 2: LaunchEntry(index, true); break;
        case 3:
        {
            std::wstring args = L"/select,\"" + e.targetPath + L"\"";
            ShellExecuteW(hwnd_, L"open", L"explorer.exe", args.c_str(), nullptr, SW_SHOWNORMAL);
            break;
        }
        case 4: RemoveEntry(index); break;

        case 5: // 重命名
        {
            std::wstring name = e.displayName;
            modalOpen_ = true;
            bool ok = PromptDialog::ShowModal(hwnd_, instance_, L"重命名", L"显示名称", name, name);
            modalOpen_ = false;
            if (ok && !name.empty())
            {
                entries_[index].displayName = name;
                SaveEntriesLogged();
                ScheduleRepaint();
                Log("renamed entry %d -> %ls", index, name.c_str());
            }
            break;
        }

        case 6: // 重新指定目标
        {
            std::wstring path = e.targetPath;
            modalOpen_ = true;
            bool ok = PromptDialog::ShowModal(hwnd_, instance_, L"重新指定目标",
                    L"目标路径（.exe / .lnk / .url / 文件夹）", path, path);
            modalOpen_ = false;
            if (ok && !path.empty())
            {
                entries_[index].targetPath = path;
                if (icons_[index]) { icons_[index]->Release(); icons_[index] = nullptr; }
                icons_[index] = renderer_.LoadIconFromPath(path);
                entries_[index].isBroken = GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES
                    && _wcsnicmp(path.c_str(), L"http", 4) != 0;
                SaveEntriesLogged();
                ScheduleRepaint();
                Log("retargeted entry %d", index);
            }
            break;
        }

        default: break;
    }
}

void LauncherApp::SaveEntriesLogged()
{
    std::wstring error;
    if (!Store::SaveEntries(entries_, &error))
    {
        LogW((L"保存失败：" + error).c_str());
    }
}

void LauncherApp::RemoveEntry(int index)
{
    if (index < 0 || index >= (int)entries_.size()) return;
    if (icons_[index]) icons_[index]->Release();
    entries_.erase(entries_.begin() + index);
    icons_.erase(icons_.begin() + index);
    RebuildVisible();
    std::wstring error;
    if (!Store::SaveEntries(entries_, &error))
    {
        LogW((L"保存失败：" + error).c_str());
    }
    Log("removed entry index=%d remaining=%zu", index, entries_.size());
    ScheduleRepaint();
}

void LauncherApp::MoveEntry(int from, int to)
{
    if (from < 0 || from >= (int)entries_.size()) return;
    if (to < 0 || to >= (int)entries_.size() || from == to) return;

    AppEntry entry = entries_[from];
    ID2D1Bitmap* icon = icons_[from];
    entries_.erase(entries_.begin() + from);
    icons_.erase(icons_.begin() + from);
    entries_.insert(entries_.begin() + to, entry);
    icons_.insert(icons_.begin() + to, icon);

    for (size_t i = 0; i < entries_.size(); i++) entries_[i].sortOrder = (int)i;
    RebuildVisible();

    std::wstring error;
    if (!Store::SaveEntries(entries_, &error))
    {
        LogW((L"保存失败：" + error).c_str());
    }
    Log("moved entry %d -> %d", from, to);
}

// ── 外部拖入 ────────────────────────────────────────────────

void LauncherApp::HandleDroppedFiles(HDROP drop)
{
    POINT pt = {};
    DragQueryPoint(drop, &pt);
    POINT dip = {(LONG)(pt.x / dpi_), (LONG)(pt.y / dpi_)};
    int insertAt = filter_.empty() ? HitTestItems(dip) : -1;
    if (insertAt < 0) insertAt = (int)entries_.size();

    UINT count = DragQueryFileW(drop, 0xFFFFFFFF, nullptr, 0);
    int added = 0;
    for (UINT i = 0; i < count; i++)
    {
        wchar_t path[MAX_PATH] = {};
        if (DragQueryFileW(drop, i, path, MAX_PATH) == 0) continue;

        std::wstring target;
        if (!ResolveDroppedPath(path, target)) continue;

        AppEntry entry;
        entry.targetPath = target;
        entry.sourcePath = path;
        entry.displayName = FileStem(path);
        if (entry.displayName.empty()) entry.displayName = FileStem(target);
        if (entry.displayName.empty()) entry.displayName = target;

        bool duplicate = false;
        for (const auto& existing : entries_)
        {
            if (_wcsicmp(existing.targetPath.c_str(), entry.targetPath.c_str()) == 0)
            {
                duplicate = true;
                break;
            }
        }
        if (duplicate) continue;

        int index = std::clamp(insertAt + added, 0, (int)entries_.size());
        entries_.insert(entries_.begin() + index, entry);
        icons_.insert(icons_.begin() + index, nullptr);
        added++;
    }

    for (size_t i = 0; i < entries_.size(); i++) entries_[i].sortOrder = (int)i;

    for (int i = 0; i < (int)entries_.size(); i++)
    {
        if (icons_[i] == nullptr)
        {
            ID2D1Bitmap* bitmap = nullptr;
            if (!entries_[i].iconCachePath.empty()) bitmap = renderer_.LoadPng(entries_[i].iconCachePath);
            if (!bitmap && !entries_[i].targetPath.empty()) bitmap = renderer_.LoadIconFromPath(entries_[i].targetPath);
            icons_[i] = bitmap;
        }
    }

    DragFinish(drop);

    if (added > 0)
    {
        RebuildVisible();
        std::wstring error;
        if (!Store::SaveEntries(entries_, &error))
        {
            LogW((L"保存失败：" + error).c_str());
        }
        Log("dropped files added=%d total=%zu", added, entries_.size());
    }
    ScheduleRepaint();
}

void LauncherApp::CheckBrokenEntries()
{
    int broken = 0;
    for (auto& e : entries_)
    {
        if (e.targetPath.empty()) continue;
        bool isUrl = e.targetPath.rfind(L"http://", 0) == 0 || e.targetPath.rfind(L"https://", 0) == 0;
        bool exists = isUrl || GetFileAttributesW(e.targetPath.c_str()) != INVALID_FILE_ATTRIBUTES;
        if (!exists && !e.isBroken)
        {
            e.isBroken = true;
            broken++;
        }
        else if (exists && e.isBroken)
        {
            e.isBroken = false;
        }
    }
    if (broken > 0) Log("broken entries=%d", broken);
}

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
    if (layered_)
    {
        SetLayeredWindowAttributes(hwnd_, 0, 0, LWA_ALPHA);
    }
    ShowWindow(hwnd_, SW_SHOWNOACTIVATE);
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
    UpdateWindowRegion();
    LayoutChildren();
    liveCapture_.Start(hwnd_);

    LoadIcons();

    wchar_t dumpPath[MAX_PATH] = {};
    DWORD dumpLen = GetEnvironmentVariableW(L"GL_NATIVE_DUMP_BACKDROP", dumpPath, MAX_PATH);
    if (dumpLen > 0 && dumpLen < MAX_PATH)
    {
        Log("backdrop dump %ls ok=%d", dumpPath, backdrop_.DumpBmp(dumpPath) ? 1 : 0);
    }

    backdrop_.Upload(renderer_);
    InvalidateRect(hwnd_, nullptr, TRUE);

    if (layered_)
    {
        StartFade(false);
    }

    InitTray();
    ApplyHotkey();
    ApplyTopmost();
    SetForegroundWindow(hwnd_);

    if (GetEnvironmentVariableW(L"GL_NATIVE_PERF", noExclusion, 8) > 0)
    {
        perfTimer_ = SetTimer(hwnd_, TIMER_PERF, 1000, nullptr);
    }

    Log("startup window-ready %llu ms", GetTickCount64() - startTick_);

    // 自动化回归入口（仅测试用，不影响正常启动）
    wchar_t autoDragSpec[128] = {};
    if (GetEnvironmentVariableW(L"GL_NATIVE_AUTODRAG", autoDragSpec, 128) > 0)
    {
        if (swscanf(autoDragSpec, L"%d,%d,%d,%d,%d",
                &autoDx_, &autoDy_, &autoDurationMs_, &autoRepeat_, &autoGapMs_) < 2)
        {
            autoDx_ = 200;
            autoDy_ = 80;
        }
        ScheduleAutoDrag(1200);
    }

    // 打开/关闭动画时长（测试可调）
    {
        wchar_t fadeBuf[32] = {};
        int fadeMs = 0;
        if (GetEnvironmentVariableW(L"GL_NATIVE_FADE_MS", fadeBuf, 32) > 0 &&
            swscanf(fadeBuf, L"%d", &fadeMs) == 1 && fadeMs > 0)
        {
            fadeDurationMs_ = (ULONGLONG)fadeMs;
        }
    }

    // 自动化测试：到期自动退出（避免测试进程留在用户桌面上）
    int exitAfterMs = 0;
    if (GetEnvironmentVariableW(L"GL_NATIVE_EXIT_AFTER_MS", autoDragSpec, 128) > 0 &&
        swscanf(autoDragSpec, L"%d", &exitAfterMs) == 1 && exitAfterMs > 0)
    {
        SetTimer(hwnd_, 4 /* TIMER_AUTO_EXIT */, (UINT)exitAfterMs, nullptr);
    }

    MSG msg = {};
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    if (hotkeyOk_) UnregisterHotKey(hwnd_, HOTKEY_ID);
    RemoveTray();
    liveCapture_.Stop();
    if (searchFont_) { DeleteObject(searchFont_); searchFont_ = nullptr; }
    if (searchBackBrush_) { DeleteObject(searchBackBrush_); searchBackBrush_ = nullptr; }
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
    wc.hIcon = LoadIconW(inst, MAKEINTRESOURCEW(1));
    if (!wc.hIcon) wc.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
    wc.hIconSm = wc.hIcon;
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

    // 打开/关闭动画：分层窗口 alpha 淡入淡出（默认开启；GL_NATIVE_NO_ANIM=1 可关）
    layered_ = GetEnvironmentVariableW(L"GL_NATIVE_NO_ANIM", nullptr, 0) == 0;
    DWORD exStyle = layered_ ? WS_EX_LAYERED : 0;

    // 无标题栏自定义框架：WS_POPUP + WS_THICKFRAME（保留 DWM 阴影），客户区铺满
    hwnd_ = CreateWindowExW(exStyle, wc.lpszClassName, L"应用程序",
        WS_POPUP | WS_THICKFRAME, x, y, w, h, nullptr, nullptr, inst, this);
    if (!hwnd_)
    {
        return false;
    }

    if (layered_)
    {
        SetLayeredWindowAttributes(hwnd_, 0, 255, LWA_ALPHA);
    }

    int pref = 2; // DWMWCP_ROUND
    DwmSetWindowAttribute(hwnd_, 33, &pref, sizeof(pref));

    // Win11 会给带边框的窗口画一圈 1px 的系统边框（浅色模式下是白线），
    // 我们的客户区铺满整窗，这圈线正好落在画面上、沿窗口轮廓连成一圈 ——
    // 用户反馈的"靠近边缘的白线"就是它。DWMWA_COLOR_NONE 关掉。
    {
        DWORD noBorder = 0xFFFFFFFE; // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(hwnd_, 34 /* DWMWA_BORDER_COLOR */, &noBorder, sizeof(noBorder));
    }

    UpdateWindowRegion();

    CreateSearchEdit();
    DragAcceptFiles(hwnd_, TRUE);
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
    CheckBrokenEntries();
    RebuildVisible();
}

void LauncherApp::ApplyTheme()
{
    dark_ = settings_.theme == ThemeMode::Dark
        || (settings_.theme == ThemeMode::FollowSystem && !SystemUsesLightTheme());
    colors_ = dark_ ? ThemeColors::Dark() : ThemeColors::Light();

    if (searchBackBrush_) DeleteObject(searchBackBrush_);
    searchBackBrush_ = CreateSolidBrush(dark_ ? RGB(0x2A, 0x2B, 0x31) : RGB(0xE6, 0xE8, 0xED));
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

    // 先用不透明底色清屏：任何"背景位图没盖到"的边缘都不会透出桌面（透明感 bug 的根因之一）
    rt->Clear(dark_ ? D2D1::ColorF(0.13f, 0.14f, 0.17f) : D2D1::ColorF(0.91f, 0.93f, 0.97f));

    if (GetEnvironmentVariableW(L"GL_NATIVE_DEBUG_RED", nullptr, 0) > 0)
    {
        renderer_.FillRect(D2D1::RectF(0, 0, widthDip_, heightDip_), D2D1::ColorF(1, 0, 0));
        rt->EndDraw();
        return;
    }

    DrawBackdrop();

    renderer_.FillRect(D2D1::RectF(0, 0, widthDip_, heightDip_), colors_.wash);

    // WindowSheen：整窗受光层（亮色刷白、暗色压暗），与 WPF 的 WindowSheenBrush 同款纵向渐变。
    // 之前暗色用 40% 纯黑整层铺，窗口比桌面明显暗一档，边缘就现出"界线两侧色差"。
    if (dark_)
    {
        D2D1_GRADIENT_STOP sheen[3] = {
            {0.0f, D2D1::ColorF(0x23 / 255.0f, 0x25 / 255.0f, 0x29 / 255.0f, 0x2E / 255.0f)},
            {0.45f, D2D1::ColorF(0x1E / 255.0f, 0x20 / 255.0f, 0x24 / 255.0f, 0x38 / 255.0f)},
            {1.0f, D2D1::ColorF(0x19 / 255.0f, 0x1B / 255.0f, 0x1E / 255.0f, 0x42 / 255.0f)},
        };
        renderer_.FillRectGradient(D2D1::RectF(0, 0, widthDip_, heightDip_), sheen, 3, false);
    }
    else
    {
        D2D1_GRADIENT_STOP sheen[4] = {
            {0.0f, D2D1::ColorF(1, 1, 1, 0x17 / 255.0f)},
            {0.45f, D2D1::ColorF(1, 1, 1, 0x0E / 255.0f)},
            {0.8f, D2D1::ColorF(1, 1, 1, 0x06 / 255.0f)},
            {1.0f, D2D1::ColorF(0, 0, 0, 0x14 / 255.0f)},
        };
        renderer_.FillRectGradient(D2D1::RectF(0, 0, widthDip_, heightDip_), sheen, 4, false);
    }

    // 面板内表层去掉：它在窗口内侧 18 DIP 处形成一圈与轮廓平行的分界线，
    // 用户看到的就是"线内侧和线外侧有色差"。整窗只保留一层均匀的玻璃色。

    DrawSearchBar();
    DrawPill();
    DrawItems();
    DrawEmptyHint();

    // 玻璃棱线用渐变而不是纯白 1px：纯白描边在亮背景上就是一条扎眼的白线，
    // mac 的观感是"左上受光明亮 → 右下压暗"，渐变才能撑出厚度。
    // 面板内表层/棱线都去掉：它们会在窗口内侧 18 DIP 处画出一圈与窗口轮廓平行的
    // 分界线，用户看到的就是"线内侧和线外侧有色差"。现在整窗只有一层均匀的玻璃色，
    // 边界交给 DWM 阴影。


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
        // 实时帧是在"抓取那一刻"的窗口位置抓的；窗口之后又动了，就按差值平移，
        // 保证背景内容始终钉在桌面对应位置上（而不是贴死在窗口上）。
        float shiftX = 0.0f;
        float shiftY = 0.0f;
        if (backdrop_.HasOrigin())
        {
            RECT wr = {};
            GetWindowRect(hwnd_, &wr);
            shiftX = (float)(backdrop_.OriginX() - wr.left) / dpi_;
            shiftY = (float)(backdrop_.OriginY() - wr.top) / dpi_;
        }

        // 平移不能超过垫边，否则位图覆盖不到窗口的另一边
        shiftX = std::clamp(shiftX, -PadDip, PadDip);
        shiftY = std::clamp(shiftY, -PadDip, PadDip);

        // 目标尺寸按【当前窗口】算（不是位图自己的尺寸）：缩放期间旧帧尺寸可能偏小，
        // 拉伸铺满也比在底部/右侧留一条透出桌面的缝要好。
        D2D1_RECT_F dest = D2D1::RectF(
            -PadDip + shiftX, -PadDip + shiftY,
            widthDip_ + PadDip + shiftX, heightDip_ + PadDip + shiftY);
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

    // 与子窗口 EDIT 的背景刷同色（EDIT 是实心窗口，胶囊必须跟它一致才看不出接缝）
    D2D1_COLOR_F bg = dark_
        ? D2D1::ColorF(0x2A / 255.0f, 0x2B / 255.0f, 0x31 / 255.0f)
        : D2D1::ColorF(0xE6 / 255.0f, 0xE8 / 255.0f, 0xED / 255.0f);
    renderer_.FillRoundRect(pill, 15.0f, bg);
    renderer_.StrokeRoundRect(pill, 15.0f, colors_.searchBorder, 1.0f);
    renderer_.DrawText(L"\uE721", D2D1::RectF(cx - 118.0f, 7.0f, cx - 98.0f, 37.0f),
        renderer_.GlyphFormat(), colors_.searchGlyph);

    // 右上角齿轮（设置入口），与 WPF 版同位置
    D2D1_POINT_2F gearCenter = D2D1::Point2F(widthDip_ - 33.0f, 22.0f);
    renderer_.StrokeRoundRect(D2D1::RectF(gearCenter.x - 13.0f, gearCenter.y - 13.0f,
        gearCenter.x + 13.0f, gearCenter.y + 13.0f), 13.0f, colors_.searchBorder, 1.2f);
    renderer_.DrawTextCentered(L"\uE713", D2D1::RectF(gearCenter.x - 13.0f, gearCenter.y - 13.0f,
        gearCenter.x + 13.0f, gearCenter.y + 13.0f), renderer_.GlyphFormat(), colors_.searchGlyph);
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
    int rows = (int)((visible_.size() + cols - 1) / cols);
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
    int slot = row * cols + col;
    if (slot < 0 || slot >= (int)visible_.size()) return -1;
    return visible_[slot];
}

void LauncherApp::DrawItems()
{
    ID2D1HwndRenderTarget* rt = renderer_.Target();
    if (!rt) return;

    // 悬停放大是绕图块中心缩放的，顶部会向上多出 ~3.4 DIP；裁剪框必须留这段余量，
    // 否则放大动画的第一帧就会被切在"静止状态的顶边"上（用户看到的顶部缺一条）。
    rt->PushAxisAlignedClip(D2D1::RectF(PanelMargin + 1.0f, GridTopDip - 6.0f,
        widthDip_ - PanelMargin - 1.0f, heightDip_ - 8.0f), D2D1_ANTIALIAS_MODE_ALIASED);

    for (size_t slot = 0; slot < visible_.size(); slot++)
    {
        int entry = visible_[slot];
        RECT r = ItemRect((int)slot);
        float x = (float)r.left / dpi_;
        float y = (float)r.top / dpi_;
        if (y > heightDip_ + 8.0f) continue;
        if (y + ItemH < GridTopDip - 8.0f) continue;

        bool hover = (entry == hover_ && !itemDrag_);
        bool source = itemDrag_ && entry == dragFrom_;
        DrawItemTile(entry, x, y, hover, source ? 0.25f : 1.0f);
    }

    rt->PopAxisAlignedClip();

    // 拖拽中的浮起副本：跟随光标画一份完整条目
    if (itemDrag_ && dragFrom_ >= 0)
    {
        float gx = (float)dragPoint_.x - ItemW * 0.5f;
        float gy = (float)dragPoint_.y - 34.0f;
        DrawItemTile(dragFrom_, gx, gy, true, 1.0f);
    }
}

void LauncherApp::DrawItemTile(int entry, float x, float y, bool hover, float alpha)
{
    ID2D1HwndRenderTarget* rt = renderer_.Target();
    if (!rt || entry < 0 || entry >= (int)entries_.size()) return;

    D2D1_COLOR_F plate = hover ? colors_.hoverPlate : colors_.iconPlate;
    plate.a *= alpha;
    D2D1_RECT_F tile = D2D1::RectF(x + 22.0f, y, x + 90.0f, y + 68.0f);

    float scale = 1.0f;
    if (entry < (int)hoverProgress_.size() && hoverProgress_[entry] > 0.0f)
    {
        float t = hoverProgress_[entry];
        t = t * t * (3.0f - 2.0f * t); // smoothstep
        scale = 1.0f + 0.10f * t;
    }

    if (scale != 1.0f)
    {
        D2D1_POINT_2F center = D2D1::Point2F((tile.left + tile.right) * 0.5f,
                                             (tile.top + tile.bottom) * 0.5f);
        tile = D2D1::RectF(center.x + (tile.left - center.x) * scale,
                           center.y + (tile.top - center.y) * scale,
                           center.x + (tile.right - center.x) * scale,
                           center.y + (tile.bottom - center.y) * scale);
    }

    renderer_.FillRoundRect(tile, 17.0f * scale, plate);
    renderer_.StrokeRoundRect(tile, 17.0f * scale, colors_.iconPlateBorder, 1.0f);

    float inset = 6.0f * scale;
    D2D1_RECT_F iconRect = D2D1::RectF(tile.left + inset, tile.top + inset,
        tile.right - inset, tile.bottom - inset);
    if (icons_[entry])
    {
        rt->DrawBitmap(icons_[entry], iconRect, alpha, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
    }
    else
    {
        std::wstring initial = entries_[entry].displayName.empty()
            ? L"?" : entries_[entry].displayName.substr(0, 1);
        D2D1_COLOR_F color = colors_.initialText;
        color.a *= alpha;
        renderer_.DrawTextCentered(initial, D2D1::RectF(tile.left, tile.top, tile.right, tile.bottom),
            renderer_.InitialFormat(), color);
    }

    D2D1_COLOR_F text = colors_.textPrimary;
    text.a *= alpha;
    renderer_.DrawText(entries_[entry].displayName,
        D2D1::RectF(x + 2.0f, y + 76.0f, x + ItemW - 2.0f, y + 98.0f),
        renderer_.NameFormat(), text, DWRITE_TEXT_ALIGNMENT_CENTER, true);

    if (entries_[entry].isBroken)
    {
        D2D1_COLOR_F warn = colors_.warning;
        warn.a *= alpha;
        renderer_.FillEllipse(D2D1::Ellipse(D2D1::Point2F(tile.right - 5.0f, tile.top + 5.0f), 8.5f, 8.5f), warn);
        D2D1_COLOR_F mark = {1.0f, 1.0f, 1.0f, alpha};
        renderer_.DrawTextCentered(L"!", D2D1::RectF(tile.right - 13.5f, tile.top - 3.5f, tile.right + 3.5f, tile.top + 13.5f),
            renderer_.SmallFormat(), mark);
    }
}

void LauncherApp::DrawEmptyHint()
{
    if (!entries_.empty())
    {
        if (!visible_.empty()) return;
        renderer_.DrawTextCentered(L"没有匹配的应用",
            D2D1::RectF(0, heightDip_ * 0.5f - 16, widthDip_, heightDip_ * 0.5f + 16),
            renderer_.SearchFormat(), colors_.textSecondary);
        return;
    }

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

void LauncherApp::RecaptureBackdrop(bool live)
{
    if (!rendererReady_ || !hwnd_) return;
    ULONGLONG t0 = GetTickCount64();
    bool captured = backdrop_.CapturePixels(hwnd_, dpi_, settings_.blurRadius);
    ULONGLONG t1 = GetTickCount64();
    bool uploaded = captured && backdrop_.Upload(renderer_);
    ULONGLONG t2 = GetTickCount64();

    if (uploaded)
    {
        if (live)
        {
            liveCaptureMs_ += (double)(t1 - t0);
            liveUploadMs_ += (double)(t2 - t1);
        }
        else
        {
            Log("backdrop %dx%d in %llu ms (capture %llu / upload %llu)",
                backdrop_.WidthPx(), backdrop_.HeightPx(), t2 - t0, t1 - t0, t2 - t1);
        }
    }
    else if (!live)
    {
        Log("backdrop capture failed");
    }
}

void LauncherApp::RebuildBackdrop()
{
    wchar_t path[MAX_PATH] = {};
    bool dump = GetEnvironmentVariableW(L"GL_NATIVE_DUMP_BACKDROP", path, MAX_PATH) > 0;

    if (dump)
    {
        // 诊断：可见状态下重新抓一次并落盘，检查"自己的窗口/阴影是否被抓进背景"
        backdrop_.CapturePixels(hwnd_, dpi_, settings_.blurRadius);
        Log("rebuild dump ok=%d", backdrop_.DumpBmp(path) ? 1 : 0);
        backdrop_.Upload(renderer_);
        return;
    }

    RecaptureBackdrop(false);
}

void LauncherApp::OnLiveCaptureReady()
{
    const uint32_t* pixels = nullptr;
    int width = 0;
    int height = 0;
    POINT origin = {};

    if (!liveCapture_.TakeReady(&pixels, &width, &height, &origin)) return;

    // 只在拖动/缩放进行中接受实时帧；结束后的在途帧会覆盖刚抓好的静态帧，
    // 造成"底部一条没盖到"之类的透明缝（旧帧尺寸比窗口小）。尺寸不匹配也丢。
    bool active = windowDrag_ || windowResize_;
    RECT wr = {};
    GetWindowRect(hwnd_, &wr);
    int padPx = (int)(48.0f * dpi_);
    int expectedWidth = ((wr.right - wr.left) + padPx * 2 + 1) / 2;
    int expectedHeight = ((wr.bottom - wr.top) + padPx * 2 + 1) / 2;

    if (!active || width != expectedWidth || height != expectedHeight)
    {
        liveCapture_.ReleaseBuffer();
        return;
    }

    if (backdrop_.AdoptPixels(pixels, width, height, origin.x, origin.y, renderer_))
    {
        liveFrames_++;
        liveTotalMs_ += (double)liveCapture_.LastGrabMs();
    }

    liveCapture_.ReleaseBuffer();
    ScheduleRepaint();
}

void LauncherApp::UpdateWindowRegion()
{
    if (!hwnd_) return;
    RECT rc = {};
    GetClientRect(hwnd_, &rc);
    if (rc.right <= 0 || rc.bottom <= 0) return;

    // 最大化时由 DWM 负责（直角），自绘圆角区域要清掉，否则四角会露出桌面
    if (IsZoomed(hwnd_))
    {
        SetWindowRgn(hwnd_, nullptr, TRUE);
        return;
    }

    int radius = (int)(16.0f * dpi_);
    HRGN region = CreateRoundRectRgn(0, 0, rc.right + 1, rc.bottom + 1, radius * 2, radius * 2);
    if (region)
    {
        int ok = SetWindowRgn(hwnd_, region, TRUE);
        Log("window region %ldx%ld radius=%d set=%d err=%lu", rc.right, rc.bottom, radius, ok, ok ? 0UL : GetLastError());
    }
}

void LauncherApp::ScheduleRepaint()
{
    InvalidateRect(hwnd_, nullptr, FALSE);
}

// ── 交互 ──────────────────────────────────────────────────────

void LauncherApp::OnMouseMove(POINT clientDip)
{
    if (press_ >= 0 && (GetAsyncKeyState(VK_LBUTTON) & 0x8000))
    {
        int dx = (int)clientDip.x - dragStartPt_.x;
        int dy = (int)clientDip.y - dragStartPt_.y;
        if (!itemDrag_ && dx * dx + dy * dy > 36)
        {
            itemDrag_ = true;
            dragFrom_ = press_;
            Log("item drag start from=%d", press_);
        }

        if (itemDrag_)
        {
            dragPoint_ = clientDip;
            dragTo_ = HitTestItems(clientDip);
            ScheduleRepaint();
        }
    }

    int hit = HitTestItems(clientDip);
    if (hit != hover_)
    {
        hover_ = hit;
        SetTimer(hwnd_, 6 /* TIMER_HOVER */, 16, nullptr);
        ScheduleRepaint();
    }
}

void LauncherApp::TickHoverAnimation()
{
    if (hoverProgress_.size() != entries_.size())
    {
        hoverProgress_.resize(entries_.size(), 0.0f);
    }

    const float step = 16.0f / 180.0f; // ~180ms 完成
    bool changed = false;

    for (size_t i = 0; i < hoverProgress_.size(); i++)
    {
        float target = ((int)i == hover_ && !itemDrag_) ? 1.0f : 0.0f;
        float value = hoverProgress_[i];
        if (value < target)
        {
            value = (std::min)(target, value + step);
            hoverProgress_[i] = value;
            changed = true;
        }
        else if (value > target)
        {
            value = (std::max)(target, value - step);
            hoverProgress_[i] = value;
            changed = true;
        }
    }

    if (!changed)
    {
        KillTimer(hwnd_, 6);
    }
    ScheduleRepaint();
}

void LauncherApp::OnLeftDown(POINT clientDip)
{
    float x = (float)clientDip.x;
    float y = (float)clientDip.y;

    Log("left down dip=%ld,%ld title=%d", clientDip.x, clientDip.y, y < TitleDip ? 1 : 0);

    if (y < TitleDip && x >= widthDip_ - 48.0f && x <= widthDip_ - 18.0f)
    {
        ShowSettings();
        return;
    }

    if (y < TitleDip)
    {
        StartWindowDrag();
        return;
    }

    press_ = HitTestItems(clientDip);
    dragStartPt_ = clientDip;
    dragPoint_ = clientDip;
    itemDrag_ = false;
    dragFrom_ = -1;
    dragTo_ = -1;
    if (press_ >= 0)
    {
        SetCapture(hwnd_);
    }
}

void LauncherApp::OnLeftUp(POINT clientDip)
{
    ReleaseCapture();
    Log("left up dip=%ld,%ld press=%d itemDrag=%d from=%d to=%d",
        clientDip.x, clientDip.y, press_, itemDrag_ ? 1 : 0, dragFrom_, dragTo_);

    if (itemDrag_ && dragFrom_ >= 0 && dragTo_ >= 0 && dragTo_ != dragFrom_ && filter_.empty())
    {
        MoveEntry(dragFrom_, dragTo_);
    }
    else if (!itemDrag_ && press_ >= 0 && HitTestItems(clientDip) == press_)
    {
        LaunchEntry(press_);
    }

    press_ = -1;
    itemDrag_ = false;
    dragFrom_ = -1;
    dragTo_ = -1;
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
    lastLiveTick_ = 0;
    liveFrames_ = 0;
    windowTicks_ = 0;
    liveTotalMs_ = 0;
    GetCursorPos(&dragCursorStart_);
    GetWindowRect(hwnd_, &dragWindowStart_);
    dragTimer_ = SetTimer(hwnd_, TIMER_DRAG, 8, nullptr);
    SetCapture(hwnd_);
}

void LauncherApp::MaybeSubmitLiveFrame()
{
    ULONGLONG now = GetTickCount64();
    if (now - lastLiveTick_ >= 16)
    {
        lastLiveTick_ = now;
        liveCapture_.Submit(hwnd_, dpi_, settings_.blurRadius);
    }
}

void LauncherApp::TickWindowDrag()
{
    if (!windowDrag_) return;
    windowTicks_++;

    if (autoDrag_)
    {
        ULONGLONG elapsed = GetTickCount64() - autoDragStart_;
        double t = autoDurationMs_ > 0 ? std::min(1.0, (double)elapsed / autoDurationMs_) : 1.0;
        double ease = 1.0 - std::pow(1.0 - t, 2.2);
        SetWindowPos(hwnd_, nullptr,
            dragWindowStart_.left + (int)(autoDx_ * ease),
            dragWindowStart_.top + (int)(autoDy_ * ease), 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        MaybeSubmitLiveFrame();
        if (t >= 1.0) StopWindowDrag();
        return;
    }

    POINT p = {};
    GetCursorPos(&p);
    int dx = p.x - dragCursorStart_.x;
    int dy = p.y - dragCursorStart_.y;
    SetWindowPos(hwnd_, nullptr, dragWindowStart_.left + dx, dragWindowStart_.top + dy, 0, 0,
        SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    MaybeSubmitLiveFrame();

    if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
    {
        StopWindowDrag();
    }
}

void LauncherApp::StartWindowResize(int hit)
{
    if (windowResize_ || windowDrag_) return;
    windowResize_ = true;
    resizeHit_ = hit;
    GetCursorPos(&resizeCursorStart_);
    GetWindowRect(hwnd_, &resizeRectStart_);
    SetCapture(hwnd_);
    resizeTimer_ = SetTimer(hwnd_, 5 /* TIMER_RESIZE */, 8, nullptr);
    Log("window resize start hit=%d", hit);
}

void LauncherApp::TickWindowResize()
{
    if (!windowResize_) return;

    POINT p = {};
    GetCursorPos(&p);
    int dx = p.x - resizeCursorStart_.x;
    int dy = p.y - resizeCursorStart_.y;

    RECT r = resizeRectStart_;
    if (resizeHit_ & 1) r.left += dx;
    if (resizeHit_ & 2) r.right += dx;
    if (resizeHit_ & 4) r.top += dy;
    if (resizeHit_ & 8) r.bottom += dy;

    int minW = (int)(680.0f * dpi_);
    int minH = (int)(460.0f * dpi_);
    if (r.right - r.left < minW)
    {
        if (resizeHit_ & 1) r.left = r.right - minW;
        else r.right = r.left + minW;
    }
    if (r.bottom - r.top < minH)
    {
        if (resizeHit_ & 4) r.top = r.bottom - minH;
        else r.bottom = r.top + minH;
    }

    SetWindowPos(hwnd_, nullptr, r.left, r.top, r.right - r.left, r.bottom - r.top,
        SWP_NOZORDER | SWP_NOACTIVATE);
    MaybeSubmitLiveFrame();

    if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
    {
        StopWindowResize();
    }
}

void LauncherApp::StopWindowResize()
{
    if (!windowResize_) return;
    windowResize_ = false;
    if (resizeTimer_) { KillTimer(hwnd_, 5); resizeTimer_ = 0; }
    ReleaseCapture();
    Log("window resize end");
    liveCapture_.DropReady();
    RebuildBackdrop();
    ScheduleRepaint();
}

void LauncherApp::ToggleMaximize()
{
    Log("toggle maximize zoomed=%d", IsZoomed(hwnd_) ? 1 : 0);
    if (IsZoomed(hwnd_)) ShowWindow(hwnd_, SW_RESTORE);
    else ShowWindow(hwnd_, SW_MAXIMIZE);
    UpdateWindowRegion();
    RebuildBackdrop();
    ScheduleRepaint();
}

void LauncherApp::ScheduleAutoDrag(int delayMs)
{
    SetTimer(hwnd_, 3 /* TIMER_AUTODRAG */, (UINT)std::max(1, delayMs), nullptr);
}

void LauncherApp::StartAutoDrag()
{
    Log("autodrag start dx=%d dy=%d dur=%d repeat=%d", autoDx_, autoDy_, autoDurationMs_, autoRepeat_);
    autoDrag_ = true;
    windowDrag_ = true;
    windowTicks_ = 0;
    liveFrames_ = 0;
    liveTotalMs_ = 0;
    lastLiveTick_ = 0;
    GetWindowRect(hwnd_, &dragWindowStart_);
    autoDragStart_ = GetTickCount64();
    SetCapture(hwnd_);
    dragTimer_ = SetTimer(hwnd_, TIMER_DRAG, 8, nullptr);
}

void LauncherApp::StopWindowDrag()
{
    if (!windowDrag_) return;
    windowDrag_ = false;
    if (dragTimer_) { KillTimer(hwnd_, TIMER_DRAG); dragTimer_ = 0; }
    ReleaseCapture();
    Log("window drag end rect=(%ld,%ld) windowTicks=%d liveFrames=%d avgGrab=%.1fms (capture=%.1f upload=%.1f blt=%d blur=%d)",
        dragWindowStart_.left, dragWindowStart_.top, windowTicks_, liveFrames_,
        liveFrames_ > 0 ? liveTotalMs_ / liveFrames_ : 0.0,
        liveFrames_ > 0 ? liveCaptureMs_ / liveFrames_ : 0.0,
        liveFrames_ > 0 ? liveUploadMs_ / liveFrames_ : 0.0,
        backdrop_.LastBltMs(), backdrop_.LastBlurMs());
    liveCapture_.DropReady();
    RebuildBackdrop();
    ScheduleRepaint();

    if (autoDrag_)
    {
        autoDrag_ = false;
        autoRepeat_--;
        if (autoRepeat_ > 0)
        {
            ScheduleAutoDrag(autoGapMs_);
        }
        else
        {
            Log("autodrag all done");
        }
    }
}

void LauncherApp::LaunchEntry(int index, bool asAdmin)
{
    if (index < 0 || index >= (int)entries_.size()) return;
    const AppEntry& e = entries_[index];
    if (e.targetPath.empty()) return;

    INT_PTR result = 0;
    if (asAdmin)
    {
        SHELLEXECUTEINFOW info = {};
        info.cbSize = sizeof(info);
        info.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_FLAG_NO_UI;
        info.hwnd = hwnd_;
        info.lpVerb = L"runas";
        info.lpFile = e.targetPath.c_str();
        info.lpParameters = e.arguments.empty() ? nullptr : e.arguments.c_str();
        info.lpDirectory = e.workingDirectory.empty() ? nullptr : e.workingDirectory.c_str();
        info.nShow = SW_SHOWNORMAL;
        result = ShellExecuteExW(&info) ? 1 : 0;
        if (info.hProcess) CloseHandle(info.hProcess);
    }
    else
    {
        HINSTANCE hr = ShellExecuteW(hwnd_, L"open", e.targetPath.c_str(),
            e.arguments.empty() ? nullptr : e.arguments.c_str(),
            e.workingDirectory.empty() ? nullptr : e.workingDirectory.c_str(),
            SW_SHOWNORMAL);
        result = (INT_PTR)hr;
    }

    if (result <= 32)
    {
        statusText_ = L"启动失败：" + e.displayName;
        showStatus_ = true;
        LogW(statusText_.c_str());
        SetTimer(hwnd_, 100, 5000, nullptr);
        ScheduleRepaint();
    }
    else
    {
        Log("launch ok index=%d admin=%d", index, asAdmin ? 1 : 0);
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

void LauncherApp::StartFade(bool fadeOut)
{
    if (!layered_ || !hwnd_) return;

    fadeOut_ = fadeOut;
    fading_ = true;
    fadeStart_ = GetTickCount64();
    if (fadeOut)
    {
        SetLayeredWindowAttributes(hwnd_, 0, 255, LWA_ALPHA);
    }
    SetTimer(hwnd_, 7 /* TIMER_FADE */, 16, nullptr);
}

void LauncherApp::TickFade()
{
    if (!fading_ || !hwnd_)
    {
        KillTimer(hwnd_, 7);
        return;
    }

    ULONGLONG elapsed = GetTickCount64() - fadeStart_;
    double t = fadeDurationMs_ > 0 ? std::min(1.0, (double)elapsed / (double)fadeDurationMs_) : 1.0;
    double eased = 1.0 - std::pow(1.0 - t, 2.0);
    int alpha = fadeOut_ ? (int)(255.0 * (1.0 - eased)) : (int)(255.0 * eased);
    if (alpha < 0) alpha = 0;
    if (alpha > 255) alpha = 255;
    SetLayeredWindowAttributes(hwnd_, 0, (BYTE)alpha, LWA_ALPHA);
    fadeAlpha_ = (BYTE)alpha;

    if (t >= 1.0)
    {
        fading_ = false;
        KillTimer(hwnd_, 7);
        if (fadeOut_)
        {
            fadeOut_ = false;
            ShowWindow(hwnd_, SW_HIDE);
            Log("hide (fade done)");
        }
    }
}

void LauncherApp::ShowLauncher()
{
    lastShowTick_ = GetTickCount64();

    // 关闭动画进行中又被唤出：直接反向淡入，不再重走"抓背景 + 重排"
    if (fading_ && fadeOut_)
    {
        StartFade(false);
        return;
    }

    fading_ = false;
    if (layered_ && hwnd_)
    {
        // 先透明，再显示：第一帧不会闪出"全亮的一下"，随后淡入
        SetLayeredWindowAttributes(hwnd_, 0, 0, LWA_ALPHA);
        fadeAlpha_ = 0;
    }

    // 先抓背景再显示：窗口不可见时抓到的是干净的桌面（隐藏期间桌面可能已经变了）
    liveCapture_.DropReady();
    if (!IsWindowVisible(hwnd_) || IsIconic(hwnd_))
    {
        backdrop_.CapturePixels(hwnd_, dpi_, settings_.blurRadius);
    }

    ShowWindow(hwnd_, SW_SHOWNOACTIVATE);
    if (rendererReady_)
    {
        backdrop_.Upload(renderer_);
        InvalidateRect(hwnd_, nullptr, FALSE);
    }

    SetForegroundWindow(hwnd_);
    if (searchEdit_) SetFocus(searchEdit_);
    ScheduleRepaint();

    if (layered_)
    {
        StartFade(false);
    }
    Log("show");
}

void LauncherApp::HideLauncher()
{
    if (windowResize_) StopWindowResize();
    if (windowDrag_) StopWindowDrag();

    if (layered_ && IsWindowVisible(hwnd_) && !fading_)
    {
        // 关闭动画：淡出 180ms 再真正隐藏
        StartFade(true);
        Log("hide (fade start)");
        return;
    }

    ShowWindow(hwnd_, SW_HIDE);
    Log("hide");
}

void LauncherApp::ShowSettings()
{
    if (windowDrag_) StopWindowDrag();

    modalOpen_ = true;
    SettingsDialog::ShowModal(hwnd_, instance_, settings_, [this](const Settings& updated) {
        settings_ = updated;
        ApplyTheme();
        ApplyTopmost();
        ApplyHotkey();
        ApplySearchFont();

        for (auto* icon : icons_)
        {
            if (icon) icon->Release();
        }
        icons_.clear();
        LoadIcons();
        RebuildVisible();
        RebuildBackdrop();
        ScheduleRepaint();
    });
    modalOpen_ = false;

    Log("settings closed theme=%d blur=%.0f topmost=%d", (int)settings_.theme,
        settings_.blurRadius, settings_.topmost ? 1 : 0);
}

void LauncherApp::InitTray()
{
    tray_.cbSize = sizeof(tray_);
    tray_.hWnd = hwnd_;
    tray_.uID = 1;
    tray_.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
    tray_.uCallbackMessage = WM_TRAYICON;
    tray_.hIcon = LoadIconW(instance_, MAKEINTRESOURCEW(1));
    if (!tray_.hIcon) tray_.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
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
    AppendMenuW(menu, MF_STRING, 2, L"设置…");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, 3, L"退出");

    POINT pt = {};
    GetCursorPos(&pt);
    SetForegroundWindow(hwnd_);
    menuOpen_ = true;
    int cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.x, pt.y, 0, hwnd_, nullptr);
    menuOpen_ = false;
    DestroyMenu(menu);
    PostMessage(hwnd_, WM_NULL, 0, 0);

    if (cmd == 1) ToggleVisible();
    else if (cmd == 2) ShowSettings();
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
            // 客户区铺满整个窗口（自绘标题栏）。
            // 实测：wParam=TRUE 时 rgrc[0] 已经是"新窗口矩形"（= 想要的客户区），
            // 直接 return 0 即可；千万不要把它改成 rgrc[1]（那是旧窗口矩形），
            // 否则缩放时客户区停在旧大小：WM_SIZE 不触发、底部/右侧露出一条桌面
            // （用户报的"最底部完全透明"）。
            if (w) return 0;
            break;

        case WM_NCHITTEST:
        {
            POINT p = {GET_X_LPARAM(l), GET_Y_LPARAM(l)};
            POINT c = p;
            ScreenToClient(hwnd, &c);

            RECT rc = {};
            GetClientRect(hwnd, &rc);
            float dx = (float)c.x / dpi_;
            float dy = (float)c.y / dpi_;
            float widthDip = (float)rc.right / dpi_;

            // 非最大化时四边/四角 6 DIP 内进入缩放（自己实现，不进 WM_ENTERSIZEMOVE）
            if (!IsZoomed(hwnd))
            {
                int border = (int)(6.0f * dpi_);
                bool left = c.x < border;
                bool right = c.x >= rc.right - border;
                bool top = c.y < border;
                bool bottom = c.y >= rc.bottom - border;

                if (top && left) return HTTOPLEFT;
                if (top && right) return HTTOPRIGHT;
                if (bottom && left) return HTBOTTOMLEFT;
                if (bottom && right) return HTBOTTOMRIGHT;
                if (left) return HTLEFT;
                if (right) return HTRIGHT;
                if (top) return HTTOP;
                if (bottom) return HTBOTTOM;
            }

            // 右上角齿轮是客户区按钮：先于"标题栏拖动"判定，否则永远点不到
            if (dy >= 0.0f && dy < TitleDip && dx >= widthDip - 48.0f && dx <= widthDip - 18.0f)
            {
                return HTCLIENT;
            }

            if (c.y >= 0 && dy < TitleDip)
            {
                return HTCAPTION;
            }
            return HTCLIENT;
        }

        case WM_NCLBUTTONDOWN:
            if (w == HTCAPTION)
            {
                if (IsZoomed(hwnd))
                {
                    ToggleMaximize();
                    return 0;
                }
                StartWindowDrag();
                return 0;
            }
            if (w >= HTLEFT && w <= HTBOTTOMRIGHT)
            {
                static const int edges[8] = {1, 2, 4, 5, 6, 8, 9, 10}; // HTLEFT..HTBOTTOMRIGHT
                StartWindowResize(edges[w - HTLEFT]);
                return 0;
            }
            Log("nclbuttondown unhandled w=%d", (int)w);
            break;

        case WM_NCLBUTTONDBLCLK:
            if (w == HTCAPTION)
            {
                StopWindowDrag();
                ToggleMaximize();
                return 0;
            }
            break;

        case WM_ACTIVATE:
            if (LOWORD(w) == WA_INACTIVE)
            {
                if (windowResize_) StopWindowResize();
                if (windowDrag_) StopWindowDrag();

                // mac Launchpad 行为：失去前台就自动收起（也顺带避开"非顶层时系统的白边"）。
                // 模态对话框/右键菜单/拖拽/淡出中不触发；刚唤出的 400ms 内也不触发，
                // 避免启动瞬间或 SetForegroundWindow 被系统拒绝时误隐藏。
                bool busy = modalOpen_ || menuOpen_ || itemDrag_ || fading_ ||
                            (GetTickCount64() - lastShowTick_ < 400);
                if (!busy && IsWindowVisible(hwnd_) &&
                    GetEnvironmentVariableW(L"GL_NATIVE_NO_AUTOHIDE", nullptr, 0) == 0)
                {
                    Log("auto-hide on blur");
                    HideLauncher();
                }
            }
            break;

        case WM_GETMINMAXINFO:
        {
            auto* info = reinterpret_cast<MINMAXINFO*>(l);
            info->ptMinTrackSize.x = (LONG)(680.0f * dpi_);
            info->ptMinTrackSize.y = (LONG)(460.0f * dpi_);
            return 0;
        }

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
        {
            RECT rc = {};
            GetClientRect(hwnd, &rc);
            if (rc.right > 0 && rc.bottom > 0)
            {
                // 圆角区域/子控件布局与渲染器无关：启动早期 renderer 还没建好时也要跟上，
                // 否则窗口区域会停在"带边框的旧客户区"，底部/右侧露出桌面（透明条 bug）。
                UpdateWindowRegion();
                LayoutChildren();

                if (rendererReady_)
                {
                    renderer_.Resize((UINT)(rc.right - rc.left), (UINT)(rc.bottom - rc.top), dpi_);
                    if (scroll_ > ScrollMax()) scroll_ = ScrollMax();
                    RebuildBackdrop();
                    ScheduleRepaint();
                }
            }
            return 0;
        }

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
            ApplySearchFont();
            LayoutChildren();
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

        case WM_RBUTTONUP:
        {
            POINT p = {GET_X_LPARAM(l), GET_Y_LPARAM(l)};
            POINT dip = {(LONG)(p.x / dpi_), (LONG)(p.y / dpi_)};
            int index = HitTestItems(dip);
            if (index >= 0) ShowItemMenu(index);
            return 0;
        }

        case WM_DROPFILES:
            HandleDroppedFiles(reinterpret_cast<HDROP>(w));
            return 0;

        case WM_COMMAND:
            if (LOWORD(w) == 1001 && HIWORD(w) == EN_CHANGE)
            {
                OnSearchChanged();
                return 0;
            }
            break;

        case WM_CTLCOLOREDIT:
        {
            HDC dc = reinterpret_cast<HDC>(w);
            SetTextColor(dc, dark_ ? RGB(0xF2, 0xF2, 0xF7) : RGB(0x1C, 0x1C, 0x1E));
            SetBkColor(dc, dark_ ? RGB(0x2A, 0x2B, 0x31) : RGB(0xE6, 0xE8, 0xED));
            return reinterpret_cast<LRESULT>(searchBackBrush_);
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
            if (w == 3) // TIMER_AUTODRAG
            {
                KillTimer(hwnd, 3);
                StartAutoDrag();
                return 0;
            }
            if (w == 5) // TIMER_RESIZE
            {
                TickWindowResize();
                return 0;
            }
            if (w == 6) // TIMER_HOVER
            {
                TickHoverAnimation();
                return 0;
            }
            if (w == 7) // TIMER_FADE
            {
                TickFade();
                return 0;
            }
            if (w == 4) // TIMER_AUTO_EXIT（测试用）
            {
                KillTimer(hwnd, 4);
                Log("auto exit");
                exiting_ = true;
                DestroyWindow(hwnd);
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

        case WM_APP_LIVE_CAPTURE:
            OnLiveCaptureReady();
            return 0;

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
