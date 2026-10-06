// The overlay's real D3D12 path, in a window of this test's own: hooks.cpp, the ImGui backends
// and the drawing code, built into this program and hooked into its own swap chain as they are
// into AC:Unreal's, with a stand-in for the host on the overlay's pipe. Built and run by
// native\test-render.ps1.
//
// The stand-in publishes what the live session had: art for Decal's bar, sent as images, and a
// hudified VVS window - a HUD of Virindi Tank's - placed where Virindi HUDs left it. The ini
// the overlay starts from has that HUD saved at -5,-20, as a minimize left it on 2026-10-05.
//
// The test then does to its swap chain what Unreal does to the game's, and after each step reads
// back what was presented and counts the pixels the overlay drew:
//   - minimizing: the window goes iconic, its client area 0 x 0, and Unreal resizes its back
//     buffers to 8 x 8 (Slate's least viewport) and presents a frame or two;
//   - restoring: the window comes back and the buffers go back to its size;
//   - both again within a moment, as a double click on the taskbar does;
//   - a resize, as dragging the frame or moving to a monitor of another scale does;
//   - a new back-buffer format, as turning HDR on does;
//   - minimized with no frame at all, as the game was for 43 minutes;
//   - a new swap chain for the window.
// After every one the overlay must draw what it drew at first, the HUD must be where the host
// put it, the device must still be there, and - where the D3D12 debug layer can be had - the
// layer must have nothing to say.
//
// The swap chain is flip-sequential, which keeps a presented buffer's pixels until it comes
// round again; what the overlay drew into it is read back then, before this test clears it.
// Nothing here goes near the game: the overlay's pipe has a name of its own in this build
// (OVERLAY_TEST_PIPE), and the window is placed off every screen and never activated.

#include <windows.h>

#include <d3d12.h>
#include <dxgi1_4.h>

#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "imgui.h"
#include "imgui_internal.h"

#include "hooks.h"
#include "log.h"

// The Agility SDK the game ships, when test-render.ps1 found it and put it beside this program:
// it carries the debug layer, which a Windows without Graphics Tools has not. Where Windows' own
// runtime is newer, it is the one used, and the test runs without the layer.
extern "C" {
__declspec(dllexport) extern const UINT D3D12SDKVersion = 618;
__declspec(dllexport) extern const char* D3D12SDKPath = ".\\D3D12\\";
}

