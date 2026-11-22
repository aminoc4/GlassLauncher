#include "settings.h"

#include <commctrl.h>
#include <shlobj.h>
#include <algorithm>
#include <cstdio>

#pragma comment(lib, "comctl32.lib")

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

const wchar_t* kClass = L"GlassLauncherSettings";
const wchar_t* kRunKey = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
const wchar_t* kRunValue = L"GlassLauncherNative";

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

    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = WndProcStatic;
    wc.hInstance = instance;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = dialog->backBrush_;
    wc.lpszClassName = kClass;
    RegisterClassExW(&wc);

    int dpi = GetDpiForWindow(owner);
    int width = MulDiv(420, dpi, 96);
    int height = MulDiv(470, dpi, 96);

    RECT ownerRect = {};
    GetWindowRect(owner, &ownerRect);
    int x = ownerRect.left + ((ownerRect.right - ownerRect.left) - width) / 2;
    int y = ownerRect.top + ((ownerRect.bottom - ownerRect.top) - height) / 2;

    dialog->hwnd_ = CreateWindowExW(WS_EX_DLGMODALFRAME, kClass, L"设置",
        WS_POPUP | WS_CAPTION | WS_SYSMENU, x, y, width, height,
        owner, nullptr, instance, dialog);

    if (!dialog->hwnd_)
    {
        delete dialog;
        return;
    }

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
    backBrush_ = CreateSolidBrush(RGB(0xF7, 0xF8, 0xFA));
    font_ = CreateFontW(-MulDiv(9, dpi, 72), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_TT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
        DEFAULT_PITCH | FF_DONTCARE, L"Microsoft YaHei UI");
}

SettingsDialog::~SettingsDialog()
{
    if (font_) DeleteObject(font_);
    if (backBrush_) DeleteObject(backBrush_);
}

void SettingsDialog::CreateChildren()
{
    int dpi = GetDpiForWindow(hwnd_);
    auto X = [dpi](int v) { return MulDiv(v, dpi, 96); };
    auto Y = [dpi](int v) { return MulDiv(v, dpi, 96); };

    HWND c;
    c = MakeControl(L"STATIC", L"主题", SS_LEFT, X(24), Y(24), X(80), Y(22), hwnd_, 0);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);

    c = MakeControl(L"COMBOBOX", L"", CBS_DROPDOWNLIST | WS_VSCROLL, X(120), Y(20), X(210), Y(200), hwnd_, ID_THEME);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);
    SendMessageW(c, CB_ADDSTRING, 0, (LPARAM)L"跟随系统");
    SendMessageW(c, CB_ADDSTRING, 0, (LPARAM)L"浅色");
    SendMessageW(c, CB_ADDSTRING, 0, (LPARAM)L"深色");

    c = MakeControl(L"STATIC", L"模糊半径", SS_LEFT, X(24), Y(60), X(80), Y(22), hwnd_, 0);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);

    c = MakeControl(TRACKBAR_CLASSW, L"", TBS_HORZ | TBS_AUTOTICKS, X(120), Y(56), X(210), Y(30), hwnd_, ID_BLUR);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);
    SendMessageW(c, TBM_SETRANGE, TRUE, MAKELONG(2, 32));
    SendMessageW(c, TBM_SETTICFREQ, 5, 0);

    c = MakeControl(L"STATIC", L"12", SS_RIGHT, X(340), Y(60), X(50), Y(22), hwnd_, ID_BLUR_VALUE);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);

    c = MakeControl(L"BUTTON", L"窗口置顶", BS_AUTOCHECKBOX, X(24), Y(100), X(180), Y(24), hwnd_, ID_TOPMOST);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);

    c = MakeControl(L"BUTTON", L"拖动时降低背景模糊（弱 GPU 用）", BS_AUTOCHECKBOX, X(24), Y(130), X(300), Y(24), hwnd_, ID_REDUCE_BLUR);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);

    c = MakeControl(L"BUTTON", L"开机自启", BS_AUTOCHECKBOX, X(24), Y(160), X(180), Y(24), hwnd_, ID_AUTOSTART);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);

    c = MakeControl(L"BUTTON", L"启动时检查失效条目", BS_AUTOCHECKBOX, X(24), Y(190), X(220), Y(24), hwnd_, ID_BROKEN);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);

    c = MakeControl(L"STATIC", L"快捷键", SS_LEFT, X(24), Y(230), X(80), Y(22), hwnd_, 0);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);

    c = MakeControl(L"EDIT", L"", WS_BORDER | ES_AUTOHSCROLL, X(120), Y(226), X(160), Y(26), hwnd_, ID_HOTKEY);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);

    c = MakeControl(L"STATIC", L"格式：Ctrl+Alt+G / F9（修饰键在前）", SS_LEFT, X(120), Y(256), X(260), Y(20), hwnd_, 0);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);

    c = MakeControl(L"BUTTON", L"打开数据目录", BS_PUSHBUTTON, X(24), Y(300), X(130), Y(30), hwnd_, ID_OPEN_DATA);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);

    c = MakeControl(L"BUTTON", L"清除图标缓存", BS_PUSHBUTTON, X(164), Y(300), X(130), Y(30), hwnd_, ID_CLEAR_CACHE);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);

    c = MakeControl(L"BUTTON", L"完成", BS_DEFPUSHBUTTON, X(300), Y(400), X(90), Y(32), hwnd_, ID_OK);
    SendMessageW(c, WM_SETFONT, (WPARAM)font_, TRUE);
}

