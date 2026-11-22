// GlassLauncher 原生版应用主体：窗口、数据、渲染、托盘/热键、拖动。
#pragma once

#include <windows.h>
#include <d2d1.h>
#include <vector>
#include <string>

#include "renderer.h"
#include "backdrop.h"
#include "livecapture.h"
#include "store.h"

namespace gl {

class LauncherApp
{
public:
    int Run(HINSTANCE instance);

private:
    // ── 窗口/渲染 ──
    HINSTANCE instance_ = nullptr;
    HWND hwnd_ = nullptr;
    Renderer renderer_;
    Backdrop backdrop_;
    LiveCapture liveCapture_;
    ThemeColors colors_;
    float dpi_ = 1.0f;
    float widthDip_ = 840.0f;
    float heightDip_ = 560.0f;
    bool dark_ = false;
    bool rendererReady_ = false;

    // ── 数据 ──
    std::vector<AppEntry> entries_;
    std::vector<ID2D1Bitmap*> icons_;
    std::vector<int> visible_;          // 过滤后的条目下标（空过滤时全部）
    std::wstring filter_;
    Settings settings_;

    // 搜索框（子窗口 EDIT：IME/选中/剪贴板都由系统处理）
    HWND searchEdit_ = nullptr;
    HFONT searchFont_ = nullptr;
    HBRUSH searchBackBrush_ = nullptr;
    WNDPROC searchEditProc_ = nullptr;

    // ── 交互状态 ──
    int hover_ = -1;
    int press_ = -1;
    std::vector<float> hoverProgress_;   // 每格 0..1 的悬停放大进度（16ms 节拍刷新）
    int dragFrom_ = -1;
    int dragTo_ = -1;
    POINT dragStartPt_ = {};
    POINT dragPoint_ = {};
    bool itemDrag_ = false;
    bool windowDrag_ = false;
    POINT dragCursorStart_ = {};
    RECT dragWindowStart_ = {};
    UINT_PTR dragTimer_ = 0;

    // 非模态边缘缩放（同样绕开 WM_ENTERSIZEMOVE：排除+模态循环在本机 build 26300 会让 DWM 停画）
    bool windowResize_ = false;
    int resizeHit_ = 0;
    POINT resizeCursorStart_ = {};
    RECT resizeRectStart_ = {};
    UINT_PTR resizeTimer_ = 0;
    ULONGLONG lastLiveTick_ = 0;
    int liveFrames_ = 0;
    int windowTicks_ = 0;
    double liveTotalMs_ = 0;
    double liveCaptureMs_ = 0;
    double liveUploadMs_ = 0;

    // 自动化回归：GL_NATIVE_AUTODRAG="dx,dy,durationMs,repeats,gapMs"（仅测试用）
    bool autoDrag_ = false;
    ULONGLONG autoDragStart_ = 0;
    int autoDx_ = 0;
    int autoDy_ = 0;
    int autoDurationMs_ = 1500;
    int autoRepeat_ = 1;
    int autoGapMs_ = 700;
    bool autoDragPending_ = false;

    // 打开/关闭动画：分层窗口 alpha 淡入淡出（180ms）
    bool layered_ = false;
    bool fading_ = false;
    bool fadeOut_ = false;
    ULONGLONG fadeStart_ = 0;
    ULONGLONG fadeDurationMs_ = 180;
    BYTE fadeAlpha_ = 255;
    float scroll_ = 0.0f;
    bool showStatus_ = false;
    std::wstring statusText_;

    // ── 托盘/热键 ──
    NOTIFYICONDATAW tray_ = {};
    bool trayAdded_ = false;
    bool exiting_ = false;
    UINT hotkeyMods_ = 0;
    UINT hotkeyVk_ = 0;
    bool hotkeyOk_ = false;

    // ── 诊断 ──
    FILE* logFile_ = nullptr;
    ULONGLONG startTick_ = 0;
    UINT_PTR perfTimer_ = 0;

    static LRESULT CALLBACK WndProcStatic(HWND hwnd, UINT msg, WPARAM w, LPARAM l);
    LRESULT WndProc(HWND hwnd, UINT msg, WPARAM w, LPARAM l);

    bool CreateMainWindow();
    void LoadData();
    void LoadIcons();
    void ApplyTheme();
    void ApplyTopmost();
    void CheckBrokenEntries();

    void Paint();
    void DrawBackdrop();
    void DrawChrome();
    void DrawSearchBar();
    void DrawPill();
    void DrawItems();
    void DrawItemTile(int entry, float x, float y, bool hover, float alpha);
    void DrawEmptyHint();
    void DrawStatusBar();

    void RebuildBackdrop();
    void RecaptureBackdrop(bool live);
    void OnLiveCaptureReady();
    void ScheduleRepaint();
    void UpdateWindowRegion();

    // 布局/命中测试（DIP）
    RECT ItemRect(int index) const;
    int HitTestItems(POINT clientDip) const;
    int Columns() const;
    float GridTop() const { return 92.0f; }
    float ScrollMax() const;
    void EnsureVisible(int index);

    void OnMouseMove(POINT clientDip);
    void OnLeftDown(POINT clientDip);
    void OnLeftUp(POINT clientDip);
    void OnMouseWheel(int delta);
    void TickHoverAnimation();
    void ShowItemMenu(int index);
    void RemoveEntry(int index);
    void MoveEntry(int from, int to);
    void SaveEntriesLogged();
    void HandleDroppedFiles(HDROP drop);
    void RebuildVisible();
    void LayoutChildren();
    void CreateSearchEdit();
    void ApplySearchFont();
    static LRESULT CALLBACK SearchEditProc(HWND hwnd, UINT msg, WPARAM w, LPARAM l);
    void OnSearchChanged();
    void StartWindowDrag();
    void StopWindowDrag();
    void TickWindowDrag();
    void StartWindowResize(int hit);
    void StopWindowResize();
    void TickWindowResize();
    void ToggleMaximize();
    void MaybeSubmitLiveFrame();
    void ScheduleAutoDrag(int delayMs);
    void StartAutoDrag();

    void ToggleVisible();
    void ShowLauncher();
    void HideLauncher();
    void TickFade();
    void StartFade(bool fadeOut);
    void LaunchEntry(int index, bool asAdmin = false);
    void ShowSettings();

    void InitTray();
    void RemoveTray();
    void ShowTrayMenu();
    void ApplyHotkey();
    void RegisterHotkeyOrLog();

    void Log(const char* fmt, ...);
    void LogW(const wchar_t* text);
    void LogPerfTick();
};

} // namespace gl
