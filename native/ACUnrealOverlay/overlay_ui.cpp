// Every pixel the overlay draws.
//
// This file knows nothing about looting, spells, opcodes or the pipe. It is handed a
// State and gives back a list of Commands; anything it cannot express in that vocabulary
// belongs on the other side of the pipe, in the host, where a mistake costs a log line
// rather than the game process.
//
// Two rules run through the whole file. The host owns every piece of truth about the
// session, so no widget keeps its own copy of any of it - a tick box reads the state it
// was given and emits a command, and if the host refuses the command the tick simply goes
// back next frame. And nothing the host publishes is trusted to be well formed: a crash
// here takes the game down with it, so every count is clamped and every mismatch is
// drawn as a gap.
//
// The shape is Decal's, not one application's. Decal was a bar with an icon per plugin,
// and each icon opened that plugin's own window; VirindiTank was one icon among many.
// So there is a bar, a window per plugin the host says is loaded - holding the controls
// and panels that plugin published, and nothing else - and a window for the host itself
// holding the furniture that was Decal's rather than any plugin's. No plugin is named
// anywhere in this file. Which of those windows are open, and what the player is part way
// through typing or dragging, are the only things remembered between frames: they are
// the player's hands on the screen, not facts about the session, and the host has no
// business knowing them until the player lets go.
//
// The look follows VirindiTank's own Decal window rather than ImGui's defaults, because a
// tool that behaves like uTank2 and looks like a debug panel reads as something else
// entirely. uTank2's view definition supplies the proportions - 856 by 210, a wide strip
// with a notebook along the top and rows pitched every sixteen to twenty pixels - and the
// palette below was sampled from Virindi's theme images. Only the sampled numbers live
// here. None of their artwork is in this repository and none of it needs to be: the
// colours are what carry the resemblance.

#include "overlay_ui.h"

#include <algorithm>
#include <charconv>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <iterator>
#include <map>
#include <string>
#include <string_view>

#include "imgui.h"
#include "imgui_internal.h"  // the settings handler that keeps Decal's bar as the player set it

#include "client_ui.h"

#include "decal_view.h"

namespace overlay {
namespace {

// Virindi's palette, sampled from the Decal theme images as the average of their
// non-transparent pixels. The hex is the sample; the floats are the same colour.
//
// The tab strip is the part a player recognises first, so the active and idle tabs and
// their shaded edges are all kept: the amber of a selected tab against the burnt brown of
// an unselected one is most of what makes the window look like uTank2.
constexpr ImVec4 kTabActive(0.706f, 0.494f, 0.169f, 1.00f);       // #B47E2B, tab centre
constexpr ImVec4 kTabActiveLit(0.765f, 0.588f, 0.302f, 1.00f);    // #C3964D, its lit edge
constexpr ImVec4 kTabActiveShade(0.608f, 0.388f, 0.122f, 1.00f);  // #9B631F, its shaded edge
constexpr ImVec4 kTabIdle(0.522f, 0.298f, 0.071f, 1.00f);         // #854C12, idle centre
constexpr ImVec4 kTabIdleLit(0.580f, 0.353f, 0.094f, 1.00f);      // #945A18, its lit edge
constexpr ImVec4 kTabIdleShade(0.565f, 0.333f, 0.090f, 1.00f);    // #905517, its shaded edge
constexpr ImVec4 kMetalPale(0.490f, 0.427f, 0.271f, 1.00f);       // #7D6D45, arrows and pins
constexpr ImVec4 kMetalWarm(0.478f, 0.298f, 0.149f, 1.00f);       // #7A4C26
constexpr ImVec4 kMetalDim(0.427f, 0.369f, 0.224f, 1.00f);        // #6D5E39

// The body of the window, chosen rather than sampled: Decal's panels sit on the game's own
// dark parchment, and ImGui's default blue-grey is the single thing that most gives away
// that this is not a Decal window. A near-black brown keeps the amber looking like metal
// on wood instead of metal on slate.
constexpr ImVec4 kBody(0.078f, 0.063f, 0.039f, 0.93f);
constexpr ImVec4 kSunken(0.141f, 0.102f, 0.063f, 1.00f);
constexpr ImVec4 kParchment(0.941f, 0.894f, 0.800f, 1.00f);
constexpr ImVec4 kParchmentDim(0.635f, 0.580f, 0.478f, 1.00f);
constexpr ImVec4 kGold(0.894f, 0.714f, 0.361f, 1.00f);

// Tone colours.
//
// These are read over a moving 3D scene, not on a page, and the window is only partly
// opaque - so the usual pure red and pure green are the wrong choices twice over. Both
// are dark (red in particular has barely a third of white's luminance), which leaves thin
// glyph strokes fighting a background that is itself dark, and both vanish against the
// coloured lighting AC throws around. Each is therefore pulled a long way towards white:
// the hue still says good or bad at a glance, the luminance does the legibility.
//
// The muted tone is warm rather than neutral so that it belongs to the palette above, and
// it is deliberately brighter than the theme's own disabled grey, which was picked for an
// opaque window and disappears when the scene shows through.
constexpr ImVec4 kToneGood(0.52f, 0.91f, 0.55f, 1.00f);
constexpr ImVec4 kToneBad(1.00f, 0.47f, 0.44f, 1.00f);
constexpr ImVec4 kToneMuted(0.663f, 0.604f, 0.518f, 1.00f);

// How much larger everything is drawn than the Decal window it copies.
//
// ImGui's built-in font is 13 pixels and Virindi's own labels were 16, on screens a
// fraction the size of the 3440 by 1440 this is read on - at 1.0 the text came out a
// smear. 1.5 puts a line at about twenty pixels, which is roughly the apparent size the
// original had on the displays it was written for. We ship no font file, so scaling the
// built-in atlas is the whole of what is available.
//
// The same factor scales the windows and every padding below, so the proportions of the
// reference survive: a wide strip, not a column.
constexpr float kUiScale = 1.5f;

// The reference window, from the root view element of uTank2's own view definition. Every
// plugin window defaults to it; the host's is narrower because it holds one tab.
constexpr float kReferenceWidth = 856.0f;
constexpr float kReferenceHeight = 210.0f;
constexpr float kHostWidth = 640.0f;

// ImGui's own ceiling is 512 columns and it asserts above it. No panel the host could
// sensibly publish is anywhere near this, so the clamp doubles as a sanity limit on a
// column list that arrived corrupt.
constexpr size_t kMaxColumns = 64;

// Decal ran fifteen plugins on this machine. A snapshot naming more owners than this has
// not come from a plugin framework, it has come from a corrupt message, and a window per
// entry would be the wrong response to it.
constexpr size_t kMaxOwners = 32;

// When a snapshot stops counting as current.
//
// The host ticks ten times a second, so three seconds is thirty missed publishes - far
// past anything scheduling jitter or a slow pipe write could account for, and well short
// of the time it takes a player to notice that the numbers have stopped meaning anything.
// Being wrong in the generous direction costs a warning that arrives late; being wrong in
// the other direction cries stale at a host that is merely busy, which would teach the
// player to ignore the warning.
constexpr int64_t kStaleAfterMs = 3000;

// How faded the panels go once the snapshot is stale. Far enough to read as inactive at a
// glance, not so far that the muted tone above stops being legible.
constexpr float kStaleAlpha = 0.70f;

// VirindiTank's notebook pages, in its order. Used for nothing but the order tabs appear
// in, so a panel the host names after one of these lands where a uTank2 user expects it.
constexpr const char* kPageOrder[] = {
    "Options", "Profiles", "Vitals", "Monsters", "Items", "Consumables", "Buffs", "Route", "Meta",
};

// Where an unrecognised title sorts: after every page uTank2 has, in the order the host
// published it.
constexpr int kUnranked = 1000;

// What the host calls a panel, and what VirindiTank calls the same page.
//
// Chrome only. Nothing here decides what a panel contains or how it is drawn, and a title
// with no entry passes through untouched - so the host can still add a panel without this
// file changing, which was the point of panels being generic in the first place.
struct TitleAlias {
    const char* published;
    const char* shown;
};

constexpr TitleAlias kTitleAliases[] = {
    {nullptr, nullptr},
};

// The most controls drawn in one window, and the most options in one list. uTank2's busiest
// page has about thirty; past these a snapshot is corrupt rather than ambitious, and a
// window of a thousand check boxes would cover the game.
constexpr size_t kMaxControls = 64;
constexpr size_t kMaxOptions = 256;

// How wide a slider, list or text box is drawn, before its label - uTank2's own edit boxes
// are about this wide on its Options page.
constexpr float kFieldWidth = 110.0f;

// The largest slider bound drawn. ImGui asserts on double ranges near the limits of the
// type, and no setting a player could want is anywhere near this.
constexpr double kSliderLimit = 1.0e12;

// A slider or text box the player is part way through changing.
//
// The host's value is what is drawn at every other time. While the player's hands are on
// a control it cannot be: the host republishes four times a second, and a text box reset
// to the host's copy mid-word would be impossible to type into. So the edit is held here
// from the moment the control becomes active and sent once, when the player lets go - the
// host hears the value they settled on, not every value the slider passed through.
struct ActiveEdit {
    std::string text;
    double number = 0.0;

    // The frame it was last drawn in. An edit whose control has vanished - its window
    // closed, its plugin gone - is dropped at the end of the frame rather than kept until
    // the same id turns up again with the old text in it.
    int frame = 0;
};

// The player's arrangement of the screen, and the only thing kept between frames.
//
// Everything else drawn here is re-derived from the snapshot every frame, because the
// host owns it. This is different in kind: whether a window is open is not a fact about
// the session, the host would learn nothing from being told, and a plugin framework whose
// windows all reopened on every publish would be unusable. So it lives here, keyed by the
// owner whose window it is.
struct Arrangement {
    // Decal's bar as the player set it here: -1 until they touch it, and then what they chose.
    int bar_state = -1;      // 1 compact, 0 expanded
    int bar_dock = -1;       // 0 top, 1 left, 2 right
    int bar_length = -1;
    float bar_side_y = -1.0f;
    size_t bar_scroll = 0;

    // Decal's bar put down a side where it starts, 120 down, before the host said where
    // AC:Unreal's own plugin bar is: placed again once it does, unless slid along since. -1 when not.
    float bar_side_unplaced_y = -1.0f;

    // VVS's bar across the screen rather than down it, as its blue arrow sets: -1 until the
    // player turns it, and then what they chose.
    int vvs_horizontal = -1;

    // VVS's bar came up where VVS starts it - nothing in vvs.s3db, nothing in the ini - before the
    // host said where AC:Unreal's own plugin bar is: it is moved clear of that once it is known,
    // unless the player has moved it first.
    bool vvs_bar_at_start = false;

