#include "textures.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>

#include <cstring>
#include <string>
#include <unordered_map>
#include <unordered_set>

#include "gdi_font.h"
#include "imgui_internal.h"  // RegisterUserTexture: the documented way in, marked experimental

namespace overlay {
namespace {

// The descriptor heap holds 1024; the font atlas takes a few. A host sending more images
// than this is sending something other than interface art, and the surplus is refused
// rather than allowed to run the heap dry - an exhausted heap draws the wrong image.
constexpr size_t kMaxTextures = 900;

struct Entry {
    ImTextureData* data = nullptr;
    Texture texture;
};

// Keyed by the image key. std::string rather than string_view keys: the key must outlive
// the frame the image arrived in.
std::unordered_map<std::string, Entry>& Live() {
    static std::unordered_map<std::string, Entry> live;
    return live;
}

std::vector<ImTextureData*>& Retiring() {
    static std::vector<ImTextureData*> retiring;
    return retiring;
}

DecalFonts& Fonts() {
    static DecalFonts fonts;
    return fonts;
}

// Keys asked for and not had: in order of first asking, and the set of everything asked of
// the host on this connection so nothing is asked twice.
std::vector<std::string>& Wanted() {
    static std::vector<std::string> wanted;
    return wanted;
}

std::unordered_set<std::string>& Asked() {
    static std::unordered_set<std::string> asked;
    return asked;
}

// Only keys the host knows how to resolve are worth asking for; anything else is a view's
// typo, and asking would only fill the host's log.
bool Resolvable(std::string_view key) {
    for (std::string_view prefix : {"portal:", "vvs:", "decal:", "bar:"}) {
        if (key.size() > prefix.size() && key.substr(0, prefix.size()) == prefix) return true;
    }
    return false;
}

void Retire(ImTextureData* data) {
    if (data == nullptr) return;

    // Destroyed next frame, by the backend, once no frame in flight can still be drawing
    // with it. Freed here only after that has happened.
    data->WantDestroyNextFrame = true;
    Retiring().push_back(data);
}

void Free(ImTextureData* data) {
    ImGui::UnregisterUserTexture(data);
    IM_DELETE(data);
}

// What joins a host image's key to the key of a texture made from it, so the made ones can
// be found and dropped when the host's image is replaced.
constexpr std::string_view kMadeFrom = "#";

// A texture under `key` of `width` by `height` RGBA pixels, replacing any there was. Null
// when the heap is full.
const Texture* Store(const std::string& key, int width, int height, const unsigned char* rgba) {
    auto& live = Live();
    auto found = live.find(key);
    if (found == live.end() && live.size() >= kMaxTextures) return nullptr;

    ImTextureData* data = IM_NEW(ImTextureData)();
    data->Create(ImTextureFormat_RGBA32, width, height);
    std::memcpy(data->GetPixels(), rgba, static_cast<size_t>(width) * static_cast<size_t>(height) * 4);
    data->UseColors = true;

    // The backend destroys, at shutdown, only textures whose count of owning contexts
    // is one. ImGui sets that for its own font atlas and leaves user textures at zero,
    // which would leave every one of ours on the GPU after an unload.
    data->RefCount = 1;
    ImGui::RegisterUserTexture(data);

    if (found != live.end()) {
        Retire(found->second.data);
        found->second.data = data;
    } else {
        found = live.emplace(key, Entry{}).first;
        found->second.data = data;
    }

    found->second.texture.ref = data->GetTexRef();
    found->second.texture.width = static_cast<float>(width);
    found->second.texture.height = static_cast<float>(height);
    return &found->second.texture;
}

// Drops every texture made from the host's image under `key`: it has been replaced, and
// they are remade from the new one when next asked for.
void ForgetMadeFrom(const std::string& key) {
    const std::string prefix = key + std::string(kMadeFrom);
    auto& live = Live();
    for (auto it = live.begin(); it != live.end();) {
        if (it->first.size() > prefix.size() && it->first.compare(0, prefix.size(), prefix) == 0) {
            Retire(it->second.data);
            it = live.erase(it);
        } else {
            ++it;
        }
    }
}

void PutPixel(unsigned char* at, ImU32 colour) {
    at[0] = static_cast<unsigned char>((colour >> IM_COL32_R_SHIFT) & 0xFF);
    at[1] = static_cast<unsigned char>((colour >> IM_COL32_G_SHIFT) & 0xFF);
    at[2] = static_cast<unsigned char>((colour >> IM_COL32_B_SHIFT) & 0xFF);
    at[3] = static_cast<unsigned char>((colour >> IM_COL32_A_SHIFT) & 0xFF);
}

std::string Hex(ImU32 value) {
    static const char digits[] = "0123456789ABCDEF";
    std::string out(8, '0');
    for (int i = 7; i >= 0; --i, value >>= 4)
        out[static_cast<size_t>(i)] = digits[value & 0xF];
    return out;
}

}  // namespace

void AcceptImages(std::vector<ImagePixels> images) {
    if (ImGui::GetCurrentContext() == nullptr) return;

    for (ImagePixels& image : images) {
        const size_t bytes = static_cast<size_t>(image.width) * static_cast<size_t>(image.height) * 4;
        if (image.width <= 0 || image.height <= 0 || image.rgba.size() != bytes) continue;
        if (Store(image.key, image.width, image.height, image.rgba.data()) != nullptr)
            ForgetMadeFrom(image.key);
    }
}

const Texture* FindOutlinedTexture(std::string_view key, ImU32 glyph, ImU32 outline) {
    if (key.empty()) return nullptr;
    const std::string made = std::string(key) + std::string(kMadeFrom) + Hex(glyph) + Hex(outline);
    auto& live = Live();
    if (const auto found = live.find(made); found != live.end()) return &found->second.texture;

    // The host's image, asked for if it has not come.
    if (FindTexture(key) == nullptr) return nullptr;
    const Entry& source = live.find(std::string(key))->second;
    const unsigned char* in = static_cast<const unsigned char*>(source.data->GetPixels());
    const int w = source.data->Width;
    const int h = source.data->Height;
    if (in == nullptr || w <= 0 || h <= 0) return nullptr;

    // As b.a did it: black only when exactly opaque black.
    auto black = [&](int x, int y) {
        if (x < 0 || y < 0 || x >= w || y >= h) return false;
        const unsigned char* p = in + (static_cast<size_t>(y) * static_cast<size_t>(w) + static_cast<size_t>(x)) * 4;
        return p[0] == 0 && p[1] == 0 && p[2] == 0 && p[3] == 0xFF;
    };
    std::vector<unsigned char> out(static_cast<size_t>(w) * static_cast<size_t>(h) * 4, 0);
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            unsigned char* at = out.data() + (static_cast<size_t>(y) * static_cast<size_t>(w) + static_cast<size_t>(x)) * 4;
            if (black(x, y)) {
                PutPixel(at, glyph);
                continue;
            }
            bool touching = false;
            for (int dy = -1; dy <= 1 && !touching; ++dy)
                for (int dx = -1; dx <= 1 && !touching; ++dx)
                    touching = (dx != 0 || dy != 0) && black(x + dx, y + dy);
            if (touching) PutPixel(at, outline);
        }
    }
    return Store(made, w, h, out.data());
}

