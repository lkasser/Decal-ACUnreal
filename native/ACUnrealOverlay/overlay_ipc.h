// The overlay's end of the pipe to the .NET host.
//
// The host does all the thinking - looting rules, spell timing, the wire protocol - and
// publishes a snapshot of what to draw. This side does nothing but carry those snapshots
// in and the player's clicks out, because everything behind the renderer is the part that
// breaks on a client update and it should stay as small as it can be.
//
// Wire format, which the .NET side matches exactly: a 4-byte little-endian length, then
// that many bytes of UTF-8 JSON. Host to overlay is a State, overlay to host a Command,
// with snake_case property names matching the field names in overlay_state.h.

#pragma once

#include <cstdint>
#include <memory>
#include <string>
#include <vector>

#include "overlay_state.h"

namespace overlay {

// A named-pipe client that lives inside the game process.
//
// The promises below are the point of this class, because it runs inside somebody's game
// and anything that goes wrong here is a crash they will blame on the game:
//
//  * No exception escapes any public member. A pipe that is not there, a host that exits
//    halfway through a message, malformed JSON, a truncated length prefix, a prefix
//    claiming a gigabyte - each of those ends as a dropped connection and a retry.
//  * No public member waits on the pipe. Every read and write happens on a thread of our
//    own; the published state changes hands as a shared pointer, so a caller polling from
//    a Present hook only ever copies a pointer and can never be held up by our I/O.
//  * The connection looks after itself. The host may be started after the game, stopped,
//    or restarted mid-session; this reconnects roughly once a second on its own.
//
// One hazard this class cannot fix on the caller's behalf: never destroy an Ipc, or call
// Stop(), from DllMain. Stop() joins a thread, and joining under the loader lock
// deadlocks. Stop it from the hook's own shutdown path before the DLL unloads.
class Ipc {
 public:
    // The name is appended to \\.\pipe\ unless it already begins with a backslash, in
    // which case it is used as given.
    explicit Ipc(std::wstring pipe_name = L"achost-overlay") noexcept;
    ~Ipc();

    Ipc(const Ipc&) = delete;
    Ipc& operator=(const Ipc&) = delete;
    Ipc(Ipc&&) = delete;
    Ipc& operator=(Ipc&&) = delete;

    // Starts the background thread. Calling it again while it is running does nothing;
    // calling it after Stop() starts a new one.
    void Start() noexcept;

    // Joins the background thread. Safe to call twice, and from the destructor. It waits,
    // but only for a cancelled I/O to settle - milliseconds - so it belongs on a shutdown
    // path rather than in a render loop.
    void Stop() noexcept;

    // Whether the pipe to the host is open.
    bool Connected() const noexcept;

    // The newest snapshot, or null until the host has published one. Nothing here is
    // copied, so this is the one to call once a frame.
    //
    // The last snapshot outlives a disconnect deliberately: the panels should not blank
    // out while the host restarts. Connected() is how a caller tells live from stale.
    std::shared_ptr<const State> Latest() const noexcept;

    // The same snapshot, deep-copied for callers that want to hold or edit it. Empty
    // before the first message arrives.
    State Snapshot() const noexcept;

    // Queues a command for the host. Never waits on the pipe, and quietly drops the
    // command if the host is gone or the queue has backed up.
    void Send(const Command& command) noexcept;

    // The images that have arrived since the last call, oldest first. For the render
    // thread, which is the only one allowed to turn them into textures. Never waits on the
    // pipe: the lock is held for a swap.
    std::vector<ImagePixels> TakeImages() noexcept;

    // The keys the host last asked to have held down, by virtual-key code, and how long ago
    // it asked. Empty while disconnected: a host that has gone holds nothing.
    void WantedKeys(std::vector<uint16_t>& keys, uint64_t& age_ms) const noexcept;

 private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

}  // namespace overlay
