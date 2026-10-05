using System;
using System.IO;

namespace Setup.Common
{
    /// <summary>
    /// One of the two things these setups install, and the names it is installed under: its
    /// entry in Apps, the list of files it leaves beside Decal Agent, and the folder its files go
    /// to within Decal Agent's.
    /// </summary>
    /// <remarks>
    /// Both live in Decal Agent's folder - the Agent at its root, Virindi Tank in its plugins
    /// folder, as any plugin is - so the folder a product's files are listed against is always
    /// the Agent's, and one uninstaller there can take out either.
    /// </remarks>
    public sealed class SetupProduct
    {
        private SetupProduct(string id, string displayName, string subfolder, string dataSubfolder)
        {
            Id = id;
            DisplayName = displayName;
            Subfolder = subfolder;
            DataSubfolder = dataSubfolder;
        }

        /// <summary>Decal Agent itself: DecalAgent.exe, its runtime, Decal.Compat, the overlay and acinject.</summary>
        public static SetupProduct DecalAgent { get; } = new SetupProduct("DecalAgent", "Decal Agent", string.Empty, string.Empty);

        /// <summary>The Virindi Tank plugin, in the Agent's plugins\VirindiTank.</summary>
        public static SetupProduct VirindiTank { get; } = new SetupProduct("VirindiTank", "Virindi Tank for Decal Agent", Path.Combine("plugins", "VirindiTank"), Path.Combine("plugins", "VirindiTank"));

        /// <summary>The products a Decal Agent folder can hold, the Agent last: it is taken out after what lives inside it.</summary>
        public static SetupProduct[] All { get; } = { VirindiTank, DecalAgent };

        /// <summary>A short name without spaces, for the command line and file names.</summary>
        public string Id { get; }

        /// <summary>What Apps and the wizard call it.</summary>
        public string DisplayName { get; }

        /// <summary>Where in Decal Agent's folder its files go; empty for the Agent, which is the folder.</summary>
        public string Subfolder { get; }

        /// <summary>
        /// Where in the host's data folder its settings are: the Agent's are all of it, a plugin's
        /// are under plugins\ by the plugin's name, as the host keeps them.
        /// </summary>
        public string DataSubfolder { get; }

        /// <summary>The key its uninstall entry has under Uninstall; Virindi Tank's names the Agent it is installed in.</summary>
        public string UninstallKeyName => this == DecalAgent ? "DecalAgent" : "DecalAgent." + Id;

        /// <summary>The list of what it installed, kept in Decal Agent's folder.</summary>
        public string ManifestName => Id + ".install.json";

        /// <summary>A product by its <see cref="Id"/>, ignoring case; null when there is none of that name.</summary>
        public static SetupProduct Find(string id)
        {
            foreach (SetupProduct product in All)
            {
                if (string.Equals(product.Id, id, StringComparison.OrdinalIgnoreCase))
                    return product;
            }

            return null;
        }

        public override string ToString() => DisplayName;
    }
}