    // A plugin with no entry is open. That is what makes the first frame a full set of
    // windows rather than an empty bar, without anything having to enumerate the plugins
    // in advance. The host's own window, the empty owner, starts closed; see IsOpen.
    std::map<std::string, bool> open;

    // The last open request seen from each view: a higher one opens the window again.
    std::map<std::string, int> open_requests;

    // The last toggle request seen from each view: a higher one turns the window over.
    std::map<std::string, int> toggle_requests;

    // The last close request seen from each view: a higher one closes the window.
    std::map<std::string, int> close_requests;

    // Keyed by EditKey. A map rather than one slot because moving from one text box to
    // another activates the second in the same frame the first lets go, and one slot
    // would hand the first box's text to the second.
    std::map<std::string, ActiveEdit> edits;
};

Arrangement& TheArrangement()
{
    static Arrangement arrangement;
    return arrangement;
}

}  // namespace

// Decal's bar as the player set it - compact or expanded, which edge, how long - in the ini
// under [DecalBar][Bar], beside where each window was left.
void RegisterOverlaySettings()
{
    if (ImGui::FindSettingsHandler("DecalBar") != nullptr)
        return;

    ImGuiSettingsHandler handler;
    handler.TypeName = "DecalBar";
    handler.TypeHash = ImHashStr("DecalBar");
    handler.ReadOpenFn = [](ImGuiContext*, ImGuiSettingsHandler*, const char*) -> void* { return &TheArrangement(); };
    handler.ReadLineFn = [](ImGuiContext*, ImGuiSettingsHandler*, void* entry, const char* line) {
        auto* arrangement = static_cast<Arrangement*>(entry);
        int value = 0;
        if (std::sscanf(line, "State=%d", &value) == 1) arrangement->bar_state = value == 0 ? 0 : 1;
        else if (std::sscanf(line, "Dock=%d", &value) == 1) arrangement->bar_dock = std::clamp(value, 0, 2);
        else if (std::sscanf(line, "Length=%d", &value) == 1) arrangement->bar_length = std::max(112, value);
        else if (std::sscanf(line, "VvsHorizontal=%d", &value) == 1) arrangement->vvs_horizontal = value != 0 ? 1 : 0;
    };
    handler.WriteAllFn = [](ImGuiContext*, ImGuiSettingsHandler* self, ImGuiTextBuffer* out) {
        const Arrangement& arrangement = TheArrangement();
        if (arrangement.bar_state < 0 && arrangement.bar_dock < 0 && arrangement.bar_length < 0 && arrangement.vvs_horizontal < 0)
            return;
        out->appendf("[%s][Bar]\n", self->TypeName);
        if (arrangement.bar_state >= 0) out->appendf("State=%d\n", arrangement.bar_state);
        if (arrangement.bar_dock >= 0) out->appendf("Dock=%d\n", arrangement.bar_dock);
        if (arrangement.bar_length > 0) out->appendf("Length=%d\n", arrangement.bar_length);
        if (arrangement.vvs_horizontal >= 0) out->appendf("VvsHorizontal=%d\n", arrangement.vvs_horizontal);
        out->append("\n");
    };
    ImGui::AddSettingsHandler(&handler);
}

namespace {

// A plugin's window with no entry is open, so the first frame is a full set of them. The
// host's is the exception: it is diagnostics, asked for with Ctrl+click on the bar's arrow,
// and starts closed - as does any window the host marks so, such as Decal's own.
bool IsOpen(const Arrangement& arrangement, const std::string& owner, bool starts_closed = false)
{
    const auto found = arrangement.open.find(owner);
    if (found == arrangement.open.end())
        return !owner.empty() && !starts_closed;
    return found->second;
}

// One entry on the bar and one window: a plugin, or the host when owner is empty.
struct OwnerGroup {
    std::string owner;

    // What the host declared for this plugin, or null for an owner known only from its
    // panels - an older host, or a panel whose owner the host did not list. Points into the
    // State being drawn, which outlives the frame.
    const PluginWindow* window = nullptr;

    std::vector<size_t> panels;  // indices into State::panels
};

float Scaled(float value)
{
    return value * kUiScale;
}

ImVec2 Scaled(float x, float y)
{
    return ImVec2(x * kUiScale, y * kUiScale);
}

// Applied every frame rather than once.
//
// It is idempotent and costs a few dozen float writes, and in exchange there is no flag
// that can fall out of step and no assumption about what the hook layer did to the style
// between frames. Starting from the dark preset rather than from whatever is there means
// the result does not depend on the caller's taste either: every colour below is then
// either set here or a known dark default.
void ApplyVirindiStyle()
{
    ImGuiStyle& style = ImGui::GetStyle();
    ImGui::StyleColorsDark(&style);

    style.Colors[ImGuiCol_Text] = kParchment;
    style.Colors[ImGuiCol_TextDisabled] = kParchmentDim;
    style.Colors[ImGuiCol_WindowBg] = kBody;
    style.Colors[ImGuiCol_ChildBg] = ImVec4(0.0f, 0.0f, 0.0f, 0.0f);
    style.Colors[ImGuiCol_PopupBg] = ImVec4(kBody.x, kBody.y, kBody.z, 0.98f);
    style.Colors[ImGuiCol_Border] = kMetalPale;
    style.Colors[ImGuiCol_BorderShadow] = ImVec4(0.0f, 0.0f, 0.0f, 0.0f);

    style.Colors[ImGuiCol_FrameBg] = kSunken;
    style.Colors[ImGuiCol_FrameBgHovered] = ImVec4(0.220f, 0.161f, 0.094f, 1.00f);
    style.Colors[ImGuiCol_FrameBgActive] = ImVec4(0.278f, 0.204f, 0.114f, 1.00f);

    // The title bar takes the tab colours, because in Decal the window frame and the
    // notebook are cut from the same strip of brass.
    style.Colors[ImGuiCol_TitleBg] = kTabIdle;
    style.Colors[ImGuiCol_TitleBgActive] = kTabActive;
    style.Colors[ImGuiCol_TitleBgCollapsed] = kTabIdleShade;

    style.Colors[ImGuiCol_ScrollbarBg] = ImVec4(0.098f, 0.078f, 0.051f, 0.60f);
    style.Colors[ImGuiCol_ScrollbarGrab] = kMetalDim;
    style.Colors[ImGuiCol_ScrollbarGrabHovered] = kMetalPale;
    style.Colors[ImGuiCol_ScrollbarGrabActive] = kGold;

    style.Colors[ImGuiCol_CheckMark] = kGold;
    style.Colors[ImGuiCol_CheckboxSelectedBg] = kMetalWarm;
    style.Colors[ImGuiCol_SliderGrab] = kTabActive;
    style.Colors[ImGuiCol_SliderGrabActive] = kTabActiveLit;

    style.Colors[ImGuiCol_Button] = kMetalWarm;
    style.Colors[ImGuiCol_ButtonHovered] = kTabActiveShade;
    style.Colors[ImGuiCol_ButtonActive] = kTabActive;

    style.Colors[ImGuiCol_Header] = kTabIdle;
    style.Colors[ImGuiCol_HeaderHovered] = kTabActiveShade;
    style.Colors[ImGuiCol_HeaderActive] = kTabActive;

    style.Colors[ImGuiCol_Separator] = kMetalDim;
    style.Colors[ImGuiCol_SeparatorHovered] = kMetalPale;
    style.Colors[ImGuiCol_SeparatorActive] = kGold;

    style.Colors[ImGuiCol_ResizeGrip] = kMetalDim;
    style.Colors[ImGuiCol_ResizeGripHovered] = kMetalPale;
    style.Colors[ImGuiCol_ResizeGripActive] = kGold;

    style.Colors[ImGuiCol_Tab] = kTabIdle;
    style.Colors[ImGuiCol_TabHovered] = kTabActiveLit;
    style.Colors[ImGuiCol_TabSelected] = kTabActive;
    style.Colors[ImGuiCol_TabDimmed] = kTabIdleShade;
    style.Colors[ImGuiCol_TabDimmedSelected] = kTabActiveShade;

    // Decal tells the selected tab apart by its colour alone. ImGui's overline is a
    // different idiom and reads as a second selection marker beside the amber, so both
    // the colour and the thickness below take it out.
    style.Colors[ImGuiCol_TabSelectedOverline] = ImVec4(0.0f, 0.0f, 0.0f, 0.0f);
    style.Colors[ImGuiCol_TabDimmedSelectedOverline] = ImVec4(0.0f, 0.0f, 0.0f, 0.0f);

    style.Colors[ImGuiCol_TableHeaderBg] = ImVec4(0.353f, 0.227f, 0.125f, 1.00f);
    style.Colors[ImGuiCol_TableBorderStrong] = kMetalDim;
    style.Colors[ImGuiCol_TableBorderLight] = ImVec4(0.278f, 0.239f, 0.149f, 1.00f);
    style.Colors[ImGuiCol_TableRowBg] = ImVec4(0.129f, 0.098f, 0.063f, 0.55f);
    style.Colors[ImGuiCol_TableRowBgAlt] = ImVec4(0.180f, 0.137f, 0.086f, 0.55f);

    style.Colors[ImGuiCol_TextSelectedBg] = kTabIdle;
    style.Colors[ImGuiCol_NavCursor] = kGold;
    style.Colors[ImGuiCol_DragDropTarget] = kGold;

    // Decal draws its controls as flat bitmaps, so nothing is rounded - except the tabs,
    // whose images carry a six pixel shaped cap at each end.
    style.WindowRounding = 0.0f;
    style.ChildRounding = 0.0f;
    style.PopupRounding = 0.0f;
    style.FrameRounding = 0.0f;
    style.GrabRounding = 0.0f;
    style.ScrollbarRounding = 0.0f;
    style.TabRounding = Scaled(3.0f);

    style.WindowBorderSize = 1.0f;
    style.FrameBorderSize = 1.0f;
    style.TabBarBorderSize = Scaled(2.0f);
    style.TabBarOverlineSize = 0.0f;

    // Tight, because the reference fits nine pages of controls into 210 pixels by pitching
    // its rows every sixteen. Scaled with the font, or the text would grow while the gaps
    // between it stayed put and the density would be lost.
    style.WindowPadding = Scaled(6.0f, 4.0f);
    style.FramePadding = Scaled(4.0f, 2.0f);
    style.ItemSpacing = Scaled(6.0f, 3.0f);
    style.ItemInnerSpacing = Scaled(4.0f, 3.0f);
    style.CellPadding = Scaled(4.0f, 1.0f);
    style.IndentSpacing = Scaled(12.0f);
    style.ScrollbarSize = Scaled(10.0f);
    style.GrabMinSize = Scaled(10.0f);

    // The cursor drawn over our windows, sized to sit near the game's own.
    style.MouseCursorScale = 1.25f;

    // ScaleAllSizes is the usual way to do the above, and is wrong here: it multiplies
    // every size member including the ones not set explicitly, so applying it once a
    // frame would compound until the window was a single enormous border.
}

const ImVec4& ColourForTone(Tone tone)
{
    switch (tone)
    {
        case Tone::Good: return kToneGood;
        case Tone::Bad: return kToneBad;
        case Tone::Muted: return kToneMuted;

        // ToneFromWire is the only place a wire value is clamped, and this is not a second
        // clamp: a switch that can fall off its end would return a dangling reference, so
        // the mapping is made total. A Tone cast from an integer rather than parsed lands
        // here and draws plainly, which is the right failure.
        case Tone::Normal:
        default:
            return ImGui::GetStyleColorVec4(ImGuiCol_Text);
    }
}

// Host strings are data, never format strings - one stray percent sign in an item name
// would otherwise walk the varargs.
void TextFrom(const std::string& text, const ImVec4& colour)
{
    ImGui::TextColored(colour, "%s", text.c_str());
}

// What the bar button and the window title say: the title the plugin declared, or else its
// name. The host has no name of its own on the wire - its panels carry an empty owner - so
// it is given one here.
const char* ShownName(const OwnerGroup& group)
{
    if (group.owner.empty())
        return "Host";

    if (group.window != nullptr && !group.window->title.empty())
        return group.window->title.c_str();

    return group.owner.c_str();
}

// Everything after ### is identity: it is what ImGui remembers the window's position by.
// It is spelt with the owner, never the title, so a plugin that retitles its window keeps
// its place on screen, and a plugin that happens to be called "Host" gets its own window
// rather than the host's.
std::string WindowLabel(const OwnerGroup& group)
{
    std::string label = ShownName(group);
    label += "###window:";
    label += group.owner;
    return label;
}

// What the tab says. The returned pointer belongs either to the alias table or to the
// panel, so it outlives the call either way.
const char* ShownTitle(const Panel& panel)
{
    for (const TitleAlias& alias : kTitleAliases)
    {
        if (alias.published != nullptr && alias.shown != nullptr && panel.title == alias.published)
            return alias.shown;
    }

    // A panel with no title still needs something on its tab, or there is nothing to
    // click.
    return panel.title.empty() ? "Panel" : panel.title.c_str();
}

int PageRank(const char* shown)
{
    for (int rank = 0; rank < static_cast<int>(std::size(kPageOrder)); ++rank)
    {
        if (std::string_view(kPageOrder[rank]) == shown)
            return rank;
    }

    return kUnranked;
}

// The group for an owner, made if there is room for it; null once there is not, with the
// owner counted among the dropped. The pointer is only good until the next call.
OwnerGroup* GroupFor(std::vector<OwnerGroup>& groups, const std::string& owner, std::vector<std::string>& dropped)
{
    for (OwnerGroup& candidate : groups)
    {
        if (candidate.owner == owner)
            return &candidate;
    }

    if (groups.size() >= kMaxOwners)
    {
        // Counted once per owner, not once per panel, so "+3 more" on the bar means three
        // plugins.
        if (std::find(dropped.begin(), dropped.end(), owner) == dropped.end())
            dropped.push_back(owner);

        return nullptr;
    }

    groups.push_back(OwnerGroup{owner, nullptr, {}});
    return &groups.back();
}

// The windows to draw: the host first, then every plugin the host declared, in its order,
// then any owner known only from its panels. Within an owner, uTank2's page order for the
// panels that correspond to one of its pages, then everything else as published.
//
// The declared list is what puts a quiet plugin on the bar. Before it existed, windows came
// from whichever panels arrived, so a plugin with nothing to report had no window and could
// not be found - Decal listed every plugin it had loaded, busy or not.
//
// The host is always present even when it owns no panel, because its window holds the
// status, which exists whether or not any plugin does. The sort within an owner is stable,
// so the host's own ordering is what breaks ties - it is the only opinion available about
// panels uTank2 has no page for, and overriding it would be inventing one.
std::vector<OwnerGroup> GroupByOwner(const State& state, size_t& owners_dropped)
{
    std::vector<OwnerGroup> groups;
    groups.push_back(OwnerGroup{std::string(), nullptr, {}});
    std::vector<std::string> dropped;

    for (const PluginWindow& window : state.windows)
    {
        // The host's own window is drawn from the status, and it has no runtime switches for
        // a declaration to describe. An entry claiming the empty owner is not a plugin.
        if (window.owner.empty())
            continue;

        // A repeated owner keeps its first declaration. Merging two would draw controls the
        // plugin declared once as if it had declared them twice.
        OwnerGroup* group = GroupFor(groups, window.owner, dropped);
        if (group != nullptr && group->window == nullptr)
            group->window = &window;
    }

    for (size_t index = 0; index < state.panels.size(); ++index)
    {
        OwnerGroup* group = GroupFor(groups, state.panels[index].owner, dropped);
        if (group != nullptr)
            group->panels.push_back(index);
    }

    owners_dropped = dropped.size();

    for (OwnerGroup& group : groups)
    {
        std::stable_sort(group.panels.begin(), group.panels.end(), [&state](size_t left, size_t right) {
            return PageRank(ShownTitle(state.panels[left])) < PageRank(ShownTitle(state.panels[right]));
        });
    }

    return groups;
}

// The tab's label, whose identity ImGui uses to remember which tab is open and how wide
// its columns are. Everything after ### is identity and is not drawn.
//
// The key is used when the host supplies one, so reordering or renaming panels between
// publishes no longer moves the player to a different tab. Falling back to the index keeps
// titles that are empty or shared from collapsing two panels into one tab, which is what
// would happen if the visible text were the whole label.
std::string TabLabel(const Panel& panel, size_t index)
{
    std::string label = ShownTitle(panel);
    label += "###panel:";
    label += panel.key.empty() ? std::to_string(index) : panel.key;
    return label;
}

// How old the snapshot is, or -1 when that cannot be known: nothing published yet, or a
// host that left published_ms at zero.
//
// Reading the clock here is not the local state the design forbids. Nothing is remembered
// between frames; the answer is derived from the snapshot each time, which is precisely
// why published_ms is on the wire.
int64_t SnapshotAgeMs(const State& state)
{
    if (state.revision == 0 || state.published_ms <= 0)
        return -1;

    const int64_t now_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                               std::chrono::system_clock::now().time_since_epoch())
                               .count();

