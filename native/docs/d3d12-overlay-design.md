# Drawing a Dear ImGui overlay inside ACUnreal.exe — the D3D12 mechanics

What an injected DLL has to do to put ImGui geometry into the game's own frame, and
where that goes wrong. Everything below is either read out of the vendored sources in
this checkout, read out of the installed client, produced by the compiler on this
machine, or cited to a document that was fetched. Section 8 lists what could not be
established; nothing outside it is guesswork.

## Provenance

| Thing | Version actually read |
|---|---|
| Dear ImGui | `1.93.0 WIP`, `IMGUI_VERSION_NUM 19297` (`third_party/imgui/imgui.h:32-33`), git `580c00c3` |
| MinHook | git `8af6b4ac` (`third_party/minhook/include/MinHook.h`, `src/hook.c`, `src/buffer.c`) |
| Windows SDK | `10.0.26100.0` — `um/d3d12.h`, `shared/dxgi1_4.h`, `shared/dxgi1_5.h` |
| Compiler | MSVC from `Microsoft Visual Studio\18\BuildTools` (used for the vtable dumps below) |
| Target | `ACUnreal.exe`, 348 MB, `ACUnreal/Binaries/Win64/ACUnreal.exe`; engine `5.8.0-55116800+++UE5+Release-5.8`, Development, Win64, D3D12 (per `docs/ac-unreal-integration.md`) |

`imconfig.h` in this checkout defines nothing: `IMGUI_DISABLE_OBSOLETE_FUNCTIONS` is
**not** set, and `native/build.ps1` does not define it either, so the obsolete fields
described below are present. `build.ps1` already compiles
`backends/imgui_impl_dx12.cpp` and `backends/imgui_impl_win32.cpp` and links
`d3d12.lib dxgi.lib d3dcompiler.lib`.

## 0. What was verified about this particular target

These came from reading `ACUnreal.exe` itself (PE directories and embedded assertion
strings, which a Development build keeps), not from a tutorial. They change several of
the decisions below.

- **The client uses the D3D12 Agility SDK.** `ACUnreal.exe` exports `D3D12SDKPath` and
  `D3D12SDKVersion`, and ships `ACUnreal/Binaries/Win64/D3D12/x64/D3D12Core.dll`
  (4.8 MB) plus `d3d12SDKLayers.dll`. Consequence: `ID3D12Device` and
  `ID3D12CommandQueue` methods are implemented in **`D3D12Core.dll`**, not in the
  system `d3d12.dll`. See section 1 for why that matters to a vtable-derived hook.
- **`dxgi.dll` is a static import; `d3d12.dll` is a *delay* import.** Static import
  table: `tbbmalloc.dll, dxgi.dll, DSOUND.dll, WS2_32.dll, WINMM.dll, KERNEL32.dll, …`.
  Delay import table: `WinPixEventRuntime.dll, d3d12.dll, d3d11.dll, XINPUT1_4.dll, …`.
  So `GetModuleHandleW(L"dxgi.dll")` is always non-null, but
  `GetModuleHandleW(L"d3d12.dll")` is null until the RHI first touches D3D12. An
  injector that runs at process start must wait or call `LoadLibraryW` itself.
- **UE 5.8 resizes through `ResizeBuffers1`, not only `ResizeBuffers`.** Two assertion
  expressions are embedded verbatim in the exe, both attributed to
  `D:\build\++UE5\Sync\Engine\Source\Runtime\D3D12RHI\Private\Windows\WindowsD3D12Viewport.cpp`:

  ```
  SwapChain3->ResizeBuffers1(NumBackBuffers, SizeX, SizeY,
      UE::DXGIUtilities::GetSwapChainFormat(PixelFormat), SwapChainFlags,
      NodeMasks.GetData(), (IUnknown**)CommandQueues.GetData())
  ```
  ```
  SwapChain1->ResizeBuffers(NumBackBuffers, SizeX, SizeY,
      UE::DXGIUtilities::GetSwapChainFormat(PixelFormat), SwapChainFlags)
  ```

  Both paths are compiled in. A resize hook must cover both slots (13 and 39).
- **The back buffer format is a setting, not a constant.** The exe contains the cvar
  `r.DefaultBackBufferPixelFormat` and the enumerators
  `EDefaultBackBufferPixelFormat::DBBPF_B8G8R8A8`, `DBBPF_FloatRGBA`,
  `DBBPF_A2B10G10R10`. Read the format from the swap chain at run time; do not
  hardcode `DXGI_FORMAT_R8G8B8A8_UNORM`.
- **The RHI has a dedicated submission thread.** The exe contains the thread names
  `RHISubmissionThread` and `RHIInterruptThread`, the scope names
  `SubmissionQueue_Process`, `CommandList_Submit`, `RHIMisc/ProcessSubmissionQueue`,
  and the file `…/D3D12RHI/Private/D3D12Submission.cpp`. It also contains the cvars
  `r.D3D12.Submission.MaxExecuteBatchSize.Direct` / `.Copy` / `.Async`, whose help
  text is "The maximum number of command lists to pass to a single
  ExecuteCommandLists invocation for direct queues". Two things follow with certainty:
  `ExecuteCommandLists` is called in batches, and it is called on more than one queue
  type. A third — that the call happens on that submission thread rather than on the
  render thread — is a strong inference from the names, not something read out of code;
  section 8 says how to settle it in one log line. Section 5 and section 7 are written
  so that they hold either way.
- **The debug layer is available in the shipped build.** Cvar
  `r.D3D12.EnableD3DDebug` exists, `-d3ddebug` is parsed, and `d3d12SDKLayers.dll`
  ships next to `D3D12Core.dll`. Develop with it on; it turns the silent
  device-removal failures in section 7 into logged errors.
- **Swap chain creation path.** The exe contains
  `CreateSwapChainForHwnd failed with result '%ls' (0x%08X), falling back to legacy
  CreateSwapChain.`, and `SwapChain4->GetContainingOutput(...)`, so the object is
  QueryInterface'd at least as far as `IDXGISwapChain4`. There is also a stereo path
  (`FD3D12Viewport::FD3D12Viewport was not able to create stereo SwapChain`), which is
  relevant because the install ships `AC-VR.bat`.
- **The client uses raw input.** Its static `USER32.dll` import list includes
  `GetRawInputData`, `RegisterRawInputDevices`, `ClipCursor`, `SetCapture`,
  `GetCapture`, `SetCursorPos`, `TrackMouseEvent` and `SetWindowLongPtrW`. So mouse
  movement does not reach the game only through `WM_MOUSEMOVE`, and the game confines
  and warps the cursor. This is decisive for section 5.

## 1. Getting the command queue

### Why the queue is needed at all

`ImGui_ImplDX12_Init` asserts on it: `IM_ASSERT(init_info->CommandQueue != nullptr);`
(`backends/imgui_impl_dx12.cpp:927`). The backend uses it for texture uploads — it
builds its own allocator and list (`imgui_impl_dx12.cpp:836-840`) and, inside
`ImGui_ImplDX12_UpdateTexture`, does:

```cpp
cmdQueue->ExecuteCommandLists(1, (ID3D12CommandList* const*)&cmdList);   // :563
hr = cmdQueue->Signal(bd->Fence, ++bd->FenceLastSignaledValue);          // :564
bd->Fence->SetEventOnCompletion(bd->FenceLastSignaledValue, bd->FenceEvent);
::WaitForSingleObject(bd->FenceEvent, INFINITE);                         // :572
```

