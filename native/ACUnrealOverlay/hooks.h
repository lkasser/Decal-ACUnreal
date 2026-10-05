// The hooks, and nothing else.
//
// Everything that touches the client's renderer is behind this interface, because that
// is the part guaranteed to break when the client updates. Drawing, state and input all
// live elsewhere and know nothing about D3D12.

#pragma once

namespace overlay {
namespace hooks {

// Installs the hooks. Returns false if the client's renderer could not be found, which
// is not a crash and not a bug in the client - it is what happens when the game has not
// created its device yet, or when a future release changes what we hook.
//
// Safe to call twice; the second call does nothing and returns the first result.
bool Install();

// Removes the hooks and waits for any hooked function still executing to return.
//
// Not safe during process termination: by then the graphics runtime may already be
// unloaded, and touching it is how a clean exit becomes a crash report. The caller is
// responsible for that distinction.
void Uninstall();

bool Installed();

// Shows or hides the overlay. Separate from installation so the hooks can stay in place
// while nothing is drawn, which is what makes hiding instant.
void SetVisible(bool visible);

bool Visible();

}  // namespace hooks
}  // namespace overlay
