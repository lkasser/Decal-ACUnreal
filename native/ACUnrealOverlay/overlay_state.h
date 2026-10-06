// The one thing every part of the overlay agrees on.
//
// The injected DLL knows nothing about looting, spells or the wire protocol. It
// receives a snapshot of what to draw and sends back what the player clicked. Keeping
// that boundary narrow is the whole strategy: the client's renderer is the fragile part
// and it will break on client updates, so as little as possible lives behind it.
//
// State arrives as JSON over a named pipe from the .NET host. This header is the shape
// that JSON parses into, and the only vocabulary the UI code may use.
//
// Integers are all int64_t on purpose. JSON has no integer types, so a mixture of signed
// and unsigned would need two readers on this side and two writers on the other, for no
// benefit: none of these quantities is large enough to care about the lost bit.

#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace overlay {

// How a row should read against a dark, moving background.
//
// An enum rather than a bare int because the drawing code indexes a palette with it, and
// a host bug or a corrupt message should not be able to produce an out-of-range index
// inside a game process. Anything unrecognised on the wire becomes Normal - see
// ToneFromWire, which is the single place that clamping happens.
enum class Tone : int {
    Normal = 0,
    Good = 1,
    Bad = 2,
    Muted = 3,
};

inline Tone ToneFromWire(int64_t value) {
    switch (value) {
        case 1:
            return Tone::Good;
        case 2:
            return Tone::Bad;
        case 3:
            return Tone::Muted;
        default:
            return Tone::Normal;
    }
}

// A row in one of the panels. Deliberately generic: the host decides what the columns
// mean, so a new panel needs no change on this side of the pipe.
struct Row {
    std::vector<std::string> cells;

    Tone tone = Tone::Normal;

    // Opaque, and the host's to interpret. Empty means the row cannot be acted on.
    //
    // Present so that panels are not permanently read-only: without an identity there is
    // no way to say "appraise this one" or to keep a selection across a publish, and
    // adding it later would mean revisiting every panel already written.
    std::string id;
};

struct Panel {
    // Which plugin produced this panel.
    //
    // Panels are grouped into one window per plugin, and the bar lists those windows -
    // which is the shape Decal had, and the reason a plugin framework looks like a
    // framework rather than like one application with tabs. Empty means the host itself
    // rather than any plugin.
    std::string owner;

    std::string title;

    // Stable across publishes, unlike the title, which may be empty, duplicated, or
    // reordered. ImGui keys remembered column widths by table identity, so without this a
    // width remembered for one panel lands on whichever panel later inherits its slot.
    std::string key;

    std::vector<std::string> columns;
    std::vector<Row> rows;
};

// What the host says about itself, for the status line.
struct Status {
    // Connected to the game server - not to this pipe. The two are different questions
    // with opposite consequences, and one field answering both was the first thing two
    // separate readers of this header got wrong.
    bool server_connected = false;

    bool acting = false;
    bool looting = false;

    std::string server;
    std::string character;
    std::string position;

    // Anything the host wants to say in words: why it stopped, what it is waiting for.
    // Without it the overlay can observe that a host went quiet but never say why.
    std::string notice;

    int64_t messages_in = 0;
    int64_t messages_out = 0;
    int64_t malformed = 0;
    int64_t objects = 0;
};

// What kind of control a plugin wants drawn.
//
// This is the vocabulary the real uTank2 pages are made of - checkboxes, sliders,
// dropdowns, edit boxes, buttons - and none of it can be expressed as rows of strings.
// Anything unrecognised on the wire becomes a Toggle; see ControlKindFromWire.
enum class ControlKind : int {
    Toggle = 0,
    Button = 1,
    Slider = 2,
    Choice = 3,
    Text = 4,
};

inline ControlKind ControlKindFromWire(int64_t value) {
    switch (value) {
        case 1:
            return ControlKind::Button;
        case 2:
            return ControlKind::Slider;
        case 3:
            return ControlKind::Choice;
        case 4:
            return ControlKind::Text;
        default:
            return ControlKind::Toggle;
    }
}