void SettingsDialog::LoadValues()
{
    SendMessageW(GetDlgItem(hwnd_, ID_THEME), CB_SETCURSEL, (WPARAM)(int)settings_.theme, 0);
    SendMessageW(GetDlgItem(hwnd_, ID_BLUR), TBM_SETPOS, TRUE, (LPARAM)(int)settings_.blurRadius);
    SetWindowTextW(GetDlgItem(hwnd_, ID_BLUR_VALUE), std::to_wstring((int)settings_.blurRadius).c_str());
    SendMessageW(GetDlgItem(hwnd_, ID_TOPMOST), BM_SETCHECK, settings_.topmost ? BST_CHECKED : BST_UNCHECKED, 0);
    SendMessageW(GetDlgItem(hwnd_, ID_REDUCE_BLUR), BM_SETCHECK,
        settings_.reduceBlurWhileDragging ? BST_CHECKED : BST_UNCHECKED, 0);
    SendMessageW(GetDlgItem(hwnd_, ID_AUTOSTART), BM_SETCHECK, IsAutostartEnabled() ? BST_CHECKED : BST_UNCHECKED, 0);
    SendMessageW(GetDlgItem(hwnd_, ID_BROKEN), BM_SETCHECK,
        settings_.checkBrokenOnStart ? BST_CHECKED : BST_UNCHECKED, 0);
    SetWindowTextW(GetDlgItem(hwnd_, ID_HOTKEY), settings_.hotkey.c_str());
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
            SetBkColor(dc, RGB(0xF7, 0xF8, 0xFA));
            SetTextColor(dc, RGB(0x1C, 0x1C, 0x1E));
            return reinterpret_cast<LRESULT>(backBrush_);
        }

        case WM_CTLCOLOREDIT:
        {
            HDC dc = reinterpret_cast<HDC>(w);
            SetBkColor(dc, RGB(0xFF, 0xFF, 0xFF));
            SetTextColor(dc, RGB(0x1C, 0x1C, 0x1E));
            return reinterpret_cast<LRESULT>(GetStockObject(WHITE_BRUSH));
        }

        case WM_HSCROLL:
            if (reinterpret_cast<HWND>(l) == GetDlgItem(hwnd, ID_BLUR))
            {
                int value = (int)SendMessageW(GetDlgItem(hwnd, ID_BLUR), TBM_GETPOS, 0, 0);
                SetWindowTextW(GetDlgItem(hwnd, ID_BLUR_VALUE), std::to_wstring(value).c_str());
            }
            return 0;

        case WM_COMMAND:
            switch (LOWORD(w))
            {
                case ID_OK:
                    Commit();
                    DestroyWindow(hwnd);
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
                        } while (FindNextFileW(find, &fd));
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
