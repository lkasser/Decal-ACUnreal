#include "log.h"

#include <windows.h>

#include <cstdarg>
#include <cstdio>
#include <mutex>
#include <vector>

namespace overlay {
namespace {

std::mutex g_gate;
HANDLE g_file = INVALID_HANDLE_VALUE;

std::wstring DirectoryOfThisDll() {
    HMODULE self = nullptr;

    // GetModuleHandleEx with the address of a local function is the only way a DLL can
    // find itself without being told its own name.
    if (!GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&DirectoryOfThisDll),
            &self)) {
        return std::wstring();
    }

    std::wstring path(MAX_PATH, L'\0');
    DWORD length = GetModuleFileNameW(self, path.data(), static_cast<DWORD>(path.size()));
    if (length == 0 || length >= path.size()) {
        return std::wstring();
    }

    path.resize(length);
    size_t slash = path.find_last_of(L'\\');
    return slash == std::wstring::npos ? std::wstring() : path.substr(0, slash + 1);
}

HANDLE TryOpen(const std::wstring& path) {
    if (path.empty()) {
        return INVALID_HANDLE_VALUE;
    }

    // FILE_SHARE_READ so the file can be tailed while the game runs, which is how it
    // will usually be read.
    return CreateFileW(
        path.c_str(),
        FILE_APPEND_DATA,
        FILE_SHARE_READ,
        nullptr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
}

void WriteRaw(const std::string& text) {
    if (g_file == INVALID_HANDLE_VALUE) {
        return;
    }

    DWORD written = 0;
    WriteFile(g_file, text.data(), static_cast<DWORD>(text.size()), &written, nullptr);

    // Flushed on every line: a buffered line lost to a crash is the one that mattered.
    FlushFileBuffers(g_file);
}

std::string Timestamp() {
    SYSTEMTIME now{};
    GetLocalTime(&now);

    char buffer[32];
    _snprintf_s(
        buffer,
        sizeof(buffer),
        _TRUNCATE,
        "%02u:%02u:%02u.%03u ",
        now.wHour,
        now.wMinute,
        now.wSecond,
        now.wMilliseconds);

    return buffer;
}

}  // namespace

std::wstring DllDirectory() { return DirectoryOfThisDll(); }

void OpenLog() {
    std::lock_guard<std::mutex> lock(g_gate);

    if (g_file != INVALID_HANDLE_VALUE) {
        return;
    }

    const wchar_t* name = L"ACUnrealOverlay.log";

    g_file = TryOpen(DirectoryOfThisDll() + name);

    if (g_file == INVALID_HANDLE_VALUE) {
        // A game under Program Files cannot be written to without elevation, and the
        // log is worth more than its location.
        std::wstring temp(MAX_PATH, L'\0');
        DWORD length = GetTempPathW(static_cast<DWORD>(temp.size()), temp.data());
        if (length > 0 && length < temp.size()) {
            temp.resize(length);
            g_file = TryOpen(temp + name);
        }
    }

    WriteRaw("\r\n");
    WriteRaw(Timestamp() + "--- overlay attached ---\r\n");
}

void CloseLog() {
    std::lock_guard<std::mutex> lock(g_gate);

    if (g_file == INVALID_HANDLE_VALUE) {
        return;
    }

    WriteRaw(Timestamp() + "--- overlay detached ---\r\n");
    CloseHandle(g_file);
    g_file = INVALID_HANDLE_VALUE;
}

void LogLine(const std::string& text) {
    std::lock_guard<std::mutex> lock(g_gate);
    WriteRaw(Timestamp() + text + "\r\n");
}

void LogFormat(const char* format, ...) {
    if (format == nullptr) {
        return;
    }

    va_list args;
    va_start(args, format);

    int needed = _vscprintf(format, args);
    va_end(args);

    if (needed <= 0) {
        return;
    }

    std::vector<char> buffer(static_cast<size_t>(needed) + 1);

    va_start(args, format);
    _vsnprintf_s(buffer.data(), buffer.size(), _TRUNCATE, format, args);
    va_end(args);

    LogLine(std::string(buffer.data()));
}

}  // namespace overlay
