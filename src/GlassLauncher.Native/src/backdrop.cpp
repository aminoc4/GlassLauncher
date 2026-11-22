#include "backdrop.h"

#include <algorithm>

namespace gl {

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

void Backdrop::Release()
{
    if (bitmap_)
    {
        bitmap_->Release();
        bitmap_ = nullptr;
    }
    width_ = height_ = 0;
    pixels_.clear();
    pixels_.shrink_to_fit();
}

bool Backdrop::Capture(HWND hwnd, float dpi, double blurRadiusDip, Renderer& renderer)
{
    if (!CapturePixels(hwnd, dpi, blurRadiusDip)) return false;
    return Upload(renderer);
}

bool Backdrop::CapturePixels(HWND hwnd, float dpi, double blurRadiusDip)
{
    RECT wr = {};
    if (!GetWindowRect(hwnd, &wr)) return false;
    int winW = wr.right - wr.left;
    int winH = wr.bottom - wr.top;
    if (winW <= 0 || winH <= 0) return false;

    int padPx = (int)(PadDip * dpi);
    int fullW = winW + padPx * 2;
    int fullH = winH + padPx * 2;
    int halfW = std::max(1, (fullW + Downscale / 2) / Downscale);
    int halfH = std::max(1, (fullH + Downscale / 2) / Downscale);

    HDC screen = GetDC(nullptr);
    if (!screen) return false;

    HDC memory = CreateCompatibleDC(screen);
    if (!memory)
    {
        ReleaseDC(nullptr, screen);
        return false;
    }

    BitmapInfo32 info = {};
    info.header.size = sizeof(BitmapInfoHeader32);
    info.header.width = halfW;
    info.header.height = -halfH; // 自上而下
    info.header.planes = 1;
    info.header.bitCount = 32;
    info.header.compression = BI_RGB;

    void* bits = nullptr;
    HBITMAP dib = CreateDIBSection(screen, reinterpret_cast<BITMAPINFO*>(&info), DIB_RGB_COLORS, &bits, nullptr, 0);
    if (!dib || !bits)
    {
        if (dib) DeleteObject(dib);
        DeleteDC(memory);
        ReleaseDC(nullptr, screen);
        return false;
    }

    HGDIOBJ previous = SelectObject(memory, dib);
    // COLORONCOLOR（点采样）比 HALFTONE 快一个数量级；后面还要做 3 次模糊，
    // 点采样的锯齿会被完全盖掉，视觉上看不出差别。
    SetStretchBltMode(memory, COLORONCOLOR);
    SetBrushOrgEx(memory, 0, 0, nullptr);

    int srcX = wr.left - padPx;
    int srcY = wr.top - padPx;
    ULONGLONG t0 = GetTickCount64();
    BOOL ok = StretchBlt(memory, 0, 0, halfW, halfH, screen, srcX, srcY, fullW, fullH, SRCCOPY | CAPTUREBLT);
    GdiFlush();
    ULONGLONG t1 = GetTickCount64();

    if (ok)
    {
        pixels_.resize((size_t)halfW * halfH);
        memcpy(pixels_.data(), bits, pixels_.size() * 4);

        for (auto& px : pixels_)
        {
            px |= 0xFF000000u; // 不透明
        }

        int radius = std::max(1, (int)(blurRadiusDip * dpi / Downscale + 0.5));
        BoxBlur(pixels_, halfW, halfH, radius);

        width_ = halfW;
        height_ = halfH;
        originX_ = wr.left;
        originY_ = wr.top;
        hasOrigin_ = true;
        lastBltMs_ = (int)(t1 - t0);
        lastBlurMs_ = (int)(GetTickCount64() - t1);
    }

    SelectObject(memory, previous);
    DeleteObject(dib);
    DeleteDC(memory);
    ReleaseDC(nullptr, screen);
    return ok && !pixels_.empty();
}

bool Backdrop::Upload(Renderer& renderer)
{
    if (pixels_.empty() || width_ <= 0 || height_ <= 0) return false;

    // 同尺寸复用：拖动中的实时背景每帧都在更新，不能每帧新建 D2D 位图
    if (bitmap_)
    {
        D2D1_SIZE_U size = bitmap_->GetPixelSize();
        if ((int)size.width == width_ && (int)size.height == height_)
        {
            return SUCCEEDED(bitmap_->CopyFromMemory(nullptr, pixels_.data(), (UINT32)width_ * 4));
        }

        bitmap_->Release();
        bitmap_ = nullptr;
    }

    bitmap_ = renderer.CreateBitmapFromBgra(pixels_.data(), (UINT)width_, (UINT)height_, (UINT)width_ * 4);
    if (bitmap_)
    {
        // 像素已经进 D2D 纹理，CPU 侧这份 0.96MB 没理由常驻；下次静态抓帧会重新分配
        pixels_.clear();
        pixels_.shrink_to_fit();
    }
    return bitmap_ != nullptr;
}

bool Backdrop::AdoptPixels(const uint32_t* pixels, int width, int height, int originX, int originY, Renderer& renderer)
{
    if (!pixels || width <= 0 || height <= 0) return false;

    width_ = width;
    height_ = height;
    originX_ = originX;
    originY_ = originY;
    hasOrigin_ = true;

    if (bitmap_)
    {
        D2D1_SIZE_U size = bitmap_->GetPixelSize();
        if ((int)size.width == width && (int)size.height == height)
        {
            return SUCCEEDED(bitmap_->CopyFromMemory(nullptr, pixels, (UINT32)width * 4));
        }

        bitmap_->Release();
        bitmap_ = nullptr;
    }

    bitmap_ = renderer.CreateBitmapFromBgra(pixels, (UINT)width, (UINT)height, (UINT)width * 4);
    return bitmap_ != nullptr;
}

bool Backdrop::DumpBmp(const std::wstring& path) const
{
    if (pixels_.empty()) return false;
    FILE* f = _wfopen(path.c_str(), L"wb");
    if (!f) return false;

    BITMAPFILEHEADER fileHeader = {};
    BITMAPINFOHEADER info = {};
    info.biSize = sizeof(info);
    info.biWidth = width_;
    info.biHeight = -height_;
    info.biPlanes = 1;
    info.biBitCount = 32;
    info.biCompression = BI_RGB;
    info.biSizeImage = (DWORD)(pixels_.size() * 4);

    fileHeader.bfType = 0x4D42; // BM
    fileHeader.bfOffBits = sizeof(fileHeader) + sizeof(info);
    fileHeader.bfSize = fileHeader.bfOffBits + info.biSizeImage;

    fwrite(&fileHeader, sizeof(fileHeader), 1, f);
    fwrite(&info, sizeof(info), 1, f);
    fwrite(pixels_.data(), 4, pixels_.size(), f);
    fclose(f);
    return true;
}

void Backdrop::BoxBlur(std::vector<uint32_t>& pixels, int width, int height, int radius)
{
    if (radius <= 0) return;
    std::vector<uint32_t> temp(pixels.size());
    for (int pass = 0; pass < 3; pass++)
    {
        BoxBlurPass(pixels, temp, width, height, radius, true);
        BoxBlurPass(temp, pixels, width, height, radius, false);
    }
}

void Backdrop::BoxBlurPass(std::vector<uint32_t>& src, std::vector<uint32_t>& dst,
                           int width, int height, int radius, bool horizontal)
{
    const int diameter = radius * 2 + 1;
    if (horizontal)
    {
        for (int y = 0; y < height; y++)
        {
            const uint32_t* row = src.data() + (size_t)y * width;
            uint32_t* out = dst.data() + (size_t)y * width;
            int r = 0, g = 0, b = 0;
            for (int i = -radius; i <= radius; i++)
            {
                int x = std::clamp(i, 0, width - 1);
                uint32_t p = row[x];
                b += p & 0xFF; g += (p >> 8) & 0xFF; r += (p >> 16) & 0xFF;
            }
            for (int x = 0; x < width; x++)
            {
                out[x] = 0xFF000000u | ((uint32_t)(r / diameter) << 16) | ((uint32_t)(g / diameter) << 8) | (uint32_t)(b / diameter);
                int addX = std::clamp(x + radius + 1, 0, width - 1);
                int subX = std::clamp(x - radius, 0, width - 1);
                uint32_t add = row[addX];
                uint32_t sub = row[subX];
                b += (add & 0xFF) - (sub & 0xFF);
                g += ((add >> 8) & 0xFF) - ((sub >> 8) & 0xFF);
                r += ((add >> 16) & 0xFF) - ((sub >> 16) & 0xFF);
            }
        }
    }
    else
    {
        for (int x = 0; x < width; x++)
        {
            int r = 0, g = 0, b = 0;
            for (int i = -radius; i <= radius; i++)
            {
                int y = std::clamp(i, 0, height - 1);
                uint32_t p = src[(size_t)y * width + x];
                b += p & 0xFF; g += (p >> 8) & 0xFF; r += (p >> 16) & 0xFF;
            }
            for (int y = 0; y < height; y++)
            {
                dst[(size_t)y * width + x] = 0xFF000000u | ((uint32_t)(r / diameter) << 16) | ((uint32_t)(g / diameter) << 8) | (uint32_t)(b / diameter);
                int addY = std::clamp(y + radius + 1, 0, height - 1);
                int subY = std::clamp(y - radius, 0, height - 1);
                uint32_t add = src[(size_t)addY * width + x];
                uint32_t sub = src[(size_t)subY * width + x];
                b += (add & 0xFF) - (sub & 0xFF);
                g += ((add >> 8) & 0xFF) - ((sub >> 8) & 0xFF);
                r += ((add >> 16) & 0xFF) - ((sub >> 16) & 0xFF);
            }
        }
    }
}

} // namespace gl
