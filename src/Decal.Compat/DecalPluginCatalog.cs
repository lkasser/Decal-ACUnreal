using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using AC.Dat;

namespace Decal.Compat
{
    /// <summary>A Decal plugin found somewhere, before it is loaded.</summary>
    internal sealed class DecalPluginCandidate
    {
        /// <summary>One found in the compatibility plugin's own folder.</summary>
        internal DecalPluginCandidate(string assemblyPath)
        {
            AssemblyPath = Path.GetFullPath(assemblyPath);
            Source = DecalPluginCatalog.FolderSource;
        }

        /// <summary>One Decal's registry lists; <paramref name="assemblyPath"/> is null when it names no file.</summary>
        internal DecalPluginCandidate(DecalRegistryEntry registered, string assemblyPath)
        {
            Registered = registered ?? throw new ArgumentNullException(nameof(registered));
            AssemblyPath = assemblyPath;
            Source = DecalPluginCatalog.RegistrySource;
        }

        /// <summary>The plugin's assembly; null for a registered plugin that names none.</summary>
        public string AssemblyPath { get; }

        /// <summary>Where it was found: "folder" or "registry".</summary>
        public string Source { get; }

        /// <summary>Its entry in Decal's registry, for one found there.</summary>
        public DecalRegistryEntry Registered { get; }

        /// <summary>What Decal lists it as, or its file's name for one found in the folder.</summary>
        public string Name => !string.IsNullOrWhiteSpace(Registered?.Name) ? Registered.Name.Trim()
            : AssemblyPath != null ? Path.GetFileNameWithoutExtension(AssemblyPath)
            : Registered?.Clsid ?? string.Empty;

        /// <summary>Its version, read from the file without loading it; empty when there is no file.</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>Why the host does not load it because it does its job itself; null when it does not.</summary>
        public string ReplacedBy { get; set; }

        /// <summary>Why it cannot be tried at all, in a few words; null when it can.</summary>
        public string CannotRun { get; set; }

        /// <summary>The same, in full.</summary>
        public string CannotRunDetail { get; set; }

        /// <summary>What its metadata says, for explaining a failure to start; null when unread.</summary>
        public PluginFacts Facts { get; set; }
    }

    /// <summary>
    /// What a plugin assembly's metadata says about whether it can run here, read without
    /// loading it: whether it is a Decal plugin at all, what it imports from native DLLs, and
    /// whether it looks itself up in Decal's registry keys.
    /// </summary>
    internal sealed class PluginFacts
    {
        public string AssemblyName { get; private set; }

        public string Version { get; private set; } = string.Empty;

        /// <summary>It defines a type whose base is Decal.Adapter's PluginBase or FilterBase.</summary>
        public bool IsDecalExtension { get; private set; }

        /// <summary>It holds native code as well as IL - a mixed-mode assembly, 32-bit here.</summary>
        public bool HasNativeCode { get; private set; }

        /// <summary>The assemblies it references, by name.</summary>
        public IReadOnlyList<string> References { get; private set; } = Array.Empty<string>();

