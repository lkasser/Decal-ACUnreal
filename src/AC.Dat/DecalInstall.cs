using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AC.Dat
{
    /// <summary>One entry under Decal's Plugins or Services key, as Decal wrote it.</summary>
    public sealed class DecalRegistryEntry
    {
        public DecalRegistryEntry(string clsid, string name, string path = null, string assembly = null, string themeDirectory = null, bool enabled = true, string objectType = null)
        {
            Clsid = clsid;
            Name = name;
            Path = path;
            Assembly = assembly;
            ThemeDirectory = themeDirectory;
            Enabled = enabled;
            ObjectType = objectType;
        }

        /// <summary>The key's own name: the component's class id, braces and all.</summary>
        public string Clsid { get; }

        /// <summary>The key's default value, which Decal shows as the display name.</summary>
        public string Name { get; }

        /// <summary>The <c>Path</c> value: the directory a .NET plugin was installed to. Native COM plugins have none.</summary>
        public string Path { get; }

        /// <summary>The <c>Assembly</c> value: the plugin's file name within <see cref="Path"/>.</summary>
        public string Assembly { get; }

        /// <summary>
        /// The <c>XMLThemeDirectory</c> value, which only Virindi View Service's bootstrapper
        /// writes - and which is its own install directory.
        /// </summary>
        public string ThemeDirectory { get; }

        /// <summary>
        /// The <c>Enabled</c> value: whether the box beside it in Decal's own list is ticked.
        /// An entry without the value counts as ticked.
        /// </summary>
        public bool Enabled { get; }

        /// <summary>
        /// The <c>Object</c> value: the .NET type Decal's surrogate made of a .NET plugin, as
        /// "Namespace.Type". Native COM plugins have none.
        /// </summary>
        public string ObjectType { get; }
    }

    /// <summary>
    /// The parts of the registry Decal writes that say where things are installed. An
    /// interface so the search can be tested without a Decal install, or off Windows.
    /// </summary>
    public interface IDecalRegistry
    {
        /// <summary>
        /// Decal's Agent key: where Decal is, and where the client it runs against is. False
        /// when there is no such key; either value may still be null when there is.
        /// </summary>
        bool TryReadAgent(out string agentPath, out string portalPath);

        IReadOnlyList<DecalRegistryEntry> ReadPlugins();

        IReadOnlyList<DecalRegistryEntry> ReadServices();

        /// <summary>
        /// The file a COM class is served from - a native Decal plugin's DLL, found by the
        /// class id its Plugins entry is named for, as DenAgent found it to show its version.
        /// Null when the class is not registered.
        /// </summary>
        string ReadComServer(string clsid) => null;
    }

    /// <summary>
    /// Decal's keys in the real registry. Decal is a 32-bit program, so it wrote them to the
    /// 32-bit view - WOW6432Node, as a 64-bit process sees it. Off Windows there is no
    /// registry and nothing is found.
    /// </summary>
    public sealed class WindowsDecalRegistry : IDecalRegistry
    {
        private const string DecalKey = @"SOFTWARE\Decal";

        public bool TryReadAgent(out string agentPath, out string portalPath)
        {
            agentPath = null;
            portalPath = null;

            if (!OperatingSystem.IsWindows())
                return false;

            return ReadAgent(out agentPath, out portalPath);
        }

        public IReadOnlyList<DecalRegistryEntry> ReadPlugins()
            => OperatingSystem.IsWindows() ? ReadEntries(DecalKey + @"\Plugins") : Array.Empty<DecalRegistryEntry>();

        public IReadOnlyList<DecalRegistryEntry> ReadServices()
            => OperatingSystem.IsWindows() ? ReadEntries(DecalKey + @"\Services") : Array.Empty<DecalRegistryEntry>();

        public string ReadComServer(string clsid)
            => OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(clsid) ? ReadInprocServer(clsid.Trim()) : null;

        /// <summary>
        /// The class's InprocServer32, in the 32-bit view like everything else of Decal's: a
        /// 32-bit COM server is registered there.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static string ReadInprocServer(string clsid)
        {
            try
            {
                using RegistryKey classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry32);
                using RegistryKey server = classes.OpenSubKey(@"CLSID\" + clsid + @"\InprocServer32");
                return server?.GetValue(string.Empty) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException || ex is ArgumentException)
            {
                return null;
            }
        }

        [SupportedOSPlatform("windows")]
        private static bool ReadAgent(out string agentPath, out string portalPath)
        {
            agentPath = null;
            portalPath = null;

            try
            {
                using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                using RegistryKey agent = machine.OpenSubKey(DecalKey + @"\Agent");

                if (agent == null)
                    return false;

                agentPath = agent.GetValue("AgentPath") as string;
                portalPath = agent.GetValue("PortalPath") as string;
                return true;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
                // A key this account may not read is, for our purposes, a key that is not there.
                return false;
            }
        }

        [SupportedOSPlatform("windows")]
        private static IReadOnlyList<DecalRegistryEntry> ReadEntries(string keyPath)
        {
            List<DecalRegistryEntry> entries = new List<DecalRegistryEntry>();

            try
            {
                using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                using RegistryKey parent = machine.OpenSubKey(keyPath);

                if (parent == null)
                    return entries;

                foreach (string clsid in parent.GetSubKeyNames())
                {
                    using RegistryKey key = parent.OpenSubKey(clsid);
                    if (key == null)
                        continue;

                    // Enabled is a DWORD; anything else, or nothing, leaves the entry ticked.
                    entries.Add(new DecalRegistryEntry(
                        clsid,
                        key.GetValue(string.Empty) as string,
                        key.GetValue("Path") as string,
                        key.GetValue("Assembly") as string,
                        key.GetValue("XMLThemeDirectory") as string,
                        key.GetValue("Enabled") is not int enabled || enabled != 0,
                        key.GetValue("Object") as string));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
                // Whatever was read before the failure is still worth having.
            }

            return entries;
        }
    }

    /// <summary>A .NET plugin Decal has registered.</summary>
    public sealed class DecalPlugin
    {
        public DecalPlugin(string clsid, string name, string directory, string assemblyFileName)
        {
            Clsid = clsid;
            Name = name;
            Directory = directory;
            AssemblyFileName = assemblyFileName;
        }

        public string Clsid { get; }

        public string Name { get; }

        public string Directory { get; }

        public string AssemblyFileName { get; }

        /// <summary>The plugin's assembly, or null when Decal recorded a directory but no file name.</summary>
        public string FullPath => string.IsNullOrEmpty(AssemblyFileName) ? null : System.IO.Path.Combine(Directory, AssemblyFileName);

        public override string ToString() => $"{Name}: {FullPath ?? Directory}";
    }

    /// <summary>
    /// Where Decal, the client, the plugins and Virindi View Service are installed, found from
    /// the registry entries Decal itself writes.
    /// </summary>
    /// <remarks>
    /// The theme artwork the overlay draws with is spread across these: Decal's own bitmaps sit
    /// in its install directory, Virindi View Service carries its themes inside its assembly,
    /// and the client's interface art is in the portal archive. None of them is anywhere fixed,
    /// but Decal has to be told where each one is to load it, and it keeps what it was told.
    ///
    /// <para>
    /// A directory is only reported if it exists, so a caller can take a non-null path as
    /// usable. What was not found, and why, is in <see cref="Describe"/>, for the log.
    /// </para>
    /// </remarks>
    public sealed class DecalInstall
    {
        public const string VirindiViewServiceFolder = "VirindiViewService";
        public const string VirindiViewServiceFileName = "VirindiViewService.dll";

        private readonly IReadOnlyList<string> _description;

        private DecalInstall(string decalDirectory, string portalDirectory, string virindiViewServicePath, IReadOnlyList<DecalPlugin> plugins, List<string> description)
        {
            DecalDirectory = decalDirectory;
            PortalDirectory = portalDirectory;
            VirindiViewServicePath = virindiViewServicePath;
            Plugins = plugins;
            _description = description.AsReadOnly();
        }

        /// <summary>Decal's install directory, where its own theme bitmaps are; null when not found.</summary>
        public string DecalDirectory { get; }

        /// <summary>The client's directory, where client_portal.dat is; null when not found.</summary>
        public string PortalDirectory { get; }

        /// <summary>The full path of VirindiViewService.dll; null when not found.</summary>
        public string VirindiViewServicePath { get; }

        /// <summary>The registered plugins that have an install directory; native COM plugins have none and are left out.</summary>
        public IReadOnlyList<DecalPlugin> Plugins { get; }

        /// <summary>
        /// Looks the install up. Either location may be given instead, for an install the
        /// registry does not describe; one given that does not exist is reported and the
        /// registry is used after all.
        /// </summary>
        /// <param name="registry">Where to read Decal's keys; the real registry when null.</param>
        /// <param name="decalDirectory">Decal's directory, overriding the registry's.</param>
        /// <param name="virindiViewServicePath">The full path of VirindiViewService.dll, overriding the search.</param>
        public static DecalInstall Detect(IDecalRegistry registry = null, string decalDirectory = null, string virindiViewServicePath = null)
        {
            registry ??= new WindowsDecalRegistry();
            List<string> lines = new List<string>();

            bool haveAgent = registry.TryReadAgent(out string agentPath, out string portalPath);

            string decal = null;
            if (!string.IsNullOrWhiteSpace(decalDirectory))
            {
                if (Directory.Exists(decalDirectory))
                {
                    decal = decalDirectory;
                    lines.Add($"Decal: {decal} (given)");
                }
                else
                {
                    lines.Add($"Decal: given as {decalDirectory}, which does not exist; trying the registry");
                }
            }

            if (decal == null)
                decal = FromAgent("Decal", "AgentPath", haveAgent, agentPath, lines);

            string portal = FromAgent("Client", "PortalPath", haveAgent, portalPath, lines);

            List<DecalPlugin> plugins = new List<DecalPlugin>();
            int native = 0;

            foreach (DecalRegistryEntry entry in registry.ReadPlugins() ?? Array.Empty<DecalRegistryEntry>())
            {
                if (string.IsNullOrWhiteSpace(entry.Path))
                {
                    native++;
                    continue;
                }

                plugins.Add(new DecalPlugin(entry.Clsid, entry.Name ?? entry.Clsid, entry.Path.Trim(), entry.Assembly?.Trim()));
            }

            lines.Add($"Plugins: {plugins.Count} with an install directory"
                + (native > 0 ? $", {native} native (no directory, not listed)" : string.Empty));

            foreach (DecalPlugin plugin in plugins)
                lines.Add("  " + plugin);

            string vvs = FindVirindiViewService(virindiViewServicePath, registry, plugins, lines);

            return new DecalInstall(decal, portal, vvs, plugins, lines);
        }

        /// <summary>A registered plugin by its display name, ignoring case; null when there is none.</summary>
        public DecalPlugin FindPlugin(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            foreach (DecalPlugin plugin in Plugins)
            {
                if (string.Equals(plugin.Name, name, StringComparison.OrdinalIgnoreCase))
                    return plugin;
            }

            return null;
        }

        /// <summary>What was found and where, and what was not and why - one line each, for a log.</summary>
        public IReadOnlyList<string> Describe() => _description;

        private static string FromAgent(string label, string valueName, bool haveAgent, string value, List<string> lines)
        {
            if (!haveAgent)
            {
                lines.Add($"{label}: not found - the registry has no Decal Agent key, so Decal does not look installed");
                return null;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                lines.Add($"{label}: not found - Decal's Agent key has no {valueName}");
                return null;
            }

            string directory = value.Trim();
            if (!Directory.Exists(directory))
            {
                lines.Add($"{label}: the registry says {directory}, which does not exist");
                return null;
            }

            lines.Add($"{label}: {directory}");
            return directory;
        }

        /// <summary>
        /// Virindi View Service is loaded by a bootstrapper service and is not itself a
        /// registered plugin, so it has no Path of its own. It is found, in order: where the
        /// caller says; where the bootstrapper keeps its themes, which is its install
        /// directory; or in a VirindiViewService folder beside any plugin's folder, since the
        /// Virindi installers put all their plugins side by side.
        /// </summary>
        private static string FindVirindiViewService(string given, IDecalRegistry registry, IReadOnlyList<DecalPlugin> plugins, List<string> lines)
        {
            const string Label = "Virindi View Service";

            if (!string.IsNullOrWhiteSpace(given))
            {
                if (File.Exists(given))
                {
                    lines.Add($"{Label}: {given} (given)");
                    return given;
                }

                lines.Add($"{Label}: given as {given}, which does not exist; searching instead");
            }

            foreach (DecalRegistryEntry service in registry.ReadServices() ?? Array.Empty<DecalRegistryEntry>())
            {
                if (string.IsNullOrWhiteSpace(service.ThemeDirectory))
                    continue;

                string candidate = TryCombine(service.ThemeDirectory.Trim(), VirindiViewServiceFileName);
                if (candidate != null && File.Exists(candidate))
                {
                    lines.Add($"{Label}: {candidate} (the theme directory of the {service.Name ?? service.Clsid} service)");
                    return candidate;
                }
            }

            HashSet<string> tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (DecalPlugin plugin in plugins)
            {
                string parent = ParentOf(plugin.Directory);
                if (parent == null || !tried.Add(parent))
                    continue;

                string candidate = TryCombine(parent, System.IO.Path.Combine(VirindiViewServiceFolder, VirindiViewServiceFileName));
                if (candidate != null && File.Exists(candidate))
                {
                    lines.Add($"{Label}: {candidate} (beside the {plugin.Name} plugin)");
                    return candidate;
                }
            }

            lines.Add($"{Label}: not found - no service names its directory, and no plugin has a {VirindiViewServiceFolder} folder beside it");
            return null;
        }

        /// <summary>The directory containing <paramref name="directory"/>, whether or not it was written with a trailing separator.</summary>
        private static string ParentOf(string directory)
        {
            try
            {
                return System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(directory));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>Combines, or null when the registry held something that is not a path at all.</summary>
        private static string TryCombine(string directory, string relative)
        {
            try
            {
                return System.IO.Path.Combine(directory, relative);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
