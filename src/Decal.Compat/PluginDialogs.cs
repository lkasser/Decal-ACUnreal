using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Decal.Compat
{
    /// <summary>
    /// Takes the message boxes Decal plugins show on the game thread out of the way: each one is
    /// read, said, and answered at once, instead of stopping the game thread until someone finds
    /// it behind the game and clicks it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Virindi's plugins, and others, report their own exceptions with MessageBox.Show. In the
    /// client that put a box over the game; here it would be a modal box on the host's game
    /// thread, which every plugin and the overlay wait on - the game would carry on and nothing
    /// else would, until the box was found and dismissed.
    /// </para>
    /// <para>
    /// A thread hook sees each dialog as it is about to be activated, reads its caption and text,
    /// moves it off the screen and ends it with the answer that does least: No where there is a No,
    /// Cancel where there is a Cancel, otherwise OK. What it said goes to the log and to the
    /// plugin's status, which is where a player looks for why a plugin is unwell.
    /// </para>
    /// </remarks>
    internal sealed class PluginDialogs : IDisposable
    {
        private const int WhCbt = 5;
        private const int HcbtActivate = 5;
        private const int IdOk = 1;
        private const int IdCancel = 2;
        private const int IdNo = 7;
        private const int MessageTextId = 0xFFFF;
        private const uint BmClick = 0x00F5;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;

        private readonly HookProc _proc;
        private readonly Action<string, string> _shown;
        private IntPtr _hook;

        private PluginDialogs(Action<string, string> shown)
        {
            _shown = shown;
            _proc = Proc;
        }

        /// <summary>
        /// Hooks the calling thread - the game thread - and tells <paramref name="shown"/> the
        /// caption and text of each dialog it answers. Null off Windows, or when the hook cannot
        /// be set, and then boxes show as they always did.
        /// </summary>
        public static PluginDialogs Install(Action<string, string> shown)
        {
            if (!OperatingSystem.IsWindows())
                return null;

            PluginDialogs dialogs = new PluginDialogs(shown);
            dialogs._hook = SetWindowsHookExW(WhCbt, dialogs._proc, IntPtr.Zero, GetCurrentThreadId());
            return dialogs._hook == IntPtr.Zero ? null : dialogs;
        }

        public void Dispose()
        {
            if (_hook == IntPtr.Zero)
                return;

            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        private IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code == HcbtActivate && IsDialog(wParam))
            {
                string caption = TextOf(wParam);
                string text = TextOf(GetDlgItem(wParam, MessageTextId));
                int answer = GetDlgItem(wParam, IdNo) != IntPtr.Zero ? IdNo : GetDlgItem(wParam, IdCancel) != IntPtr.Zero ? IdCancel : IdOk;

                // Two ways, because neither is taken everywhere: ended here, on its own thread,
                // before its loop runs, which a plain box takes; and its button clicked once its
                // loop does run, which a box in a program with visual styles on takes - there an
                // OK-only box's one button is numbered Cancel, and ending it early does nothing.
                // Whichever comes second finds the box already gone. A command posted to it is
                // taken by neither.
                SetWindowPos(wParam, IntPtr.Zero, -32000, -32000, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
                EndDialog(wParam, (IntPtr)answer);
                PostMessageW(GetDlgItem(wParam, answer), BmClick, IntPtr.Zero, IntPtr.Zero);

                try
                {
                    _shown?.Invoke(caption, text);
                }
                catch (Exception)
                {
                    // Whoever listens must not take the hook down with them.
                }
            }

            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        /// <summary>A standard dialog box - the class MessageBox's windows are.</summary>
        private static bool IsDialog(IntPtr window)
        {
            StringBuilder name = new StringBuilder(16);
            return GetClassNameW(window, name, name.Capacity) > 0 && name.ToString() == "#32770";
        }

        private static string TextOf(IntPtr window)
        {
            if (window == IntPtr.Zero)
                return string.Empty;

            int length = GetWindowTextLengthW(window);
            if (length <= 0)
                return string.Empty;

            StringBuilder text = new StringBuilder(length + 1);
            GetWindowTextW(window, text, text.Capacity);
            return text.ToString();
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookExW(int hook, HookProc proc, IntPtr module, uint threadId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr window, StringBuilder name, int capacity);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int capacity);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLengthW(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDlgItem(IntPtr dialog, int id);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EndDialog(IntPtr dialog, IntPtr result);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
