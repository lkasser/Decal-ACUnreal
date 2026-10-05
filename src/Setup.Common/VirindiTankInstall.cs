using System;
using System.IO;
using AC.Dat;

namespace Setup.Common
{
    /// <summary>
    /// What Virindi Tank's setup needs to know beyond copying the plugin: which of the plugin's
    /// files the Agent supplies itself, and whether the real Virindi Tank is installed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plugin is this repository's code, and runs without the real Virindi Tank: the loot
    /// engine, its simple window and its commands are all its own. What it takes from a real
    /// install is read, never copied or written: Virindi Tank's own window, images and shipped
    /// defaults, out of utank2-i.dll; its game database beside it; and each character's settings
    /// and profiles in its folder. Without one the plugin says so in the log and shows its simple
    /// window instead.
    /// </para>
    /// <para>
    /// So the setup does not require Virindi Tank and bundles nothing of it - utank2-i.dll is
    /// not ours to redistribute - but looks for it the way the plugin will, through Decal's
    /// registry, and tells the player what they are missing when it is not there.
    /// </para>
    /// </remarks>
    public static class VirindiTankInstall
    {
        /// <summary>Virindi Tank's name in Decal's list of plugins, which the plugin finds it by.</summary>
        public const string DecalPluginName = "Virindi Tank";

        /// <summary>
        /// Whether a file of the plugin is one the Agent supplies itself: the host's contract and
        /// libraries, which a plugin must use the host's copy of. Judged as tools\install-plugin.ps1
        /// judges it, by whether a DLL of the same name stands beside DecalAgent.exe.
        /// </summary>
        public static bool IsSuppliedByAgent(string file, string agentFolder)
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            return !string.IsNullOrEmpty(stem) && File.Exists(Path.Combine(agentFolder, stem + ".dll"));
        }

        /// <summary>
        /// utank2-i.dll as Decal has Virindi Tank registered, if it is there; null otherwise.
        /// Decal's registry is read, never written.
        /// </summary>
        /// <param name="registry">Decal's registry; the real one when null.</param>
        public static string FindRegistered(IDecalRegistry registry = null)
        {
            try
            {
                string path = DecalInstall.Detect(registry).FindPlugin(DecalPluginName)?.FullPath;
                return path != null && File.Exists(path) ? path : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return null;
            }
        }

        /// <summary>What the player misses without a real Virindi Tank, for the setup's last page and the log.</summary>
        public const string MissingNote =
            "Virindi Tank itself is not installed with Decal on this computer. The plugin still loots, and shows a simple window of its own; "
            + "Virindi Tank's own window, its monster database and your characters' Virindi Tank settings come from a real Virindi Tank, which is read and never changed. "
            + "Install Virindi Tank with Decal to have them, then press Update in Decal Agent.";
    }
}
