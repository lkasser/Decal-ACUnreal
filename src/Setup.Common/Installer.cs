using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Setup.Common
{
    /// <summary>What to install, where, and what else to do about it.</summary>
    public sealed class InstallRequest
    {
        public SetupProduct Product { get; set; }

        public Payload Payload { get; set; }

        /// <summary>Decal Agent's folder: where the Agent is installed, or where the plugin's Agent is.</summary>
        public string AgentFolder { get; set; }

        public string Version { get; set; }

        /// <summary>Where the Apps entry goes; null to write none (/NoRegistry).</summary>
        public IUserRegistry Registry { get; set; }

        /// <summary>What makes the shortcuts; null to make none and leave any there alone (/NoShortcuts).</summary>
        public IShortcutMaker Shortcuts { get; set; }

        /// <summary>The Start menu's Programs folder, for the Agent's shortcut; null for none.</summary>
        public string StartMenuFolder { get; set; }

        /// <summary>The desktop, for a shortcut there too; null for none.</summary>
        public string DesktopFolder { get; set; }

        /// <summary>
        /// Files of the payload to leave out, by their path within it: for a plugin, what the
        /// Agent beside it supplies itself, of which a plugin must not carry its own copy.
        /// </summary>
        public Func<string, bool> LeaveOut { get; set; }
    }

    /// <summary>What an install did.</summary>
    public sealed class InstallResult
    {
        public string AgentFolder { get; init; }

        /// <summary>The folder the product's files went to: the Agent's, or its plugin folder.</summary>
        public string ProductFolder { get; init; }

        public int Installed { get; init; }

        /// <summary>Files an earlier install had put there that this version no longer has, now deleted.</summary>
        public int Removed { get; init; }

        public IReadOnlyList<string> LeftOut { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> Shortcuts { get; init; } = Array.Empty<string>();

        /// <summary>Whether the Apps entry is written, now or by an earlier install.</summary>
        public bool Registered { get; init; }

        /// <summary>Whether it went over an earlier install.</summary>
        public bool Replaced { get; init; }

        public long Bytes { get; init; }
    }

    /// <summary>
    /// Installs a product's files into Decal Agent's folder, replacing an earlier install's: what
    /// the new version has is written over, what only the old one had is deleted, and the list of
    /// what is there is kept for the uninstaller. The player's settings, which are in the data
    /// folder and not here, are never touched.
    /// </summary>
    public static class Installer
    {
        public const string ShortcutName = "Decal Agent.lnk";

        /// <exception cref="SetupException">It could not be installed, and why.</exception>
        public static InstallResult Install(InstallRequest request, IProgress<SetupProgress> progress = null)
        {
            if (request?.Product == null || request.Payload == null || string.IsNullOrWhiteSpace(request.AgentFolder))
                throw new ArgumentException("An install needs a product, a payload and a folder.", nameof(request));

            if (!Path.IsPathFullyQualified(request.AgentFolder))
                throw new SetupException($"{request.AgentFolder} is not a full path to a folder.");

            SetupProduct product = request.Product;
            string root = Paths.Normalize(request.AgentFolder);
            if (product == SetupProduct.DecalAgent && !Paths.IsSafeToDeleteTree(root))
                throw new SetupException($"{root} is a folder Windows keeps other things in. Choose a folder of Decal Agent's own, such as {AgentFolder.DefaultFolder}.");

            string productFolder = product.Subfolder.Length == 0 ? root : Path.Combine(root, product.Subfolder);
            InstallManifest previous = InstallManifest.Load(root, product);
            bool replaced = previous != null || (product == SetupProduct.DecalAgent ? AgentFolder.IsAgentFolder(root) : Directory.Exists(productFolder));

            List<string> files = new List<string>();
            List<string> leftOut = new List<string>();
            foreach (string file in request.Payload.Files)
            {
                if (request.LeaveOut?.Invoke(file) == true)
                    leftOut.Add(file);
                else
                    files.Add(file);
            }

            HashSet<string> installing = new HashSet<string>(files.Select(file => InAgentFolder(product, file)), StringComparer.OrdinalIgnoreCase);

            // Before anything is written, a list of everything that may be there after it, so an
            // install that stops half way can still be uninstalled.
            InstallManifest manifest = new InstallManifest
            {
                Product = product.Id,
                Version = request.Version ?? string.Empty,
                InstalledUtc = DateTime.UtcNow,
                Files = Sorted(installing.Concat(previous?.Files ?? Enumerable.Empty<string>())),
                Shortcuts = previous?.Shortcuts ?? new List<string>(),
                Registered = previous?.Registered ?? false,
            };

            try
            {
                manifest.Save(root, product);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new SetupException($"Setup cannot write to {root}: {ex.Message} Choose a folder of your own, such as {AgentFolder.DefaultFolder}.", SetupExitCode.Failed, ex);
            }

            long bytes = 0;
            List<string> setAside = new List<string>();
            for (int i = 0; i < files.Count; i++)
            {
                string file = files[i];
                string destination = Path.Combine(productFolder, file);
                progress?.Report(new SetupProgress(i, files.Count, file));
                try
                {
                    // A file in use that had to be moved aside is the setup's to delete later, so
                    // it goes on the list at once, in case the install stops half way.
                    string aside = request.Payload.Extract(file, destination);
                    if (aside != null)
                    {
                        setAside.Add(Path.GetRelativePath(root, aside));
                        manifest.Files = Sorted(manifest.Files.Concat(setAside));
                        manifest.Save(root, product);
                    }

                    bytes += request.Payload.LengthOf(file);
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new SetupException($"Setup may not write {destination}: {ex.Message}", SetupExitCode.Failed, ex);
                }
                catch (IOException ex)
                {
                    throw new SetupException($"{destination} could not be written: {ex.Message} If Decal Agent is running from this folder, exit it (Exit, on its icon's menu in the notification area) and try again.", SetupExitCode.AgentRunning, ex);
                }
            }

            progress?.Report(new SetupProgress(files.Count, files.Count, null));

            // What the earlier version had and this one does not. One that cannot be deleted stays
            // on the list, for the uninstaller to try again.
            int removed = 0;
            List<string> stuck = new List<string>();
            if (previous != null)
            {
                List<string> stale = previous.Files.Where(file => !installing.Contains(file)).ToList();
                foreach (string file in stale)
                {
                    string path = Path.Combine(root, file);
                    if (!File.Exists(path))
                        continue;

                    if (FileRemoval.TryDelete(path))
                        removed++;
                    else
                        stuck.Add(file);
                }

                FileRemoval.PruneFolders(root, stale);
            }

            List<string> shortcuts = request.Shortcuts == null ? manifest.Shortcuts : MakeShortcuts(request, root, previous);

            bool registered = manifest.Registered;
            if (request.Registry != null && File.Exists(Path.Combine(root, AgentFolder.UninstallerName)))
            {
                UninstallEntry.Write(request.Registry, product, root, request.Version, bytes);
                registered = true;
            }

            manifest.Files = Sorted(installing.Concat(stuck).Concat(setAside));
            manifest.Shortcuts = shortcuts;
            manifest.Registered = registered;
            manifest.Save(root, product);

            return new InstallResult
            {
                AgentFolder = root,
                ProductFolder = productFolder,
                Installed = files.Count,
                Removed = removed,
                LeftOut = leftOut,
                Shortcuts = shortcuts,
                Registered = registered,
                Replaced = replaced,
                Bytes = bytes,
            };
        }

        /// <summary>A file of the product's payload, as a path within the Agent's folder.</summary>
        public static string InAgentFolder(SetupProduct product, string file)
            => product.Subfolder.Length == 0 ? file : Path.Combine(product.Subfolder, file);

        /// <summary>
        /// The Agent's shortcuts, as asked for this time; any an earlier install made that are
        /// not asked for again are deleted. Only the Agent has any: a plugin is started by it.
        /// </summary>
        private static List<string> MakeShortcuts(InstallRequest request, string root, InstallManifest previous)
        {
            List<string> made = new List<string>();
            if (request.Product == SetupProduct.DecalAgent)
            {
                foreach (string folder in new[] { request.StartMenuFolder, request.DesktopFolder })
                {
                    if (string.IsNullOrWhiteSpace(folder))
                        continue;

                    string path = Path.Combine(folder, ShortcutName);
                    try
                    {
                        request.Shortcuts.Create(new Shortcut
                        {
                            Path = path,
                            Target = Path.Combine(root, AgentFolder.ExeName),
                            WorkingDirectory = root,
                            Description = "Decal for AC:Unreal: runs the plugins and puts the overlay into the game.",
                        });
                        made.Add(path);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Runtime.InteropServices.COMException)
                    {
                        throw new SetupException($"The shortcut {path} could not be made: {ex.Message}", SetupExitCode.Failed, ex);
                    }
                }
            }

            foreach (string old in previous?.Shortcuts ?? new List<string>())
            {
                if (!made.Contains(old, StringComparer.OrdinalIgnoreCase))
                    FileRemoval.TryDelete(old);
            }

            return made;
        }

        private static List<string> Sorted(IEnumerable<string> files)
            => files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
