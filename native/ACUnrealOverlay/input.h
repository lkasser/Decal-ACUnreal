// Keys held down in the game on the host's behalf.
//
// In Asheron's Call the client decides where its own character is: the server is told, not
// asked. So a plugin that wants to walk cannot send the server a walk; it has to do what a
// player does and hold the keys down. The host says which keys it wants held - the whole set
// each time - and this presses the new ones and releases the ones that have gone, by posting
// the same key messages Windows would send the game's window.
//
// Two rules keep this from ever running away with a character:
//  * The set is the host's whole wish, not a change, so a lost or repeated message cannot
//    leave a key stuck down.
//  * If the host stops repeating itself - it crashed, the pipe broke, the DLL is unloading -
//    every key is released.
//
// Neither rule may depend on the game drawing frames. A player who minimizes the game to
// save the machine's effort still wants the character walked, and a minimized game presents
// few frames or none - so the keys are pressed, refreshed and let go by a pump of their own
// (KeyPump below, run on a thread of the overlay's own), never from the Present hook.

#pragma once

#include <cstdint>
#include <functional>
#include <string>
#include <vector>

#include "client_ui.h"
#include "overlay_state.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

namespace overlay {

// Marks a key message as one this posted, in bits of lParam Windows leaves reserved, so the
// window procedure can hand it straight to the game rather than to the overlay's own UI -
// which would otherwise swallow it whenever an overlay window has the keyboard.
constexpr LPARAM kInjectedKeyMarker = static_cast<LPARAM>(0xA) << 25;
constexpr LPARAM kInjectedKeyMask = static_cast<LPARAM>(0xF) << 25;

// How long the host may go without repeating the set before every key is let go. It repeats
// several times a second while it holds anything.
constexpr uint64_t kHeldKeysStaleMs = 1200;

// The longest the key pump sleeps when nothing wakes it: how late a stale set can be let go,
// frames or none. A new set from the host wakes it at once.
constexpr uint32_t kKeyPumpIntervalMs = 100;

// After the game window is activated, deactivated, minimized or restored, how long the pump
// waits before pressing the held keys again. Unreal lets go of every key it thinks pressed
// when its viewport loses focus, and does it on its own tick after the window message, so the
// press has to come after that rather than straight away.
constexpr uint64_t kPressAgainAfterMs = 250;

// No frame presented for this long counts as the game not drawing.
constexpr uint64_t kDrawingWithinMs = 2000;

// How often the word about keys held in a minimized game is repeated while it goes on.
constexpr uint64_t kMinimizedNoteEveryMs = 10000;

inline bool IsInjectedKey(UINT message, LPARAM lParam) {
    return (message == WM_KEYDOWN || message == WM_KEYUP) && (lParam & kInjectedKeyMask) == kInjectedKeyMarker;
}

// The same for a mouse message this posted, in bits of wParam no mouse message uses: the
// button flags are its low bits, and only the wheel's and the extra buttons' messages use the
// high word. The window procedure takes the marker off and hands the message to the game - the
// overlay's own windows would otherwise take a click that lands where one of them is drawn, as
// Virindi Tank's window covers the character list.
constexpr WPARAM kInjectedMouseMarker = static_cast<WPARAM>(0xA) << 24;
constexpr WPARAM kInjectedMouseMask = static_cast<WPARAM>(0xF) << 24;

inline bool IsInjectedMouse(UINT message, WPARAM wParam) {
    return (message == WM_MOUSEMOVE || message == WM_LBUTTONDOWN || message == WM_LBUTTONUP) &&
           (wParam & kInjectedMouseMask) == kInjectedMouseMarker;
}

// Where a point of a layout falls in the game's client area: the layout drawn centred, at its
// own size times the client's Desktop UI Scale (ClientUiScale of `ui_scale`, client_ui.h) where it
// fits and shrunk evenly to fit where it does not - as AC:Unreal draws its character select, the
// retail 800 by 600 layout, centred with black around it on a larger window.
POINT PlaceInLayout(int client_width, int client_height, int layout_width, int layout_height, int x, int y, double ui_scale = 1.0);

// What one step of a click does: the pointer brought to the point, the left button pressed or
// let go there, or - after the last point - the pointer put back where the player had it.
enum class MouseStep { Move, Down, Up, Restore };

// Makes one step, at a point of the click's layout (none for Restore). The real one posts to
// the game's window; tests record.
using MousePoster = std::function<void(MouseStep step, const Click& click, const Click::Point& point)>;

// How long after the pointer is brought to a point the button goes down there: long enough for
// the game to have seen the pointer arrive - a frame or two, even drawing slowly - since Unreal's
// buttons count a click only over the button the pointer is over.
constexpr uint64_t kClickSettleMs = 150;

// How long the button stays down: as a quick click.
constexpr uint64_t kClickHoldMs = 120;

// Between letting go at one point and moving to the next: well apart, so two points are two
// clicks and never a double click.
constexpr uint64_t kClickBetweenMs = 450;

// The steps of the host's clicks, made on the key pump's clock: each point moved to, pressed
// and let go a moment apart, the next point after a pause, and then the pointer put back.
// A new click replaces one still under way, whose button is let go first. Not thread-safe on
// its own: the key pump's lock covers it.
class Clicker {
 public:
    using Logger = std::function<void(const std::string&)>;

