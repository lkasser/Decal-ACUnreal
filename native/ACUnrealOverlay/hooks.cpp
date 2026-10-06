// Drawing inside someone else's D3D12 renderer.
//
// The shape of this file is dictated by three facts about D3D12 that do not apply to the
// older APIs people usually write overlays against.
//
// First, you cannot draw from Present alone. D3D11 let you get a device and an immediate
// context from the swap chain and issue draws; D3D12 has no immediate context, and
// submitting work needs the game's own ID3D12CommandQueue. There is no way to ask a swap
// chain for it, so it has to be intercepted: either from ExecuteCommandLists, or - better
// - from ResizeBuffers1, whose ppPresentQueue parameter is authoritative rather than
// inferred.
//
// Second, D3D12 keeps nothing alive for you, and a reference kept is worse. Holding a back
// buffer across frames makes the game's own ResizeBuffers fail, and stops it making a new
// swap chain for its window at all. So the overlay takes the back buffer it draws into for
// that frame alone, and the resize hooks only wait for the GPU before the game's resize -
// everything else the overlay draws with outlives every resize, as the ImGui backend,
// set up once, expects.
//
// Third, the ImGui backend does less than its D3D11 counterpart: it does not set
// descriptor heaps, does not bind a render target, and - having no fence of its own -
// reuses its vertex buffers every frame regardless of whether the GPU has finished with
// them. All three are this file's job, and the last one is why there is a fence here.
//
// Everything specific to this client was established by reading it rather than assumed,
// and is recorded in native/docs/d3d12-overlay-design.md.

#include "hooks.h"

#include <windows.h>

#include <d3d12.h>
#include <dwmapi.h>
#include <dxgi1_4.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <mutex>
#include <thread>
#include <vector>

#include <MinHook.h>

#include "imgui.h"
#include "backends/imgui_impl_dx12.h"
#include "backends/imgui_impl_win32.h"

#include "decal_view.h"
#include "input.h"
#include "log.h"
#include "overlay_ipc.h"
#include "overlay_state.h"
#include "overlay_ui.h"
#include "textures.h"

// Declared by the ImGui win32 backend but deliberately not in its header, so that an
// application which does not want it is not forced to take it.
extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

namespace overlay {
namespace hooks {
namespace {

// Vtable slots. Dumped with cl /d1reportSingleClassLayout on the Windows SDK in use and
// cross-checked against its MIDL-generated vtables, rather than copied from a tutorial -
// see the design note, which also records the recipe so they can be re-derived.
constexpr int kSlotPresent = 8;
constexpr int kSlotResizeBuffers = 13;
constexpr int kSlotPresent1 = 22;
constexpr int kSlotResizeBuffers1 = 39;
constexpr int kSlotExecuteCommandLists = 10;

// Descriptors for ImGui's textures. From 1.92 the backend allocates more than one, so it
// is handed an allocator rather than a single descriptor. Every image the host sends -
// each piece of Decal's theme, each icon in a list - is a texture and takes one, so this
// is sized for a few hundred of those; a descriptor costs 32 bytes.
constexpr UINT kSrvDescriptors = 1024;

using PfnPresent = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using PfnPresent1 = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
using PfnResizeBuffers = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);
using PfnResizeBuffers1 = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain3*, UINT, UINT, UINT, DXGI_FORMAT, UINT, const UINT*, IUnknown* const*);
using PfnExecuteCommandLists = void(STDMETHODCALLTYPE*)(ID3D12CommandQueue*, UINT, ID3D12CommandList* const*);

PfnPresent g_originalPresent = nullptr;
PfnPresent1 g_originalPresent1 = nullptr;
PfnResizeBuffers g_originalResizeBuffers = nullptr;
PfnResizeBuffers1 g_originalResizeBuffers1 = nullptr;
PfnExecuteCommandLists g_originalExecuteCommandLists = nullptr;

std::atomic<bool> g_installed{false};
std::atomic<bool> g_visible{true};

// Counts detours currently executing, so unhooking can wait for them to leave. MinHook
// frees its trampolines on uninitialise, and a thread still inside one then returns into
// freed memory.
std::atomic<int> g_inFlight{0};

// The queue the game presents with. Written by whichever hook sees it first and only
// read after a non-null check, so a plain atomic is the whole of the synchronisation.
std::atomic<ID3D12CommandQueue*> g_queue{nullptr};

std::once_flag g_ipcOnce;
Ipc* g_ipc = nullptr;

// The host's pipe. native/tests/render_test.cpp builds this file with OVERLAY_TEST_PIPE, so the
// overlay it draws can never reach a Decal Agent running for the game.
#ifdef OVERLAY_TEST_PIPE
constexpr wchar_t kPipeName[] = L"achost-overlay-render-test";
#else
constexpr wchar_t kPipeName[] = L"achost-overlay";
#endif

// One of the frames the overlay may have on the GPU at once: its command allocator, its view of
// the back buffer it draws into, and the fence value that says the GPU is done with both. Used
// in turn. The ImGui backend keeps as many vertex buffers and reuses them in the same turn with
// no fence of its own, so the two counts are the same, fixed when the renderer is made - not the
// game's buffer count, which a resize may change.
struct Slot {
    ID3D12CommandAllocator* allocator = nullptr;
    D3D12_CPU_DESCRIPTOR_HANDLE rtv{};
    UINT64 fenceValue = 0;
};

// The game's back buffers as last seen, to tell when they change.
struct Target {
    const void* chain = nullptr;  // which swap chain, compared and never followed
    UINT width = 0;
    UINT height = 0;
    UINT buffers = 0;
    DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
};

// Made once, on the first frame, and kept until the overlay unloads - resizes included. Nothing
// here depends on the game's back buffers: the overlay takes the one it draws into afresh each
// frame and lets it go once the frame is submitted. A reference kept between frames stops the
// game's ResizeBuffers, and stops the game making a new swap chain for its window at all.
//
// Remaking any of it on a resize is how it went wrong before: the texture heap was made anew
// while the ImGui backend, set up once, kept every font and image in the old one.
struct Renderer {
    bool ready = false;
    bool failed = false;

    ID3D12Device* device = nullptr;
    ID3D12DescriptorHeap* rtvHeap = nullptr;
    ID3D12DescriptorHeap* srvHeap = nullptr;
    ID3D12GraphicsCommandList* commandList = nullptr;
    ID3D12Fence* fence = nullptr;
    HANDLE fenceEvent = nullptr;
    UINT64 fenceValue = 0;

    std::vector<Slot> slots;
    UINT64 frameCount = 0;
    std::vector<bool> srvUsed;

    // The back-buffer format ImGui's pipeline is built for; false once rebuilding it failed.
    DXGI_FORMAT pipelineFormat = DXGI_FORMAT_UNKNOWN;
    bool pipelineReady = false;

    Target target;

    // Whether the last frame was drawn, or passed over as minimized or too small; and whether
    // the next frame drawn is the first since, or the first of all, and so worth a line.
    bool drawing = true;
    bool reportNextFrame = true;

