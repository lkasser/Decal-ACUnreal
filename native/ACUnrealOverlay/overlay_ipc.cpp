#include "overlay_ipc.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
// windows.h defines min and max as macros, which collide with the standard library that
// the JSON header below leans on.
#define NOMINMAX
#endif

#include <windows.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <mutex>
#include <new>
#include <string>
#include <thread>
#include <utility>
#include <vector>

// Not ours to keep warning-clean, and /W4 has opinions about it.
#pragma warning(push, 0)
#include <nlohmann/json.hpp>
#pragma warning(pop)

namespace overlay {
namespace {

using nlohmann::json;

// Far more than the host will ever publish - the widest panel set is a few thousand short
// strings - and far less than a corrupt or hostile prefix could ask for. The prefix is
// four bytes of someone else's memory as far as we know, and turning it straight into an
// allocation would let a single flipped bit ask a game process for four gigabytes.
constexpr uint32_t kMaxMessageBytes = 1u << 20;

// The host may not be running yet, or ever. Connecting to a pipe that is not there costs
// one failed syscall, so a second between tries keeps that invisible.
constexpr DWORD kReconnectIntervalMs = 1000;

// Images received but not yet made into textures. A full set of theme art is well under a
// megabyte; this is what a render thread that has stopped taking them may be owed.
constexpr size_t kMaxPendingImageBytes = 64u << 20;

// One queued command is one thing the player clicked. If this many are waiting then the
// host is not reading them, and by the time it does they describe a screen that has since
// changed.
constexpr size_t kMaxQueuedCommands = 64;

// A pipe handle leaked here is leaked inside somebody else's game for as long as they play,
// so ownership is never left implicit.
class Handle {
 public:
    Handle() = default;

    explicit Handle(HANDLE raw) noexcept : raw_(Owned(raw)) {}

    ~Handle() { Close(); }

    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;

    Handle(Handle&& other) noexcept : raw_(other.raw_) { other.raw_ = nullptr; }

    Handle& operator=(Handle&& other) noexcept {
        if (this != &other) {
            Close();
            raw_ = other.raw_;
            other.raw_ = nullptr;
        }
        return *this;
    }

    void Close() noexcept {
        if (raw_ != nullptr) {
            CloseHandle(raw_);
            raw_ = nullptr;
        }
    }

    HANDLE get() const noexcept { return raw_; }

    explicit operator bool() const noexcept { return raw_ != nullptr; }

 private:
    // CreateFile reports failure as INVALID_HANDLE_VALUE and CreateEvent as null, and
    // neither is worth closing.
    static HANDLE Owned(HANDLE raw) noexcept {
        return raw == INVALID_HANDLE_VALUE ? nullptr : raw;
    }