// A control a plugin owns, drawn in that plugin's window.
//
// The value is always a string so that one field serves every kind: "true" or "false"
// for a toggle, the number for a slider, the chosen option for a choice, the text for a
// text box, nothing for a button. The host owns the truth of it - the UI draws what it
// is given and sends back what the player did, and a refused change shows as the
// control springing back rather than the overlay disagreeing with the host.
struct Control {
    // What comes back in Command::control_id. Unique within the window.
    std::string id;

    std::string label;

    ControlKind kind = ControlKind::Toggle;

    std::string value;

    // Choice only: what may be chosen.
    std::vector<std::string> options;

    // Slider only. A step of zero means continuous.
    double min = 0.0;
    double max = 1.0;
    double step = 0.0;

    // Shown on hover when non-empty.
    std::string tooltip;
};

// A plugin's window, declared by the host rather than inferred from whichever panels
// happened to arrive this frame.
//
// This is the explicit plugin list Decal had. Without it a plugin with nothing to show
// this frame had no window this frame, and the bar listed only the talkative plugins.
// It is also where a plugin's controls live, which removes the one place the drawing
// code had to name a plugin: the loot switch arrives already owned.
// ---------------------------------------------------------------------------------------
// Decal views.
//
// A plugin that ships a Decal view XML - Virindi Tank's main window, say - has its window
// drawn as that view: Decal's controls at Decal's positions, in the Decal theme. The host
// parses the XML; what arrives here is the parsed tree with every control's current state.
// Positions and sizes are Decal pixels, scaled by the drawing code.

enum class ViewControlType : int {
    Unknown = 0,  // drawn as nothing - a kind the host knows and this DLL does not yet
    Fixed,
    Notebook,
    Static,
    Checkbox,
    PushButton,
    Button,
    Edit,
    Choice,
    Slider,
    List,
    Progress,
    Picture,  // VVS's HudPictureBox: an image, or the part of one in `uv`, stretched over it
    Console,  // VVS's HudConsole: coloured lines, the newest at the bottom, wrapped and scrolled
};

inline ViewControlType ViewControlTypeFromWire(const std::string& type) {
    if (type == "picture") return ViewControlType::Picture;
    if (type == "console") return ViewControlType::Console;
    if (type == "fixed") return ViewControlType::Fixed;
    if (type == "notebook") return ViewControlType::Notebook;
    if (type == "static") return ViewControlType::Static;
    if (type == "checkbox") return ViewControlType::Checkbox;
    if (type == "pushbutton") return ViewControlType::PushButton;
    if (type == "button") return ViewControlType::Button;
    if (type == "edit") return ViewControlType::Edit;
    if (type == "choice") return ViewControlType::Choice;
    if (type == "slider") return ViewControlType::Slider;
    if (type == "list") return ViewControlType::List;
    if (type == "progress") return ViewControlType::Progress;
    return ViewControlType::Unknown;
}

enum class ViewColumnType : int { Text = 0, Check, Icon };

inline ViewColumnType ViewColumnTypeFromWire(const std::string& type) {
    if (type == "check") return ViewColumnType::Check;
    if (type == "icon") return ViewColumnType::Icon;
    return ViewColumnType::Text;
}

enum class Justify : int { Default = 0, Left, Center, Right };

inline Justify JustifyFromWire(const std::string& justify) {
    if (justify == "left") return Justify::Left;
    if (justify == "center") return Justify::Center;
    if (justify == "right") return Justify::Right;
    return Justify::Default;
}

struct ViewCell {
    std::string text;
    bool checked = false;
    std::string image;
    int64_t color = -1;  // 0xAARRGGBB, or -1 for the theme's list text colour
};

struct ViewRow {
    std::vector<ViewCell> cells;
};

