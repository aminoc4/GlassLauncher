#include "livecapture.h"
#include "backdrop.h"

#include <algorithm>
#include <cstring>

namespace gl {

namespace {

#pragma pack(push, 1)
struct BitmapInfoHeader32
{
    uint32_t size;
    int32_t width;
    int32_t height;
    uint16_t planes;
    uint16_t bitCount;
    uint32_t compression;
    uint32_t sizeImage;
    int32_t xPelsPerMeter;
    int32_t yPelsPerMeter;
    uint32_t clrUsed;
    uint32_t clrImportant;
};

struct BitmapInfo32
{
    BitmapInfoHeader32 header;
    uint32_t colors[3];
};
#pragma pack(pop)

long long NowMicros()
{
    static LARGE_INTEGER frequency = {};
    if (frequency.QuadPart == 0) QueryPerformanceFrequency(&frequency);
    LARGE_INTEGER counter = {};
    QueryPerformanceCounter(&counter);
    return counter.QuadPart * 1000000LL / frequency.QuadPart;
}

} // namespace

LiveCapture::~LiveCapture()
{
    Stop();
}

void LiveCapture::Start(HWND hwnd)
{
    if (started_) return;
    notify_ = hwnd;

    {
        std::lock_guard<std::mutex> lock(mutex_);
        free_ = {0, 1};
        readyIndex_ = -1;
        takenIndex_ = -1;
        requestPending_ = false;
    }

    running_ = true;
    thread_ = std::thread(&LiveCapture::WorkerLoop, this);
    started_ = true;
}

void LiveCapture::Stop()
{
    if (!started_) return;

    {
        std::lock_guard<std::mutex> lock(mutex_);
        running_ = false;
    }
    wake_.notify_all();

    if (thread_.joinable()) thread_.join();
    started_ = false;
    ReleaseGdi();
}

void LiveCapture::Submit(HWND hwnd, float dpi, double blurRadius)
{
    if (!started_) return;

    {
        std::lock_guard<std::mutex> lock(mutex_);
        requestHwnd_ = hwnd;
        requestDpi_ = dpi;
        requestBlur_ = blurRadius;
        requestPending_ = true;
    }
    wake_.notify_one();
}

bool LiveCapture::TakeReady(const uint32_t** pixels, int* width, int* height, POINT* origin)
{
    std::lock_guard<std::mutex> lock(mutex_);
    if (readyIndex_ < 0) return false;

    takenIndex_ = readyIndex_;
    readyIndex_ = -1;
    const Frame& frame = frames_[takenIndex_];
    *pixels = frame.pixels.data();
    *width = frame.width;
    *height = frame.height;
    *origin = frame.origin;
    return true;
}

void LiveCapture::DropReady()
{
    std::lock_guard<std::mutex> lock(mutex_);
    if (readyIndex_ >= 0)
    {
        free_.push_back(readyIndex_);
        readyIndex_ = -1;
    }
}

void LiveCapture::ReleaseBuffer()
{
    std::lock_guard<std::mutex> lock(mutex_);
    if (takenIndex_ >= 0)
    {
        free_.push_back(takenIndex_);
        takenIndex_ = -1;
    }
}

void LiveCapture::WorkerLoop()
{
    for (;;)
    {
        HWND hwnd = nullptr;
        float dpi = 1.0f;
        double blur = 12.0;
        int index = -1;

        {
            std::unique_lock<std::mutex> lock(mutex_);
            wake_.wait(lock, [this] { return !running_ || requestPending_; });
            if (!running_) break;

            requestPending_ = false;
            hwnd = requestHwnd_;
            dpi = requestDpi_;
            blur = requestBlur_;

            // 没有空闲缓冲：这一拍丢。注意不要因为"上一帧还没被 UI 取走"就停抓 ——
            // 那样工作线程会被 UI 的消费节奏限流（实测掉到 ~35fps）。
            if (free_.empty() || !hwnd)
            {
                continue;
            }

            index = free_.back();
            free_.pop_back();
        }

        Frame& frame = frames_[index];
        long long started = NowMicros();
        bool ok = CaptureInto(frame, hwnd, dpi, blur);
        int elapsedMs = (int)((NowMicros() - started) / 1000);
        if (elapsedMs == 0) elapsedMs = 1;

        {
            std::lock_guard<std::mutex> lock(mutex_);
            if (!ok)
            {
                free_.push_back(index);
                continue;
            }

            // 旧的就绪帧还没被取走：直接丢掉换新的（UI 要的永远是最新画面）
            if (readyIndex_ >= 0)
            {
                free_.push_back(readyIndex_);
            }

            readyIndex_ = index;
            lastGrabMs_ = elapsedMs;
            liveFrames_++;
        }

        PostMessageW(notify_, WM_APP_LIVE_CAPTURE, 0, 0);
    }
}

bool LiveCapture::CaptureInto(Frame& frame, HWND hwnd, float dpi, double blurRadius)
{
    RECT wr = {};
    if (!GetWindowRect(hwnd, &wr)) return false;

    int winW = wr.right - wr.left;
    int winH = wr.bottom - wr.top;
    if (winW <= 0 || winH <= 0) return false;

    int padPx = (int)(48.0f * dpi);
    int fullW = winW + padPx * 2;
    int fullH = winH + padPx * 2;
    int halfW = std::max(1, (fullW + 1) / 2);
    int halfH = std::max(1, (fullH + 1) / 2);

    if (!EnsureGdi(halfW, halfH)) return false;

    SetStretchBltMode(memoryDc_, COLORONCOLOR);
    SetBrushOrgEx(memoryDc_, 0, 0, nullptr);
    BOOL ok = StretchBlt(memoryDc_, 0, 0, halfW, halfH, screenDc_,
        wr.left - padPx, wr.top - padPx, fullW, fullH, SRCCOPY | CAPTUREBLT);
    GdiFlush();
    if (!ok) return false;

    frame.pixels.resize((size_t)halfW * halfH);
    memcpy(frame.pixels.data(), bits_, frame.pixels.size() * 4);
    for (auto& pixel : frame.pixels) pixel |= 0xFF000000u;

    int radius = std::max(1, (int)(blurRadius * dpi / 2.0 + 0.5));
    Backdrop::BoxBlur(frame.pixels, halfW, halfH, radius);

    frame.width = halfW;
    frame.height = halfH;
    frame.origin.x = wr.left;
    frame.origin.y = wr.top;
    return true;
}

bool LiveCapture::EnsureGdi(int width, int height)
{
    if (memoryDc_ && width == gdiWidth_ && height == gdiHeight_) return true;

    ReleaseGdi();

    screenDc_ = GetDC(nullptr);
    if (!screenDc_) return false;

    memoryDc_ = CreateCompatibleDC(screenDc_);
    if (!memoryDc_)
    {
        ReleaseGdi();
        return false;
    }

    BitmapInfo32 info = {};
    info.header.size = sizeof(BitmapInfoHeader32);
    info.header.width = width;
    info.header.height = -height;
    info.header.planes = 1;
    info.header.bitCount = 32;
    info.header.compression = BI_RGB;

    dib_ = CreateDIBSection(screenDc_, reinterpret_cast<BITMAPINFO*>(&info), DIB_RGB_COLORS, &bits_, nullptr, 0);
    if (!dib_ || !bits_)
    {
        ReleaseGdi();
        return false;
    }

    previous_ = SelectObject(memoryDc_, dib_);
    if (!previous_ || previous_ == (HGDIOBJ)-1)
    {
        ReleaseGdi();
        return false;
    }

    gdiWidth_ = width;
    gdiHeight_ = height;
    return true;
}

void LiveCapture::ReleaseGdi()
{
    if (memoryDc_ && previous_ && previous_ != (HGDIOBJ)-1)
    {
        SelectObject(memoryDc_, previous_);
    }
    if (dib_) { DeleteObject(dib_); dib_ = nullptr; }
    if (memoryDc_) { DeleteDC(memoryDc_); memoryDc_ = nullptr; }
    if (screenDc_) { ReleaseDC(nullptr, screenDc_); screenDc_ = nullptr; }
    bits_ = nullptr;
    previous_ = nullptr;
    gdiWidth_ = gdiHeight_ = 0;
}

} // namespace gl