    HANDLE raw_ = nullptr;
};

// Everything below reads JSON that arrived over a pipe, so it answers with a default
// rather than throwing or trusting a type. nlohmann's own value() accessors throw when the
// type on the wire is not the type asked for, which is exactly the case to expect from a
// host that is mid-rewrite.

const json* Field(const json& object, const char* name) {
    if (!object.is_object()) return nullptr;
    const auto found = object.find(name);
    return found == object.end() ? nullptr : &*found;
}

std::string ReadString(const json& object, const char* name) {
    const json* value = Field(object, name);
    if (value == nullptr || !value->is_string()) return std::string();
    return value->get<std::string>();
}

bool ReadBool(const json& object, const char* name) {
    const json* value = Field(object, name);
    return value != nullptr && value->is_boolean() && value->get<bool>();
}

// For the fields whose absence means true. PluginWindow::enabled is one: an older host
// that sends no such field must not have every plugin drawn greyed out.
bool ReadBool(const json& object, const char* name, bool fallback) {
    const json* value = Field(object, name);
    return value != nullptr && value->is_boolean() ? value->get<bool>() : fallback;
}

// The first non-integer numbers in the contract. Accepts an integer as readily as a
// float, because a host writing a slider's range will as likely send 0 as 0.0, and
// nlohmann's get<double> on an integer is fine but is_number_float() on one is false - a
// reader that checked only for floats would turn every whole-number bound into the
// fallback and every slider into the range 0 to 1.
double ReadDouble(const json& object, const char* name, double fallback) {
    const json* value = Field(object, name);
    if (value == nullptr || !value->is_number()) return fallback;
    return value->get<double>();
}

std::vector<std::string> ReadStrings(const json& object, const char* name) {
    std::vector<std::string> out;
    if (const json* items = Field(object, name); items != nullptr && items->is_array()) {
        out.reserve(items->size());
        for (const json& item : *items)
            out.push_back(item.is_string() ? item.get<std::string>() : std::string());
    }
    return out;
}

// Every quantity in the contract is int64_t, so this is the only integer reader. A host
// mid-rewrite sending a float where a count belongs is worth truncating rather than
// discarding.
int64_t ReadInt64(const json& object, const char* name) {
    const json* value = Field(object, name);
    if (value == nullptr || !value->is_number()) return 0;
    if (value->is_number_float()) return static_cast<int64_t>(value->get<double>());
    return value->get<int64_t>();
}

// For the fields whose absence means something other than zero. A view's text colour is
// one: absent is "the theme's colour", and zero would be black.
int64_t ReadInt64(const json& object, const char* name, int64_t fallback) {
    const json* value = Field(object, name);
    if (value == nullptr || !value->is_number()) return fallback;
    if (value->is_number_float()) return static_cast<int64_t>(value->get<double>());
    return value->get<int64_t>();
}

// Decal pixels are small numbers; anything outside this is a corrupt message, and a
// clamp keeps the arithmetic in the drawing code far from overflow.
int ReadCoordinate(const json& object, const char* name) {
    const int64_t value = ReadInt64(object, name);
    return static_cast<int>(value < -100000 ? -100000 : value > 100000 ? 100000 : value);
}

Row ReadRow(const json& source) {
    Row row;
    if (const json* cells = Field(source, "cells"); cells != nullptr && cells->is_array()) {
        row.cells.reserve(cells->size());
        for (const json& cell : *cells)
            row.cells.push_back(cell.is_string() ? cell.get<std::string>() : std::string());
    }

    row.tone = ToneFromWire(ReadInt64(source, "tone"));
    row.id = ReadString(source, "id");
    return row;
}

Panel ReadPanel(const json& source) {
    Panel panel;
    panel.owner = ReadString(source, "owner");
    panel.title = ReadString(source, "title");
    panel.key = ReadString(source, "key");

    if (const json* columns = Field(source, "columns"); columns != nullptr && columns->is_array()) {
        panel.columns.reserve(columns->size());
        for (const json& column : *columns)
            panel.columns.push_back(column.is_string() ? column.get<std::string>() : std::string());
    }

    if (const json* rows = Field(source, "rows"); rows != nullptr && rows->is_array()) {
        panel.rows.reserve(rows->size());
        for (const json& row : *rows)
            panel.rows.push_back(ReadRow(row));
    }

    return panel;
}

Control ReadControl(const json& source) {
    Control control;
    control.id = ReadString(source, "id");
    control.label = ReadString(source, "label");
    control.kind = ControlKindFromWire(ReadInt64(source, "kind"));
    control.value = ReadString(source, "value");
    control.options = ReadStrings(source, "options");
    control.min = ReadDouble(source, "min", 0.0);
    control.max = ReadDouble(source, "max", 1.0);
    control.step = ReadDouble(source, "step", 0.0);
    control.tooltip = ReadString(source, "tooltip");
    return control;
}

// A view nests - a notebook's pages hold layouts that hold controls - so reading one is
// recursive, and recursion over input from another process needs a floor and a ceiling.
// Real views are four deep and a few hundred controls; these limits are far past that and
// far short of what would exhaust the stack or the heap of the game.
constexpr int kMaxViewDepth = 24;
constexpr int kMaxViewControls = 8000;

ViewCell ReadViewCell(const json& source) {
    ViewCell cell;
    cell.text = ReadString(source, "text");
    cell.checked = ReadBool(source, "checked");
    cell.image = ReadString(source, "image");
    cell.color = ReadInt64(source, "color", -1);
    return cell;
}

ViewControl ReadViewControl(const json& source, int depth, int& budget) {
    ViewControl control;
    if (--budget < 0 || depth > kMaxViewDepth) return control;  // Unknown: drawn as nothing

    control.type = ViewControlTypeFromWire(ReadString(source, "type"));
    control.name = ReadString(source, "name");
    control.x = ReadCoordinate(source, "x");
    control.y = ReadCoordinate(source, "y");
    control.w = ReadCoordinate(source, "w");
    control.h = ReadCoordinate(source, "h");
    control.text = ReadString(source, "text");
    control.text_color = ReadInt64(source, "text_color", -1);
    control.font_size = ReadCoordinate(source, "font_size");
    control.bold = ReadBool(source, "bold");
    control.shadow = ReadBool(source, "shadow");
    control.justify = JustifyFromWire(ReadString(source, "justify"));
    control.image = ReadString(source, "image");
    control.checked = ReadBool(source, "checked");
    control.value = ReadString(source, "value");
    control.options = ReadStrings(source, "options");
    control.selected = static_cast<int>(ReadInt64(source, "selected", -1));
    control.min = ReadDouble(source, "min", 0.0);
    control.max = ReadDouble(source, "max", 100.0);
    control.vertical = ReadBool(source, "vertical");
    control.enabled = ReadBool(source, "enabled", true);
    control.visible = ReadBool(source, "visible", true);
    control.tooltip = ReadString(source, "tooltip");
    control.clickable = ReadBool(source, "clickable");
    control.font = ReadString(source, "font");
    control.font_points = static_cast<float>(std::clamp(ReadDouble(source, "font_points", 0.0), 0.0, 200.0));
    control.middle = ReadBool(source, "middle");
    control.focus_request = static_cast<int>(ReadInt64(source, "focus_request"));

    // A picture's part of its image: four fractions, kept within the image.
    if (const json* uv = Field(source, "uv"); uv != nullptr && uv->is_array() && uv->size() == 4) {
        for (size_t i = 0; i < 4; ++i) {
            const json& part = (*uv)[i];
            const double value = part.is_number() ? part.get<double>() : (i < 2 ? 0.0 : 1.0);
            control.uv[i] = static_cast<float>(std::isfinite(value) ? std::clamp(value, 0.0, 1.0) : (i < 2 ? 0.0 : 1.0));
        }
    }

    // A console's lines. A console keeps a hundred; this is far past any and far short of what
    // would weigh on the game.
    if (const json* lines = Field(source, "lines"); lines != nullptr && lines->is_array()) {
        for (const json& line : *lines) {
            if (control.lines.size() >= 2000) break;
            ConsoleLine read;
            if (const json* segments = Field(line, "segments"); segments != nullptr && segments->is_array()) {
                for (const json& segment : *segments) {
                    if (read.segments.size() >= 64) break;
                    ConsoleSegment run;
                    run.text = ReadString(segment, "text");
                    run.cls = static_cast<int>(ReadInt64(segment, "class", 99));
                    run.link = ReadBool(segment, "link");
                    read.segments.push_back(std::move(run));
                }
            }
            control.lines.push_back(std::move(read));
        }
    }

    if (const json* pages = Field(source, "pages"); pages != nullptr && pages->is_array()) {
        for (const json& page : *pages) {
            ViewPage read;
            read.label = ReadString(page, "label");
            if (const json* content = Field(page, "content"); content != nullptr && content->is_object())
                read.content.push_back(ReadViewControl(*content, depth + 1, budget));
            control.pages.push_back(std::move(read));
        }
    }

    if (const json* children = Field(source, "children"); children != nullptr && children->is_array()) {
        for (const json& child : *children) {
            if (budget <= 0) break;
            control.children.push_back(ReadViewControl(child, depth + 1, budget));
        }
    }

    if (const json* columns = Field(source, "columns"); columns != nullptr && columns->is_array()) {
        for (const json& column : *columns) {
            ViewColumn read;
            read.type = ViewColumnTypeFromWire(ReadString(column, "type"));
            read.width = ReadCoordinate(column, "width");
            control.columns.push_back(read);
        }
    }

    if (const json* rows = Field(source, "rows"); rows != nullptr && rows->is_array()) {
        control.rows.reserve(rows->size());
        for (const json& row : *rows) {
            ViewRow read;
            if (const json* cells = Field(row, "cells"); cells != nullptr && cells->is_array()) {
                read.cells.reserve(cells->size());
                for (const json& cell : *cells)
                    read.cells.push_back(ReadViewCell(cell));
            }
            control.rows.push_back(std::move(read));
        }
    }

    return control;
}

bool ReadView(const json& source, View& view) {
    if (!source.is_object()) return false;

    view.title = ReadString(source, "title");
    view.icon = ReadString(source, "icon");
    view.bar = ReadString(source, "bar");
    view.width = ReadCoordinate(source, "width");
    view.height = ReadCoordinate(source, "height");
    view.theme = ReadString(source, "theme");
    view.ghosted = ReadBool(source, "ghosted");
    view.click_through = ReadBool(source, "click_through");
    view.resizeable = ReadBool(source, "resizeable", true);
    view.ghostable = ReadBool(source, "ghostable", true);
    view.click_throughable = ReadBool(source, "click_throughable", true);
    view.show_in_bar = ReadBool(source, "show_in_bar", true);
    view.minimizable = ReadBool(source, "minimizable", true);
    view.alpha_changeable = ReadBool(source, "alpha_changeable", true);
    view.open_request = ReadCoordinate(source, "open_request");
    view.toggle_request = ReadCoordinate(source, "toggle_request");
    view.close_request = ReadCoordinate(source, "close_request");
    view.bar_group = ReadString(source, "bar_group");
    if (Field(source, "bar_order") != nullptr) {
        view.has_bar_order = true;
        view.bar_order = static_cast<int>(ReadInt64(source, "bar_order"));
    }
    view.opens_from_grip = ReadBool(source, "opens_from_grip");
    if (const json* buttons = Field(source, "title_buttons"); buttons != nullptr && buttons->is_array()) {
        for (const json& each : *buttons) {
            if (!each.is_object() || view.title_buttons.size() >= 8) continue;
            TitleButton button;
            button.name = ReadString(each, "name");
            button.image = ReadString(each, "image");
            button.image_down = ReadString(each, "image_down");
            button.tooltip = ReadString(each, "tooltip");
            if (!button.name.empty()) view.title_buttons.push_back(std::move(button));
        }
    }
    if (Field(source, "x") != nullptr && Field(source, "y") != nullptr) {
        view.has_position = true;
        view.x = ReadCoordinate(source, "x");
        view.y = ReadCoordinate(source, "y");
    }
    view.stuck = ReadString(source, "stuck");
    if (Field(source, "stored_width") != nullptr && Field(source, "stored_height") != nullptr) {
        view.has_stored_size = true;
        view.stored_width = std::max(1, ReadCoordinate(source, "stored_width"));
        view.stored_height = std::max(1, ReadCoordinate(source, "stored_height"));
    }
    view.min_width = std::max(0, ReadCoordinate(source, "min_width"));
    view.min_height = std::max(0, ReadCoordinate(source, "min_height"));
    view.max_width = std::max(0, ReadCoordinate(source, "max_width"));
    view.max_height = std::max(0, ReadCoordinate(source, "max_height"));

    int budget = kMaxViewControls;
    if (const json* root = Field(source, "root"); root != nullptr && root->is_object())
        view.root = ReadViewControl(*root, 0, budget);

    return true;
}

PluginWindow ReadWindow(const json& source) {
    PluginWindow window;
    window.owner = ReadString(source, "owner");
    window.title = ReadString(source, "title");
    window.enabled = ReadBool(source, "enabled", true);
    window.starts_closed = ReadBool(source, "starts_closed", false);

    if (const json* controls = Field(source, "controls"); controls != nullptr && controls->is_array()) {
        window.controls.reserve(controls->size());
        for (const json& control : *controls)
            window.controls.push_back(ReadControl(control));
    }

    // Null or absent is "no view", which is every window before views existed.
    if (const json* view = Field(source, "view"); view != nullptr && view->is_object())
        window.has_view = ReadView(*view, window.view);

    return window;
}

// Standard base64, as .NET's Convert.ToBase64String writes it. False on anything else,
// because a half-decoded image is worse than a missing one.
bool DecodeBase64(const std::string& text, std::vector<unsigned char>& out) {
    static const auto table = [] {
        std::array<int, 256> t{};
        t.fill(-1);
        const char* alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        for (int i = 0; i < 64; ++i)
            t[static_cast<unsigned char>(alphabet[i])] = i;
        return t;
    }();

    if (text.size() % 4 != 0) return false;

    out.clear();
    out.reserve(text.size() / 4 * 3);

    for (size_t i = 0; i < text.size(); i += 4) {
        int v[4];
        int pad = 0;
        for (int k = 0; k < 4; ++k) {
            const char c = text[i + k];
            if (c == '=') {
                // Padding only at the very end, and never in the first two places.
                if (i + 4 != text.size() || k < 2) return false;
                v[k] = 0;
                ++pad;
            } else {
                if (pad != 0) return false;
                v[k] = table[static_cast<unsigned char>(c)];
                if (v[k] < 0) return false;
            }
        }

        const uint32_t bits = (static_cast<uint32_t>(v[0]) << 18) | (static_cast<uint32_t>(v[1]) << 12) |
                              (static_cast<uint32_t>(v[2]) << 6) | static_cast<uint32_t>(v[3]);
        out.push_back(static_cast<unsigned char>(bits >> 16));
        if (pad < 2) out.push_back(static_cast<unsigned char>((bits >> 8) & 0xFF));
        if (pad < 1) out.push_back(static_cast<unsigned char>(bits & 0xFF));
    }

    return true;
}

// The largest side accepted. Interface art is at most a few hundred pixels; this bounds
// the allocation a corrupt size could ask for.
constexpr int kMaxImageSide = 2048;

bool ReadImage(const json& source, ImagePixels& image) {
    image.key = ReadString(source, "key");
    const int64_t width = ReadInt64(source, "width");
    const int64_t height = ReadInt64(source, "height");

    if (image.key.empty() || width <= 0 || height <= 0 || width > kMaxImageSide || height > kMaxImageSide)
        return false;

    image.width = static_cast<int>(width);
    image.height = static_cast<int>(height);

    if (!DecodeBase64(ReadString(source, "rgba"), image.rgba)) return false;
    return image.rgba.size() == static_cast<size_t>(width * height * 4);
}

void ParseStateDocument(const json& document, State& into) {
    if (const json* status = Field(document, "status"); status != nullptr) {
        into.status.server_connected = ReadBool(*status, "server_connected");
        into.status.acting = ReadBool(*status, "acting");
        into.status.looting = ReadBool(*status, "looting");
        into.status.server = ReadString(*status, "server");
        into.status.character = ReadString(*status, "character");
        into.status.position = ReadString(*status, "position");
        into.status.notice = ReadString(*status, "notice");
        into.status.messages_in = ReadInt64(*status, "messages_in");
        into.status.messages_out = ReadInt64(*status, "messages_out");
        into.status.malformed = ReadInt64(*status, "malformed");
        into.status.objects = ReadInt64(*status, "objects");
    }

    // Absent reads as no windows, which the contract says must behave as before: every
    // panel still gets a window from its owner. An older host keeps working unchanged.
    if (const json* windows = Field(document, "windows"); windows != nullptr && windows->is_array()) {
        into.windows.reserve(windows->size());
        for (const json& window : *windows)
            into.windows.push_back(ReadWindow(window));
    }

    if (const json* panels = Field(document, "panels"); panels != nullptr && panels->is_array()) {
        into.panels.reserve(panels->size());
        for (const json& panel : *panels)
            into.panels.push_back(ReadPanel(panel));
    }

    if (const json* hotkeys = Field(document, "hotkeys"); hotkeys != nullptr && hotkeys->is_array()) {
        for (const json& source : *hotkeys) {
            if (!source.is_object() || into.hotkeys.size() >= 256) continue;
            Hotkey hotkey;
            hotkey.owner = ReadString(source, "owner");
            hotkey.id = ReadString(source, "id");
            hotkey.key = static_cast<int>(ReadInt64(source, "key"));
            hotkey.ctrl = ReadBool(source, "ctrl");
            hotkey.shift = ReadBool(source, "shift");
            hotkey.alt = ReadBool(source, "alt");
            if (hotkey.key >= 1 && hotkey.key <= 254 && !hotkey.id.empty()) into.hotkeys.push_back(std::move(hotkey));
        }
    }

    // Both pass through as sent, negatives included. A negative revision or a timestamp
    // before 1970 is a host bug either way, and clamping the first to zero would claim
    // nothing had ever been published when something plainly had, while a nonsensical time
    // reads as a very stale snapshot - which is the direction that errs safely.
    into.revision = ReadInt64(document, "revision");
    into.published_ms = ReadInt64(document, "published_ms");
    into.default_theme = ReadString(document, "default_theme");
    into.key_capture = ReadString(document, "key_capture");
    into.keep_playing_minimized = ReadBool(document, "keep_playing_minimized");
    if (const json* bar = Field(document, "decal_bar"); bar != nullptr && bar->is_object()) {
        into.decal_bar.known = true;
        into.decal_bar.state = ReadCoordinate(*bar, "state");
        into.decal_bar.dock = std::clamp(ReadCoordinate(*bar, "dock"), 0, 2);
        into.decal_bar.start = std::max(0, ReadCoordinate(*bar, "start"));
        into.decal_bar.length = std::max(112, ReadCoordinate(*bar, "length"));
        into.decal_bar.alpha = std::clamp(static_cast<int>(ReadInt64(*bar, "alpha", 255)), 0, 255);
        into.decal_bar.view_alpha = std::clamp(static_cast<int>(ReadInt64(*bar, "view_alpha", 255)), 0, 255);
    }
    if (const json* bar = Field(document, "vvs_bar"); bar != nullptr && bar->is_object()) {
        if (Field(*bar, "x") != nullptr && Field(*bar, "y") != nullptr) {
            into.vvs_bar.has_position = true;
            into.vvs_bar.x = ReadCoordinate(*bar, "x");
            into.vvs_bar.y = ReadCoordinate(*bar, "y");
        }
        if (Field(*bar, "stuck") != nullptr) {
            into.vvs_bar.has_stuck = true;
            into.vvs_bar.stuck = ReadString(*bar, "stuck");
        }
        if (Field(*bar, "horizontal") != nullptr) {
            into.vvs_bar.has_horizontal = true;
            into.vvs_bar.horizontal = ReadBool(*bar, "horizontal");
        }
    }
    // {"ui_scale":1.5,"plugin_bar":[8,80,62,214]}: the client's own plugin bar, in its interface
    // units, and the UI scale the player chose. A bar of any other shape is the client's default.
    if (const json* client = Field(document, "client_ui"); client != nullptr && client->is_object()) {
        into.client_ui.known = true;
        const double scale = ReadDouble(*client, "ui_scale", 1.0);
        into.client_ui.ui_scale = std::isfinite(scale) ? std::clamp(scale, 1.0, 3.0) : 1.0;
        const json* bar = Field(*client, "plugin_bar");
        if (bar != nullptr && bar->is_array() && bar->size() == 4 &&
            std::all_of(bar->begin(), bar->end(), [](const json& v) { return v.is_number() && std::isfinite(v.get<double>()); })) {
            for (size_t i = 0; i < 4; ++i)
                into.client_ui.plugin_bar[i] = static_cast<float>(std::clamp((*bar)[i].get<double>(), -100000.0, 100000.0));
        }
    }
}

// What one message from the host turned out to be.
enum class FrameKind { Rubbish, State, Image, Input };

// A click an input frame carries, if it carries a sound one: a positive id, a layout of a
// sensible size, and up to eight points inside it. Anything else is no click at all - a
// click in the wrong place is worse than none.
void ReadClick(const json& source, Click& click) {
    click = Click{};
    const json* found = Field(source, "click");
    if (found == nullptr || !found->is_object()) return;

    Click read;
    read.id = ReadInt64(*found, "id", 0);
    const int64_t width = ReadInt64(*found, "layout_width", 0);
    const int64_t height = ReadInt64(*found, "layout_height", 0);
    if (read.id <= 0 || width < 1 || width > 10000 || height < 1 || height > 10000) return;
    read.layout_width = static_cast<int>(width);
    read.layout_height = static_cast<int>(height);

    // The client's Desktop UI Scale, which an older host does not send: its own size then.
    const double scale = ReadDouble(*found, "ui_scale", 1.0);
    read.ui_scale = std::isfinite(scale) ? std::clamp(scale, 1.0, 3.0) : 1.0;

    const json* points = Field(*found, "points");
    if (points == nullptr || !points->is_array()) return;
    for (const json& point : *points) {
        if (!point.is_array() || point.size() != 2 || !point[0].is_number_integer() || !point[1].is_number_integer()) return;
        const int64_t x = point[0].get<int64_t>();
        const int64_t y = point[1].get<int64_t>();
        if (x < 0 || y < 0 || x >= read.layout_width || y >= read.layout_height || read.points.size() >= 8) return;
        read.points.push_back({static_cast<int>(x), static_cast<int>(y)});
    }

    if (!read.points.empty()) click = std::move(read);
}

// The keys an input frame asks to have held: {"input":{"held":[87,65],"sequence":3}}, and the
// click it may carry beside them.
bool ReadInput(const json& source, std::vector<uint16_t>& keys, Click& click) {
    const json* held = Field(source, "held");
    if (held == nullptr || !held->is_array()) return false;

    keys.clear();
    for (const json& key : *held) {
        if (!key.is_number_integer()) continue;
        const int64_t code = key.get<int64_t>();
        if (code >= 1 && code <= 254 && keys.size() < 16) keys.push_back(static_cast<uint16_t>(code));
    }

    ReadClick(source, click);
    return true;
}

// Rubbish is one lost message, never a reason to drop the host. An image frame is an
// object whose only member is "image"; anything else is taken as a snapshot, which is
// what every message was before images existed.
FrameKind ParseFrame(const std::vector<char>& utf8, State& state, ImagePixels& image, std::vector<uint16_t>& keys, Click& click) {
    const json document = json::parse(utf8.begin(), utf8.end(), nullptr, /*allow_exceptions=*/false);
    if (document.is_discarded() || !document.is_object()) return FrameKind::Rubbish;

    if (const json* frame = Field(document, "image"); frame != nullptr)
        return frame->is_object() && ReadImage(*frame, image) ? FrameKind::Image : FrameKind::Rubbish;

    if (const json* frame = Field(document, "input"); frame != nullptr)
        return frame->is_object() && ReadInput(*frame, keys, click) ? FrameKind::Input : FrameKind::Rubbish;

    ParseStateDocument(document, state);
    return FrameKind::State;
}

std::vector<char> Frame(const Command& command) {
    json document;
    document["name"] = command.name;
    document["value"] = command.value;
    document["row_id"] = command.row_id;
    document["owner"] = command.owner;
    document["control_id"] = command.control_id;

    // Command values carry item and spell names that came off the wire as the game's own
    // bytes, and those are not certainly valid UTF-8. Replacing the offending bytes keeps
    // dump() from throwing over a character nobody will read anyway.
    const std::string payload =
        document.dump(-1, ' ', false, json::error_handler_t::replace);

    const uint32_t length = static_cast<uint32_t>(payload.size());
    std::vector<char> framed;
    framed.reserve(sizeof(uint32_t) + payload.size());
    framed.push_back(static_cast<char>(length & 0xFF));
    framed.push_back(static_cast<char>((length >> 8) & 0xFF));
    framed.push_back(static_cast<char>((length >> 16) & 0xFF));
    framed.push_back(static_cast<char>((length >> 24) & 0xFF));
    framed.insert(framed.end(), payload.begin(), payload.end());
    return framed;
}

}  // namespace

struct Ipc::Impl {
    explicit Impl(std::wstring pipe_name) noexcept
        : name(std::move(pipe_name)),
          // Manual-reset because an overlapped read is only ever waited on by the thread
          // that issued it, and ReadFile clears the event itself when it issues the next.
          read_done(CreateEventW(nullptr, TRUE, FALSE, nullptr)),
          write_done(CreateEventW(nullptr, TRUE, FALSE, nullptr)),
          // Auto-reset: one wake, one waiter, and the reasons to wake (stopping, or a
          // command to send) are flags the worker re-reads every time round.
          wake(CreateEventW(nullptr, FALSE, FALSE, nullptr)) {}

