using System;
using System.Collections.Generic;
using System.IO;
using AC.Dat;

namespace Decal.Compat
{
    /// <summary>
    /// The plugins in Decal's registry whose job this host does itself, so that they are never
    /// loaded beside the host's own and run twice: Virindi Tank, the Virindi HUDs its plugin
    /// hosts, and the two hotkey systems whose windows are the host's.
    /// </summary>
    /// <remarks>
    /// An entry is known by its class id, which the plugin's installer chose and Decal names the
    /// key for; failing that by its assembly's file name, for an install that registered itself
    /// under another id; and, for a native plugin with neither a file nor a known id, by the name
    /// Decal lists it under.
    /// </remarks>
    internal static class Replacements
    {
        private sealed class Replacement
        {
            public Replacement(string clsid, string assemblyFileName, string name, string reason)
            {
                Clsid = clsid;
                AssemblyFileName = assemblyFileName;
                Name = name;
                Reason = reason;
            }

            public string Clsid { get; }

            public string AssemblyFileName { get; }

            public string Name { get; }

            public string Reason { get; }
        }

        private static readonly Replacement[] Known =
        {
            new Replacement("{642F1F48-16BE-48BF-B1D4-286652C4533E}", "utank2-i.dll", "Virindi Tank",
                "Replaced by this host: Virindi Tank runs as the host's own VirindiTank plugin, so the real one is not loaded beside it."),
            new Replacement("{C6B1DF06-FF20-459E-8302-AA346CBFDA01}", "VirindiHUDs.dll", "Virindi HUDs",
                "Replaced by this host: the HUDs - Virindi UIs, the MiniRemote, the Comps and Status HUDs - are drawn by the host's VirindiTank plugin, so the real one is not loaded beside it."),
            new Replacement("{ED7CC818-7159-461F-A833-4CA49E1C85B6}", "VirindiHotkeySystem.dll", "Virindi Hotkey System",
                "Replaced by this host: its window, on VVS's bar, is the host's own, over the host's hotkeys, so the real one is not loaded beside it."),
            // Native, part of Decal itself: no assembly of its own to know it by.
            new Replacement("{6B6B9FA8-37DE-4FA3-8C60-52BD6A2F9855}", null, "Decal Hotkey System",
                "Replaced by this host: its window, on Decal's bar, is the host's own, over the host's hotkeys. The real one is a native part of Decal and could not run here in any case."),
        };

        /// <summary>
        /// Why the host does not load this entry because it does the entry's job itself; null when
        /// it does not.
        /// </summary>
        public static string ReasonFor(DecalRegistryEntry entry)
        {
            if (entry == null)
                return null;

            string file = string.IsNullOrWhiteSpace(entry.Assembly) ? null : Path.GetFileName(entry.Assembly.Trim());
            bool native = string.IsNullOrWhiteSpace(entry.Path) && file == null;

            foreach (Replacement known in Known)
            {
                if (SameClsid(entry.Clsid, known.Clsid)
                    || (file != null && known.AssemblyFileName != null && string.Equals(file, known.AssemblyFileName, StringComparison.OrdinalIgnoreCase))
                    || (native && string.Equals(entry.Name?.Trim(), known.Name, StringComparison.OrdinalIgnoreCase)))
                    return known.Reason;
            }

            return null;
        }

        /// <summary>
        /// The same, for an assembly put in the compatibility plugin's own folder: known by its
        /// file name alone, since nothing registered it.
        /// </summary>
        public static string ReasonForFile(string assemblyPath)
            => string.IsNullOrEmpty(assemblyPath) ? null : ReasonFor(new DecalRegistryEntry(null, null, Path.GetDirectoryName(assemblyPath), Path.GetFileName(assemblyPath)));

        /// <summary>Every class id the host replaces, for the log and the tests.</summary>
        public static IEnumerable<string> Clsids
        {
            get
            {
                foreach (Replacement known in Known)
                    yield return known.Clsid;
            }
        }

        /// <summary>Class ids compared as Windows compares them: braces kept, case ignored.</summary>
        public static bool SameClsid(string a, string b)
            => a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>The form class ids are kept in in the compatibility plugin's settings.</summary>
        public static string NormaliseClsid(string clsid) => clsid?.Trim().ToUpperInvariant();
    }
}