    // The host stamps the time and the overlay reads it in a different process, so a
    // snapshot can appear to arrive a few milliseconds before it was sent. Negative age is
    // meaningless to a reader, so it reads as brand new.
    return std::max<int64_t>(0, now_ms - state.published_ms);
}

void DrawPanel(const Panel& panel)
{
    // A panel that declares no columns can still be carrying rows, so the width comes from
    // the widest row rather than the panel being refused. Losing the headings is a far
    // smaller failure than losing the contents.
    size_t column_count = panel.columns.size();
    if (column_count == 0)
    {
        for (const Row& row : panel.rows)
            column_count = std::max(column_count, row.cells.size());
    }

    column_count = std::min(column_count, kMaxColumns);

    if (column_count == 0)
    {
        ImGui::TextDisabled("This panel has nothing in it.");
        return;
    }

    // Saved column widths are on, which they could not be before Panel::key existed: ImGui
    // keys them by table identity, and the identity now comes from the key rather than the
    // panel's position in the list. Whether they survive the session at all is the hook
    // layer's business - it owns whether the context has an ini file.
    //
    // Row backgrounds alternate and the only rules are between columns, which is how
    // Decal's own lists are drawn: a full grid at this row pitch turns into noise.
    const ImGuiTableFlags flags =
        ImGuiTableFlags_RowBg | ImGuiTableFlags_BordersInnerV | ImGuiTableFlags_Resizable |
        ImGuiTableFlags_ScrollY | ImGuiTableFlags_SizingStretchProp;

    // Zero height with ScrollY fills the rest of the tab, which is right because the table
    // is the last thing in it. In a window this short that is the difference between a
    // panel that scrolls and a panel that runs off the bottom edge.
    if (!ImGui::BeginTable("rows", static_cast<int>(column_count), flags, ImVec2(0.0f, 0.0f)))
        return;

    for (size_t column = 0; column < column_count; ++column)
    {
        const char* heading = column < panel.columns.size() ? panel.columns[column].c_str() : "";
        ImGui::TableSetupColumn(heading);
    }

    // With no headings there is no header row to freeze, and an empty one would only take a
    // line of the scene for nothing.
    if (!panel.columns.empty())
    {
        ImGui::TableSetupScrollFreeze(0, 1);
        ImGui::TableHeadersRow();
    }

    // Row::id is deliberately untouched. Making a row selectable would mean remembering
    // which row is selected, and that memory belongs to the host or it drifts from it
    // across a publish. When a row command exists it goes through Emit with the window's
    // owner and the id as row_id, like every other command; there is not one yet.
    for (const Row& row : panel.rows)
    {
        ImGui::TableNextRow();

        const ImVec4& colour = ColourForTone(row.tone);
        for (size_t column = 0; column < column_count; ++column)
        {
            // False means the column is clipped or hidden, and the cell is not worth
            // formatting.
            if (!ImGui::TableSetColumnIndex(static_cast<int>(column)))
                continue;

            // A short row leaves the remaining cells blank; a long one has its surplus
            // dropped. Neither is allowed to change the table's width, which is fixed at
            // BeginTable - one bad row must not shear the panel below it.
            if (column < row.cells.size())
                TextFrom(row.cells[column], colour);
        }
    }

    ImGui::EndTable();
}

// One tab per panel, inside a tab bar the caller has already begun.
void DrawPanelTabs(const State& state, const std::vector<size_t>& panels)
{
    for (size_t index : panels)
    {
        const Panel& panel = state.panels[index];

        if (ImGui::BeginTabItem(TabLabel(panel, index).c_str()))
        {
            // Panels share the table id "rows", so the key - or the index, when the host
            // published none - is what keeps their scroll positions and column widths
            // apart.
            if (panel.key.empty())
                ImGui::PushID(static_cast<int>(index));
            else
                ImGui::PushID(panel.key.c_str());

            DrawPanel(panel);
            ImGui::PopID();
            ImGui::EndTabItem();
        }
    }
}

void Field(const char* label, const std::string& value)
{
    ImGui::TableNextRow();
    ImGui::TableSetColumnIndex(0);
    ImGui::TextDisabled("%s", label);
    ImGui::TableSetColumnIndex(1);

    // An empty string is the host having nothing to say, which is worth distinguishing from
    // a blank line the reader might take for a rendering fault.
    if (value.empty())
        ImGui::TextDisabled("unknown");
    else
        TextFrom(value, ImGui::GetStyleColorVec4(ImGuiCol_Text));
}

void Field(const char* label, int64_t value)
{
    ImGui::TableNextRow();
    ImGui::TableSetColumnIndex(0);
    ImGui::TextDisabled("%s", label);
    ImGui::TableSetColumnIndex(1);
    ImGui::Text("%lld", static_cast<long long>(value));
}

void MalformedField(int64_t value)
{
    ImGui::TableNextRow();

    // A non-zero count here means a decoder is reading the wire wrongly, which is the one
    // number on this tab that demands action. A coloured digit in a column of digits is
    // easy to slide past, so the row gets a background as well, and the reason is spelt out
    // beside it - the number alone tells nobody what to do about it.
    if (value != 0)
        ImGui::TableSetBgColor(ImGuiTableBgTarget_RowBg0, ImGui::GetColorU32(ImVec4(0.45f, 0.10f, 0.10f, 0.65f)));

    ImGui::TableSetColumnIndex(0);
    if (value != 0)
        ImGui::TextColored(kToneBad, "Malformed");
    else
        ImGui::TextDisabled("Malformed");

    ImGui::TableSetColumnIndex(1);
    if (value != 0)
        ImGui::TextColored(kToneBad, "%lld  -  a decoder is misreading the wire", static_cast<long long>(value));
    else
        ImGui::Text("%lld", static_cast<long long>(value));
}

void AgeField(int64_t age_ms, bool stale)
{
    ImGui::TableNextRow();
    ImGui::TableSetColumnIndex(0);
    ImGui::TextDisabled("Last update");
    ImGui::TableSetColumnIndex(1);

    if (age_ms < 0)
    {
        // The host published but stamped no time, so nothing here can be said about whether
        // the figures are current. Saying that is better than implying they are.
        ImGui::TextDisabled("not stamped");
        return;
    }

    const ImVec4& colour = stale ? kToneBad : ImGui::GetStyleColorVec4(ImGuiCol_Text);

    // Tenths below a minute, because that is the range where the difference between a live
    // snapshot and a stuck one is being judged; past that the precision is noise.
    if (age_ms < 60000)
        ImGui::TextColored(colour, "%.1f s ago", static_cast<double>(age_ms) / 1000.0);
    else
        ImGui::TextColored(colour, "%lld min ago", static_cast<long long>(age_ms / 60000));
}

// Label and value pairs that take no more width than they need, so two of these sit side
// by side. NoHostExtendX is what stops the first one claiming the whole strip.
ImGuiTableFlags PairTableFlags()
{
    return ImGuiTableFlags_SizingFixedFit | ImGuiTableFlags_NoHostExtendX |
           ImGuiTableFlags_NoSavedSettings;
}

void DrawStatus(const State& state, int64_t age_ms, bool stale)
{
    // Revision, not server_connected, answers "has the host ever spoken to us" - the
    // snapshot says nothing about the pipe, so its absence is the only evidence there is.
    // Everything below would read as a real measurement of zero, so the banner stands alone
    // until there is something true to put under it.
    if (state.revision == 0)
    {
        ImGui::TextUnformatted("Waiting for the host.");
        ImGui::TextDisabled("The overlay is drawing, but nothing has arrived over the pipe yet.");
        return;
    }

    const Status& status = state.status;

    // Side by side, not stacked. The reference window is 210 pixels tall, so height is the
    // scarce dimension: stacked, the counters would sit below the bottom edge of a window
    // of these proportions, and the whole point of the shape is that it does not cover the
    // scene.
    ImGui::BeginGroup();
    ImGui::TextColored(kGold, "Session");

    if (ImGui::BeginTable("fields", 2, PairTableFlags()))
    {
        // "Game server" rather than "connected", because the pipe from the host is a
        // different question with a different answer and the snapshot only speaks to one of
        // them. A reader who has to guess which is being reported learns nothing.
        ImGui::TableNextRow();
        ImGui::TableSetColumnIndex(0);
        ImGui::TextDisabled("Game server");
        ImGui::TableSetColumnIndex(1);
        if (status.server_connected)
            ImGui::TextColored(kToneGood, "connected");
        else
            ImGui::TextColored(kToneBad, "not connected");

        Field("Server", status.server);
        Field("Character", status.character);
        Field("Position", status.position);

        // A fact, not a switch. Whether plugins may act is fixed when the host starts and
        // nothing at runtime changes it, so a tick box here - even a disabled one - would be
        // a control that does not control anything. The tooltip says where it is set,
        // which is the question a reader of a "no" will have.
        ImGui::TableNextRow();
        ImGui::TableSetColumnIndex(0);
        ImGui::TextDisabled("Plugins may act");
        ImGui::TableSetColumnIndex(1);
        if (status.acting)
            ImGui::TextColored(kToneGood, "yes");
        else
            ImGui::TextColored(kToneMuted, "no");
        ImGui::SetItemTooltip("Set when the host starts; restart it to change.");

        ImGui::EndTable();
    }

    ImGui::EndGroup();

    ImGui::SameLine(0.0f, Scaled(28.0f));

    ImGui::BeginGroup();
    ImGui::TextColored(kGold, "Counters");

    if (ImGui::BeginTable("counters", 2, PairTableFlags()))
    {
        Field("Messages in", status.messages_in);
        Field("Messages out", status.messages_out);
        Field("Objects", status.objects);
        MalformedField(status.malformed);

        // Revision separates a host that has gone quiet from one still sending the same
        // figures; the age says whether quiet means idle or stuck. Neither number can be
        // worked out from the counters above.
        Field("Revision", state.revision);
        AgeField(age_ms, stale);

        ImGui::EndTable();
    }

    ImGui::EndGroup();
}

// The one way a command leaves this file.
//
// Every command is stamped with the owner of the window it was clicked in, because that is
// how the host decides which plugin hears it. Funnelling them through here means a command
// added later - from a row, say, with its id filled in - cannot forget the stamp: the
// owner is a parameter, not a field somebody has to remember to set.
void Emit(std::vector<Command>& commands, const std::string& owner, const char* name, const std::string& row_id = std::string())
{
    Command command;
    command.name = name;
    command.owner = owner;
    command.row_id = row_id;
    commands.push_back(std::move(command));
}

// A command from one of a plugin's controls: "set" with the new value, or "press". Goes
// through Emit, so it is stamped with the owner like everything else.
void EmitControl(std::vector<Command>& commands, const std::string& owner, const char* name,
                 const Control& control, const std::string& value)
{
    Emit(commands, owner, name);
    commands.back().control_id = control.id;
    commands.back().value = value;
}

// A number as the plugin wrote it, or the fallback when it is not one. from_chars rather
// than strtod because it ignores the locale: this runs inside the game, and a game that
// has set a comma decimal separator would otherwise read "0.5" as zero.
double ParseNumber(const std::string& text, double fallback)
{
    double number = 0.0;
    const char* first = text.data();
    const char* last = first + text.size();
    const std::from_chars_result result = std::from_chars(first, last, number);

    if (result.ec != std::errc() || result.ptr != last || !std::isfinite(number))
        return fallback;

    return number;
}

// The shortest text that reads back as the same double, in the form the host's parser
// takes. Also locale-free, for the same reason.
std::string FormatNumber(double number)
{
    char buffer[64];
    const std::to_chars_result result = std::to_chars(buffer, buffer + sizeof(buffer), number);
    return result.ec == std::errc() ? std::string(buffer, result.ptr) : std::string("0");
}

bool SliderRangeUsable(const Control& control)
{
    return std::isfinite(control.min) && std::isfinite(control.max) && control.min <= control.max &&
           std::fabs(control.min) <= kSliderLimit && std::fabs(control.max) <= kSliderLimit;
}

// Whole steps show as whole numbers; anything finer, or continuous, to two places.
const char* SliderFormat(double step)
{
    return step >= 1.0 && step == std::floor(step) ? "%.0f" : "%.2f";
}

double SnapToStep(double value, const Control& control)
{
    if (control.step > 0.0)
        value = control.min + std::round((value - control.min) / control.step) * control.step;

    return std::clamp(value, control.min, control.max);
}

// The key an edit is held under. Owner and id together, because two plugins are free to
// call a control the same thing; the separator is a character neither is likely to hold.
std::string EditKey(const std::string& owner, const Control& control)
{
    std::string key = owner;
    key += '\x1f';
    key += control.id;
    return key;
}

// ImGui's text box writes into a fixed buffer; this grows a std::string under it instead,
// which is what ImGui's own std::string helper does. The helper is not compiled in, and
// this is the whole of it that is needed.
int ResizeString(ImGuiInputTextCallbackData* data)
{
    if (data->EventFlag == ImGuiInputTextFlags_CallbackResize)
    {
        std::string* text = static_cast<std::string*>(data->UserData);
        text->resize(static_cast<size_t>(data->BufTextLen));
        data->Buf = text->data();
    }

    return 0;
}

// How wide a control will be, worked out before it is drawn so that the row can wrap in
// front of it rather than after it has already run off the edge.
float ControlWidth(const Control& control)
{
    const ImGuiStyle& style = ImGui::GetStyle();
    const float label = ImGui::CalcTextSize(control.label.c_str(), nullptr, true).x;
    const float label_part = label > 0.0f ? style.ItemInnerSpacing.x + label : 0.0f;

    switch (control.kind)
    {
        case ControlKind::Toggle: return ImGui::GetFrameHeight() + label_part;
        case ControlKind::Button: return label + style.FramePadding.x * 2.0f;
        default: return Scaled(kFieldWidth) + label_part;
    }
}

void DrawSlider(const Control& control, const std::string& label, const std::string& owner,
                Arrangement& arrangement, std::vector<Command>& commands)
{
    // A range that cannot be drawn is shown as what it is. Drawing it anyway would mean
    // inventing bounds the plugin did not give, and ImGui asserts on some of them.
    if (!SliderRangeUsable(control))
    {
        ImGui::TextDisabled("%s: unusable range", control.label.c_str());
        return;
    }

    const std::string key = EditKey(owner, control);
    const auto found = arrangement.edits.find(key);
    const bool editing = found != arrangement.edits.end();

    double value = editing ? found->second.number
                           : std::clamp(ParseNumber(control.value, control.min), control.min, control.max);

    ImGui::SetNextItemWidth(Scaled(kFieldWidth));
    if (ImGui::SliderScalar(label.c_str(), ImGuiDataType_Double, &value, &control.min, &control.max,
                            SliderFormat(control.step), ImGuiSliderFlags_AlwaysClamp))
    {
        value = SnapToStep(value, control);
    }

    if (ImGui::IsItemActive())
    {
        ActiveEdit& edit = arrangement.edits[key];
        edit.number = value;
        edit.frame = ImGui::GetFrameCount();
    }
    else if (editing)
    {
        if (ImGui::IsItemDeactivatedAfterEdit())
            EmitControl(commands, owner, "set", control, FormatNumber(found->second.number));

        arrangement.edits.erase(found);
    }
}

void DrawTextBox(const Control& control, const std::string& label, const std::string& owner,
                 Arrangement& arrangement, std::vector<Command>& commands)
{
    const std::string key = EditKey(owner, control);
    const auto found = arrangement.edits.find(key);
    const bool editing = found != arrangement.edits.end();

    // Not editing, the box shows a copy of the host's text that is thrown away after this
    // frame. ImGui keeps its own copy while the box is active, so the switch to the held
    // edit on the next frame is invisible.
    std::string scratch;
    if (!editing)
        scratch = control.value;

    std::string& text = editing ? found->second.text : scratch;

    ImGui::SetNextItemWidth(Scaled(kFieldWidth));
    ImGui::InputText(label.c_str(), text.data(), text.capacity() + 1, ImGuiInputTextFlags_CallbackResize,
                     ResizeString, &text);

    if (ImGui::IsItemActive())
    {
        ActiveEdit& edit = arrangement.edits[key];
        if (!editing)
            edit.text = text;

        edit.frame = ImGui::GetFrameCount();
    }
    else if (editing)
    {
        if (ImGui::IsItemDeactivatedAfterEdit())
            EmitControl(commands, owner, "set", control, found->second.text);

        arrangement.edits.erase(found);
    }
}

// Returns false when the list is open, which is when the caller must not add a tooltip:
// the last item is then inside the list rather than the list itself.
bool DrawChoice(const Control& control, const std::string& label, const std::string& owner,
                std::vector<Command>& commands)
{
    ImGui::SetNextItemWidth(Scaled(kFieldWidth));
    if (!ImGui::BeginCombo(label.c_str(), control.value.c_str()))
        return true;

    const size_t options = std::min(control.options.size(), kMaxOptions);
    for (size_t option = 0; option < options; ++option)
    {
        const std::string& text = control.options[option];
        const bool chosen = text == control.value;

        // By position, because two options are allowed to read the same, and each still
        // needs to be clickable on its own.
        ImGui::PushID(static_cast<int>(option));

        // Choosing what is already chosen sends nothing: the plugin would only be told
        // what it already knows.
        if (ImGui::Selectable(text.c_str(), chosen) && !chosen)
            EmitControl(commands, owner, "set", control, text);

        if (chosen)
            ImGui::SetItemDefaultFocus();

        ImGui::PopID();
    }

    ImGui::EndCombo();
    return false;
}

// A plugin's controls, in the order it declared them, flowing left to right and wrapping
// at the window's edge - a row of switches across the top of a wide strip, as uTank2's
// Options page lays them out, rather than a column down the side of it.
//
// Every widget reads the value it was given and emits a command; none of them keeps it. A
// change the plugin refuses shows as the control springing back on the next publish. A
// stale snapshot does not disable them: an old snapshot is no evidence that the command
// channel is dead, and refusing input on a guess is worse than a switch that has to be
// thrown twice.
void DrawControls(const PluginWindow& window, Arrangement& arrangement, std::vector<Command>& commands)
{
    const ImGuiStyle& style = ImGui::GetStyle();
    const float gap = style.ItemSpacing.x * 2.0f;
    const float right_edge = ImGui::GetCursorScreenPos().x + ImGui::GetContentRegionAvail().x;
    float previous_right = 0.0f;

    const size_t count = std::min(window.controls.size(), kMaxControls);
    for (size_t index = 0; index < count; ++index)
    {
        const Control& control = window.controls[index];
        const float width = ControlWidth(control);

        if (index > 0 && previous_right + gap + width <= right_edge)
            ImGui::SameLine(0.0f, gap);

        previous_right = ImGui::GetCursorScreenPos().x + width;

        // Identity is the control's id, never its label: a label the plugin rewrites while
        // a slider is held would otherwise tear the slider out of the player's hand. The
        // label is only what is drawn - everything after ### is identity and is not.
        ImGui::PushID(control.id.c_str());
        const std::string label = control.label + "###control";

        // A control with no id can be drawn but not answered: the command would reach the
        // plugin with nothing to say which control it came from. So it is drawn inert,
        // which shows the plugin's author the mistake without inventing an id for it.
        const bool inert = control.id.empty();
        if (inert)
            ImGui::BeginDisabled();

        bool tooltip_allowed = true;

        switch (control.kind)
        {
            case ControlKind::Toggle:
            {
                // A copy that lives for one frame: the widget needs somewhere to write, and
                // the only lasting record of the switch is the plugin's.
                bool on = control.value == "true";
                if (ImGui::Checkbox(label.c_str(), &on))
                    EmitControl(commands, window.owner, "set", control, on ? "true" : "false");
                break;
            }

            case ControlKind::Button:
                if (ImGui::Button(label.c_str()))
                    EmitControl(commands, window.owner, "press", control, std::string());
                break;

            case ControlKind::Slider:
                DrawSlider(control, label, window.owner, arrangement, commands);
                break;

            case ControlKind::Choice:
                tooltip_allowed = DrawChoice(control, label, window.owner, commands);
                break;

            case ControlKind::Text:
                DrawTextBox(control, label, window.owner, arrangement, commands);
                break;
        }

        // Last, after anything that asks ImGui about the item: a tooltip opens a window of
        // its own, and nothing after it should depend on what "the last item" then means.
        if (tooltip_allowed && !control.tooltip.empty())
            ImGui::SetItemTooltip("%s", control.tooltip.c_str());

        if (inert)
            ImGui::EndDisabled();

        ImGui::PopID();
    }

    if (window.controls.size() > count)
        ImGui::TextDisabled("%lld more controls not shown", static_cast<long long>(window.controls.size() - count));
}

// Which bar a window's switch belongs on: Virindi View Service's, down the side, for a view
// the host says is VVS's; Decal's, across the top, for everything else.
bool OnVvsBar(const OwnerGroup& group)
{
    return group.window != nullptr && group.window->has_view && group.window->view.bar == "vvs";
}

// A HUD has no switch on either bar: its plugin shows and hides it from a list of its own.
bool OffTheBars(const OwnerGroup& group)
{
    return group.window != nullptr && group.window->has_view && !group.window->view.show_in_bar;
}

SwitchLook LookOf(const OwnerGroup& group, const Arrangement& arrangement)
{
    const bool open = IsOpen(arrangement, group.owner, group.window != nullptr && group.window->starts_closed);
    const bool faulted = group.window != nullptr && !group.window->enabled;
    return faulted ? SwitchLook::Faulted : open ? SwitchLook::Open : SwitchLook::Closed;
}

std::string IconOf(const OwnerGroup& group)
{
    return group.window != nullptr && group.window->has_view ? group.window->view.icon : std::string();
}

// The title a window goes by on VVS's bar: the view's own, as VVS's tooltip was.
std::string ViewTitleOf(const OwnerGroup& group)
{
    if (group.window != nullptr && group.window->has_view && !group.window->view.title.empty())
        return group.window->view.title;
    return ShownName(group);
}

// How Decal's bar is laid out now: the player's own choice once they have made one, else what
// Decal's registry said, else Decal's defaults.
struct BarLayout {
    bool compact = true;
    int dock = 0;       // 0 top, 1 left, 2 right
    float length = 250.0f;
};

BarLayout LayoutOf(const State& state, const Arrangement& arrangement)
{
    BarLayout layout;
    const DecalBarSettings& saved = state.decal_bar;
    // Decal's own default with nothing in its registry is BarState 0: expanded.
    layout.compact = arrangement.bar_state >= 0 ? arrangement.bar_state == 1 : saved.known && saved.state != 0;
    layout.dock = arrangement.bar_dock >= 0 ? arrangement.bar_dock : saved.known ? saved.dock : 0;
    const int length = arrangement.bar_length > 0 ? arrangement.bar_length : saved.known ? saved.length : 250;
    layout.length = static_cast<float>(std::max(112, length));
    return layout;
}

// What the bar needs to say that is not a switch - the host not there yet, a notice, a decoder
// misreading the wire, a stale snapshot - beside it, in a line of its own.
void DrawBarStatus(const State& state, bool stale, ImVec2 at)
{
    const bool anything = state.revision == 0 || !state.status.notice.empty() || state.status.malformed != 0 || stale;
    if (!anything)
        return;

    ImGui::SetNextWindowPos(at, ImGuiCond_Always);
    const ImGuiWindowFlags flags = ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove |
                                   ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags_NoScrollbar |
                                   ImGuiWindowFlags_NoInputs | ImGuiWindowFlags_NoBackground | ImGuiWindowFlags_NoFocusOnAppearing;
    if (ImGui::Begin("###decalbarstatus", nullptr, flags))
    {
        bool first = true;
        auto next = [&]() {
            if (!first)
                ImGui::SameLine(0.0f, 6.0f);
            first = false;
        };

        // Before the host has said anything the bar would be empty, which looks like a
        // fault. It is a state, so it is said.
        if (state.revision == 0)
        {
            next();
            ImGui::TextDisabled("Waiting for the host");
        }

        // A host notice is a reason not to believe what the windows say.
        if (!state.status.notice.empty())
        {
            next();
            ImGui::TextColored(kToneBad, "%s", state.status.notice.c_str());
        }

        // A non-zero malformed count means a decoder is misreading the wire. It must not be
        // possible to hide that, so it is here, beside the bar that is always on screen.
        if (state.status.malformed != 0)
        {
            next();
            ImGui::TextColored(kToneBad, "Malformed: %lld", static_cast<long long>(state.status.malformed));
        }

        // Likewise stale: the plugin windows fade when the snapshot is old, and without this a
        // player with the host window closed would see the fade and not the reason.
        if (stale)
        {
            next();
            ImGui::TextColored(kToneBad, "Stale");
        }
    }

    ImGui::End();
}

// Decal's bar, as Decal's Inject.dll laid it out: a grip at each end; the gold square that
// switches it between compact - a small square per plugin - and expanded - Decal's 100-pixel
// labelled switches; the switches, paged with arrows when there are more than fit; and the grey
// square that docks it to the top, the left or the right. The grips move it, along its edge
// when it is docked to a side. Ctrl and a click on the first grip opens the host's own status
// window, and on the second any window its plugin keeps off the bar for that grip, neither of
// which Decal had.
void DrawBar(const State& state, const std::vector<OwnerGroup>& groups, size_t owners_dropped,
             bool stale, Arrangement& arrangement)
{
    BarLayout layout = LayoutOf(state, arrangement);
    const bool vertical = layout.dock != 0;
    const ImGuiViewport* viewport = ImGui::GetMainViewport();

    // Inject.dll's expanded width: a hundred pixels, or eighty on a screen no wider than 1024.
    const float expanded = viewport->Size.x > 1024.0f ? 100.0f : 80.0f;
    const float thickness = vertical ? (layout.compact ? 20.0f : expanded) : 23.0f;

    // The switches: the plugins on Decal's bar, in the host's order.
    std::vector<const OwnerGroup*> switches;
    for (const OwnerGroup& group : groups)
    {
        // The host is not a plugin, and Decal listed plugins only; VVS's plugins are on
        // VVS's bar, and a HUD is on neither.
        if (!group.owner.empty() && !OnVvsBar(group) && !OffTheBars(group))
            switches.push_back(&group);
    }

    // Each switch's own extent along the bar, and the two pixels after it, as Inject.dll laid
    // them: across the top 20 wide compact and the expanded width labelled, 21 high; down a side
    // 20 high, 18 wide compact and two less than the bar labelled.
    const float switch_w = vertical ? (layout.compact ? 18.0f : expanded - 2.0f) : (layout.compact ? 20.0f : expanded);
    const float switch_h = vertical ? 20.0f : 21.0f;
    const float each = (vertical ? switch_h : switch_w) + 2.0f;

    // An expanded switch is a hundred pixels: across the top, a bar shorter than one switch
    // and its ends - the player's compact length, say - is lengthened to show one whole, with
    // the pager's arrows when more than one does not fit. Nothing on it is drawn cut off.
    if (!vertical && !layout.compact)
    {
        const float one = 58.0f + each;
        const bool overflows = static_cast<float>(switches.size()) * each > layout.length - 58.0f;
        layout.length = std::max(layout.length, overflows && switches.size() > 1 ? one + 40.0f : one);
    }

    const ImVec2 size = vertical ? ImVec2(thickness, layout.length) : ImVec2(layout.length, thickness);

    const char* name = layout.dock == 0 ? "###decalbar" : layout.dock == 1 ? "###decalbar-left" : "###decalbar-right";

    // Along the top, just right of AC:Unreal's own toolbar, which has the corner Decal's bar had
    // in the retail client; from there where the player drags it.
    const float start = state.decal_bar.known ? std::max(158.0f, static_cast<float>(state.decal_bar.start)) : 158.0f;
    const ImVec2 top_start(viewport->WorkPos.x + start, viewport->WorkPos.y + 4.0f);
    if (!vertical)
    {
        ImGui::SetNextWindowPos(top_start, ImGuiCond_FirstUseEver);
    }
    else
    {
        // Against its edge, where the player slid it along: where Decal's registry has it, or 120
        // down - below AC:Unreal's own plugin bar instead where the two would overlap, as they do
        // on the left with the client's bar where it starts (8, 80 at 100%, 62 wide, to about 250).
        const float x = layout.dock == 1 ? viewport->WorkPos.x : viewport->WorkPos.x + viewport->WorkSize.x - thickness;
        if (arrangement.bar_side_unplaced_y >= 0.0f && state.client_ui.known)
        {
            if (arrangement.bar_side_y == arrangement.bar_side_unplaced_y)
                arrangement.bar_side_y = -1.0f;
            arrangement.bar_side_unplaced_y = -1.0f;
        }

        float along = state.decal_bar.known ? static_cast<float>(state.decal_bar.start) : 120.0f;
        if (!state.decal_bar.known && state.client_ui.known)
        {
            const float work_top = viewport->WorkPos.y - viewport->Pos.y;
            const float left = x - viewport->Pos.x;
            const ClientRect ours{left, work_top + along, left + thickness, work_top + along + layout.length};
            const ClientRect theirs = ClientUiRect(state.client_ui.plugin_bar, state.client_ui.ui_scale, static_cast<int>(viewport->Size.x),
                                                   static_cast<int>(viewport->Size.y));
            along = StartClearOf(ours, theirs, viewport->Size.y) - work_top;
        }

        const bool starting = arrangement.bar_side_y < 0.0f;
        const float y = starting ? viewport->WorkPos.y + along : arrangement.bar_side_y;
        if (starting && !state.decal_bar.known && !state.client_ui.known)
            arrangement.bar_side_unplaced_y = y;
        ImGui::SetNextWindowPos(ImVec2(x, y), ImGuiCond_Always);
    }
    ImGui::SetNextWindowSize(size, ImGuiCond_Always);

    const ImGuiWindowFlags flags = ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoCollapse |
                                   ImGuiWindowFlags_NoScrollbar | ImGuiWindowFlags_NoScrollWithMouse |
                                   ImGuiWindowFlags_NoBackground | ImGuiWindowFlags_NoMove;

    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(0.0f, 0.0f));
    ImGui::PushStyleVar(ImGuiStyleVar_ItemSpacing, ImVec2(0.0f, 0.0f));
    ImGui::PushStyleVar(ImGuiStyleVar_WindowMinSize, ImVec2(1.0f, 1.0f));