    std::wstring name;

    Handle read_done;
    Handle write_done;
    Handle wake;

    // The lock over the published pointer is only ever held for a pointer copy - never
    // across a read, a parse or an allocation - which is what keeps a caller in a Present
    // hook from ever waiting on the pipe.
    mutable std::mutex state_gate;
    std::shared_ptr<const State> latest;

    std::mutex outbound_gate;
    std::vector<Command> outbound;

    std::mutex images_gate;
    std::vector<ImagePixels> pending_images;
    size_t pending_image_bytes = 0;

    mutable std::mutex input_gate;
    std::vector<uint16_t> wanted_keys;
    ULONGLONG wanted_at = 0;

    // The last click the host asked for, and the id of the last one taken to be made.
    Click wanted_click;
    int64_t taken_click = 0;

    // Set before the worker starts and only read by it afterwards, so it needs no lock.
    std::function<void()> keys_listener;

    void KeysChanged() noexcept {
        if (!keys_listener) return;
        try {
            keys_listener();
        } catch (...) {
            // A listener that throws costs a key press its promptness, not the connection.
        }
    }

    std::mutex life_gate;
    std::thread worker;
    std::atomic<bool> stopping{false};
    std::atomic<bool> connected{false};

    // Touched only by the worker thread.
    Handle pipe;
    std::vector<char> buffer;

