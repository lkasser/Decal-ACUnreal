// Where AC:Unreal draws its own interface, which the overlay has to reckon with twice: to click
// its character select for the host, and to start its own bars clear of the client's plugin bar.
//
// Since release 94 the client has a "Desktop UI Scale" option - DesktopUIScale under
// [ACE.Presentation] in Saved\Config\Windows\GameUserSettings.ini - applied as Unreal's DPI scale
// for the whole game viewport: every one of its widgets is drawn that much larger, the 800 by 600
// character select and the plugin bar among them. The window, its swap chain and the overlay's own
// drawing are not changed by it. The host reads what the player chose; what the client draws at
// depends on the window as well, which only the overlay knows.
//
// Header-only and free of Windows, so the drawing code and its self-test can use it as they are.

#pragma once

#include <algorithm>
#include <cmath>

namespace overlay {

// AC:Unreal's Desktop UI Scale as the client applies it in a client area of this size, as observed
// in ACUnreal.exe release 96 (its settings table gives DesktopUIScale a default of 1 and bounds of
// 1 and 3; its routine at 0x1499D4760 limits it to the window): the scale the
// player chose, taken to the nearest quarter step, but no more than the largest quarter step at
// which its 800 by 600 layout fits the window, and never less than 1. So 200% in a 1920 by 1080
// window is drawn at 175%, and an 800 by 600 window is always at 100%. 1 for a window of no size.
inline double ClientUiScale(double chosen, int client_width, int client_height) {
    if (client_width <= 0 || client_height <= 0) return 1.0;
    if (!std::isfinite(chosen)) chosen = 1.0;
    chosen = std::clamp(chosen, 1.0, 3.0);

    // In single precision, as the client works it out. (std::min) and (std::max), parenthesized,
    // for a file that has windows.h's macros of those names.
    const float fit = (std::min)(static_cast<float>(client_height) * (1.0f / 600.0f), static_cast<float>(client_width) * (1.0f / 800.0f));
    const float largest = (std::max)(std::floor(fit * 4.0f) * 0.25f, 1.0f);
    const float wanted = std::floor(static_cast<float>(chosen) * 4.0f + 0.5f) * 0.25f;
    return static_cast<double>((std::min)(largest, wanted));
}

// A rectangle of the client area, in pixels.
struct ClientRect {
    float left = 0.0f;
    float top = 0.0f;
    float right = 0.0f;
    float bottom = 0.0f;

    bool Overlaps(const ClientRect& other) const {
        return left < other.right && other.left < right && top < other.bottom && other.top < bottom;
    }
};

// Where something of the client's own interface is drawn - `units` is its left, top, width and
// height in the client's interface units - in pixels of a client area of this size, at the scale
// the client applies there.
inline ClientRect ClientUiRect(const float units[4], double chosen_scale, int client_width, int client_height) {
    const float scale = static_cast<float>(ClientUiScale(chosen_scale, client_width, client_height));
    ClientRect rect;
    rect.left = units[0] * scale;
    rect.top = units[1] * scale;
    rect.right = (units[0] + units[2]) * scale;
    rect.bottom = (units[1] + units[3]) * scale;
    return rect;
}

// Where a bar of the overlay's that would be at `ours` should start, down the client area, to keep
// clear of `theirs`: where it is when it would not overlap it, else `gap` below it - unless it
// would then run off the bottom of a client area `client_height` high, when where it is all the same.
inline float StartClearOf(const ClientRect& ours, const ClientRect& theirs, float client_height, float gap = 4.0f) {
    if (!ours.Overlaps(theirs)) return ours.top;

    const float below = theirs.bottom + gap;
    return below + (ours.bottom - ours.top) <= client_height ? below : ours.top;
}

}  // namespace overlay
