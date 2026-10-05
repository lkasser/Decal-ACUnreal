#include "gdi_font.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <vector>

#include "imgui.h"
#include "imgui_internal.h"  // the font loader interface and the atlas packer

namespace overlay {
namespace {

// Per face: a memory DC to select fonts into, and one font at a fixed size for asking
// which characters the face has.
struct SourceData {
    HDC dc = nullptr;
    HFONT probe = nullptr;
};

// Per face per size: the GDI font at that size. The atlas owns this memory and hands it
// over zeroed; it is only ever a handle.
struct BakedData {
    HFONT font;
};

float Density(const ImFontConfig* src, const ImFontBaked* baked) {
    return src->RasterizerDensity * baked->RasterizerDensity;
}

HFONT MakeFont(const ImFontConfig* src, int em_pixels) {
    LOGFONTW font{};
    font.lfHeight = -std::max(1, em_pixels);  // negative: the em, not the whole cell
    font.lfWeight = src->FontLoaderFlags != 0 ? static_cast<LONG>(src->FontLoaderFlags) : FW_NORMAL;
    font.lfCharSet = DEFAULT_CHARSET;
    font.lfOutPrecision = OUT_TT_ONLY_PRECIS;
    font.lfQuality = NONANTIALIASED_QUALITY;
    MultiByteToWideChar(CP_UTF8, 0, src->Name, -1, font.lfFaceName, LF_FACESIZE);
    return CreateFontIndirectW(&font);
}

bool SourceInit(ImFontAtlas*, ImFontConfig* src) {
    SourceData* data = IM_NEW(SourceData)();
    data->dc = CreateCompatibleDC(nullptr);
    data->probe = MakeFont(src, 16);

    if (data->dc == nullptr || data->probe == nullptr) {
        if (data->probe != nullptr) DeleteObject(data->probe);
        if (data->dc != nullptr) DeleteDC(data->dc);
        IM_DELETE(data);
        return false;
    }

    src->FontLoaderData = data;
    return true;
}

void SourceDestroy(ImFontAtlas*, ImFontConfig* src) {
    SourceData* data = static_cast<SourceData*>(src->FontLoaderData);
    if (data == nullptr) return;
    DeleteObject(data->probe);
    DeleteDC(data->dc);
    IM_DELETE(data);
    src->FontLoaderData = nullptr;
}

bool HasGlyph(SourceData* data, HFONT font, ImWchar codepoint) {
    const HGDIOBJ previous = SelectObject(data->dc, font);
    WCHAR character = static_cast<WCHAR>(codepoint);
    WORD index = 0xFFFF;
    const DWORD found = GetGlyphIndicesW(data->dc, &character, 1, &index, GGI_MARK_NONEXISTING_GLYPHS);
    SelectObject(data->dc, previous);
    return found != GDI_ERROR && index != 0xFFFF;
}

bool SourceContainsGlyph(ImFontAtlas*, ImFontConfig* src, ImWchar codepoint) {
    SourceData* data = static_cast<SourceData*>(src->FontLoaderData);
    return data != nullptr && HasGlyph(data, data->probe, codepoint);
}

bool BakedInit(ImFontAtlas*, ImFontConfig* src, ImFontBaked* baked, void* loader_data) {
    SourceData* data = static_cast<SourceData*>(src->FontLoaderData);
    BakedData* bake = static_cast<BakedData*>(loader_data);
    const float density = Density(src, baked);

    bake->font = MakeFont(src, static_cast<int>(std::lround(baked->Size * density)));
    if (bake->font == nullptr) return false;

    if (!src->MergeMode) {
        TEXTMETRICW metrics{};
        const HGDIOBJ previous = SelectObject(data->dc, bake->font);
        GetTextMetricsW(data->dc, &metrics);
        SelectObject(data->dc, previous);

        baked->Ascent = static_cast<float>(metrics.tmAscent) / density;
        baked->Descent = -static_cast<float>(metrics.tmDescent) / density;
    }

    return true;
}

void BakedDestroy(ImFontAtlas*, ImFontConfig*, ImFontBaked*, void* loader_data) {
    BakedData* bake = static_cast<BakedData*>(loader_data);
    if (bake->font != nullptr) DeleteObject(bake->font);
    bake->font = nullptr;
}

bool BakedLoadGlyph(ImFontAtlas* atlas, ImFontConfig* src, ImFontBaked* baked, void* loader_data, ImWchar codepoint,
                    ImFontGlyph* out_glyph, float* out_advance_x) {
    SourceData* data = static_cast<SourceData*>(src->FontLoaderData);
    BakedData* bake = static_cast<BakedData*>(loader_data);
    if (data == nullptr || bake->font == nullptr || !HasGlyph(data, bake->font, codepoint)) return false;

    const float density = Density(src, baked);
    const MAT2 identity = {{0, 1}, {0, 0}, {0, 0}, {0, 1}};

    const HGDIOBJ previous = SelectObject(data->dc, bake->font);
    GLYPHMETRICS metrics{};
    const DWORD size = GetGlyphOutlineW(data->dc, codepoint, GGO_BITMAP, &metrics, 0, nullptr, &identity);
    if (size == GDI_ERROR) {
        SelectObject(data->dc, previous);
        return false;
    }

    const float advance = static_cast<float>(metrics.gmCellIncX) / density;

    if (out_advance_x != nullptr) {
        *out_advance_x = advance;
        SelectObject(data->dc, previous);
        return true;
    }

    out_glyph->Codepoint = codepoint;
    out_glyph->AdvanceX = advance;

    const int w = static_cast<int>(metrics.gmBlackBoxX);
    const int h = static_cast<int>(metrics.gmBlackBoxY);

    // A space has an advance and no pixels; GDI reports a one-pixel box and no bitmap.
    const int pitch = ((w + 31) / 32) * 4;  // one bit per pixel, rows padded to four bytes
    if (size > 0 && w > 0 && h > 0 && size >= static_cast<DWORD>(pitch * h)) {
        std::vector<BYTE> bits(size);
        GetGlyphOutlineW(data->dc, codepoint, GGO_BITMAP, &metrics, size, bits.data(), &identity);

        const ImFontAtlasRectId pack = ImFontAtlasPackAddRect(atlas, w, h);
        if (pack == ImFontAtlasRectId_Invalid) {
            SelectObject(data->dc, previous);
            return false;
        }
        ImTextureRect* rect = ImFontAtlasPackGetRect(atlas, pack);

        // One bit per pixel into the atlas's one byte per pixel: set bits fully opaque, the
        // rest clear. No grey at all, which is the whole difference from ImGui's own text.
        ImFontAtlasBuilder* builder = atlas->Builder;
        builder->TempBuffer.resize(w * h);
        unsigned char* alpha = builder->TempBuffer.Data;
        for (int y = 0; y < h; ++y) {
            const BYTE* row = bits.data() + static_cast<size_t>(y) * static_cast<size_t>(pitch);
            for (int x = 0; x < w; ++x)
                alpha[y * w + x] = (row[x >> 3] & (0x80 >> (x & 7))) != 0 ? 255 : 0;
        }

        // GDI places the box relative to the baseline, y upward; ImGui wants it relative to
        // the top of the line, y downward. Whole pixels, so the glyph lands on the grid.
        const float ascent = std::round(baked->Ascent * density);
        const float x0 = static_cast<float>(metrics.gmptGlyphOrigin.x);
        const float y0 = ascent - static_cast<float>(metrics.gmptGlyphOrigin.y);

        out_glyph->X0 = x0 / density;
        out_glyph->Y0 = y0 / density;
        out_glyph->X1 = (x0 + static_cast<float>(w)) / density;
        out_glyph->Y1 = (y0 + static_cast<float>(h)) / density;
        out_glyph->Visible = true;
        out_glyph->PackId = pack;
        ImFontAtlasBakedSetFontGlyphBitmap(atlas, baked, src, out_glyph, rect, alpha, ImTextureFormat_Alpha8, w);
    }

    SelectObject(data->dc, previous);
    return true;
}

const ImFontLoader* GdiLoader() {
    static ImFontLoader loader = [] {
        ImFontLoader l;
        l.Name = "gdi";
        l.FontSrcInit = SourceInit;
        l.FontSrcDestroy = SourceDestroy;
        l.FontSrcContainsGlyph = SourceContainsGlyph;
        l.FontBakedInit = BakedInit;
        l.FontBakedDestroy = BakedDestroy;
        l.FontBakedLoadGlyph = BakedLoadGlyph;
        l.FontBakedSrcLoaderDataSize = sizeof(BakedData);
        return l;
    }();
    return &loader;
}

}  // namespace

ImFont* AddGdiFont(ImFontAtlas* atlas, const char* face, int weight) {
    // Windows substitutes a similar face for one it does not have, which would draw a
    // different font under the right name. Asked first, so a missing face is reported.
    HDC dc = CreateCompatibleDC(nullptr);
    if (dc == nullptr) return nullptr;

    LOGFONTW wanted{};
    wanted.lfHeight = -16;
    wanted.lfCharSet = DEFAULT_CHARSET;
    MultiByteToWideChar(CP_UTF8, 0, face, -1, wanted.lfFaceName, LF_FACESIZE);
    HFONT probe = CreateFontIndirectW(&wanted);
    wchar_t got[LF_FACESIZE] = {};
    if (probe != nullptr) {
        const HGDIOBJ previous = SelectObject(dc, probe);
        GetTextFaceW(dc, LF_FACESIZE, got);
        SelectObject(dc, previous);
        DeleteObject(probe);
    }
    DeleteDC(dc);
    if (_wcsicmp(got, wanted.lfFaceName) != 0) return nullptr;

    ImFontConfig config;
    config.FontLoader = GdiLoader();
    config.FontLoaderFlags = static_cast<unsigned int>(weight);
    config.SizePixels = 16.0f;
    ImStrncpy(config.Name, face, IM_ARRAYSIZE(config.Name));
    return atlas->AddFont(&config);
}

}  // namespace overlay