    std::wstring FullPath() const {
        // A caller passing \\.\pipe\something, or \\machine\pipe\something, means it.
        if (!name.empty() && name.front() == L'\\') return name;
        return L"\\\\.\\pipe\\" + name;
    }

    void Run() noexcept {
        // An exception leaving a thread function calls std::terminate, which ends the game
        // rather than the overlay. Nothing inside is worth that, so everything stops here.
        try {
            Loop();
        } catch (...) {
        }

        connected.store(false, std::memory_order_relaxed);
        pipe.Close();
    }

    void Loop() {
        const std::wstring path = FullPath();

        while (!stopping.load(std::memory_order_relaxed)) {
            if (!Connect(path)) {
                WaitForSingleObject(wake.get(), kReconnectIntervalMs);
                continue;
            }

            connected.store(true, std::memory_order_relaxed);
            try {
                Serve();
            } catch (...) {
                // A failure while serving (an allocation for an oversized message, say) is
                // worth losing the connection over, but not the thread: the host is still
                // there and the next attempt may well succeed.
            }
            connected.store(false, std::memory_order_relaxed);
            pipe.Close();

            // A host that has gone holds no keys, and wants no click it has not had made.
            {
                std::lock_guard<std::mutex> hold(input_gate);
                wanted_keys.clear();
                wanted_click = Click{};
            }
            KeysChanged();

            // Commands queued for a host that has gone describe a panel the next host has
            // not drawn yet, and sending them at it would act on a stale click.
            DropQueued();
        }
    }