// A run of a console line in one colour: VVS's eConsoleColorClass, which the theme's console
// colour scheme turns into a colour. A link sends "click" when clicked.
struct ConsoleSegment {
    std::string text;
    int cls = 99;  // Unknown
    bool link = false;
};

struct ConsoleLine {
    std::vector<ConsoleSegment> segments;
};

struct ViewColumn {
    ViewColumnType type = ViewColumnType::Text;
    int width = 0;  // Decal pixels; 0 shares what the fixed columns leave
};

struct ViewControl;

struct ViewPage {
    std::string label;
    // Zero or one control. A vector rather than a pointer so the tree copies and frees
    // itself like everything else here.
    std::vector<ViewControl> content;
};

struct ViewControl {
    ViewControlType type = ViewControlType::Unknown;
    std::string name;  // what comes back as a command's control_id
    int x = 0;
    int y = 0;
    int w = 0;
    int h = 0;

    std::string text;
    int64_t text_color = -1;  // 0xAARRGGBB, or -1 for the theme's
    int font_size = 0;        // Decal pixels, or 0 for the theme's
    bool bold = false;
    bool shadow = false;      // a one-pixel black shadow under a label's text
    Justify justify = Justify::Default;
    std::string image;  // an image key, or empty for the theme's

    bool checked = false;
    std::string value;
    std::vector<std::string> options;
    int selected = -1;
    double min = 0.0;
    double max = 100.0;
    bool vertical = false;
    bool enabled = true;
    bool visible = true;

    // Shown in the theme's tooltip while the pointer rests on the control; empty for none.
    std::string tooltip;

    // A label or picture that takes a click, sending "press": VVS's HudStaticText and
    // HudPictureBox let a plugin hear one.
    bool clickable = false;

    // A label's own face by name ("Verdana") and its size in points; empty and 0 for the theme's.
    std::string font;
    float font_points = 0.0f;

    // A label centred from top to bottom, VVS's VerticalCenter; otherwise at the top.
    bool middle = false;

    // A picture's part of its image, as fractions: left, top, right, bottom.
    float uv[4] = {0.0f, 0.0f, 1.0f, 1.0f};

    // An edit box's count of the plugin's requests for the keyboard.
    int focus_request = 0;

    // A console's lines, oldest first.
    std::vector<ConsoleLine> lines;

    std::vector<ViewPage> pages;
    std::vector<ViewControl> children;
    std::vector<ViewColumn> columns;
    std::vector<ViewRow> rows;
};

// A plugin's own button on a view's title bar. A press sends "press" with its name as the
// control, like a push button in the view.
struct TitleButton {
    std::string name;
    std::string image;
    std::string image_down;
    std::string tooltip;
};

struct View {
    std::string title;
    std::string icon;  // an image key, or empty
    std::string bar;   // "vvs" for Virindi View Service's bar down the side; else Decal's across the top
    int width = 0;
    int height = 0;
    ViewControl root;

    // How the window starts, until the player picks otherwise from its title bar - which the
    // overlay then remembers. The theme by name ("Decal", "Float"; empty for the default);
    // hudified, VVS's "ghosted": only the body shows until left Ctrl is held; click-through,
    // which a hudified window can also be: the game gets its clicks.
    std::string theme;
    bool ghosted = false;
    bool click_through = false;

    // What the player may do with it, as VVS's view properties had it. A view the player can
    // resize has a frame in the Decal theme; one they cannot has none. Virindi Tank's main
    // window can be, so that is the default until the host says.
    bool resizeable = true;
    bool ghostable = true;
    bool click_throughable = true;

    // Where on screen it first opens, when the host says: VVS put a hudified window back
    // where the player left it. After that the overlay remembers where it is.
    bool has_position = false;
    int x = 0;
    int y = 0;

    // The screen edges a hudified window was left stuck to, "L"/"R" then "T"/"B", from vvs.s3db;
    // the overlay's own memory of them, once there is one, outranks it.
    std::string stuck;

