using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text.Json;

namespace AC.Host.Plugins
{
    /// <summary>One plugin assembly the host knows about, loaded or not.</summary>
    public sealed class PluginEntry
    {
        internal PluginEntry(string assemblyPath)
        {
            AssemblyPath = assemblyPath;
            Name = Path.GetFileNameWithoutExtension(assemblyPath);
        }

        /// <summary>The plugin's own name once it has loaded; the file's until then.</summary>
        public string Name { get; internal set; }

        /// <summary>Where the plugin is installed. It runs from a copy, so this file can be rebuilt.</summary>
        public string AssemblyPath { get; }

        /// <summary>Whether the player wants it running. Remembered between sessions.</summary>
        public bool Enabled { get; internal set; } = true;

        /// <summary>Running, off, or what went wrong, in a few words for the Decal window.</summary>
        public string Status { get; internal set; } = "not loaded";

        /// <summary>The assembly's version, when it has loaded.</summary>
        public string Version { get; internal set; } = string.Empty;

        /// <summary>True while it is one of the host's plugins.</summary>
        public bool IsRunning => Plugin != null;

        public IPlugin Plugin { get; internal set; }

        internal AssemblyLoadContext Context { get; set; }

        internal string ShadowDirectory { get; set; }
    }

    /// <summary>
    /// Decal's plugin list: finds the plugins installed in the host's plugin folder, loads the
    /// ones the player has switched on, and switches them on, off, or reloads them while the
    /// host runs - without the reconnect a restart of the host would cost.
    /// </summary>
    /// <remarks>
    /// Each plugin runs from a copy of its folder, in a load context that can be unloaded. The
    /// copy is what lets a plugin be rebuilt in place and reloaded: the files in the plugin
    /// folder are never held open. Unloading is best effort - a plugin that leaves something
    /// of its own referenced from outside keeps its old copy in memory - but the old copy is
    /// shut down and cut off from the host's events either way, so it does nothing more.
    ///
    /// Which plugins are switched off is kept in the host's data directory, so a plugin the
    /// player turned off stays off next time.
    ///
    /// Everything here runs before the host starts or on its game thread, as everything that
    /// touches a plugin does.
    /// </remarks>
    public sealed class PluginManager
    {
        private readonly GameHost _host;
        private readonly string _directory;
        private readonly IPluginLog _log;
        private readonly string _stateFile;
        private readonly string _shadowRoot;
        private readonly List<PluginEntry> _entries = new List<PluginEntry>();

        public PluginManager(GameHost host, string pluginDirectory, IPluginLog log, string dataRoot = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _directory = pluginDirectory;
            _log = log ?? throw new ArgumentNullException(nameof(log));

            string root = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ACHost");
            _stateFile = Path.Combine(root, "plugins.json");
            _shadowRoot = Path.Combine(root, "running", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ClearOldCopies(Path.Combine(root, "running"));
        }

        /// <summary>Every plugin found, in the order they were found.</summary>
        public IReadOnlyList<PluginEntry> Entries => _entries;

        /// <summary>The plugin folder this manages.</summary>
        public string Directory => _directory;

        /// <summary>Raised when the list or any entry's state changes.</summary>
        public event EventHandler Changed;

        /// <summary>
        /// Finds the installed plugins and starts the ones switched on. Before the host starts
        /// they are simply added to it; afterwards they start as they load.
        /// </summary>
        public void LoadAll()
        {
            HashSet<string> disabled = ReadDisabled();

            foreach (string path in Candidates())
            {
                if (_entries.Any(e => SamePath(e.AssemblyPath, path)))
                    continue;

                Load(new PluginEntry(path), disabled);
            }

            OnChanged();
        }

        /// <summary>Looks for plugins installed since the last look, and loads them.</summary>
        public void Rescan() => LoadAll();

        /// <summary>Switches a plugin on or off, and remembers the choice.</summary>
        public void SetEnabled(PluginEntry entry, bool enabled)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            entry.Enabled = enabled;
            SaveDisabled();

            if (enabled && !entry.IsRunning)
                Load(entry);
            else if (!enabled && entry.IsRunning)
                Stop(entry, "off");

            _log.Info($"Plugin {entry.Name} switched {(enabled ? "on" : "off")}.");
            OnChanged();
        }