        /// <summary>The native DLLs it imports functions from, as it names them.</summary>
        public IReadOnlyList<string> NativeImports { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// A key under HKLM\SOFTWARE\Decal it reads through <c>Registry.LocalMachine</c> - the
        /// 64-bit view, in this process, where Decal's keys are not; null when it reads none.
        /// </summary>
        public string DecalKeyRead { get; private set; }

        /// <summary>The facts, or null when the file is not a .NET assembly or cannot be read.</summary>
        public static PluginFacts Read(string path)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                using PEReader pe = new PEReader(stream);
                if (!pe.HasMetadata)
                    return null;

                MetadataReader reader = pe.GetMetadataReader();
                PluginFacts facts = new PluginFacts
                {
                    HasNativeCode = pe.PEHeaders.CorHeader != null && (pe.PEHeaders.CorHeader.Flags & CorFlags.ILOnly) == 0,
                    IsDecalExtension = DerivesFromDecal(reader),
                    References = reader.AssemblyReferences.Select(h => reader.GetString(reader.GetAssemblyReference(h).Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    NativeImports = Enumerable.Range(1, reader.GetTableRowCount(TableIndex.ModuleRef))
                        .Select(i => reader.GetString(reader.GetModuleReference(MetadataTokens.ModuleReferenceHandle(i)).Name))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                };

                if (reader.IsAssembly)
                {
                    AssemblyDefinition assembly = reader.GetAssemblyDefinition();
                    facts.AssemblyName = reader.GetString(assembly.Name);
                    facts.Version = assembly.Version.ToString();
                }

                if (ReadsLocalMachine(reader))
                    facts.DecalKeyRead = DecalKeyIn(reader);

                return facts;
            }
            catch (Exception ex) when (ex is IOException || ex is BadImageFormatException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// Whether the assembly defines a type whose base is Decal.Adapter's PluginBase or
        /// FilterBase - up through its own classes, for a plugin with a base of its own.
        /// </summary>
        private static bool DerivesFromDecal(MetadataReader reader)
        {
            foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
            {
                // A type with no base - <Module>, an interface - has a nil one, which reads as a
                // type definition numbered zero, so nil is tested for first.
                EntityHandle baseType = reader.GetTypeDefinition(handle).BaseType;
                for (int depth = 0; !baseType.IsNil && baseType.Kind == HandleKind.TypeDefinition && depth < 16; depth++)
                    baseType = reader.GetTypeDefinition((TypeDefinitionHandle)baseType).BaseType;

                if (baseType.IsNil || baseType.Kind != HandleKind.TypeReference)
                    continue;

                TypeReference reference = reader.GetTypeReference((TypeReferenceHandle)baseType);
                if (reader.GetString(reference.Namespace) != "Decal.Adapter")
                    continue;

                string name = reader.GetString(reference.Name);
                if (name == "PluginBase" || name == "FilterBase")
                    return true;
            }

            return false;
        }

        /// <summary>Whether it touches <c>Microsoft.Win32.Registry.LocalMachine</c>, the default - here 64-bit - view.</summary>
        private static bool ReadsLocalMachine(MetadataReader reader)
        {
            foreach (MemberReferenceHandle handle in reader.MemberReferences)
            {
                MemberReference member = reader.GetMemberReference(handle);
                if (member.Parent.Kind != HandleKind.TypeReference)
                    continue;

                TypeReference type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
                string name = reader.GetString(member.Name);
                if (reader.GetString(type.Namespace) == "Microsoft.Win32" && reader.GetString(type.Name) == "Registry"
                    && (name == "LocalMachine" || name == "get_LocalMachine"))
                    return true;
            }

            return false;
        }

        /// <summary>The first of Decal's keys named in its string literals, if any.</summary>
        private static string DecalKeyIn(MetadataReader reader)
        {
            string found = null;
            UserStringHandle handle = MetadataTokens.UserStringHandle(1);
            int limit = reader.GetHeapSize(HeapIndex.UserString);
            while (!handle.IsNil && MetadataTokens.GetHeapOffset(handle) < limit)
            {
                string text = reader.GetUserString(handle);
                if (text.StartsWith(@"SOFTWARE\Decal", StringComparison.OrdinalIgnoreCase))
                {
                    // Its own Plugins key is the telling one: that is where it finds its folder.
                    if (text.IndexOf(@"\Plugins\", StringComparison.OrdinalIgnoreCase) >= 0)
                        return text;
                    found ??= text;
                }

                handle = reader.GetNextHandle(handle);
            }

            return found;
        }
    }

    /// <summary>
    /// Finds Decal plugins: in the compatibility plugin's own folder, laid out as the host lays
    /// out its plugins, and where Decal's registry says they are installed, as Decal did.
    /// </summary>
    /// <remarks>
    /// A folder of Decal plugins is mostly their dependencies, so each assembly is read as
    /// metadata first, loading nothing, and only one that derives from Decal's PluginBase or
    /// FilterBase counts. That is the whole test for the folder: Decal needed a registry entry
    /// per plugin, and there being in the folder is enough.
    ///
    /// <para>
    /// Every entry in Decal's registry is listed, whether or not it can run, so the list reads as
    /// DenAgent's did; each one that cannot is given its reason here, before anything is loaded:
    /// the host does its job itself, it is native, its file is gone, or it is the same assembly as
    /// one in the folder, which runs instead so that nothing runs twice.
    /// </para>
    /// </remarks>
    internal static class DecalPluginCatalog
    {
        public const string FolderSource = "folder";
        public const string RegistrySource = "registry";

        /// <summary>The Decal plugins in <paramref name="folder"/>, in the order found.</summary>
        public static IReadOnlyList<DecalPluginCandidate> FindInFolder(string folder)
        {
            List<DecalPluginCandidate> found = new List<DecalPluginCandidate>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return found;

            foreach (string path in EnumerateAssemblies(folder))
            {
                PluginFacts facts = PluginFacts.Read(path);
                if (facts == null || !facts.IsDecalExtension)
                    continue;

                found.Add(new DecalPluginCandidate(path) { Facts = facts, Version = facts.Version });
            }

            return found;
        }

        /// <summary>
        /// Every plugin Decal's registry lists, in its order, each with its reason if it cannot be
        /// tried.
        /// </summary>
        /// <param name="skip">Names or file names the setting DecalCompat:Skip says to leave alone.</param>
        /// <param name="folder">What the folder holds, which runs in place of a registered copy of the same assembly.</param>
        public static IReadOnlyList<DecalPluginCandidate> ReadRegistered(IDecalRegistry registry, ICollection<string> skip, IReadOnlyList<DecalPluginCandidate> folder)
        {
            List<DecalPluginCandidate> found = new List<DecalPluginCandidate>();
            if (registry == null)
                return found;

            foreach (DecalRegistryEntry entry in registry.ReadPlugins() ?? Array.Empty<DecalRegistryEntry>())
            {
                if (entry == null)
                    continue;

                string path = FullPath(entry);
                DecalPluginCandidate candidate = new DecalPluginCandidate(entry, path);
                found.Add(candidate);

                if (path != null && File.Exists(path))
                {
                    candidate.Facts = PluginFacts.Read(path);
                    candidate.Version = candidate.Facts?.Version ?? FileVersion(path);
                }
                else if (path == null)
                {
                    candidate.Version = FileVersion(registry.ReadComServer(entry.Clsid));
                }

                Classify(candidate, registry, skip, folder, found);
            }

            return found;
        }

        private static void Classify(DecalPluginCandidate candidate, IDecalRegistry registry, ICollection<string> skip, IReadOnlyList<DecalPluginCandidate> folder, List<DecalPluginCandidate> earlier)
        {
            DecalRegistryEntry entry = candidate.Registered;
            string path = candidate.AssemblyPath;

            candidate.ReplacedBy = Replacements.ReasonFor(entry);
            if (candidate.ReplacedBy != null)
                return;

            if (skip != null && (skip.Contains(candidate.Name) || (path != null && skip.Contains(Path.GetFileNameWithoutExtension(path)))))
            {
                CannotRun(candidate, "skipped", "Named in the setting DecalCompat:Skip, so it is not loaded.");
                return;
            }

            if (path == null)
            {
                string server = registry.ReadComServer(entry.Clsid);
                CannotRun(candidate, "cannot run: native",
                    "A native (COM) plugin" + (string.IsNullOrEmpty(server) ? string.Empty : ", " + server)
                    + ". This host runs Decal's .NET plugins only; a native one needs the real Decal injected into the retail client.");
                return;
            }

            if (!File.Exists(path))
            {
                CannotRun(candidate, "cannot run: file missing", $"Decal's registry says it is {path}, and there is no such file.");
                return;
            }

            if (candidate.Facts == null)
            {
                CannotRun(candidate, "cannot run: not .NET", $"{path} is not a .NET assembly, so it is not a Decal .NET plugin this host can load.");
                return;
            }

            if (candidate.Facts.HasNativeCode)
            {
                CannotRun(candidate, "cannot run: 32-bit code", $"{path} holds native 32-bit code as well as .NET, which a 64-bit host cannot load.");
                return;
            }

            if (!candidate.Facts.IsDecalExtension)
            {
                CannotRun(candidate, "cannot run: no plugin", $"{path} holds no class derived from Decal's PluginBase or FilterBase.");
                return;
            }

            DecalPluginCandidate own = folder?.FirstOrDefault(c => SameAssembly(c, candidate));
            if (own != null)
            {
                CannotRun(candidate, "runs from the folder", $"The copy in DecalCompat's own folder, {own.AssemblyPath}, runs instead, so the two are not loaded together.");
                return;
            }

            DecalPluginCandidate twin = earlier.FirstOrDefault(c => !ReferenceEquals(c, candidate) && c.AssemblyPath != null && string.Equals(c.AssemblyPath, path, StringComparison.OrdinalIgnoreCase));
            if (twin != null)
                CannotRun(candidate, "listed twice", $"Decal's registry lists the same file as {twin.Name}, which is the one loaded.");
        }

        private static void CannotRun(DecalPluginCandidate candidate, string status, string detail)
        {
            candidate.CannotRun = status;
            candidate.CannotRunDetail = detail;
        }

        /// <summary>The same assembly, by its name - wherever each copy is, and whatever each file is called.</summary>
        private static bool SameAssembly(DecalPluginCandidate a, DecalPluginCandidate b)
        {
            string nameA = a.Facts?.AssemblyName ?? (a.AssemblyPath != null ? Path.GetFileNameWithoutExtension(a.AssemblyPath) : null);
            string nameB = b.Facts?.AssemblyName ?? (b.AssemblyPath != null ? Path.GetFileNameWithoutExtension(b.AssemblyPath) : null);
            return nameA != null && string.Equals(nameA, nameB, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The plugin's assembly, from its Path and Assembly values; null when it names none.</summary>
        internal static string FullPath(DecalRegistryEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry?.Assembly))
                return null;

            try
            {
                string assembly = entry.Assembly.Trim();
                string path = string.IsNullOrWhiteSpace(entry.Path) ? assembly : Path.Combine(entry.Path.Trim(), assembly);
                return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
            }
            catch (ArgumentException)
            {
                // Something in the registry that is not a path at all.
                return null;
            }
        }

        /// <summary>A native file's version, as DenAgent showed it; empty when there is none.</summary>
        private static string FileVersion(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return string.Empty;

            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                return info.FileMajorPart + info.FileMinorPart + info.FileBuildPart + info.FilePrivatePart == 0
                    ? info.FileVersion ?? string.Empty
                    : $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// <c>&lt;folder&gt;/&lt;name&gt;/&lt;name&gt;.dll</c> - or every assembly in a subfolder not laid out so -
        /// and every assembly directly in the folder, as the host finds its own plugins.
        /// </summary>
        private static IEnumerable<string> EnumerateAssemblies(string folder)
        {
            foreach (string sub in Directory.GetDirectories(folder))
            {
                string named = Path.Combine(sub, Path.GetFileName(sub) + ".dll");
                if (File.Exists(named))
                {
                    yield return named;
                    continue;
                }

                foreach (string dll in Directory.GetFiles(sub, "*.dll"))
                    yield return dll;
            }

            foreach (string dll in Directory.GetFiles(folder, "*.dll"))
                yield return dll;
        }

        /// <summary>
        /// Whether the assembly defines a type whose base is Decal.Adapter's PluginBase or
        /// FilterBase - read from its metadata, without loading it.
        /// </summary>
        internal static bool IsDecalExtension(string path) => PluginFacts.Read(path)?.IsDecalExtension == true;
    }
}
