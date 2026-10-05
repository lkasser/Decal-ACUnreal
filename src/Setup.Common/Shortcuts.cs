using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Setup.Common
{
    /// <summary>A shortcut to make: the .lnk file, and what it starts.</summary>
    public sealed class Shortcut
    {
        public string Path { get; set; }

        public string Target { get; set; }

        public string Arguments { get; set; } = string.Empty;

        public string WorkingDirectory { get; set; }

        public string Description { get; set; } = string.Empty;
    }

    /// <summary>Makes shortcuts. An interface so a run with /NoShortcuts, or a test, makes none in the Start menu.</summary>
    public interface IShortcutMaker
    {
        void Create(Shortcut shortcut);
    }

    /// <summary>Shortcuts made by the shell's own ShellLink object, as every installer makes them.</summary>
    public sealed class ShellShortcuts : IShortcutMaker
    {
        public void Create(Shortcut shortcut)
        {
            if (shortcut == null)
                throw new ArgumentNullException(nameof(shortcut));

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(shortcut.Path));

            IShellLinkW link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(shortcut.Target);
                link.SetArguments(shortcut.Arguments ?? string.Empty);
                link.SetWorkingDirectory(shortcut.WorkingDirectory ?? System.IO.Path.GetDirectoryName(shortcut.Target));
                link.SetDescription(shortcut.Description ?? string.Empty);
                link.SetIconLocation(shortcut.Target, 0);
                ((IPersistFile)link).Save(shortcut.Path, true);
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }

        /// <summary>What a shortcut starts, as the shell reads it back.</summary>
        internal static string ReadTarget(string path)
        {
            IShellLinkW link = (IShellLinkW)new ShellLink();
            try
            {
                ((IPersistFile)link).Load(path, 0);
                StringBuilder target = new StringBuilder(1024);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                return target.ToString();
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink
        {
        }

        /// <summary>IShellLinkW, in its vtable's order; only some of it is called.</summary>
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);

            void GetIDList(out IntPtr idList);

            void SetIDList(IntPtr idList);

            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);

            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int size);

            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int size);

            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

            void GetHotkey(out short hotkey);

            void SetHotkey(short hotkey);

            void GetShowCmd(out int showCommand);

            void SetShowCmd(int showCommand);

            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int size, out int index);

            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int index);

            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, int reserved);

            void Resolve(IntPtr window, int flags);

            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }
    }
}
