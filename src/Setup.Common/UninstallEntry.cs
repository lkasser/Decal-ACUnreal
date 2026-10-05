using System;
using System.Globalization;
using System.IO;

namespace Setup.Common
{
    /// <summary>
    /// A product's entry in Apps (Programs and Features), for the current user only, and the
    /// key recording where Decal Agent is installed, which Virindi Tank's setup looks for.
    /// </summary>
    /// <remarks>
    /// Taken out again only when it points at the folder being uninstalled: a trial install
    /// somewhere else, uninstalled, must not take the real install's entry with it.
    /// </remarks>
    public static class UninstallEntry
    {
        public const string Root = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

        /// <summary>Who Apps says made it.</summary>
        public const string Publisher = "Decal for AC:Unreal";

        public static string KeyFor(SetupProduct product) => Root + "\\" + product.UninstallKeyName;

        /// <summary>
        /// Writes the product's entry, uninstalled by the uninstaller in the Agent's folder; for the
        /// Agent, also where it is. <paramref name="bytes"/> is what its files take up.
        /// </summary>
        public static void Write(IUserRegistry registry, SetupProduct product, string agentFolder, string version, long bytes)
        {
            string key = KeyFor(product);
            string folder = Paths.Normalize(agentFolder);
            string uninstaller = Path.Combine(folder, AgentFolder.UninstallerName);
            string arguments = product == SetupProduct.DecalAgent ? string.Empty : " /Product=" + product.Id;

            registry.SetValue(key, "DisplayName", product.DisplayName);
            registry.SetValue(key, "DisplayVersion", version ?? string.Empty);
            registry.SetValue(key, "Publisher", Publisher);
            registry.SetValue(key, "InstallLocation", product == SetupProduct.DecalAgent ? folder : Path.Combine(folder, product.Subfolder));
            registry.SetValue(key, "DisplayIcon", Path.Combine(folder, AgentFolder.ExeName) + ",0");
            registry.SetValue(key, "UninstallString", "\"" + uninstaller + "\"" + arguments);
            registry.SetValue(key, "QuietUninstallString", "\"" + uninstaller + "\"" + arguments + " /S");
            registry.SetValue(key, "InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            registry.SetValue(key, "EstimatedSize", (int)Math.Min(int.MaxValue, (bytes + 1023) / 1024));
            registry.SetValue(key, "NoModify", 1);
            registry.SetValue(key, "NoRepair", 1);

            // What Virindi Tank's setup finds the Agent by.
            if (product == SetupProduct.DecalAgent)
            {
                registry.SetValue(AgentFolder.RegistryKey, AgentFolder.InstallDirValue, folder);
                registry.SetValue(AgentFolder.RegistryKey, AgentFolder.VersionValue, version ?? string.Empty);
            }
        }

        /// <summary>Whether the product's entry is for the Agent in this folder.</summary>
        public static bool PointsAt(IUserRegistry registry, SetupProduct product, string agentFolder)
        {
            string expected = product == SetupProduct.DecalAgent ? agentFolder : Path.Combine(agentFolder, product.Subfolder);
            return registry.GetValue(KeyFor(product), "InstallLocation") is string location && Paths.Same(location, expected);
        }

        /// <summary>
        /// Takes the product's entry out, and for the Agent the key saying where it is - each only
        /// if it is for this folder. True when anything was taken out.
        /// </summary>
        public static bool Remove(IUserRegistry registry, SetupProduct product, string agentFolder)
        {
            bool removed = false;
            if (PointsAt(registry, product, agentFolder))
            {
                registry.DeleteKey(KeyFor(product));
                removed = true;
            }

            if (product == SetupProduct.DecalAgent
                && registry.GetValue(AgentFolder.RegistryKey, AgentFolder.InstallDirValue) is string installed
                && Paths.Same(installed, agentFolder))
            {
                registry.DeleteKey(AgentFolder.RegistryKey);
                removed = true;
            }

            return removed;
        }
    }
}