    HWND window = nullptr;
    WNDPROC originalWndProc = nullptr;
};

Renderer g_renderer;
std::mutex g_rendererGate;

// The game's window, for what runs off the render thread - the key pump and the parking. Set
// once the renderer has found it.
std::atomic<HWND> g_window{nullptr};

// Presents seen, whether or not the overlay drew into them. Only whether this still moves
// matters: it is how the key pump tells a game that draws from one that has stopped.
std::atomic<uint64_t> g_frames{0};

// The window is off-screen in place of minimized; see "playing on while minimized" below.
std::atomic<bool> g_parked{false};

// Keys held down in the game for the host. Touched under g_keysGate only - a lock of their own
// rather than the renderer's, so the key pump never waits behind a frame being drawn, nor a
// frame behind the pump.
std::mutex g_keysGate;

HeldKeys& Keys() {
    static HeldKeys keys([](uint16_t key, bool down) {
        static int logged = 0;
        if (logged < 400) {
            ++logged;
            LogLine(std::string(down ? "Holding" : "Releasing") + " key " + std::to_string(key) + " for the host.");
        }
        PostKeyToWindow(g_window.load(std::memory_order_acquire), key, down);
    });
    return keys;
}

// The held keys' clock. Touched under g_keysGate only, like the keys.
KeyPump& Pump() {
    static KeyPump pump(
        Keys(),
        [](const std::string& line) { LogLine(line); },
        [](const std::string& value) {
            if (g_ipc == nullptr) return;
            Command told;
            told.name = "game-window";
            told.value = value;
            g_ipc->Send(told);
        });
    return pump;
}

// ---------------------------------------------------------------- clicks for the host

// Where the player had the pointer before a click of the host's moved it, and where the click
// put it: put back afterwards, unless the player has moved it since. Touched under g_keysGate.
POINT g_pointerBefore{};
POINT g_pointerPut{};
bool g_pointerMoved = false;

// One step of a click for the host, in the game's window. The pointer is brought to the point
// as well as the messages posted there: Unreal takes a button press at the point the message
// gives, but counts it a click only over the button its own idea of the pointer is over, and
// that it reads from the real pointer. A parked window is off every screen, where the pointer
// cannot go; the messages go alone.
void PostMouseStep(MouseStep step, const Click& click, const Click::Point& point) {
    HWND window = g_window.load(std::memory_order_acquire);

    if (step == MouseStep::Restore) {
        POINT now{};
        if (g_pointerMoved && GetCursorPos(&now) != FALSE && now.x == g_pointerPut.x && now.y == g_pointerPut.y)
            SetCursorPos(g_pointerBefore.x, g_pointerBefore.y);
        g_pointerMoved = false;
        return;
    }

    RECT client{};
    if (window == nullptr || GetClientRect(window, &client) == FALSE || client.right <= client.left || client.bottom <= client.top) {
        if (step == MouseStep::Move) LogLine("Did not click for the host: the game window has no area to click in now - it may be minimized.");
        return;
    }

    const int width = client.right - client.left;
    const int height = client.bottom - client.top;
    const POINT at = PlaceInLayout(width, height, click.layout_width, click.layout_height, point.x, point.y, click.ui_scale);
    const LPARAM where = MAKELPARAM(at.x, at.y);

    switch (step) {
        case MouseStep::Move: {
            POINT screen = at;
            ClientToScreen(window, &screen);
            if (MonitorFromPoint(screen, MONITOR_DEFAULTTONULL) != nullptr) {
                if (!g_pointerMoved && GetCursorPos(&g_pointerBefore) != FALSE) g_pointerMoved = true;
                SetCursorPos(screen.x, screen.y);
                g_pointerPut = screen;
            }
            PostMessageW(window, WM_MOUSEMOVE, kInjectedMouseMarker, where);
            LogFormat("Clicking %d,%d of the game window's %dx%d client area for the host (%d,%d of the layout, drawn at %g%%).", static_cast<int>(at.x),
                      static_cast<int>(at.y), width, height, point.x, point.y, ClientUiScale(click.ui_scale, width, height) * 100.0);
            break;
        }
        case MouseStep::Down:
            PostMessageW(window, WM_LBUTTONDOWN, MK_LBUTTON | kInjectedMouseMarker, where);
            break;
        case MouseStep::Up:
            PostMessageW(window, WM_LBUTTONUP, kInjectedMouseMarker, where);
            break;
        default:
            break;
    }
}

// The host's clicks, made on the key pump's clock. Touched under g_keysGate only.
Clicker& Clicks() {
    static Clicker clicks(&PostMouseStep, [](const std::string& line) { LogLine(line); });
    return clicks;
}

// ---------------------------------------------------------------- the key pump

// Presses, refreshes and lets go of the held keys on a thread of its own, so none of it waits
// on a frame: a minimized game presents few or none, and keys applied only from Present were
// neither pressed nor - worse - let go once the host went quiet. The pipe wakes it when the
// host sends a new set; otherwise it looks every kKeyPumpIntervalMs.
std::thread g_keyPump;
std::atomic<bool> g_keyPumpStopping{false};

// Never closed: the window procedure can still be on its way in when the overlay unloads, and
// a closed handle's number can be handed to something else, which it would then signal. One
// event per load is the price.
std::atomic<HANDLE> g_keyPumpWake{nullptr};

// Set by the window procedure when the window is activated, deactivated, minimized or
// restored; the pump presses the held keys again a moment later.
std::atomic<bool> g_windowChanged{false};

void WakeKeyPump() {
    HANDLE wake = g_keyPumpWake.load(std::memory_order_acquire);
    if (wake != nullptr) SetEvent(wake);
}

void UpdateParking(uint64_t now_ms);

void PumpKeysOnce() {
    std::vector<uint16_t> wanted;
    uint64_t age_ms = UINT64_MAX;
    bool connected = false;
    if (g_ipc != nullptr) {
        g_ipc->WantedKeys(wanted, age_ms);
        connected = g_ipc->Connected();
    }

    GameWindowSeen seen;
    HWND window = g_window.load(std::memory_order_acquire);
    seen.minimized = window != nullptr && IsIconic(window) != FALSE;
    seen.frames = g_frames.load(std::memory_order_relaxed);

    const uint64_t now = GetTickCount64();
    UpdateParking(now);
    seen.parked = g_parked.load(std::memory_order_acquire);

    Click click;
    const bool clicking = g_ipc != nullptr && g_ipc->TakeClick(click);

    std::lock_guard<std::mutex> lock(g_keysGate);
    if (g_windowChanged.exchange(false, std::memory_order_acq_rel))
        Pump().WindowChanged(now);
    Pump().Pass(std::move(wanted), age_ms, connected, seen, now);

    if (clicking) Clicks().Begin(click, now);
    Clicks().Pass(now);
}

// How long the pump may sleep: its usual interval, or less when a click's next step is due
// sooner.
DWORD PumpWaitMs() {
    std::lock_guard<std::mutex> lock(g_keysGate);
    const uint64_t due = Clicks().NextDueIn(GetTickCount64());
    return due < kKeyPumpIntervalMs ? static_cast<DWORD>(due) : static_cast<DWORD>(kKeyPumpIntervalMs);
}

void KeyPumpLoop() {
    HANDLE wake = g_keyPumpWake.load(std::memory_order_acquire);
    while (!g_keyPumpStopping.load(std::memory_order_acquire)) {
        const DWORD wait = PumpWaitMs();
        if (wake != nullptr)
            WaitForSingleObject(wake, wait);
        else
            Sleep(wait);
        if (g_keyPumpStopping.load(std::memory_order_acquire)) break;

        // An exception leaving this thread would end the game; a pass that fails is one late
        // key, and the next pass tries again.
        try {
            PumpKeysOnce();
        } catch (...) {
        }
    }
}

// Before the pipe starts, so its first set of keys already wakes the pump.
void StartKeyPump() {
    g_keyPumpWake.store(CreateEventW(nullptr, FALSE, FALSE, nullptr), std::memory_order_release);
    if (g_ipc != nullptr) g_ipc->SetKeysListener(&WakeKeyPump);

    try {
        g_keyPumpStopping.store(false, std::memory_order_release);
        g_keyPump = std::thread(&KeyPumpLoop);
        LogLine("Held keys are pressed from a pump of their own, frames or none.");
    } catch (...) {
        LogLine("Could not start the key pump; no key will be held for the host.");
    }
}

// Waits for the pump to leave, but not for ever: under the loader lock a thread cannot finish
// exiting, and a wait without a bound would hang the game where it should merely unload.
void StopKeyPump() {
    g_keyPumpStopping.store(true, std::memory_order_release);
    WakeKeyPump();

    if (g_keyPump.joinable()) {
        if (WaitForSingleObject(static_cast<HANDLE>(g_keyPump.native_handle()), 2000) == WAIT_OBJECT_0)
            g_keyPump.join();
        else {
            LogLine("The key pump did not stop in time; leaving it to finish on its own.");
            g_keyPump.detach();
        }
    }
}

// ---------------------------------------------------------------- playing on while minimized

// For a player who asked to keep playing with the game minimized: minimizing it sends the
// window off every screen instead - "parked" - and slows its frames, so it costs little, while
// Unreal, which never sees a minimized window, goes on taking the keys held for plugins. It
// comes back where it was the moment the player brings it back - from the taskbar, with
// Alt+Tab, or by minimizing it again - and on its own if the switch goes off or the host goes
// for good. native/docs/d3d12-overlay-design.md has the trade-offs.

// The longest frame while parked, in milliseconds: about thirty frames a second, slow enough to
// spare the machine and quick enough that a turn timed in tenths of a second still lands.
constexpr DWORD kParkedFrameMs = 33;

// How long the host may be gone before a parked window is put back and minimized for real: a
// host restarting is back well within it.
constexpr uint64_t kParkedHostGraceMs = 10000;

std::mutex g_parkGate;
RECT g_home{};  // under g_parkGate: where the window was before it was parked
uint64_t g_parkHostGoneAt = 0;  // the pump's own; when the host or the switch went

bool KeepPlayingMinimized() {
    if (g_ipc == nullptr || !g_ipc->Connected()) return false;
    std::shared_ptr<const State> latest = g_ipc->Latest();
    return latest != nullptr && latest->keep_playing_minimized;
}

// An ordinary window with a title bar, as the game is when windowed, and shown. A full-screen
// or maximized one minimizes as it always did.
bool CanPark(HWND window) {
    const LONG_PTR style = GetWindowLongPtrW(window, GWL_STYLE);
    return (style & WS_CAPTION) == WS_CAPTION && IsIconic(window) == FALSE && IsZoomed(window) == FALSE;
}

// The window minimizing would have handed the keyboard to: the next one down that a player
// could see and use - or the desktop, if there is none.
HWND NextWindowToActivate(HWND window) {
    for (HWND next = GetWindow(window, GW_HWNDNEXT); next != nullptr; next = GetWindow(next, GW_HWNDNEXT)) {
        if (IsWindowVisible(next) == FALSE || IsIconic(next) != FALSE || GetWindow(next, GW_OWNER) != nullptr) continue;

        const LONG_PTR extended = GetWindowLongPtrW(next, GWL_EXSTYLE);
        if ((extended & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) continue;

        // Windows on another virtual desktop, and suspended store apps, are "cloaked": there,
        // but nowhere the player can see.
        BOOL cloaked = FALSE;
        if (SUCCEEDED(DwmGetWindowAttribute(next, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked) continue;

        RECT rect{};
        if (GetWindowRect(next, &rect) == FALSE || rect.right <= rect.left || rect.bottom <= rect.top) continue;
        return next;
    }

    return GetShellWindow();
}

// Game thread only: called from the window procedure in place of minimizing.
void Park(HWND window) {
    RECT home{};
    if (GetWindowRect(window, &home) == FALSE) return;

    {
        std::lock_guard<std::mutex> lock(g_parkGate);
        g_home = home;
    }
    g_parked.store(true, std::memory_order_release);

    // Clear of every monitor: past the left edge of the whole desktop by the window's own width,
    // at its own height, and its size unchanged - a resize would have Unreal and the client lay
    // their screens out again, and the client keeps where its panels sit by the window's size.
    const int x = GetSystemMetrics(SM_XVIRTUALSCREEN) - (home.right - home.left) - 64;
    HWND next = NextWindowToActivate(window);
    SetWindowPos(window, HWND_BOTTOM, x, home.top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    if (next != nullptr) SetForegroundWindow(next);

    LogFormat("Parked the game window off-screen in place of minimizing it (it was at %ld,%ld); frames are held to %u ms each until it comes back.",
              home.left, home.top, static_cast<unsigned>(kParkedFrameMs));
    WakeKeyPump();
}

// Puts a parked window back where it was. From the game's own thread it moves at once; from any
// other the move is posted, so this never waits on a game thread that may be waiting on us.
// With then_minimize the window is then minimized for real, which is what the player asked for
// when it was parked.
void Unpark(HWND window, bool on_window_thread, bool then_minimize, const char* why) {
    if (window == nullptr || !g_parked.exchange(false, std::memory_order_acq_rel)) return;

    RECT home{};
    {
        std::lock_guard<std::mutex> lock(g_parkGate);
        home = g_home;
    }

    UINT flags = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE;
    if (!on_window_thread) flags |= SWP_ASYNCWINDOWPOS;
    SetWindowPos(window, nullptr, home.left, home.top, 0, 0, flags);
    if (then_minimize) ShowWindowAsync(window, SW_SHOWMINNOACTIVE);

    LogFormat("Put the game window back at %ld,%ld%s: %s.", home.left, home.top, then_minimize ? " and minimized it" : "", why);
    WakeKeyPump();
}

// The pump's look at a parked window: put back, and minimized for real, once the player has
// switched keeping on off, or the host has been gone a while.
void UpdateParking(uint64_t now_ms) {
    if (!g_parked.load(std::memory_order_acquire)) {
        g_parkHostGoneAt = 0;
        return;
    }

    const bool connected = g_ipc != nullptr && g_ipc->Connected();
    if (connected && KeepPlayingMinimized()) {
        g_parkHostGoneAt = 0;
        return;
    }

    if (connected) {
        Unpark(g_window.load(std::memory_order_acquire), false, true, "keeping on while minimized was switched off");
        return;
    }

    if (g_parkHostGoneAt == 0) g_parkHostGoneAt = now_ms;
    if (now_ms - g_parkHostGoneAt >= kParkedHostGraceMs)
        Unpark(g_window.load(std::memory_order_acquire), false, true, "the host has gone");
}

// Called from the Present hooks: while parked, no frame comes sooner than kParkedFrameMs after
// the one before. Sleeping here holds up the render thread, and Unreal's game thread waits on
// that, so the whole game slows - which is the point - while it keeps ticking, taking keys and
// keeping its connection to the server.
void ThrottleWhileParked() {
    static std::atomic<int64_t> last_ms{0};
    if (!g_parked.load(std::memory_order_relaxed)) return;

    using namespace std::chrono;
    const int64_t now = duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count();
    const int64_t due = last_ms.load(std::memory_order_relaxed) + kParkedFrameMs;
    if (now < due && due - now <= static_cast<int64_t>(kParkedFrameMs))
        Sleep(static_cast<DWORD>(due - now));
    last_ms.store(duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count(), std::memory_order_relaxed);
}

void NoteWindowChanged() {
    g_windowChanged.store(true, std::memory_order_release);
    WakeKeyPump();
}

template <typename T>
void Release(T*& object) {
    if (object != nullptr) {
        object->Release();
        object = nullptr;
    }
}

// ---------------------------------------------------------------- descriptors

void AllocateSrv(ImGui_ImplDX12_InitInfo*, D3D12_CPU_DESCRIPTOR_HANDLE* cpu, D3D12_GPU_DESCRIPTOR_HANDLE* gpu) {
    Renderer& r = g_renderer;
    UINT stride = r.device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);

    for (size_t i = 0; i < r.srvUsed.size(); ++i) {
        if (r.srvUsed[i]) continue;

        r.srvUsed[i] = true;
        cpu->ptr = r.srvHeap->GetCPUDescriptorHandleForHeapStart().ptr + i * stride;
        gpu->ptr = r.srvHeap->GetGPUDescriptorHandleForHeapStart().ptr + i * stride;
        return;
    }

    // Out of descriptors. Handing back the first is wrong but survivable - a texture
    // draws with the wrong image - whereas an out-of-range handle is a device removal.
    LogLine("Ran out of texture descriptors; something is allocating far more than expected.");
    cpu->ptr = r.srvHeap->GetCPUDescriptorHandleForHeapStart().ptr;
    gpu->ptr = r.srvHeap->GetGPUDescriptorHandleForHeapStart().ptr;
}

void FreeSrv(ImGui_ImplDX12_InitInfo*, D3D12_CPU_DESCRIPTOR_HANDLE cpu, D3D12_GPU_DESCRIPTOR_HANDLE) {
    Renderer& r = g_renderer;
    if (r.srvHeap == nullptr || r.device == nullptr) return;

    UINT stride = r.device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    SIZE_T base = r.srvHeap->GetCPUDescriptorHandleForHeapStart().ptr;
    if (cpu.ptr < base || stride == 0) return;

    size_t index = (cpu.ptr - base) / stride;
    if (index < r.srvUsed.size())
        r.srvUsed[index] = false;
}

// ---------------------------------------------------------------- input

// Marks a call as executing inside this DLL, so removal can wait for it to leave.
//
// Covers the window procedure as well as the render detours. Leaving it off the window
// procedure crashed the client on unload: the message thread was inside it when the
// procedure was restored, and by the time it returned the code it was returning into had
// been unmapped. An access violation with our name on it, during what had just reported
// itself a clean removal.
struct InFlight {
    InFlight() { g_inFlight.fetch_add(1, std::memory_order_acq_rel); }
    ~InFlight() { g_inFlight.fetch_sub(1, std::memory_order_acq_rel); }
};


LRESULT CALLBACK OverlayWndProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    InFlight guard;

    WNDPROC original = g_renderer.originalWndProc;

    // Restoring the procedure is not instant from the point of view of a call already on
    // its way in, so this can be reached once after removal has begun. Passing it
    // straight to the window's own default is the only safe answer left.
    if (original == nullptr)
        return DefWindowProcW(window, message, wParam, lParam);

    switch (message) {
        case WM_SYSCOMMAND:
            if ((wParam & 0xFFF0) == SC_MINIMIZE) {
                // Minimizing a parked window - the taskbar button of the window in front - is the
                // player reaching for a window they cannot see: it comes back.
                if (g_parked.load(std::memory_order_acquire)) {
                    Unpark(window, true, false, "the player asked for it");
                    SetForegroundWindow(window);
                    return 0;
                }

                // Not once unloading has begun: nothing would be left to bring it back.
                if (g_installed.load(std::memory_order_acquire) && KeepPlayingMinimized()) {
                    if (CanPark(window)) {
                        Park(window);
                        return 0;
                    }
                    LogLine("Minimizing the game window as usual: it is full-screen or maximized, and only a window with a title bar is parked.");
                }
            }
            break;

        case WM_ACTIVATE:
            // Brought back from the taskbar or with Alt+Tab: back where it was, before Unreal
            // sees the activation and confines the cursor to wherever the window is.
            if (LOWORD(wParam) != WA_INACTIVE && g_parked.load(std::memory_order_acquire))
                Unpark(window, true, false, "the player brought it back");
            NoteWindowChanged();
            break;

        case WM_ACTIVATEAPP:
        case WM_SETFOCUS:
        case WM_KILLFOCUS:
            NoteWindowChanged();
            break;

        case WM_SIZE:
            if (wParam == SIZE_MINIMIZED || wParam == SIZE_RESTORED || wParam == SIZE_MAXIMIZED)
                NoteWindowChanged();
            break;

        case WM_CLOSE:
        case WM_QUERYENDSESSION:
        case WM_ENDSESSION:
        case WM_DESTROY:
            // A game that keeps where its window was must not keep it off-screen.
            if (g_parked.load(std::memory_order_acquire))
                Unpark(window, true, false, "the game is closing");
            break;

        default:
            break;
    }

    // A key held for the host goes to the game whatever the overlay's windows want: they
    // would otherwise take it whenever one of them had the keyboard.
    if (IsInjectedKey(message, lParam))
        return CallWindowProcW(original, window, message, wParam, lParam);

    // So does a click made for the host, its marker taken off: an overlay window drawn where it
    // lands - Virindi Tank's, over the character list - would otherwise take it.
    if (IsInjectedMouse(message, wParam))
        return CallWindowProcW(original, window, message, wParam & ~kInjectedMouseMask, lParam);

    // A hotkey window waiting for a key: the next key pressed that is not a modifier goes to
    // it and not to the game - Escape to cancel - once for each snapshot that asks.
    if ((message == WM_KEYDOWN || message == WM_SYSKEYDOWN) && (lParam & (static_cast<LPARAM>(1) << 30)) == 0 &&
        g_ipc != nullptr && g_ipc->Connected()) {
        std::shared_ptr<const State> latest = g_ipc->Latest();
        if (latest != nullptr && !latest->key_capture.empty()) {
            const int vk = static_cast<int>(wParam);
            const bool modifier = vk == VK_SHIFT || vk == VK_CONTROL || vk == VK_MENU || vk == VK_LSHIFT || vk == VK_RSHIFT ||
                                  vk == VK_LCONTROL || vk == VK_RCONTROL || vk == VK_LMENU || vk == VK_RMENU ||
                                  vk == VK_LWIN || vk == VK_RWIN || vk == VK_CAPITAL;
            if (!modifier) {
                static int64_t answered = -1;
                if (answered != latest->revision) {
                    answered = latest->revision;
                    Command captured;
                    captured.name = "key-captured";
                    captured.owner = latest->key_capture;
                    if (vk == VK_ESCAPE) {
                        captured.value = "cancel";
                    } else {
                        const bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
                        const bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
                        const bool alt = (GetKeyState(VK_MENU) & 0x8000) != 0;
                        captured.value = std::to_string(vk) + "," + (ctrl ? "1" : "0") + "," + (shift ? "1" : "0") + "," + (alt ? "1" : "0");
                    }
                    g_ipc->Send(captured);
                    LogLine("Caught key " + captured.value + " for " + captured.owner + ".");
                }
                return 0;
            }
        }
    }

    // A plugin's hotkey: told to the plugin, kept from the game. Not while a text box of ours
    // is being typed into, and not on the repeats of a key held down.
    if ((message == WM_KEYDOWN || message == WM_SYSKEYDOWN) && (lParam & (static_cast<LPARAM>(1) << 30)) == 0 &&
        g_ipc != nullptr && g_ipc->Connected()) {
        const bool typing = ImGui::GetCurrentContext() != nullptr && ImGui::GetIO().WantTextInput;
        if (!typing) {
            std::shared_ptr<const State> latest = g_ipc->Latest();
            if (latest != nullptr && !latest->hotkeys.empty()) {
                const bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
                const bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
                const bool alt = (GetKeyState(VK_MENU) & 0x8000) != 0;
                if (const Hotkey* hotkey = FindHotkey(latest->hotkeys, static_cast<int>(wParam), ctrl, shift, alt)) {
                    Command pressed;
                    pressed.name = "hotkey";
                    pressed.value = hotkey->id;
                    pressed.owner = hotkey->owner;
                    g_ipc->Send(pressed);
                    LogLine("Hotkey " + hotkey->id + " for " + hotkey->owner + ".");
                    return 0;
                }
            }
        }
    }

    if (g_visible.load(std::memory_order_relaxed) && ImGui::GetCurrentContext() != nullptr) {
        ImGui_ImplWin32_WndProcHandler(window, message, wParam, lParam);

        ImGuiIO& io = ImGui::GetIO();

        // The first few presses and releases, and who took them. A window that cannot be
        // dragged is either the game taking the press or the overlay losing the movement
        // after it, and this line is what tells the two apart without a debugger in the
        // game process. Capped, because a play session is thousands of clicks.
        if (message == WM_LBUTTONDOWN || message == WM_LBUTTONUP) {
            static std::atomic<int> logged{0};
            if (logged.fetch_add(1, std::memory_order_relaxed) < 40) {
                LogLine(std::string("Left button ") + (message == WM_LBUTTONDOWN ? "down" : "up") + " at " +
                        std::to_string(static_cast<short>(LOWORD(lParam))) + "," +
                        std::to_string(static_cast<short>(HIWORD(lParam))) + "; the overlay " +
                        (io.WantCaptureMouse ? "has" : "does not have") + " the mouse.");
            }
        }

        // This client reads the mouse through raw input, which the ImGui backend does not
        // handle at all. Swallowing the cooked mouse messages alone would leave the game
        // still turning the camera while the overlay believes it has the pointer, so
        // WM_INPUT has to go with them.
        switch (message) {
            case WM_INPUT:
                if (io.WantCaptureMouse || io.WantCaptureKeyboard)
                    return 0;
                break;

            case WM_MOUSEMOVE:
            case WM_LBUTTONDOWN:
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
            case WM_RBUTTONDOWN:
            case WM_RBUTTONUP:
            case WM_MBUTTONDOWN:
            case WM_MBUTTONUP:
            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
                if (io.WantCaptureMouse)
                    return 0;
                break;

            case WM_KEYDOWN:
            case WM_KEYUP:
            case WM_SYSKEYDOWN:
            case WM_SYSKEYUP:
            case WM_CHAR:
                if (io.WantCaptureKeyboard)
                    return 0;
                break;

            default:
                break;
        }
    }

    return CallWindowProcW(original, window, message, wParam, lParam);
}

// ---------------------------------------------------------------- lifetime

void WaitForGpu() {
    Renderer& r = g_renderer;
    if (r.fence == nullptr || r.fenceEvent == nullptr) return;

    ID3D12CommandQueue* queue = g_queue.load(std::memory_order_acquire);
    if (queue == nullptr) return;

    UINT64 target = ++r.fenceValue;
    if (FAILED(queue->Signal(r.fence, target))) return;

    if (r.fence->GetCompletedValue() < target) {
        if (SUCCEEDED(r.fence->SetEventOnCompletion(target, r.fenceEvent)))
            WaitForSingleObject(r.fenceEvent, 1000);
    }
}

void ReleaseSlots() {
    Renderer& r = g_renderer;
    for (Slot& slot : r.slots) Release(slot.allocator);
    r.slots.clear();
}

void Teardown() {
    std::lock_guard<std::mutex> lock(g_rendererGate);
    Renderer& r = g_renderer;

    if (!r.ready && !r.failed) return;

    WaitForGpu();

    if (ImGui::GetCurrentContext() != nullptr) {
        // The backend frees the GPU side of our textures as it shuts down; their CPU side
        // is ours to free after that and before the context they are registered with goes.
        if (r.pipelineReady) ImGui_ImplDX12_Shutdown();
        ImGui_ImplWin32_Shutdown();
        ReleaseAllTextures();
        ImGui::DestroyContext();
    }

    ReleaseSlots();
    Release(r.commandList);
    Release(r.fence);
    Release(r.srvHeap);
    Release(r.rtvHeap);
    Release(r.device);

    if (r.fenceEvent != nullptr) {
        CloseHandle(r.fenceEvent);
        r.fenceEvent = nullptr;
    }

    r.srvUsed.clear();
    r.pipelineReady = false;
    r.ready = false;
    r.failed = false;
}

// What ImGui's D3D12 backend is told: the device, the queue it uploads textures on, the turn of
// frames, our texture heap, and the format of the back buffers its pipeline draws into.
ImGui_ImplDX12_InitInfo PipelineInfo(DXGI_FORMAT format) {
    Renderer& r = g_renderer;
    ImGui_ImplDX12_InitInfo info{};
    info.Device = r.device;
    info.CommandQueue = g_queue.load(std::memory_order_acquire);
    info.NumFramesInFlight = static_cast<int>(r.slots.size());

    // Read from the swap chain rather than assumed: the back buffer format is a cvar
    // in this client and can be a float format.
    info.RTVFormat = format;

    // No depth buffer is bound, so the pipeline state must be built without one or
    // the draw is invalid.
    info.DSVFormat = DXGI_FORMAT_UNKNOWN;

    info.SrvDescriptorHeap = r.srvHeap;
    info.SrvDescriptorAllocFn = &AllocateSrv;
    info.SrvDescriptorFreeFn = &FreeSrv;
    return info;
}

// Builds everything the overlay draws with, once. Returns false once and then stays false: a
// renderer that cannot be set up will not become able to on the next frame, and retrying every
// frame would fill the log at sixty lines a second.
bool Prepare(IDXGISwapChain3* swapChain, const DXGI_SWAP_CHAIN_DESC& desc) {
    Renderer& r = g_renderer;

    if (r.ready) return true;
    if (r.failed) return false;

    ID3D12CommandQueue* queue = g_queue.load(std::memory_order_acquire);
    if (queue == nullptr) {
        // Not a failure yet: the queue arrives from ExecuteCommandLists or a resize, and
        // one of those will happen shortly.
        return false;
    }

    if (r.device == nullptr && FAILED(swapChain->GetDevice(__uuidof(ID3D12Device), reinterpret_cast<void**>(&r.device)))) {
        LogLine("The swap chain is not a D3D12 swap chain.");
        r.failed = true;
        return false;
    }

    // The queue must belong to the same device, or every submission is invalid. Two
    // devices in one process is unusual but a stereo or VR path could do it.
    {
        ID3D12Device* queueDevice = nullptr;
        if (SUCCEEDED(queue->GetDevice(__uuidof(ID3D12Device), reinterpret_cast<void**>(&queueDevice)))) {
            bool mismatch = queueDevice != r.device;
            queueDevice->Release();

            if (mismatch) {
                LogLine("The captured queue belongs to a different device; waiting for one that matches.");
                Release(r.device);
                g_queue.store(nullptr, std::memory_order_release);
                return false;
            }
        }
    }

    // As many frames in flight as the game has buffers when the overlay starts - three, for this
    // client - and that many from then on.
    const UINT slots = std::clamp<UINT>(desc.BufferCount, 2, 4);

    D3D12_DESCRIPTOR_HEAP_DESC rtvDesc{};
    rtvDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
    rtvDesc.NumDescriptors = slots;
    rtvDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE;

    D3D12_DESCRIPTOR_HEAP_DESC srvDesc{};
    srvDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
    srvDesc.NumDescriptors = kSrvDescriptors;
    srvDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;

    if (FAILED(r.device->CreateDescriptorHeap(&rtvDesc, __uuidof(ID3D12DescriptorHeap), reinterpret_cast<void**>(&r.rtvHeap)))
        || FAILED(r.device->CreateDescriptorHeap(&srvDesc, __uuidof(ID3D12DescriptorHeap), reinterpret_cast<void**>(&r.srvHeap)))) {
        LogLine("Could not create descriptor heaps.");
        r.failed = true;
        return false;
    }

    r.srvUsed.assign(kSrvDescriptors, false);

    UINT rtvStride = r.device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
    D3D12_CPU_DESCRIPTOR_HANDLE rtvStart = r.rtvHeap->GetCPUDescriptorHandleForHeapStart();

    r.slots.resize(slots);
    for (UINT i = 0; i < slots; ++i) {
        Slot& slot = r.slots[i];
        slot.rtv.ptr = rtvStart.ptr + i * rtvStride;

        if (FAILED(r.device->CreateCommandAllocator(
                D3D12_COMMAND_LIST_TYPE_DIRECT,
                __uuidof(ID3D12CommandAllocator),
                reinterpret_cast<void**>(&slot.allocator)))) {
            LogLine("Could not create a command allocator.");
            r.failed = true;
            return false;
        }
    }

    if (FAILED(r.device->CreateCommandList(
            0,
            D3D12_COMMAND_LIST_TYPE_DIRECT,
            r.slots[0].allocator,
            nullptr,
            __uuidof(ID3D12GraphicsCommandList),
            reinterpret_cast<void**>(&r.commandList)))) {
        LogLine("Could not create a command list.");
        r.failed = true;
        return false;
    }

    r.commandList->Close();

    if (FAILED(r.device->CreateFence(0, D3D12_FENCE_FLAG_NONE, __uuidof(ID3D12Fence), reinterpret_cast<void**>(&r.fence)))) {
        LogLine("Could not create a fence.");
        r.failed = true;
        return false;
    }

    r.fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);

    // The window comes from the swap chain rather than from a search, so a client with
    // more than one window cannot be got wrong.
    {
        DXGI_SWAP_CHAIN_DESC1 desc1{};
        if (SUCCEEDED(swapChain->GetDesc1(&desc1)))
            swapChain->GetHwnd(&r.window);

        if (r.window == nullptr)
            r.window = desc.OutputWindow;

        g_window.store(r.window, std::memory_order_release);
    }

    ImGui::CreateContext();

    // Where the player left the bar and each window, kept beside this DLL - never in the
    // game's folder - so a reload does not put everything back in the corner. Static,
    // because ImGui keeps the pointer for the context's life.
    static std::string ini;
    const std::wstring folder = DllDirectory();
    ini.clear();
    if (!folder.empty()) {
        const std::wstring path = folder + L"ACUnrealOverlay.ini";
        const int needed = WideCharToMultiByte(CP_UTF8, 0, path.c_str(), -1, nullptr, 0, nullptr, nullptr);
        if (needed > 1) {
            ini.assign(static_cast<size_t>(needed), '\0');
            WideCharToMultiByte(CP_UTF8, 0, path.c_str(), -1, ini.data(), needed, nullptr, nullptr);
            ini.resize(static_cast<size_t>(needed - 1));
        }
    }
    ImGui::GetIO().IniFilename = ini.empty() ? nullptr : ini.c_str();

    // Each window's theme, pin and alpha, kept in the same file - registered before the
    // first frame, which is when ImGui reads it.
    RegisterDecalSettings();
    RegisterOverlaySettings();

    // The game owns the cursor. The Win32 backend would otherwise set its own arrow on
    // every WM_SETCURSOR and every frame, the game would set its cursor straight back,
    // and the pointer blinked between the two whenever it was over one of our windows.
    // Decal drew over the game's cursor rather than replacing it, and so do we - at the
    // cost of the resize arrows at window edges, which the game's cursor cannot show.
    ImGui::GetIO().ConfigFlags |= ImGuiConfigFlags_NoMouseCursorChange;

    ImGui::StyleColorsDark();

    // ImGui's own font first, so it stays the default for everything not drawn in the
    // Decal look; then the faces the Decal theme names.
    ImGui::GetIO().Fonts->AddFontDefault();
    if (!LoadDecalFonts(ImGui::GetIO().Fonts))
        LogLine("Times New Roman was not found in the Windows fonts folder; Decal views will use ImGui's font.");

    if (!ImGui_ImplWin32_Init(r.window)) {
        LogLine("The win32 backend refused the window.");
        r.failed = true;
        return false;
    }

    ImGui_ImplDX12_InitInfo info = PipelineInfo(desc.BufferDesc.Format);
    if (!ImGui_ImplDX12_Init(&info)) {
        LogLine("The D3D12 backend refused to initialise.");
        r.failed = true;
        return false;
    }
    r.pipelineFormat = desc.BufferDesc.Format;
    r.pipelineReady = true;

    r.originalWndProc = reinterpret_cast<WNDPROC>(
        SetWindowLongPtrW(r.window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&OverlayWndProc)));

    LogFormat(
        "Renderer ready: %u buffers, format %d, window 0x%p; %u frames in flight.",
        desc.BufferCount,
        static_cast<int>(desc.BufferDesc.Format),
        static_cast<void*>(r.window),
        slots);

    r.ready = true;
    return true;
}

// The game's resizes and swap-chain changes, said up to a point: dragging the window's frame
// resizes on every step of the drag.
constexpr int kResizeLines = 60;
std::atomic<int> g_resizeLines{0};

bool MayLogResize() {
    const int said = g_resizeLines.fetch_add(1, std::memory_order_relaxed);
    if (said == kResizeLines) LogLine("Further resizes of the game's back buffers are not logged this session.");
    return said < kResizeLines;
}

Target TargetOf(const void* chain, const DXGI_SWAP_CHAIN_DESC& desc) {
    Target target;
    target.chain = chain;
    target.width = desc.BufferDesc.Width;
    target.height = desc.BufferDesc.Height;
    target.buffers = desc.BufferCount;
    target.format = desc.BufferDesc.Format;
    return target;
}

// Notes the back buffers the game presents, and says when they changed other than by a resize
// the hooks saw - a new swap chain for the window, or a path not hooked.
void NoteTarget(const void* chain, const DXGI_SWAP_CHAIN_DESC& desc) {
    Renderer& r = g_renderer;
    const Target now = TargetOf(chain, desc);
    const Target& was = r.target;

    const bool resized = now.width != was.width || now.height != was.height || now.buffers != was.buffers || now.format != was.format;
    if (was.chain != nullptr && now.chain != was.chain) {
        if (MayLogResize())
            LogFormat("The game presents through a new swap chain for its window: %ux%u, %u buffers, format %d. "
                      "The overlay draws into it as into the last; nothing of its own needed making again.",
                      now.width, now.height, now.buffers, static_cast<int>(now.format));
    } else if (was.chain != nullptr && resized) {
        if (MayLogResize())
            LogFormat("The game's back buffers changed from %ux%u to %ux%u (%u buffers, format %d) without a resize the overlay saw.",
                      was.width, was.height, now.width, now.height, now.buffers, static_cast<int>(now.format));
    }

    r.target = now;
}

// Builds ImGui's pipeline again for back buffers of another format - as HDR gives: drawing with
// a pipeline built for one format into buffers of another is invalid, and may cost the device.
// The fonts and images go with it and are uploaded again on the next frame.
bool RebuildPipeline(DXGI_FORMAT format) {
    Renderer& r = g_renderer;
    WaitForGpu();

    const DXGI_FORMAT was = r.pipelineFormat;
    if (r.pipelineReady) ImGui_ImplDX12_Shutdown();
    r.pipelineReady = false;

    ImGui_ImplDX12_InitInfo info = PipelineInfo(format);
    if (!ImGui_ImplDX12_Init(&info)) {
        LogFormat("The game's back buffers are now format %d, and the D3D12 backend refused to draw into them. No overlay until the game restarts.",
                  static_cast<int>(format));
        r.failed = true;
        return false;
    }

    r.pipelineFormat = format;
    r.pipelineReady = true;
    LogFormat("The game's back buffers changed format from %d to %d; rebuilt the overlay's pipeline for it, and its fonts and images are uploaded again.",
              static_cast<int>(was), static_cast<int>(format));
    return true;
}

// Whether a frame is drawn at all: not while the game is minimized, nor while its window or its
// back buffers are too small to hold a window - Unreal resizes them to 8 x 8 when minimized. Such
// a frame does not reach ImGui, so nothing in it can move a window or have one saved where it
// was put: a hudified window kept on a screen of no size went to its corner, and was saved there.
bool Drawable(const DXGI_SWAP_CHAIN_DESC& desc) {
    Renderer& r = g_renderer;

    RECT client{};
    const bool iconic = IsIconic(r.window) != FALSE;
    if (!iconic) GetClientRect(r.window, &client);
    const int clientWidth = client.right - client.left;
    const int clientHeight = client.bottom - client.top;

    const bool drawable = !iconic && DisplayUsable(static_cast<float>(clientWidth), static_cast<float>(clientHeight)) &&
                          DisplayUsable(static_cast<float>(desc.BufferDesc.Width), static_cast<float>(desc.BufferDesc.Height));

    if (drawable != r.drawing) {
        r.drawing = drawable;
        if (drawable) r.reportNextFrame = true;
        static int said = 0;
        if (said < 200) {
            ++said;
            if (drawable)
                LogFormat("Drawing again: the game window is %dx%d, its back buffers %ux%u.", clientWidth, clientHeight,
                          desc.BufferDesc.Width, desc.BufferDesc.Height);
            else
                LogFormat("Not drawing while the game window is %s (window %dx%d, back buffers %ux%u); the overlay's windows stay where they are.",
                          iconic ? "minimized" : "too small", clientWidth, clientHeight, desc.BufferDesc.Width, desc.BufferDesc.Height);
        }
    }

    return drawable;
}

// A frame the overlay meant to draw and could not, said - up to a point, since one cause fails
// every frame. Such a frame used to go unrecorded, and an overlay that drew nothing said nothing.
void NoteUndrawn(const char* step, HRESULT result) {
    static int said = 0;
    if (said >= 20) return;
    ++said;
    LogFormat("Did not draw a frame: %s failed (0x%08lX).%s", step, static_cast<unsigned long>(result),
              said == 20 ? " No more of these are logged this session." : "");
}

// ---------------------------------------------------------------- drawing

void DrawFrame(IDXGISwapChain3* swapChain) {
    std::lock_guard<std::mutex> lock(g_rendererGate);

    DXGI_SWAP_CHAIN_DESC desc{};
    if (FAILED(swapChain->GetDesc(&desc)))
        return;

    if (!Prepare(swapChain, desc))
        return;

    Renderer& r = g_renderer;

    // The overlay belongs to the game's window. A swap chain for any other - a splash screen, a
    // second viewport - is left to itself.
    if (desc.OutputWindow != nullptr && desc.OutputWindow != r.window) {
        static bool said = false;
        if (!said) {
            said = true;
            LogFormat("A swap chain for another window (0x%p) presents; the overlay draws only on the game's.",
                      static_cast<void*>(desc.OutputWindow));
        }
        return;
    }

    NoteTarget(swapChain, desc);

    // The pipe, and the pump that holds keys for it, start with the first frame the renderer is
    // ready for, and run on their own threads from then on, frames or none.
    std::call_once(g_ipcOnce, [] {
        g_ipc = new (std::nothrow) Ipc(kPipeName);
        if (g_ipc != nullptr) {
            StartKeyPump();
            g_ipc->Start();
            LogLine("Listening for the host on the overlay pipe.");
        }
    });

    if (!Drawable(desc))
        return;

    if (desc.BufferDesc.Format != r.pipelineFormat && !RebuildPipeline(desc.BufferDesc.Format))
        return;

    bool visible = g_visible.load(std::memory_order_relaxed);

    State state;
    if (g_ipc != nullptr) {
        state = g_ipc->Snapshot();

        // Textures are made here, on the thread that owns the context, from whatever images
        // the pipe thread has received since the last frame.
        std::vector<ImagePixels> images = g_ipc->TakeImages();
        if (!images.empty()) {
            const size_t before = TextureCount();
            AcceptImages(std::move(images));
            LogLine("Received images from the host; " + std::to_string(TextureCount()) + " textures now, " +
                    std::to_string(TextureCount() - before) + " new.");
        }

        // The keys the host wants held are not pressed here: the key pump does that, on its own
        // thread, because a minimized game presents few frames or none.
    }

    ImGui_ImplDX12_NewFrame();
    ImGui_ImplWin32_NewFrame();
    ImGui::NewFrame();

    // Left Ctrl shows a hudified window's frame. Read from the keyboard itself, not from
    // ImGui's key events: the game may take its keys as raw input, and then no WM_KEYDOWN
    // ever reaches ImGui. Only while the game is the window in front.
    SetDecalReveal(GetForegroundWindow() == r.window && (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0);

    std::vector<Command> commands = DrawOverlay(state, visible);

    // The game draws its cursor into the frame itself, before Present, so our windows
    // cover it. Over one of them - or dragging one - we draw a cursor of our own on top;
    // everywhere else the game's is the only one. Letting the backend set the OS cursor
    // instead is what made the pointer blink, as the game set its own straight back.
    ImGui::GetIO().MouseDrawCursor = visible && ImGui::GetIO().WantCaptureMouse;

    ImGui::Render();

    g_visible.store(visible, std::memory_order_relaxed);

    if (g_ipc != nullptr) {
        // Every command is a thing the player did, so each is worth a line: when a click seems
        // to do nothing, this says whether it left the overlay at all, and as what. Capped all
        // the same, since a session can be long.
        static int logged_commands = 0;
        for (const Command& command : commands) {
            if (logged_commands < 500) {
                ++logged_commands;
                LogLine("Sent " + command.name + (command.control_id.empty() ? std::string() : " " + command.control_id) +
                        (command.value.empty() ? std::string() : " = " + command.value) +
                        (command.row_id.empty() ? std::string() : " row " + command.row_id) + " to " +
                        (command.owner.empty() ? std::string("the host") : command.owner) +
                        (g_ipc->Connected() ? "." : ", but the host is not connected."));
            }
            g_ipc->Send(command);
        }

        // Art the drawing code wanted and did not have is asked of the host, so new art in
        // this DLL does not wait on a host that knows to send it. A few a frame, so a window's
        // worth of requests cannot crowd the player's clicks out of the command queue; and
        // afresh on each connection, since a new host has sent nothing yet.
        static bool was_connected = false;
        const bool connected = g_ipc->Connected();
        if (connected && !was_connected)
            ForgetWantedImageKeys();
        was_connected = connected;

        if (connected) {
            for (std::string& key : TakeWantedImageKeys(4)) {
                Command wanted;
                wanted.name = "need-image";
                wanted.value = std::move(key);
                g_ipc->Send(wanted);
            }
        }
    }

    Slot& slot = r.slots[r.frameCount % r.slots.size()];

    // The backend reuses its vertex and index buffers with no fence of its own, so
    // without this wait a frame can overwrite geometry the GPU is still reading.
    if (r.fence->GetCompletedValue() < slot.fenceValue && r.fenceEvent != nullptr) {
        if (SUCCEEDED(r.fence->SetEventOnCompletion(slot.fenceValue, r.fenceEvent)))
            WaitForSingleObject(r.fenceEvent, 1000);
    }

    HRESULT step = slot.allocator->Reset();
    if (FAILED(step)) {
        NoteUndrawn("resetting its command allocator", step);
        return;
    }

    // The buffer the game is about to present, taken for this frame alone.
    const UINT index = swapChain->GetCurrentBackBufferIndex();
    ID3D12Resource* backBuffer = nullptr;
    step = swapChain->GetBuffer(index, __uuidof(ID3D12Resource), reinterpret_cast<void**>(&backBuffer));
    if (FAILED(step)) {
        NoteUndrawn("taking the back buffer", step);
        return;
    }

    step = r.commandList->Reset(slot.allocator, nullptr);
    if (FAILED(step)) {
        NoteUndrawn("resetting its command list", step);
        backBuffer->Release();
        return;
    }

    r.device->CreateRenderTargetView(backBuffer, nullptr, slot.rtv);

    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Flags = D3D12_RESOURCE_BARRIER_FLAG_NONE;
    barrier.Transition.pResource = backBuffer;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;

    // The game has just finished with this buffer and is about to present it, so it is in
    // the present state. Getting this wrong is not a visual artefact: the runtime removes
    // the device on a barrier state mismatch, which ends the game.
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
    r.commandList->ResourceBarrier(1, &barrier);

    r.commandList->OMSetRenderTargets(1, &slot.rtv, FALSE, nullptr);
    r.commandList->SetDescriptorHeaps(1, &r.srvHeap);

    ImDrawData* drawData = ImGui::GetDrawData();
    ImGui_ImplDX12_RenderDrawData(drawData, r.commandList);

    // Frees textures the backend has now finished destroying - images the host replaced.
    CollectRetiredTextures();

    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
    r.commandList->ResourceBarrier(1, &barrier);

    step = r.commandList->Close();
    ID3D12CommandQueue* queue = g_queue.load(std::memory_order_acquire);
    if (FAILED(step) || queue == nullptr) {
        NoteUndrawn(queue == nullptr ? "finding the game's queue" : "closing its command list", step);
        backBuffer->Release();
        return;
    }

    ID3D12CommandList* lists[] = { r.commandList };
    queue->ExecuteCommandLists(1, lists);

    slot.fenceValue = ++r.fenceValue;
    queue->Signal(r.fence, slot.fenceValue);
    ++r.frameCount;

    // The first frame drawn after one passed over, said: what ImGui drew, and where it went. If
    // the player sees nothing after this line, the frame went somewhere they do not look.
    if (r.reportNextFrame) {
        r.reportNextFrame = false;
        static int said = 0;
        if (said < 100) {
            ++said;
            LogFormat("Drew a frame, the first %s: %d windows, %d vertices, at %.0fx%.0f, into back buffer %u (%ux%u, format %d) "
                      "of swap chain 0x%p, on queue 0x%p.",
                      r.frameCount == 1 ? "of the session" : "since the window was minimized or too small",
                      drawData != nullptr ? drawData->CmdListsCount : 0, drawData != nullptr ? drawData->TotalVtxCount : 0,
                      drawData != nullptr ? drawData->DisplaySize.x : 0.0f, drawData != nullptr ? drawData->DisplaySize.y : 0.0f,
                      index, desc.BufferDesc.Width, desc.BufferDesc.Height, static_cast<int>(desc.BufferDesc.Format),
                      static_cast<void*>(swapChain), static_cast<void*>(queue));
        }
    }

    // Let go at once: the swap chain keeps its buffer alive, and the game waits for the GPU -
    // as the resize hooks below do - before it resizes or lets go of its swap chain.
    backBuffer->Release();
}

// Logged once so the design note's open question - Present or Present1 - is answered by
// the client rather than guessed at.
void NotePresentPath(const char* which) {
    static std::atomic<bool> said{false};
    bool expected = false;
    if (said.compare_exchange_strong(expected, true))
        LogFormat("The client presents through %s.", which);
}

HRESULT STDMETHODCALLTYPE PresentDetour(IDXGISwapChain* swapChain, UINT syncInterval, UINT flags) {
    InFlight guard;

    // A test present shows nothing: an engine asks with one whether its window can be seen, as
    // around a minimize. Nothing is drawn into it, and it is not counted as a frame.
    if ((flags & DXGI_PRESENT_TEST) != 0)
        return g_originalPresent(swapChain, syncInterval, flags);

    NotePresentPath("Present");
    g_frames.fetch_add(1, std::memory_order_relaxed);

    IDXGISwapChain3* chain3 = nullptr;
    if (SUCCEEDED(swapChain->QueryInterface(__uuidof(IDXGISwapChain3), reinterpret_cast<void**>(&chain3)))) {
        DrawFrame(chain3);
        chain3->Release();
    }

    ThrottleWhileParked();
    return g_originalPresent(swapChain, syncInterval, flags);
}

HRESULT STDMETHODCALLTYPE Present1Detour(
    IDXGISwapChain1* swapChain,
    UINT syncInterval,
    UINT flags,
    const DXGI_PRESENT_PARAMETERS* parameters) {
    InFlight guard;

    if ((flags & DXGI_PRESENT_TEST) != 0)
        return g_originalPresent1(swapChain, syncInterval, flags, parameters);

    NotePresentPath("Present1");
    g_frames.fetch_add(1, std::memory_order_relaxed);

    IDXGISwapChain3* chain3 = nullptr;
    if (SUCCEEDED(swapChain->QueryInterface(__uuidof(IDXGISwapChain3), reinterpret_cast<void**>(&chain3)))) {
        DrawFrame(chain3);
        chain3->Release();
    }

    ThrottleWhileParked();
    return g_originalPresent1(swapChain, syncInterval, flags, parameters);
}

// After the game's resize: said, with the sizes before and after, and noted, so the next frame
// does not take the change for one the hooks missed.
void AfterResize(IDXGISwapChain* swapChain, const char* how, const DXGI_SWAP_CHAIN_DESC& before, UINT width, UINT height, HRESULT result) {
    DXGI_SWAP_CHAIN_DESC after{};
    swapChain->GetDesc(&after);

    if (MayLogResize()) {
        if (FAILED(result))
            LogFormat("The game's %s from %ux%u to %ux%u failed (0x%08lX); its buffers stay %ux%u.", how, before.BufferDesc.Width,
                      before.BufferDesc.Height, width, height, static_cast<unsigned long>(result), after.BufferDesc.Width,
                      after.BufferDesc.Height);
        else
            LogFormat("The game resized its back buffers with %s from %ux%u to %ux%u (%u buffers, format %d). The overlay holds none of "
                      "them between frames and kept its texture heap, pipeline, fonts and images; nothing of its own was made again.",
                      how, before.BufferDesc.Width, before.BufferDesc.Height, after.BufferDesc.Width, after.BufferDesc.Height,
                      after.BufferCount, static_cast<int>(after.BufferDesc.Format));
    }

    IDXGISwapChain3* chain3 = nullptr;
    if (SUCCEEDED(swapChain->QueryInterface(__uuidof(IDXGISwapChain3), reinterpret_cast<void**>(&chain3)))) {
        std::lock_guard<std::mutex> lock(g_rendererGate);
        if (g_renderer.target.chain == chain3 || g_renderer.target.chain == nullptr)
            g_renderer.target = TargetOf(chain3, after);
        chain3->Release();
    }
}

// The game's resize, `resize`, made under the renderer's lock once the GPU is done with every
// frame the overlay drew into the old buffers: no frame of the overlay's can take one of them
// while they are resized, which would fail the resize. The overlay holds none of them otherwise,
// so there is nothing to let go; its view of the new ones is made as it draws, and its texture
// heap, pipeline, fonts and images are all kept.
template <typename Resize>
HRESULT Resized(IDXGISwapChain* swapChain, const char* how, UINT width, UINT height, Resize resize) {
    DXGI_SWAP_CHAIN_DESC before{};
    swapChain->GetDesc(&before);

    HRESULT result = E_FAIL;
    {
        std::lock_guard<std::mutex> lock(g_rendererGate);
        WaitForGpu();
        result = resize();
    }

    AfterResize(swapChain, how, before, width, height, result);
    return result;
}
HRESULT STDMETHODCALLTYPE ResizeBuffersDetour(
    IDXGISwapChain* swapChain,
    UINT bufferCount,
    UINT width,
    UINT height,
    DXGI_FORMAT format,
    UINT flags) {
    InFlight guard;

    return Resized(swapChain, "ResizeBuffers", width, height,
                   [&] { return g_originalResizeBuffers(swapChain, bufferCount, width, height, format, flags); });
}

HRESULT STDMETHODCALLTYPE ResizeBuffers1Detour(
    IDXGISwapChain3* swapChain,
    UINT bufferCount,
    UINT width,
    UINT height,
    DXGI_FORMAT format,
    UINT flags,
    const UINT* nodeMasks,
    IUnknown* const* presentQueues) {
    InFlight guard;

    // The authoritative present queue, handed over rather than inferred. Better evidence
    // than anything ExecuteCommandLists can offer, so it wins.
    if (presentQueues != nullptr && presentQueues[0] != nullptr) {
        ID3D12CommandQueue* queue = nullptr;
        if (SUCCEEDED(presentQueues[0]->QueryInterface(__uuidof(ID3D12CommandQueue), reinterpret_cast<void**>(&queue)))) {
            ID3D12CommandQueue* previous = g_queue.exchange(queue, std::memory_order_acq_rel);
            if (previous != queue)
                LogLine("Took the present queue from ResizeBuffers1.");
            else
                queue->Release();  // already held: one reference is enough

            // Otherwise not released: the pointer is kept for the life of the session, and
            // the reference is what stops it going away underneath us.
        }
    }

    return Resized(swapChain, "ResizeBuffers1", width, height, [&] {
        return g_originalResizeBuffers1(swapChain, bufferCount, width, height, format, flags, nodeMasks, presentQueues);
    });
}

void STDMETHODCALLTYPE ExecuteCommandListsDetour(
    ID3D12CommandQueue* queue,
    UINT count,
    ID3D12CommandList* const* lists) {
    InFlight guard;

    // Only a direct queue can present. This client also runs copy and compute queues,
    // and submitting our draws to one of those would be invalid.
    if (g_queue.load(std::memory_order_acquire) == nullptr && queue != nullptr) {
        D3D12_COMMAND_QUEUE_DESC desc = queue->GetDesc();
        if (desc.Type == D3D12_COMMAND_LIST_TYPE_DIRECT) {
            queue->AddRef();

            ID3D12CommandQueue* expected = nullptr;
            if (g_queue.compare_exchange_strong(expected, queue, std::memory_order_acq_rel))
                LogLine("Took a direct queue from ExecuteCommandLists.");
            else
                queue->Release();
        }
    }

    g_originalExecuteCommandLists(queue, count, lists);
}

// ---------------------------------------------------------------- installation

// Reads a vtable slot from a throwaway object of the same type. MinHook patches the
// implementation, so a temporary instance is enough to find the code the real one shares.
void** VTableOf(void* object) {
    return *reinterpret_cast<void***>(object);
}

bool Hook(void* target, void* detour, void** original, const char* name) {
    if (target == nullptr) return false;

    MH_STATUS status = MH_CreateHook(target, detour, original);

    // Two slots implemented by one function is not a failure; it is one hook covering
    // both, which is what we wanted anyway.
    if (status == MH_ERROR_ALREADY_CREATED) {
        LogFormat("%s shares an implementation with a slot already hooked.", name);
        return true;
    }

    if (status != MH_OK) {
        LogFormat("Could not hook %s (MinHook status %d).", name, static_cast<int>(status));
        return false;
    }

    if (MH_EnableHook(target) != MH_OK) {
        LogFormat("Could not enable the hook on %s.", name);
        return false;
    }

    return true;
}

}  // namespace

bool Install() {
    if (g_installed.load(std::memory_order_acquire))
        return true;

    // d3d12.dll is a delay import in this client, so this is null until the renderer has
    // initialised. That is the ordinary case when injecting at launch, and the caller
    // retries.
    if (GetModuleHandleW(L"d3d12.dll") == nullptr || GetModuleHandleW(L"dxgi.dll") == nullptr)
        return false;

    ID3D12Device* device = nullptr;
    if (FAILED(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device), reinterpret_cast<void**>(&device)))) {
        LogLine("Could not create a throwaway device to read vtables from.");
        return false;
    }

    D3D12_COMMAND_QUEUE_DESC queueDesc{};
    queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;

    ID3D12CommandQueue* queue = nullptr;
    if (FAILED(device->CreateCommandQueue(&queueDesc, __uuidof(ID3D12CommandQueue), reinterpret_cast<void**>(&queue)))) {
        LogLine("Could not create a throwaway queue.");
        device->Release();
        return false;
    }

    IDXGIFactory2* factory = nullptr;
    if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory2), reinterpret_cast<void**>(&factory)))) {
        LogLine("Could not create a DXGI factory.");
        queue->Release();
        device->Release();
        return false;
    }

    // A message-only window is enough: the swap chain is created solely to be asked for
    // its vtable and is never presented.
    WNDCLASSEXW windowClass{};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"ACUnrealOverlayProbe";
    RegisterClassExW(&windowClass);

    HWND probe = CreateWindowExW(
        0, windowClass.lpszClassName, L"", WS_OVERLAPPEDWINDOW,
        0, 0, 16, 16, nullptr, nullptr, windowClass.hInstance, nullptr);

    DXGI_SWAP_CHAIN_DESC1 chainDesc{};
    chainDesc.BufferCount = 2;
    chainDesc.Width = 16;
    chainDesc.Height = 16;
    chainDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    chainDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    chainDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    chainDesc.SampleDesc.Count = 1;

    IDXGISwapChain1* chain = nullptr;
    HRESULT created = factory->CreateSwapChainForHwnd(queue, probe, &chainDesc, nullptr, nullptr, &chain);

    bool ok = false;

    if (SUCCEEDED(created) && chain != nullptr) {
        if (MH_Initialize() != MH_OK) {
            LogLine("MinHook would not initialise.");
        }
        else {
            void** chainVTable = VTableOf(chain);
            void** queueVTable = VTableOf(queue);

            // The present path is hooked both ways because which one this client uses
            // could not be established by reading the binary. The first frame says which,
            // and the other hook simply never fires.
            bool present = Hook(chainVTable[kSlotPresent], &PresentDetour, reinterpret_cast<void**>(&g_originalPresent), "Present");
            bool present1 = Hook(chainVTable[kSlotPresent1], &Present1Detour, reinterpret_cast<void**>(&g_originalPresent1), "Present1");

            // Both resize entry points: UE 5.8 uses ResizeBuffers1 as well, and a missed
            // one means the game's resize fails because we still hold a back buffer.
            Hook(chainVTable[kSlotResizeBuffers], &ResizeBuffersDetour, reinterpret_cast<void**>(&g_originalResizeBuffers), "ResizeBuffers");
            Hook(chainVTable[kSlotResizeBuffers1], &ResizeBuffers1Detour, reinterpret_cast<void**>(&g_originalResizeBuffers1), "ResizeBuffers1");

            Hook(queueVTable[kSlotExecuteCommandLists], &ExecuteCommandListsDetour, reinterpret_cast<void**>(&g_originalExecuteCommandLists), "ExecuteCommandLists");

            ok = present || present1;

            if (!ok) {
                LogLine("Neither present path could be hooked; removing what was installed.");
                MH_DisableHook(MH_ALL_HOOKS);
                MH_Uninitialize();
            }
        }
    }
    else {
        LogFormat("Could not create a throwaway swap chain (0x%08lX).", static_cast<unsigned long>(created));
    }

    if (chain != nullptr) chain->Release();
    factory->Release();
    queue->Release();
    device->Release();

    if (probe != nullptr) DestroyWindow(probe);
    UnregisterClassW(windowClass.lpszClassName, windowClass.hInstance);

    if (ok)
        g_installed.store(true, std::memory_order_release);

    return ok;
}