    // BarAlpha, from Decal's registry: the whole bar as opaque as Inject.dll's layer was made.
    const float bar_alpha = state.decal_bar.known ? static_cast<float>(std::clamp(state.decal_bar.alpha, 0, 255)) / 255.0f : 1.0f;
    ImGui::PushStyleVar(ImGuiStyleVar_Alpha, ImGui::GetStyle().Alpha * bar_alpha);

    ImVec2 bar_pos = viewport->WorkPos;
    const bool began = ImGui::Begin(name, nullptr, flags);

    // Along the top, where the ini had it if that is on the display; down a side it is placed
    // every frame.
    if (!vertical)
        PlaceOnDisplayOnce(top_start);

    if (began)
    {
        // Always in front of the windows it opens, as Decal's bar was - but not of a menu,
        // which would open behind it.
        if (GImGui->OpenPopupStack.Size == 0)
            ImGui::BringWindowToDisplayFront(ImGui::GetCurrentWindow());

        const BarDrag drag = vertical ? BarDrag::Down : BarDrag::Free;
        const char* first_tip = "Drag to move Decal's bar. Ctrl+click for the host's status.";
        const char* second_tip = "Drag to move Decal's bar. Ctrl+click for the Decal window.";
        const float length = layout.length;

        // The grips, as cBarLayer drew them: four lines a pixel thick, light, dark, a gap, light,
        // dark - at 3 to 7 from the start and 8 to 4 from the end; across the top fifteen pixels
        // long from 4 down, down a side from 4 across to four short of the far side.
        const float span = vertical ? thickness - 8.0f : 15.0f;

        // The first grip, at the start.
        ImGui::SetCursorPos(vertical ? ImVec2(3.0f, 2.0f) : ImVec2(2.0f, 3.0f));
        if (DecalBarGrip("grip-start", first_tip, vertical, drag, span) && ImGui::GetIO().KeyCtrl)
            arrangement.open[std::string()] = !IsOpen(arrangement, std::string());

        // Where Inject.dll put the gold square and the grey one (its layout at 0x1852BF40): along
        // the top, the gold at (12, 3) and the grey 28 from the end; down a side compact, at 12
        // and 28 from the end; down a side expanded, both on the top row at 12 down - the gold on
        // the side the bar is docked to, the grey on the other.
        const float far = thickness - 19.0f;
        const ImVec2 minmax_at = !vertical ? ImVec2(12.0f, 3.0f)
                                 : layout.compact ? ImVec2(3.0f, 12.0f)
                                 : ImVec2(layout.dock == 1 ? 3.0f : far, 12.0f);
        const ImVec2 dock_at = !vertical ? ImVec2(length - 28.0f, 3.0f)
                               : layout.compact ? ImVec2(3.0f, length - 28.0f)
                               : ImVec2(layout.dock == 1 ? far : 3.0f, 12.0f);

        // The gold square: compact or expanded.
        ImGui::SetCursorPos(minmax_at);
        if (DecalBarButton("minmax", layout.compact ? "portal:06005E64" : "portal:06005E65",
                           layout.compact ? "portal:06005E65" : "portal:06005E64",
                           layout.compact ? "Show the switches with their names" : "Show the switches as icons"))
        {
            arrangement.bar_state = layout.compact ? 0 : 1;
            ImGui::MarkIniSettingsDirty();
        }

        // The switches' strip: from 30 to 28 short of the end, or between the pager's arrows,
        // 50 to 48 short, once they take more than that; down an expanded side, from 30 to 12
        // short of the end whether paged or not, the arrows being on the top row.
        const bool side_expanded = vertical && !layout.compact;
        const float total = static_cast<float>(switches.size()) * each;
        const bool paged = total > length - (side_expanded ? 44.0f : 58.0f);
        const float list_start = paged && !side_expanded ? 50.0f : 30.0f;
        const float list_end = side_expanded ? length - 12.0f : paged ? length - 48.0f : length - 28.0f;
        const float list_room = std::max(0.0f, list_end - list_start);
        const size_t fits = std::max<size_t>(1, static_cast<size_t>((list_room + 2.0f) / each));
        const size_t most_first = switches.size() > fits ? switches.size() - fits : 0;
        arrangement.bar_scroll = std::min(arrangement.bar_scroll, most_first);

        if (paged)
        {
            // Decal's pager: gold arrows across the top, green triangles down a side - the arrows
            // at either end of the strip, or on an expanded side's top row either side of its
            // middle.
            const ImVec2 back_at = !vertical ? ImVec2(30.0f, 3.0f)
                                   : layout.compact ? ImVec2(3.0f, 30.0f)
                                   : ImVec2(std::floor(thickness / 2.0f) - 17.0f, 12.0f);
            const ImVec2 on_at = !vertical ? ImVec2(length - 46.0f, 3.0f)
                                 : layout.compact ? ImVec2(3.0f, length - 46.0f)
                                 : ImVec2(std::floor(thickness / 2.0f) + 1.0f, 12.0f);
            ImGui::SetCursorPos(back_at);
            if (DecalBarButton("page-back", vertical ? "portal:060012B2" : "portal:06004C7B", vertical ? "portal:060012B2" : "portal:06004C79", "")
                && arrangement.bar_scroll > 0)
                --arrangement.bar_scroll;
            ImGui::SetCursorPos(on_at);
            if (DecalBarButton("page-on", vertical ? "portal:060012B1" : "portal:06004C7E", vertical ? "portal:060012B1" : "portal:06004C7C", "")
                && arrangement.bar_scroll < most_first)
                ++arrangement.bar_scroll;
        }

        for (size_t i = arrangement.bar_scroll, shown = 0; i < switches.size() && shown < fits; ++i, ++shown)
        {
            const OwnerGroup& group = *switches[i];
            const float along = list_start + static_cast<float>(shown) * each;
            ImGui::SetCursorPos(vertical ? ImVec2(1.0f, along) : ImVec2(along, 1.0f));

            const SwitchLook look = LookOf(group, arrangement);
            const std::string label = ShownName(group);
            const std::string tip = look == SwitchLook::Faulted ? label + ": its controls could not be read. The host log says why." : label;

            // Spelt with the owner, so a plugin called "Host" has its own switch.
            const std::string id = "owner:" + group.owner;
            const bool clicked = layout.compact
                                     ? DecalIconSwitch(id.c_str(), IconOf(group), look, tip.c_str(), ImVec2(switch_w, switch_h))
                                     : DecalLabelSwitch(id.c_str(), label, IconOf(group), look, switch_w, tip.c_str(), switch_h);
            if (clicked)
                arrangement.open[group.owner] = !IsOpen(arrangement, group.owner, group.window != nullptr && group.window->starts_closed);
        }

        (void)owners_dropped;

        // The grey square: docks the bar to the next edge.
        ImGui::SetCursorPos(dock_at);
        if (DecalBarButton("dock", "portal:060012AA", "portal:060012A9",
                           layout.dock == 0 ? "Dock the bar on the left" : layout.dock == 1 ? "Dock the bar on the right" : "Dock the bar along the top"))
        {
            arrangement.bar_dock = (layout.dock + 1) % 3;
            arrangement.bar_side_y = -1.0f;
            ImGui::MarkIniSettingsDirty();
        }

        // The second grip, at the end.
        ImGui::SetCursorPos(vertical ? ImVec2(3.0f, length - 9.0f) : ImVec2(length - 9.0f, 3.0f));
        if (DecalBarGrip("grip-end", second_tip, vertical, drag, span) && ImGui::GetIO().KeyCtrl)
        {
            for (const OwnerGroup& group : groups)
            {
                if (group.window != nullptr && group.window->has_view && group.window->view.opens_from_grip)
                    arrangement.open[group.owner] = !IsOpen(arrangement, group.owner, group.window->starts_closed);
            }
        }

        bar_pos = ImGui::GetWindowPos();
        if (vertical)
            arrangement.bar_side_y = bar_pos.y;
    }