Separately, *we* need a DIRECT queue to submit our own command list on. It should be
the queue the swap chain presents on, because `Present` orders the flip against work
already submitted to that queue. Work submitted to a different queue has no ordering
relationship with the flip, so the overlay would appear a frame late, intermittently,
or not at all.

### Where the vtable comes from before the game has a queue

The trick is that a vtable slot holds the address of the *shared implementation* in
`D3D12Core.dll`. Create a throwaway device and a throwaway queue, read slot 10 out of
the queue's vtable, and that is the same code pointer the game's queue will use.
MinHook then patches the prologue of that function, so every `ID3D12CommandQueue` in
the process — including ones created later — is hooked. The canonical implementation of
this is [kiero](https://github.com/Rebzzel/kiero) (`kiero.cpp`, the
`RenderType::D3D12` branch), which does exactly:

```cpp
ID3D12Device* device;
D3D12CreateDevice(adapter, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device), (void**)&device);

D3D12_COMMAND_QUEUE_DESC queueDesc;
queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
queueDesc.Priority = 0;
queueDesc.Flags = D3D12_COMMAND_QUEUE_FLAG_NONE;
queueDesc.NodeMask = 0;
device->CreateCommandQueue(&queueDesc, __uuidof(ID3D12CommandQueue), (void**)&commandQueue);
…
::memcpy(g_methodsTable + 44, *(uint150_t**)commandQueue, 19 * sizeof(uint150_t));
```

Note kiero copies **19** slots for the queue and **18** for `IDXGISwapChain`, which
matches the SDK exactly (below). Kiero resolves `D3D12CreateDevice` with
`GetProcAddress(GetModuleHandle("d3d12.dll"), …)` and bails if the module is not
loaded — on this target that means *after* the RHI has initialised, which is what we
want anyway (see the Agility SDK caveat).

Getting the vtable pointer from a COM object is `*(void***)pObject`; slot *n* is
`(*(void***)pObject)[n]`.

### The vtable index, and how to determine it instead of guessing

`ID3D12CommandQueue::ExecuteCommandLists` is **slot 10**. Two independent derivations,
both reproducible on this machine:

1. **Ask the compiler.** MSVC prints the vtable layout with slot numbers:

   ```
   cl /nologo /c /EHsc /d1reportSingleClassLayoutID3D12CommandQueue vt.cpp
   ```

   where `vt.cpp` is just `#include <d3d12.h>`. Output (abridged, run 2026-09-28):

   ```
   ID3D12CommandQueue::$vftable@:
    0	| &IUnknown::QueryInterface
    1	| &IUnknown::AddRef
    2	| &IUnknown::Release
    3	| &ID3D12Object::GetPrivateData
    4	| &ID3D12Object::SetPrivateData
    5	| &ID3D12Object::SetPrivateDataInterface
    6	| &ID3D12Object::SetName
    7	| &ID3D12DeviceChild::GetDevice
    8	| &ID3D12CommandQueue::UpdateTileMappings
    9	| &ID3D12CommandQueue::CopyTileMappings
   10	| &ID3D12CommandQueue::ExecuteCommandLists
   11	| &ID3D12CommandQueue::SetMarker
   12	| &ID3D12CommandQueue::BeginEvent
   13	| &ID3D12CommandQueue::EndEvent
   14	| &ID3D12CommandQueue::Signal
   15	| &ID3D12CommandQueue::Wait
   16	| &ID3D12CommandQueue::GetTimestampFrequency
   17	| &ID3D12CommandQueue::GetClockCalibration
   18	| &ID3D12CommandQueue::GetDesc
   ```

   This is the ground truth for the ABI the DLL is compiled against. Use the same
   switch (`/d1reportSingleClassLayout<NAME>`, no space) any time an index is in doubt;
   it is the only method that cannot drift from the headers being used.

2. **Count the MIDL C vtable in the SDK header.** `ID3D12CommandQueueVtbl` in
   `um/d3d12.h` lists the slots in order. Watch for one artefact: `GetDesc` appears
   twice, guarded by `#if !defined(_WIN32)` / `#else`, because it returns a struct by
   value. It is one slot.

Do not try to "detect" the index at run time by pattern-matching code; there is no
reliable signal. Instead, guard the assumption:

- After reading the slot, call `GetModuleHandleExW` with
  `GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | …_UNCHANGED_REFCOUNT` on the resolved
  pointer and log the owning module. On this target it should be `D3D12Core.dll`
  (Agility) or `d3d12.dll`. If it is neither, something else has already hooked it —
  another overlay, a driver layer, PIX, or the Steam/Discord overlay — and chaining on
  top of a foreign detour needs to be a deliberate decision, not an accident.
- Log the first time the detour fires. If it never fires, the index or the module is
  wrong; that is far easier to see than to debug from a crash.

### The detour signature and the filtering you must do

```cpp
using PFN_ExecuteCommandLists = void (STDMETHODCALLTYPE*)(
    ID3D12CommandQueue* self, UINT NumCommandLists, ID3D12CommandList* const* ppCommandLists);
```

The hook will fire for **every** queue in the process. On this target that includes
copy and async-compute queues (the `r.D3D12.Submission.MaxExecuteBatchSize.Copy` and
`.Async` cvars prove they exist), and it fires with `NumCommandLists > 1` because UE
batches. So:

1. Filter on type: `self->GetDesc().Type == D3D12_COMMAND_LIST_TYPE_DIRECT`.
2. Filter on device: `self->GetDevice(…)` must match the device you took from the swap
   chain (`IDXGISwapChain::GetDevice`). A second adapter, or D3D11On12, would otherwise
   hand you a queue from the wrong device — and using a resource from device A on a
   queue from device B removes the device.
3. Record and return — do nothing else inside this hook. It is on the submission hot
   path and it fires many times per frame.

There is a better source of the present queue on this target, because UE 5.8 passes it
explicitly. `IDXGISwapChain3::ResizeBuffers1`'s last parameter is
`IUnknown *const *ppPresentQueue` (`shared/dxgi1_4.h`), and the embedded assertion
string shows UE passing `(IUnknown**)CommandQueues.GetData()`. A `ResizeBuffers1` hook
therefore yields the authoritative present queue rather than a heuristic — but only
when a resize happens. `IDXGIFactory2::CreateSwapChainForHwnd`'s first parameter is
also the present device/queue, but hooking it requires being resident before the RHI
creates the swap chain. Treat `ExecuteCommandLists` as the always-available path and
either of the other two as corroboration.

## 2. Getting the Present hook

### Dummy swap chain

Same shape as the queue: create one, read its vtable, throw it away. Kiero's version:

```cpp
DXGI_SWAP_CHAIN_DESC swapChainDesc = {};
swapChainDesc.BufferDesc.Width  = 100;
swapChainDesc.BufferDesc.Height = 100;
swapChainDesc.BufferDesc.RefreshRate = { 60, 1 };
swapChainDesc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
swapChainDesc.SampleDesc  = { 1, 0 };
swapChainDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
swapChainDesc.BufferCount = 2;
swapChainDesc.OutputWindow = window;
swapChainDesc.Windowed = 1;
swapChainDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
swapChainDesc.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH;
factory->CreateSwapChain(commandQueue, &swapChainDesc, &swapChain);
```