        /// <summary>
        /// Shuts a plugin down and loads it again from its folder, picking up a rebuilt copy.
        /// A plugin that is switched off is only re-read, not started.
        /// </summary>
        public void Reload(PluginEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            if (entry.IsRunning)
                Stop(entry, "reloading");

            if (entry.Enabled)
            {
                if (Load(entry))
                    _log.Info($"Plugin {entry.Name} reloaded from {entry.AssemblyPath}.");
            }

            OnChanged();
        }

        public PluginEntry Find(string name)
            => _entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------- installing and removing

        /// <summary>
        /// Installs a plugin into the plugin folder and starts it - or, when that plugin's assembly
        /// is already installed, copies the new one over it and reloads it, so there is still only
        /// one of it. Before the host starts or on its game thread, as everything here is.
        /// </summary>
        /// <param name="source">
        /// A plugin assembly, copied into a folder of its own with whatever it needs from beside
        /// it; or a folder, copied whole - the way to install a plugin that keeps data files.
        /// </param>
        /// <returns>
        /// The plugin's entry, whose status says whether it started; or null if, once installed, it
        /// would not load at all, which the log explains.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// There is no plugin for this host there, or it is in the plugin folder already.
        /// </exception>
        /// <exception cref="IOException">The files could not be copied.</exception>
        public PluginEntry Install(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
                throw new ArgumentException("Nothing was named to install.", nameof(source));
            if (string.IsNullOrEmpty(_directory))
                throw new InvalidOperationException("This host has no plugin folder to install into.");

            string full = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
            bool isFolder = System.IO.Directory.Exists(full);
            if (!isFolder && !File.Exists(full))
                throw new FileNotFoundException($"There is nothing at {full}.", full);

            if (PluginFiles.IsWithin(full, _directory))
                throw new InvalidOperationException($"{full} is in the plugin folder already; refreshing the list finds it there.");

            // Recognised the same way an installed plugin is: by the contract it declares.
            List<string> assemblies = isFolder
                ? System.IO.Directory.GetFiles(full, "*.dll").Where(DeclaresPluginApi).ToList()
                : DeclaresPluginApi(full) ? new List<string> { full } : new List<string>();

            if (assemblies.Count == 0)
            {
                throw new InvalidOperationException(isFolder
                    ? $"There is no plugin for this host in {full}: none of its assemblies says which host contract it was built against."
                    : $"{Path.GetFileName(full)} is not a plugin for this host: it does not say which host contract it was built against.");
            }

            PluginEntry existing = _entries.FirstOrDefault(e => assemblies.Any(a => string.Equals(Path.GetFileName(a), Path.GetFileName(e.AssemblyPath), StringComparison.OrdinalIgnoreCase)));
            string target = existing != null
                ? Path.GetDirectoryName(Path.GetFullPath(existing.AssemblyPath))
                : Path.Combine(_directory, isFolder ? Path.GetFileName(full) : Path.GetFileNameWithoutExtension(full));

            // The running copy is a shadow, so the installed files are never held open and the
            // new ones can go straight over them.
            if (isFolder)
                PluginFiles.CopyFolder(full, target);
            else
                PluginFiles.CopyAssembly(full, target);

            _log.Info($"Installed {Path.GetFileName(full)} into {target}.");

            if (existing != null)
            {
                Reload(existing);
                return existing;
            }

            LoadAll();
            string installed = Path.Combine(target, Path.GetFileName(assemblies[0]));
            return _entries.FirstOrDefault(e => SamePath(e.AssemblyPath, installed));
        }

        /// <summary>
        /// Switches a plugin off, takes it off the list, and deletes it from the plugin folder: its
        /// own folder when it has one, otherwise just its assembly and the files that go with it.
        /// Before the host starts or on its game thread.
        /// </summary>
        /// <remarks>
        /// Its data - settings, profiles - is kept in the host's data directory, not the plugin
        /// folder, and is left alone: installing it again finds everything as it was.
        /// </remarks>
        /// <exception cref="IOException">
        /// Some of its files could not be deleted. It is off and off the list regardless; whatever
        /// is left is found again by the next rescan.
        /// </exception>
        public void Uninstall(PluginEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            if (entry.IsRunning)
                Stop(entry, "removed");
            else
                entry.Status = "removed";

            _entries.Remove(entry);
            SaveDisabled();
            OnChanged();

            string assembly = Path.GetFullPath(entry.AssemblyPath);
            string folder = Path.GetDirectoryName(assembly);

            // Its own folder is one directly under the plugin folder that nothing else still
            // listed is installed in.
            bool ownFolder = !SamePath(folder, _directory)
                && Path.GetDirectoryName(folder) is string parent && SamePath(parent, _directory)
                && !_entries.Any(e => SamePath(Path.GetDirectoryName(Path.GetFullPath(e.AssemblyPath)), folder));

            if (ownFolder)
            {
                System.IO.Directory.Delete(folder, recursive: true);
            }
            else
            {
                foreach (string file in PluginFiles.WithCompanions(assembly))
                    File.Delete(file);
            }

            _log.Info($"Plugin {entry.Name} removed: {(ownFolder ? folder : assembly)} deleted.");
        }