    ImGui::End();
    ImGui::PopStyleVar(4);

    DrawBarStatus(state, stale, vertical ? ImVec2(bar_pos.x + (layout.dock == 1 ? thickness + 2.0f : -160.0f), bar_pos.y)
                                         : ImVec2(bar_pos.x + layout.length + 4.0f, bar_pos.y + 3.0f));
}

// Virindi View Service's bar, as VVS drew it: a 20-pixel cell per view down the left of the
// screen, "ab" first - which steps VVS's primary theme on - and a rule between one plugin's
// views and the next's, in the theme's colours. A click opens or closes a view's window; the
// bar moves only while left Ctrl is held, as a hudified VVS window did.
void DrawVvsBar(const State& state, const std::vector<OwnerGroup>& groups, Arrangement& arrangement)
{
    // Grouped as VVS grouped them - by the plugin that made the view - in the order each
    // plugin first appears.
    std::vector<std::pair<std::string, std::vector<const OwnerGroup*>>> byGroup;
    for (const OwnerGroup& group : groups)
    {
        if (group.owner.empty() || !OnVvsBar(group) || OffTheBars(group))
            continue;

        std::string key = group.window->view.bar_group;
        if (key.empty())
            key = group.owner.substr(0, group.owner.find('/'));

        auto found = std::find_if(byGroup.begin(), byGroup.end(), [&](const auto& g) { return g.first == key; });
        if (found == byGroup.end())
        {
            byGroup.push_back({key, {}});
            found = byGroup.end() - 1;
        }

        found->second.push_back(&group);
    }

    if (byGroup.empty())
        return;

    // VVS sorted the groups by its hash of each plugin's assembly name; a group the host gave
    // no order keeps its place after those it did.
    auto order = [](const std::vector<const OwnerGroup*>& views) -> int64_t {
        const View& view = views.front()->window->view;
        return view.has_bar_order ? static_cast<int64_t>(view.bar_order) : static_cast<int64_t>(INT32_MAX) + 1;
    };
    std::stable_sort(byGroup.begin(), byGroup.end(), [&](const auto& a, const auto& b) { return order(a.second) < order(b.second); });

    // Down the side, unless the player turned it here or VVS's ExtraInfo says it was across.
    const VvsBarSettings& stored = state.vvs_bar;
    const bool across = arrangement.vvs_horizontal >= 0 ? arrangement.vvs_horizontal == 1 : stored.has_horizontal && stored.horizontal;

    // Where VVS put it, (0, 52), or where the player left it in the standard client - its
    // "VirindiViewService:VVS Bar" row in vvs.s3db - until it is moved here. Where VVS put it is
    // where AC:Unreal's own plugin bar starts, on the same edge (8, 80 at 100%, 62 wide): a bar
    // that would overlap it starts below it instead - and one that came up before the host said
    // where that bar is is moved there once it does, if the player has not moved it first.
    const ImGuiViewport* viewport = ImGui::GetMainViewport();
    const ImVec2 vvs_start(viewport->WorkPos.x, viewport->WorkPos.y + 52.0f);
    ImVec2 first = stored.has_position ? ImVec2(viewport->Pos.x + static_cast<float>(stored.x), viewport->Pos.y + static_cast<float>(stored.y)) : vvs_start;
    ImGuiCond when = ImGuiCond_FirstUseEver;
    ImGuiWindow* existing = ImGui::FindWindowByName("###vvsbar");
    const bool creating = existing == nullptr;
    if (!stored.has_position && state.client_ui.known && (creating || arrangement.vvs_bar_at_start))
    {
        // A theme square, and for each plugin a rule and a square a view: 20 pixels each, 4 a rule.
        float length = 20.0f;
        for (const auto& entry : byGroup)
            length += 4.0f + 20.0f * static_cast<float>(entry.second.size());

        const float left = vvs_start.x - viewport->Pos.x;
        const float top = vvs_start.y - viewport->Pos.y;
        const ClientRect ours{left, top, left + (across ? length : 20.0f), top + (across ? 20.0f : length)};
        const ClientRect theirs = ClientUiRect(state.client_ui.plugin_bar, state.client_ui.ui_scale, static_cast<int>(viewport->Size.x),
                                               static_cast<int>(viewport->Size.y));
        const ImVec2 clear(vvs_start.x, viewport->Pos.y + StartClearOf(ours, theirs, viewport->Size.y));

        if (creating)
        {
            first = clear;
        }
        else if (existing->Pos.x == vvs_start.x && existing->Pos.y == vvs_start.y)
        {
            first = clear;
            when = ImGuiCond_Always;
        }
        arrangement.vvs_bar_at_start = false;
    }
    ImGui::SetNextWindowPos(first, when);

    const ImGuiWindowFlags flags =
        ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoCollapse |
        ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags_NoScrollbar |
        ImGuiWindowFlags_NoScrollWithMouse | ImGuiWindowFlags_NoBackground | ImGuiWindowFlags_NoMove;

    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(0.0f, 0.0f));
    ImGui::PushStyleVar(ImGuiStyleVar_ItemSpacing, ImVec2(0.0f, 0.0f));
    ImGui::PushStyleVar(ImGuiStyleVar_WindowMinSize, ImVec2(1.0f, 1.0f));

    if (ImGui::Begin("###vvsbar", nullptr, flags))
    {
        // Always in front of the windows it opens, as VVS's bar was (ForcedZOrder) - but not
        // of a menu, its own coral box's included, which would open behind it.
        if (GImGui->OpenPopupStack.Size == 0)
            ImGui::BringWindowToDisplayFront(ImGui::GetCurrentWindow());

        const ImVec2 was = ImGui::GetWindowPos();
        const bool movable = DecalRevealHeld();

        // Come up where VVS starts it - the ini had no place for it - before the host said where
        // the client's plugin bar is: moved clear of it once it does.
        if (creating && !stored.has_position && !state.client_ui.known && was.x == vvs_start.x && was.y == vvs_start.y)
            arrangement.vvs_bar_at_start = true;

        // The bar is a hudified VVS window: with left Ctrl held its title strip shows - its
        // coral box, and the blue arrow that turns it between down the side and across.
        if (VvsBarBox(across, movable))
        {
            arrangement.vvs_horizontal = across ? 0 : 1;
            ImGui::MarkIniSettingsDirty();
        }
        if (movable)
        {
            // VVS's hot-dog stand: the H.S. button, left of the blue arrow, sets VVS's primary
            // theme to "Minimalist H.S." - which no menu offers and "ab" steps on from.
            if (across)
                ImGui::SameLine(0.0f, 0.0f);
            if (DecalBarButton("hot-dog-stand", "vvs:HudBar.hsicon.png", "vvs:HudBar.hsicon.png", nullptr))
                SetDecalGlobalTheme("Minimalist H.S.");
            if (across)
                ImGui::SameLine(0.0f, 0.0f);
            if (DecalBarButton("orientation", across ? "vvs:HudBar.bluearrow_right_up.png" : "vvs:HudBar.bluearrow_down_up.png",
                               across ? "vvs:HudBar.bluearrow_right_down.png" : "vvs:HudBar.bluearrow_down_down.png",
                               across ? "Set Vertical" : "Set Horizontal"))
            {
                arrangement.vvs_horizontal = across ? 0 : 1;
                ImGui::MarkIniSettingsDirty();
            }
            if (across)
                ImGui::SameLine(0.0f, 4.0f);
        }

        // "Change Global Theme": always first, a group of its own.
        const std::string theme_tip = "Change Global Theme (" + DecalGlobalTheme() + ")";
        if (VvsBarItem("theme", "vvs:HudBar.Icon_TButton.png", false, theme_tip.c_str(), movable))
            CycleDecalGlobalTheme();

        for (const auto& [key, views] : byGroup)
        {
            if (across)
                ImGui::SameLine(0.0f, 0.0f);
            VvsBarRule(!across);
            for (const OwnerGroup* group : views)
            {
                if (across)
                    ImGui::SameLine(0.0f, 0.0f);
                const bool open = IsOpen(arrangement, group->owner, group->window != nullptr && group->window->starts_closed);
                const std::string id = "owner:" + group->owner;
                const std::string title = ViewTitleOf(*group);
                if (VvsBarItem(id.c_str(), IconOf(*group), open, title.c_str(), movable))
                    arrangement.open[group->owner] = !open;
            }
        }

        // On screen, and against the edge it was left at - the left, as VVS made it - until moved;
        // moved, against whichever edges it was pushed to, as any hudified view.
        const ImVec2 now = ImGui::GetWindowPos();
        KeepVvsBarOnScreen(now.x != was.x || now.y != was.y, stored.has_stuck ? &stored.stuck : nullptr);

        // While left Ctrl shows a hudified window's frame, the bar shows it can be moved.
        if (movable)
        {
            const ImVec2 a = ImGui::GetWindowPos();
            const ImVec2 b(a.x + ImGui::GetWindowSize().x, a.y + ImGui::GetWindowSize().y);
            ImGui::GetWindowDrawList()->AddRect(a, b, IM_COL32(0x8F, 0x78, 0x54, 0xFF));
        }
    }

    ImGui::End();
    ImGui::PopStyleVar(3);
}

