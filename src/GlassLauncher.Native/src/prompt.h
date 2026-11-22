// 单行输入对话框（重命名/重新指定目标共用）。模态：禁用 owner + 局部消息循环。
#pragma once

#include <windows.h>
#include <string>

namespace gl {

class PromptDialog
{
public:
    static bool ShowModal(HWND owner, HINSTANCE instance, const wchar_t* title,
                          const wchar_t* label, const std::wstring& initial,
                          std::wstring& result);

private:
    static LRESULT CALLBACK WndProcStatic(HWND hwnd, UINT msg, WPARAM w, LPARAM l);
};

} // namespace gl
