// The whole of the drawing code's public surface: one call per frame.
//
// The hook layer owns the device, the swap chain and the frame; it is not told what is
// being drawn, and this header is deliberately everything it needs to know. Keeping the
// seam this narrow means the panels can be rewritten without touching the renderer,
// which is the part that breaks when the client is patched.

#pragma once

#include <vector>

#include "overlay_state.h"

namespace overlay {

// Draws one frame. Returns commands the player generated this frame.
//
// Call between the backend's NewFrame and Render. Nothing here touches the device, so a
// caller that has lost its swap chain can keep calling this or stop, as it prefers.
//
// Not a pure function of its argument. It reads the wall clock to compare against
// State::published_ms, which is how a snapshot that has stopped arriving is drawn
// differently from one that has nothing new to say. And it remembers the player's hands:
// which windows are open, and the one slider or text box being changed until it is let
// go. Nothing about the session is remembered - every value drawn comes from the State.
//
// `visible` is the whole overlay - the bar and every window. While it is false this
// submits nothing at all, which is also how the game gets the mouse and keyboard back:
// an invisible window would still swallow clicks. It is taken by reference so the drawing
// code may clear it, though at present nothing does: a window's close button closes that
// window alone, and which windows are open is remembered in here, not by the caller, as
// the player's arrangement of the screen. The caller's hotkey is the only thing that
// hides the lot.
//
// On a display too small to hold a window - a minimized game's, which is 0 x 0 - it submits
// nothing either, and so moves nothing: a window kept on a screen of no size goes to its
// corner, and ImGui would save it there. The caller should not draw such a frame at all.
std::vector<Command> DrawOverlay(const State& state, bool& visible);

// The least width and height, in pixels, of a display the overlay draws on. Less is a minimized
// window, or one Unreal has given 8 x 8 back buffers while it is minimized.
constexpr float kLeastDisplaySide = 100.0f;

// Whether a display, or back buffers, of this size are drawn on.
inline bool DisplayUsable(float width, float height) {
    return width >= kLeastDisplaySide && height >= kLeastDisplaySide;
}

// Once, after the ImGui context is made and before its first frame: keeps Decal's bar as the
// player set it - compact or expanded, which edge, how long - in the ini.
void RegisterOverlaySettings();

}  // namespace overlay