namespace {

int g_failures = 0;
int g_checks = 0;

#define CHECK(cond)                                                   \
    do {                                                              \
        ++g_checks;                                                   \
        if (!(cond)) {                                                \
            std::printf("FAIL line %d: %s\n", __LINE__, #cond);       \
            ++g_failures;                                             \
        }                                                             \
    } while (0)

template <typename T>
void Release(T*& object) {
    if (object != nullptr) {
        object->Release();
        object = nullptr;
    }
}

// The clear colour, and so the colour of every pixel the overlay did not draw.
constexpr float kClear[4] = {0.10f, 0.20f, 0.30f, 1.0f};

// The HUD's body is black and 190 x 140; Decal's bar adds its art. A frame with less than this
// has lost most of the overlay.
constexpr size_t kDrawnAtLeast = 20000;

// Where the host says the HUD was left, and the ini's corrupted place for it.
constexpr int kHudX = 283;
constexpr int kHudY = 103;
const char* const kHud = "###decal:Tank/status";

// ---------------------------------------------------------------- the host's stand-in

const wchar_t* const kPipe = L"\\\\.\\pipe\\achost-overlay-render-test";
std::atomic<bool> g_hostStopping{false};
std::atomic<bool> g_hostConnected{false};

std::string Base64(const std::vector<unsigned char>& bytes) {
    static const char* alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    size_t i = 0;
    for (; i + 2 < bytes.size(); i += 3) {
        const uint32_t n = (bytes[i] << 16) | (bytes[i + 1] << 8) | bytes[i + 2];
        out += alphabet[(n >> 18) & 63];
        out += alphabet[(n >> 12) & 63];
        out += alphabet[(n >> 6) & 63];
        out += alphabet[n & 63];
    }
    if (i + 1 == bytes.size()) {
        const uint32_t n = bytes[i] << 16;
        out += alphabet[(n >> 18) & 63];
        out += alphabet[(n >> 12) & 63];
        out += "==";
    } else if (i + 2 == bytes.size()) {
        const uint32_t n = (bytes[i] << 16) | (bytes[i + 1] << 8);
        out += alphabet[(n >> 18) & 63];
        out += alphabet[(n >> 12) & 63];
        out += alphabet[(n >> 6) & 63];
        out += '=';
    }
    return out;
}

bool Send(HANDLE pipe, const std::string& json) {
    const uint32_t length = static_cast<uint32_t>(json.size());
    std::string framed(4, '\0');
    for (int i = 0; i < 4; ++i) framed[static_cast<size_t>(i)] = static_cast<char>((length >> (8 * i)) & 0xFF);
    framed += json;
    DWORD written = 0;
    return WriteFile(pipe, framed.data(), static_cast<DWORD>(framed.size()), &written, nullptr) != FALSE && written == framed.size();
}

// A plain 16 x 16 square of one colour, as art for one of Decal's bar's buttons.
std::string ImageFrame(const char* key, unsigned char r, unsigned char g, unsigned char b) {
    std::vector<unsigned char> rgba(16 * 16 * 4);
    for (size_t i = 0; i < rgba.size(); i += 4) {
        rgba[i] = r;
        rgba[i + 1] = g;
        rgba[i + 2] = b;
        rgba[i + 3] = 0xFF;
    }
    return std::string("{\"image\":{\"key\":\"") + key + "\",\"width\":16,\"height\":16,\"rgba\":\"" + Base64(rgba) + "\"}}";
}

std::string StateFrame(int64_t revision) {
    const int64_t now = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
    return "{\"revision\":" + std::to_string(revision) + ",\"published_ms\":" + std::to_string(now) +
           ",\"windows\":[{\"owner\":\"Tank/status\",\"title\":\"Status\",\"view\":{\"title\":\"Status\",\"bar\":\"vvs\","
           "\"theme\":\"Decal\",\"ghosted\":true,\"resizeable\":false,\"show_in_bar\":false,\"minimizable\":false,"
           "\"width\":190,\"height\":140,\"x\":" + std::to_string(kHudX) + ",\"y\":" + std::to_string(kHudY) +
           ",\"root\":{\"type\":\"fixed\"}}}]}";
}

// Waits for the overlay, sends it the art and the windows, and goes on publishing every half a
// second - and reading what it sends back, so its writes never back up.
void Host() {
    while (!g_hostStopping.load()) {
        HANDLE pipe = CreateNamedPipeW(kPipe, PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_NOWAIT, 1, 1 << 20, 1 << 20, 0, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) return;

        // Non-blocking, so the test can stop it: connected once ConnectNamedPipe says so.
        bool connected = false;
        while (!g_hostStopping.load() && !connected) {
            connected = ConnectNamedPipe(pipe, nullptr) != FALSE || GetLastError() == ERROR_PIPE_CONNECTED;
            if (!connected) Sleep(20);
        }

        if (connected) {
            DWORD mode = PIPE_READMODE_BYTE | PIPE_WAIT;
            SetNamedPipeHandleState(pipe, &mode, nullptr, nullptr);
            bool ok = Send(pipe, ImageFrame("portal:06005E64", 0xC0, 0x90, 0x30)) && Send(pipe, ImageFrame("portal:06005E65", 0xC0, 0x90, 0x30)) &&
                      Send(pipe, ImageFrame("portal:060012AA", 0x80, 0x80, 0x80)) && Send(pipe, ImageFrame("portal:060012A9", 0x80, 0x80, 0x80));
            g_hostConnected.store(ok);
            for (int64_t revision = 1; ok && !g_hostStopping.load(); ++revision) {
                ok = Send(pipe, StateFrame(revision));
                for (int i = 0; ok && i < 25 && !g_hostStopping.load(); ++i) {
                    DWORD waiting = 0;
                    if (PeekNamedPipe(pipe, nullptr, 0, nullptr, &waiting, nullptr) == FALSE) {
                        ok = false;
                        break;
                    }
                    if (waiting > 0) {
                        std::vector<char> drain(waiting);
                        DWORD got = 0;
                        ReadFile(pipe, drain.data(), waiting, &got, nullptr);
                    }
                    Sleep(20);
                }
            }
            g_hostConnected.store(false);
        }

        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
    }
}

// ---------------------------------------------------------------- the game's stand-in

struct Gpu {
    HWND window = nullptr;
    ID3D12Device* device = nullptr;
    ID3D12CommandQueue* queue = nullptr;
    ID3D12CommandAllocator* allocator = nullptr;
    ID3D12GraphicsCommandList* list = nullptr;
    ID3D12DescriptorHeap* rtvHeap = nullptr;
    ID3D12Fence* fence = nullptr;
    HANDLE fenceEvent = nullptr;
    UINT64 fenceValue = 0;
    IDXGIFactory2* factory = nullptr;
    IDXGISwapChain3* chain = nullptr;
    ID3D12Resource* readback = nullptr;
    UINT64 readbackSize = 0;
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{};
    UINT width = 800;
    UINT height = 600;
    DXGI_FORMAT format = DXGI_FORMAT_B8G8R8A8_UNORM;
    bool debugLayer = false;

    // Shader-visible descriptor heaps as Unreal keeps them: a large one bound for every frame
    // (D3D12.Bindless.ResourceDescriptorHeapSize is 32768 in AC:Unreal), and others made again
    // when the back buffers are resized. They share the GPU's descriptor space with the
    // overlay's, and move its heaps about in it.
    ID3D12DescriptorHeap* gameHeap = nullptr;
    std::vector<ID3D12DescriptorHeap*> churn;
};

Gpu g;

LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    return DefWindowProcW(window, message, wParam, lParam);
}

void Pump() {
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
}

void WaitIdle() {
    const UINT64 target = ++g.fenceValue;
    g.queue->Signal(g.fence, target);
    if (g.fence->GetCompletedValue() < target) {
        g.fence->SetEventOnCompletion(target, g.fenceEvent);
        WaitForSingleObject(g.fenceEvent, 5000);
    }
}

// The window's outer size for a client area of width x height.
SIZE OuterSize(UINT width, UINT height) {
    RECT rect{0, 0, static_cast<LONG>(width), static_cast<LONG>(height)};
    AdjustWindowRectEx(&rect, WS_OVERLAPPEDWINDOW, FALSE, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    return SIZE{rect.right - rect.left, rect.bottom - rect.top};
}

void MakeReadback() {
    Release(g.readback);

    D3D12_RESOURCE_DESC buffer{};
    buffer.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    buffer.Width = g.width;
    buffer.Height = g.height;
    buffer.DepthOrArraySize = 1;
    buffer.MipLevels = 1;
    buffer.Format = g.format;
    buffer.SampleDesc.Count = 1;
    g.device->GetCopyableFootprints(&buffer, 0, 1, 0, &g.footprint, nullptr, nullptr, &g.readbackSize);

    D3D12_HEAP_PROPERTIES heap{};
    heap.Type = D3D12_HEAP_TYPE_READBACK;
    D3D12_RESOURCE_DESC desc{};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    desc.Width = g.readbackSize;
    desc.Height = 1;
    desc.DepthOrArraySize = 1;
    desc.MipLevels = 1;
    desc.SampleDesc.Count = 1;
    desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    g.device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc, D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                      __uuidof(ID3D12Resource), reinterpret_cast<void**>(&g.readback));
}

// One frame as a game draws one: clear the back buffer, present it. With `capture`, what the
// buffer held before the clear - the frame presented when it last came round, overlay and all -
// is copied out first.
void Frame(bool capture = false) {
    WaitIdle();

    const UINT index = g.chain->GetCurrentBackBufferIndex();
    ID3D12Resource* buffer = nullptr;
    if (FAILED(g.chain->GetBuffer(index, __uuidof(ID3D12Resource), reinterpret_cast<void**>(&buffer)))) {
        std::printf("FAIL: no back buffer %u\n", index);
        ++g_failures;
        return;
    }

    g.allocator->Reset();
    g.list->Reset(g.allocator, nullptr);

    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = buffer;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;

    if (capture && g.readback != nullptr) {
        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
        barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
        g.list->ResourceBarrier(1, &barrier);

        D3D12_TEXTURE_COPY_LOCATION from{};
        from.pResource = buffer;
        from.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        from.SubresourceIndex = 0;
        D3D12_TEXTURE_COPY_LOCATION to{};
        to.pResource = g.readback;
        to.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        to.PlacedFootprint = g.footprint;
        g.list->CopyTextureRegion(&to, 0, 0, 0, &from, nullptr);

        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
    } else {
        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
    }
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
    g.list->ResourceBarrier(1, &barrier);

    const D3D12_CPU_DESCRIPTOR_HANDLE rtv = g.rtvHeap->GetCPUDescriptorHandleForHeapStart();
    g.device->CreateRenderTargetView(buffer, nullptr, rtv);
    if (g.gameHeap != nullptr) g.list->SetDescriptorHeaps(1, &g.gameHeap);
    g.list->ClearRenderTargetView(rtv, kClear, 0, nullptr);

    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
    g.list->ResourceBarrier(1, &barrier);
    g.list->Close();

    ID3D12CommandList* lists[] = {g.list};
    g.queue->ExecuteCommandLists(1, lists);
    WaitIdle();
    buffer->Release();

    // Through the vtable, as the game does: this is the call hooks.cpp has hooked.
    g.chain->Present(0, 0);
    Pump();
}

void Frames(int count) {
    for (int i = 0; i < count; ++i) Frame();
}

// How many pixels of the frame presented when the current back buffer last came round are not
// the clear colour: what the overlay drew. The clear colour is read from the bottom-right
// corner, where the overlay draws nothing, so any 32-bit format compares the same way.
size_t DrawnPixels() {
    // Enough frames that every buffer has been presented with the overlay in it.
    Frames(6);
    Frame(true);
    WaitIdle();

    void* mapped = nullptr;
    D3D12_RANGE range{0, static_cast<SIZE_T>(g.readbackSize)};
    if (FAILED(g.readback->Map(0, &range, &mapped)) || mapped == nullptr) return 0;

    const auto* bytes = static_cast<const uint8_t*>(mapped) + g.footprint.Offset;
    const UINT pitch = g.footprint.Footprint.RowPitch;
    uint32_t clear = 0;
    std::memcpy(&clear, bytes + static_cast<size_t>(g.height - 1) * pitch + static_cast<size_t>(g.width - 1) * 4, 4);

    size_t drawn = 0;
    for (UINT y = 0; y < g.height; ++y) {
        const uint8_t* row = bytes + static_cast<size_t>(y) * pitch;
        for (UINT x = 0; x < g.width; ++x) {
            uint32_t pixel = 0;
            std::memcpy(&pixel, row + static_cast<size_t>(x) * 4, 4);
            if (pixel != clear) ++drawn;
        }
    }

    D3D12_RANGE none{0, 0};
    g.readback->Unmap(0, &none);
    return drawn;
}

// Where the overlay has the HUD now: read from its ImGui context, which is this program's own.
ImVec2 HudAt() {
    if (ImGui::GetCurrentContext() == nullptr) return ImVec2(NAN, NAN);
    ImGuiWindow* window = ImGui::FindWindowByName(kHud);
    return window != nullptr ? window->Pos : ImVec2(NAN, NAN);
}

// Whether the HUD is where the host's stand-in says Virindi HUDs left it.
bool HudWhereTheHostPutIt() {
    const ImVec2 at = HudAt();
    return std::fabs(at.x - static_cast<float>(kHudX)) < 0.5f && std::fabs(at.y - static_cast<float>(kHudY)) < 0.5f;
}

// What Unreal's FD3D12Viewport does on a resize: let go of its back buffers - it holds none
// between frames here - and resize them, through ResizeBuffers1 with its queue, or
// ResizeBuffers.
// Unreal's descriptor heaps for what it draws at the new size, made again: the old let go, new
// ones made - of a size that differs each time, so they do not simply take the old ones' places.
void ChurnHeaps() {
    static UINT round = 0;
    for (ID3D12DescriptorHeap*& heap : g.churn) Release(heap);
    g.churn.clear();
    for (UINT i = 0; i < 3; ++i) {
        D3D12_DESCRIPTOR_HEAP_DESC desc{};
        desc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        desc.NumDescriptors = 512 + 256 * ((round + i) % 5);
        desc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        ID3D12DescriptorHeap* heap = nullptr;
        if (SUCCEEDED(g.device->CreateDescriptorHeap(&desc, __uuidof(ID3D12DescriptorHeap), reinterpret_cast<void**>(&heap))))
            g.churn.push_back(heap);
    }
    ++round;
}

bool ResizeBuffers(UINT width, UINT height, DXGI_FORMAT format, bool one) {
    WaitIdle();
    ChurnHeaps();
    HRESULT result = E_FAIL;
    if (one) {
        UINT nodes[3] = {0, 0, 0};
        IUnknown* queues[3] = {g.queue, g.queue, g.queue};
        result = g.chain->ResizeBuffers1(0, width, height, format, 0, nodes, queues);
    } else {
        result = g.chain->ResizeBuffers(0, width, height, format, 0);
    }
    if (FAILED(result)) {
        std::printf("FAIL: ResizeBuffers%s(%u x %u) gave 0x%08lX\n", one ? "1" : "", width, height,
                    static_cast<unsigned long>(result));
        ++g_failures;
        return false;
    }
    if (format != DXGI_FORMAT_UNKNOWN) g.format = format;
    return true;
}

// Minimized as AC:Unreal is: iconic, a 0 x 0 client area, back buffers of 8 x 8, and - with
// `presents` - a frame or two presented before Unreal stops.
void Minimize(bool one, int presents = 3) {
    ShowWindow(g.window, SW_SHOWMINNOACTIVE);
    Pump();
    ResizeBuffers(8, 8, DXGI_FORMAT_UNKNOWN, one);
    Frames(presents);
}

// Restored as AC:Unreal is: shown, a frame presented while the back buffers are still 8 x 8,
// then resized back to the window.
void Restore(bool one) {
    ShowWindow(g.window, SW_SHOWNOACTIVATE);
    Pump();
    Frames(1);
    ResizeBuffers(g.width, g.height, DXGI_FORMAT_UNKNOWN, one);
    Frames(2);
}

void Resize(UINT width, UINT height) {
    g.width = width;
    g.height = height;
    const SIZE outer = OuterSize(width, height);
    SetWindowPos(g.window, nullptr, 0, 0, outer.cx, outer.cy, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    Pump();
    ResizeBuffers(width, height, DXGI_FORMAT_UNKNOWN, true);
    MakeReadback();
}

// A swap chain made afresh for the same window, as an engine may when its display changes: the
// old one let go of first, as flip-model swap chains must be, one to a window.
bool NewSwapChain() {
    WaitIdle();
    Release(g.chain);

    DXGI_SWAP_CHAIN_DESC1 chain{};
    chain.Width = g.width;
    chain.Height = g.height;
    chain.Format = g.format;
    chain.SampleDesc.Count = 1;
    chain.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    chain.BufferCount = 3;
    chain.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    IDXGISwapChain1* chain1 = nullptr;
    const HRESULT made = g.factory->CreateSwapChainForHwnd(g.queue, g.window, &chain, nullptr, nullptr, &chain1);
    if (FAILED(made)) {
        std::printf("   a new swap chain for the window was refused: 0x%08lX\n", static_cast<unsigned long>(made));
        return false;
    }
    chain1->QueryInterface(__uuidof(IDXGISwapChain3), reinterpret_cast<void**>(&g.chain));
    chain1->Release();
    MakeReadback();
    return g.chain != nullptr;
}

bool DeviceAlive() {
    const HRESULT removed = g.device->GetDeviceRemovedReason();
    if (removed != S_OK) std::printf("   the device was removed: 0x%08lX\n", static_cast<unsigned long>(removed));
    return removed == S_OK;
}

// The debug layer's errors since the last call, printed, and how many.
size_t DebugErrors() {
    if (!g.debugLayer) return 0;
    ID3D12InfoQueue* info = nullptr;
    if (FAILED(g.device->QueryInterface(__uuidof(ID3D12InfoQueue), reinterpret_cast<void**>(&info)))) return 0;

    size_t errors = 0;
    const UINT64 count = info->GetNumStoredMessages();
    for (UINT64 i = 0; i < count; ++i) {
        SIZE_T length = 0;
        info->GetMessage(i, nullptr, &length);
        std::vector<char> storage(length);
        auto* message = reinterpret_cast<D3D12_MESSAGE*>(storage.data());
        if (FAILED(info->GetMessage(i, message, &length))) continue;
        if (message->Severity != D3D12_MESSAGE_SEVERITY_ERROR && message->Severity != D3D12_MESSAGE_SEVERITY_CORRUPTION) continue;
        if (++errors <= 3) std::printf("   debug layer: %.300s\n", message->pDescription);
    }
    info->ClearStoredMessages();
    info->Release();
    if (errors > 3) std::printf("   ... and %zu more from the debug layer\n", errors - 3);
    return errors;
}

bool Start(bool warp) {
    ID3D12Debug* debug = nullptr;
    if (SUCCEEDED(D3D12GetDebugInterface(__uuidof(ID3D12Debug), reinterpret_cast<void**>(&debug)))) {
        debug->EnableDebugLayer();
        debug->Release();
        g.debugLayer = true;
    }

    if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory2), reinterpret_cast<void**>(&g.factory)))) return false;

    IDXGIAdapter* adapter = nullptr;
    if (warp) {
        IDXGIFactory4* factory4 = nullptr;
        if (SUCCEEDED(g.factory->QueryInterface(__uuidof(IDXGIFactory4), reinterpret_cast<void**>(&factory4)))) {
            factory4->EnumWarpAdapter(__uuidof(IDXGIAdapter), reinterpret_cast<void**>(&adapter));
            factory4->Release();
        }
        if (adapter == nullptr) return false;
    }
    const HRESULT made = D3D12CreateDevice(adapter, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device), reinterpret_cast<void**>(&g.device));
    if (adapter != nullptr) adapter->Release();
    if (FAILED(made)) return false;

    if (g.debugLayer) {
        ID3D12InfoQueue* info = nullptr;
        if (SUCCEEDED(g.device->QueryInterface(__uuidof(ID3D12InfoQueue), reinterpret_cast<void**>(&info)))) {
            info->SetMessageCountLimit(4096);
            info->Release();
        } else {
            g.debugLayer = false;
        }
    }

    D3D12_COMMAND_QUEUE_DESC queue{};
    queue.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    if (FAILED(g.device->CreateCommandQueue(&queue, __uuidof(ID3D12CommandQueue), reinterpret_cast<void**>(&g.queue)))) return false;
    if (FAILED(g.device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, __uuidof(ID3D12CommandAllocator),
                                                reinterpret_cast<void**>(&g.allocator))))
        return false;
    if (FAILED(g.device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, g.allocator, nullptr, __uuidof(ID3D12GraphicsCommandList),
                                           reinterpret_cast<void**>(&g.list))))
        return false;
    g.list->Close();

    D3D12_DESCRIPTOR_HEAP_DESC rtv{};
    rtv.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
    rtv.NumDescriptors = 1;
    if (FAILED(g.device->CreateDescriptorHeap(&rtv, __uuidof(ID3D12DescriptorHeap), reinterpret_cast<void**>(&g.rtvHeap)))) return false;
    if (FAILED(g.device->CreateFence(0, D3D12_FENCE_FLAG_NONE, __uuidof(ID3D12Fence), reinterpret_cast<void**>(&g.fence)))) return false;
    g.fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);

    D3D12_DESCRIPTOR_HEAP_DESC bindless{};
    bindless.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
    bindless.NumDescriptors = 32768;
    bindless.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    if (FAILED(g.device->CreateDescriptorHeap(&bindless, __uuidof(ID3D12DescriptorHeap), reinterpret_cast<void**>(&g.gameHeap)))) return false;

    // Off every screen, never activated, and off the taskbar: nothing the player could see or
    // click, and nothing that takes the keyboard from them.
    WNDCLASSEXW windowClass{};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.lpfnWndProc = &WindowProc;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"ACUnrealOverlayRenderTest";
    RegisterClassExW(&windowClass);
    const SIZE outer = OuterSize(g.width, g.height);
    const int x = GetSystemMetrics(SM_XVIRTUALSCREEN) - outer.cx - 200;
    g.window = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, windowClass.lpszClassName, L"overlay render test", WS_OVERLAPPEDWINDOW,
                               x, 0, outer.cx, outer.cy, nullptr, nullptr, windowClass.hInstance, nullptr);
    if (g.window == nullptr) return false;
    ShowWindow(g.window, SW_SHOWNOACTIVATE);
    Pump();

    DXGI_SWAP_CHAIN_DESC1 chain{};
    chain.Width = g.width;
    chain.Height = g.height;
    chain.Format = g.format;
    chain.SampleDesc.Count = 1;
    chain.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    chain.BufferCount = 3;
    chain.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    IDXGISwapChain1* chain1 = nullptr;
    if (FAILED(g.factory->CreateSwapChainForHwnd(g.queue, g.window, &chain, nullptr, nullptr, &chain1))) return false;
    const HRESULT three = chain1->QueryInterface(__uuidof(IDXGISwapChain3), reinterpret_cast<void**>(&g.chain));
    chain1->Release();
    if (FAILED(three)) return false;

    MakeReadback();
    return true;
}