        // ------------------------------------------------------------------- loading

        /// <summary>
        /// Loads a plugin and starts it - unless <paramref name="disabled"/> names it, in which
        /// case it is only read, for its name and version, and listed as off. Its constructor
        /// runs either way; its Startup only when it is to run.
        /// </summary>
        private bool Load(PluginEntry entry, ISet<string> disabled = null)
        {
            if (!_entries.Contains(entry))
                _entries.Add(entry);

            string copy;
            try
            {
                copy = CopyToShadow(entry.AssemblyPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                entry.Status = "could not be copied: " + ex.Message;
                _log.Error($"Could not copy plugin {entry.AssemblyPath} to run it.", ex);
                return false;
            }

            IReadOnlyList<LoadedPlugin> loaded;
            AssemblyLoadContext context;
            try
            {
                loaded = PluginLoader.LoadAssembly(copy, _log, collectible: true, out context);
            }
            catch (Exception ex)
            {
                entry.Status = "failed to load: " + ex.GetType().Name;
                _log.Error($"Could not load plugin assembly {entry.AssemblyPath}", ex);
                TryDelete(Path.GetDirectoryName(copy));
                return false;
            }

            if (loaded.Count == 0)
            {
                // Either not a plugin at all (a dependency), or refused as incompatible, which
                // the loader has already said.
                entry.Status = "no plugin in it";
                Unload(context);
                TryDelete(Path.GetDirectoryName(copy));
                _entries.Remove(entry);
                return false;
            }

            if (loaded.Count > 1)
                _log.Warn($"{entry.AssemblyPath} holds {loaded.Count} plugins; only {loaded[0].Plugin.Name} is managed from Decal's window.");

            IPlugin plugin = loaded[0].Plugin;
            entry.Name = plugin.Name;
            entry.Version = plugin.GetType().Assembly.GetName().Version?.ToString() ?? string.Empty;
            entry.Context = context;
            entry.ShadowDirectory = Path.GetDirectoryName(copy);

            if (disabled != null && (disabled.Contains(entry.Name) || disabled.Contains(Path.GetFileNameWithoutExtension(entry.AssemblyPath))))
            {
                entry.Enabled = false;
                entry.Status = "off";
                Unload(context);
                TryDelete(entry.ShadowDirectory);
                entry.Context = null;
                entry.ShadowDirectory = null;
                return false;
            }

            if (_host.Plugins.Any(p => p != plugin && string.Equals(p.Name, plugin.Name, StringComparison.OrdinalIgnoreCase)))
            {
                entry.Status = "a plugin of this name is already running";
                _log.Error($"Plugin {plugin.Name} from {entry.AssemblyPath} was not started: one of that name is already running.");
                Unload(context);
                TryDelete(entry.ShadowDirectory);
                entry.Context = null;
                entry.ShadowDirectory = null;
                return false;
            }

            if (!_host.Attach(plugin))
            {
                entry.Status = "failed to start";
                entry.Plugin = null;
                Unload(context);
                entry.Context = null;
                return false;
            }

            entry.Plugin = plugin;
            entry.Status = "running";
            return true;
        }

        private void Stop(PluginEntry entry, string status)
        {
            IPlugin plugin = entry.Plugin;
            entry.Plugin = null;
            entry.Status = status;

            if (plugin != null)
                _host.Detach(plugin);

            Unload(entry.Context);
            entry.Context = null;

            // The copy stays until the process ends if the unload has not let go of it yet;
            // the next start clears it.
            TryDelete(entry.ShadowDirectory);
            entry.ShadowDirectory = null;
        }

        private static void Unload(AssemblyLoadContext context)
        {
            if (context == null || !context.IsCollectible)
                return;

            try
            {
                context.Unload();
            }
            catch (InvalidOperationException)
            {
                // Already unloading.
            }
        }

        /// <summary>
        /// Copies a plugin - its whole folder when it has one of its own, since that holds its
        /// dependencies - to a private directory, and returns where the assembly now is.
        /// </summary>
        private string CopyToShadow(string assemblyPath)
        {
            string source = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
            bool ownFolder = !SamePath(source, Path.GetFullPath(_directory));
            // Named uniquely, not by a count: a copy an earlier load could not delete may still be
            // held open, by this process or one before it with the same id.
            string target = Path.Combine(_shadowRoot, Path.GetFileNameWithoutExtension(assemblyPath) + "-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            System.IO.Directory.CreateDirectory(target);

            if (ownFolder)
            {
                foreach (string file in System.IO.Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(source, file);
                    string destination = Path.Combine(target, relative);
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.Copy(file, destination, overwrite: true);
                }
            }
            else
            {
                File.Copy(assemblyPath, Path.Combine(target, Path.GetFileName(assemblyPath)), overwrite: true);
                foreach (string extra in new[] { ".pdb", ".deps.json" })
                {
                    string beside = Path.ChangeExtension(assemblyPath, null) + extra;
                    if (File.Exists(beside))
                        File.Copy(beside, Path.Combine(target, Path.GetFileName(beside)), overwrite: true);
                }
            }

            return Path.Combine(target, Path.GetFileName(assemblyPath));
        }

        private IEnumerable<string> Candidates()
        {
            if (string.IsNullOrEmpty(_directory) || !System.IO.Directory.Exists(_directory))
                return Array.Empty<string>();

            // Only assemblies that declare a plugin contract are worth a load context: the rest
            // of a plugin's folder is its dependencies. Reading the attribute from metadata costs
            // nothing and loads nothing.
            return PluginLoader.EnumerateCandidates(_directory).Where(DeclaresPluginApi).ToList();
        }

        private static bool DeclaresPluginApi(string path)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                using PEReader pe = new PEReader(stream);
                if (!pe.HasMetadata)
                    return false;

                MetadataReader reader = pe.GetMetadataReader();
                foreach (System.Reflection.Metadata.CustomAttributeHandle handle in reader.GetAssemblyDefinition().GetCustomAttributes())
                {
                    System.Reflection.Metadata.CustomAttribute attribute = reader.GetCustomAttribute(handle);
                    if (attribute.Constructor.Kind != System.Reflection.Metadata.HandleKind.MemberReference)
                        continue;

                    System.Reflection.Metadata.MemberReference ctor = reader.GetMemberReference((System.Reflection.Metadata.MemberReferenceHandle)attribute.Constructor);
                    if (ctor.Parent.Kind != System.Reflection.Metadata.HandleKind.TypeReference)
                        continue;

                    System.Reflection.Metadata.TypeReference type = reader.GetTypeReference((System.Reflection.Metadata.TypeReferenceHandle)ctor.Parent);
                    if (reader.GetString(type.Name) == nameof(PluginApiAttribute))
                        return true;
                }

                return false;
            }
            catch (Exception ex) when (ex is IOException || ex is BadImageFormatException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------- remembering

        private sealed class SavedState
        {
            public List<string> Disabled { get; set; } = new List<string>();
        }

        private HashSet<string> ReadDisabled()
        {
            try
            {
                if (File.Exists(_stateFile))
                {
                    SavedState state = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(_stateFile));
                    if (state?.Disabled != null)
                        return new HashSet<string>(state.Disabled, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                _log.Warn($"Could not read which plugins are switched off from {_stateFile}: {ex.Message}. Starting them all.");
            }

            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private void SaveDisabled()
        {
            try
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(_stateFile));
                SavedState state = new SavedState { Disabled = _entries.Where(e => !e.Enabled).Select(e => e.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() };
                File.WriteAllText(_stateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _log.Warn($"Could not remember which plugins are switched off: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------- housekeeping

        /// <summary>Removes the copies left by hosts that are no longer running.</summary>
        private static void ClearOldCopies(string runningRoot)
        {
            if (!System.IO.Directory.Exists(runningRoot))
                return;

            foreach (string directory in System.IO.Directory.GetDirectories(runningRoot))
            {
                if (int.TryParse(Path.GetFileName(directory), out int pid) && pid != Environment.ProcessId && !IsRunning(pid))
                    TryDelete(directory);
            }
        }

        private static bool IsRunning(int pid)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static void TryDelete(string directory)
        {
            if (string.IsNullOrEmpty(directory))
                return;

            try
            {
                if (System.IO.Directory.Exists(directory))
                    System.IO.Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Still held by a context that has not finished unloading; the next start clears it.
            }
        }

        private static bool SamePath(string a, string b)
            => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

        private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
