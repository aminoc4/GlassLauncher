#include "renderer.h"

#include <shlwapi.h>
#include <shellapi.h>
#include <cstdio>

namespace gl {

static D2D1_COLOR_F Rgba(unsigned r, unsigned g, unsigned b, unsigned a = 255)
{
    return D2D1::ColorF(r / 255.0f, g / 255.0f, b / 255.0f, a / 255.0f);
}

ThemeColors ThemeColors::Light()
{
    ThemeColors c;
    c.textPrimary = Rgba(0x1C, 0x1C, 0x1E);
    c.textSecondary = Rgba(0x6E, 0x6E, 0x73);
    c.textTertiary = Rgba(0x9A, 0x9A, 0x9F);
    c.wash = Rgba(0xFA, 0xFC, 0xFF, 0x73);
    c.panelSurface = Rgba(0xFF, 0xFF, 0xFF, 0x08);
    c.panelEdge = Rgba(0xFF, 0xFF, 0xFF, 0x80);
    c.iconPlate = Rgba(0xFF, 0xFF, 0xFF, 0xF2);
    c.iconPlateBorder = Rgba(0x00, 0x00, 0x00, 0x1F);
    c.searchSurface = Rgba(0x00, 0x00, 0x00, 0x2E);
    c.searchBorder = Rgba(0x00, 0x00, 0x00, 0x1F);
    c.searchGlyph = Rgba(0x00, 0x00, 0x00, 0x8A);
    c.hoverPlate = Rgba(0xFF, 0xFF, 0xFF, 0xF5);
    c.hoverBorder = Rgba(0x00, 0x00, 0x00, 0x33);
    c.initialText = Rgba(0x8A, 0x8A, 0x8F);
    c.windowEdge = Rgba(0xFF, 0xFF, 0xFF, 0xB3);
    c.specular = Rgba(0xFF, 0xFF, 0xFF, 0xD9);
    c.warning = Rgba(0xFF, 0xB0, 0x20);
    return c;
}

ThemeColors ThemeColors::Dark()
{
    ThemeColors c;
    c.textPrimary = Rgba(0xF2, 0xF2, 0xF7);
    c.textSecondary = Rgba(0xB8, 0xB8, 0xBF);
    c.textTertiary = Rgba(0x8E, 0x8E, 0x96);
    c.wash = Rgba(0x0F, 0x10, 0x14, 0x66);
    c.panelSurface = Rgba(0xFF, 0xFF, 0xFF, 0x06);
    c.panelEdge = Rgba(0xFF, 0xFF, 0xFF, 0x4D);
    c.iconPlate = Rgba(0xFF, 0xFF, 0xFF, 0x1A);
    c.iconPlateBorder = Rgba(0xFF, 0xFF, 0xFF, 0x26);
    c.searchSurface = Rgba(0xFF, 0xFF, 0xFF, 0x14);
    c.searchBorder = Rgba(0xFF, 0xFF, 0xFF, 0x22);
    c.searchGlyph = Rgba(0xFF, 0xFF, 0xFF, 0x99);
    c.hoverPlate = Rgba(0xFF, 0xFF, 0xFF, 0x26);
    c.hoverBorder = Rgba(0xFF, 0xFF, 0xFF, 0x40);
    c.initialText = Rgba(0xC8, 0xC8, 0xD0);
    c.windowEdge = Rgba(0xFF, 0xFF, 0xFF, 0x66);
    c.specular = Rgba(0xFF, 0xFF, 0xFF, 0x8C);
    c.warning = Rgba(0xFF, 0xB0, 0x20);
    return c;
}

bool Renderer::Initialize(HWND hwnd, float dpi)
{
    if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, &d2d_)))
    {
        return false;
    }

    if (FAILED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
            reinterpret_cast<IUnknown**>(&dwrite_))))
    {
        return false;
    }

    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(&wic_))))
    {
        return false;
    }

    RECT rc = {};
    GetClientRect(hwnd, &rc);
    D2D1_SIZE_U size = D2D1::SizeU(rc.right - rc.left, rc.bottom - rc.top);

    if (FAILED(d2d_->CreateHwndRenderTarget(
            D2D1::RenderTargetProperties(),
            D2D1::HwndRenderTargetProperties(hwnd, size),
            &rt_)))
    {
        return false;
    }

    // 不在 RT 上 SetDpi：改用绘制变换统一乘 DPI（等价且更可控）
    rt_->CreateSolidColorBrush(D2D1::ColorF(D2D1::ColorF::White), &brush_);

    nameFormat_ = MakeFormat(11.5f);
    if (nameFormat_)
    {
        // 名字单行 + 字符级省略号：条目名可能很长（系统组件），不能溢出到邻居格子上
        nameFormat_->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
        IDWriteInlineObject* ellipsis = nullptr;
        if (SUCCEEDED(dwrite_->CreateEllipsisTrimmingSign(nameFormat_, &ellipsis)))
        {
            DWRITE_TRIMMING trimming = {DWRITE_TRIMMING_GRANULARITY_CHARACTER, 0, 0};
            nameFormat_->SetTrimming(&trimming, ellipsis);
            ellipsis->Release();
        }
    }
    smallFormat_ = MakeFormat(11.0f);
    searchFormat_ = MakeFormat(12.5f);
    initialFormat_ = MakeFormat(21.0f, DWRITE_FONT_WEIGHT_SEMI_BOLD);
    glyphFormat_ = MakeFormat(12.5f, DWRITE_FONT_WEIGHT_NORMAL, L"Segoe MDL2 Assets");
    return true;
}

