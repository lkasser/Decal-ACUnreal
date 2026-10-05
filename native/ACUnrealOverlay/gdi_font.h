// Text drawn the way Decal's views drew it.
//
// Virindi View Service built its fonts with GDI+ at a size in points, and drew every glyph
// with TextRenderingHint.SingleBitPerPixelGridFit: hinted to the pixel grid, one bit per
// pixel, no anti-aliasing. ImGui's own rasteriser is unhinted and anti-aliased, so the same
// face at the same size came out soft and a little differently shaped - and, since ImGui
// sizes a font by its whole line rather than its em, smaller too.
//
// This is an ImGui font loader that asks Windows for each glyph instead: GDI's
// GetGlyphOutline in its one-bit mode, which is the same TrueType hinter GDI+ used. A font
// added through it is sized by its em in pixels, as GDI+ sized by points, so the drawing
// code passes the theme's points converted at 96 per inch and gets the same glyphs VVS drew.

#pragma once

struct ImFont;
struct ImFontAtlas;

namespace overlay {

// Adds a face drawn by GDI, or returns null if Windows has no such face. `weight` is GDI's:
// 400 regular, 700 bold.
ImFont* AddGdiFont(ImFontAtlas* atlas, const char* face, int weight);

}  // namespace overlay