    bool Connect(const std::wstring& path) {
        // Overlapped, because a read waiting on a host that has stopped speaking has to be
        // cancellable; a synchronous read would hold the thread until the host said
        // something, and Stop() would have to wait just as long.
        Handle opened(CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                                  OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr));
        if (!opened) return false;

        // Byte mode both ends: the length prefix is our framing, and a second framing
        // underneath it is one more thing that has to agree.
        DWORD mode = PIPE_READMODE_BYTE;
        SetNamedPipeHandleState(opened.get(), &mode, nullptr, nullptr);

        pipe = std::move(opened);
        return true;
    }

    // Reads framed messages until the host goes away or we are asked to stop.
    void Serve() {
        for (;;) {
            if (!FlushOutbound()) return;

            unsigned char prefix[sizeof(uint32_t)] = {};
            if (!ReadExactly(prefix, sizeof(prefix))) return;

            const uint32_t length = static_cast<uint32_t>(prefix[0]) |
                                    (static_cast<uint32_t>(prefix[1]) << 8) |
                                    (static_cast<uint32_t>(prefix[2]) << 16) |
                                    (static_cast<uint32_t>(prefix[3]) << 24);

            if (length == 0) continue;

            // Past the limit there is no way to find where the next message starts, so the
            // stream is finished with either way; dropping it resynchronises us.
            if (length > kMaxMessageBytes) return;

            buffer.resize(length);
            if (!ReadExactly(buffer.data(), length)) return;

            State next;
            ImagePixels image;
            std::vector<uint16_t> keys;
            Click click;
            switch (ParseFrame(buffer, next, image, keys, click)) {
                case FrameKind::State:
                    Publish(std::move(next));
                    break;
                case FrameKind::Image:
                    Receive(std::move(image));
                    break;
                case FrameKind::Input: {
                    {
                        std::lock_guard<std::mutex> hold(input_gate);
                        wanted_keys = std::move(keys);
                        wanted_at = GetTickCount64();
                        if (click.id != 0) wanted_click = std::move(click);
                    }
                    KeysChanged();
                    break;
                }
                case FrameKind::Rubbish:
                    break;
            }
        }
    }