IDWriteTextFormat* Renderer::MakeFormat(float size, DWRITE_FONT_WEIGHT weight, const wchar_t* family)
{
    IDWriteTextFormat* format = nullptr;
    if (FAILED(dwrite_->CreateTextFormat(family, nullptr, weight,
            DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, size, L"zh-CN", &format)))
    {
        return nullptr;
    }

    format->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
    format->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING);
    format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
    return format;
}

void Renderer::Release()
{
    if (nameFormat_) { nameFormat_->Release(); nameFormat_ = nullptr; }
    if (smallFormat_) { smallFormat_->Release(); smallFormat_ = nullptr; }
    if (searchFormat_) { searchFormat_->Release(); searchFormat_ = nullptr; }
    if (initialFormat_) { initialFormat_->Release(); initialFormat_ = nullptr; }
    if (glyphFormat_) { glyphFormat_->Release(); glyphFormat_ = nullptr; }
    if (brush_) { brush_->Release(); brush_ = nullptr; }
    if (rt_) { rt_->Release(); rt_ = nullptr; }
    if (wic_) { wic_->Release(); wic_ = nullptr; }
    if (dwrite_) { dwrite_->Release(); dwrite_ = nullptr; }
    if (d2d_) { d2d_->Release(); d2d_ = nullptr; }
}

void Renderer::Resize(UINT widthPx, UINT heightPx, float dpi)
{
    if (!rt_) return;
    rt_->Resize(D2D1::SizeU(widthPx, heightPx));
}

void Renderer::SetBrush(const D2D1_COLOR_F& c)
{
    if (brush_) brush_->SetColor(c);
}

void Renderer::FillRect(const D2D1_RECT_F& r, const D2D1_COLOR_F& c)
{
    if (!rt_ || !brush_) return;
    SetBrush(c);
    rt_->FillRectangle(r, brush_);
}

void Renderer::FillRoundRect(const D2D1_RECT_F& r, float radius, const D2D1_COLOR_F& c)
{
    if (!rt_ || !brush_) return;
    SetBrush(c);
    rt_->FillRoundedRectangle(D2D1::RoundedRect(r, radius, radius), brush_);
}

void Renderer::StrokeRoundRect(const D2D1_RECT_F& r, float radius, const D2D1_COLOR_F& c, float width)
{
    if (!rt_ || !brush_) return;
    SetBrush(c);
    rt_->DrawRoundedRectangle(D2D1::RoundedRect(r, radius, radius), brush_, width);
}

