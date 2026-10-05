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
// Second, D3D12 keeps nothing alive for you. Holding a back buffer across a frame keeps a
// reference that makes the game's own ResizeBuffers fail, so resizes must be intercepted
// to let go first.
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
#include <dxgi1_4.h>

#include <atomic>
#include <mutex>
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


// Everything the renderer needs, torn down and rebuilt together on a resize.
struct Frame {
    ID3D12CommandAllocator* allocator = nullptr;
    ID3D12Resource* backBuffer = nullptr;
    D3D12_CPU_DESCRIPTOR_HANDLE rtv{};
    UINT64 fenceValue = 0;
};

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

    std::vector<Frame> frames;
    std::vector<bool> srvUsed;

    HWND window = nullptr;
    WNDPROC originalWndProc = nullptr;
};

Renderer g_renderer;

// Keys held down in the game for the host. Touched under g_rendererGate only.
HeldKeys& Keys() {
    static HeldKeys keys([](uint16_t key, bool down) {
        static int logged = 0;
        if (logged < 400) {
            ++logged;
            LogLine(std::string(down ? "Holding" : "Releasing") + " key " + std::to_string(key) + " for the host.");
        }
        PostKeyToWindow(g_renderer.window, key, down);
    });
    return keys;
}
std::mutex g_rendererGate;

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

    // A key held for the host goes to the game whatever the overlay's windows want: they
    // would otherwise take it whenever one of them had the keyboard.
    if (IsInjectedKey(message, lParam))
        return CallWindowProcW(original, window, message, wParam, lParam);

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

// Releases everything that depends on the swap chain's buffers. Called before the game's
// own resize, because a reference we still hold makes that resize fail.
void ReleaseFrames() {
    Renderer& r = g_renderer;

    for (Frame& frame : r.frames) {
        Release(frame.backBuffer);
        Release(frame.allocator);
    }

    r.frames.clear();
}

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

void Teardown() {
    std::lock_guard<std::mutex> lock(g_rendererGate);
    Renderer& r = g_renderer;

    if (!r.ready && !r.failed) return;

    WaitForGpu();

    if (ImGui::GetCurrentContext() != nullptr) {
        // The backend frees the GPU side of our textures as it shuts down; their CPU side
        // is ours to free after that and before the context they are registered with goes.
        ImGui_ImplDX12_Shutdown();
        ImGui_ImplWin32_Shutdown();
        ReleaseAllTextures();
        ImGui::DestroyContext();
    }

    ReleaseFrames();
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
    r.ready = false;
    r.failed = false;
}