    // Images wait here until the render thread takes them, since only it may create
    // textures. The render thread takes them every frame, so this only grows if it has
    // stopped drawing - and then the cap keeps a host that sends images in a loop from
    // filling the game's memory.
    void Receive(ImagePixels image) {
        std::lock_guard<std::mutex> hold(images_gate);
        if (pending_image_bytes + image.rgba.size() > kMaxPendingImageBytes) return;
        pending_image_bytes += image.rgba.size();
        pending_images.push_back(std::move(image));
    }

    void Publish(State next) {
        // Built outside the lock on purpose - see state_gate.
        auto published = std::make_shared<const State>(std::move(next));

        std::lock_guard<std::mutex> hold(state_gate);
        latest = std::move(published);
    }

    bool FlushOutbound() {
        for (;;) {
            Command command;
            {
                std::lock_guard<std::mutex> hold(outbound_gate);
                if (outbound.empty()) return true;
                command = std::move(outbound.front());
                outbound.erase(outbound.begin());
            }

            // Framed outside the queue lock, so Send() never waits behind a write.
            const std::vector<char> framed = Frame(command);
            if (!WriteAll(framed.data(), framed.size())) return false;
        }
    }

    void DropQueued() {
        std::lock_guard<std::mutex> hold(outbound_gate);
        outbound.clear();
    }

