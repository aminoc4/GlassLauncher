// 设置窗口：标准 Win32 控件（组合框/滑杆/复选框/编辑框），保持与主窗口同一套设置文件。
#pragma once

#include <windows.h>
#include <functional>
#include <string>
#include "store.h"

namespace gl {

bool IsAutostartEnabled();
void SetAutostart(bool enabled);

class SettingsDialog
{
public:
    using ApplyFn = std::function<void(const Settings&)>;

    static void ShowModal(HWND owner, HINSTANCE instance, const Settings& current, ApplyFn onApply);

private:
    SettingsDialog(HINSTANCE instance, HWND owner, const Settings& current, ApplyFn onApply);
    ~SettingsDialog();

    static LRESULT CALLBACK WndProcStatic(HWND hwnd, UINT msg, WPARAM w, LPARAM l);
    LRESULT WndProc(HWND hwnd, UINT msg, WPARAM w, LPARAM l);

    void CreateChildren();
    void LoadValues();
    void UpdateBlurLabel();
    void ResetToDefaults();
    Settings ReadValues() const;
    void Commit();

    HINSTANCE instance_ = nullptr;
    HWND owner_ = nullptr;
    HWND hwnd_ = nullptr;
    HFONT font_ = nullptr;
    HFONT fontBold_ = nullptr;
    HBRUSH backBrush_ = nullptr;
    HBRUSH editBrush_ = nullptr;
    bool dark_ = false;
    COLORREF colorBack_ = 0;
    COLORREF colorText_ = 0;
    COLORREF colorHint_ = 0;
    COLORREF colorEditBack_ = 0;
    COLORREF colorEditText_ = 0;
    Settings settings_;
    ApplyFn onApply_;
};

} // namespace gl