    Clicker(MousePoster poster, Logger log) : poster_(std::move(poster)), log_(std::move(log)) {}

    // Starts a click from now. A click with no points, or whose id was the last one begun, is
    // ignored: the host repeats a click for a moment so that a lost frame cannot lose it.
    void Begin(const Click& click, uint64_t now_ms);

    // Makes every step that is due.
    void Pass(uint64_t now_ms);

    // How long until the next step is due: 0 if one is due now, UINT64_MAX when none is left.
    uint64_t NextDueIn(uint64_t now_ms) const;

    bool Busy() const { return next_ < steps_.size(); }

 private:
    struct Step {
        uint64_t at = 0;
        MouseStep what = MouseStep::Move;
        Click::Point point;
    };

    MousePoster poster_;
    Logger log_;
    Click click_;
    std::vector<Step> steps_;
    size_t next_ = 0;
    int64_t last_id_ = 0;
    bool down_ = false;
};

// Sends one key message to the game. The real one posts to the window; tests record.
using KeyPoster = std::function<void(uint16_t virtual_key, bool down)>;

// Posts a key message to a window as a real key press would arrive, marked as ours.
void PostKeyToWindow(HWND window, uint16_t virtual_key, bool down);

// The hotkey a key press matches: the same key with exactly the same modifiers. Null when
// none does, which leaves the key to the game.
const Hotkey* FindHotkey(const std::vector<Hotkey>& hotkeys, int key, bool ctrl, bool shift, bool alt);

// "87,65": the keys as the log names them.
std::string DescribeKeys(const std::vector<uint16_t>& keys);

class HeldKeys {
 public:
    explicit HeldKeys(KeyPoster poster) : poster_(std::move(poster)) {}

    // Brings what is held into line with what is wanted: releases what is no longer wanted,
    // then presses what is newly wanted. Keys outside the ordinary range are ignored.
    void Apply(const std::vector<uint16_t>& wanted);

    // Presses every held key again, as a fresh press. For after the game may have let go of
    // them on its own - see kPressAgainAfterMs. Nothing is released first: a release would
    // stop a run for a frame, and end a jump's charge in a jump.
    void PressAgain();

    // Lets go of everything.
    void ReleaseAll() { Apply({}); }

    const std::vector<uint16_t>& Held() const { return held_; }

 private:
    KeyPoster poster_;
    std::vector<uint16_t> held_;
};

// What one pass of the key pump sees of the game's window.
struct GameWindowSeen {
    // The window is minimized (IsIconic).
    bool minimized = false;

    // The overlay has sent it off-screen in place of minimizing it, for a player who asked to
    // keep playing while it is minimized.
    bool parked = false;

    // Frames presented since the overlay loaded. Only its movement matters.
    uint64_t frames = 0;
};

// The window as the host is told it, in a "game-window" command: "minimized,drawing,parked",
// each 0 or 1.
std::string DescribeGameWindow(bool minimized, bool drawing, bool parked);

// The held keys' clock, independent of frames: each pass takes the host's latest wish, lets go
// of everything if the host has gone quiet, presses again after the window changed, and keeps
// an eye on whether the game is minimized and drawing - logging that, and telling the host
// when it changes. Not thread-safe on its own: the caller holds one lock across WindowChanged
// and Pass, the same lock the held keys are touched under everywhere else.
class KeyPump {
 public:
    using Logger = std::function<void(const std::string&)>;

    // Sends the host a "game-window" command with the given value.
    using Reporter = std::function<void(const std::string&)>;

    KeyPump(HeldKeys& keys, Logger log, Reporter report)
        : keys_(keys), log_(std::move(log)), report_(std::move(report)) {}

    // The window was activated, deactivated, minimized or restored: whatever is held is pressed
    // again kPressAgainAfterMs from now.
    void WindowChanged(uint64_t now_ms);

    // One pass. wanted and age_ms are the host's last wish and how long ago it came; connected
    // is whether the host is there at all.
    void Pass(std::vector<uint16_t> wanted, uint64_t age_ms, bool connected, const GameWindowSeen& window, uint64_t now_ms);

    // Whether frames have been presented within kDrawingWithinMs, as of the last pass.
    bool Drawing() const { return drawing_; }

 private:
    HeldKeys& keys_;
    Logger log_;
    Reporter report_;

    bool press_again_ = false;
    uint64_t press_again_at_ = 0;

    bool started_ = false;
    uint64_t frames_ = 0;
    uint64_t frame_at_ = 0;
    bool drawing_ = true;

    bool was_connected_ = false;
    bool reported_ = false;
    std::string last_report_;

    bool was_stale_ = false;
    bool was_minimized_ = false;
    uint64_t minimized_noted_at_ = 0;
    uint64_t frames_at_note_ = 0;
    int pressed_again_logged_ = 0;
};

}  // namespace overlay