const Texture* FindCheckerTexture(ImU32 even, ImU32 odd, int width, int height) {
    if (width <= 0 || height <= 0) return nullptr;
    const std::string made = "made:checker" + std::string(kMadeFrom) + Hex(even) + Hex(odd) + "#" + std::to_string(width) + "x" +
                             std::to_string(height);
    auto& live = Live();
    if (const auto found = live.find(made); found != live.end()) return &found->second.texture;

    std::vector<unsigned char> out(static_cast<size_t>(width) * static_cast<size_t>(height) * 4, 0);
    for (int y = 0; y < height; ++y)
        for (int x = 0; x < width; ++x)
            PutPixel(out.data() + (static_cast<size_t>(y) * static_cast<size_t>(width) + static_cast<size_t>(x)) * 4,
                     (x + y) % 2 == 0 ? even : odd);
    return Store(made, width, height, out.data());
}

const Texture* FindTexture(std::string_view key) {
    if (key.empty()) return nullptr;
    auto& live = Live();
    std::string owned(key);
    const auto found = live.find(owned);
    if (found != live.end()) return &found->second.texture;

    if (Resolvable(key) && Asked().insert(owned).second)
        Wanted().push_back(std::move(owned));
    return nullptr;
}

std::vector<std::string> TakeWantedImageKeys(size_t limit) {
    auto& wanted = Wanted();
    const size_t count = wanted.size() < limit ? wanted.size() : limit;
    std::vector<std::string> taken(wanted.begin(), wanted.begin() + static_cast<std::ptrdiff_t>(count));
    wanted.erase(wanted.begin(), wanted.begin() + static_cast<std::ptrdiff_t>(count));
    return taken;
}