// Builds everything that depends on the swap chain. Returns false once and then stays
// false: a renderer that cannot be set up will not become able to on the next frame, and
// retrying every frame would fill the log at sixty lines a second.
bool Prepare(IDXGISwapChain3* swapChain) {
    Renderer& r = g_renderer;

    if (r.ready) return true;
    if (r.failed) return false;

    ID3D12CommandQueue* queue = g_queue.load(std::memory_order_acquire);
    if (queue == nullptr) {
        // Not a failure yet: the queue arrives from ExecuteCommandLists or a resize, and
        // one of those will happen shortly.
        return false;
    }

    DXGI_SWAP_CHAIN_DESC desc{};
    if (FAILED(swapChain->GetDesc(&desc))) {
        LogLine("Could not read the swap chain description.");
        r.failed = true;
        return false;
    }

    if (FAILED(swapChain->GetDevice(__uuidof(ID3D12Device), reinterpret_cast<void**>(&r.device)))) {
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

    UINT buffers = desc.BufferCount == 0 ? 2 : desc.BufferCount;

    D3D12_DESCRIPTOR_HEAP_DESC rtvDesc{};
    rtvDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
    rtvDesc.NumDescriptors = buffers;
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

    r.frames.resize(buffers);

    for (UINT i = 0; i < buffers; ++i) {
        Frame& frame = r.frames[i];
        frame.rtv.ptr = rtvStart.ptr + i * rtvStride;

        if (FAILED(swapChain->GetBuffer(i, __uuidof(ID3D12Resource), reinterpret_cast<void**>(&frame.backBuffer)))) {
            LogFormat("Could not get back buffer %u.", i);
            r.failed = true;
            return false;
        }

        r.device->CreateRenderTargetView(frame.backBuffer, nullptr, frame.rtv);

        if (FAILED(r.device->CreateCommandAllocator(
                D3D12_COMMAND_LIST_TYPE_DIRECT,
                __uuidof(ID3D12CommandAllocator),
                reinterpret_cast<void**>(&frame.allocator)))) {
            LogLine("Could not create a command allocator.");
            r.failed = true;
            return false;
        }
    }

    if (r.commandList == nullptr) {
        if (FAILED(r.device->CreateCommandList(
                0,
                D3D12_COMMAND_LIST_TYPE_DIRECT,
                r.frames[0].allocator,
                nullptr,
                __uuidof(ID3D12GraphicsCommandList),
                reinterpret_cast<void**>(&r.commandList)))) {
            LogLine("Could not create a command list.");
            r.failed = true;
            return false;
        }

        r.commandList->Close();
    }

    if (r.fence == nullptr) {
        if (FAILED(r.device->CreateFence(0, D3D12_FENCE_FLAG_NONE, __uuidof(ID3D12Fence), reinterpret_cast<void**>(&r.fence)))) {
            LogLine("Could not create a fence.");
            r.failed = true;
            return false;
        }

        r.fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    }

    // The window comes from the swap chain rather than from a search, so a client with
    // more than one window cannot be got wrong.
    if (r.window == nullptr) {
        DXGI_SWAP_CHAIN_DESC1 desc1{};
        if (SUCCEEDED(swapChain->GetDesc1(&desc1)))
            swapChain->GetHwnd(&r.window);

        if (r.window == nullptr)
            r.window = desc.OutputWindow;
    }

    if (ImGui::GetCurrentContext() == nullptr) {
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

        ImGui_ImplDX12_InitInfo info{};
        info.Device = r.device;
        info.CommandQueue = queue;
        info.NumFramesInFlight = static_cast<int>(buffers);

        // Read from the swap chain rather than assumed: the back buffer format is a cvar
        // in this client and can be a float format.
        info.RTVFormat = desc.BufferDesc.Format;

        // No depth buffer is bound, so the pipeline state must be built without one or
        // the draw is invalid.
        info.DSVFormat = DXGI_FORMAT_UNKNOWN;

        info.SrvDescriptorHeap = r.srvHeap;
        info.SrvDescriptorAllocFn = &AllocateSrv;
        info.SrvDescriptorFreeFn = &FreeSrv;

        if (!ImGui_ImplDX12_Init(&info)) {
            LogLine("The D3D12 backend refused to initialise.");
            r.failed = true;
            return false;
        }

        r.originalWndProc = reinterpret_cast<WNDPROC>(
            SetWindowLongPtrW(r.window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&OverlayWndProc)));

        LogFormat(
            "Renderer ready: %u buffers, format %d, window 0x%p.",
            buffers,
            static_cast<int>(desc.BufferDesc.Format),
            static_cast<void*>(r.window));
    }

    r.ready = true;
    return true;
}

// ---------------------------------------------------------------- drawing

