using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AC.Injector
{
    /// <summary>
    /// An open handle to another process, closed whatever happens on the way out.
    /// </summary>
    /// <remarks>
    /// Its own type rather than <see cref="SafeProcessHandle"/> because the interop
    /// marshaller constructs the return value of a P/Invoke itself and needs an accessible
    /// parameterless constructor to do it with.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    internal sealed class ProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ProcessHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    /// <summary>
    /// The Win32 calls that put a DLL into another process. Internal: nothing outside this
    /// assembly should be handling raw handles or error codes.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class NativeMethods
    {
        internal const uint ProcessCreateThread = 0x0002;
        internal const uint ProcessQueryInformation = 0x0400;
        internal const uint ProcessQueryLimitedInformation = 0x1000;
        internal const uint ProcessVmOperation = 0x0008;
        internal const uint ProcessVmRead = 0x0010;
        internal const uint ProcessVmWrite = 0x0020;

        /// <summary>Exactly the rights a remote LoadLibraryW needs, and no others.</summary>
        internal const uint InjectAccess =
            ProcessCreateThread | ProcessQueryInformation | ProcessVmOperation | ProcessVmRead | ProcessVmWrite;

        internal const uint MemCommit = 0x1000;
        internal const uint MemReserve = 0x2000;
        internal const uint MemRelease = 0x8000;
        internal const uint PageReadWrite = 0x04;

        internal const uint WaitObject0 = 0x00000000;
        internal const uint WaitTimeout = 0x00000102;

        internal const int ErrorAccessDenied = 5;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern ProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        /// <summary>
        /// True when the process runs under WOW64, which on a 64-bit Windows means it is a
        /// 32-bit process.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWow64Process(ProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr VirtualAllocEx(ProcessHandle process, IntPtr address, nuint size, uint allocationType, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool VirtualFreeEx(ProcessHandle process, IntPtr address, nuint size, uint freeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WriteProcessMemory(ProcessHandle process, IntPtr address, byte[] buffer, nuint size, out nuint written);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandle(string moduleName);

        /// <summary>
        /// Always ANSI: there is no GetProcAddressW, because an exported name is bytes in
        /// the module's export table rather than text.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern IntPtr GetProcAddress(IntPtr module, string name);

        /// <summary>
        /// Maps a DLL without running any of it: no DllMain, no imports resolved.
        /// </summary>
        /// <remarks>
        /// Used only to read where an export sits relative to its module's base, so the
        /// same export can be located in another process that has the same file mapped
        /// elsewhere. Running the DLL here would be pointless and unsafe: it is written
        /// to live inside a game, not inside this.
        /// </remarks>
        internal const uint DontResolveDllReferences = 0x00000001;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryExW")]
        internal static extern IntPtr LoadLibraryEx(string fileName, IntPtr reserved, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FreeLibrary(IntPtr module);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateRemoteThread(
            ProcessHandle process,
            IntPtr threadAttributes,
            nuint stackSize,
            IntPtr startAddress,
            IntPtr parameter,
            uint creationFlags,
            IntPtr threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        /// <summary>
        /// The exit code of a finished thread. For a remote LoadLibraryW that is the low
        /// half of the module handle it returned, because an exit code is a DWORD - so it
        /// tells success from failure and nothing more.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