void Renderer::FillVerticalGradient(const D2D1_RECT_F& r, const D2D1_COLOR_F& top, const D2D1_COLOR_F& bottom)
{
    if (!rt_) return;
    D2D1_GRADIENT_STOP stops[2] = {{0.0f, top}, {1.0f, bottom}};
    ID2D1GradientStopCollection* collection = nullptr;
    if (FAILED(rt_->CreateGradientStopCollection(stops, 2, &collection))) return;

    ID2D1LinearGradientBrush* gradient = nullptr;
    if (SUCCEEDED(rt_->CreateLinearGradientBrush(
            D2D1::LinearGradientBrushProperties(D2D1::Point2F(r.left, r.top), D2D1::Point2F(r.left, r.bottom)),
            collection, &gradient)))
    {
        rt_->FillRectangle(r, gradient);
        gradient->Release();
    }
    collection->Release();
}

void Renderer::DrawText(const std::wstring& text, const D2D1_RECT_F& r, IDWriteTextFormat* format,
                        const D2D1_COLOR_F& color, DWRITE_TEXT_ALIGNMENT align, bool clip)
{
    if (!rt_ || !format || text.empty()) return;
    SetBrush(color);
    format->SetTextAlignment(align);
    rt_->DrawText(text.c_str(), (UINT32)text.size(), format, r, brush_,
        clip ? D2D1_DRAW_TEXT_OPTIONS_CLIP : D2D1_DRAW_TEXT_OPTIONS_NONE);
}

void Renderer::DrawTextCentered(const std::wstring& text, const D2D1_RECT_F& r, IDWriteTextFormat* format,
                                const D2D1_COLOR_F& color)
{
    DrawText(text, r, format, color, DWRITE_TEXT_ALIGNMENT_CENTER);
}

ID2D1Bitmap* Renderer::CreateBitmapFromBgra(const void* pixels, UINT width, UINT height, UINT stride)
{
    if (!rt_) return nullptr;
    ID2D1Bitmap* bitmap = nullptr;
    D2D1_BITMAP_PROPERTIES props = D2D1::BitmapProperties(
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_IGNORE),
        96.0f, 96.0f);
    if (FAILED(rt_->CreateBitmap(D2D1::SizeU(width, height), pixels, stride, &props, &bitmap)))
    {
        return nullptr;
    }
    return bitmap;
}

ID2D1Bitmap* Renderer::LoadPng(const std::wstring& path)
{
    if (!wic_ || !rt_) return nullptr;

    IWICBitmapDecoder* decoder = nullptr;
    IWICBitmapFrameDecode* frame = nullptr;
    IWICFormatConverter* converter = nullptr;
    ID2D1Bitmap* bitmap = nullptr;

    do
    {
        if (FAILED(wic_->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ,
                WICDecodeMetadataCacheOnLoad, &decoder)))
        {
            break;
        }

        if (FAILED(decoder->GetFrame(0, &frame))) break;

        if (FAILED(wic_->CreateFormatConverter(&converter))) break;
        if (FAILED(converter->Initialize(frame, GUID_WICPixelFormat32bppPBGRA,
                WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom)))
        {
            break;
        }

        if (FAILED(rt_->CreateBitmapFromWicBitmap(converter, nullptr, &bitmap))) bitmap = nullptr;
    } while (false);

    if (converter) converter->Release();
    if (frame) frame->Release();
    if (decoder) decoder->Release();
    return bitmap;
}

ID2D1Bitmap* Renderer::LoadIconFromPath(const std::wstring& exePath)
{
    if (!wic_ || !rt_) return nullptr;

    HICON icon = nullptr;
    if (ExtractIconExW(exePath.c_str(), 0, &icon, nullptr, 1) == 0 || !icon)
    {
        return nullptr;
    }

    ICONINFO info = {};
    if (!GetIconInfo(icon, &info))
    {
        DestroyIcon(icon);
        return nullptr;
    }

    ID2D1Bitmap* bitmap = nullptr;
    IWICBitmap* wicBitmap = nullptr;
    if (info.hbmColor &&
        SUCCEEDED(wic_->CreateBitmapFromHBITMAP(info.hbmColor, nullptr,
            WICBitmapIgnoreAlpha, &wicBitmap)) &&
        wicBitmap)
    {
        rt_->CreateBitmapFromWicBitmap(wicBitmap, nullptr, &bitmap);
    }

    if (wicBitmap) wicBitmap->Release();
    if (info.hbmColor) DeleteObject(info.hbmColor);
    if (info.hbmMask) DeleteObject(info.hbmMask);
    DestroyIcon(icon);
    return bitmap;
}

} // namespace gl
