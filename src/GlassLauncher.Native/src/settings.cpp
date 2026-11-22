// 设置窗口：标准 Win32 控件（组合框/滑杆/复选框/编辑框），保持与主窗口同一套设置文件。
// 布局按"外观 / 窗口与行为 / 快捷键 / 数据"分组；跟随应用主题（深色时启用系统暗色控件）。
#include "settings.h"

#include <commctrl.h>
#include <dwmapi.h>
#include <shlobj.h>
#include <algorithm>
#include <cstdio>

#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "dwmapi.lib")

namespace gl {

namespace {

constexpr int ID_THEME = 101;
constexpr int ID_BLUR = 102;
constexpr int ID_BLUR_VALUE = 103;
constexpr int ID_TOPMOST = 201;
constexpr int ID_REDUCE_BLUR = 202;
constexpr int ID_AUTOSTART = 203;
constexpr int ID_BROKEN = 204;
constexpr int ID_HOTKEY = 301;
constexpr int ID_OK = 401;
constexpr int ID_CLEAR_CACHE = 402;
constexpr int ID_OPEN_DATA = 403;
constexpr int ID_RESET = 404;
constexpr int ID_HINT = 501;

const wchar_t* kClass = L"GlassLauncherSettings";
const wchar_t* kRunKey = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
const wchar_t* kRunValue = L"GlassLauncherNative";

// 以下三个颜色随主题切换；主窗口的深色判断在 app.cpp 里，这里是设置窗口自己的判断。
struct Palette
{
    COLORREF back;
    COLORREF text;
    COLORREF hint;
    COLORREF editBack;
    COLORREF editText;
};

Palette LightPalette() { return { RGB(0xF7, 0xF8, 0xFA), RGB(0x1C, 0x1C, 0x1E), RGB(0x6E, 0x6E, 0x73), RGB(0xFF, 0xFF, 0xFF), RGB(0x1C, 0x1C, 0x1E) }; }
Palette DarkPalette() { return { RGB(0x1E, 0x20, 0x24), RGB(0xF2, 0xF2, 0xF7), RGB(0xB8, 0xB8, 0xBF), RGB(0x2A, 0x2C, 0x31), RGB(0xF2, 0xF2, 0xF7) }; }

bool SystemUsesDarkApps()
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER,
            L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", 0, KEY_READ, &key) != ERROR_SUCCESS)
    {
        return false;
    }
    DWORD value = 1;
    DWORD size = sizeof(value);
    RegQueryValueExW(key, L"AppsUseLightTheme", nullptr, nullptr, reinterpret_cast<LPBYTE>(&value), &size);
    RegCloseKey(key);
    return value == 0;
}

bool IsDarkSettings(const Settings& s)
{
    if (s.theme == ThemeMode::Dark) return true;
    if (s.theme == ThemeMode::Light) return false;
    return SystemUsesDarkApps();
}

// uxtheme 的暗色开关（Win10 1903+ 的未文档化导出）：
//   135 = SetPreferredAppMode(0 默认 / 1 允许 / 2 强制暗 / 3 强制亮)
//   133 = AllowDarkModeForWindow(hwnd, bool)
void SetProcessDarkMode(bool dark)
{
    static HMODULE ux = nullptr;
    if (!ux) ux = LoadLibraryW(L"uxtheme.dll");
    if (!ux) return;
    using SetModeFn = int(__stdcall*)(int);
    auto setMode = reinterpret_cast<SetModeFn>(GetProcAddress(ux, MAKEINTRESOURCEA(135)));
    if (setMode) setMode(dark ? 2 : 3);
}

void AllowDarkWindow(HWND hwnd, bool dark)
{
    static HMODULE ux = nullptr;
    if (!ux) ux = LoadLibraryW(L"uxtheme.dll");
    if (!ux) return;
    using AllowFn = bool(__stdcall*)(HWND, BOOL);
    auto allow = reinterpret_cast<AllowFn>(GetProcAddress(ux, MAKEINTRESOURCEA(133)));
    if (allow) allow(hwnd, dark ? TRUE : FALSE);
}

HWND MakeControl(const wchar_t* cls, const wchar_t* text, DWORD style, int x, int y, int w, int h,
                 HWND parent, int id)
{
    return CreateWindowExW(0, cls, text, WS_CHILD | WS_VISIBLE | style, x, y, w, h,
        parent, reinterpret_cast<HMENU>((INT_PTR)id), nullptr, nullptr);
}

} // namespace

