#include "input.h"

#include <algorithm>
#include <cstdio>

namespace overlay {

namespace {

bool Contains(const std::vector<uint16_t>& keys, uint16_t key) {
    return std::find(keys.begin(), keys.end(), key) != keys.end();
}

// Virtual-key codes run from 1 to 254; anything else is not a key.
bool IsKey(uint16_t key) {
    return key >= 1 && key <= 254;
}

std::string Seconds(uint64_t ms) {
    char text[32];
    std::snprintf(text, sizeof(text), "%.1f", static_cast<double>(ms) / 1000.0);
    return text;
}

// How the log says what the window is doing, once each time that changes.
std::string SayWindow(bool minimized, bool drawing, bool parked) {
    if (parked)
        return drawing ? "The game window is off-screen in place of minimized, and still presents frames."
                       : "The game window is off-screen in place of minimized, and has presented no frame for 2 s.";
    if (minimized)
        return drawing ? "The game window is minimized, and still presents frames."
                       : "The game window is minimized, and presents no frames.";
    return drawing ? "The game window is shown, and presents frames."
                   : "The game window is shown, but has presented no frame for 2 s.";
}

}  // namespace

void PostKeyToWindow(HWND window, uint16_t virtual_key, bool down) {
    if (window == nullptr) return;

    // As the keyboard would send it: one repeat, the key's scan code, and for a release the
    // previous-state and transition bits. Games that read the scan code rather than the
    // virtual key - for layout-independent movement - find the right one.
    const UINT scan = MapVirtualKeyW(virtual_key, MAPVK_VK_TO_VSC);
    LPARAM lParam = 1 | (static_cast<LPARAM>(scan & 0xFF) << 16) | kInjectedKeyMarker;
    if (!down) lParam |= (static_cast<LPARAM>(1) << 30) | (static_cast<LPARAM>(1) << 31);

    PostMessageW(window, down ? WM_KEYDOWN : WM_KEYUP, virtual_key, lParam);
}

const Hotkey* FindHotkey(const std::vector<Hotkey>& hotkeys, int key, bool ctrl, bool shift, bool alt) {
    for (const Hotkey& hotkey : hotkeys) {
        if (hotkey.key == key && hotkey.ctrl == ctrl && hotkey.shift == shift && hotkey.alt == alt) return &hotkey;
    }
    return nullptr;
}

std::string DescribeKeys(const std::vector<uint16_t>& keys) {
    std::string text;
    for (uint16_t key : keys) {
        if (!text.empty()) text += ',';
        text += std::to_string(key);
    }
    return text;
}

POINT PlaceInLayout(int client_width, int client_height, int layout_width, int layout_height, int x, int y, double ui_scale) {
    POINT placed{x, y};
    if (layout_width <= 0 || layout_height <= 0 || client_width <= 0 || client_height <= 0) return placed;

    // At the client's UI scale - its own size at 100% - and never larger; smaller only where the
    // window is.
    const double scale = std::min({ClientUiScale(ui_scale, client_width, client_height), static_cast<double>(client_width) / layout_width,
                                   static_cast<double>(client_height) / layout_height});
    const double left = (client_width - layout_width * scale) / 2.0;
    const double top = (client_height - layout_height * scale) / 2.0;
    placed.x = static_cast<LONG>(left + x * scale + 0.5);
    placed.y = static_cast<LONG>(top + y * scale + 0.5);
    return placed;
}

void Clicker::Begin(const Click& click, uint64_t now_ms) {
    if (click.points.empty() || click.id == last_id_) return;
    last_id_ = click.id;

    // One under way is cut short, its button let go where it went down, rather than left held.
    if (down_ && next_ > 0) poster_(MouseStep::Up, click_, steps_[next_ - 1].point);
    down_ = false;

    click_ = click;
    steps_.clear();
    next_ = 0;

    uint64_t at = now_ms;
    for (const Click::Point& point : click.points) {
        steps_.push_back({at, MouseStep::Move, point});
        steps_.push_back({at + kClickSettleMs, MouseStep::Down, point});
        steps_.push_back({at + kClickSettleMs + kClickHoldMs, MouseStep::Up, point});
        at += kClickSettleMs + kClickHoldMs + kClickBetweenMs;
    }
    steps_.push_back({at - kClickBetweenMs + kClickSettleMs, MouseStep::Restore, {}});

    std::string where;
    for (const Click::Point& point : click.points) {
        if (!where.empty()) where += ", then ";
        where += std::to_string(point.x) + "," + std::to_string(point.y);
    }
    std::string scale;
    if (click.ui_scale != 1.0) {
        char percent[64];
        std::snprintf(percent, sizeof(percent), ", at the client's UI scale of %g%%", click.ui_scale * 100.0);
        scale = percent;
    }
    log_("Clicking in the game window for the host: " + where + " of the layout centred in it, " + std::to_string(click.layout_width) +
         " by " + std::to_string(click.layout_height) + scale + ".");
}

void Clicker::Pass(uint64_t now_ms) {
    while (next_ < steps_.size() && steps_[next_].at <= now_ms) {
        const Step& step = steps_[next_++];
        poster_(step.what, click_, step.point);
        if (step.what == MouseStep::Down) down_ = true;
        if (step.what == MouseStep::Up) down_ = false;
    }
}

uint64_t Clicker::NextDueIn(uint64_t now_ms) const {
    if (next_ >= steps_.size()) return UINT64_MAX;
    return steps_[next_].at <= now_ms ? 0 : steps_[next_].at - now_ms;
}

void HeldKeys::Apply(const std::vector<uint16_t>& wanted) {
    std::vector<uint16_t> next;
    for (uint16_t key : wanted) {
        if (IsKey(key) && !Contains(next, key)) next.push_back(key);
    }

    // Releases first, so turning left then right never has both held for a moment.
    for (uint16_t key : held_) {
        if (!Contains(next, key)) poster_(key, false);
    }

    for (uint16_t key : next) {
        if (!Contains(held_, key)) poster_(key, true);
    }

    held_ = std::move(next);
}

void HeldKeys::PressAgain() {
    for (uint16_t key : held_) poster_(key, true);
}

std::string DescribeGameWindow(bool minimized, bool drawing, bool parked) {
    return std::string(minimized ? "1" : "0") + "," + (drawing ? "1" : "0") + "," + (parked ? "1" : "0");
}

void KeyPump::WindowChanged(uint64_t now_ms) {
    press_again_ = true;
    press_again_at_ = now_ms + kPressAgainAfterMs;
}

void KeyPump::Pass(std::vector<uint16_t> wanted, uint64_t age_ms, bool connected, const GameWindowSeen& window, uint64_t now_ms) {
    // Frames, counted by the Present hook: only whether they are still coming matters.
    if (!started_) {
        started_ = true;
        frames_ = window.frames;
        frame_at_ = now_ms;
    }
    if (window.frames != frames_) {
        frames_ = window.frames;
        frame_at_ = now_ms;
    }
    drawing_ = now_ms - frame_at_ <= kDrawingWithinMs;

    // The host's wish, unless it has gone quiet - then nothing. Said once each time keys are let
    // go for it, since a character that stops for no reason the player can see wants explaining.
    const bool stale = !connected || age_ms > kHeldKeysStaleMs;
    if (stale) {
        if (!keys_.Held().empty() && !was_stale_)
            log_("Letting go of keys " + DescribeKeys(keys_.Held()) + ": " +
                 (connected ? "the host has not repeated them for over a second." : "the host is not connected."));
        wanted.clear();
    }
    was_stale_ = stale;

    keys_.Apply(wanted);

    // The window changed a moment ago, and the game may have let go of what it thought held.
    if (press_again_ && now_ms >= press_again_at_) {
        press_again_ = false;
        if (!keys_.Held().empty()) {
            if (pressed_again_logged_ < 100) {
                ++pressed_again_logged_;
                log_("Pressed keys " + DescribeKeys(keys_.Held()) + " again after the game window changed" +
                     (window.minimized ? "; it is minimized." : "."));
            }
            keys_.PressAgain();
        }
    }

    // Keys held in a minimized game: whether the game still draws is the first thing to know
    // when the character does not move, so it is said, and said again every so often while it
    // lasts rather than on every pass.
    if (window.minimized && !keys_.Held().empty()) {
        if (!was_minimized_ || now_ms - minimized_noted_at_ >= kMinimizedNoteEveryMs) {
            std::string line = "Holding keys " + DescribeKeys(keys_.Held()) + " while the game window is minimized; ";
            line += drawing_ ? "it still presents frames" : "no frame presented for " + Seconds(now_ms - frame_at_) + " s";
            if (was_minimized_)
                line += ", " + std::to_string(window.frames - frames_at_note_) + " in the last " +
                        Seconds(now_ms - minimized_noted_at_) + " s";
            log_(line + ".");
            minimized_noted_at_ = now_ms;
            frames_at_note_ = window.frames;
        }
        was_minimized_ = true;
    } else {
        was_minimized_ = false;
    }

    // What the window is doing: logged when it changes, and told to the host when it changes
    // and afresh to each host that connects, which has heard nothing yet.
    const std::string state = DescribeGameWindow(window.minimized, drawing_, window.parked);
    if (state != last_report_) {
        log_(SayWindow(window.minimized, drawing_, window.parked));
        last_report_ = state;
        reported_ = false;
    }
    if (connected && (!was_connected_ || !reported_)) {
        report_(state);
        reported_ = true;
    }
    was_connected_ = connected;
}

}  // namespace overlay