// Positions and sizes an owner's window for its first appearance and begins it. The caller
// always owes End(), whatever this returns, as with Begin itself.
//
// The cascade steps each window down and right from the last, so a first frame with
// several plugins does not stack every window on the same spot; after that ImGui remembers
// wherever the player put them.
bool BeginOwnerWindow(const OwnerGroup& group, size_t cascade, float reference_width, bool& open)
{
    const ImGuiViewport* viewport = ImGui::GetMainViewport();
    const float step = Scaled(32.0f) * static_cast<float>(cascade);

    const ImVec2 start(viewport->WorkPos.x + Scaled(48.0f) + step, viewport->WorkPos.y + Scaled(64.0f) + step);
    ImGui::SetNextWindowPos(start, ImGuiCond_FirstUseEver);
    ImGui::SetNextWindowSize(Scaled(reference_width, kReferenceHeight), ImGuiCond_FirstUseEver);

    const bool shown = ImGui::Begin(WindowLabel(group).c_str(), &open);
    PlaceOnDisplayOnce(start);
    return shown;
}

// The host's window: Decal's furniture rather than any plugin's. The notice and the
// staleness warning, then the status as the first tab and any panels the host itself
// published after it. Nothing in it emits a command: the host has no switch a player can
// throw at runtime, and pretending otherwise is what the old "Let plugins act" box did.
void DrawHostContents(const State& state, const OwnerGroup& group, int64_t age_ms, bool stale)
{
    // The notice and the staleness warning sit above the tab bar, at full strength and
    // outside the fading below, because both are reasons not to believe what the panels say
    // - they would be worth least in the one place the player is not looking. The rule
    // under them is drawn only when there is something above it to rule off.
    bool warned = false;

    if (!state.status.notice.empty())
    {
        ImGui::PushStyleColor(ImGuiCol_Text, kToneBad);
        ImGui::TextWrapped("%s", state.status.notice.c_str());
        ImGui::PopStyleColor();
        warned = true;
    }

    if (stale)
    {
        ImGui::TextColored(kToneBad, "Stale: the host has not published for %.0f seconds.",
                           static_cast<double>(age_ms) / 1000.0);
        warned = true;
    }

    if (warned)
        ImGui::Separator();

    // Fading the panels is the honest rendering of a snapshot that has stopped being
    // current: the figures are still there to be read, but they no longer look live.
    if (stale)
        ImGui::PushStyleVar(ImGuiStyleVar_Alpha, kStaleAlpha);

    if (ImGui::BeginTabBar("MainTabs"))
    {
        if (ImGui::BeginTabItem("Status###status"))
        {
            DrawStatus(state, age_ms, stale);
            ImGui::EndTabItem();
        }

        DrawPanelTabs(state, group.panels);
        ImGui::EndTabBar();
    }

    // A host that is publishing but has no panels to publish is a real state and an alarming
    // one, so it is said out loud instead of leaving windows that look broken.
    if (state.panels.empty() && state.revision != 0)
        ImGui::TextDisabled("The host has published no panels.");

    if (stale)
        ImGui::PopStyleVar();
}