bool IsAutostartEnabled()
{
    wchar_t value[MAX_PATH * 2] = {};
    DWORD size = sizeof(value);
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kRunKey, 0, KEY_READ, &key) != ERROR_SUCCESS) return false;
    LONG result = RegQueryValueExW(key, kRunValue, nullptr, nullptr, reinterpret_cast<LPBYTE>(value), &size);
    RegCloseKey(key);
    return result == ERROR_SUCCESS && value[0] != 0;
}

void SetAutostart(bool enabled)
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kRunKey, 0, KEY_SET_VALUE, &key) != ERROR_SUCCESS) return;

    if (enabled)
    {
        wchar_t exe[MAX_PATH] = {};
        GetModuleFileNameW(nullptr, exe, MAX_PATH);
        std::wstring quoted = L"\"" + std::wstring(exe) + L"\"";
        RegSetValueExW(key, kRunValue, 0, REG_SZ, reinterpret_cast<const BYTE*>(quoted.c_str()),
            (DWORD)((quoted.size() + 1) * sizeof(wchar_t)));
    }
    else
    {
        RegDeleteValueW(key, kRunValue);
    }
    RegCloseKey(key);
}

void SettingsDialog::ShowModal(HWND owner, HINSTANCE instance, const Settings& current, ApplyFn onApply)
{
    auto* dialog = new SettingsDialog(instance, owner, current, std::move(onApply));

    SetProcessDarkMode(dialog->dark_);

    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = WndProcStatic;
    wc.hInstance = instance;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = dialog->backBrush_;
    wc.lpszClassName = kClass;
    RegisterClassExW(&wc);

    int dpi = GetDpiForWindow(owner);
    int width = MulDiv(520, dpi, 96);
    // 高度含标题栏：客户区约 570 DIP，底部按钮在 504–538，留 32 DIP 余量
    int height = MulDiv(600, dpi, 96);

    RECT ownerRect = {};
    GetWindowRect(owner, &ownerRect);
    int x = ownerRect.left + ((ownerRect.right - ownerRect.left) - width) / 2;
    int y = ownerRect.top + ((ownerRect.bottom - ownerRect.top) - height) / 2;

    dialog->hwnd_ = CreateWindowExW(WS_EX_DLGMODALFRAME, kClass, L"GlassLauncher 设置",
        WS_POPUP | WS_CAPTION | WS_SYSMENU, x, y, width, height,
        owner, nullptr, instance, dialog);

    if (!dialog->hwnd_)
    {
        delete dialog;
        return;
    }

    AllowDarkWindow(dialog->hwnd_, dialog->dark_);
    BOOL darkTitle = dialog->dark_ ? TRUE : FALSE;
    DwmSetWindowAttribute(dialog->hwnd_, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, &darkTitle, sizeof(darkTitle));

    EnableWindow(owner, FALSE);
    ShowWindow(dialog->hwnd_, SW_SHOW);
    UpdateWindow(dialog->hwnd_);

    MSG msg = {};
    while (IsWindow(dialog->hwnd_))
    {
        if (GetMessageW(&msg, nullptr, 0, 0) <= 0) break;
        if (!IsDialogMessageW(dialog->hwnd_, &msg))
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    EnableWindow(owner, TRUE);
    SetForegroundWindow(owner);
}

SettingsDialog::SettingsDialog(HINSTANCE instance, HWND owner, const Settings& current, ApplyFn onApply)
    : instance_(instance), owner_(owner), settings_(current), onApply_(std::move(onApply))
{
    int dpi = GetDpiForWindow(owner);
    dark_ = IsDarkSettings(settings_);
    Palette pal = dark_ ? DarkPalette() : LightPalette();
    colorBack_ = pal.back;
    colorText_ = pal.text;
    colorHint_ = pal.hint;
    colorEditBack_ = pal.editBack;
    colorEditText_ = pal.editText;

    backBrush_ = CreateSolidBrush(colorBack_);
    editBrush_ = CreateSolidBrush(colorEditBack_);
    font_ = CreateFontW(-MulDiv(10, dpi, 72), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_TT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
        DEFAULT_PITCH | FF_DONTCARE, L"Microsoft YaHei UI");
    fontBold_ = CreateFontW(-MulDiv(10, dpi, 72), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_TT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
        DEFAULT_PITCH | FF_DONTCARE, L"Microsoft YaHei UI");
}

SettingsDialog::~SettingsDialog()
{
    if (font_) DeleteObject(font_);
    if (fontBold_) DeleteObject(fontBold_);
    if (editBrush_) DeleteObject(editBrush_);
    if (backBrush_) DeleteObject(backBrush_);
}

void SettingsDialog::CreateChildren()
{
    int dpi = GetDpiForWindow(hwnd_);
    auto X = [dpi](int v) { return MulDiv(v, dpi, 96); };
    auto Y = [dpi](int v) { return MulDiv(v, dpi, 96); };

    HWND c = nullptr;

    auto label = [&](const wchar_t* text, int x, int y, int w, int h, int id = 0, HFONT font = nullptr) {
        HWND hw = MakeControl(L"STATIC", text, SS_LEFT, X(x), Y(y), X(w), Y(h), hwnd_, id);
        SendMessageW(hw, WM_SETFONT, (WPARAM)(font ? font : font_), TRUE);
        return hw;
    };
    auto group = [&](const wchar_t* title, int x, int y, int w, int h) {
        HWND hw = MakeControl(L"BUTTON", title, BS_GROUPBOX, X(x), Y(y), X(w), Y(h), hwnd_, 0);
        SendMessageW(hw, WM_SETFONT, (WPARAM)fontBold_, TRUE);
    };

    // —— 外观 ——
    group(L"外观", 20, 14, 480, 118);
    label(L"主题", 36, 48, 86, 24);
    c = MakeControl(L"COMBOBOX", L"", CBS_DROPDOWNLIST | WS_VSCROLL, X(130), Y(42), X(170), Y(220), hwnd_, ID_THEME);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);
    SendMessageW(c, CB_ADDSTRING, 0, (LPARAM)L"跟随系统");
    SendMessageW(c, CB_ADDSTRING, 0, (LPARAM)L"浅色");
    SendMessageW(c, CB_ADDSTRING, 0, (LPARAM)L"深色");

    label(L"模糊半径", 36, 88, 86, 24);
    c = MakeControl(TRACKBAR_CLASSW, L"", TBS_HORZ | TBS_AUTOTICKS, X(130), Y(82), X(220), Y(32), hwnd_, ID_BLUR);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);
    SendMessageW(c, TBM_SETRANGE, TRUE, MAKELONG(2, 32));
    SendMessageW(c, TBM_SETTICFREQ, 5, 0);
    c = MakeControl(L"STATIC", L"12 px", SS_LEFT, X(362), Y(88), X(80), Y(24), hwnd_, ID_BLUR_VALUE);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);

    // —— 窗口与行为 ——
    group(L"窗口与行为", 20, 146, 480, 146);
    c = MakeControl(L"BUTTON", L"窗口置顶（始终显示在最前面）", BS_AUTOCHECKBOX, X(36), Y(172), X(380), Y(24), hwnd_, ID_TOPMOST);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);
    c = MakeControl(L"BUTTON", L"拖动时降低背景模糊（弱 GPU 用）", BS_AUTOCHECKBOX, X(36), Y(200), X(380), Y(24), hwnd_, ID_REDUCE_BLUR);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);
    c = MakeControl(L"BUTTON", L"开机自启", BS_AUTOCHECKBOX, X(36), Y(228), X(380), Y(24), hwnd_, ID_AUTOSTART);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);
    c = MakeControl(L"BUTTON", L"启动时检查失效条目", BS_AUTOCHECKBOX, X(36), Y(256), X(380), Y(24), hwnd_, ID_BROKEN);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);

    // —— 快捷键 ——
    group(L"快捷键", 20, 306, 480, 86);
    label(L"全局热键", 36, 336, 86, 24);
    c = MakeControl(L"EDIT", L"", WS_BORDER | ES_AUTOHSCROLL, X(130), Y(330), X(150), Y(28), hwnd_, ID_HOTKEY);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);
    c = MakeControl(L"STATIC", L"例：Ctrl+Alt+G 或 F9（修饰键在前）", SS_LEFT, X(130), Y(362), X(330), Y(22), hwnd_, ID_HINT);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);

    // —— 数据 ——
    group(L"数据", 20, 406, 480, 78);
    c = MakeControl(L"BUTTON", L"打开数据目录", BS_PUSHBUTTON, X(36), Y(436), X(130), Y(32), hwnd_, ID_OPEN_DATA);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);
    c = MakeControl(L"BUTTON", L"清除图标缓存", BS_PUSHBUTTON, X(180), Y(436), X(130), Y(32), hwnd_, ID_CLEAR_CACHE);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);

    // —— 底部按钮 ——
    c = MakeControl(L"BUTTON", L"恢复默认", BS_PUSHBUTTON, X(20), Y(504), X(110), Y(34), hwnd_, ID_RESET);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);
    c = MakeControl(L"BUTTON", L"完成", BS_DEFPUSHBUTTON, X(390), Y(504), X(110), Y(34), hwnd_, ID_OK);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);
}