    // The size the player left a window they could resize at, from vvs.s3db: the window opens at
    // it and the plugin is told, with "resize". And how small and large the player may make it.
    bool has_stored_size = false;
    int stored_width = 0;
    int stored_height = 0;
    int min_width = 0;
    int min_height = 0;
    int max_width = 0;
    int max_height = 0;

    // A HUD: no switch on any bar, no close button, no alpha buttons - Virindi HUDs turned
    // all three off - and buttons of the plugin's own on its title bar.
    bool show_in_bar = true;
    bool minimizable = true;
    bool alpha_changeable = true;
    std::vector<TitleButton> title_buttons;

    // Goes up each time the plugin asks for the window to be shown: the overlay opens it
    // whenever this is higher than it last saw, even after the player closed it.
    int open_request = 0;

    // Goes up each time something asks for the window to be turned over - shown if hidden,
    // hidden if shown - as a key bound to it in the Virindi Hotkey System does.
    int toggle_request = 0;

    // Goes up each time the plugin asks for the window to be hidden, as a Close button of its
    // own does.
    int close_request = 0;

    // Which of VVS's bar groups the view is in - its plugin's assembly, in VVS - with a rule
    // between one group and the next. Empty means the owner's plugin.
    std::string bar_group;

    // Where the group goes on VVS's bar, lowest first - VVS's hash of the plugin assembly's
    // name - when the host knows it; groups without one follow, in the order they come.
    bool has_bar_order = false;
    int bar_order = 0;

    // A window with no switch that Ctrl and a click on the Decal bar's right grip opens, as the
    // host's own window opens from the left grip.
    bool opens_from_grip = false;
};

struct PluginWindow {
    // Empty means the host's own window.
    std::string owner;

    // What the title bar says. Empty means the owner's name.
    std::string title;

    // A disabled plugin is still listed on the bar, drawn greyed, so its absence is
    // visible rather than mysterious.
    bool enabled = true;

    // Closed until the player opens it from the bar, instead of open when first seen.
    bool starts_closed = false;

    std::vector<Control> controls;

    // A Decal view, when the plugin has one; the window is then drawn as that view.
    bool has_view = false;
    View view;
};

// An image from the host: pixels for a key a view or the theme names. Sent once per
// connection in a frame of its own, not in every snapshot.
struct ImagePixels {
    std::string key;
    int width = 0;
    int height = 0;
    std::vector<unsigned char> rgba;  // width * height * 4, R G B A
};

// A key a plugin wants to hear about: pressed with exactly these modifiers, it becomes a
// "hotkey" command to that plugin, and the game does not see it.
struct Hotkey {
    std::string owner;
    std::string id;
    int key = 0;  // virtual-key code
    bool ctrl = false;
    bool shift = false;
    bool alt = false;
};

// Decal's bar as the player left it in the standard client, from Decal's registry values:
// compact or expanded, which edge, where along it, and how long.
struct DecalBarSettings {
    bool known = false;
    int state = 1;     // BarState: 1 compact, 0 expanded
    int dock = 0;      // BarDock: 0 top, 1 left, 2 right
    int start = 0;     // BarStart: pixels along the edge
    int length = 250;  // BarLength: pixels, at least 112
    int alpha = 255;       // BarAlpha: how opaque the bar is
    int view_alpha = 255;  // ViewAlpha: how opaque Decal's own views are, until the player changes one
};

// VVS's bar as the player left it, from vvs.s3db: its own row - where, and against which edges -
// and ExtraInfo's VVSBarHorizontal. Each part only when the store has it.
struct VvsBarSettings {
    bool has_position = false;
    int x = 0;
    int y = 0;
    bool has_stuck = false;
    std::string stuck;
    bool has_horizontal = false;
    bool horizontal = false;
};

// AC:Unreal's own interface, as its settings say: since release 94 a bar of its own plugins'
// buttons, kept as "__bar" in its Saved\ClientPlugins\settings.json and starting 8 in from the
// left and 80 down, 62 wide and growing with its buttons - 8,80 to 70,250 live with four, so
// taken as 214 high, room for a fifth - in its interface units, which its Desktop UI Scale
// multiplies into pixels. The overlay starts its own bars clear of it.
struct ClientUiSettings {
    bool known = false;

