using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AC.Injector
{
    /// <summary>
    /// Puts a native DLL into a running process.
    /// </summary>
    /// <remarks>
    /// The method is the oldest one there is: write the DLL's path into the target's
    /// address space, then start a thread there whose entry point is LoadLibraryW and whose
    /// argument is that path. It works because kernel32 sits at the same address in every
    /// process of the same architecture, so the address of LoadLibraryW here is also its
    /// address there - which is equally the reason the two processes must be the same
    /// architecture.
    ///
    /// Everything that can be checked is checked first: the process exists, it is x64, the
    /// DLL is where the caller says, and the DLL is not already loaded. The last one
    /// matters most. A second load runs the entry point again in a process whose renderer
    /// has already been hooked once, and the usual result is the game closing without a
    /// word.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static class DllInjector
    {
        /// <summary>
        /// How long the remote thread is given to come back. LoadLibraryW runs the DLL's
        /// entry point, which for the overlay installs a hook; if that has not finished in
        /// ten seconds it is stuck rather than slow.
        /// </summary>
        private const uint WaitMilliseconds = 10_000;

        /// <summary>
        /// Injects into the single running process with this name.
        /// </summary>
        /// <param name="processName">The executable's name, with or without the .exe.</param>
        /// <param name="dllPath">An absolute path to the DLL.</param>
        public static InjectionResult Inject(string processName, string dllPath)
        {
            if (string.IsNullOrWhiteSpace(processName))
                return InjectionResult.Refused("No process was named. Give --process the name of the running client, with or without the .exe.");

            InjectionResult refused = CheckDll(dllPath);
            if (refused != null)
                return refused;

            string wanted = WithoutExtension(processName);
            Process[] matches = Process.GetProcessesByName(wanted);

            try
            {
                if (matches.Length == 0)
                {
                    return InjectionResult.Refused(
                        $"No process called '{wanted}' is running. The name is the executable's, without the .exe; --list prints the ones that could be injected into.");
                }

                if (matches.Length > 1)
                {
                    string ids = string.Join(", ", Array.ConvertAll(matches, p => p.Id.ToString()));
                    return InjectionResult.Refused(
                        $"{matches.Length} processes are called '{wanted}' (ids {ids}). Injecting into the wrong client is worse than not injecting, so choose one by process id.");
                }

                return InjectInto(matches[0].Id, dllPath);
            }
            finally
            {
                foreach (Process match in matches)
                    match.Dispose();
            }
        }

        /// <summary>
        /// Injects into one process by id, for when the name is ambiguous or already known.
        /// </summary>
        public static InjectionResult InjectInto(int processId, string dllPath)
        {
            InjectionResult refused = CheckDll(dllPath);
            if (refused != null)
                return refused;

            string full = Path.GetFullPath(dllPath);
            string module = Path.GetFileName(full);

            Process process;
            try
            {
                process = Process.GetProcessById(processId);
            }
            catch (ArgumentException)
            {
                return InjectionResult.Refused($"No process with id {processId} is running. It may have closed since it was listed.");
            }

            try
            {
                using (process)
                {
                    ProcessHandle handle = NativeMethods.OpenProcess(NativeMethods.InjectAccess, false, processId);
                    if (handle.IsInvalid)
                    {
                        int error = Marshal.GetLastPInvokeError();
                        handle.Dispose();
                        return InjectionResult.Refused(OpenFailure(process.ProcessName, processId, error));
                    }

                    using (handle)
                    {
                        refused = CheckArchitecture(handle, process.ProcessName, processId);
                        if (refused != null)
                            return refused;

                        refused = CheckNotAlreadyLoaded(process, module);
                        if (refused != null)
                            return refused;

                        return LoadRemotely(handle, process.ProcessName, processId, full, module);
                    }
                }
            }
            catch (Exception ex)
            {
                // Whatever this was, the caller gets a sentence rather than a stack: a
                // failed injection is a normal outcome of asking about a process that is
                // closing underneath us.
                return InjectionResult.Refused($"Injecting {module} into process {processId} failed unexpectedly: {ex.Message}");
            }
        }

        /// <summary>
        /// Processes worth offering as a target: with a name, anything called that; without
        /// one, every process that owns a window. The game has a window, and a list of every
        /// service on the machine is not a list anybody reads.
        /// </summary>
        public static IReadOnlyList<InjectionTarget> Candidates(string processName = null)
        {
            bool named = !string.IsNullOrWhiteSpace(processName);
            Process[] running = named
                ? Process.GetProcessesByName(WithoutExtension(processName))
                : Process.GetProcesses();

            List<InjectionTarget> found = new List<InjectionTarget>();

            foreach (Process process in running)
            {
                try
                {
                    if (named || process.MainWindowHandle != IntPtr.Zero)
                        found.Add(new InjectionTarget(process.Id, process.ProcessName, Is64Bit(process.Id)));
                }
                catch (Exception)
                {
                    // Processes come and go while the list is being walked; one that has
                    // gone is simply not a candidate.
                }
                finally
                {
                    process.Dispose();
                }
            }

            found.Sort((a, b) =>
            {
                int byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return byName != 0 ? byName : a.ProcessId.CompareTo(b.ProcessId);
            });

            return found;
        }

        private static InjectionResult CheckDll(string dllPath)
        {
            if (string.IsNullOrWhiteSpace(dllPath))
                return InjectionResult.Refused("No DLL was named. Give --dll the full path of the DLL to load.");

            if (!Path.IsPathFullyQualified(dllPath))
            {
                // The path is resolved by the loader inside the target, whose working
                // directory is the game's installation, not ours.
                return InjectionResult.Refused(
                    $"The DLL path must be absolute: '{dllPath}' is relative, and it is resolved inside the target process, against the game's working directory rather than this one.");
            }

            if (!File.Exists(dllPath))
            {
                return InjectionResult.Refused(
                    $"There is no file at '{dllPath}'. The path is only read inside the target, where a wrong one shows up as a load that quietly does nothing.");
            }

            return null;
        }

        private static InjectionResult CheckArchitecture(ProcessHandle handle, string name, int processId)
        {
            if (!NativeMethods.IsWow64Process(handle, out bool wow64))
            {
                return InjectionResult.Refused(
                    $"Could not tell whether {name} ({processId}) is 64-bit (Win32 error {Marshal.GetLastPInvokeError()}), and injecting across architectures corrupts the target rather than failing.");
            }

            // On 64-bit Windows, running under WOW64 is what being a 32-bit process means.
            bool target64 = !wow64;

            if (!Environment.Is64BitProcess && target64)
            {
                return InjectionResult.Refused(
                    $"{name} ({processId}) is 64-bit and acinject is not. A remote thread cannot be started across architectures, because the LoadLibraryW address taken here would not be the one there. Run the x64 build.");
            }

            if (Environment.Is64BitProcess && !target64)
            {
                return InjectionResult.Refused(
                    $"{name} ({processId}) is a 32-bit process and acinject is 64-bit. The LoadLibraryW address taken here does not exist there, so the thread would start at nothing. Build a 32-bit acinject for a 32-bit client.");
            }

            return null;
        }

        private static InjectionResult CheckNotAlreadyLoaded(Process process, string module)
        {
            ProcessModuleCollection modules;
            try
            {
                modules = process.Modules;
            }
            catch (Exception ex)
            {
                // Refusing is the safe answer: loading a second copy runs the entry point
                // again, and for a DLL that hooks the renderer that usually closes the game.
                return InjectionResult.Refused(
                    $"The modules of {process.ProcessName} ({process.Id}) could not be read ({ex.Message}), so whether {module} is already loaded is unknown. Not injecting: a second copy would run its entry point again.");
            }

            foreach (ProcessModule loaded in modules)
            {
                if (string.Equals(loaded.ModuleName, module, StringComparison.OrdinalIgnoreCase))
                {
                    return InjectionResult.Refused(
                        $"{module} is already loaded in {process.ProcessName} ({process.Id}). Injecting it again would run its entry point a second time on top of the first, which is one of the surer ways to close the game. Restart the client to start over.");
                }
            }

            return null;
        }

        private static InjectionResult LoadRemotely(ProcessHandle process, string name, int processId, string dllPath, string module)
        {
            IntPtr kernel32 = NativeMethods.GetModuleHandle("kernel32.dll");
            if (kernel32 == IntPtr.Zero)
                return InjectionResult.Refused($"kernel32.dll is not loaded in acinject itself (Win32 error {Marshal.GetLastPInvokeError()}), which leaves nothing to take LoadLibraryW from.");

            IntPtr loadLibrary = NativeMethods.GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero)
                return InjectionResult.Refused($"LoadLibraryW was not found in kernel32.dll (Win32 error {Marshal.GetLastPInvokeError()}).");

            // The terminator is written too: the target reads this as a null-terminated
            // wide string and has no idea how long we meant it to be.
            byte[] path = Encoding.Unicode.GetBytes(dllPath + "\0");

            IntPtr remote = NativeMethods.VirtualAllocEx(
                process,
                IntPtr.Zero,
                (nuint)path.Length,
                NativeMethods.MemCommit | NativeMethods.MemReserve,
                NativeMethods.PageReadWrite);

            if (remote == IntPtr.Zero)
            {
                return InjectionResult.Refused(
                    $"Could not reserve {path.Length} bytes in {name} ({processId}) for the DLL path (Win32 error {Marshal.GetLastPInvokeError()}). That usually means the process is exiting, or something is guarding it.");
            }

            bool pathStillInUse = false;
            try
            {
                if (!NativeMethods.WriteProcessMemory(process, remote, path, (nuint)path.Length, out nuint written)
                    || written != (nuint)path.Length)
                {
                    return InjectionResult.Refused(
                        $"Could not write the DLL path into {name} ({processId}) (Win32 error {Marshal.GetLastPInvokeError()}, {written} of {path.Length} bytes written).");
                }

                IntPtr thread = NativeMethods.CreateRemoteThread(process, IntPtr.Zero, (nuint)0, loadLibrary, remote, 0, IntPtr.Zero);
                if (thread == IntPtr.Zero)
                {
                    return InjectionResult.Refused(
                        $"Could not start a thread in {name} ({processId}) (Win32 error {Marshal.GetLastPInvokeError()}). Anti-cheat software and a process that is closing both look like this.");
                }

                try
                {
                    uint wait = NativeMethods.WaitForSingleObject(thread, WaitMilliseconds);

                    if (wait == NativeMethods.WaitTimeout)
                    {
                        // The thread may still read the path, so the page has to stay.
                        pathStillInUse = true;
                        return InjectionResult.Refused(
                            $"{module}'s entry point in {name} ({processId}) has not returned after {WaitMilliseconds / 1000} seconds. The DLL is loaded but something in it is blocking, and the game will be unresponsive until it stops.");
                    }

                    if (wait != NativeMethods.WaitObject0)
                    {
                        pathStillInUse = true;
                        return InjectionResult.Refused(
                            $"Waiting for the remote thread in {name} ({processId}) failed (Win32 error {Marshal.GetLastPInvokeError()}). Whether {module} loaded is unknown.");
                    }

                    if (!NativeMethods.GetExitCodeThread(thread, out uint exitCode))
                    {
                        return InjectionResult.Refused(
                            $"The remote thread in {name} ({processId}) finished but its exit code could not be read (Win32 error {Marshal.GetLastPInvokeError()}), so whether {module} loaded is unknown.");
                    }

                    // Zero is the null HMODULE: LoadLibraryW ran and refused the DLL. The
                    // reason for that is inside the target and does not come back here.
                    if (exitCode == 0)
                    {
                        return InjectionResult.Refused(
                            $"{name} ({processId}) would not load {module}. LoadLibraryW refused it, which nearly always means one of the DLL's own dependencies is missing beside it, or it is built for the wrong architecture.");
                    }

                    return InjectionResult.Loaded($"{module} is loaded in {name} ({processId}).");
                }
                finally
                {
                    NativeMethods.CloseHandle(thread);
                }
            }
            finally
            {
                // Leaving a page behind in a game's address space is untidy but harmless;
                // freeing one a remote thread might still be reading is not.
                if (!pathStillInUse)
                    NativeMethods.VirtualFreeEx(process, remote, (nuint)0, NativeMethods.MemRelease);
            }
        }

        private static string OpenFailure(string name, int processId, int error)
        {
            if (error == NativeMethods.ErrorAccessDenied)
            {
                return $"Access to {name} ({processId}) was denied. acinject has to run at the same elevation as the client and be built for the same architecture: a process started as a plain user cannot open one started as administrator.";
            }

            return $"{name} ({processId}) could not be opened for injection (Win32 error {error}). The usual causes are the process exiting while it is being opened, or anti-cheat software holding it shut.";
        }

        private static bool? Is64Bit(int processId)
        {
            ProcessHandle handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);

            using (handle)
            {
                if (handle.IsInvalid)
                    return null;

                return NativeMethods.IsWow64Process(handle, out bool wow64) ? !wow64 : (bool?)null;
            }
        }

        /// <summary>
        /// Process names in Win32 have no extension, but people type the one they see in
        /// Task Manager.
        /// </summary>
        private static string WithoutExtension(string processName)
            => processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName.Substring(0, processName.Length - 4)
                : processName;
    }
}
