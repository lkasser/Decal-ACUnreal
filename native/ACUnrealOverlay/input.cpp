#include "input.h"

#include <algorithm>

namespace overlay {

namespace {

bool Contains(const std::vector<uint16_t>& keys, uint16_t key) {
    return std::find(keys.begin(), keys.end(), key) != keys.end();
}

// Virtual-key codes run from 1 to 254; anything else is not a key.
bool IsKey(uint16_t key) {
    return key >= 1 && key <= 254;
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

}  // namespace overlay