void SettingsDialog::LoadValues()
{
    SendMessageW(GetDlgItem(hwnd_, ID_THEME), CB_SETCURSEL, (WPARAM)(int)settings_.theme, 0);
    SendMessageW(GetDlgItem(hwnd_, ID_BLUR), TBM_SETPOS, TRUE, (LPARAM)(int)settings_.blurRadius);
    UpdateBlurLabel();
    SendMessageW(GetDlgItem(hwnd_, ID_TOPMOST), BM_SETCHECK, settings_.topmost ? BST_CHECKED : BST_UNCHECKED, 0);
    SendMessageW(GetDlgItem(hwnd_, ID_REDUCE_BLUR), BM_SETCHECK,
        settings_.reduceBlurWhileDragging ? BST_CHECKED : BST_UNCHECKED, 0);
    SendMessageW(GetDlgItem(hwnd_, ID_AUTOSTART), BM_SETCHECK, IsAutostartEnabled() ? BST_CHECKED : BST_UNCHECKED, 0);
    SendMessageW(GetDlgItem(hwnd_, ID_BROKEN), BM_SETCHECK,
        settings_.checkBrokenOnStart ? BST_CHECKED : BST_UNCHECKED, 0);
    SetWindowTextW(GetDlgItem(hwnd_, ID_HOTKEY), settings_.hotkey.c_str());
}