// A plugin's window: the controls it declared along the top, then its panels as tabs under
// uTank2's chrome. Whatever the plugin is, this is all it gets and all it needs - the
// window knows nothing of which plugin it belongs to beyond the owner it stamps on
// commands.
//
// The controls stay outside the fading for the same reason the host's warnings do: they
// are still usable when the snapshot is old, and drawing them faded would say otherwise.
void DrawPluginContents(const State& state, const OwnerGroup& group, bool stale, Arrangement& arrangement,
                        std::vector<Command>& commands)
{
    const PluginWindow* window = group.window;
    const bool has_controls = window != nullptr && window->enabled && !window->controls.empty();

    if (window != nullptr && !window->enabled)
    {
        ImGui::TextColored(kToneBad, "This plugin's controls could not be read. The host log says why.");
        ImGui::Separator();
    }
    else if (has_controls)
    {
        DrawControls(*window, arrangement, commands);
        ImGui::Separator();
    }

    if (group.panels.empty())
    {
        // Loaded and quiet is a real state, and the reason the window exists at all: it
        // should read as a plugin with nothing to say, not as a window that failed to draw.
        if (!has_controls)
            ImGui::TextDisabled("This plugin has nothing to show yet.");

        return;
    }

    if (stale)
        ImGui::PushStyleVar(ImGuiStyleVar_Alpha, kStaleAlpha);

    if (ImGui::BeginTabBar("MainTabs"))
    {
        DrawPanelTabs(state, group.panels);
        ImGui::EndTabBar();
    }

    if (stale)
        ImGui::PopStyleVar();
}

