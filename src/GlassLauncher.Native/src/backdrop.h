// 背景：抓取窗口下方桌面 → 半分辨率 → 多次盒式模糊 → D2D 位图。
// 与 WPF 版同一套几何：抓取区域 = 窗口四周各扩 48 DIP，位图按 2 倍降采样。
#pragma once

#include <windows.h>
#include <d2d1.h>
#include <vector>
#include <cstdint>
#include "renderer.h"

namespace gl {

class Backdrop
{
public:
    static constexpr int PadDip = 48;
    static constexpr int Downscale = 2;

    bool Capture(HWND hwnd, float dpi, double blurRadiusDip, Renderer& renderer);

    // 两段式：窗口还没显示时先把像素抓下来（桌面干净），等渲染器就绪再上传成 D2D 位图
    bool CapturePixels(HWND hwnd, float dpi, double blurRadiusDip);
    bool Upload(Renderer& renderer);
    bool DumpBmp(const std::wstring& path) const;

    void Release();

    ID2D1Bitmap* Bitmap() const { return bitmap_; }
    bool Valid() const { return bitmap_ != nullptr; }
    int WidthPx() const { return width_; }
    int HeightPx() const { return height_; }
    int LastBltMs() const { return lastBltMs_; }
    int LastBlurMs() const { return lastBlurMs_; }

private:
    static void BoxBlur(std::vector<uint32_t>& pixels, int width, int height, int radius);
    static void BoxBlurPass(std::vector<uint32_t>& src, std::vector<uint32_t>& dst,
                            int width, int height, int radius, bool horizontal);

    std::vector<uint32_t> pixels_;
    int width_ = 0;
    int height_ = 0;
    int lastBltMs_ = 0;
    int lastBlurMs_ = 0;
    ID2D1Bitmap* bitmap_ = nullptr;
};

} // namespace gl