void SettingsDialog::UpdateBlurLabel()
{
    int value = (int)SendMessageW(GetDlgItem(hwnd_, ID_BLUR), TBM_GETPOS, 0, 0);
    std::wstring text = std::to_wstring(value) + L" px";
    SetWindowTextW(GetDlgItem(hwnd_, ID_BLUR_VALUE), text.c_str());
}

void SettingsDialog::ResetToDefaults()
{
    SendMessageW(GetDlgItem(hwnd_, ID_THEME), CB_SETCURSEL, 0, 0);
    SendMessageW(GetDlgItem(hwnd_, ID_BLUR), TBM_SETPOS, TRUE, 12);
    UpdateBlurLabel();
    SendMessageW(GetDlgItem(hwnd_, ID_TOPMOST), BM_SETCHECK, BST_UNCHECKED, 0);
    SendMessageW(GetDlgItem(hwnd_, ID_REDUCE_BLUR), BM_SETCHECK, BST_UNCHECKED, 0);
    SendMessageW(GetDlgItem(hwnd_, ID_BROKEN), BM_SETCHECK, BST_CHECKED, 0);
    SetWindowTextW(GetDlgItem(hwnd_, ID_HOTKEY), L"Alt+G");
}

Settings SettingsDialog::ReadValues() const
{
    Settings s = settings_;
    s.theme = (ThemeMode)SendMessageW(GetDlgItem(hwnd_, ID_THEME), CB_GETCURSEL, 0, 0);
    s.blurRadius = (double)SendMessageW(GetDlgItem(hwnd_, ID_BLUR), TBM_GETPOS, 0, 0);
    s.topmost = SendMessageW(GetDlgItem(hwnd_, ID_TOPMOST), BM_GETCHECK, 0, 0) == BST_CHECKED;
    s.reduceBlurWhileDragging = SendMessageW(GetDlgItem(hwnd_, ID_REDUCE_BLUR), BM_GETCHECK, 0, 0) == BST_CHECKED;
    s.checkBrokenOnStart = SendMessageW(GetDlgItem(hwnd_, ID_BROKEN), BM_GETCHECK, 0, 0) == BST_CHECKED;

    wchar_t buffer[128] = {};
    GetWindowTextW(GetDlgItem(hwnd_, ID_HOTKEY), buffer, 128);
    if (buffer[0]) s.hotkey = buffer;
    if (s.blurRadius < 2) s.blurRadius = 2;
    if (s.blurRadius > 32) s.blurRadius = 32;
    return s;
}

