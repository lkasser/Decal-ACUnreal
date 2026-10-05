// Entry point for the injected overlay.
//
// DllMain does almost nothing on purpose. It runs while the loader lock is held, so
// anything that could load another library, create a thread that waits, or touch the
// graphics runtime would deadlock the game at the moment of injection - which looks
// exactly like the game freezing for no reason. So DllMain records the handle, starts
// one thread, and returns.

#include <windows.h>

#include "hooks.h"
#include "log.h"

namespace {

HMODULE g_self = nullptr;
volatile LONG g_unloading = 0;

// How long to keep trying to install the hooks, and how often.
//
// Injection usually happens after the game is up, in which case the first attempt
// succeeds. But injecting at launch is the more useful case - the overlay is there from
// the login screen - and then the device does not exist yet. Retrying costs nothing and
// removes the need to time the injection by hand.
constexpr int kInstallAttempts = 60;
constexpr DWORD kInstallIntervalMs = 500;

DWORD WINAPI Bootstrap(LPVOID) {
    overlay::OpenLog();
    overlay::LogLine("Bootstrap thread started.");

    for (int attempt = 1; attempt <= kInstallAttempts; ++attempt) {
        if (InterlockedCompareExchange(&g_unloading, 0, 0) != 0) {
            overlay::LogLine("Unload requested before the hooks were installed.");
            return 0;
        }

        if (overlay::hooks::Install()) {
            overlay::LogFormat("Hooks installed on attempt %d.", attempt);
            return 0;
        }

        if (attempt == 1) {
            overlay::LogLine("The renderer is not ready yet; retrying.");
        }

        Sleep(kInstallIntervalMs);
    }

    // Deliberately not fatal. The game carries on without an overlay, which is far
    // better than a game that will not start.
    overlay::LogFormat(
        "Gave up after %d attempts. No overlay this session; the game is unaffected.",
        kInstallAttempts);
    return 0;
}

// Runs on its own thread so the DLL can be removed from memory after this returns.
DWORD WINAPI Unload(LPVOID) {
    overlay::LogLine("Unloading.");

    overlay::hooks::Uninstall();
    overlay::CloseLog();

    // Releases the reference injection took and ends this thread in one step: returning
    // normally would leave this thread running inside a module that is being freed.
    FreeLibraryAndExitThread(g_self, 0);
}

}  // namespace

// Lets the injector, or anything else with a handle, ask for a clean removal. Exported
// with a plain name so GetProcAddress finds it without name mangling.
extern "C" __declspec(dllexport) void OverlayRequestUnload() {
    if (InterlockedExchange(&g_unloading, 1) != 0) {
        return;
    }

    HANDLE thread = CreateThread(nullptr, 0, &Unload, nullptr, 0, nullptr);
    if (thread != nullptr) {
        CloseHandle(thread);
    }
}

// Lets the host toggle the overlay without a keyboard hook of our own.
extern "C" __declspec(dllexport) void OverlaySetVisible(int visible) {
    overlay::hooks::SetVisible(visible != 0);
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved) {
    switch (reason) {
        case DLL_PROCESS_ATTACH: {
            g_self = module;

            // The game creates and destroys threads constantly; we care about none of
            // them, and the notifications are not free.
            DisableThreadLibraryCalls(module);

            HANDLE bootstrap = CreateThread(nullptr, 0, &Bootstrap, nullptr, 0, nullptr);
            if (bootstrap == nullptr) {
                // Nothing else is safe to do from here, and failing the load is the
                // honest outcome: an injected DLL that does nothing would be worse.
                return FALSE;
            }

            // Nothing waits on this thread, so the handle is of no further use and
            // holding it would leak one for the life of the process.
            CloseHandle(bootstrap);

            break;
        }

        case DLL_PROCESS_DETACH: {
            // reserved is non-null when the process is terminating rather than the
            // library being freed. At that point other DLLs may already be gone, so
            // unhooking the graphics runtime would touch code that no longer exists.
            // The process is ending anyway; leave everything alone.
            if (reserved != nullptr) {
                break;
            }

            overlay::hooks::Uninstall();
            overlay::CloseLog();
            break;
        }

        default:
            break;
    }

    return TRUE;
}
