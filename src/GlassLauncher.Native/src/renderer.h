// Direct2D / DirectWrite 封装：所有绘制都用 DIP 坐标（RT 上设置 DPI）。
#pragma once

#include <windows.h>
#include <d2d1.h>
#include <dwrite.h>
#include <wincodec.h>
#include <string>
#include <vector>

namespace gl {

struct ThemeColors
{
    D2D1_COLOR_F textPrimary;
    D2D1_COLOR_F textSecondary;
    D2D1_COLOR_F textTertiary;
    D2D1_COLOR_F wash;          // 白纱/深纱
    D2D1_COLOR_F panelSurface;
    D2D1_COLOR_F panelEdge;
    D2D1_COLOR_F iconPlate;
    D2D1_COLOR_F iconPlateBorder;
    D2D1_COLOR_F searchSurface;
    D2D1_COLOR_F searchBorder;
    D2D1_COLOR_F searchGlyph;
    D2D1_COLOR_F hoverPlate;
    D2D1_COLOR_F hoverBorder;
    D2D1_COLOR_F initialText;
    D2D1_COLOR_F windowEdge;
    D2D1_COLOR_F specular;
    D2D1_COLOR_F warning;

    static ThemeColors Light();
    static ThemeColors Dark();
};

class Renderer
{
public:
    bool Initialize(HWND hwnd, float dpi);
    void Resize(UINT widthPx, UINT heightPx, float dpi);
    void Release();

    ID2D1HwndRenderTarget* Target() const { return rt_; }
    IDWriteFactory* DWrite() const { return dwrite_; }
    IWICImagingFactory* Wic() const { return wic_; }

    // 基础图元（DIP 坐标）
    void FillRect(const D2D1_RECT_F& r, const D2D1_COLOR_F& c);
    void FillRoundRect(const D2D1_RECT_F& r, float radius, const D2D1_COLOR_F& c);
    void FillEllipse(const D2D1_ELLIPSE& e, const D2D1_COLOR_F& c);
    void StrokeRoundRect(const D2D1_RECT_F& r, float radius, const D2D1_COLOR_F& c, float width);
    void StrokeRoundRectGradient(const D2D1_RECT_F& r, float radius,
                                 const D2D1_GRADIENT_STOP* stops, UINT32 count, float width,
                                 bool diagonal);
    void FillRectGradient(const D2D1_RECT_F& r, const D2D1_GRADIENT_STOP* stops, UINT32 count,
                          bool horizontal);
    void FillVerticalGradient(const D2D1_RECT_F& r, const D2D1_COLOR_F& top, const D2D1_COLOR_F& bottom);
    void DrawText(const std::wstring& text, const D2D1_RECT_F& r, IDWriteTextFormat* format,
                  const D2D1_COLOR_F& color, DWRITE_TEXT_ALIGNMENT align = DWRITE_TEXT_ALIGNMENT_LEADING,
                  bool clip = false);
    void DrawTextCentered(const std::wstring& text, const D2D1_RECT_F& r, IDWriteTextFormat* format,
                          const D2D1_COLOR_F& color);

    ID2D1Bitmap* CreateBitmapFromBgra(const void* pixels, UINT width, UINT height, UINT stride);
    ID2D1Bitmap* LoadPng(const std::wstring& path);
    ID2D1Bitmap* LoadIconFromPath(const std::wstring& exePath);

    IDWriteTextFormat* NameFormat() const { return nameFormat_; }
    IDWriteTextFormat* SmallFormat() const { return smallFormat_; }
    IDWriteTextFormat* SearchFormat() const { return searchFormat_; }
    IDWriteTextFormat* InitialFormat() const { return initialFormat_; }
    IDWriteTextFormat* GlyphFormat() const { return glyphFormat_; }

private:
    ID2D1Factory* d2d_ = nullptr;
    IDWriteFactory* dwrite_ = nullptr;
    IWICImagingFactory* wic_ = nullptr;
    ID2D1HwndRenderTarget* rt_ = nullptr;
    ID2D1SolidColorBrush* brush_ = nullptr;

    IDWriteTextFormat* nameFormat_ = nullptr;
    IDWriteTextFormat* smallFormat_ = nullptr;
    IDWriteTextFormat* searchFormat_ = nullptr;
    IDWriteTextFormat* initialFormat_ = nullptr;
    IDWriteTextFormat* glyphFormat_ = nullptr;

    IDWriteTextFormat* MakeFormat(float size, DWRITE_FONT_WEIGHT weight = DWRITE_FONT_WEIGHT_NORMAL,
                                   const wchar_t* family = L"Microsoft YaHei UI");
    void SetBrush(const D2D1_COLOR_F& c);
};

} // namespace gl
