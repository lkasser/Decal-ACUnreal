// Decal views, drawn in the Decal theme.
//
// A plugin that ships a Decal view XML - Virindi Tank's main window, for one - has its
// window drawn from it: Decal's controls, at the positions the XML gives, looking the way
// one of Virindi View Service's themes - Decal, Float or the four Minimalists - drew them.
// Each theme's every image and colour was read out of VirindiViewService.dll's own theme
// classes; the images themselves come from the client's portal.dat and the theme files on
// this machine, by way of the host.
//
// Like the rest of the drawing code this knows no plugin and no game concept. It is handed
// a view with its state filled in, and gives back commands naming the control that was
// used; the plugin decides what they mean.

#pragma once

#include <string>
#include <vector>

#include "imgui.h"
#include "overlay_state.h"

namespace overlay {

// Draws one plugin window as its Decal view. `cascade` places a window the first time it
// appears; after that it stays where the player dragged it. `open` is cleared when the
// player closes it with the title bar's button.
void DrawDecalWindow(const PluginWindow& window, size_t cascade, bool& open, std::vector<Command>& commands);

// Once a frame, after every window: forgets what the player was part way through doing in
// controls that were not drawn this frame - a text box whose window was closed mid-edit.
void EndDecalFrame();

// Once a frame, before any window: whether left Ctrl is held, which shows a hudified
// window's frame and lets a click-through one be clicked, as in VVS.
void SetDecalReveal(bool held);

// Once, after the ImGui context is made and before its first frame: keeps each window's
// theme, pin, click-through and alpha in the ini, beside where it was left.
void RegisterDecalSettings();

// How a window is being drawn now: what the host said, or what the player picked since.
struct DecalWindowLook {
    std::string theme;
    bool ghosted = false;
    bool click_through = false;
    int alpha = 255;
};
DecalWindowLook DescribeDecalWindow(const std::string& owner, const View& view);

// How a plugin's entry on the bar looks.
enum class SwitchLook { Open, Closed, Faulted };

// One entry on Decal's bar: the plugin's icon and name on a switch, gold while its window
// is open and red while it is closed. Returns true when clicked. Falls back to a plain
// button if the switch images have not arrived.
bool DecalSwitch(const char* id, const std::string& label, const std::string& icon, SwitchLook look, const char* tooltip);

// Decal's gold arrow, at the start of the bar, pointing left while the switches are out and
// right while they are folded away. Dragging it moves the bar; a click that did not drag
// returns true, and the caller decides what the click means. Call it inside the bar's window.
bool DecalBarArrow(const char* id, bool expanded, const char* tooltip);

// Which way a press that drags moves a bar's window: anywhere, along it, down it, or not at all.
enum class BarDrag { Free, Across, Down, None };

// Decal's bar as the standard client shows it: a grip at each end, the gold square that
// switches it between compact and expanded, a switch per plugin, and the grey square that
// docks it to another edge.
//
// The grip: a press that drags moves the bar's window; a press that does not is a click,
// returned as true. Upright on a bar across the top, lying down on one down a side.
bool DecalBarGrip(const char* id, const char* tooltip, bool vertical = false, BarDrag drag = BarDrag::Free);

// One of the bar's 16-pixel image buttons: its image, or its down image while held.
bool DecalBarButton(const char* id, const char* up, const char* down, const char* tooltip);

// A plugin's switch, compact: a small square of Decal's gold switch texture while its window
// is open, the red one while closed, the grey one when faulted, with the plugin's icon on it.
bool DecalIconSwitch(const char* id, const std::string& icon, SwitchLook look, const char* tooltip);

// A plugin's switch, expanded: the whole gold or red switch bitmap, its icon and its name.
bool DecalLabelSwitch(const char* id, const std::string& label, const std::string& icon, SwitchLook look, float width,
                      const char* tooltip);

// Virindi View Service's bar: a 20-pixel cell per view, the open view's icon on a square of the
// theme's button shadow, and a rule between one plugin's views and the next's. A press toggles
// the view's window, returned as true; one that drags moves the bar only when `movable`.
bool VvsBarItem(const char* id, const std::string& icon, bool open, const char* tooltip, bool movable);
void VvsBarRule(bool vertical = true);

// The bar's coral box, drawn while `shown` - left Ctrl held, with the bar's title strip. A press
// opens its window menu: Change Theme for the bar alone, then "Set Vertical" or "Set
// Horizontal", which returns true. Call it every frame, shown or not, so an open menu stays.
bool VvsBarBox(bool across, bool shown);

// The bar's own theme, as its menu last set it, or VVS's primary one.
std::string DecalBarTheme();

// VVS's primary theme, which a VVS window with no theme of its own is drawn in: the host's
// word for VVS's default, until the player picks another with the bar's "ab" square - which
// steps through VVS's six in the order it registered them - or its title strip's H.S. button,
// which sets VVS's hot-dog stand, "Minimalist H.S.".
void SetDecalDefaultTheme(const std::string& name);
std::string DecalGlobalTheme();
void CycleDecalGlobalTheme();
void SetDecalGlobalTheme(const std::string& name);

// Whether left Ctrl is held now, as SetDecalReveal was last told.
bool DecalRevealHeld();

// How much larger than Decal's own pixels everything is drawn. Decal's windows were sized
// for screens of 800 by 600; at native size they are small on a modern one.
float DecalScale();

}  // namespace overlay
