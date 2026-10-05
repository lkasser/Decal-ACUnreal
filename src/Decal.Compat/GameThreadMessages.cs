using System;
using System.Runtime.InteropServices;

namespace Decal.Compat
{
    /// <summary>
    /// Dispatches the window messages waiting for the calling thread - the game thread, when
    /// called from the tick.
    /// </summary>
    /// <remarks>
    /// A Windows Forms timer is a window of its thread's and ticks on WM_TIMER, which arrives
    /// only if something dispatches that thread's messages. In the client the game's own loop
    /// did; the host's game thread has no loop of the kind, so Decal plugins' timers would
    /// never fire. Dispatching what is waiting on each tick costs nothing when nothing is, and
    /// is capped so a flood of messages cannot hold the game thread.
    /// </remarks>
    internal static class GameThreadMessages
    {
        private const uint PmRemove = 0x0001;
        private const int MaxPerPump = 64;

        public static void Pump()
        {
            if (!OperatingSystem.IsWindows())
                return;

            for (int i = 0; i < MaxPerPump && PeekMessageW(out Msg message, IntPtr.Zero, 0, 0, PmRemove); i++)
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public IntPtr Hwnd;
            public uint Message;
            public UIntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X;
            public int Y;
            public uint Private;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessageW(out Msg message, IntPtr window, uint filterMin, uint filterMax, uint remove);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref Msg message);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessageW(ref Msg message);
    }
}
