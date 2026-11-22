// 拖动中的实时背景抓帧：工作线程负责 BitBlt + 模糊，UI 线程只做 D2D 上传。
//
// 为什么需要：同步抓一帧要 15–20ms（StretchBlt + 3×盒式模糊），放在 8ms 的 UI 计时器里
// 会把窗口移动节拍拖到 ~50fps。搬到工作线程后 UI 每帧只剩 ~0.2ms 的 CopyFromMemory，
// 窗口移动与背景更新互不阻塞；抓帧本身仍然 ~50–60fps（掉帧就丢，不排队）。
#pragma once

#include <windows.h>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <mutex>
#include <thread>
#include <vector>

namespace gl {

constexpr UINT WM_APP_LIVE_CAPTURE = WM_APP + 5;

class LiveCapture
{
public:
    ~LiveCapture();

    void Start(HWND hwnd);
    void Stop();

    /// UI 线程：请求抓一帧（窗口当前矩形）。工作线程忙/没有空闲缓冲时直接丢。
    void Submit(HWND hwnd, float dpi, double blurRadius);

    /// UI 线程（收到 WM_APP_LIVE_CAPTURE 后调用）：取走最新一帧。
    bool TakeReady(const uint32_t** pixels, int* width, int* height, POINT* origin);

    /// UI 线程：上传完成后把缓冲还给工作线程。
    void ReleaseBuffer();

    int LiveFrames() const { return liveFrames_; }
    int LastGrabMs() const { return lastGrabMs_; }

private:
    struct Frame
    {
        std::vector<uint32_t> pixels;
        int width = 0;
        int height = 0;
        POINT origin = {};
    };

    void WorkerLoop();
    bool CaptureInto(Frame& frame, HWND hwnd, float dpi, double blurRadius);
    bool EnsureGdi(int width, int height);
    void ReleaseGdi();

    static constexpr int BufferCount = 2;

    std::thread thread_;
    std::atomic<bool> running_{false};
    HWND notify_ = nullptr;
    bool started_ = false;

    std::mutex mutex_;
    std::condition_variable wake_;
    bool requestPending_ = false;
    HWND requestHwnd_ = nullptr;
    float requestDpi_ = 1.0f;
    double requestBlur_ = 12.0;

    Frame frames_[BufferCount];
    std::vector<int> free_;
    int readyIndex_ = -1;
    int takenIndex_ = -1;

    // 工作线程独占
    HDC screenDc_ = nullptr;
    HDC memoryDc_ = nullptr;
    HBITMAP dib_ = nullptr;
    HGDIOBJ previous_ = nullptr;
    void* bits_ = nullptr;
    int gdiWidth_ = 0;
    int gdiHeight_ = 0;

    int liveFrames_ = 0;
    int lastGrabMs_ = 0;
};

} // namespace gl
