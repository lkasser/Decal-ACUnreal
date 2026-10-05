// A log file, because there is no console.
//
// This code runs inside someone else's process. When injection fails, or a hook lands
// in the wrong place, the only evidence is whatever was written down before things
// stopped - so the log is not a convenience here, it is the entire debugging story.
//
// Writes are flushed immediately for that reason: a buffered line lost to a crash is
// exactly the line that would have explained the crash.

#pragma once

#include <string>

namespace overlay {

// Opens the log beside the DLL, or in the temporary directory if that is not writable
// - a game installed under Program Files usually is not.
void OpenLog();

void CloseLog();

// The folder this DLL was loaded from, ending in a backslash, or empty if Windows would not
// say. Ours to write in - unlike the game's own folder.
std::wstring DllDirectory();

void LogLine(const std::string& text);

// Formats and writes. Kept printf-style rather than iostreams to avoid pulling locale
// machinery into a process whose locale is not ours to disturb.
void LogFormat(const char* format, ...);

}  // namespace overlay