void Stop() {
    if (g.queue != nullptr && g.fence != nullptr) WaitIdle();
    for (ID3D12DescriptorHeap*& heap : g.churn) Release(heap);
    g.churn.clear();
    Release(g.gameHeap);
    Release(g.readback);
    Release(g.chain);
    Release(g.factory);
    Release(g.fence);
    Release(g.rtvHeap);
    Release(g.list);
    Release(g.allocator);
    Release(g.queue);
    Release(g.device);
    if (g.fenceEvent != nullptr) CloseHandle(g.fenceEvent);
    if (g.window != nullptr) DestroyWindow(g.window);
}

// The ini the overlay starts from: the HUD where the minimize of 2026-10-05 saved it, and
// nothing else, so no run before this one moves anything.
void StartFromTheLiveIni() {
    const std::wstring folder = overlay::DllDirectory();
    if (folder.empty()) return;
    const char* ini =
        "[Window][decal:Tank/status]\r\nPos=-5,-20\r\nSize=190,160\r\nLastUsed=20261005\r\n\r\n"
        "[VVSView][Tank/status]\r\nStuck=\r\n\r\n";
    HANDLE file = CreateFileW((folder + L"ACUnrealOverlay.ini").c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    WriteFile(file, ini, static_cast<DWORD>(std::strlen(ini)), &written, nullptr);
    CloseHandle(file);
}

}  // namespace