`FLIP_DISCARD` is not a stylistic choice: D3D12 rejects the bitblt models. Microsoft's
[`DXGI_SWAP_EFFECT`](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/ne-dxgi-dxgi_swap_effect)
reference says of both `DISCARD` and `SEQUENTIAL`: "**Direct3D 12:** This enumeration
value is never supported. D3D12 apps must use `DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL` or
`DXGI_SWAP_EFFECT_FLIP_DISCARD`." A useful corollary: because the game's swap chain is
necessarily flip-model, `IDXGISwapChain3::GetCurrentBackBufferIndex` is always
available on it.

### The dummy window

Minimal: a registered class and an unshown `CreateWindowW`. Kiero's:

```cpp
windowClass.style = CS_HREDRAW | CS_VREDRAW;
windowClass.lpfnWndProc = DefWindowProc;
windowClass.hInstance = GetModuleHandle(NULL);
windowClass.lpszClassName = "Kiero";
RegisterClassEx(&windowClass);
HWND window = CreateWindow(windowClass.lpszClassName, "Kiero DirectX Window",
                           WS_OVERLAPPEDWINDOW, 0, 0, 100, 100, NULL, NULL,
                           windowClass.hInstance, NULL);
```

It is never shown, never pumped, and destroyed immediately afterwards
(`DestroyWindow` then `UnregisterClass`). Three things matter:

- `lpfnWndProc` must be `DefWindowProc`, or some function that outlives the DLL. A
  window proc inside the injected DLL creates an unload hazard: `UnregisterClass` must
  succeed before `FreeLibrary`, and if any window of the class survives it will not.
  Use `DefWindowProc` and the problem does not exist.
- Use a class name unlikely to collide, and ignore `RegisterClassEx` failing with
  `ERROR_CLASS_ALREADY_EXISTS` (it will, on a reinjection).
- `WS_OVERLAPPEDWINDOW` with a nonzero client size and `Windowed = 1`: DXGI needs a real
  HWND, not a message-only window (`HWND_MESSAGE` parents are not valid swap chain
  output windows).

### Indices

From the same MSVC dump, `/d1reportSingleClassLayoutIDXGISwapChain3` (abridged; the
full `IDXGISwapChain4` chain from `shared/dxgi1_5.h` runs to slot 40, `SetHDRMetaData`):

| Slot | Method | Interface |
|---|---|---|
| 8 | `Present` | `IDXGISwapChain` |
| 9 | `GetBuffer` | `IDXGISwapChain` |
| 12 | `GetDesc` | `IDXGISwapChain` |
| 13 | `ResizeBuffers` | `IDXGISwapChain` |
| 18 | `GetDesc1` | `IDXGISwapChain1` |
| 20 | `GetHwnd` | `IDXGISwapChain1` |
| 22 | `Present1` | `IDXGISwapChain1` |
| 36 | `GetCurrentBackBufferIndex` | `IDXGISwapChain3` |
| 39 | `ResizeBuffers1` | `IDXGISwapChain3` |

```cpp
using PFN_Present  = HRESULT (STDMETHODCALLTYPE*)(IDXGISwapChain* self, UINT SyncInterval, UINT Flags);
using PFN_Present1 = HRESULT (STDMETHODCALLTYPE*)(IDXGISwapChain1* self, UINT SyncInterval, UINT Flags,
                                                  const DXGI_PRESENT_PARAMETERS* pPresentParameters);
```

`Present1` is a **different slot and a different function**, so a `Present` hook does
not see `Present1` calls. Whether UE 5.8 uses one or the other could not be established
statically (section 8): the exe contains no stringified `Present` call expression, so
there is nothing to read. The cheap resolution is to hook both slots and log which one
fires first, then keep the one that does. Two notes if you do:

- A `Present1` detour must not assume `pPresentParameters` is non-null; it is optional.
- If `dxgi.dll` happens to implement both slots with the same function address,
  `MH_CreateHook` on the second returns `MH_ERROR_ALREADY_CREATED`. Treat that status as
  success-with-one-hook rather than as a failure.

Note that `IDXGISwapChain1`/`3`/`4` are the *same object* with the *same vtable*; the
game holding an `IDXGISwapChain4*` (which it does — see section 0) changes nothing,
because MinHook patches the implementation, not a per-object pointer.

## 3. The ImGui DX12 backend contract in this checkout

This version takes a struct. The separate-arguments form is **gone**, not merely
deprecated — `imgui_impl_dx12.cpp:24`:

```
//  2026-08-05: DirectX12: *BREAKING CHANGE* Removed support for legacy `ImGui_ImplDX12_Init()`
//  signature obsoleted in 1.91.6 (2024-11-15) because it needed to forcefully disable support
//  for ImGuiBackendFlags_RendererHasTextures. (#9487)
```

The declaration in `imgui_impl_dx12.h:84` is commented out. There is no fallback.

### The exact declarations

`backends/imgui_impl_dx12.h:34-54`, verbatim:

```cpp
// Initialization data, for ImGui_ImplDX12_Init()
struct ImGui_ImplDX12_InitInfo
{
    ID3D12Device*               Device;
    ID3D12CommandQueue*         CommandQueue;       // Command queue used for queuing texture uploads.
    int                         NumFramesInFlight;
    DXGI_FORMAT                 RTVFormat;          // RenderTarget format.
    DXGI_FORMAT                 DSVFormat;          // DepthStencilView format.
    void*                       UserData;

    // Allocating SRV descriptors for textures is up to the application, so we provide callbacks.
    // (current version of the backend will only allocate one descriptor, from 1.92 the backend will need to allocate more)
    ID3D12DescriptorHeap*       SrvDescriptorHeap;
    void                        (*SrvDescriptorAllocFn)(ImGui_ImplDX12_InitInfo* info, D3D12_CPU_DESCRIPTOR_HANDLE* out_cpu_desc_handle, D3D12_GPU_DESCRIPTOR_HANDLE* out_gpu_desc_handle);
    void                        (*SrvDescriptorFreeFn)(ImGui_ImplDX12_InitInfo* info, D3D12_CPU_DESCRIPTOR_HANDLE cpu_desc_handle, D3D12_GPU_DESCRIPTOR_HANDLE gpu_desc_handle);
#ifndef IMGUI_DISABLE_OBSOLETE_FUNCTIONS
    D3D12_CPU_DESCRIPTOR_HANDLE LegacySingleSrvCpuDescriptor; // To facilitate transition from single descriptor to allocator callback, you may use those.
    D3D12_GPU_DESCRIPTOR_HANDLE LegacySingleSrvGpuDescriptor;
#endif

    ImGui_ImplDX12_InitInfo()   { memset((void*)this, 0, sizeof(*this)); }
};
```

`backends/imgui_impl_dx12.h:57-67`, verbatim:

```cpp
IMGUI_IMPL_API bool     ImGui_ImplDX12_Init(ImGui_ImplDX12_InitInfo* info);
IMGUI_IMPL_API void     ImGui_ImplDX12_Shutdown();
IMGUI_IMPL_API void     ImGui_ImplDX12_NewFrame();
IMGUI_IMPL_API void     ImGui_ImplDX12_RenderDrawData(ImDrawData* draw_data, ID3D12GraphicsCommandList* graphics_command_list);

// Use if you want to reset your rendering device without losing Dear ImGui state.
IMGUI_IMPL_API bool     ImGui_ImplDX12_CreateDeviceObjects();
IMGUI_IMPL_API void     ImGui_ImplDX12_InvalidateDeviceObjects();

// (Advanced) Use e.g. if you need to precisely control the timing of texture updates (e.g. for staged rendering), by setting ImDrawData::Textures = nullptr to handle this manually.
IMGUI_IMPL_API void     ImGui_ImplDX12_UpdateTexture(ImTextureData* tex);
```

