using System;
using System.IO;

namespace Setup.Common
{
    /// <summary>
    /// Where Decal Agent is installed, and the names of what is in its folder that the setups
    /// need to know: the Agent itself, its uninstaller, and the registry value saying where it is.
    /// </summary>
    public static class AgentFolder
    {
        /// <summary>Decal Agent's program; a folder holding it is an Agent's folder.</summary>
        public const string ExeName = "DecalAgent.exe";

        /// <summary>The uninstaller the Agent's setup leaves in its folder, which uninstalls either product.</summary>
        public const string UninstallerName = "Uninstall.exe";

        /// <summary>Decal Agent's own key under HKEY_CURRENT_USER. Not Decal's: that is HKLM\SOFTWARE\Decal, and is never touched.</summary>
        public const string RegistryKey = @"Software\Decal Agent";

        /// <summary>The value under <see cref="RegistryKey"/> naming the folder the Agent is installed in.</summary>
        public const string InstallDirValue = "InstallDir";

        /// <summary>The version installed there, beside it.</summary>
        public const string VersionValue = "Version";

        /// <summary>%LOCALAPPDATA%\Programs\Decal Agent: per user, like AC:Unreal's own install, so no administrator is needed.</summary>
        public static string DefaultFolder
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Decal Agent");

        /// <summary>
        /// %LOCALAPPDATA%\ACHost, where the Agent keeps its settings, its plugins' settings and its
        /// logs. Never the setups' to delete, unless the player asks for it when uninstalling.
        /// </summary>
        public static string DefaultDataFolder
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ACHost");

        /// <summary>Whether this folder holds a Decal Agent.</summary>
        public static bool IsAgentFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return false;

            try
            {
                return File.Exists(Path.Combine(folder, ExeName));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// The folder the Agent's setup recorded it installed into, if a Decal Agent is still
        /// there; null otherwise, and the player is asked.
        /// </summary>
        public static string Find(IUserRegistry registry)
        {
            if (registry?.GetValue(RegistryKey, InstallDirValue) is string folder && IsAgentFolder(folder))
                return Paths.Normalize(folder);

            return null;
        }
    }
}
