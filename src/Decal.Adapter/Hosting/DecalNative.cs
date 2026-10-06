using System;
using System.Runtime.InteropServices;

namespace Decal.Adapter.Hosting
{
    /// <summary>
    /// The entry points of Decal's native Decal.dll that plugins called directly, past
    /// Decal.Adapter. The host rewrites a registered plugin's working copy so that its P/Invoke
    /// of one of them comes here (<c>Decal.Compat.CallRewrite</c>): there is no Decal.dll in this
    /// process, and the one Decal installed is 32-bit.
    /// </summary>
    public static class DecalNative
    {
        /// <summary>
        /// Decal.dll's DispatchOnChatCommand: offers a line, as a BSTR, to every plugin's
        /// CommandLineText, as though the player had typed it - what Decal did with the chat box's
        /// line before the client had it. Bit 0 of the answer is set when a plugin ate it.
        /// </summary>
        /// <remarks>
        /// Mag-Tools sends its login and periodic commands this way, and the chat window Virindi
        /// HUDs drew its line: when no plugin eats the line they hand it to
        /// <see cref="Wrappers.HooksWrapper.InvokeChatParser"/>, which then goes straight on to the
        /// game. A plugin could change the line in Decal; here it reaches each plugin as it was.
        /// </remarks>
        /// <param name="text">The line, as a BSTR.</param>
        /// <param name="target">Decal's chat window for it; one chat box here, so unused.</param>
        public static int DispatchOnChatCommand(ref IntPtr text, int target)
        {
            DecalRuntime runtime = DecalRuntime.Current;
            string line = text == IntPtr.Zero ? null : Marshal.PtrToStringBSTR(text);
            if (runtime == null || string.IsNullOrEmpty(line))
                return 0;

            return runtime.DispatchCommandLine(line) ? 1 : 0;
        }
    }
}
