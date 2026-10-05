# Changes wanted in `overlay_state.h`

Raised while writing the ImGui drawing code against the first version of the contract.
Recorded rather than applied immediately, because the .NET publisher was being written
against that version at the same time and a contract that changes under one side is
worse than one that is briefly imperfect.

Everything here is additive except the first item.

## To apply

**`Status::connected` is ambiguous** — connected to the host's pipe, or to the game
server? Both readings matter and they mean opposite things for whether the controls
should be usable. Rename to `server_connected` and let the pipe's state be expressed by
whether a snapshot has ever arrived at all.

**No time in the snapshot.** `revision` separates "never published" from "published",
but a host that hung thirty seconds ago looks exactly like a quiet one. The drawing code
cannot keep its own clock without holding local state, which the design deliberately
forbids. Add `published_ms` and let the UI grey itself when a snapshot goes stale.

**`Row` has no identity.** Without one the panels are permanently read-only: no
appraising a row, no adding one to the loot rules, no selection that survives the next
publish. An opaque `id` string costs nothing now and is awkward to retrofit once panels
are assumed to be display-only.

**`Panel` has no stable key.** Titles may be empty, duplicated, or reordered between
publishes. ImGui keys a table's remembered column widths by table id, so without a
stable key a remembered width lands on whichever panel inherits the id - which is why
the drawing code currently turns saved widths off.

**No channel for a host-side message.** When the pipe dies or the host shuts down, the
UI can see that `revision` stopped moving but cannot say why. One `notice` string on
`Status` covers it.

## Applied since

`Panel::owner` and `Command::owner`: panels are grouped into one window per plugin and
a click names the window it came from, which is what lets the host route it to one
plugin and no other. `Status::server_connected`, `notice`, `published_ms`, `Row::id`,
`Panel::key`, and `Tone` as an enum - all from the first list above.

The control vocabulary and the explicit plugin list, together, as `State::windows`. Each
`PluginWindow` is one loaded plugin - `owner`, an optional `title`, `enabled`, and its
`controls` - so the bar lists every plugin the host loaded, idle ones included, and a
plugin whose controls could not be read stays listed, drawn in the bad tone. Each
`Control` is `{id, label, kind, value, options, min, max, step, tooltip}` with `kind` one
of toggle, button, slider, choice and text; a change comes back as a `Command` named
"set" or "press" carrying the new `control_id`. `kControlsOwner` is gone: the DLL no
longer names any plugin, and VirindiTank's switches arrive the way any plugin's would.
Panels whose owner has no window entry still get a window, so a host that sends no
`windows` behaves as before.

`Status::looting` is now unread by the DLL - the loot switch's state travels as that
control's value - and can be dropped the next time the status changes shape.

`native/test.ps1` builds the drawing code against real ImGui with asserts live and drives
every control kind through clicks, drags and typing, plus a hundred hostile plugins, and
checks the commands that come out. Before it, an unbalanced push in the drawing code
could only have been found by the game crashing, since the DLL is built with asserts off.

Decal views and images. `PluginWindow::view` carries a plugin's Decal view - the view
XML's controls, parsed by the host, with their state filled in (`ViewControl`: type, name,
position, text and style, value, options, pages, children, list columns and rows). A command
from one names the control in `control_id` and is "set", "press", "page" (a notebook tab) or
"click" (a list cell, row in `row_id`, column in `value`). Images travel in frames of their
own - `{"image": {key, width, height, rgba}}`, base64 RGBA - sent once per connection ahead
of the snapshots, never coalesced away. The DLL turns them into ImGui user textures on the
render thread. `native/test.ps1` drives a view end to end; the IPC self-test checks both
frame kinds through a real pipe.

Keys. `{"input": {"held": [87, 65], "sequence": n}}` is the whole set of virtual-key codes
the host wants held down in the game - never a change, so a lost or repeated frame cannot
strand a key. The DLL presses what is new and releases what has gone by posting the key
messages Windows itself would send, marked in lParam's reserved bits so its own window
procedure hands them straight to the game past ImGui. The host repeats the set every 300 ms
while anything is held; the DLL lets go of everything when it has heard nothing for 1.2
seconds, when the pipe drops, and when it unloads. This is how a plugin walks: the AC client
decides where its own character is, so moving it means pressing its keys. A new connection
starts with nothing held. `native/test.ps1` checks the press/release bookkeeping.

Themes and hudified windows. `View` gains `theme` ("Decal", "Float"; empty means Decal, which
is how every window looked before a host sent one), `ghosted` and `click_through` - how the
window starts - and `resizeable`, `ghostable` and `click_throughable` - what the player may do,
absent meaning true, since Virindi Tank's window is all three. The host resolves VVS's rules:
the player's own pick from `vvs.s3db`, else VVS's default (Float). What the player then picks
from the title bar - theme, pin, click-through, alpha - the DLL keeps itself, in its ini under
`[VVSView][owner]`, and it outranks what the host says; nothing goes back over the pipe.
Holding left Ctrl shows a hudified window's frame; the DLL reads that key from the keyboard,
not from window messages, since the game may take its keys as raw input.

## The next change, and why it is needed

**A bar that stays on top.** ImGui orders windows by focus, so a window dragged over
the bar hides it until the bar is clicked. Drawing the bar into the foreground draw
list, or an internal-API call to bring it forward, fixes it; neither has been done
because neither belongs in a file that deliberately includes only the public header.

**Icons.** Decal's bar was sixteen-pixel icons, one per plugin; ours is names, because
nothing on the wire carries an image and the DLL ships no assets. An icon would have to
arrive as data.

## Deliberately deferred, with the compatible path written down

**Per-cell colour rather than per-row.** Real trackers colour the one column that
matters - the health figure, the refused item - not the whole line. Doing it by turning
`cells` into a struct would change every row on the wire and both sides at once. The
additive path is a parallel `cell_tones` array, empty meaning "use the row's tone", and
that can be added whenever it is actually wanted.

**Column type hints.** ImGui tables sort and right-align for free, but sorting strings
puts 10 before 9. A parallel `column_types` array unlocks both, and is additive in the
same way.

## Settled, and not to be revisited without a reason

`position` arrives pre-formatted. All geometry stays on the host side and the injected
code never learns what a landblock is. This is the boundary working as intended.

Panels are generic. The drawing code contains no game concepts at all - no loot, no
spells, no protocol - which is what will let it survive changes on the host side
untouched.