    // The Desktop UI Scale the player chose, 1 to 3 in quarter steps; ClientUiScale says what the
    // client draws at in a window of a given size.
    double ui_scale = 1.0;

    // The plugin bar's left, top, width and height, in interface units.
    float plugin_bar[4] = {8.0f, 80.0f, 62.0f, 214.0f};
};

struct State {
    Status status;

    // VVS's primary theme on this machine, for a VVS window with no theme of its own. Empty
    // from a host that predates themes.
    std::string default_theme;

    DecalBarSettings decal_bar;

    VvsBarSettings vvs_bar;

    // The client's own plugin bar and UI scale; not known from a host that predates them, or one
    // that could not read the client's settings.
    ClientUiSettings client_ui;

    // Who wants the next key the player presses - a hotkey window's Set - or empty. The overlay
    // catches that key, keeps it from the game, and sends "key-captured" to this owner with
    // "vk,ctrl,shift,alt", or "cancel" for Escape.
    std::string key_capture;

    // The player asked to keep playing while the game is minimized: minimizing the window
    // sends it off-screen and slows its frames instead, so the game still takes the keys held
    // for plugins. False from a host that predates it, which leaves minimizing alone.
    bool keep_playing_minimized = false;

    // Every hotkey bound, whoever it belongs to.
    std::vector<Hotkey> hotkeys;

    // Every window there is, in bar order. Panels whose owner has no entry here still
    // get a window, so an older host that sends none behaves as before.
    std::vector<PluginWindow> windows;

    std::vector<Panel> panels;

    // Bumped by the host on every publish, so the UI can tell a stale snapshot from a
    // quiet session. Zero means nothing has ever been published.
    int64_t revision = 0;

    // Unix milliseconds at the moment of publishing.
    //
    // A revision that stops moving says a host went quiet; it cannot distinguish a host
    // that hung thirty seconds ago from one with nothing to report. The UI cannot keep
    // its own clock without holding local state, which this design deliberately forbids,
    // so the time comes with the snapshot.
    int64_t published_ms = 0;
};

// What the player did, sent back to the host. One per user action; the host decides
// what any of them mean.
struct Command {
    std::string name;   // "toggle-acting", "toggle-looting", "reload-profile", ...
    std::string value;  // optional argument

    // The row acted on, when the command came from one. Empty otherwise.
    std::string row_id;

    // The plugin whose window the command came from: the owner of the panels in that
    // window. Empty means the host's own window. This is what lets the host route a
    // click to the one plugin it belongs to and to no other; without it every plugin
    // would have to be told about every click.
    std::string owner;

    // The control acted on, when the command came from one: its Control::id. For a
    // toggle, slider, choice or text box the name is "set" and value is the new value;
    // for a button the name is "press" and value is empty. Empty when the command did
    // not come from a control.
    std::string control_id;
};

// A click the host asks to have made in the game's window, once: the points of a layout the
// game draws centred in its window - AC:Unreal's character select is the retail 800 by 600
// one - clicked in turn. It travels in the input frame:
// {"input":{"held":[],"sequence":4,"click":{"id":1759700000123,"layout_width":800,
// "layout_height":600,"points":[[122,220],[344,394]],"ui_scale":1.75}}}, repeated for a moment
// so a lost frame cannot lose it; the id says whether it has been made already.
struct Click {
    int64_t id = 0;
    int layout_width = 0;
    int layout_height = 0;

    // The client's Desktop UI Scale as the player chose it, 1 to 3: the layout is drawn that much
    // larger, as far as the window lets it (ClientUiScale). 1, its own size, when the host does
    // not say.
    double ui_scale = 1.0;

    struct Point {
        int x = 0;
        int y = 0;
    };

    std::vector<Point> points;
};

}  // namespace overlay
