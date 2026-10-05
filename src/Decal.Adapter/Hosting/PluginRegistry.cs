using System;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Decal.Adapter.Hosting
{
    /// <summary>
    /// The registry as a Decal plugin saw it from inside 32-bit Decal, for the plugins this host
    /// runs in a 64-bit process. The host rewrites a plugin's working copy so that its reads of
    /// HKEY_LOCAL_MACHINE come here (<c>Decal.Compat.RegistryRewrite</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Virindi's tools find their folder by reading their own key under
    /// HKLM\SOFTWARE\Decal\Plugins through the default view, which in 32-bit Decal was the 32-bit
    /// one: in this process the same call finds nothing, and they fail at Startup or at login.
    /// Here HKEY_LOCAL_MACHINE is the 32-bit view.
    /// </para>
    /// <para>
    /// A plugin's own Path is answered with the folder the host runs it from - its working copy -
    /// so what it reads and writes there stays out of the player's install, as everything else it
    /// does already does. Keys are only ever opened for reading: a plugin that asks to write one
    /// gets it read-only, and its write fails as it would have without rights to HKLM.
    /// </para>
    /// </remarks>
    public static class PluginRegistry
    {
        private static readonly Regex OwnKey = new Regex(@"\\Decal\\Plugins\\(\{[0-9A-Fa-f-]{36}\})$", RegexOptions.CultureInvariant);

        /// <summary>
        /// The folder the host runs a registered plugin from, by its class id ("{...}"); null for
        /// one it does not. Set by the host.
        /// </summary>
        public static Func<string, string> FolderFor { get; set; }

        /// <summary>HKEY_LOCAL_MACHINE, as 32-bit Decal saw it: <c>Registry.LocalMachine</c>.</summary>
        public static RegistryKey LocalMachine() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);

        /// <summary><c>key.OpenSubKey(name, writable)</c>, always for reading.</summary>
        public static RegistryKey OpenSubKey(RegistryKey key, string name, bool writable)
            => key.OpenSubKey(name, false);

        /// <summary><c>key.GetValue(name)</c>, with a registered plugin's Path its working copy.</summary>
        public static object GetValue(RegistryKey key, string name)
            => Answer(key, name, key.GetValue(name), FolderFor);

        /// <summary><c>key.GetValue(name, defaultValue)</c>, with a registered plugin's Path its working copy.</summary>
        public static object GetValue(RegistryKey key, string name, object defaultValue)
            => Answer(key, name, key.GetValue(name, defaultValue), FolderFor);

        /// <summary>
        /// <c>Registry.GetValue(keyName, valueName, defaultValue)</c>: HKEY_LOCAL_MACHINE through
        /// the 32-bit view, anything else as the runtime reads it.
        /// </summary>
        public static object GetValue(string keyName, string valueName, object defaultValue)
        {
            const string Machine = "HKEY_LOCAL_MACHINE\\";
            if (keyName == null || !keyName.StartsWith(Machine, StringComparison.OrdinalIgnoreCase))
                return Registry.GetValue(keyName, valueName, defaultValue);

            using RegistryKey root = LocalMachine();
            using RegistryKey key = root.OpenSubKey(keyName.Substring(Machine.Length), false);
            if (key == null)
                return null;
            return Answer(key, valueName, key.GetValue(valueName, defaultValue), FolderFor);
        }

        /// <summary>
        /// What a read returns: a registered plugin's Path the folder <paramref name="folderFor"/>
        /// gives for its class id, all else as read.
        /// </summary>
        public static object Answer(RegistryKey key, string name, object value, Func<string, string> folderFor)
        {
            if (!string.Equals(name, "Path", StringComparison.OrdinalIgnoreCase) || key == null)
                return value;

            Match match = OwnKey.Match(key.Name ?? string.Empty);
            if (!match.Success)
                return value;

            string folder = folderFor?.Invoke(match.Groups[1].Value);
            if (string.IsNullOrEmpty(folder))
                return value;

            // Written as Decal's installer wrote it: Virindi's with a closing backslash, others without.
            folder = folder.TrimEnd('\\');
            return value is string written && written.EndsWith("\\", StringComparison.Ordinal) ? folder + "\\" : folder;
        }
    }
}