    // False when the connection is finished or we are stopping. Waits only on the pipe and
    // on the wake event, and services outbound commands while it waits, so a chatty player
    // is not held up by a quiet host.
    bool ReadExactly(void* into, size_t count) {
        auto* cursor = static_cast<unsigned char*>(into);
        size_t done = 0;

        while (done < count) {
            if (stopping.load(std::memory_order_relaxed)) return false;

            OVERLAPPED overlapped = {};
            overlapped.hEvent = read_done.get();

            DWORD got = 0;
            if (!ReadFile(pipe.get(), cursor + done, static_cast<DWORD>(count - done), &got,
                          &overlapped)) {
                if (GetLastError() != ERROR_IO_PENDING) return false;
                if (!AwaitRead(overlapped, got)) return false;
            }

            // Zero bytes on a blocking pipe read means the far end closed.
            if (got == 0) return false;
            done += got;
        }

        return true;
    }

    bool AwaitRead(OVERLAPPED& overlapped, DWORD& got) {
        for (;;) {
            const HANDLE waits[] = {read_done.get(), wake.get()};
            const DWORD woke = WaitForMultipleObjects(2, waits, FALSE, INFINITE);

            if (woke == WAIT_OBJECT_0) break;

            if (woke == WAIT_OBJECT_0 + 1) {
                if (stopping.load(std::memory_order_relaxed)) {
                    Abandon(overlapped);
                    return false;
                }
                if (!FlushOutbound()) {
                    Abandon(overlapped);
                    return false;
                }
                continue;
            }

            // A failed wait means one of our own handles is gone, which the retry loop
            // cannot mend, but dropping the connection at least leaves the game alone.
            Abandon(overlapped);
            return false;
        }

        return GetOverlappedResult(pipe.get(), &overlapped, &got, FALSE) != FALSE;
    }

    bool WriteAll(const char* data, size_t count) {
        size_t done = 0;

        while (done < count) {
            if (stopping.load(std::memory_order_relaxed)) return false;

            OVERLAPPED overlapped = {};
            overlapped.hEvent = write_done.get();

            DWORD put = 0;
            if (!WriteFile(pipe.get(), data + done, static_cast<DWORD>(count - done), &put,
                           &overlapped)) {
                if (GetLastError() != ERROR_IO_PENDING) return false;

                for (;;) {
                    const HANDLE waits[] = {write_done.get(), wake.get()};
                    const DWORD woke = WaitForMultipleObjects(2, waits, FALSE, INFINITE);

                    if (woke == WAIT_OBJECT_0) break;

                    // A host that has stopped reading can leave a write pending for as long
                    // as it likes, so Stop() has to be able to walk away from one.
                    if (woke != WAIT_OBJECT_0 + 1 || stopping.load(std::memory_order_relaxed)) {
                        Abandon(overlapped);
                        return false;
                    }
                }

                if (!GetOverlappedResult(pipe.get(), &overlapped, &put, FALSE)) return false;
            }

            if (put == 0) return false;
            done += put;
        }

        return true;
    }

