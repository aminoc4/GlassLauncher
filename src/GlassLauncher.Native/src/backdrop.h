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
    static void BoxBlur(std::vector<uint32_t>& pixels, int width, int height, int radius);

    // 两段式：窗口还没显示时先把像素抓下来（桌面干净），等渲染器就绪再上传成 D2D 位图
    bool CapturePixels(HWND hwnd, float dpi, double blurRadiusDip);
    bool Upload(Renderer& renderer);

    // 工作线程的实时帧：直接更新 D2D 位图（不保留 CPU 像素），并记录抓取时的窗口原点
    bool AdoptPixels(const uint32_t* pixels, int width, int height, int originX, int originY, Renderer& renderer);

    int OriginX() const { return originX_; }
    int OriginY() const { return originY_; }
    bool HasOrigin() const { return hasOrigin_; }
    bool DumpBmp(const std::wstring& path) const;

    void Release();

    ID2D1Bitmap* Bitmap() const { return bitmap_; }
    bool Valid() const { return bitmap_ != nullptr; }
    int WidthPx() const { return width_; }
    int HeightPx() const { return height_; }
    int LastBltMs() const { return lastBltMs_; }
    int LastBlurMs() const { return lastBlurMs_; }

private:
    static void BoxBlurPass(std::vector<uint32_t>& src, std::vector<uint32_t>& dst,
                            int width, int height, int radius, bool horizontal);

    std::vector<uint32_t> pixels_;
    int width_ = 0;
    int height_ = 0;
    int originX_ = 0;
    int originY_ = 0;
    bool hasOrigin_ = false;
    int lastBltMs_ = 0;
    int lastBlurMs_ = 0;
    ID2D1Bitmap* bitmap_ = nullptr;
};

} // namespace gl