void Uninstall() {
    if (!g_installed.exchange(false, std::memory_order_acq_rel))
        return;

    // The key pump stops before anything it reads goes, and nothing stays held down once the
    // overlay has gone. A window parked off-screen goes back where it was, minimized as the
    // player asked when it was parked.
    StopKeyPump();
    {
        std::lock_guard<std::mutex> lock(g_keysGate);
        Keys().ReleaseAll();
    }
    Unpark(g_window.load(std::memory_order_acquire), false, true, "the overlay is unloading");

    // The window procedure goes first, and before the drain, because it is the entry
    // point most likely to be executing: the client pumps messages continuously, and a
    // call already inside it when the DLL is unmapped is an access violation rather than
    // a missed frame.
    {
        std::lock_guard<std::mutex> lock(g_rendererGate);
        Renderer& r = g_renderer;

        if (r.originalWndProc != nullptr && r.window != nullptr) {
            SetWindowLongPtrW(r.window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(r.originalWndProc));
            r.originalWndProc = nullptr;
        }
    }

    // Stop new calls entering the detours, then wait for the ones inside to leave.
    // MinHook frees its trampolines on uninitialise, and a thread still executing one
    // would return into freed memory - which is a crash with our name on it during what
    // was meant to be a clean unload.
    MH_DisableHook(MH_ALL_HOOKS);

    // Both the detours and the window procedure are counted here, so this waits for the
    // render thread and the message thread alike.
    for (int waited = 0; waited < 200 && g_inFlight.load(std::memory_order_acquire) > 0; ++waited)
        Sleep(10);

    if (g_inFlight.load(std::memory_order_acquire) > 0)
        LogLine("A hooked call has not returned; leaving the trampolines in place rather than freeing them.");
    else
        MH_Uninitialize();

    Teardown();

    if (g_ipc != nullptr) {
        g_ipc->Stop();
        delete g_ipc;
        g_ipc = nullptr;
    }

    ID3D12CommandQueue* queue = g_queue.exchange(nullptr, std::memory_order_acq_rel);
    if (queue != nullptr)
        queue->Release();

    LogLine("Hooks removed.");
}

bool Installed() {
    return g_installed.load(std::memory_order_acquire);
}

void SetVisible(bool visible) {
    g_visible.store(visible, std::memory_order_relaxed);
}

bool Visible() {
    return g_visible.load(std::memory_order_relaxed);
}

}  // namespace hooks
}  // namespace overlay
