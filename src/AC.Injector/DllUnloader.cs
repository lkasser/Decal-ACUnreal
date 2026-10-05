using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace AC.Injector
{
    /// <summary>
    /// Asks an injected DLL to remove itself, by calling an exported function inside the
    /// host process.
    /// </summary>
    /// <remarks>
    /// Exists because iterating on an overlay otherwise means restarting the game for
    /// every change: the DLL stays locked on disk while it is loaded, so it cannot even
    /// be rebuilt. It also exercises the unload path, which is the one part of an
    /// injected DLL that never runs during ordinary use and therefore never gets tested
    /// unless something like this makes it easy.
    ///
    /// <para>
    /// The address of the export in the other process is worked out rather than guessed.
    /// The same DLL is mapped into this process without running any of its code, its
    /// export's offset from its own base is taken, and that offset is added to the base
    /// the other process has it at. Both processes are x64 and it is the same file, so
    /// the offset is the same in both.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static class DllUnloader
    {
        /// <summary>
        /// Calls <c>OverlayRequestUnload</c> in the given process, if the DLL is loaded
        /// there.
        /// </summary>
        public static InjectionResult Unload(int processId, string dllPath, string export = "OverlayRequestUnload")
        {
            if (string.IsNullOrWhiteSpace(dllPath))
                return InjectionResult.Refused("No DLL was named, so there is nothing to unload.");

            if (!Path.IsPathRooted(dllPath))
                return InjectionResult.Refused($"'{dllPath}' is relative, and the other process does not share this working directory.");

            string module = Path.GetFileName(dllPath);

            Process process;
            try
            {
                process = Process.GetProcessById(processId);
            }
            catch (ArgumentException)
            {
                return InjectionResult.Refused($"No process with id {processId} is running.");
            }

            IntPtr remoteBase = IntPtr.Zero;

            try
            {
                foreach (ProcessModule loaded in process.Modules)
                {
                    if (string.Equals(loaded.ModuleName, module, StringComparison.OrdinalIgnoreCase))
                    {
                        remoteBase = loaded.BaseAddress;
                        break;
                    }
                }
            }
            catch (Win32Exception)
            {
                return InjectionResult.Refused(
                    $"The modules of {process.ProcessName} ({processId}) could not be read, so whether {module} is loaded is unknown. "
                    + "This usually means the process is running with more privilege than this one.");
            }

            if (remoteBase == IntPtr.Zero)
                return InjectionResult.Refused($"{module} is not loaded in {process.ProcessName} ({processId}), so there is nothing to unload.");

            // Mapped, deliberately, without running DllMain or resolving imports: this is
            // only ever asked where a symbol sits, never asked to do anything.
            IntPtr local = NativeMethods.LoadLibraryEx(dllPath, IntPtr.Zero, NativeMethods.DontResolveDllReferences);
            if (local == IntPtr.Zero)
                return InjectionResult.Refused($"{dllPath} could not be read to find '{export}' in it.");

            IntPtr offset;
            try
            {
                IntPtr localExport = NativeMethods.GetProcAddress(local, export);
                if (localExport == IntPtr.Zero)
                    return InjectionResult.Refused($"{module} does not export '{export}'. Is it the same build that was injected?");

                offset = (IntPtr)(localExport.ToInt64() - local.ToInt64());
            }
            finally
            {
                NativeMethods.FreeLibrary(local);
            }

            IntPtr remoteExport = (IntPtr)(remoteBase.ToInt64() + offset.ToInt64());

            using ProcessHandle handle = NativeMethods.OpenProcess(NativeMethods.InjectAccess, false, processId);
            if (handle.IsInvalid)
                return InjectionResult.Refused($"{process.ProcessName} ({processId}) could not be opened. It may need the same elevation as this process.");

            IntPtr thread = NativeMethods.CreateRemoteThread(handle, IntPtr.Zero, 0, remoteExport, IntPtr.Zero, 0, IntPtr.Zero);
            if (thread == IntPtr.Zero)
                return InjectionResult.Refused($"'{export}' could not be called in {process.ProcessName} ({processId}).");

            try
            {
                // The export only starts the unload and returns; the DLL frees itself on
                // its own thread afterwards. So this waits for the request to be made,
                // not for the unloading to finish.
                NativeMethods.WaitForSingleObject(thread, 5000);
            }
            finally
            {
                NativeMethods.CloseHandle(thread);
            }

            return InjectionResult.Loaded($"{module} was asked to unload from {process.ProcessName} ({processId}).");
        }

        /// <summary>
        /// Waits for the module to actually disappear, which is what tells the caller the
        /// file is free to overwrite.
        /// </summary>
        public static bool WaitUntilGone(int processId, string module, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                Process process;
                try
                {
                    process = Process.GetProcessById(processId);
                    process.Refresh();
                }
                catch (ArgumentException)
                {
                    // The process has gone, so the module certainly has.
                    return true;
                }

                bool present = false;

                try
                {
                    foreach (ProcessModule loaded in process.Modules)
                    {
                        if (string.Equals(loaded.ModuleName, module, StringComparison.OrdinalIgnoreCase))
                        {
                            present = true;
                            break;
                        }
                    }
                }
                catch (Win32Exception)
                {
                    return false;
                }

                if (!present)
                    return true;

                System.Threading.Thread.Sleep(100);
            }

            return false;
        }
    }
}