    // Cancels a pending I/O and waits for it to settle. The wait is not optional: the
    // OVERLAPPED lives on our stack and the buffer belongs to this object, and the kernel
    // may still write to both until it acknowledges the cancellation.
    void Abandon(OVERLAPPED& overlapped) {
        CancelIoEx(pipe.get(), &overlapped);

        DWORD ignored = 0;
        GetOverlappedResult(pipe.get(), &overlapped, &ignored, TRUE);
    }
};

Ipc::Ipc(std::wstring pipe_name) noexcept
    // Nothing here may throw, so an overlay that quietly does nothing is the failure mode
    // for an allocation this small going wrong. Every member below copes with a null impl_.
    : impl_(new (std::nothrow) Impl(std::move(pipe_name))) {}

Ipc::~Ipc() { Stop(); }

void Ipc::Start() noexcept {
    if (impl_ == nullptr) return;

    try {
        std::lock_guard<std::mutex> hold(impl_->life_gate);
        if (impl_->worker.joinable()) return;
        if (!impl_->wake || !impl_->read_done || !impl_->write_done) return;

        impl_->stopping.store(false, std::memory_order_relaxed);
        ResetEvent(impl_->wake.get());

        Impl* impl = impl_.get();
        impl_->worker = std::thread([impl] { impl->Run(); });
    } catch (...) {
        // No thread means no snapshots, which every caller already has to cope with because
        // the host may not be running either.
    }
}

void Ipc::Stop() noexcept {
    if (impl_ == nullptr) return;

    try {
        std::lock_guard<std::mutex> hold(impl_->life_gate);
        impl_->stopping.store(true, std::memory_order_relaxed);
        if (impl_->wake) SetEvent(impl_->wake.get());
        if (impl_->worker.joinable()) impl_->worker.join();
        return;
    } catch (...) {
    }

    // join() fails only on a thread that cannot be joined from here, which takes a caller
    // bug to arrange. If it ever happens the thread is still running and still reading
    // these members, so the members have to outlive it: abandoning them costs three handles
    // and a thread, where freeing them would hand a live thread freed memory.
    static_cast<void>(impl_.release());
}

bool Ipc::Connected() const noexcept {
    return impl_ != nullptr && impl_->connected.load(std::memory_order_relaxed);
}

std::shared_ptr<const State> Ipc::Latest() const noexcept {
    if (impl_ == nullptr) return nullptr;

    try {
        std::lock_guard<std::mutex> hold(impl_->state_gate);
        return impl_->latest;
    } catch (...) {
        return nullptr;
    }
}

State Ipc::Snapshot() const noexcept {
    try {
        // Copied outside the lock: a deep copy of a few thousand rows is not something the
        // worker should ever be blocked behind.
        const std::shared_ptr<const State> published = Latest();
        if (published == nullptr) return State();
        return *published;
    } catch (...) {
        return State();
    }
}

void Ipc::Send(const Command& command) noexcept {
    if (impl_ == nullptr) return;

    try {
        {
            std::lock_guard<std::mutex> hold(impl_->outbound_gate);
            if (impl_->outbound.size() >= kMaxQueuedCommands) return;
            impl_->outbound.push_back(command);
        }

        if (impl_->wake) SetEvent(impl_->wake.get());
    } catch (...) {
        // A dropped command costs the player one click. An exception out of here, from the
        // UI code that called it, costs them the session.
    }
}

void Ipc::WantedKeys(std::vector<uint16_t>& keys, uint64_t& age_ms) const noexcept {
    keys.clear();
    age_ms = UINT64_MAX;
    if (impl_ == nullptr) return;

    try {
        std::lock_guard<std::mutex> hold(impl_->input_gate);
        keys = impl_->wanted_keys;
        if (impl_->wanted_at != 0) age_ms = GetTickCount64() - impl_->wanted_at;
    } catch (...) {
        keys.clear();
    }
}

bool Ipc::TakeClick(Click& click) noexcept {
    if (impl_ == nullptr) return false;

    try {
        std::lock_guard<std::mutex> hold(impl_->input_gate);
        if (impl_->wanted_click.id == 0 || impl_->wanted_click.id == impl_->taken_click) return false;
        impl_->taken_click = impl_->wanted_click.id;
        click = impl_->wanted_click;
        return true;
    } catch (...) {
        return false;
    }
}

void Ipc::SetKeysListener(std::function<void()> listener) noexcept {
    if (impl_ == nullptr) return;

    try {
        std::lock_guard<std::mutex> hold(impl_->life_gate);
        if (impl_->worker.joinable()) return;  // too late: the worker is already reading it
        impl_->keys_listener = std::move(listener);
    } catch (...) {
        // Without it the keys are still pressed, a pass of the pump later.
    }
}

std::vector<ImagePixels> Ipc::TakeImages() noexcept {
    std::vector<ImagePixels> taken;
    if (impl_ == nullptr) return taken;

    std::lock_guard<std::mutex> hold(impl_->images_gate);
    taken.swap(impl_->pending_images);
    impl_->pending_image_bytes = 0;
    return taken;
}

}  // namespace overlay