And, for draw callbacks, `imgui_impl_dx12.h:72-77`:

```cpp
struct ImGui_ImplDX12_RenderState
{
    ID3D12Device*               Device;
    ID3D12GraphicsCommandList*  CommandList;
};
IMGUI_IMPL_API ImGui_ImplDX12_RenderState* ImGui_ImplDX12_GetRenderState();
```

Platform side, `backends/imgui_impl_win32.h:24-35`:

```cpp
IMGUI_IMPL_API bool     ImGui_ImplWin32_Init(void* hwnd);
IMGUI_IMPL_API void     ImGui_ImplWin32_Shutdown();
IMGUI_IMPL_API void     ImGui_ImplWin32_NewFrame();
…
extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);
```

The handler forward declaration is inside `#if 0` in the header specifically so it can
be copied into a `.cpp`; `imgui_impl_win32.cpp:622-623` offers both it and a variant
that avoids `ImGui::GetCurrentContext()`:

```cpp
extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);                // Use ImGui::GetCurrentContext()
extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandlerEx(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam, ImGuiIO& io); // Doesn't use ImGui::GetCurrentContext()
```

### Initialisation order

Once, on the first `Present` where both the swap chain and a DIRECT queue are known
(from the example, `main.cpp:135-164`):

```cpp
IMGUI_CHECKVERSION();
ImGui::CreateContext();
ImGuiIO& io = ImGui::GetIO();
io.ConfigFlags |= ImGuiConfigFlags_NoMouseCursorChange;   // see section 5
io.MouseDrawCursor = true;                                 // see section 5
io.IniFilename = nullptr;                                  // imgui.h:2465 — default is relative to cwd
ImGui::StyleColorsDark();
ImGui_ImplWin32_Init(hwndFromSwapChain);
ImGui_ImplDX12_Init(&initInfo);      // initInfo filled as above
```

`ImGui_ImplDX12_Init` itself calls `IMGUI_CHECKVERSION()` and asserts
`io.BackendRendererUserData == nullptr` (`imgui_impl_dx12.cpp:918-919`), so a second
initialisation without a `Shutdown` in between fires an assert inside the game's present
path. Guard it with your own state flag rather than relying on the assert.

### Frames in flight

`NumFramesInFlight` is copied to `bd->numFramesInFlight` (`:931`) and used for exactly
two things: the size of the vertex/index buffer ring (`:953`) and the delay before a
retired texture is destroyed (`:577`). The ring is advanced blindly:

```cpp
// FIXME: We are assuming that this only gets called once per frame!            // :248
ImGui_ImplDX12_Data* bd = ImGui_ImplDX12_GetBackendData();
bd->frameIndex = bd->frameIndex + 1;                                            // :250
ImGui_ImplDX12_RenderBuffers* fr = &bd->pFrameResources[bd->frameIndex % bd->numFramesInFlight];
```

There is **no fence anywhere in `RenderDrawData`**. The backend will happily `Map` and
overwrite the vertex buffer the GPU is still reading if you call it more than
`NumFramesInFlight` times without waiting. The whole of the synchronisation obligation
is yours. Set `NumFramesInFlight` to the swap chain's `BufferCount` (read it, do not
assume 2 or 3) and enforce it with your own fence, as the vendored example does in
`WaitForNextFrameContext` (`examples/example_win32_directx12/main.cpp:503-516`).

Also note `RenderDrawData` must be called exactly once per frame. If the overlay is
hidden, skip `NewFrame`/`Render` entirely rather than calling `RenderDrawData` twice or
zero times inconsistently.

### Descriptor heaps: who owns what

Two heaps, both owned by us, both created from the game's device.

- **SRV heap (`CBV_SRV_UAV`, `SHADER_VISIBLE`).** Passed as
  `InitInfo.SrvDescriptorHeap`, and the backend allocates out of it through your
  callbacks. Because this build advertises `ImGuiBackendFlags_RendererHasTextures`
  (`:938`), the dynamic font atlas can ask for several textures over the process
  lifetime, each needing a descriptor: `SrvDescriptorAllocFn` is called per texture in
  `ImGui_ImplDX12_UpdateTexture` and `SrvDescriptorFreeFn` in
  `ImGui_ImplDX12_DestroyTexture`. So a single descriptor is not enough. The vendored
  example uses 64 (`APP_SRV_HEAP_SIZE`) with a free-list allocator
  (`ExampleDescriptorHeapAllocator`, `main.cpp:37-79`) — copy that. The legacy
  single-descriptor mode still exists (`imgui_impl_dx12.cpp:894-912`) and is selected
  automatically if you leave the callbacks null, but it asserts the moment a second
  texture is live: `"Only 1 simultaneous texture allowed with legacy
  ImGui_ImplDX12_Init() signature!"`. Do not use it.
- **RTV heap (`RTV`, non-shader-visible).** The backend never touches this. It is ours,
  for the swap chain's back buffers; see section 4.

Critically, `ImGui_ImplDX12_RenderDrawData` **does not call `SetDescriptorHeaps`** —
grep the file, the only `SetDescriptorHeaps` call in the DX12 path is in the example,
not the backend. Nor does it call `OMSetRenderTargets`. Both are the caller's job. What
it *does* set on your command list, via `ImGui_ImplDX12_SetupRenderState`
(`:170-219`): `RSSetViewports`, `IASetVertexBuffers`, `IASetIndexBuffer`,
`IASetPrimitiveTopology`, `SetPipelineState`, `SetGraphicsRootSignature`,
`SetGraphicsRoot32BitConstants`, `OMSetBlendFactor`, then per command
`RSSetScissorRects`, `SetGraphicsRootDescriptorTable(1, …)`, `DrawIndexedInstanced`.

### The exact per-frame sequence

Taken from the vendored example, `examples/example_win32_directx12/main.cpp:260-295`,
with the clear removed (we are compositing, not clearing):

```cpp
ImGui_ImplDX12_NewFrame();      // main.cpp:218 — lazily builds PSOs on first call
ImGui_ImplWin32_NewFrame();     // main.cpp:219
ImGui::NewFrame();              // main.cpp:220
  … your windows …
ImGui::Render();                // main.cpp:260

// wait until this frame's allocator is free, then:
UINT backBufferIdx = swapChain3->GetCurrentBackBufferIndex();            // main.cpp:263
allocator->Reset();
cmdList->Reset(allocator, nullptr);                                     // main.cpp:273
cmdList->ResourceBarrier(1, &toRenderTarget);                           // PRESENT -> RENDER_TARGET
cmdList->OMSetRenderTargets(1, &rtvHandle[backBufferIdx], FALSE, nullptr); // main.cpp:279
cmdList->SetDescriptorHeaps(1, &srvHeap);                               // main.cpp:280
ImGui_ImplDX12_RenderDrawData(ImGui::GetDrawData(), cmdList);           // main.cpp:281
cmdList->ResourceBarrier(1, &toPresent);                                // RENDER_TARGET -> PRESENT
cmdList->Close();                                                       // main.cpp:285
queue->ExecuteCommandLists(1, (ID3D12CommandList* const*)&cmdList);     // main.cpp:287
queue->Signal(fence, ++fenceValue);                                     // main.cpp:288
// then let the game's original Present run
```

`ImGui_ImplDX12_NewFrame` is trivial — it only builds device objects on demand
(`:983-991`) — but it must still be called, and it must be called after
`ImGui_ImplDX12_Init`, on a thread where the device is usable.

