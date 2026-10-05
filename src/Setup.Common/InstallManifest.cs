using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Setup.Common
{
    /// <summary>
    /// What a setup installed into Decal Agent's folder - every file, every shortcut, whether it
    /// wrote its Apps entry - kept there as DecalAgent.install.json or VirindiTank.install.json.
    /// The uninstaller deletes what is listed and nothing else, so a plugin the player put in the
    /// plugins folder themselves survives it.
    /// </summary>
    public sealed class InstallManifest
    {
        public string Product { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public DateTime InstalledUtc { get; set; }

        /// <summary>Each file, relative to the Agent's folder, with backslashes.</summary>
        public List<string> Files { get; set; } = new List<string>();

        /// <summary>Each shortcut made, as a full path.</summary>
        public List<string> Shortcuts { get; set; } = new List<string>();

        /// <summary>Whether the setup wrote the product's Apps entry (it does not with /NoRegistry).</summary>
        public bool Registered { get; set; }

        public static string PathIn(string agentFolder, SetupProduct product) => Path.Combine(agentFolder, product.ManifestName);

        /// <summary>The product's list from the Agent's folder, or null when it has none or it cannot be read.</summary>
        public static InstallManifest Load(string agentFolder, SetupProduct product)
        {
            string path = PathIn(agentFolder, product);
            try
            {
                if (!File.Exists(path))
                    return null;

                InstallManifest manifest = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path));
                if (manifest == null)
                    return null;

                manifest.Files ??= new List<string>();
                manifest.Shortcuts ??= new List<string>();

                // A hand-edited list must not be able to send the uninstaller outside the folder.
                manifest.Files.RemoveAll(file => !IsInside(file));
                return manifest;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        public void Save(string agentFolder, SetupProduct product)
        {
            Directory.CreateDirectory(agentFolder);
            string path = PathIn(agentFolder, product);
            string temporary = path + ".new";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }

        private static bool IsInside(string file)
        {
            try
            {
                return Payload.Check(file) != null;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }
    }
}