void DrawFrame(IDXGISwapChain3* swapChain) {
    std::lock_guard<std::mutex> lock(g_rendererGate);

    if (!Prepare(swapChain))
        return;

    Renderer& r = g_renderer;

    std::call_once(g_ipcOnce, [] {
        g_ipc = new (std::nothrow) Ipc();
        if (g_ipc != nullptr) {
            g_ipc->Start();
            LogLine("Listening for the host on the overlay pipe.");
        }
    });

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

        // The keys the host wants held, unless it has gone quiet - then none.
        std::vector<uint16_t> wanted;
        uint64_t age_ms = 0;
        g_ipc->WantedKeys(wanted, age_ms);
        if (!g_ipc->Connected() || age_ms > kHeldKeysStaleMs)
            wanted.clear();
        Keys().Apply(wanted);
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

    UINT index = swapChain->GetCurrentBackBufferIndex();
    if (index >= r.frames.size())
        return;

    Frame& frame = r.frames[index];

    // The backend reuses its vertex and index buffers with no fence of its own, so
    // without this wait a frame can overwrite geometry the GPU is still reading.
    if (r.fence->GetCompletedValue() < frame.fenceValue && r.fenceEvent != nullptr) {
        if (SUCCEEDED(r.fence->SetEventOnCompletion(frame.fenceValue, r.fenceEvent)))
            WaitForSingleObject(r.fenceEvent, 1000);
    }

    if (FAILED(frame.allocator->Reset()))
        return;

    if (FAILED(r.commandList->Reset(frame.allocator, nullptr)))
        return;

    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Flags = D3D12_RESOURCE_BARRIER_FLAG_NONE;
    barrier.Transition.pResource = frame.backBuffer;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;

    // The game has just finished with this buffer and is about to present it, so it is in
    // the present state. Getting this wrong is not a visual artefact: the runtime removes
    // the device on a barrier state mismatch, which ends the game.
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
    r.commandList->ResourceBarrier(1, &barrier);

    r.commandList->OMSetRenderTargets(1, &frame.rtv, FALSE, nullptr);
    r.commandList->SetDescriptorHeaps(1, &r.srvHeap);

    ImGui_ImplDX12_RenderDrawData(ImGui::GetDrawData(), r.commandList);

    // Frees textures the backend has now finished destroying - images the host replaced.
    CollectRetiredTextures();

    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
    r.commandList->ResourceBarrier(1, &barrier);

    if (FAILED(r.commandList->Close()))
        return;

    ID3D12CommandQueue* queue = g_queue.load(std::memory_order_acquire);
    if (queue == nullptr)
        return;

    ID3D12CommandList* lists[] = { r.commandList };
    queue->ExecuteCommandLists(1, lists);

    frame.fenceValue = ++r.fenceValue;
    queue->Signal(r.fence, frame.fenceValue);
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
    NotePresentPath("Present");

    IDXGISwapChain3* chain3 = nullptr;
    if (SUCCEEDED(swapChain->QueryInterface(__uuidof(IDXGISwapChain3), reinterpret_cast<void**>(&chain3)))) {
        DrawFrame(chain3);
        chain3->Release();
    }

    return g_originalPresent(swapChain, syncInterval, flags);
}

HRESULT STDMETHODCALLTYPE Present1Detour(
    IDXGISwapChain1* swapChain,
    UINT syncInterval,
    UINT flags,
    const DXGI_PRESENT_PARAMETERS* parameters) {
    InFlight guard;
    NotePresentPath("Present1");

    IDXGISwapChain3* chain3 = nullptr;
    if (SUCCEEDED(swapChain->QueryInterface(__uuidof(IDXGISwapChain3), reinterpret_cast<void**>(&chain3)))) {
        DrawFrame(chain3);
        chain3->Release();
    }

    return g_originalPresent1(swapChain, syncInterval, flags, parameters);
}

HRESULT STDMETHODCALLTYPE ResizeBuffersDetour(
    IDXGISwapChain* swapChain,
    UINT bufferCount,
    UINT width,
    UINT height,
    DXGI_FORMAT format,
    UINT flags) {
    InFlight guard;

    {
        std::lock_guard<std::mutex> lock(g_rendererGate);
        WaitForGpu();
        ReleaseFrames();
        g_renderer.ready = false;
    }

    return g_originalResizeBuffers(swapChain, bufferCount, width, height, format, flags);
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

            // Not released: the pointer is kept for the life of the session, and the
            // reference is what stops it going away underneath us.
        }
    }

    {
        std::lock_guard<std::mutex> lock(g_rendererGate);
        WaitForGpu();
        ReleaseFrames();
        g_renderer.ready = false;
    }

    return g_originalResizeBuffers1(swapChain, bufferCount, width, height, format, flags, nodeMasks, presentQueues);
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

    // The window procedure goes first, and before the drain, because it is the entry
    // point most likely to be executing: the client pumps messages continuously, and a
    // call already inside it when the DLL is unmapped is an access violation rather than
    // a missed frame.
    {
        std::lock_guard<std::mutex> lock(g_rendererGate);
        Renderer& r = g_renderer;

        // Nothing stays held down once the overlay has gone.
        Keys().ReleaseAll();

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