// Drops the held edit of any control that was not drawn this frame, so an edit cannot
// outlive its control and reappear if the same id comes back.
void ForgetAbandonedEdits(Arrangement& arrangement)
{
    const int frame = ImGui::GetFrameCount();
    for (auto edit = arrangement.edits.begin(); edit != arrangement.edits.end();)
    {
        if (edit->second.frame != frame)
            edit = arrangement.edits.erase(edit);
        else
            ++edit;
    }
}

}  // namespace

std::vector<Command> DrawOverlay(const State& state, bool& visible)
{
    std::vector<Command> commands;

    // Submitting no windows is what hands the input back: a hidden-but-submitted window
    // still hovers, still takes clicks, and the player would be fighting an overlay they
    // cannot see. This hides the bar as well - it is the whole overlay that goes, not one
    // window of it.
    if (!visible)
        return commands;

    // Nor on a display too small to hold a window - a minimized game's, of no size at all.
    // Submitted there, a window kept on screen goes to a corner, and ImGui saves it there.
    const ImVec2 display = ImGui::GetIO().DisplaySize;
    if (!DisplayUsable(display.x, display.y))
        return commands;

    const int64_t age_ms = SnapshotAgeMs(state);
    const bool stale = age_ms >= kStaleAfterMs;

    size_t owners_dropped = 0;
    const std::vector<OwnerGroup> groups = GroupByOwner(state, owners_dropped);
    Arrangement& arrangement = TheArrangement();

    ApplyVirindiStyle();

    // PushFont with a null font keeps the atlas and changes only the size, which is the
    // documented way to do this after NewFrame. The alternative, style.FontScaleMain, is a
    // global the context's owner may have opinions about; this is scoped to our windows and
    // undone before returning.
    ImGui::PushFont(nullptr, ImGui::GetStyle().FontSizeBase * kUiScale);

    // The bar goes first so that a button pressed on it opens or closes its window in the
    // same frame, rather than one frame late.
    // A plugin asking for its window opens it, whatever the player did with it before.
    for (const OwnerGroup& group : groups)
    {
        if (group.window == nullptr || !group.window->has_view)
            continue;
        const int request = group.window->view.open_request;
        auto seen = arrangement.open_requests.find(group.owner);
        if (seen == arrangement.open_requests.end())
        {
            arrangement.open_requests[group.owner] = request;
            if (request > 0)
                arrangement.open[group.owner] = true;
        }
        else if (request > seen->second)
        {
            seen->second = request;
            arrangement.open[group.owner] = true;
        }
        else if (request < seen->second)
        {
            // A plugin reloaded counts from nought again.
            seen->second = request;
        }

        // A key bound to the window turns it over, once for each press.
        const int toggle = group.window->view.toggle_request;
        auto toggled = arrangement.toggle_requests.find(group.owner);
        if (toggled == arrangement.toggle_requests.end())
        {
            arrangement.toggle_requests[group.owner] = toggle;
        }
        else if (toggle != toggled->second)
        {
            if (toggle > toggled->second)
                arrangement.open[group.owner] = !IsOpen(arrangement, group.owner, group.window->starts_closed);
            toggled->second = toggle;
        }

        // The plugin's own Close button.
        const int close = group.window->view.close_request;
        auto closed = arrangement.close_requests.find(group.owner);
        if (closed == arrangement.close_requests.end())
        {
            arrangement.close_requests[group.owner] = close;
        }
        else if (close != closed->second)
        {
            if (close > closed->second)
                arrangement.open[group.owner] = false;
            closed->second = close;
        }
    }

    SetDecalDefaultTheme(state.default_theme);
    SetDecalViewAlpha(state.decal_bar.known ? state.decal_bar.view_alpha : 255);
    DrawBar(state, groups, owners_dropped, stale, arrangement);
    DrawVvsBar(state, groups, arrangement);

    for (size_t cascade = 0; cascade < groups.size(); ++cascade)
    {
        const OwnerGroup& group = groups[cascade];

        bool open = IsOpen(arrangement, group.owner, group.window != nullptr && group.window->starts_closed);
        if (!open)
            continue;

        // A plugin with a Decal view is drawn as that view, in the Decal theme, and nothing
        // else: its panels, if it publishes any, were for windows that had no view to show.
        if (group.window != nullptr && group.window->has_view)
        {
            DrawDecalWindow(*group.window, cascade, open, commands);
            if (!open)
                arrangement.open[group.owner] = false;
            continue;
        }

        // The arrangement's flag is handed to Begin, so the title bar's close button and the
        // bar's button are the same switch rather than two that have to be kept in
        // agreement. Begin returns false when collapsed, and End is still owed then.
        if (BeginOwnerWindow(group, cascade, group.owner.empty() ? kHostWidth : kReferenceWidth, open))
        {
            if (group.owner.empty())
                DrawHostContents(state, group, age_ms, stale);
            else
                DrawPluginContents(state, group, stale, arrangement, commands);
        }

        ImGui::End();

        if (!open)
            arrangement.open[group.owner] = false;
    }

    ForgetAbandonedEdits(arrangement);
    EndDecalFrame();

    ImGui::PopFont();
    return commands;
}

}  // namespace overlay