int main(int argc, char** argv) {
    bool warp = false;
    for (int i = 1; i < argc; ++i)
        if (std::strcmp(argv[i], "--warp") == 0) warp = true;

    StartFromTheLiveIni();
    overlay::OpenLog();
    std::thread host(&Host);

    if (!Start(warp)) {
        std::printf("Could not set up D3D12 on the %s adapter; nothing was tested.\n", warp ? "WARP" : "default");
        g_hostStopping.store(true);
        host.join();
        Stop();
        return 2;
    }

    DXGI_ADAPTER_DESC adapter{};
    {
        IDXGIFactory4* factory4 = nullptr;
        if (SUCCEEDED(g.factory->QueryInterface(__uuidof(IDXGIFactory4), reinterpret_cast<void**>(&factory4)))) {
            IDXGIAdapter* found = nullptr;
            if (SUCCEEDED(factory4->EnumAdapterByLuid(g.device->GetAdapterLuid(), __uuidof(IDXGIAdapter), reinterpret_cast<void**>(&found)))) {
                found->GetDesc(&adapter);
                found->Release();
            }
            factory4->Release();
        }
    }
    std::printf("Render test on %ls, the D3D12 debug layer %s.\n", adapter.Description, g.debugLayer ? "on" : "not available");

    if (!overlay::hooks::Install()) {
        std::printf("FAIL: the hooks would not install in this process.\n");
        g_hostStopping.store(true);
        host.join();
        Stop();
        return 1;
    }

    // 1. Drawn at all, once the host's stand-in has been heard: the HUD where the host put it,
    // not where the ini had it.
    for (int i = 0; i < 400 && !(g_hostConnected.load() && !std::isnan(HudAt().x)); ++i) {
        Frame();
        Sleep(10);
    }
    CHECK(g_hostConnected.load());
    size_t drawn = DrawnPixels();
    const ImVec2 started = HudAt();
    std::printf("1. first frames: the overlay drew %zu pixels; the HUD at %.0f,%.0f, which the ini had at -5,-20\n", drawn, started.x, started.y);
    CHECK(drawn >= kDrawnAtLeast);
    CHECK(HudWhereTheHostPutIt());
    const size_t first = drawn;

    // Wherever that left it, the HUD starts what follows where the host put it, so a minimize
    // that moves it is seen as such.
    if (!HudWhereTheHostPutIt()) {
        ImGui::SetWindowPos(kHud, ImVec2(static_cast<float>(kHudX), static_cast<float>(kHudY)));
        Frames(2);
    }

    // 2. Minimized and restored as AC:Unreal is, through ResizeBuffers1 as Unreal 5.8 resizes.
    Minimize(true);
    Restore(true);
    drawn = DrawnPixels();
    std::printf("2. minimized to 8 x 8 and restored: %zu pixels; the HUD at %.0f,%.0f\n", drawn, HudAt().x, HudAt().y);
    CHECK(drawn == first);
    CHECK(HudWhereTheHostPutIt());

    // 3. Restored, minimized again at once and restored again - a double click on the taskbar -
    // through plain ResizeBuffers this time.
    Minimize(false);
    Restore(false);
    Minimize(false);
    Restore(false);
    drawn = DrawnPixels();
    std::printf("3. minimized and restored twice more: %zu pixels; the HUD at %.0f,%.0f\n", drawn, HudAt().x, HudAt().y);
    CHECK(drawn == first);
    CHECK(HudWhereTheHostPutIt());

    // 4. A new size, as a drag of the frame or a monitor of another scale gives.
    Resize(1024, 700);
    drawn = DrawnPixels();
    std::printf("4. resized to 1024 x 700: %zu pixels\n", drawn);
    CHECK(drawn >= kDrawnAtLeast);
    CHECK(HudWhereTheHostPutIt());

    // 5. A new back-buffer format, as HDR gives: the pipeline is built for a format, and drawing
    // into another is an error the debug layer reports and a driver may remove the device for.
    ResizeBuffers(g.width, g.height, DXGI_FORMAT_R10G10B10A2_UNORM, true);
    MakeReadback();
    drawn = DrawnPixels();
    std::printf("5. back buffers made R10G10B10A2: %zu pixels\n", drawn);
    CHECK(drawn >= kDrawnAtLeast);

    // 6. And back, minimized on the way, at the first size.
    Minimize(true);
    g.width = 800;
    g.height = 600;
    ShowWindow(g.window, SW_SHOWNOACTIVATE);
    const SIZE outer = OuterSize(g.width, g.height);
    SetWindowPos(g.window, nullptr, 0, 0, outer.cx, outer.cy, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    Pump();
    ResizeBuffers(g.width, g.height, DXGI_FORMAT_B8G8R8A8_UNORM, true);
    MakeReadback();
    drawn = DrawnPixels();
    std::printf("6. minimized, restored at 800 x 600 in B8G8R8A8: %zu pixels\n", drawn);
    CHECK(drawn == first);

    // 7. Minimized with no frame at all until it comes back, as AC:Unreal was for 43 minutes.
    Minimize(true, 0);
    Restore(true);
    drawn = DrawnPixels();
    std::printf("7. minimized with no frame presented, restored: %zu pixels\n", drawn);
    CHECK(drawn == first);

    // 8. A new swap chain for the same window, the old one gone: refused while anything still
    // holds one of the old one's buffers.
    const bool remade = NewSwapChain();
    CHECK(remade);
    if (remade) {
        drawn = DrawnPixels();
        std::printf("8. a new swap chain for the window: %zu pixels\n", drawn);
        CHECK(drawn == first);
    }

    CHECK(HudWhereTheHostPutIt());
    CHECK(DeviceAlive());
    const size_t errors = DebugErrors();
    if (g.debugLayer) std::printf("   the debug layer reported %zu errors\n", errors);
    CHECK(errors == 0);

    overlay::hooks::Uninstall();
    g_hostStopping.store(true);
    host.join();
    overlay::CloseLog();
    Stop();

    std::printf("\n%d checks, %d failed\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