void SettingsDialog::Commit()
{
    Settings updated = ReadValues();
    SetAutostart(SendMessageW(GetDlgItem(hwnd_, ID_AUTOSTART), BM_GETCHECK, 0, 0) == BST_CHECKED);

    std::wstring error;
    Store::SaveSettings(updated, &error);
    if (onApply_) onApply_(updated);
}

LRESULT CALLBACK SettingsDialog::WndProcStatic(HWND hwnd, UINT msg, WPARAM w, LPARAM l)
{
    SettingsDialog* self = nullptr;
    if (msg == WM_NCCREATE)
    {
        auto* cs = reinterpret_cast<CREATESTRUCTW*>(l);
        self = reinterpret_cast<SettingsDialog*>(cs->lpCreateParams);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
    }
    else
    {
        self = reinterpret_cast<SettingsDialog*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    }

    if (self) return self->WndProc(hwnd, msg, w, l);
    return DefWindowProcW(hwnd, msg, w, l);
}

LRESULT SettingsDialog::WndProc(HWND hwnd, UINT msg, WPARAM w, LPARAM l)
{
    hwnd_ = hwnd;

    switch (msg)
    {
        case WM_CREATE:
            CreateChildren();
            LoadValues();
            return 0;

        case WM_CTLCOLORSTATIC:
        case WM_CTLCOLORBTN:
        {
            HDC dc = reinterpret_cast<HDC>(w);
            HWND control = reinterpret_cast<HWND>(l);
            SetBkColor(dc, colorBack_);
            SetBkMode(dc, OPAQUE);
            bool hint = GetDlgCtrlID(control) == ID_HINT;
            SetTextColor(dc, hint ? colorHint_ : colorText_);
            return reinterpret_cast<LRESULT>(backBrush_);
        }

        case WM_CTLCOLOREDIT:
        {
            HDC dc = reinterpret_cast<HDC>(w);
            SetBkColor(dc, colorEditBack_);
            SetTextColor(dc, colorEditText_);
            return reinterpret_cast<LRESULT>(editBrush_);
        }

        case WM_HSCROLL:
            if (reinterpret_cast<HWND>(l) == GetDlgItem(hwnd, ID_BLUR))
            {
                UpdateBlurLabel();
            }
            return 0;

        case WM_COMMAND:
            switch (LOWORD(w))
            {
                case ID_OK:
                    Commit();
                    DestroyWindow(hwnd);
                    return 0;

                case ID_RESET:
                    ResetToDefaults();
                    return 0;

                case ID_CLEAR_CACHE:
                {
                    std::wstring dir = Store::DataDirectory() + L"\\icons";
                    WIN32_FIND_DATAW fd = {};
                    HANDLE find = FindFirstFileW((dir + L"\\*.png").c_str(), &fd);
                    if (find != INVALID_HANDLE_VALUE)
                    {
                        do
                        {
                            DeleteFileW((dir + L"\\" + fd.cFileName).c_str());
                        } while (FindNextFile(find, &fd));
                        FindClose(find);
                    }
                    MessageBoxW(hwnd, L"图标缓存已清除，重启后自动重建。", L"Glass Launcher", MB_OK | MB_ICONINFORMATION);
                    return 0;
                }

                case ID_OPEN_DATA:
                {
                    std::wstring dir = Store::DataDirectory();
                    ShellExecuteW(hwnd, L"open", L"explorer.exe", dir.c_str(), nullptr, SW_SHOWNORMAL);
                    return 0;
                }
            }
            break;

        case WM_CLOSE:
            DestroyWindow(hwnd);
            return 0;

        case WM_DESTROY:
            delete this;
            return 0;
    }

    return DefWindowProcW(hwnd, msg, w, l);
}

} // namespace gl
