// Images from the host, as textures the drawing code can use by key.
//
// The host sends each image once per connection - the client's own interface art out of
// portal.dat, the theme images Virindi View Service and Decal ship - and this turns them
// into ImGui user textures, which the DX12 backend uploads the next time it renders. The
// drawing code asks for a key ("portal:0600126F") and gets a texture, or nothing if that
// image has not arrived, in which case it draws a plain stand-in rather than waiting.
//
// Everything here belongs to the render thread: it is the only thread that owns the ImGui
// context, and the only one that may create or retire textures.

#pragma once

#include <string>
#include <string_view>
#include <vector>

#include "imgui.h"
#include "overlay_state.h"

namespace overlay {

struct Texture {
    ImTextureRef ref;
    float width = 0.0f;   // the image's own pixels
    float height = 0.0f;
};

// Makes textures of newly arrived images. An image for a key that already has a texture
// replaces it: the old one is retired, not freed, since the frame in flight may use it.
void AcceptImages(std::vector<ImagePixels> images);

// The texture for a key, or null. The pointer is good until the next AcceptImages.
const Texture* FindTexture(std::string_view key);

// A host image redrawn as Virindi View Service redrew the glyphs of its Minimalist themes
// (its b.a(bitmap, glyph, outline)): every opaque black pixel in `glyph`, every pixel that
// touches one of those, across or diagonally, in `outline`, and the rest clear. Made from
// the host's image the first time it is asked for, and again whenever that image is
// replaced; null, and the host's image asked for, until it has arrived.
const Texture* FindOutlinedTexture(std::string_view key, ImU32 glyph, ImU32 outline);

// A checkerboard one pixel to a square, `width` by `height`, `even` where x + y is even and
// `odd` elsewhere: the bitmap VVS's Minimalist themes tiled down their scroll bars. Made on
// first use.
const Texture* FindCheckerTexture(ImU32 even, ImU32 odd, int width, int height);

// How many textures are live, for the log.
size_t TextureCount();

// Keys the drawing code asked for and did not have, not yet asked of the host - at most
// `limit` of them, oldest first. The host sends its theme without being asked; this is for
// everything else, so that art the DLL starts drawing does not wait on a host that knows to
// send it. Each key is asked for once per connection.
std::vector<std::string> TakeWantedImageKeys(size_t limit);

// Forgets which keys were asked for, so a new host is asked again. Call on reconnecting.
void ForgetWantedImageKeys();

// Frees textures the backend has finished destroying. Once a frame, after rendering.
void CollectRetiredTextures();

// Frees every texture's CPU side. Call after the backend has shut down - which destroys
// the GPU side of each of ours, since they are marked as belonging to one context - and
// before the ImGui context is destroyed.
void ReleaseAllTextures();

// The fonts the Decal look is drawn in, loaded from the Windows fonts folder when the
// context is made. Null when they could not be loaded; the drawing code then falls back
// to ImGui's own font, which is legible if not faithful.
struct DecalFonts {
    ImFont* regular = nullptr;
    ImFont* bold = nullptr;
};

// Adds Times New Roman, regular and bold, to the atlas. Call once, after the context is
// created and before the first frame. Returns false if either face was missing.
bool LoadDecalFonts(ImFontAtlas* atlas);

const DecalFonts& GetDecalFonts();

}  // namespace overlay