The PSO is built with `psoDesc.RTVFormats[0] = bd->RTVFormat` and
`psoDesc.DSVFormat = bd->DSVFormat` (`:699-700`). `DSVFormat` must be
`DXGI_FORMAT_UNKNOWN` because we bind no depth buffer, and `RTVFormat` must equal the
format of the RTV we bind, or the draw is invalid.

Shaders are compiled at run time with `D3DCompile` at `vs_5_0` (`:737`) and `ps_5_0`
(`:769`), which is why `d3dcompiler.lib` is already in `build.ps1` and why SM5 feature
level is sufficient. This pulls a dependency on `d3dcompiler_47.dll` being loadable in
the process.

## 4. Render targets and resize

### The RTV heap and the views

Once per swap chain (from the vendored example, `main.cpp:370-386` and `:476-485`):

```cpp
D3D12_DESCRIPTOR_HEAP_DESC desc = {};
desc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
desc.NumDescriptors = bufferCount;        // from GetDesc1().BufferCount
desc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE;   // RTV heaps are never shader-visible
desc.NodeMask = 1;
device->CreateDescriptorHeap(&desc, IID_PPV_ARGS(&rtvHeap));

SIZE_T stride = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
D3D12_CPU_DESCRIPTOR_HANDLE h = rtvHeap->GetCPUDescriptorHandleForHeapStart();
for (UINT i = 0; i < bufferCount; i++) { rtv[i] = h; h.ptr += stride; }

for (UINT i = 0; i < bufferCount; i++) {
    swapChain->GetBuffer(i, IID_PPV_ARGS(&backBuffer[i]));
    device->CreateRenderTargetView(backBuffer[i], nullptr, rtv[i]);
}
```

Passing `nullptr` for the `D3D12_RENDER_TARGET_VIEW_DESC` inherits the resource's own
format, which is what you want: it keeps the RTV format equal to
`GetDesc1().Format`, which is what you passed as `InitInfo.RTVFormat`. Read all three
from the same place and they cannot disagree.

Per frame: `backBufferIdx = swapChain3->GetCurrentBackBufferIndex()` (slot 36).
`QueryInterface` for `IDXGISwapChain3` once and cache it; as established in section 2,
a D3D12 swap chain is always flip-model so this always succeeds.

### What must be recreated, and whether the resize hook is necessary

**Yes, hooking resize is necessary**, and the reason is a hard DXGI rule rather than a
preference. From
[`IDXGISwapChain::ResizeBuffers`](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-resizebuffers):

> You can't resize a swap chain unless you release all outstanding references to its
> back buffers. You must release all of its direct and indirect references on the back
> buffers in order for **ResizeBuffers** to succeed.

