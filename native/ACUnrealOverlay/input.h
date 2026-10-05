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

#pragma once

#include <cstdint>
#include <functional>
#include <vector>

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

inline bool IsInjectedKey(UINT message, LPARAM lParam) {
    return (message == WM_KEYDOWN || message == WM_KEYUP) && (lParam & kInjectedKeyMask) == kInjectedKeyMarker;
}

// Sends one key message to the game. The real one posts to the window; tests record.
using KeyPoster = std::function<void(uint16_t virtual_key, bool down)>;

// Posts a key message to a window as a real key press would arrive, marked as ours.
void PostKeyToWindow(HWND window, uint16_t virtual_key, bool down);

// The hotkey a key press matches: the same key with exactly the same modifiers. Null when
// none does, which leaves the key to the game.
const Hotkey* FindHotkey(const std::vector<Hotkey>& hotkeys, int key, bool ctrl, bool shift, bool alt);

class HeldKeys {
 public:
    explicit HeldKeys(KeyPoster poster) : poster_(std::move(poster)) {}

    // Brings what is held into line with what is wanted: releases what is no longer wanted,
    // then presses what is newly wanted. Keys outside the ordinary range are ignored.
    void Apply(const std::vector<uint16_t>& wanted);

    // Lets go of everything.
    void ReleaseAll() { Apply({}); }

    const std::vector<uint16_t>& Held() const { return held_; }

 private:
    KeyPoster poster_;
    std::vector<uint16_t> held_;
};

}  // namespace overlay
