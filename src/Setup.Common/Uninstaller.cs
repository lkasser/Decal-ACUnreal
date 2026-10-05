using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Setup.Common
{
    /// <summary>What to uninstall, from where, and whether the settings go too.</summary>
    public sealed class UninstallRequest
    {
        public SetupProduct Product { get; set; }

        /// <summary>Decal Agent's folder, which holds the lists of what was installed.</summary>
        public string AgentFolder { get; set; }

        /// <summary>Where the Apps entries are; null to leave the registry alone (/NoRegistry).</summary>
        public IUserRegistry Registry { get; set; }

        /// <summary>Whether to delete the settings too. Never without the player asking.</summary>
        public bool DeleteSettings { get; set; }

        /// <summary>The host's data folder, %LOCALAPPDATA%\ACHost unless given.</summary>
        public string DataFolder { get; set; }
    }

    /// <summary>What an uninstall did, and what it left for later.</summary>
    public sealed class UninstallResult
    {
        public IReadOnlyList<SetupProduct> Uninstalled { get; init; } = Array.Empty<SetupProduct>();

        public int Removed { get; init; }

        /// <summary>
        /// Files that could not be deleted because they are in use - the uninstaller's own, while
        /// it runs - to be deleted once it has exited (see <see cref="DeferredDelete"/>).
        /// </summary>
        public IReadOnlyList<string> InUse { get; init; } = Array.Empty<string>();

        /// <summary>Folders to take out once those files are gone, deepest first; each only if empty by then.</summary>
        public IReadOnlyList<string> FoldersToRemove { get; init; } = Array.Empty<string>();

        /// <summary>
        /// What is in the uninstalled product's folder that no setup put there - a plugin the
        /// player added to the Agent's, say - and is left alone, by its top-level names.
        /// </summary>
        public IReadOnlyList<string> LeftBehind { get; init; } = Array.Empty<string>();

        /// <summary>The settings folder that was, or would have been, deleted.</summary>
        public string SettingsFolder { get; init; }

        public bool SettingsDeleted { get; init; }

        /// <summary>Why the settings were not deleted when asked to be; null otherwise.</summary>
        public string SettingsProblem { get; init; }
    }

    /// <summary>
    /// Uninstalls what a setup installed, by its list: the files and shortcuts on it, the folders
    /// they leave empty, and its Apps entry. Uninstalling Decal Agent takes Virindi Tank with it,
    /// since Virindi Tank lives in the Agent's plugins folder and runs nowhere else.
    /// </summary>
    public static class Uninstaller
    {
        /// <summary>The products with a list in this folder: what an uninstall of the Agent would take out.</summary>
        public static IReadOnlyList<SetupProduct> InstalledIn(string agentFolder)
            => SetupProduct.All.Where(product => File.Exists(InstallManifest.PathIn(agentFolder, product))).ToList();

        public static UninstallResult Uninstall(UninstallRequest request)
        {
            if (request?.Product == null || string.IsNullOrWhiteSpace(request.AgentFolder))
                throw new ArgumentException("An uninstall needs a product and a folder.", nameof(request));

            string root = Paths.Normalize(request.AgentFolder);
            List<SetupProduct> products = request.Product == SetupProduct.DecalAgent
                ? SetupProduct.All.Where(product => product == SetupProduct.DecalAgent || File.Exists(InstallManifest.PathIn(root, product))).ToList()
                : new List<SetupProduct> { request.Product };

            int removed = 0;
            List<string> inUse = new List<string>();
            List<string> folders = new List<string>();
            foreach (SetupProduct product in products)
                removed += UninstallOne(product, root, request.Registry, inUse, folders);

            if (request.Product == SetupProduct.DecalAgent && !FileRemoval.TryDeleteEmptyFolder(root))
                folders.Add(root);

            string data = request.DataFolder ?? AgentFolder.DefaultDataFolder;
            string settings = request.Product.DataSubfolder.Length == 0 ? data : Path.Combine(data, request.Product.DataSubfolder);
            bool settingsDeleted = false;
            string settingsProblem = null;
            if (request.DeleteSettings && Directory.Exists(settings))
            {
                if (!Paths.IsSafeToDeleteTree(settings))
                {
                    settingsProblem = $"{settings} is not a folder the uninstaller will delete whole.";
                }
                else
                {
                    try
                    {
                        Directory.Delete(settings, recursive: true);
                        settingsDeleted = true;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        settingsProblem = ex.Message;
                    }
                }
            }

            return new UninstallResult
            {
                Uninstalled = products,
                Removed = removed,
                InUse = inUse,
                FoldersToRemove = folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(folder => folder.Count(c => c == Path.DirectorySeparatorChar)).ToList(),
                LeftBehind = LeftBehind(request.Product.Subfolder.Length == 0 ? root : Path.Combine(root, request.Product.Subfolder), inUse),
                SettingsFolder = settings,
                SettingsDeleted = settingsDeleted,
                SettingsProblem = settingsProblem,
            };
        }

        private static int UninstallOne(SetupProduct product, string root, IUserRegistry registry, List<string> inUse, List<string> folders)
        {
            int removed = 0;
            InstallManifest manifest = InstallManifest.Load(root, product);
            if (manifest != null)
            {
                foreach (string shortcut in manifest.Shortcuts)
                    FileRemoval.TryDelete(shortcut);

                int stillThere = inUse.Count;
                foreach (string file in manifest.Files)
                {
                    string path = Path.Combine(root, file);
                    bool existed = File.Exists(path);
                    if (!FileRemoval.TryDelete(path))
                        inUse.Add(path);
                    else if (existed)
                        removed++;
                }

                List<string> productFolders = FileRemoval.FoldersOf(root, manifest.Files);
                if (product.Subfolder.Length > 0)
                    productFolders.Add(Path.Combine(root, product.Subfolder));

                foreach (string folder in productFolders)
                {
                    if (!FileRemoval.TryDeleteEmptyFolder(folder))
                        folders.Add(folder);
                }

                // Kept, and deleted last, while anything it lists is still there to be deleted.
                string list = InstallManifest.PathIn(root, product);
                if (inUse.Count > stillThere)
                    inUse.Add(list);
                else
                    FileRemoval.TryDelete(list);
            }

            if (registry != null)
                UninstallEntry.Remove(registry, product, root);

            return removed;
        }

        /// <summary>What is left in a folder besides the files waiting to be deleted, by its top-level names.</summary>
        private static List<string> LeftBehind(string root, List<string> inUse)
        {
            List<string> left = new List<string>();
            if (!Directory.Exists(root))
                return left;

            HashSet<string> waiting = new HashSet<string>(inUse, StringComparer.OrdinalIgnoreCase);
            foreach (string entry in Directory.EnumerateFileSystemEntries(root))
            {
                bool other = Directory.Exists(entry)
                    ? Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories).Any(file => !waiting.Contains(file))
                    : !waiting.Contains(entry);
                if (other)
                    left.Add(Path.GetFileName(entry));
            }

            return left;
        }
    }
}