We hold direct references: the `ID3D12Resource*` from `GetBuffer`. We cannot avoid
holding them, because D3D12 does not keep resources alive for us —
[Differences in the Binding Model from Direct3D 11](https://learn.microsoft.com/en-us/windows/win32/direct3d12/binding-model):

> Before freeing any resource, such as a texture, applications now must make sure the
> GPU has completed referencing it. This means before an application can safely free a
> resource the GPU must have completed execution of the command list referencing the
> resource.

So the back buffer reference has to survive until our frame's fence completes, which
means it is alive across `Present`, which means the game's resize would fail with
`DXGI_ERROR_INVALID_CALL` if we did nothing. Descriptors are not references — D3D12
descriptor heaps hold no lifetime on the resource — so the RTV descriptors themselves
are not the problem; the `ID3D12Resource*` pointers are.

The hook shape, for **both** slot 13 and slot 39 (section 0 established UE 5.8 uses
both):

1. Wait on your fence until every submitted overlay frame has retired.
2. `Release` every back buffer reference and forget the RTV handles. Do not destroy the
   RTV heap; it can be reused.
3. Call the original.
4. On success, re-read `GetDesc1()` and rebuild references and views; on failure, leave
   yourself uninitialised and rebuild lazily on the next `Present`.

`ResizeBuffers1` signature, from `shared/dxgi1_4.h`:

```cpp
using PFN_ResizeBuffers  = HRESULT (STDMETHODCALLTYPE*)(IDXGISwapChain* self,
    UINT BufferCount, UINT Width, UINT Height, DXGI_FORMAT NewFormat, UINT SwapChainFlags);
using PFN_ResizeBuffers1 = HRESULT (STDMETHODCALLTYPE*)(IDXGISwapChain3* self,
    UINT BufferCount, UINT Width, UINT Height, DXGI_FORMAT Format, UINT SwapChainFlags,
    const UINT* pCreationNodeMask, IUnknown* const* ppPresentQueue);
```

Things that must be recreated on resize: the back buffer references, the RTVs, and —
if `BufferCount` changed — the RTV heap and your per-frame allocator ring. Things that
need not be: the ImGui context, the PSOs, the SRV heap, the font textures.
`ImGui_ImplDX12_InvalidateDeviceObjects` / `ImGui_ImplDX12_CreateDeviceObjects`
(`imgui_impl_dx12.h:63-64`) exist for a *device* reset, not a buffer resize; a resize
does not need them. `io.DisplaySize` needs nothing either — `ImGui_ImplWin32_NewFrame`
recomputes it from the HWND every frame (`imgui_impl_win32.cpp:408-410`).

A belt-and-braces check that costs nothing: each `Present`, compare the swap chain
pointer and the cached `GetDesc1().Width/Height/BufferCount/Format` against the live
ones, and rebuild if they differ. That catches a resize path you did not hook, a
fullscreen transition, and a second swap chain, and it turns "overlay stretched or
invisible after alt-tab" into a self-correcting condition rather than a bug report.

## 5. Input

### What the win32 backend does and does not do

It does **not** hook anything. There is no `SetWindowLongPtr`, `SetWindowsHookEx`,
`CallWindowProc` or `SetWindowSubclass` anywhere in `imgui_impl_win32.cpp`. It takes
input in two ways only:

1. You call `ImGui_ImplWin32_WndProcHandler(hwnd, msg, wParam, lParam)` for each
   message. It translates to `io.AddMousePosEvent`, `io.AddKeyEvent`,
   `io.AddInputCharacter`, `io.AddFocusEvent`, `io.AddMouseWheelEvent` and so on.
2. `ImGui_ImplWin32_NewFrame` polls: `GetClientRect` for `io.DisplaySize` (`:408-410`),
   `QueryPerformanceCounter` for `io.DeltaTime`, then `GetForegroundWindow` /
   `GetCursorPos` / `ScreenToClient` in `ImGui_ImplWin32_UpdateMouseData` (`:315-341`),
   `SetCursor` in `ImGui_ImplWin32_UpdateMouseCursor` (`:247-279`), and `XInputGetState`
   in `ImGui_ImplWin32_UpdateGamepads`.

Pass the swap chain's own HWND to `ImGui_ImplWin32_Init`. Get it from
`IDXGISwapChain1::GetHwnd` (slot 20) or from `IDXGISwapChain::GetDesc().OutputWindow`
(`shared/dxgi.h:299`) — **not** from `GetDesc1()`, which has no `OutputWindow` field
(`shared/dxgi1_2.h`: `Width, Height, Format, Stereo, SampleDesc, BufferUsage,
BufferCount, Scaling, SwapEffect, AlphaMode, Flags`), and not from
`GetForegroundWindow`. `bd->hWnd` is what `GetClientRect` and `ScreenToClient` are
called on, so getting it wrong gives a mis-scaled overlay and wrong mouse coordinates.

The handler's return value is almost always 0. Only two cases return 1: `WM_IME_CHAR`
on a non-Unicode window, and `WM_SETCURSOR` when the hit-test is `HTCLIENT` and the
cursor was changed (`:819-822`). The header's guidance — "Keep calling your message
handler unless this function returns TRUE" — therefore gives you almost no filtering on
its own. Filtering is done with `io.WantCaptureMouse` / `io.WantCaptureKeyboard`, not
with the return value.

### Hooking the WndProc: `SetWindowLongPtr` versus a code hook

Use `SetWindowLongPtrW(hwnd, GWLP_WNDPROC, ours)`, keeping the returned old proc and
calling it with `CallWindowProcW`. Reasons:

- It is the documented mechanism, it is per-window (so you cannot accidentally affect
  other windows in the process), and it is what every overlay does.
- Removing it is a single `SetWindowLongPtrW` back to the saved proc.
- MinHook is the wrong tool here: UE's window procedure is a member function reached
  through a thunk, it is not exported, and finding it needs a pattern scan that will
  break on the next client patch.

Two caveats. `SetWindowLongPtrW` on a window owned by another thread is allowed, but
the new proc will be invoked on **that window's thread**, not yours — which is the
whole point, but see the threading discussion below. And the removal is not
race-free: if someone else subclassed after you, restoring the saved proc drops their
hook. Restore only if the current proc is still yours
(`GetWindowLongPtrW(hwnd, GWLP_WNDPROC) == ours`); otherwise leave it alone and set a
flag that makes your proc a pure pass-through.

### The threading problem, which is the real problem here

The messages arrive on the thread that owns the window — UE's main/game thread. The
`Present` and `ExecuteCommandLists` hooks almost certainly fire on a different one: this
build has an `RHISubmissionThread` (section 0), and in any case UE renders off the game
thread. Assume the split is real until measured otherwise, because ImGui does not allow
it implicitly. `docs/FAQ.md:752`:

> A same Dear ImGui context may be not used from multiple threads in parallel.

and `:756`:

> If you want to submit contents from a main/update thread but render Dear ImGui output
> in a dedicated render thread, you'll need to stage `ImDrawData` and texture requests.
> See the `ImDrawDataSnapshot` and `ImTextureQueue` helpers in imgui_threaded_rendering.

For an overlay the simpler arrangement is the reverse of what the FAQ describes: keep
the *entire* ImGui context on the render thread and move the *input* across the seam,
which is a much smaller thing to marshal.

Concretely: the hooked WndProc copies `(msg, wParam, lParam)` — plus, for mouse
messages, `GetMessageExtraInfo()`, because
`ImGui_ImplWin32_GetMouseSourceFromMessageExtraInfo` (`:603-614`) reads it and its own
comment warns to read it early — into a lock-protected ring buffer and returns. The
`Present` hook drains that buffer, calling `ImGui_ImplWin32_WndProcHandlerEx` for each
entry, then `ImGui_ImplWin32_NewFrame`, `ImGui::NewFrame`, the UI, `ImGui::Render`,
`ImGui_ImplDX12_RenderDrawData`. Nothing but the queue is shared.

Three consequences of running the win32 backend off the message thread:

- **`SetCursor` is per-thread.** `ImGui_ImplWin32_UpdateMouseCursor` calls
  `::SetCursor(nullptr)` or `::SetCursor(::LoadCursor(nullptr, …))` (`:268`, `:278`),
  and `SetCursor` affects the calling thread's cursor. Called from the render thread it
  does nothing useful. Set `io.ConfigFlags |= ImGuiConfigFlags_NoMouseCursorChange`
  (`imgui.h:1753`) and `io.MouseDrawCursor = true` (`imgui.h:2502`) so ImGui draws its
  own cursor into the overlay geometry. This is also the right answer for a game that
  hides the OS cursor.
- **`WM_SETCURSOR` handling becomes moot**, which is fine, because with
  `NoMouseCursorChange` the handler returns 0 for it anyway.
- **`TrackMouseEvent`** is called from inside the handler on `WM_MOUSEMOVE`
  (`:645-655`). Calling it from a non-owning thread is documented as permitted, but if
  `WM_MOUSELEAVE` behaves oddly, the fallback in `UpdateMouseData` (`:335-338`, the
  `GetCursorPos`/`ScreenToClient` path taken when `MouseTrackedArea == 0`) already
  covers the case.

### Deciding who gets the click

Always feed everything to ImGui; decide separately what to hide from the game. The
fields are documented at `imgui.h:2603-2604`:

> `WantCaptureMouse` — Set when Dear ImGui will use mouse inputs, in this case do not
> dispatch them to your main game/application (either way, always pass on mouse inputs
> to imgui).

So in the hooked WndProc: enqueue the message for ImGui unconditionally, then decide
whether to call the game's original proc. Swallow mouse messages when
`io.WantCaptureMouse`, keyboard messages when `io.WantCaptureKeyboard`, and let
everything else through. Two adjustments make this behave:

- **Read the flags from the previous frame.** `WantCaptureMouse` is produced by
  `NewFrame` on the render thread; the WndProc reads a value that is up to one frame
  stale. Cache it into an atomic at the end of each overlay frame and read the atomic.
  One frame of latency on the capture decision is not noticeable; a torn read of
  `ImGuiIO` from two threads is a bug.
- **Gate on your own visibility flag first.** When the overlay is hidden, pass
  everything through and submit no ImGui windows at all — which is exactly what
  `overlay_ui.h` already documents `DrawOverlay`'s `visible` parameter to do ("While it
  is false this submits no windows at all, which is also how the game gets the mouse and
  keyboard back — an invisible window would still swallow clicks").

### The risk of stealing input the game needs

This is the failure mode most likely to make the tool unusable, and it is subtle
because it does not crash.

- **Swallowing key-up without key-down, or the reverse.** If the overlay opens while a
  movement key is held and you then start swallowing keyboard messages, the game never
  sees `WM_KEYUP` and the character runs into a wall forever. Either only ever begin
  swallowing on a clean transition, or synthesise the releases the game is owed when
  capture starts.
- **Mouse capture and look-mode.** A first-person camera typically calls `SetCapture`
  and warps the cursor. Swallowing `WM_MOUSEMOVE` while the game holds capture can wedge
  it. The backend already watches for this on its own side
  (`imgui_impl_win32.cpp:699` and `:721` compare `::GetCapture()` against the hwnd) but
  that only protects ImGui's state, not the game's.
- **Raw input, and it is not hypothetical here.** The win32 backend handles no
  `WM_INPUT` at all — grep `imgui_impl_win32.cpp`, it is not in the switch. And section 0
  established that `ACUnreal.exe` imports `GetRawInputData` and
  `RegisterRawInputDevices`. So swallowing `WM_MOUSEMOVE` swallows nothing useful: the
  camera will still turn while you drag an ImGui window. You must also swallow `WM_INPUT`
  (not forward it to the original proc) while `WantCaptureMouse` is set. ImGui does not
  need `WM_INPUT` itself — it gets position from `WM_MOUSEMOVE` and the `GetCursorPos`
  fallback — so dropping it is safe on our side.
- **`ClipCursor` and `SetCursorPos`.** The client imports both, so it confines and warps
  the cursor in look-mode. While the overlay has capture, the game may still be
  re-centring the pointer every frame, which makes ImGui unusable even with messages
  swallowed. Expect to have to call `ClipCursor(nullptr)` while the overlay is open, and
  to restore whatever clip rect was in force when it closes (`GetClipCursor` reads it).
  This was not tested; it is named here because it is the predictable next problem after
  `WM_INPUT`.
- **Hotkey collision.** Pick a toggle the game does not use, and swallow the toggle key
  itself in both directions so the game never sees it.

## 6. MinHook usage

The whole API used here is five functions, from `third_party/minhook/include/MinHook.h`:

```cpp
MH_STATUS WINAPI MH_Initialize(VOID);                                            // :96
MH_STATUS WINAPI MH_Uninitialize(VOID);                                          // :100
MH_STATUS WINAPI MH_CreateHook(LPVOID pTarget, LPVOID pDetour, LPVOID *ppOriginal); // :111
MH_STATUS WINAPI MH_RemoveHook(LPVOID pTarget);                                  // :147
MH_STATUS WINAPI MH_EnableHook(LPVOID pTarget);                                  // :154
MH_STATUS WINAPI MH_DisableHook(LPVOID pTarget);                                 // :161
const char *WINAPI MH_StatusToString(MH_STATUS status);                          // :181
```

`MH_Initialize` is documented "You must call this function EXACTLY ONCE at the
beginning of your program" and returns `MH_ERROR_ALREADY_INITIALIZED` otherwise.
`MH_EnableHook`/`MH_DisableHook` accept `MH_ALL_HOOKS` (`#define MH_ALL_HOOKS NULL`,
`:88`). For enabling several hooks under one thread-freeze there are
`MH_QueueEnableHook` / `MH_QueueDisableHook` / `MH_ApplyQueued` (`:168-178`) — prefer
these when installing more than one hook, so the process is frozen once rather than
four times.

The call sequence for a vtable-derived target. Note that `pTarget` is the **value** in
the slot, not the address of the slot:

```cpp
// 1. Read the slot out of a throwaway object's vtable.
void** vt = *reinterpret_cast<void***>(dummyQueue);
void*  target = vt[10];                       // ExecuteCommandLists

// 2. Once per process.
if (MH_Initialize() != MH_OK) { /* log MH_StatusToString(st) */ }

// 3. Create, disabled.
static PFN_ExecuteCommandLists s_origExecuteCommandLists = nullptr;
MH_STATUS st = MH_CreateHook(target, reinterpret_cast<void*>(&Detour_ExecuteCommandLists),
                             reinterpret_cast<void**>(&s_origExecuteCommandLists));
// MH_ERROR_ALREADY_CREATED means this exact address is already hooked by us.

// 4. Enable (queue several, then MH_ApplyQueued, if installing more than one).
MH_EnableHook(target);

// 5. Teardown, in this order.
MH_DisableHook(target);   // or MH_DisableHook(MH_ALL_HOOKS)
MH_RemoveHook(target);
MH_Uninitialize();
```

`MH_CreateHook` leaves the hook disabled; nothing happens until `MH_EnableHook`. The
trampoline written to `ppOriginal` is the only correct way to reach the original — do
not call `target` from inside the detour, that recurses.

What MinHook actually does when enabling or disabling, from `src/hook.c`: it snapshots
the process's threads with `CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0)`, skipping
its own (`EnumerateThreads`, `:263-290`), suspends each one (`Freeze`, `:328-367`), and
for any thread whose instruction pointer is inside the bytes being rewritten it
relocates the IP (`ProcessThreadIPs`, `:199-258`). That protects the patched prologue.
It does **not** wait for threads that are inside your detour body or past the prologue
in the trampoline. Section 7 covers what that means for unloading.

## 7. Pitfalls, most likely to kill the process first

**1. Rendering from the wrong thread.** Two distinct hazards.

*ImGui side:* `docs/FAQ.md:752` — "A same Dear ImGui context may be not used from
multiple threads in parallel." If the WndProc calls `io.AddKeyEvent` on the game thread
while `Present` is inside `ImGui::NewFrame` on the render thread, you are mutating
`ImGuiContext::InputEventsQueue` concurrently with the code that drains it. The symptom
is a corrupted `ImVector` and a crash in code that looks unrelated. The queue-and-drain
arrangement in section 5 is not a nicety.

*D3D12 side:* command lists are single-threaded objects and queues are not.
[Design Philosophy of Command Queues and Command Lists](https://learn.microsoft.com/en-us/windows/win32/direct3d12/design-philosophy-of-command-queues-and-command-lists):

> Like immediate contexts, each command list is not free-threaded; however, multiple
> command lists can be recorded concurrently

and

> Any thread may submit a command list to any command queue at any time, and the runtime
> will automatically serialize submission of the command list in the command queue while
> preserving the submission order.

So submitting from the `Present` hook thread is fine; recording into the same command
list from two threads is not. Since all our recording happens in one place inside the
`Present` hook, this is safe by construction — until someone adds a "render from the
hotkey handler" convenience. Guard the render path with a thread-id check that asserts
in Debug.

There is also a re-entrancy case: our own `ExecuteCommandLists` (and the one inside
`ImGui_ImplDX12_UpdateTexture`, `imgui_impl_dx12.cpp:563`) will re-enter our own
`ExecuteCommandLists` detour. Keep a thread-local or per-call guard, or the detour must
at minimum be trivially re-entrant.

**2. Getting the back buffer's resource state wrong.** The overlay transitions
`PRESENT -> RENDER_TARGET` and back. If the back buffer is not actually in
`D3D12_RESOURCE_STATE_PRESENT` when our barrier executes, the runtime does not merely
warn. From
[`ID3D12CommandQueue::ExecuteCommandLists`](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12commandqueue-executecommandlists):

> The runtime will validate the "before" and "after" states of resource transition
> barriers inside of **ExecuteCommandLists**. If the "before" state of a transition does
> not match up with the "after" state of a previous transition, then the runtime will
> drop the call and remove the device.

Same page, same consequence, for three more mistakes we can plausibly make: submitting
a list whose allocator was reset after `Close`; submitting a list whose previous
execution has not completed; and mismatched query states. Each of these is "the game
dies, with no message, some frames later". This is why `-d3ddebug` /
`r.D3D12.EnableD3DDebug` matters during development: "The debug layer issues errors for
all cases where the runtime would drop the call."

Ordering also matters and is easy to get wrong: our command list must be submitted
*before* the original `Present` runs, and after the game's own frame has been submitted.
Doing the work at the top of the `Present` detour and then tail-calling the original
gives exactly that, because the game has necessarily already submitted the frame it is
about to present.

**3. Overwriting buffers the GPU is still reading.** As established in section 3,
`ImGui_ImplDX12_RenderDrawData` cycles its vertex and index buffers with no fence. Get
your own fencing wrong and you get flickering geometry, then a device hang, then
`DXGI_ERROR_DEVICE_HUNG`. Signal a fence after each `ExecuteCommandLists` and do not
reuse slot *n* until its fence value has retired. The same applies to your command
allocator: `allocator->Reset()` while the GPU is executing from it is the
"allocators … have been reset after `Close`" case above, which removes the device.

**4. Device removal.** `Present` can return `DXGI_ERROR_DEVICE_REMOVED` or
`DXGI_ERROR_DEVICE_RESET`, and a driver TDR can remove the device at any time
regardless of anything we do. Once removed, every object derived from it is dead;
[`ID3D12Device::GetDeviceRemovedReason`](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12device-getdeviceremovedreason)
"Gets the reason that the device was removed, or **S_OK** if the device isn't removed."
The only recovery is to release everything and rebuild, which for an injected overlay
means: release our RTVs, back buffer references, command list, allocator, fence, SRV
heap, call `ImGui_ImplDX12_Shutdown`, and go back to the uninitialised state so the next
`Present` on a fresh device re-initialises from scratch. Keep the `ImGuiContext` — it
holds no device objects — so the user's window positions survive.

The same page carries a detail that matters for the `INFINITE` wait at
`imgui_impl_dx12.cpp:572`: "device removal causes all fences to be signaled to
[`UINT64_MAX`]". So that wait does not deadlock on device loss. Do not assume the same
of any wait you write yourself against a non-fence handle.

Practical rule: check the `HRESULT` from the original `Present` in the detour, and on
anything other than `S_OK`/`DXGI_STATUS_OCCLUDED` tear the overlay's device objects
down rather than trying to carry on.

**5. Unload safety.** This is the one that bites during development, because you will
inject and eject repeatedly. MinHook relocates instruction pointers only if a thread is
inside the *patched prologue* (`ProcessThreadIPs`, `hook.c:199-258`). A thread sitting
inside your detour, or inside the trampoline past the prologue, is not accounted for.
And `MH_Uninitialize` calls `UninitializeBuffer` (`hook.c:556`), which does
`VirtualFree(pBlock, 0, MEM_RELEASE)` on every trampoline block (`buffer.c:74-85`). So
the sequence "disable hooks, uninitialise, `FreeLibrary`" can free the code a thread is
currently executing.

A workable shutdown, in order:

1. Set an atomic `g_shuttingDown`. Every detour checks it first and, if set, tail-calls
   the original and does nothing else.
2. `MH_DisableHook(MH_ALL_HOOKS)`, then `MH_RemoveHook` each target. After this, new
   calls do not enter our code.
3. Wait for the in-flight detours to leave. Maintain an atomic counter incremented on
   detour entry and decremented on exit, and spin until it reads zero (with a timeout —
   if it will not drain, refusing to unload is better than crashing).
4. Wait on the GPU fence so no submitted command list is still executing, then release
   D3D objects, `ImGui_ImplDX12_Shutdown`, `ImGui_ImplWin32_Shutdown`,
   `ImGui::DestroyContext`.
5. Restore the WndProc (only if it is still ours, section 5).
6. `MH_Uninitialize()`.
7. Only now let `FreeLibrary` proceed.

Never do any of this from `DllMain`. `DLL_PROCESS_DETACH` runs under the loader lock,
so `MH_DisableHook`'s thread-suspension will deadlock against any thread that holds or
wants the loader lock. Do the teardown on a thread you created, and have `DllMain`
merely signal it. `build.ps1` compiles with `/MT`, so the DLL has its own CRT, which
makes the DLL self-contained but does not change any of this.

**6. Foreign overlays and hook chaining.** Steam, Discord, GeForce Experience, RTSS and
PIX all hook the same `Present` slot. If one of them installed a detour before us,
MinHook patches its detour rather than DXGI's function, which usually works but makes
the ordering of composited overlays arbitrary and makes teardown order matter. The
module check in section 1 turns this from a mystery into a log line.

**7. Two swap chains.** UE creates a viewport per window. A second window (an editor
panel, a VR mirror, the stereo path whose failure string exists in the exe) means a
second swap chain through the same `Present` slot. Bind to one swap chain pointer and
ignore calls from any other, or the overlay will fight over which back buffer it is
drawing into.

**8. Colour space and gamma.** Cosmetic, not fatal, but it will look wrong before it
looks right. The exe contains `Setting color space on swap chain (%p): %ls`, the
identifier `SetColorSpace1`, the colour space names `RGB_FULL_G22_NONE_P709`,
`RGB_FULL_G10_NONE_P709` and `RGB_FULL_G2084…`, and
`r.DefaultBackBufferPixelFormat` with a `FloatRGBA` option. If HDR is on, the back
buffer is scRGB or HDR10 and ImGui's sRGB-ish vertex colours will be wildly over- or
under-bright. Read `GetDesc1().Format`, log it, and treat anything other than an
8-bit UNORM format as a known-unstyled case for now.

## 8. What could not be established

Stated plainly, because each of these is a place where a confident guess would be
wrong.

- **Whether UE 5.8 calls `Present` or `Present1`.** The exe contains no stringified
  present call expression to read (unlike the two `ResizeBuffers` ones), and Epic's
  source is not public. A 2016 Epic forum thread has an Epic staffer asserting "It's
  `IDXGISwapChain` and `Present`, not `IDXGISwapChain1`/`Present1`" and "the D3D12 RHI
  also uses Present", but that discussion is about UE 4.11–4.12 and quotes no code, so
  it is not evidence about 5.8. Resolve it empirically: hook slots 8 and 22, log which
  fires.
- **Which thread `Present` is called on.** Section 0 establishes that this build has an
  `RHISubmissionThread` and that `ExecuteCommandLists` is driven from a submission
  queue, which makes it very likely `Present` is called there too, but that is
  inference from thread names and cvars, not from code. Determine it at run time:
  compare `GetCurrentThreadId()` in the hook against
  `GetWindowThreadProcessId(hwnd, nullptr)` and log both. The design in section 5 is
  correct either way; if they turn out to be the same thread the queue is simply
  redundant.
- **How much of the input problem raw input actually causes.** That the client imports
  `GetRawInputData` and `RegisterRawInputDevices` is established (section 0); *which*
  inputs it reads that way, and whether dropping `WM_INPUT` while the overlay has
  capture is sufficient, is not. Nor is the behaviour of its `ClipCursor`/`SetCursorPos`
  use while the overlay is open. This has to be tested, not reasoned about: open the
  overlay, drag a window, and watch the camera.
- **How many back buffers, and in what format.** `NumBackBuffers` is a variable in UE's
  viewport code and `r.DefaultBackBufferPixelFormat` is a cvar; neither has a value that
  can be read out of the binary. Both must be read from `GetDesc1()` at run time.
- **Whether a dummy device created by our DLL resolves to the same `D3D12Core.dll` as
  the game's.** The Agility SDK is opted into by the *executable's* exported
  `D3D12SDKVersion`/`D3D12SDKPath`, which is a per-process decision made when `d3d12.dll`
  is first used, so a dummy device created after the RHI has initialised should go
  through the same core. This was reasoned from the opt-in mechanism, not observed. The
  check is the module-identity log in section 1: if the resolved slot address belongs to
  `D3D12Core.dll`, we are in the same implementation as the game.
- **The state the back buffer is actually in at `Present` time.** The standard
  assumption is `D3D12_RESOURCE_STATE_PRESENT`, and UE has no reason to deviate, but it
  was not verified for this build and getting it wrong removes the device (pitfall 2).
  Verify with the debug layer on before trusting it.
- **Anything about the VR path.** The install ships `AC-VR.bat` and the exe has a
  stereo swap chain path. Nothing here was checked against it, and a stereo swap chain
  changes the back buffer layout. Treat VR as out of scope until tested.
- **Whether `d3dcompiler_47.dll` is loadable in this process.** The backend calls
  `D3DCompile` at run time (`imgui_impl_dx12.cpp:737`, `:769`). It is a system DLL on
  Windows 10+ and UE normally loads it anyway, but this was not confirmed for this
  target. If `ImGui_ImplDX12_CreateDeviceObjects` fails it asserts inside
  `ImGui_ImplDX12_NewFrame` (`:988-990`); handle that rather than letting an assert fire
  inside the game's present path.
