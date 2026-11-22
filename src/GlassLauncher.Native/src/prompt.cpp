#include "prompt.h"

#include <algorithm>

namespace gl {

namespace {

constexpr int ID_EDIT = 201;
constexpr int ID_OK = 401;
constexpr int ID_CANCEL = 402;
const wchar_t* kClass = L"GlassLauncherPrompt";

struct PromptState
{
    HWND hwnd = nullptr;
    HFONT font = nullptr;
    HBRUSH back = nullptr;
    HWND edit = nullptr;
    std::wstring initial;
    std::wstring viewLabel;
    std::wstring viewTitle;
    std::wstring* result = nullptr;
    bool accepted = false;
};

HWND Label(HWND parent, const wchar_t* text, int x, int y, int w, int h, HFONT font)
{
    HWND c = CreateWindowExW(0, L"STATIC", text, WS_CHILD | WS_VISIBLE | SS_LEFT,
        x, y, w, h, parent, nullptr, nullptr, nullptr);
    SendMessageW(c, WM_SETFONT, (WPARAM)font, TRUE);
    return c;
}

} // namespace

bool PromptDialog::ShowModal(HWND owner, HINSTANCE instance, const wchar_t* title,
                             const wchar_t* label, const std::wstring& initial,
                             std::wstring& result)
{
    int dpi = GetDpiForWindow(owner);
    int width = MulDiv(440, dpi, 96);
    int height = MulDiv(196, dpi, 96);

    PromptState state;
    state.initial = initial;
    state.viewLabel = label ? label : L"";
    state.viewTitle = title ? title : L"";
    state.result = &result;
    state.back = CreateSolidBrush(RGB(0xF7, 0xF8, 0xFA));
    state.font = CreateFontW(-MulDiv(9, dpi, 72), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_TT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
        DEFAULT_PITCH | FF_DONTCARE, L"Microsoft YaHei UI");

    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = WndProcStatic;
    wc.hInstance = instance;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = state.back;
    wc.lpszClassName = kClass;
    RegisterClassExW(&wc);

    RECT ownerRect = {};
    GetWindowRect(owner, &ownerRect);
    int x = ownerRect.left + ((ownerRect.right - ownerRect.left) - width) / 2;
    int y = ownerRect.top + ((ownerRect.bottom - ownerRect.top) - height) / 2;

    HWND hwnd = CreateWindowExW(WS_EX_DLGMODALFRAME, kClass, state.viewTitle.c_str(),
        WS_POPUP | WS_CAPTION | WS_SYSMENU, x, y, width, height,
        owner, nullptr, instance, &state);
    if (!hwnd) return false;

    EnableWindow(owner, FALSE);
    ShowWindow(hwnd, SW_SHOW);
    UpdateWindow(hwnd);
    SetForegroundWindow(hwnd);

    MSG msg = {};
    while (IsWindow(hwnd))
    {
        if (GetMessageW(&msg, nullptr, 0, 0) <= 0) break;
        if (!IsDialogMessageW(hwnd, &msg))
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    EnableWindow(owner, TRUE);
    SetForegroundWindow(owner);

    if (state.font) DeleteObject(state.font);
    if (state.back) DeleteObject(state.back);
    return state.accepted;
}

LRESULT CALLBACK PromptDialog::WndProcStatic(HWND hwnd, UINT msg, WPARAM w, LPARAM l)
{
    auto* state = reinterpret_cast<PromptState*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));

    switch (msg)
    {
        case WM_NCCREATE:
        {
            auto* cs = reinterpret_cast<CREATESTRUCTW*>(l);
            state = reinterpret_cast<PromptState*>(cs->lpCreateParams);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(state));
            state->hwnd = hwnd;
            return DefWindowProcW(hwnd, msg, w, l);
        }

        case WM_CREATE:
        {
            int dpi = GetDpiForWindow(hwnd);
            auto X = [dpi](int v) { return MulDiv(v, dpi, 96); };
            auto Y = [dpi](int v) { return MulDiv(v, dpi, 96); };

            Label(hwnd, state->viewLabel.c_str(), X(20), Y(18), X(390), Y(22), state->font);

            state->edit = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", state->initial.c_str(),
                WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL, X(20), Y(50), X(390), Y(30),
                hwnd, reinterpret_cast<HMENU>((INT_PTR)ID_EDIT), nullptr, nullptr);
            SendMessageW(state->edit, WM_SETFONT, (WPARAM)state->font, TRUE);
            SendMessageW(state->edit, EM_SETSEL, 0, -1);

            HWND ok = CreateWindowExW(0, L"BUTTON", L"确定", WS_CHILD | WS_VISIBLE | BS_DEFPUSHBUTTON,
                X(240), Y(104), X(80), Y(32), hwnd, reinterpret_cast<HMENU>((INT_PTR)ID_OK), nullptr, nullptr);
            HWND cancel = CreateWindowExW(0, L"BUTTON", L"取消", WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
                X(330), Y(104), X(80), Y(32), hwnd, reinterpret_cast<HMENU>((INT_PTR)ID_CANCEL), nullptr, nullptr);
            SendMessageW(ok, WM_SETFONT, (WPARAM)state->font, TRUE);
            SendMessageW(cancel, WM_SETFONT, (WPARAM)state->font, TRUE);

            SetFocus(state->edit);
            return 0;
        }

        case WM_CTLCOLORSTATIC:
        case WM_CTLCOLORBTN:
        {
            HDC dc = reinterpret_cast<HDC>(w);
            SetBkColor(dc, RGB(0xF7, 0xF8, 0xFA));
            SetTextColor(dc, RGB(0x1C, 0x1C, 0x1E));
            return reinterpret_cast<LRESULT>(state ? state->back : GetStockObject(WHITE_BRUSH));
        }

        case WM_CTLCOLOREDIT:
        {
            HDC dc = reinterpret_cast<HDC>(w);
            SetBkColor(dc, RGB(0xFF, 0xFF, 0xFF));
            SetTextColor(dc, RGB(0x1C, 0x1C, 0x1E));
            return reinterpret_cast<LRESULT>(GetStockObject(WHITE_BRUSH));
        }

        case WM_COMMAND:
            if (LOWORD(w) == ID_OK)
            {
                wchar_t buf[1024] = {};
                GetWindowTextW(state->edit, buf, 1024);
                *state->result = buf;
                state->accepted = true;
                DestroyWindow(hwnd);
                return 0;
            }
            if (LOWORD(w) == ID_CANCEL)
            {
                DestroyWindow(hwnd);
                return 0;
            }
            break;

        case WM_CLOSE:
            DestroyWindow(hwnd);
            return 0;
    }

    return DefWindowProcW(hwnd, msg, w, l);
}

} // namespace gl
