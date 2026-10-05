// Headless check of overlay_ui.cpp: real ImGui, no device, asserts on.
//
// Built and run by native\test.ps1. The drawing code runs inside the game, where a failed
// ImGui assert - an unbalanced push, an id clash, a range ImGui refuses - takes the client
// down with it. Here the same code runs against real ImGui with IM_ASSERT live, so those
// mistakes abort a test instead of a play session.
//
// ImGui's test-engine hooks report every widget's id, label and rectangle, so the harness
// can click a control by its label and read back exactly which commands came out. No
// pixels are checked: what the player sees is judged in the game, and what this can say
// for certain is which command a click produces.

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <limits>
#include <map>
#include <string>
#include <vector>

#include "imgui.h"
#include "imgui_internal.h"
#include "decal_view.h"
#include "input.h"
#include "overlay_ui.h"
#include "textures.h"

using overlay::Command;
using overlay::Control;
using overlay::ControlKind;
using overlay::Panel;
using overlay::PluginWindow;
using overlay::Row;
using overlay::State;

static int g_failures = 0;
static int g_checks = 0;
#define CHECK(cond)                                                                   \
    do {                                                                              \
        ++g_checks;                                                                   \
        if (!(cond)) {                                                                \
            std::printf("FAIL line %d: %s\n", __LINE__, #cond);                       \
            ++g_failures;                                                             \
        }                                                                             \
    } while (0)

// --- Test-engine hooks --------------------------------------------------------------------

struct Item {
    ImGuiID id = 0;
    ImRect bb;
    std::string label;
    std::string window;
};

static std::map<ImGuiID, ImRect> g_rects;
static std::vector<Item> g_items;

void ImGuiTestEngineHook_ItemAdd(ImGuiContext*, ImGuiID id, const ImRect& bb, const ImGuiLastItemData*)
{
    g_rects[id] = bb;
}

void ImGuiTestEngineHook_ItemInfo(ImGuiContext* ctx, ImGuiID id, const char* label, ImGuiItemStatusFlags)
{
    Item item;
    item.id = id;
    item.bb = g_rects[id];
    item.label = label != nullptr ? label : "";
    item.window = ctx->CurrentWindow != nullptr ? ctx->CurrentWindow->Name : "";
    g_items.push_back(item);
}

void ImGuiTestEngineHook_Log(ImGuiContext*, const char*, ...) {}

const char* ImGuiTestEngine_FindItemDebugLabel(ImGuiContext*, ImGuiID) { return nullptr; }

static const Item* Find(const std::string& label, const std::string& window_part = std::string())
{
    const Item* found = nullptr;
    for (const Item& item : g_items)
    {
        if (item.label == label && (window_part.empty() || item.window.find(window_part) != std::string::npos))
            found = &item;
    }
    return found;
}

// --- Frames --------------------------------------------------------------------------------

// For widgets that report no label (BeginCombo, in this ImGui): the id the drawing code
// gives a control is its window's id, then PushID(control id), then "###control".
static const ImRect* ControlRect(const char* owner, const char* control_id)
{
    std::string identity = "###window:";
    identity += owner;
    ImGuiWindow* window = ImGui::FindWindowByName(identity.c_str());
    if (window == nullptr)
        return nullptr;

    const ImGuiID seed = ImHashStr(control_id, 0, window->ID);
    const ImGuiID id = ImHashStr("###control", 0, seed);
    const auto found = g_rects.find(id);
    return found == g_rects.end() ? nullptr : &found->second;
}

// Widgets drawn as invisible buttons report no label, so they are found by the id the
// drawing code gives them: the window's id, then each PushID in turn, then the item's own.
struct IdPath {
    ImGuiID id = 0;
    IdPath Str(const char* s) const { return IdPath{ImHashStr(s, 0, id)}; }
    IdPath Int(int n) const { return IdPath{ImHashData(&n, sizeof(n), id)}; }
};

static IdPath WindowPath(const char* identity)
{
    ImGuiWindow* window = ImGui::FindWindowByName(identity);
    return IdPath{window != nullptr ? window->ID : 0};
}

static const ImRect* RectOf(const IdPath& path)
{
    const auto found = g_rects.find(path.id);
    return found == g_rects.end() ? nullptr : &found->second;
}

// A plugin's switch on the bar.
static const ImRect* BarSwitch(const std::string& owner)
{
    if (ImGui::FindWindowByName("###decalbar") == nullptr)
        return nullptr;
    return RectOf(WindowPath("###decalbar").Str(("owner:" + owner).c_str()));
}

// A plugin's switch on Virindi View Service's bar.
static const ImRect* VvsSwitch(const std::string& owner)
{
    if (ImGui::FindWindowByName("###vvsbar") == nullptr)
        return nullptr;
    return RectOf(WindowPath("###vvsbar").Str(("owner:" + owner).c_str()));
}

static int64_t NowMs()
{
    return std::chrono::duration_cast<std::chrono::milliseconds>(
               std::chrono::system_clock::now().time_since_epoch())
        .count();
}

// Stands in for the DX12 backend's texture uploads: every request is marked done, which is
// all ImGui needs to go on drawing.
static void FakeUploads()
{
    for (ImTextureData* tex : ImGui::GetPlatformIO().Textures)
    {
        if (tex->Status == ImTextureStatus_WantCreate || tex->Status == ImTextureStatus_WantUpdates)
        {
            tex->SetTexID(static_cast<ImTextureID>(1));
            tex->SetStatus(ImTextureStatus_OK);
        }
        else if (tex->Status == ImTextureStatus_WantDestroy)
        {
            tex->SetTexID(ImTextureID_Invalid);
            tex->SetStatus(ImTextureStatus_Destroyed);
        }
    }
}

static std::vector<Command> Frame(const State& state, bool visible = true)
{
    ImGui::GetIO().DeltaTime = 1.0f / 60.0f;
    g_items.clear();
    g_rects.clear();
    ImGui::NewFrame();
    std::vector<Command> commands = overlay::DrawOverlay(state, visible);
    ImGui::Render();
    FakeUploads();
    return commands;
}

static void Append(std::vector<Command>& to, const std::vector<Command>& from)
{
    to.insert(to.end(), from.begin(), from.end());
}

static std::vector<Command> ClickAt(const State& state, ImVec2 at)
{
    ImGuiIO& io = ImGui::GetIO();
    std::vector<Command> all;
    io.AddMousePosEvent(at.x, at.y);
    Append(all, Frame(state));
    io.AddMouseButtonEvent(0, true);
    Append(all, Frame(state));
    io.AddMouseButtonEvent(0, false);
    Append(all, Frame(state));
    // Park the mouse where nothing is, so the next frame's hover is not this control's.
    io.AddMousePosEvent(1900.0f, 1060.0f);
    Append(all, Frame(state));
    return all;
}

static std::vector<Command> Click(const State& state, const std::string& label, const std::string& window_part = std::string())
{
    const Item* item = Find(label, window_part);
    if (item == nullptr)
    {
        std::printf("FAIL: no item labelled \"%s\"\n", label.c_str());
        ++g_failures;
        return {};
    }
    return ClickAt(state, item->bb.GetCenter());
}

// --- States --------------------------------------------------------------------------------

static Control MakeControl(const char* id, const char* label, ControlKind kind, const char* value)
{
    Control control;
    control.id = id;
    control.label = label;
    control.kind = kind;
    control.value = value;
    return control;
}

static State Rich()
{
    State state;
    state.revision = 5;
    state.published_ms = NowMs();
    state.status.server_connected = true;
    state.status.character = "Frostfell";

    // Decal's bar compact, as the player's registry has it.
    state.decal_bar.known = true;
    state.decal_bar.state = 1;
    state.decal_bar.length = 250;

    // Decal's own window: first on the bar, and closed until asked for.
    PluginWindow decal;
    decal.owner = "Decal";
    decal.starts_closed = true;
    state.windows.push_back(decal);

    PluginWindow tank;
    tank.owner = "VirindiTank";
    tank.title = "uTank2";
    tank.controls.push_back(MakeControl("loot", "Pick loot up", ControlKind::Toggle, "false"));
    tank.controls.back().tooltip = "Needs --enable-actions.";
    tank.controls.push_back(MakeControl("echo", "Say it in chat", ControlKind::Toggle, "true"));
    tank.controls.push_back(MakeControl("reload", "Reload profile", ControlKind::Button, ""));

    Control range = MakeControl("range", "Range", ControlKind::Slider, "12.5");
    range.min = 1.0;
    range.max = 40.0;
    range.step = 0.5;
    tank.controls.push_back(range);

    Control mode = MakeControl("mode", "Mode", ControlKind::Choice, "Melee");
    mode.options = {"Melee", "Missile", "Magic"};
    tank.controls.push_back(mode);

    tank.controls.push_back(MakeControl("name", "Name", ControlKind::Text, "Bob"));
    state.windows.push_back(tank);

    PluginWindow quiet;
    quiet.owner = "Quiet";
    state.windows.push_back(quiet);

    PluginWindow broken;
    broken.owner = "Broken";
    broken.enabled = false;
    state.windows.push_back(broken);

    Panel loot;
    loot.owner = "VirindiTank";
    loot.title = "Loot";
    loot.key = "loot";
    loot.columns = {"Item", "Decision"};
    Row row;
    row.cells = {"Leather Cap", "Keep"};
    loot.rows.push_back(row);
    state.panels.push_back(loot);

    // An owner the host did not declare: an older host, or a panel it failed to list.
    Panel legacy;
    legacy.owner = "Legacy";
    legacy.title = "Old";
    state.panels.push_back(legacy);

    return state;
}

static bool IsOpenWindow(const char* identity)
{
    ImGuiWindow* window = ImGui::FindWindowByName(identity);
    return window != nullptr && window->Active;
}

static void ExpectOne(const std::vector<Command>& commands, const char* name, const char* control_id, const char* value,
                      const char* owner = "VirindiTank")
{
    CHECK(commands.size() == 1);
    if (commands.size() != 1)
    {
        for (const Command& c : commands)
            std::printf("   got: %s %s=%s owner=%s\n", c.name.c_str(), c.control_id.c_str(), c.value.c_str(), c.owner.c_str());
        return;
    }
    CHECK(commands[0].name == name);
    CHECK(commands[0].control_id == control_id);
    CHECK(commands[0].value == value);
    if (commands[0].value != value)
        std::printf("   got: %s %s=%s\n", commands[0].name.c_str(), commands[0].control_id.c_str(), commands[0].value.c_str());
    CHECK(commands[0].owner == owner);
    CHECK(commands[0].row_id.empty());
}

int main()
{
    ImGui::CreateContext();
    GImGui->TestEngineHookItems = true;

    ImGuiIO& io = ImGui::GetIO();
    io.DisplaySize = ImVec2(1920.0f, 1080.0f);
    io.IniFilename = nullptr;
    overlay::RegisterDecalSettings();
    overlay::RegisterOverlaySettings();
    io.BackendFlags |= ImGuiBackendFlags_RendererHasTextures;

    // The fonts as the hook layer loads them: ImGui's own first, then the Decal faces.
    io.Fonts->AddFontDefault();
    const bool decal_fonts = overlay::LoadDecalFonts(io.Fonts);

    State state = Rich();

    // 1. Every declared plugin is on the bar, in order, then the undeclared one.
    {
        std::vector<Command> commands = Frame(state);
        commands = Frame(state);
        CHECK(commands.empty());

        // The host is not a plugin, and Decal's bar listed plugins only.
        CHECK(BarSwitch("") == nullptr);

        const ImRect* tank = BarSwitch("VirindiTank");
        const ImRect* quiet = BarSwitch("Quiet");
        const ImRect* broken = BarSwitch("Broken");
        const ImRect* legacy = BarSwitch("Legacy");
        CHECK(tank && quiet && broken && legacy);
        if (tank && quiet && broken && legacy)
        {
            CHECK(tank->Min.x < quiet->Min.x);
            CHECK(quiet->Min.x < broken->Min.x);
            CHECK(broken->Min.x < legacy->Min.x);
        }

        // The host's own window is diagnostics, and starts closed.
        CHECK(!IsOpenWindow("###window:"));

        // So does a window the host marks so - Decal's - though its switch is on the bar, first.
        const ImRect* decal = BarSwitch("Decal");
        CHECK(decal != nullptr);
        if (decal && tank)
            CHECK(decal->Min.x < tank->Min.x);
        CHECK(!IsOpenWindow("###window:Decal"));
        CHECK(IsOpenWindow("###window:VirindiTank"));
        CHECK(IsOpenWindow("###window:Quiet"));
        CHECK(IsOpenWindow("###window:Broken"));
        CHECK(IsOpenWindow("###window:Legacy"));

        // The title is what is shown; the identity is still the owner.
        ImGuiWindow* window = ImGui::FindWindowByName("###window:VirindiTank");
        CHECK(window != nullptr && std::string(window->Name).rfind("uTank2", 0) == 0);

        // The broken plugin's window draws no controls at all.
        for (const Item& item : g_items)
            CHECK(!(item.window.find("###window:Broken") != std::string::npos && item.label.find("###control") != std::string::npos));

        std::printf("1. bar lists %s, %s, %s, %s, and no Host\n", "uTank2", "Quiet", "Broken", "Legacy");
    }

    // 2. Closing windows from the bar, leaving only VirindiTank's in front.
    {
        for (const char* owner : {"Quiet", "Broken", "Legacy"})
        {
            const ImRect* at = BarSwitch(owner);
            CHECK(at != nullptr);
            if (at != nullptr)
                CHECK(ClickAt(state, at->GetCenter()).empty());
        }

        CHECK(!IsOpenWindow("###window:Quiet"));
        CHECK(!IsOpenWindow("###window:Broken"));
        CHECK(!IsOpenWindow("###window:Legacy"));
        CHECK(IsOpenWindow("###window:VirindiTank"));
        std::printf("2. bar buttons close windows, and send nothing\n");
    }

    // 3. A toggle sends "set" with the new value, and springs back while the host disagrees.
    {
        std::vector<Command> commands = Click(state, "Pick loot up###control", "VirindiTank");
        ExpectOne(commands, "set", "loot", "true");
        std::printf("3. loot toggle -> set loot=true\n");

        commands = Click(state, "Say it in chat###control", "VirindiTank");
        ExpectOne(commands, "set", "echo", "false");
        std::printf("   echo toggle -> set echo=false\n");

        // Clicked twice with the host never agreeing: the same request both times, because
        // the box shows the host's value rather than remembering the click.
        commands = Click(state, "Pick loot up###control", "VirindiTank");
        ExpectOne(commands, "set", "loot", "true");
    }

    // 4. A button sends "press".
    {
        std::vector<Command> commands = Click(state, "Reload profile###control", "VirindiTank");
        ExpectOne(commands, "press", "reload", "");
        std::printf("4. reload button -> press reload\n");
    }

    // 5. A slider sends once, when let go, snapped to its step.
    {
        const Item* found = Find("Range###control", "VirindiTank");
        CHECK(found != nullptr);
        if (found != nullptr)
        {
            // Copied: the table it points into is rebuilt by every frame below.
            const Item held = *found;
            const Item* slider = &held;
            std::vector<Command> during;
            const float y = slider->bb.GetCenter().y;
            const float x0 = slider->bb.Min.x + 4.0f;

            io.AddMousePosEvent(x0, y);
            Append(during, Frame(state));
            io.AddMouseButtonEvent(0, true);
            Append(during, Frame(state));
            for (int step = 1; step <= 10; ++step)
            {
                io.AddMousePosEvent(x0 + 60.0f * static_cast<float>(step), y);
                Append(during, Frame(state));
            }
            CHECK(during.empty());

            io.AddMouseButtonEvent(0, false);
            std::vector<Command> after = Frame(state);
            Append(after, Frame(state));
            ExpectOne(after, "set", "range", "40");
            std::printf("5. slider dragged past its end -> set range=%s, nothing while held\n",
                        after.empty() ? "?" : after[0].value.c_str());

            // Part way, to check the step: a quarter of the frame is not a multiple of 0.5 by
            // accident often enough to matter, so check the arithmetic rather than a value.
            io.AddMousePosEvent(x0, y);
            Frame(state);
            io.AddMouseButtonEvent(0, true);
            Frame(state);
            io.AddMousePosEvent(x0 + 37.0f, y);
            Frame(state);
            io.AddMouseButtonEvent(0, false);
            std::vector<Command> part = Frame(state);
            Append(part, Frame(state));
            CHECK(part.size() == 1);
            if (part.size() == 1)
            {
                const double value = std::stod(part[0].value);
                CHECK(value >= 1.0 && value <= 40.0);
                CHECK(std::fabs(std::fmod(value - 1.0, 0.5)) < 1e-9);
                std::printf("   part-way drag -> set range=%s (a whole number of 0.5 steps from 1)\n", part[0].value.c_str());
            }
            io.AddMousePosEvent(1900.0f, 1060.0f);
            Frame(state);
        }
    }

    // 6. A choice sends the option picked, and nothing for the one already chosen.
    {
        const ImRect* combo = ControlRect("VirindiTank", "mode");
        CHECK(combo != nullptr);
        if (combo != nullptr)
        {
            const ImVec2 at(combo->Min.x + 20.0f, combo->GetCenter().y);
            CHECK(ClickAt(state, at).empty());

            std::vector<Command> commands = Click(state, "Missile");
            ExpectOne(commands, "set", "mode", "Missile");
            std::printf("6. choice -> set mode=Missile\n");

            CHECK(ClickAt(state, at).empty());
            CHECK(Click(state, "Melee").empty());
            std::printf("   choosing the current option sends nothing\n");
        }
    }

    // 7. A text box sends once, on Enter, with what was typed - even though the host
    //    republishes its own value underneath every frame.
    {
        const Item* box = Find("Name###control", "VirindiTank");
        CHECK(box != nullptr);
        if (box != nullptr)
        {
            // Inside the frame, right of the text, so the cursor lands at the end.
            const ImVec2 at(box->bb.Min.x + 150.0f, box->bb.GetCenter().y);
            std::vector<Command> commands = ClickAt(state, at);
            CHECK(commands.empty());

            io.AddInputCharacter('b');
            commands = Frame(state);
            io.AddInputCharacter('y');
            Append(commands, Frame(state));
            CHECK(commands.empty());

            io.AddKeyEvent(ImGuiKey_Enter, true);
            Append(commands, Frame(state));
            io.AddKeyEvent(ImGuiKey_Enter, false);
            Append(commands, Frame(state));
            Append(commands, Frame(state));
            ExpectOne(commands, "set", "name", "Bobby");
            std::printf("7. text box -> set name=%s on Enter, nothing while typing\n",
                        commands.empty() ? "?" : commands[0].value.c_str());
        }
    }

    // 8. A slider held while its plugin vanishes sends nothing when let go.
    {
        const Item* found = Find("Range###control", "VirindiTank");
        CHECK(found != nullptr);
        if (found != nullptr)
        {
            // Copied: the table it points into is rebuilt by every frame below.
            const Item held = *found;
            const Item* slider = &held;
            const float y = slider->bb.GetCenter().y;
            io.AddMousePosEvent(slider->bb.Min.x + 4.0f, y);
            Frame(state);
            io.AddMouseButtonEvent(0, true);
            Frame(state);
            io.AddMousePosEvent(slider->bb.Min.x + 80.0f, y);
            Frame(state);

            State gone = state;
            gone.windows.erase(std::remove_if(gone.windows.begin(), gone.windows.end(),
                                              [](const PluginWindow& w) { return w.owner == "VirindiTank"; }),
                               gone.windows.end());
            std::vector<Command> commands = Frame(gone);
            io.AddMouseButtonEvent(0, false);
            Append(commands, Frame(gone));
            Append(commands, Frame(state));
            Append(commands, Frame(state));
            CHECK(commands.empty());
            std::printf("8. a held slider whose plugin vanished sends nothing\n");
        }
    }

    // 9. Hidden means nothing is submitted and nothing is sent.
    {
        std::vector<Command> commands = Frame(state, false);
        CHECK(commands.empty());
        // ImGui submits its own implicit "Debug##Default" window every frame and hides it
        // when nothing is in it. Anything else here came from the overlay.
        int ours = 0;
        for (const Item& item : g_items)
        {
            if (item.window.rfind("Debug##Default", 0) == 0)
                continue;
            std::printf("   hidden frame reported: \"%s\" in \"%s\"\n", item.label.c_str(), item.window.c_str());
            ++ours;
        }
        CHECK(ours == 0);
        Frame(state);
        std::printf("9. hidden overlay submits no widgets\n");
    }

    // 10. Hostile input: too many owners, broken ranges, no ids, huge option lists.
    {
        State bad;
        bad.revision = 1;
        bad.published_ms = NowMs();
        for (int index = 0; index < 100; ++index)
        {
            PluginWindow window;
            window.owner = "P" + std::to_string(index);
            Control upside = MakeControl("u", "Upside", ControlKind::Slider, "3");
            upside.min = 5.0;
            upside.max = 1.0;
            window.controls.push_back(upside);
            Control nan = MakeControl("n", "NaN", ControlKind::Slider, "nan");
            nan.min = std::numeric_limits<double>::quiet_NaN();
            window.controls.push_back(nan);
            Control huge = MakeControl("h", "Huge", ControlKind::Slider, "1e308");
            huge.min = -1e308;
            huge.max = 1e308;
            window.controls.push_back(huge);
            Control garbage = MakeControl("g", "Garbage", ControlKind::Slider, "twelve");
            garbage.max = 10.0;
            window.controls.push_back(garbage);
            window.controls.push_back(MakeControl("", "No id", ControlKind::Toggle, "true"));
            window.controls.push_back(MakeControl("pct", "100% %s %n", ControlKind::Button, ""));
            Control many = MakeControl("m", "Many", ControlKind::Choice, "not an option");
            for (int option = 0; option < 1000; ++option)
                many.options.push_back("o" + std::to_string(option));
            window.controls.push_back(many);
            for (int extra = 0; extra < 100; ++extra)
                window.controls.push_back(MakeControl(("x" + std::to_string(extra)).c_str(), "x", ControlKind::Toggle, "maybe"));
            bad.windows.push_back(window);
        }
        PluginWindow duplicate;
        duplicate.owner = "P0";
        bad.windows.push_back(duplicate);
        PluginWindow empty_owner;
        bad.windows.push_back(empty_owner);

        std::vector<Command> commands = Frame(bad);
        Append(commands, Frame(bad));
        CHECK(commands.empty());

        // 32 groups including the host, which has no switch: 31 plugins, more than Decal's
        // 250-pixel bar holds, so it pages - as many as fit, and the arrows to go on.
        auto visible = [&]() {
            std::vector<int> shown;
            for (int index = 0; index < 100; ++index)
                if (BarSwitch("P" + std::to_string(index)) != nullptr)
                    shown.push_back(index);
            return shown;
        };
        const std::vector<int> first_page = visible();
        CHECK(!first_page.empty() && first_page.size() < 31);

        // With nothing in Decal's registry the bar is expanded, as Decal's own BarState 0 was:
        // labelled switches, a hundred pixels each.
        if (!first_page.empty())
        {
            const ImRect* labelled = BarSwitch("P" + std::to_string(first_page.front()));
            CHECK(labelled != nullptr && std::fabs(labelled->GetWidth() - 100.0f) < 0.5f && std::fabs(labelled->GetHeight() - 21.0f) < 0.5f);
        }
        const ImRect* on = RectOf(WindowPath("###decalbar").Str("page-on"));
        CHECK(on != nullptr);
        if (on != nullptr)
        {
            CHECK(ClickAt(bad, on->GetCenter()).empty());
            const std::vector<int> second_page = visible();
            CHECK(!second_page.empty() && second_page.front() > first_page.front());
        }
        std::printf("10. 100 hostile plugins: %d bar buttons a page, paged on, no assert, nothing sent\n", static_cast<int>(first_page.size()));

        // The "controls not shown" lines are text, which the hooks do not report; that they
        // drew without an assert is what this checks.
    }

    // 12. Decal's bar: a drag on a grip moves it and sends nothing; Ctrl+click on the first
    //     grip opens the host's status window; the gold square switches it between compact
    //     and expanded; the grey square docks it to the left, the right and the top again.
    {
        // Compact, as the player's registry has it; with nothing there Decal's bar starts
        // expanded, which the hostile plugins above had.
        State plain = Rich();
        plain.decal_bar.known = true;
        plain.decal_bar.state = 1;
        plain.decal_bar.length = 250;
        Frame(plain);
        ImGuiWindow* bar = ImGui::FindWindowByName("###decalbar");
        const ImRect* grip = RectOf(WindowPath("###decalbar").Str("grip-start"));
        CHECK(bar != nullptr && grip != nullptr);
        if (bar != nullptr && grip != nullptr)
        {
            const ImVec2 before = bar->Pos;
            const ImVec2 at = grip->GetCenter();
            std::vector<Command> commands;

            io.AddMousePosEvent(at.x, at.y);
            Append(commands, Frame(plain));
            io.AddMouseButtonEvent(0, true);
            Append(commands, Frame(plain));
            for (int step = 1; step <= 5; ++step)
            {
                io.AddMousePosEvent(at.x + 40.0f * static_cast<float>(step), at.y + 30.0f * static_cast<float>(step));
                Append(commands, Frame(plain));
            }
            io.AddMouseButtonEvent(0, false);
            Append(commands, Frame(plain));
            Append(commands, Frame(plain));

            const ImVec2 after = bar->Pos;
            CHECK(std::fabs((after.x - before.x) - 200.0f) < 1.0f);
            CHECK(std::fabs((after.y - before.y) - 150.0f) < 1.0f);
            CHECK(commands.empty());
            CHECK(BarSwitch("VirindiTank") != nullptr);
            std::printf("12. dragging the grip moved the bar by %.0f,%.0f\n", after.x - before.x, after.y - before.y);

            // Ctrl and a click: the host's window, which the bar has no switch for.
            const ImRect* moved = RectOf(WindowPath("###decalbar").Str("grip-start"));
            CHECK(moved != nullptr);
            if (moved != nullptr)
            {
                CHECK(!IsOpenWindow("###window:"));
                io.AddKeyEvent(ImGuiMod_Ctrl, true);
                CHECK(ClickAt(plain, moved->GetCenter()).empty());
                io.AddKeyEvent(ImGuiMod_Ctrl, false);
                Frame(plain);
                CHECK(IsOpenWindow("###window:"));
                std::printf("   ctrl+click on the grip opens the host's status window\n");

                // And closes it again, so it covers nothing the rest of this clicks.
                if (const ImRect* again = RectOf(WindowPath("###decalbar").Str("grip-start")))
                {
                    io.AddKeyEvent(ImGuiMod_Ctrl, true);
                    ClickAt(plain, again->GetCenter());
                    io.AddKeyEvent(ImGuiMod_Ctrl, false);
                    Frame(plain);
                }
                CHECK(!IsOpenWindow("###window:"));
            }

            // The gold square: compact squares, then Decal's 100-pixel labelled switches.
            const ImRect* compact = BarSwitch("VirindiTank");
            CHECK(compact != nullptr && compact->GetWidth() < 30.0f);
            if (const ImRect* minmax = RectOf(WindowPath("###decalbar").Str("minmax")))
            {
                CHECK(ClickAt(plain, minmax->GetCenter()).empty());
                // A 250-pixel bar holds one switch this wide, so whichever is showing.
                const ImRect* expanded = nullptr;
                for (const PluginWindow& window : plain.windows)
                    if (expanded == nullptr)
                        expanded = BarSwitch(window.owner);
                CHECK(expanded != nullptr && std::fabs(expanded->GetWidth() - 100.0f) < 0.5f);
                std::printf("   the gold square shows the switches with their names\n");

                // The player's own bar is 114 long, too short for a 100-pixel switch and its
                // ends: expanded, it is lengthened to show one whole, short of the grey square.
                State short_bar = plain;
                short_bar.decal_bar.known = true;
                short_bar.decal_bar.length = 114;
                short_bar.revision += 1;
                Frame(short_bar);
                Frame(short_bar);
                const ImRect* whole = nullptr;
                for (const PluginWindow& window : short_bar.windows)
                    if (whole == nullptr)
                        whole = BarSwitch(window.owner);
                const ImRect* grey = RectOf(WindowPath("###decalbar").Str("dock"));
                CHECK(whole != nullptr && grey != nullptr && whole->Max.x <= grey->Min.x + 0.5f);
                ImGuiWindow* lengthened = ImGui::FindWindowByName("###decalbar");
                CHECK(lengthened != nullptr && lengthened->Size.x >= 160.0f - 0.5f);
                Frame(plain);
                Frame(plain);
            }
            else
            {
                CHECK(!"no min/max square");
            }

            // The grey square: left, then right, then back along the top.
            if (const ImRect* dock = RectOf(WindowPath("###decalbar").Str("dock")))
            {
                CHECK(ClickAt(plain, dock->GetCenter()).empty());
                ImGuiWindow* left = ImGui::FindWindowByName("###decalbar-left");
                CHECK(left != nullptr && left->Active && std::fabs(left->Pos.x - ImGui::GetMainViewport()->WorkPos.x) < 0.5f);
                CHECK(left != nullptr && left->Size.y > left->Size.x);
                if (const ImRect* next = RectOf(WindowPath("###decalbar-left").Str("dock")))
                    CHECK(ClickAt(plain, next->GetCenter()).empty());
                ImGuiWindow* right = ImGui::FindWindowByName("###decalbar-right");
                CHECK(right != nullptr && right->Active);
                if (const ImRect* next = RectOf(WindowPath("###decalbar-right").Str("dock")))
                    CHECK(ClickAt(plain, next->GetCenter()).empty());
                CHECK(ImGui::FindWindowByName("###decalbar")->Active);
                std::printf("   the grey square docks the bar left, right and back along the top\n");
            }
            else
            {
                CHECK(!"no dock square");
            }

            // What the player set is kept in the ini.
            const std::string ini = ImGui::SaveIniSettingsToMemory();
            CHECK(ini.find("[DecalBar][Bar]") != std::string::npos);
            CHECK(ini.find("State=0") != std::string::npos);
            CHECK(ini.find("Dock=0") != std::string::npos);

            // Back to compact for what follows.
            if (const ImRect* minmax = RectOf(WindowPath("###decalbar").Str("minmax")))
                ClickAt(plain, minmax->GetCenter());
        }
        io.AddMousePosEvent(1900.0f, 1060.0f);
        Frame(plain);
    }

    // 13. A Decal view: every control kind, the tabs, the title bar, and textures.
    {
        using overlay::View;
        using overlay::ViewControl;
        using overlay::ViewControlType;
        using overlay::ViewColumn;
        using overlay::ViewColumnType;
        using overlay::ViewPage;
        using overlay::ViewRow;
        using overlay::ViewCell;

        auto control = [](ViewControlType type, const char* name, int x, int y, int w, int h) {
            ViewControl c;
            c.type = type;
            c.name = name;
            c.x = x;
            c.y = y;
            c.w = w;
            c.h = h;
            return c;
        };

        ViewControl options;
        options.type = overlay::ViewControlType::Fixed;
        options.children.push_back(control(ViewControlType::Checkbox, "cLoot", 8, 8, 120, 20));
        options.children.back().text = "Enable Looting";
        options.children.push_back(control(ViewControlType::PushButton, "bBuff", 8, 32, 112, 25));
        options.children.back().text = "Force Buff";
        options.children.push_back(control(ViewControlType::Edit, "txtRange", 8, 64, 60, 16));
        options.children.back().value = "4";
        options.children.push_back(control(ViewControlType::Slider, "slHP", 8, 88, 144, 16));
        options.children.back().value = "50";
        options.children.push_back(control(ViewControlType::Choice, "cmbNav", 160, 8, 80, 16));
        options.children.back().options = {"Circular", "Linear", "Follow"};
        options.children.back().selected = 0;
        options.children.push_back(control(ViewControlType::Static, "lblE", 160, 32, 16, 16));
        options.children.back().text = "E";
        options.children.back().text_color = 0xFFC00000;
        options.children.back().justify = overlay::Justify::Center;
        options.children.push_back(control(ViewControlType::Progress, "prg", 160, 56, 80, 8));
        options.children.back().value = "30";
        options.children.push_back(control(ViewControlType::Button, "btnUp", 250, 8, 16, 16));
        options.children.back().image = "portal:060012B2";
        options.children.push_back(control(ViewControlType::Unknown, "odd", 0, 0, 10, 10));

        ViewControl monsters;
        monsters.type = overlay::ViewControlType::Fixed;
        ViewControl list = control(ViewControlType::List, "lstMonsters", 0, 0, 300, 80);
        list.columns.push_back(ViewColumn{ViewColumnType::Check, 16});
        list.columns.push_back(ViewColumn{ViewColumnType::Text, 0});
        list.columns.push_back(ViewColumn{ViewColumnType::Icon, 16});
        for (int row = 0; row < 12; ++row)
        {
            ViewRow data;
            data.cells.push_back(ViewCell{"", row % 2 == 0, "", -1});
            data.cells.push_back(ViewCell{"Drudge " + std::to_string(row), false, "", row == 1 ? 0xFFFF0000 : -1});
            data.cells.push_back(ViewCell{"", false, "portal:060029AB", -1});
            list.rows.push_back(data);
        }
        monsters.children.push_back(list);

        State decal;
        decal.revision = 9;
        decal.published_ms = NowMs();
        PluginWindow tank;
        tank.owner = "VirindiTank";
        tank.has_view = true;
        tank.view.title = "Virindi Tank";
        tank.view.icon = "portal:060029AB";
        tank.view.width = 420;
        tank.view.height = 150;
        tank.view.root.type = ViewControlType::Notebook;
        tank.view.root.name = "MainTabs";
        tank.view.root.pages.push_back(ViewPage{"Options", {options}});
        tank.view.root.pages.push_back(ViewPage{"Monsters", {monsters}});
        tank.view.root.pages.push_back(ViewPage{"Empty", {}});
        decal.windows.push_back(tank);

        // Some of the theme's images, as the host would send them. The rest stay missing, so
        // the stand-ins are exercised too.
        std::vector<overlay::ImagePixels> images;
        for (const char* key : {"portal:0600126F", "portal:0600191A", "portal:0600191F", "portal:060029AB",
                                "vvs:Decal_Theme_Images.TabActiveLeft.png", "vvs:Decal_Theme_Images.TabActiveCenter.png",
                                "vvs:Decal_Theme_Images.TabActiveRight.png", "bar:open", "bar:closed"})
        {
            overlay::ImagePixels image;
            image.key = key;
            image.width = 64;
            image.height = 20;
            image.rgba.assign(64 * 20 * 4, 200);
            images.push_back(image);
        }
        overlay::AcceptImages(images);
        CHECK(overlay::TextureCount() == 9);

        // A replacement for one key: the old texture is retired, not leaked or freed early.
        std::vector<overlay::ImagePixels> again(1, images[0]);
        overlay::AcceptImages(again);
        CHECK(overlay::TextureCount() == 9);

        std::vector<Command> commands = Frame(decal);
        Append(commands, Frame(decal));
        overlay::CollectRetiredTextures();
        Append(commands, Frame(decal));
        overlay::CollectRetiredTextures();
        CHECK(commands.empty());

        const IdPath window = WindowPath("###decal:VirindiTank");
        CHECK(window.id != 0);
        const IdPath notebook = window.Str("VirindiTank").Str("MainTabs");
        const IdPath page0 = notebook.Int(0).Int(0);

        // The checkbox, by its name in the view.
        if (const ImRect* box = RectOf(page0.Str("cLoot").Str("check")))
        {
            ExpectOne(ClickAt(decal, box->GetCenter()), "set", "cLoot", "true");
            std::printf("13. Decal checkbox -> set cLoot=true\n");
        }
        else
        {
            CHECK(!"checkbox not found");
        }

        if (const ImRect* push = RectOf(page0.Str("bBuff").Str("push")))
            ExpectOne(ClickAt(decal, push->GetCenter()), "press", "bBuff", "");
        else
            CHECK(!"push button not found");

        if (const ImRect* button = RectOf(page0.Str("btnUp").Str("button")))
            ExpectOne(ClickAt(decal, button->GetCenter()), "press", "btnUp", "");
        else
            CHECK(!"image button not found");

        // The edit box: click in, type, Enter.
        if (const ImRect* edit = RectOf(page0.Str("txtRange").Str("##edit")))
        {
            std::vector<Command> typed = ClickAt(decal, ImVec2(edit->Max.x - 4.0f, edit->GetCenter().y));
            io.AddInputCharacter('2');
            Append(typed, Frame(decal));
            io.AddKeyEvent(ImGuiKey_Enter, true);
            Append(typed, Frame(decal));
            io.AddKeyEvent(ImGuiKey_Enter, false);
            Append(typed, Frame(decal));
            // Let go by Enter: "enter", which the host takes as a set and as VVS's Enter key.
            ExpectOne(typed, "enter", "txtRange", "42");
            std::printf("   Decal edit -> enter txtRange=%s\n", typed.empty() ? "?" : typed[0].value.c_str());

            // Let go by a click elsewhere: only "set". Long enough after the last click not to be
            // a double click, which would choose the word rather than put the cursor at its end.
            std::vector<Command> left;
            for (int wait = 0; wait < 30; ++wait)
                Append(left, Frame(decal));
            if (const ImRect* edit_again = RectOf(page0.Str("txtRange").Str("##edit")))
            {
                Append(left, ClickAt(decal, ImVec2(edit_again->Max.x - 4.0f, edit_again->GetCenter().y)));
                io.AddInputCharacter('7');
                Append(left, Frame(decal));
                Append(left, ClickAt(decal, ImVec2(1900.0f, 1060.0f)));
            }
            ExpectOne(left, "set", "txtRange", "47");
            std::printf("   and left by a click elsewhere -> set txtRange=%s\n", left.empty() ? "?" : left[0].value.c_str());
        }
        else
        {
            CHECK(!"edit not found");
        }

        // The slider: drag to the right end, one whole-number command on release.
        if (const ImRect* found = RectOf(page0.Str("slHP").Str("slider")))
        {
            // Copied: the table it points into is rebuilt by every frame below.
            const ImRect bounds = *found;
            const ImRect* slider = &bounds;
            std::vector<Command> dragged;
            const float y = slider->GetCenter().y;
            io.AddMousePosEvent(slider->GetCenter().x, y);
            Append(dragged, Frame(decal));
            io.AddMouseButtonEvent(0, true);
            Append(dragged, Frame(decal));
            for (int step = 1; step <= 6; ++step)
            {
                io.AddMousePosEvent(slider->GetCenter().x + 60.0f * static_cast<float>(step), y);
                Append(dragged, Frame(decal));
            }
            CHECK(dragged.empty());
            io.AddMouseButtonEvent(0, false);
            Append(dragged, Frame(decal));
            Append(dragged, Frame(decal));
            ExpectOne(dragged, "set", "slHP", "100");
            io.AddMousePosEvent(1900.0f, 1060.0f);
            Frame(decal);
        }
        else
        {
            CHECK(!"slider not found");
        }

        // The dropdown: open it, pick the second option.
        if (const ImRect* choice = RectOf(page0.Str("cmbNav").Str("choice")))
        {
            CHECK(ClickAt(decal, choice->GetCenter()).empty());
            ImGuiContext& g = *GImGui;
            CHECK(!g.OpenPopupStack.empty());
            if (!g.OpenPopupStack.empty() && g.OpenPopupStack.back().Window != nullptr)
            {
                const IdPath popup{g.OpenPopupStack.back().Window->ID};
                if (const ImRect* option = RectOf(popup.Int(1).Str("option")))
                {
                    ExpectOne(ClickAt(decal, option->GetCenter()), "set", "cmbNav", "1");
                    std::printf("   Decal choice -> set cmbNav=1\n");
                }
                else
                {
                    CHECK(!"option not found");
                }
            }
        }
        else
        {
            CHECK(!"choice not found");
        }

        // The tabs: clicking the second sends "page" and shows its list.
        if (const ImRect* tab = RectOf(notebook.Int(1).Str("tab")))
        {
            ExpectOne(ClickAt(decal, tab->GetCenter()), "page", "MainTabs", "1");
            Frame(decal);
            const IdPath page1 = notebook.Int(1).Int(0);

            // Row 3, column 1 (the text column).
            const IdPath cell = page1.Str("lstMonsters").Int(3).Int(1).Str("cell");
            if (const ImRect* at = RectOf(cell))
            {
                std::vector<Command> clicked = ClickAt(decal, at->GetCenter());
                CHECK(clicked.size() == 1);
                if (clicked.size() == 1)
                {
                    CHECK(clicked[0].name == "click" && clicked[0].control_id == "lstMonsters");
                    CHECK(clicked[0].row_id == "3" && clicked[0].value == "1");
                }
                std::printf("   Decal tab -> page MainTabs=1; list cell -> click row 3, column 1\n");
            }
            else
            {
                CHECK(!"list cell not found");
            }

            // The options page's controls are not drawn while another page shows.
            CHECK(RectOf(page0.Str("cLoot").Str("check")) == nullptr);

            // The plugin choosing a page wins over the player's choice.
            decal.windows[0].view.root.selected = 0;
            Frame(decal);
            Frame(decal);
            CHECK(RectOf(page0.Str("cLoot").Str("check")) != nullptr);
        }
        else
        {
            CHECK(!"tab not found");
        }

        // The title bar moves the window; the close button closes it.
        ImGuiWindow* win = ImGui::FindWindowByName("###decal:VirindiTank");
        if (const ImRect* title = RectOf(window.Str("title")); title != nullptr && win != nullptr)
        {
            const ImVec2 before = win->Pos;
            const ImVec2 at(title->Min.x + 60.0f, title->GetCenter().y);
            io.AddMousePosEvent(at.x, at.y);
            Frame(decal);
            io.AddMouseButtonEvent(0, true);
            Frame(decal);
            io.AddMousePosEvent(at.x + 50.0f, at.y + 40.0f);
            Frame(decal);
            io.AddMouseButtonEvent(0, false);
            Frame(decal);
            CHECK(std::fabs(win->Pos.x - before.x - 50.0f) < 1.0f && std::fabs(win->Pos.y - before.y - 40.0f) < 1.0f);
            std::printf("   Decal title bar drag moved the window by %.0f,%.0f\n", win->Pos.x - before.x, win->Pos.y - before.y);
        }
        else
        {
            CHECK(!"title bar not found");
        }

        if (const ImRect* close = RectOf(window.Str("close")))
        {
            CHECK(ClickAt(decal, close->GetCenter()).empty());
            CHECK(!IsOpenWindow("###decal:VirindiTank"));
            std::printf("   Decal close button closed the window\n");
        }
        else
        {
            CHECK(!"close button not found");
        }
    }

    // 11. An empty snapshot, and the waiting state, still draw.
    {
        State empty;
        CHECK(Frame(empty).empty());
        CHECK(Frame(empty).empty());
        std::printf("11. before the first publish, nothing is sent\n");
    }

    // 14. Decal text is drawn by GDI: one bit per pixel, hinted, sized by its em.
    {
        CHECK(decal_fonts);
        const overlay::DecalFonts& fonts = overlay::GetDecalFonts();
        CHECK(fonts.regular != nullptr && fonts.bold != nullptr);
        if (fonts.regular != nullptr)
        {
            // Eight points at 96 per inch, at the overlay's scale of 1.25: a 13-pixel em.
            ImFontBaked* baked = fonts.regular->GetFontBaked(13.0f);
            ImFontGlyph* h = baked != nullptr ? baked->FindGlyph('H') : nullptr;
            CHECK(h != nullptr && h->Visible);
            if (h != nullptr && h->Visible)
            {
                const float cap = h->Y1 - h->Y0;
                CHECK(cap >= 7.0f && cap <= 11.0f);          // a capital is about two thirds of the em
                CHECK(h->AdvanceX == std::floor(h->AdvanceX));  // whole-pixel advances, as GDI gives them
                CHECK(baked->Ascent > 0.0f && baked->Descent < 0.0f);

                // Every pixel of the glyph in the atlas is either clear or fully opaque.
                ImFontAtlas* atlas = io.Fonts;
                ImTextureRect* rect = ImFontAtlasPackGetRect(atlas, h->PackId);
                ImTextureData* tex = atlas->TexData;
                CHECK(rect != nullptr && tex != nullptr && tex->Pixels != nullptr);
                if (rect != nullptr && tex != nullptr && tex->Pixels != nullptr)
                {
                    int grey = 0;
                    int solid = 0;
                    for (int y = rect->y; y < rect->y + rect->h; ++y)
                    {
                        for (int x = rect->x; x < rect->x + rect->w; ++x)
                        {
                            const unsigned char* px = static_cast<unsigned char*>(tex->GetPixelsAt(x, y));
                            const unsigned char alpha = tex->Format == ImTextureFormat_RGBA32 ? px[3] : px[0];
                            if (alpha == 255) ++solid;
                            else if (alpha != 0) ++grey;
                        }
                    }
                    CHECK(grey == 0);
                    CHECK(solid > 10);
                    std::printf("14. Decal text: 'H' at a 13px em is %.0f px tall, %d solid pixels, %d grey\n", cap, solid, grey);
                }
            }
        }
    }

    // 15. Keys held for the host: the whole wish each time, releases before presses, and
    // nothing left down once it is withdrawn.
    {
        std::vector<std::string> posted;
        overlay::HeldKeys keys([&](uint16_t key, bool down) {
            posted.push_back(std::string(down ? "down " : "up ") + std::to_string(key));
        });

        keys.Apply({'W'});
        CHECK(posted == std::vector<std::string>{"down 87"});

        posted.clear();
        keys.Apply({'W', 'A', 'W'});
        CHECK(posted == std::vector<std::string>{"down 65"});

        // The same wish again is no change at all: a repeat from the host presses nothing.
        posted.clear();
        keys.Apply({'A', 'W'});
        CHECK(posted.empty());

        // Turning the other way lets go of the old turn first.
        posted.clear();
        keys.Apply({'W', 'D'});
        CHECK(posted == (std::vector<std::string>{"up 65", "down 68"}));

        // Not keys at all are ignored rather than posted.
        posted.clear();
        keys.Apply({'W', 'D', 0, 300});
        CHECK(posted.empty());

        posted.clear();
        keys.ReleaseAll();
        CHECK(posted == (std::vector<std::string>{"up 87", "up 68"}));
        CHECK(keys.Held().empty());

        // Only messages this posted are marked as its own.
        const LPARAM ours = 1 | overlay::kInjectedKeyMarker;
        CHECK(overlay::IsInjectedKey(WM_KEYDOWN, ours));
        CHECK(overlay::IsInjectedKey(WM_KEYUP, ours | (static_cast<LPARAM>(3) << 30)));
        CHECK(!overlay::IsInjectedKey(WM_KEYDOWN, 1 | (0x11 << 16)));
        CHECK(!overlay::IsInjectedKey(WM_CHAR, ours));
        std::printf("15. Held keys: presses, releases and repeats as the host asks\n");

        // A hotkey matches its key with exactly its modifiers, and nothing else.
        std::vector<overlay::Hotkey> hotkeys(2);
        hotkeys[0].owner = "VirindiTank";
        hotkeys[0].id = "ToggleMacro";
        hotkeys[0].key = VK_F12;
        hotkeys[0].ctrl = true;
        hotkeys[1].owner = "Decal";
        hotkeys[1].id = "ToggleBar";
        hotkeys[1].key = VK_F12;
        const overlay::Hotkey* macro = overlay::FindHotkey(hotkeys, VK_F12, true, false, false);
        CHECK(macro != nullptr && macro->id == "ToggleMacro");
        const overlay::Hotkey* bar = overlay::FindHotkey(hotkeys, VK_F12, false, false, false);
        CHECK(bar != nullptr && bar->owner == "Decal");
        CHECK(overlay::FindHotkey(hotkeys, VK_F12, true, true, false) == nullptr);
        CHECK(overlay::FindHotkey(hotkeys, VK_F11, true, false, false) == nullptr);
    }

    // 16. Two bars, as the standard client has them: a VVS view's switch is on Virindi View
    // Service's bar down the side, and only there; everything else is on Decal's across the top.
    {
        State two;
        two.revision = 11;
        two.published_ms = NowMs();

        PluginWindow tank;
        tank.owner = "Tank";
        tank.has_view = true;
        tank.view.title = "Virindi Tank";
        tank.view.icon = "portal:060029AB";
        tank.view.bar = "vvs";
        tank.view.width = 200;
        tank.view.height = 100;
        tank.view.root.type = overlay::ViewControlType::Fixed;
        two.windows.push_back(tank);

        PluginWindow sorter;
        sorter.owner = "Sorter";
        sorter.has_view = true;
        sorter.view.title = "Sorter";
        sorter.view.width = 200;
        sorter.view.height = 100;
        sorter.view.root.type = overlay::ViewControlType::Fixed;
        two.windows.push_back(sorter);

        Frame(two);
        Frame(two);

        CHECK(VvsSwitch("Tank") != nullptr);
        CHECK(BarSwitch("Tank") == nullptr);
        CHECK(BarSwitch("Sorter") != nullptr);
        CHECK(VvsSwitch("Sorter") == nullptr);

        // Down the side: the VVS bar is a column, so it is taller than it is wide.
        ImGuiWindow* column = ImGui::FindWindowByName("###vvsbar");
        CHECK(column != nullptr);

        // A click on its icon opens or closes the window, and sends nothing.
        const bool before = IsOpenWindow("###decal:Tank");
        if (const ImRect* at = VvsSwitch("Tank"))
        {
            CHECK(ClickAt(two, at->GetCenter()).empty());
            Frame(two);
            CHECK(IsOpenWindow("###decal:Tank") != before);
        }

        // Closed from the bar, then asked for by its plugin: it opens again.
        if (IsOpenWindow("###decal:Tank"))
        {
            if (const ImRect* at = VvsSwitch("Tank"))
                ClickAt(two, at->GetCenter());
        }
        Frame(two);
        CHECK(!IsOpenWindow("###decal:Tank"));
        two.windows[0].view.open_request = 1;
        Frame(two);
        Frame(two);
        CHECK(IsOpenWindow("###decal:Tank"));

        // The same request seen again does not reopen it once the player closes it.
        if (const ImRect* at = VvsSwitch("Tank"))
            ClickAt(two, at->GetCenter());
        Frame(two);
        CHECK(!IsOpenWindow("###decal:Tank"));

        // A key bound to it in VHS turns it over once for each press; its own Close closes it.
        two.windows[0].view.toggle_request = 1;
        Frame(two);
        Frame(two);
        CHECK(IsOpenWindow("###decal:Tank"));
        two.windows[0].view.toggle_request = 2;
        Frame(two);
        Frame(two);
        CHECK(!IsOpenWindow("###decal:Tank"));
        two.windows[0].view.toggle_request = 3;
        Frame(two);
        Frame(two);
        CHECK(IsOpenWindow("###decal:Tank"));
        two.windows[0].view.close_request = 1;
        Frame(two);
        Frame(two);
        CHECK(!IsOpenWindow("###decal:Tank"));
        Frame(two);
        CHECK(!IsOpenWindow("###decal:Tank"));

        // "ab" is first on the VVS bar and steps VVS's primary theme on. Copied, since the
        // rectangles are rebuilt every frame.
        const ImRect* ab_found = RectOf(WindowPath("###vvsbar").Str("theme"));
        const ImRect* tank_found = VvsSwitch("Tank");
        CHECK(ab_found != nullptr && tank_found != nullptr && ab_found->Min.y < tank_found->Min.y);
        const ImRect ab_rect = ab_found != nullptr ? *ab_found : ImRect();
        const ImRect tank_rect = tank_found != nullptr ? *tank_found : ImRect();
        const ImRect* ab = ab_found != nullptr ? &ab_rect : nullptr;
        const ImRect* tank_cell = tank_found != nullptr ? &tank_rect : nullptr;
        if (ab != nullptr)
        {
            // Through all six, in the order VVS registered them, and round to where it began.
            static const char* const kVvsOrder[] = {"Minimalist", "Float", "Minimalist Transparent", "Decal", "Minimalist Black",
                                                    "Minimalist Green"};
            const size_t themes = sizeof(kVvsOrder) / sizeof(kVvsOrder[0]);
            const std::string before_theme = overlay::DecalGlobalTheme();
            size_t at = themes;
            for (size_t i = 0; i < themes; ++i)
                if (before_theme == kVvsOrder[i]) at = i;
            CHECK(at < themes);
            for (size_t step = 1; step <= themes && at < themes; ++step)
            {
                CHECK(ClickAt(two, ab->GetCenter()).empty());
                CHECK(overlay::DecalGlobalTheme() == kVvsOrder[(at + step) % themes]);
            }
            CHECK(overlay::DecalGlobalTheme() == before_theme);
        }

        // Twenty-pixel cells, and between "ab" and the first plugin a four-pixel rule.
        if (ab != nullptr && tank_cell != nullptr)
        {
            CHECK(std::fabs(ab->GetHeight() - 20.0f) < 0.5f);
            CHECK(std::fabs(tank_cell->Min.y - ab->Max.y - 4.0f) < 0.5f);
        }

        // The bar moves only while left Ctrl is held.
        ImGuiWindow* column_window = ImGui::FindWindowByName("###vvsbar");
        if (column_window != nullptr && ab != nullptr)
        {
            auto drag = [&](ImVec2 from) {
                io.AddMousePosEvent(from.x, from.y);
                Frame(two);
                io.AddMouseButtonEvent(0, true);
                Frame(two);
                for (int step = 1; step <= 3; ++step)
                {
                    io.AddMousePosEvent(from.x + 20.0f * step, from.y + 10.0f * step);
                    Frame(two);
                }
                io.AddMouseButtonEvent(0, false);
                Frame(two);
                io.AddMousePosEvent(1900.0f, 1060.0f);
                Frame(two);
            };
            const ImVec2 still = column_window->Pos;
            drag(tank_cell != nullptr ? tank_cell->GetCenter() : ab->GetCenter());
            CHECK(std::fabs(column_window->Pos.x - still.x) < 0.5f && std::fabs(column_window->Pos.y - still.y) < 0.5f);
            // With left Ctrl held the bar's title strip shows above the cells, so they are found
            // again once it has been laid out.
            overlay::SetDecalReveal(true);
            Frame(two);
            const ImRect* cell = VvsSwitch("Tank");
            const ImVec2 grab = cell != nullptr ? cell->GetCenter() : ImVec2(-1.0f, -1.0f);
            if (cell != nullptr)
                drag(grab);
            overlay::SetDecalReveal(false);
            CHECK(std::fabs(column_window->Pos.x - still.x - 60.0f) < 1.0f);

            // The blue arrow, there only with left Ctrl held, lays the bar across and back.
            overlay::SetDecalReveal(true);
            Frame(two);
            const ImRect* arrow = RectOf(WindowPath("###vvsbar").Str("orientation"));
            CHECK(arrow != nullptr);
            if (arrow != nullptr)
            {
                const ImVec2 turn = arrow->GetCenter();
                CHECK(ClickAt(two, turn).empty());
                const ImRect* theme_across = RectOf(WindowPath("###vvsbar").Str("theme"));
                const ImRect* tank_across = VvsSwitch("Tank");
                const float theme_y = theme_across != nullptr ? theme_across->Min.y : -100.0f;
                const float tank_y = tank_across != nullptr ? tank_across->Min.y : 100.0f;
                const float tank_x = tank_across != nullptr ? tank_across->Min.x : -100.0f;
                const float theme_x = theme_across != nullptr ? theme_across->Min.x : 100.0f;
                CHECK(std::fabs(theme_y - tank_y) < 0.5f && tank_x > theme_x);
                if (const ImRect* back = RectOf(WindowPath("###vvsbar").Str("orientation")))
                    ClickAt(two, back->GetCenter());
            }

            // The coral box opens the bar's window menu, which stays when Ctrl is let go: Set
            // Horizontal lays it across, and Change Theme gives the bar a theme of its own.
            Frame(two);
            const ImRect* box = RectOf(WindowPath("###vvsbar").Str("vvsbar-box"));
            CHECK(box != nullptr);
            if (box != nullptr)
            {
                char popup[32];
                ImFormatString(popup, sizeof(popup), "##Popup_%08x", WindowPath("###vvsbar").Str("vvsbar-menu").id);
                ClickAt(two, box->GetCenter());
                overlay::SetDecalReveal(false);
                Frame(two);
                ImGuiWindow* menu = ImGui::FindWindowByName(popup);
                CHECK(menu != nullptr && menu->Active);
                const ImRect* across_item = RectOf(WindowPath(popup).Str("Set Horizontal"));
                CHECK(across_item != nullptr);
                if (across_item != nullptr)
                {
                    const ImVec2 at = across_item->GetCenter();
                    ClickAt(two, at);
                    Frame(two);
                    const ImRect* theme_across = RectOf(WindowPath("###vvsbar").Str("theme"));
                    const ImRect* tank_across = VvsSwitch("Tank");
                    CHECK(theme_across != nullptr && tank_across != nullptr
                          && std::fabs(theme_across->Min.y - tank_across->Min.y) < 0.5f && tank_across->Min.x > theme_across->Min.x);
                }

                const std::string before_bar = overlay::DecalBarTheme();
                CHECK(before_bar == overlay::DecalGlobalTheme());
                overlay::SetDecalReveal(true);
                Frame(two);
                if (const ImRect* again = RectOf(WindowPath("###vvsbar").Str("vvsbar-box")))
                {
                    ClickAt(two, again->GetCenter());
                    Frame(two);
                    ImGuiWindow* reopened = ImGui::FindWindowByName(popup);
                    CHECK(reopened != nullptr && reopened->Active);
                    if (const ImRect* themes = RectOf(WindowPath(popup).Str("Change Theme")))
                    {
                        const ImVec2 over = themes->GetCenter();
                        io.AddMousePosEvent(over.x, over.y);
                        for (int wait = 0; wait < 4; ++wait)
                            Frame(two);
                        const char* other = before_bar == "Float" ? "Decal" : "Float";
                        ImGuiWindow* submenu = ImGui::FindWindowByName("###Menu_00");
                        CHECK(submenu != nullptr && submenu->Active);
                        const ImRect* pick = RectOf(WindowPath("###Menu_00").Str(other));
                        CHECK(pick != nullptr);
                        if (pick != nullptr)
                        {
                            // Across into the submenu along its row, as a hand would.
                            const ImVec2 choose = pick->GetCenter();
                            io.AddMousePosEvent(choose.x, choose.y);
                            Frame(two);
                            io.AddMouseButtonEvent(0, true);
                            Frame(two);
                            io.AddMouseButtonEvent(0, false);
                            Frame(two);
                            io.AddMousePosEvent(1900.0f, 1060.0f);
                            Frame(two);
                            CHECK(overlay::DecalBarTheme() == other);
                            CHECK(overlay::DecalGlobalTheme() == before_bar);
                            CHECK(std::string(ImGui::SaveIniSettingsToMemory()).find("[VVSView][VirindiViewService:VVS Bar]") != std::string::npos);
                        }
                    }
                    else
                    {
                        CHECK(false);
                    }
                }

                // Back as it was: down the side, in VVS's primary theme.
                Frame(two);
                if (const ImRect* back = RectOf(WindowPath("###vvsbar").Str("orientation")))
                    ClickAt(two, back->GetCenter());
                overlay::SetDecalReveal(false);
                Frame(two);
            }
            overlay::SetDecalReveal(false);
            Frame(two);
        }

        // Groups in VVS's order, lowest first, whatever order the host lists them in.
        {
            PluginWindow huds;
            huds.owner = "Huds";
            huds.has_view = true;
            huds.view.title = "Virindi UIs";
            huds.view.bar = "vvs";
            huds.view.has_bar_order = true;
            huds.view.bar_order = -456600296;
            huds.view.width = 180;
            huds.view.height = 200;
            huds.view.root.type = overlay::ViewControlType::Fixed;
            two.windows[0].view.has_bar_order = true;
            two.windows[0].view.bar_order = 204573080;
            two.windows.push_back(huds);
            two.revision += 1;
            Frame(two);
            Frame(two);
            const ImRect* huds_cell = VvsSwitch("Huds");
            const float huds_y = huds_cell != nullptr ? huds_cell->Min.y : -1.0f;
            const ImRect* tank_again = VvsSwitch("Tank");
            const float tank_y = tank_again != nullptr ? tank_again->Min.y : -1.0f;
            CHECK(huds_y >= 0.0f && tank_y >= 0.0f && huds_y < tank_y);
        }

        std::printf("16. a VVS view's switch is on VVS's bar down the side; the rest on Decal's; a plugin's request reopens it\n");
        std::printf("   VVS's bar: ab first steps the theme, 20-pixel cells, a rule between plugins, moved only with left Ctrl\n");
    }

    // 17. VVS's Float theme and hudified windows: only the body shows until left Ctrl is
    // held; the pin and the click-through arrow on the title bar; the choices kept in the ini.
    {
        State hud;
        hud.revision = 12;
        hud.published_ms = NowMs();

        PluginWindow remote;
        remote.owner = "Remote";
        remote.has_view = true;
        remote.view.title = "VT MiniRemote";
        remote.view.bar = "vvs";
        remote.view.theme = "Float";
        remote.view.ghosted = true;
        remote.view.resizeable = false;
        remote.view.width = 78;
        remote.view.height = 96;
        remote.view.root.type = overlay::ViewControlType::Fixed;
        overlay::ViewControl on;
        on.type = overlay::ViewControlType::Checkbox;
        on.name = "chkOn";
        on.text = "On";
        on.x = 2;
        on.y = 2;
        on.w = 60;
        on.h = 16;
        remote.view.root.children.push_back(on);
        hud.windows.push_back(remote);

        overlay::SetDecalReveal(false);
        Frame(hud);
        Frame(hud);
        const bool opened = IsOpenWindow("###decal:Remote");
        if (!opened)
        {
            if (const ImRect* at = VvsSwitch("Remote")) ClickAt(hud, at->GetCenter());
        }
        Frame(hud);
        CHECK(IsOpenWindow("###decal:Remote"));

        // A hudified window sits behind the others, as in VVS, so it is moved clear of the
        // host's window from the earlier scenarios, which would otherwise take its clicks.
        ImGui::SetWindowPos("###decal:Remote", ImVec2(1400.0f, 600.0f));
        Frame(hud);

        overlay::DecalWindowLook look = overlay::DescribeDecalWindow("Remote", remote.view);
        CHECK(look.theme == "Float" && look.ghosted && !look.click_through);

        const IdPath window = WindowPath("###decal:Remote");
        const IdPath body = window.Str("Remote");

        // Hudified: no title bar, no buttons, and the body's controls still work.
        CHECK(RectOf(window.Str("close")) == nullptr);
        CHECK(RectOf(window.Str("title")) == nullptr);
        if (const ImRect* box = RectOf(body.Int(0).Str("chkOn").Str("check")))
            ExpectOne(ClickAt(hud, box->GetCenter()), "set", "chkOn", "true", "Remote");
        else
            CHECK(!"checkbox not found on a hudified window");

        // Left Ctrl shows the frame: Float's close button, 14 pixels, one in from the 5-pixel
        // frame at the top right; then the pin, and the arrow that makes it click-through.
        overlay::SetDecalReveal(true);
        Frame(hud);
        ImGuiWindow* win = ImGui::FindWindowByName("###decal:Remote");
        const ImRect* close = RectOf(window.Str("close"));
        CHECK(close != nullptr && win != nullptr);
        if (close != nullptr && win != nullptr)
        {
            CHECK(std::fabs(close->Max.x - (win->Pos.x + win->Size.x - 6.0f)) < 0.5f);
            CHECK(std::fabs(close->Min.y - (win->Pos.y + 6.0f)) < 0.5f);
            CHECK(std::fabs(close->GetWidth() - 14.0f) < 0.5f);
        }
        const ImRect* pin = RectOf(window.Str("ghost"));
        const ImRect* arrow = RectOf(window.Str("click-through"));
        CHECK(pin != nullptr && arrow != nullptr);
        CHECK(RectOf(window.Str("alpha-up")) == nullptr);  // Float has no alpha buttons
        if (pin != nullptr && close != nullptr)
            CHECK(std::fabs(pin->Max.x - (close->Min.x - 2.0f)) < 0.5f);  // ButtonSpacing 2

        // Click-through: once Ctrl is let go, the window takes no clicks at all.
        if (arrow != nullptr)
        {
            CHECK(ClickAt(hud, arrow->GetCenter()).empty());
            CHECK(overlay::DescribeDecalWindow("Remote", remote.view).click_through);
        }
        overlay::SetDecalReveal(false);
        Frame(hud);
        if (const ImRect* box = RectOf(body.Int(0).Str("chkOn").Str("check")))
            CHECK(ClickAt(hud, box->GetCenter()).empty());

        // Unpinned from the title bar, the frame stays after Ctrl is let go.
        overlay::SetDecalReveal(true);
        Frame(hud);
        if (const ImRect* again = RectOf(window.Str("ghost")))
            CHECK(ClickAt(hud, again->GetCenter()).empty());
        overlay::SetDecalReveal(false);
        Frame(hud);
        look = overlay::DescribeDecalWindow("Remote", remote.view);
        CHECK(!look.ghosted && !look.click_through);
        CHECK(RectOf(window.Str("close")) != nullptr);

        // Pinning it again says, the first time only, how to get at a hudified window - three
        // lines for the game's chat, which the host writes.
        overlay::SetDecalReveal(true);
        Frame(hud);
        std::vector<Command> said;
        if (const ImRect* pin_again = RectOf(window.Str("ghost")))
            said = ClickAt(hud, pin_again->GetCenter());
        CHECK(said.size() == 3 && said[1].name == "say" && said[1].owner.empty() &&
              said[1].value.find("has been hudified") != std::string::npos);
        Frame(hud);
        std::vector<Command> again;
        if (const ImRect* pin_off = RectOf(window.Str("ghost")))
            ClickAt(hud, pin_off->GetCenter());
        Frame(hud);
        if (const ImRect* pin_on = RectOf(window.Str("ghost")))
            again = ClickAt(hud, pin_on->GetCenter());
        CHECK(again.empty());
        // And unpinned, as the rest of this expects.
        Frame(hud);
        if (const ImRect* pin_last = RectOf(window.Str("ghost")))
            ClickAt(hud, pin_last->GetCenter());
        overlay::SetDecalReveal(false);
        Frame(hud);
        CHECK(!overlay::DescribeDecalWindow("Remote", remote.view).ghosted);

        // A hudified window dragged against the right edge stays on screen and stuck there.
        {
            PluginWindow stuck_window = remote;
            stuck_window.owner = "Sticky";
            stuck_window.view.ghosted = true;
            State sticky = hud;
            sticky.windows = {stuck_window};
            sticky.revision += 1;
            Frame(sticky);
            ImGui::SetWindowPos("###decal:Sticky", ImVec2(1500.0f, 500.0f));
            Frame(sticky);
            overlay::SetDecalReveal(true);
            Frame(sticky);
            const IdPath sticky_window = WindowPath("###decal:Sticky");
            if (const ImRect* title = RectOf(sticky_window.Str("title")))
            {
                // Clear of the icon, which opens the menu.
                const ImVec2 from(title->Max.x - 4.0f, title->GetCenter().y);
                io.AddMousePosEvent(from.x, from.y);
                Frame(sticky);
                io.AddMouseButtonEvent(0, true);
                Frame(sticky);
                for (int step = 1; step <= 4; ++step)
                {
                    io.AddMousePosEvent(from.x + 300.0f * step, from.y);
                    Frame(sticky);
                }
                io.AddMouseButtonEvent(0, false);
                Frame(sticky);
            }
            overlay::SetDecalReveal(false);
            io.AddMousePosEvent(1900.0f, 1060.0f);
            Frame(sticky);
            ImGuiWindow* placed = ImGui::FindWindowByName("###decal:Sticky");
            // Its body - inside the invisible frame - ends at the screen's right edge.
            CHECK(placed != nullptr && std::fabs(placed->Pos.x + placed->Size.x - 5.0f - 1920.0f) < 1.0f);
            CHECK(std::string(ImGui::SaveIniSettingsToMemory()).find("Stuck=R") != std::string::npos);
        }

        // What the player chose is written to the ini, and read back from it.
        const std::string ini = ImGui::SaveIniSettingsToMemory();
        CHECK(ini.find("[VVSView][Remote]") != std::string::npos);
        CHECK(ini.find("Ghost=0") != std::string::npos);
        CHECK(ini.find("ClickThrough=1") != std::string::npos);
        const char* saved = "[VVSView][Remote]\nTheme=Decal\nGhost=1\nAlpha=140\n";
        ImGui::LoadIniSettingsFromMemory(saved);
        look = overlay::DescribeDecalWindow("Remote", remote.view);
        CHECK(look.theme == "Decal" && look.ghosted && look.alpha == 140);

        // The Decal theme's buttons, by VVS's own arithmetic: flush with the frame, top right.
        overlay::SetDecalReveal(true);
        Frame(hud);
        overlay::SetDecalReveal(false);
        win = ImGui::FindWindowByName("###decal:Remote");
        close = RectOf(window.Str("close"));
        CHECK(close != nullptr);
        if (close != nullptr && win != nullptr)
        {
            // Not resizeable: the Decal theme gives such a view no frame at all.
            CHECK(std::fabs(close->Max.x - (win->Pos.x + win->Size.x)) < 0.5f);
            CHECK(std::fabs(close->Min.y - win->Pos.y) < 0.5f);
            CHECK(RectOf(window.Str("alpha-down")) != nullptr);
        }

        std::printf("17. Float and hudified windows: the frame only with left Ctrl, pin, click-through, kept in the ini\n");

        // A HUD as Virindi HUDs made one: no switch on either bar, no close or alpha buttons,
        // a plain colour for its icon, and buttons of its own on the title bar.
        PluginWindow comps;
        comps.owner = "Tank/comps";
        comps.has_view = true;
        comps.view.title = "Comps HUD";
        comps.view.bar = "vvs";
        comps.view.theme = "Decal";
        comps.view.icon = "color:FFFF7F50";
        comps.view.show_in_bar = false;
        comps.view.minimizable = false;
        comps.view.alpha_changeable = false;
        comps.view.width = 62;
        comps.view.height = 60;
        comps.view.root.type = overlay::ViewControlType::Fixed;
        overlay::TitleButton plus{"add", "portal:060029AB", "", "Add the selected item"};
        overlay::TitleButton minus{"remove", "portal:060029AB", "", "Remove one"};
        comps.view.title_buttons = {minus, plus};
        hud.windows.push_back(comps);
        hud.revision = 13;

        Frame(hud);
        Frame(hud);
        CHECK(IsOpenWindow("###decal:Tank/comps"));
        CHECK(VvsSwitch("Tank/comps") == nullptr && BarSwitch("Tank/comps") == nullptr);
        CHECK(VvsSwitch("Remote") != nullptr);

        const IdPath hud_window = WindowPath("###decal:Tank/comps");
        CHECK(RectOf(hud_window.Str("close")) == nullptr);
        CHECK(RectOf(hud_window.Str("alpha-up")) == nullptr);
        const ImRect* add = RectOf(hud_window.Str("add").Str("own"));
        const ImRect* remove = RectOf(hud_window.Str("remove").Str("own"));
        CHECK(add != nullptr && remove != nullptr);
        if (add != nullptr && remove != nullptr)
        {
            // The first listed sits nearest the window's own buttons, as VVS's z-order put it.
            CHECK(remove->Min.x > add->Min.x);
            ImGui::SetWindowPos("###decal:Tank/comps", ImVec2(1200.0f, 300.0f));
            Frame(hud);
            add = RectOf(hud_window.Str("add").Str("own"));
            if (add != nullptr)
                ExpectOne(ClickAt(hud, add->GetCenter()), "press", "add", "", "Tank/comps");
        }

        std::printf("   a HUD: off the bars, no close or alpha buttons, its own title-bar buttons press\n");
    }

    // 18. VVS's Minimalist themes: each picked from a window's own menu and laying the window
    // out by its own numbers; the glyphs redrawn as VVS redrew them; and the VVS bar's H.S.
    // button, which sets the hot-dog stand no menu offers.
    {
        // The glyphs first: VVS's b.a turned a glyph's opaque black one colour, every pixel
        // touching it - across or diagonally - another, and the rest clear.
        {
            overlay::ImagePixels glyph;
            glyph.key = "vvs:Selftest.glyph.png";
            glyph.width = 5;
            glyph.height = 5;
            glyph.rgba.assign(5 * 5 * 4, 0xFF);   // opaque white
            auto black = [&](int x, int y) {
                unsigned char* p = glyph.rgba.data() + (y * 5 + x) * 4;
                p[0] = p[1] = p[2] = 0;
            };
            black(2, 2);
            overlay::AcceptImages({glyph});
            const ImU32 red = IM_COL32(0xFF, 0, 0, 0xFF);
            const ImU32 blue = IM_COL32(0, 0, 0xFF, 0xFF);
            auto pixel = [](const overlay::Texture* tex, int x, int y) -> ImU32 {
                const unsigned char* p = static_cast<const unsigned char*>(tex->ref._TexData->GetPixels()) + (y * 5 + x) * 4;
                return IM_COL32(p[0], p[1], p[2], p[3]);
            };
            const overlay::Texture* made = overlay::FindOutlinedTexture(glyph.key, red, blue);
            CHECK(made != nullptr && made->width == 5.0f && made->height == 5.0f);
            if (made != nullptr)
            {
                CHECK(pixel(made, 2, 2) == red);
                CHECK(pixel(made, 1, 1) == blue && pixel(made, 3, 2) == blue && pixel(made, 2, 3) == blue);
                CHECK(pixel(made, 0, 0) == IM_COL32(0, 0, 0, 0) && pixel(made, 4, 2) == IM_COL32(0, 0, 0, 0));
            }

            // The host's image replaced, the redrawn one is made again from the new one.
            black(0, 0);
            overlay::AcceptImages({glyph});
            made = overlay::FindOutlinedTexture(glyph.key, red, blue);
            CHECK(made != nullptr && pixel(made, 0, 0) == red && pixel(made, 1, 0) == blue);
        }

        State mini;
        mini.revision = 30;
        mini.published_ms = NowMs();

        PluginWindow plain;
        plain.owner = "Plain";
        plain.has_view = true;
        plain.view.title = "Minimal";
        plain.view.bar = "vvs";
        plain.view.resizeable = false;
        plain.view.width = 200;
        plain.view.height = 140;
        plain.view.root.type = overlay::ViewControlType::Fixed;

        overlay::ViewControl box;
        box.type = overlay::ViewControlType::Checkbox;
        box.name = "chkBox";
        box.text = "Box";
        box.x = 4;
        box.y = 6;
        box.w = 80;
        box.h = 16;
        plain.view.root.children.push_back(box);

        // A notebook whose first page holds a list long enough to scroll.
        overlay::ViewControl list;
        list.type = overlay::ViewControlType::List;
        list.name = "lst";
        list.x = 2;
        list.y = 2;
        list.w = 150;
        list.h = 60;
        list.columns = {overlay::ViewColumn{overlay::ViewColumnType::Check, 16}, overlay::ViewColumn{}};
        for (int i = 0; i < 20; ++i)
        {
            overlay::ViewRow row;
            row.cells = {overlay::ViewCell{"", i % 2 == 0}, overlay::ViewCell{"Row " + std::to_string(i)}};
            list.rows.push_back(row);
        }
        overlay::ViewControl page;
        page.type = overlay::ViewControlType::Fixed;
        page.children.push_back(list);
        overlay::ViewControl notebook;
        notebook.type = overlay::ViewControlType::Notebook;
        notebook.name = "nb";
        notebook.x = 0;
        notebook.y = 30;
        notebook.w = 200;
        notebook.h = 110;
        notebook.pages = {overlay::ViewPage{"Items", {page}}, overlay::ViewPage{"Other", {}}};
        plain.view.root.children.push_back(notebook);

        overlay::ViewControl push;
        push.type = overlay::ViewControlType::PushButton;
        push.name = "btnGo";
        push.text = "Go";
        push.x = 100;
        push.y = 4;
        push.w = 40;
        push.h = 20;
        plain.view.root.children.push_back(push);
        overlay::ViewControl edit;
        edit.type = overlay::ViewControlType::Edit;
        edit.name = "txtName";
        edit.value = "Bob";
        edit.x = 144;
        edit.y = 4;
        edit.w = 50;
        edit.h = 20;
        plain.view.root.children.push_back(edit);
        mini.windows.push_back(plain);

        overlay::SetDecalReveal(false);
        Frame(mini);
        Frame(mini);
        if (!IsOpenWindow("###decal:Plain"))
        {
            if (const ImRect* at = VvsSwitch("Plain")) ClickAt(mini, at->GetCenter());
        }
        Frame(mini);
        CHECK(IsOpenWindow("###decal:Plain"));
        ImGui::SetWindowPos("###decal:Plain", ImVec2(700.0f, 300.0f));
        Frame(mini);

        const IdPath window = WindowPath("###decal:Plain");
        const IdPath body = window.Str("Plain").Int(0);
        char popup[32];
        ImFormatString(popup, sizeof(popup), "##Popup_%08x", window.Str("window-menu").id);

        // From the window's icon: Change Theme, then a theme - or (Reset Theme) - as a hand would.
        auto pick = [&](const char* name) -> bool {
            const ImRect* icon = RectOf(window.Str("icon"));
            if (icon == nullptr)
                return false;
            ClickAt(mini, icon->GetCenter());
            const ImRect* themes = RectOf(WindowPath(popup).Str("Change Theme"));
            if (themes == nullptr)
                return false;
            const ImVec2 over = themes->GetCenter();
            io.AddMousePosEvent(over.x, over.y);
            for (int wait = 0; wait < 4; ++wait)
                Frame(mini);
            const ImRect* item = RectOf(WindowPath("###Menu_00").Str(name));
            if (item == nullptr)
                return false;
            const ImVec2 choose = item->GetCenter();
            io.AddMousePosEvent(choose.x, choose.y);
            Frame(mini);
            io.AddMouseButtonEvent(0, true);
            Frame(mini);
            io.AddMouseButtonEvent(0, false);
            Frame(mini);
            io.AddMousePosEvent(1900.0f, 1060.0f);
            Frame(mini);
            Frame(mini);
            return true;
        };

        // What each Minimalist theme sets: its border round a view the player cannot resize,
        // its title bar, its buttons, its tabs and its scroll bar.
        struct Metrics {
            const char* name;
            float border, title_bar, button, tab, scroll;
        };
        const Metrics minimalists[] = {
            {"Minimalist", 1.0f, 12.0f, 10.0f, 13.0f, 12.0f},
            {"Minimalist Green", 1.0f, 11.0f, 9.0f, 13.0f, 12.0f},
            {"Minimalist Black", 1.0f, 12.0f, 10.0f, 13.0f, 12.0f},
            {"Minimalist Transparent", 1.0f, 12.0f, 10.0f, 13.0f, 12.0f},
        };
        auto expect_layout = [&](const Metrics& m) {
            ImGuiWindow* win = ImGui::FindWindowByName("###decal:Plain");
            CHECK(win != nullptr);
            if (win == nullptr)
                return;
            CHECK(std::fabs(win->Size.x - (200.0f + 2.0f * m.border)) < 0.5f);
            CHECK(std::fabs(win->Size.y - (140.0f + m.title_bar + 2.0f * m.border)) < 0.5f);

            // The title bar, the border in and ViewTitleBar_Size high.
            const ImRect* title = RectOf(window.Str("title"));
            CHECK(title != nullptr && std::fabs(title->Min.y - (win->Pos.y + m.border)) < 0.5f &&
                  std::fabs(title->GetHeight() - m.title_bar) < 0.5f);

            // Close ButtonsLeft 1 and ButtonsDown 1 in from the border, ButtonSize square; the
            // alpha buttons after it, ButtonSpacing 1 apart.
            const ImRect* close = RectOf(window.Str("close"));
            const ImRect* more = RectOf(window.Str("alpha-up"));
            CHECK(close != nullptr && more != nullptr);
            if (close != nullptr)
            {
                CHECK(std::fabs(close->Max.x - (win->Pos.x + win->Size.x - 1.0f - m.border)) < 0.5f);
                CHECK(std::fabs(close->Min.y - (win->Pos.y + 1.0f + m.border)) < 0.5f);
                CHECK(std::fabs(close->GetWidth() - m.button) < 0.5f && std::fabs(close->GetHeight() - m.button) < 0.5f);
                if (more != nullptr)
                    CHECK(std::fabs(more->Max.x - (close->Min.x - 1.0f)) < 0.5f);
            }

            // The view starts under the title bar: its checkbox where the view put it.
            const ImRect* check = RectOf(body.Str("chkBox").Str("check"));
            CHECK(check != nullptr && std::fabs(check->Min.y - (win->Pos.y + m.border + m.title_bar + 6.0f)) < 0.5f &&
                  std::fabs(check->Min.x - (win->Pos.x + m.border + 4.0f)) < 0.5f);

            // TabHeight, and a scroll bar VScrollBarButtonSize wide.
            const ImRect* tab = RectOf(body.Str("nb").Int(0).Str("tab"));
            CHECK(tab != nullptr && std::fabs(tab->GetHeight() - m.tab) < 0.5f);
            const ImRect* up = RectOf(body.Str("nb").Int(0).Int(0).Str("lst").Str("up"));
            CHECK(up != nullptr && std::fabs(up->GetWidth() - m.scroll) < 0.5f && std::fabs(up->GetHeight() - m.scroll) < 0.5f);

            // And the view still works in it.
            if (check != nullptr)
            {
                const std::vector<Command> set = ClickAt(mini, check->GetCenter());
                CHECK(set.size() == 1 && set[0].name == "set" && set[0].control_id == "chkBox" && set[0].owner == "Plain");
            }
        };

        for (const Metrics& m : minimalists)
        {
            CHECK(pick(m.name));
            CHECK(overlay::DescribeDecalWindow("Plain", plain.view).theme == m.name);
            // Held down and hovered, the title-bar buttons and tooltips draw in the theme too.
            if (const ImRect* close = RectOf(window.Str("close")))
            {
                io.AddMousePosEvent(close->GetCenter().x, close->GetCenter().y);
                for (int wait = 0; wait < 3; ++wait)
                    Frame(mini);
                io.AddMousePosEvent(1900.0f, 1060.0f);
                Frame(mini);
            }
            expect_layout(m);
        }
        CHECK(std::string(ImGui::SaveIniSettingsToMemory()).find("Theme=Minimalist Transparent") != std::string::npos);

        // The H.S. button, on the VVS bar's title strip while left Ctrl is held, sets VVS's
        // hot-dog stand as its primary theme; a window that keeps no theme of its own follows.
        CHECK(pick("(Reset Theme)"));
        overlay::SetDecalReveal(true);
        Frame(mini);
        const ImRect* hs = RectOf(WindowPath("###vvsbar").Str("hot-dog-stand"));
        CHECK(hs != nullptr);
        if (hs != nullptr)
            CHECK(ClickAt(mini, hs->GetCenter()).empty());
        overlay::SetDecalReveal(false);
        Frame(mini);
        CHECK(overlay::DecalGlobalTheme() == "Minimalist H.S.");
        CHECK(overlay::DescribeDecalWindow("Plain", plain.view).theme == "Minimalist H.S.");
        expect_layout(Metrics{"Minimalist H.S.", 1.0f, 12.0f, 10.0f, 13.0f, 12.0f});

        // No menu offers it: the window's lists the six.
        if (const ImRect* icon = RectOf(window.Str("icon")))
        {
            ClickAt(mini, icon->GetCenter());
            if (const ImRect* themes = RectOf(WindowPath(popup).Str("Change Theme")))
            {
                io.AddMousePosEvent(themes->GetCenter().x, themes->GetCenter().y);
                for (int wait = 0; wait < 4; ++wait)
                    Frame(mini);
                CHECK(RectOf(WindowPath("###Menu_00").Str("Minimalist")) != nullptr);
                CHECK(RectOf(WindowPath("###Menu_00").Str("Minimalist Green")) != nullptr);
                CHECK(RectOf(WindowPath("###Menu_00").Str("Minimalist H.S.")) == nullptr);
            }
            else
            {
                CHECK(false);
            }
            io.AddMousePosEvent(1900.0f, 1060.0f);
            ClickAt(mini, ImVec2(1900.0f, 1060.0f));
        }

        // And "ab" steps on from it as VVS's NextTheme did from a theme it never registered:
        // to the first.
        Frame(mini);
        if (const ImRect* ab = RectOf(WindowPath("###vvsbar").Str("theme")))
        {
            CHECK(ClickAt(mini, ab->GetCenter()).empty());
            CHECK(overlay::DecalGlobalTheme() == "Minimalist");
        }
        else
        {
            CHECK(false);
        }

        std::printf("18. Minimalist, Green, Black and Transparent from a window's menu, each by its own numbers;\n");
        std::printf("   glyphs redrawn as VVS redrew them; the H.S. button's hot-dog stand, and ab on from it\n");
    }

    // 19. What Virindi HUDs' HSM bars, Chat Window and Comps HUD are made of: pictures cut from
    // their art, labels in a face and size of their own that take a click, tooltips, a console
    // whose links click, an edit box the plugin can put the cursor in, and a window the player
    // resizes by its frame from the size vvs.s3db left it at.
    {
        using overlay::ViewControl;
        using overlay::ViewControlType;
        auto control = [](ViewControlType type, const char* name, int x, int y, int w, int h) {
            ViewControl c;
            c.type = type;
            c.name = name;
            c.x = x;
            c.y = y;
            c.w = w;
            c.h = h;
            return c;
        };

        State huds;
        huds.revision = 40;
        huds.published_ms = NowMs();

        PluginWindow hsm;
        hsm.owner = "Huds/hsm";
        hsm.has_view = true;
        hsm.view.title = "HSM Bar";
        hsm.view.bar = "vvs";
        hsm.view.theme = "Float";
        hsm.view.show_in_bar = false;
        hsm.view.resizeable = false;
        hsm.view.width = 365;
        hsm.view.height = 75;
        hsm.view.root.type = ViewControlType::Fixed;
        hsm.view.root.children.push_back(control(ViewControlType::Picture, "bg", 0, 0, 365, 75));
        hsm.view.root.children.back().image = "host:vhuds-ac2hsmbar_bg";
        hsm.view.root.children.push_back(control(ViewControlType::Picture, "bar0", 0, 0, 202, 75));
        hsm.view.root.children.back().image = "host:vhuds-ac2hsmbar_h";
        hsm.view.root.children.back().uv[2] = 202.0f / 365.0f;
        hsm.view.root.children.push_back(control(ViewControlType::Static, "cur0", 96, 15, 200, 14));
        hsm.view.root.children.back().text = "100";
        hsm.view.root.children.back().font = "Verdana";
        hsm.view.root.children.back().font_points = 10.0f;
        hsm.view.root.children.back().bold = true;
        hsm.view.root.children.back().middle = true;
        hsm.view.root.children.back().shadow = true;
        // A Comps HUD row: its picture and its count both take a click and name the item.
        hsm.view.root.children.push_back(control(ViewControlType::Picture, "icon0", 300, 40, 20, 20));
        hsm.view.root.children.back().image = "color:FFFF7F50";
        hsm.view.root.children.back().clickable = true;
        hsm.view.root.children.back().tooltip = "Prismatic Taper";
        hsm.view.root.children.push_back(control(ViewControlType::Static, "count0", 322, 40, 40, 20));
        hsm.view.root.children.back().text = "973";
        hsm.view.root.children.back().clickable = true;
        hsm.view.root.children.back().tooltip = "Prismatic Taper";
        huds.windows.push_back(hsm);

        PluginWindow chat;
        chat.owner = "Huds/chat";
        chat.has_view = true;
        chat.view.title = "Game Chat";
        chat.view.bar = "vvs";
        chat.view.theme = "Float";
        chat.view.show_in_bar = false;
        chat.view.resizeable = true;
        chat.view.width = 500;
        chat.view.height = 175;
        chat.view.has_stored_size = true;
        chat.view.stored_width = 600;
        chat.view.stored_height = 200;
        chat.view.min_width = 100;
        chat.view.min_height = 100;
        chat.view.max_width = 1000;
        chat.view.max_height = 1000;
        chat.view.root.type = ViewControlType::Fixed;
        ViewControl console = control(ViewControlType::Console, "console0", 0, 0, 0, 159);
        for (const char* words : {"You say, \"one\"", "You say, \"two\""})
            console.lines.push_back(overlay::ConsoleLine{{overlay::ConsoleSegment{"12:00:00 ", 10, false}, overlay::ConsoleSegment{words, 9, false}}});
        console.lines.push_back(overlay::ConsoleLine{{overlay::ConsoleSegment{"12:00:01 ", 10, false}, overlay::ConsoleSegment{"Bob", 98, true},
                                                      overlay::ConsoleSegment{" tells you, \"hi\"", 5, false}}});
        chat.view.root.children.push_back(console);
        chat.view.root.children.push_back(control(ViewControlType::Edit, "VH_TextInputBox", 0, 159, 0, 16));
        huds.windows.push_back(chat);

        // Decal's ViewAlpha: a Decal view starts at it, a VVS view opaque.
        PluginWindow faded;
        faded.owner = "Faded";
        faded.has_view = true;
        faded.view.title = "Faded";
        faded.view.width = 120;
        faded.view.height = 60;
        faded.view.root.type = ViewControlType::Fixed;
        huds.windows.push_back(faded);
        huds.decal_bar.known = true;
        huds.decal_bar.alpha = 128;
        huds.decal_bar.view_alpha = 140;

        overlay::SetDecalReveal(false);
        std::vector<Command> first = Frame(huds);
        Append(first, Frame(huds));
        for (const char* owner : {"Huds/hsm", "Huds/chat", "Faded"})
        {
            if (!IsOpenWindow((std::string("###decal:") + owner).c_str()))
                CHECK(!"a HUD's window did not open");
        }

        // The chat window opens at the size vvs.s3db left it at, and its plugin is told, once.
        int resizes = 0;
        for (const Command& c : first)
        {
            if (c.name == "resize")
            {
                ++resizes;
                CHECK(c.owner == "Huds/chat" && c.value == "600,200");
            }
        }
        CHECK(resizes == 1);
        CHECK(Frame(huds).empty());
        CHECK(overlay::DescribeDecalWindow("Huds/chat", chat.view).width == 600);
        CHECK(overlay::DescribeDecalWindow("Faded", faded.view).alpha == 140);
        CHECK(overlay::DescribeDecalWindow("Huds/hsm", hsm.view).alpha == 255);

        ImGui::SetWindowPos("###decal:Huds/hsm", ImVec2(300.0f, 700.0f));
        ImGui::SetWindowPos("###decal:Huds/chat", ImVec2(800.0f, 300.0f));
        ImGui::SetWindowPos("###decal:Faded", ImVec2(1600.0f, 900.0f));
        Frame(huds);
        Frame(huds);

        const IdPath hsm_body = WindowPath("###decal:Huds/hsm").Str("Huds/hsm").Int(0);
        // A picture nothing listens to takes no click; one something does, and a label too.
        CHECK(RectOf(hsm_body.Str("bg").Str("picture")) == nullptr);
        CHECK(RectOf(hsm_body.Str("cur0").Str("label")) == nullptr);
        if (const ImRect* icon = RectOf(hsm_body.Str("icon0").Str("picture")))
            ExpectOne(ClickAt(huds, icon->GetCenter()), "press", "icon0", "", "Huds/hsm");
        else
            CHECK(!"the clickable picture has no item");
        if (const ImRect* count = RectOf(hsm_body.Str("count0").Str("label")))
        {
            // The rect goes with the frame it was drawn in, so its middle is kept.
            const ImVec2 middle = count->GetCenter();
            ExpectOne(ClickAt(huds, middle), "press", "count0", "", "Huds/hsm");

            // Resting on it shows its tooltip.
            io.AddMousePosEvent(middle.x, middle.y);
            for (int wait = 0; wait < 3; ++wait)
                Frame(huds);
            ImGuiWindow* tip = ImGui::FindWindowByName("##Tooltip_00");
            CHECK(tip != nullptr && tip->Active);
            io.AddMousePosEvent(1900.0f, 1060.0f);
            Frame(huds);
        }
        else
        {
            CHECK(!"the clickable label has no item");
        }

        // The console: the newest line at the foot, its player's name a link that says which.
        const IdPath chat_window = WindowPath("###decal:Huds/chat");
        const IdPath chat_body = chat_window.Str("Huds/chat").Int(0);
        ImGuiWindow* chat_win = ImGui::FindWindowByName("###decal:Huds/chat");
        if (const ImRect* link = RectOf(chat_body.Str("console0").Int(2).Int(1).Str("link")))
        {
            const float body_top = chat_win != nullptr ? chat_win->Pos.y + 5.0f + 19.0f : 0.0f;
            CHECK(std::fabs(link->Max.y - (body_top + 159.0f - 2.0f)) < 1.5f);
            const std::vector<Command> clicked = ClickAt(huds, link->GetCenter());
            CHECK(clicked.size() == 1 && clicked[0].name == "click" && clicked[0].control_id == "console0" &&
                  clicked[0].value == "1" && clicked[0].row_id == "2" && clicked[0].owner == "Huds/chat");
        }
        else
        {
            CHECK(!"the console's link has no item");
        }

        // The plugin asks for the keyboard: the cursor goes into the box, and Enter sends the line.
        const IdPath input = chat_body.Str("VH_TextInputBox").Str("##edit");
        huds.windows[1].view.root.children[1].focus_request = 1;
        Frame(huds);
        Frame(huds);
        CHECK(ImGui::GetActiveID() == input.id);
        for (char ch : std::string("hello"))
            io.AddInputCharacter(static_cast<unsigned int>(ch));
        std::vector<Command> sent = Frame(huds);
        io.AddKeyEvent(ImGuiKey_Enter, true);
        Append(sent, Frame(huds));
        io.AddKeyEvent(ImGuiKey_Enter, false);
        Append(sent, Frame(huds));
        ExpectOne(sent, "enter", "VH_TextInputBox", "hello", "Huds/chat");

        // Its frame resizes it: the corner by the pointer, within the least and the most, the
        // plugin told, and the size kept.
        auto drag = [&](const IdPath& grip, ImVec2 by) {
            std::vector<Command> out;
            const ImRect* at = RectOf(grip);
            if (at == nullptr)
                return out;
            const ImVec2 from = at->GetCenter();
            io.AddMousePosEvent(from.x, from.y);
            Append(out, Frame(huds));
            io.AddMouseButtonEvent(0, true);
            Append(out, Frame(huds));
            for (int step = 1; step <= 4; ++step)
            {
                io.AddMousePosEvent(from.x + by.x * static_cast<float>(step) / 4.0f, from.y + by.y * static_cast<float>(step) / 4.0f);
                Append(out, Frame(huds));
            }
            io.AddMouseButtonEvent(0, false);
            Append(out, Frame(huds));
            io.AddMousePosEvent(1900.0f, 1060.0f);
            Append(out, Frame(huds));
            Append(out, Frame(huds));
            return out;
        };
        auto last_resize = [](const std::vector<Command>& commands) {
            std::string value;
            for (const Command& c : commands)
                if (c.name == "resize" && c.owner == "Huds/chat") value = c.value;
            return value;
        };
        std::vector<Command> grown = drag(chat_window.Str("size-bottom-right"), ImVec2(50.0f, 30.0f));
        CHECK(last_resize(grown) == "650,230");
        CHECK(std::string(ImGui::SaveIniSettingsToMemory()).find("Size=650,230") != std::string::npos);
        chat_win = ImGui::FindWindowByName("###decal:Huds/chat");
        CHECK(chat_win != nullptr && std::fabs(chat_win->Size.x - (650.0f + 10.0f)) < 0.5f);

        // From the left edge the window's right side stays where it was.
        const float right = chat_win != nullptr ? chat_win->Pos.x + chat_win->Size.x : 0.0f;
        std::vector<Command> wider = drag(chat_window.Str("size-left"), ImVec2(-40.0f, 0.0f));
        CHECK(last_resize(wider) == "690,230");
        chat_win = ImGui::FindWindowByName("###decal:Huds/chat");
        CHECK(chat_win != nullptr && std::fabs(chat_win->Pos.x + chat_win->Size.x - right) < 1.0f);

        std::vector<Command> shrunk = drag(chat_window.Str("size-bottom-right"), ImVec2(-1500.0f, -1500.0f));
        CHECK(last_resize(shrunk) == "100,100");

        // A hudified window vvs.s3db left against the right edge is put back against it.
        PluginWindow stuck = hsm;
        stuck.owner = "Huds/stuck";
        stuck.view.ghosted = true;
        stuck.view.stuck = "R";
        stuck.view.has_position = true;
        stuck.view.x = 200;
        stuck.view.y = 200;
        huds.windows.push_back(stuck);
        Frame(huds);
        Frame(huds);
        ImGuiWindow* placed = ImGui::FindWindowByName("###decal:Huds/stuck");
        CHECK(placed != nullptr && std::fabs(placed->Pos.x + placed->Size.x - 5.0f - 1920.0f) < 1.0f);
        CHECK(overlay::DescribeDecalWindow("Huds/stuck", stuck.view).stuck == "R");

        // VVS's bar - there while a VVS view is on it - is kept on screen as a hudified view is.
        PluginWindow listed;
        listed.owner = "Huds/uis";
        listed.starts_closed = true;
        listed.has_view = true;
        listed.view.title = "Virindi UIs";
        listed.view.bar = "vvs";
        listed.view.width = 180;
        listed.view.height = 200;
        listed.view.root.type = ViewControlType::Fixed;
        huds.windows.push_back(listed);
        Frame(huds);
        ImGui::SetWindowPos("###vvsbar", ImVec2(-300.0f, 500.0f));
        Frame(huds);
        Frame(huds);
        ImGuiWindow* vvsbar = ImGui::FindWindowByName("###vvsbar");
        CHECK(vvsbar != nullptr && vvsbar->Active && std::fabs(vvsbar->Pos.x) < 0.5f);

        // 20. Decal's bar at the player's 114 pixels, laid out by Inject.dll's own rectangles: the
        // grips' lines from 3 and to 4 short of the end, the gold square at (12, 3) and the grey
        // 28 short, the switches 20 by 21 from 30 - or from 50 between the arrows once they take
        // more than 58 short - and the same down a side; on an expanded side the squares and the
        // arrows on its top row.
        {
            State player = Rich();
            player.revision = 50;
            player.decal_bar.known = true;
            player.decal_bar.state = 1;
            player.decal_bar.start = 4;
            player.decal_bar.length = 114;
            player.decal_bar.alpha = 255;
            Frame(player);
            Frame(player);

            auto at = [&](const char* window, const char* item, ImVec2 where, ImVec2 size) {
                ImGuiWindow* bar_window = ImGui::FindWindowByName(window);
                const ImRect* rect = RectOf(WindowPath(window).Str(item));
                if (bar_window == nullptr || rect == nullptr)
                {
                    std::printf("FAIL: no %s on %s\n", item, window);
                    ++g_failures;
                    return;
                }
                const ImVec2 min(rect->Min.x - bar_window->Pos.x, rect->Min.y - bar_window->Pos.y);
                const bool right = std::fabs(min.x - where.x) < 0.5f && std::fabs(min.y - where.y) < 0.5f &&
                                   std::fabs(rect->GetWidth() - size.x) < 0.5f && std::fabs(rect->GetHeight() - size.y) < 0.5f;
                CHECK(right);
                if (!right)
                    std::printf("   %s on %s at %.0f,%.0f %.0fx%.0f\n", item, window, min.x, min.y, rect->GetWidth(), rect->GetHeight());
            };
            auto first_switch = [&](const char* window) -> std::string {
                for (const PluginWindow& w : player.windows)
                    if (RectOf(WindowPath(window).Str(("owner:" + w.owner).c_str())) != nullptr)
                        return "owner:" + w.owner;
                return std::string();
            };

            ImGuiWindow* top = ImGui::FindWindowByName("###decalbar");
            CHECK(top != nullptr && std::fabs(top->Size.x - 114.0f) < 0.5f && std::fabs(top->Size.y - 23.0f) < 0.5f);
            at("###decalbar", "grip-start", ImVec2(2, 3), ImVec2(7, 17));
            at("###decalbar", "grip-end", ImVec2(105, 3), ImVec2(7, 17));
            at("###decalbar", "minmax", ImVec2(12, 3), ImVec2(16, 16));
            at("###decalbar", "dock", ImVec2(86, 3), ImVec2(16, 16));
            // Four switches take 88 pixels, more than the 56 the bar has: paged.
            at("###decalbar", "page-back", ImVec2(30, 3), ImVec2(16, 16));
            at("###decalbar", "page-on", ImVec2(68, 3), ImVec2(16, 16));
            const std::string shown = first_switch("###decalbar");
            CHECK(!shown.empty());
            if (!shown.empty())
                at("###decalbar", shown.c_str(), ImVec2(50, 1), ImVec2(20, 21));

            // Down the left, compact.
            if (const ImRect* dock = RectOf(WindowPath("###decalbar").Str("dock")))
                ClickAt(player, dock->GetCenter());
            Frame(player);
            at("###decalbar-left", "grip-start", ImVec2(3, 2), ImVec2(14, 7));
            at("###decalbar-left", "minmax", ImVec2(3, 12), ImVec2(16, 16));
            at("###decalbar-left", "dock", ImVec2(3, 86), ImVec2(16, 16));
            at("###decalbar-left", "page-back", ImVec2(3, 30), ImVec2(16, 16));
            at("###decalbar-left", "page-on", ImVec2(3, 68), ImVec2(16, 16));
            const std::string side = first_switch("###decalbar-left");
            if (!side.empty())
                at("###decalbar-left", side.c_str(), ImVec2(1, 50), ImVec2(18, 20));
            else
                CHECK(!"no switch down the side");

            // Expanded down the left: the gold square on the left of its top row, the grey on
            // the right, the arrows either side of the middle; the switches from 30.
            if (const ImRect* minmax = RectOf(WindowPath("###decalbar-left").Str("minmax")))
                ClickAt(player, minmax->GetCenter());
            Frame(player);
            ImGuiWindow* wide = ImGui::FindWindowByName("###decalbar-left");
            CHECK(wide != nullptr && std::fabs(wide->Size.x - 100.0f) < 0.5f);
            at("###decalbar-left", "minmax", ImVec2(3, 12), ImVec2(16, 16));
            at("###decalbar-left", "dock", ImVec2(81, 12), ImVec2(16, 16));
            at("###decalbar-left", "page-back", ImVec2(33, 12), ImVec2(16, 16));
            at("###decalbar-left", "page-on", ImVec2(51, 12), ImVec2(16, 16));
            at("###decalbar-left", "grip-start", ImVec2(3, 2), ImVec2(94, 7));
            const std::string labelled = first_switch("###decalbar-left");
            if (!labelled.empty())
                at("###decalbar-left", labelled.c_str(), ImVec2(1, 30), ImVec2(98, 20));
            else
                CHECK(!"no labelled switch down the side");

            // Back along the top, compact, as the rest found it.
            if (const ImRect* minmax = RectOf(WindowPath("###decalbar-left").Str("minmax")))
                ClickAt(player, minmax->GetCenter());
            for (const char* window : {"###decalbar-left", "###decalbar-right"})
                if (const ImRect* dock = RectOf(WindowPath(window).Str("dock")))
                    ClickAt(player, dock->GetCenter());
            Frame(player);
            CHECK(ImGui::FindWindowByName("###decalbar")->Active);
            std::printf("20. Decal's bar by Inject.dll's own rectangles, along the top, down a side and expanded there\n");
        }

        std::printf("19. pictures cut from their art and labels in a face of their own, clicked and with tooltips;\n");
        std::printf("   a console's link clicked; the cursor put in an edit box and Enter sent; a window resized by its\n");
        std::printf("   frame from vvs.s3db's size, within its least; a hudified window stuck where vvs.s3db left it\n");
    }

    ImGui::DestroyContext();
    std::printf("%d checks, %d failures\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