void ForgetWantedImageKeys() {
    Wanted().clear();
    Asked().clear();
}

size_t TextureCount() { return Live().size(); }

void CollectRetiredTextures() {
    auto& retiring = Retiring();
    for (size_t i = 0; i < retiring.size();) {
        if (retiring[i]->Status == ImTextureStatus_Destroyed) {
            Free(retiring[i]);
            retiring[i] = retiring.back();
            retiring.pop_back();
        } else {
            ++i;
        }
    }
}

void ReleaseAllTextures() {
    if (ImGui::GetCurrentContext() == nullptr) {
        Live().clear();
        Retiring().clear();
        return;
    }

    for (auto& [key, entry] : Live())
        Free(entry.data);
    Live().clear();

    for (ImTextureData* data : Retiring())
        Free(data);
    Retiring().clear();

    Fonts() = DecalFonts{};
}

bool LoadDecalFonts(ImFontAtlas* atlas) {
    wchar_t windows[MAX_PATH] = {};
    const UINT length = GetWindowsDirectoryW(windows, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return false;

    auto load = [&](const wchar_t* file) -> ImFont* {
        std::wstring path = std::wstring(windows) + L"\\Fonts\\" + file;

        // ImGui takes a narrow path and opens it with its own UTF-8-aware fopen; the
        // Windows folder is plain ASCII on every install worth supporting, but converting
        // properly costs nothing.
        const int needed = WideCharToMultiByte(CP_UTF8, 0, path.c_str(), -1, nullptr, 0, nullptr, nullptr);
        if (needed <= 0) return nullptr;
        std::string utf8(static_cast<size_t>(needed), '\0');
        WideCharToMultiByte(CP_UTF8, 0, path.c_str(), -1, utf8.data(), needed, nullptr, nullptr);
        utf8.resize(static_cast<size_t>(needed - 1));

        if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES) return nullptr;

        ImFontConfig config;
        config.OversampleH = 2;
        return atlas->AddFontFromFileTTF(utf8.c_str(), 16.0f, &config);
    };

    // Drawn by GDI, one bit per pixel and hinted, as VVS drew them; see gdi_font.h. The
    // font files are the fallback, anti-aliased by ImGui - legible, but visibly not the same.
    Fonts().regular = AddGdiFont(atlas, "Times New Roman", FW_NORMAL);
    Fonts().bold = AddGdiFont(atlas, "Times New Roman", FW_BOLD);

    if (Fonts().regular == nullptr)
        Fonts().regular = load(L"times.ttf");
    if (Fonts().bold == nullptr)
        Fonts().bold = load(L"timesbd.ttf");

    Fonts().verdana = AddGdiFont(atlas, "Verdana", FW_NORMAL);
    Fonts().verdana_bold = AddGdiFont(atlas, "Verdana", FW_BOLD);
    Fonts().palatino = AddGdiFont(atlas, "Palatino Linotype", FW_NORMAL);
    if (Fonts().verdana == nullptr)
        Fonts().verdana = load(L"verdana.ttf");
    if (Fonts().verdana_bold == nullptr)
        Fonts().verdana_bold = load(L"verdanab.ttf");
    if (Fonts().palatino == nullptr)
        Fonts().palatino = load(L"pala.ttf");

    return Fonts().regular != nullptr && Fonts().bold != nullptr;
}

const DecalFonts& GetDecalFonts() { return Fonts(); }

}  // namespace overlay
