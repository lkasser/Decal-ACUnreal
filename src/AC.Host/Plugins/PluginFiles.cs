using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace AC.Host.Plugins
{
    /// <summary>
    /// The files that make up an installed plugin: what to copy into the plugin folder to install
    /// one, and what to delete to remove it.
    /// </summary>
    /// <remarks>
    /// A plugin is either a folder of its own under the plugin folder, holding its assembly and
    /// everything it needs, or an assembly lying in the plugin folder itself - the two shapes
    /// <see cref="PluginLoader"/> looks for. Installing always makes the first, because a loose
    /// assembly's dependencies would lie beside every other plugin's and be mistaken for them.
    /// </remarks>
    internal static class PluginFiles
    {
        /// <summary>Files that belong to an assembly and go wherever it goes.</summary>
        private static readonly string[] Companions = { ".pdb", ".deps.json", ".runtimeconfig.json", ".xml" };

        /// <summary>
        /// Copies a whole folder - a plugin with its dependencies and data files - into
        /// <paramref name="target"/>, over whatever is there.
        /// </summary>
        public static void CopyFolder(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(file, destination, overwrite: true);
            }
        }

        /// <summary>
        /// Copies one plugin assembly into <paramref name="target"/> with what it needs from beside
        /// it: its own companion files, and every assembly it refers to - however indirectly - that
        /// is in the same folder and is not one the host supplies itself.
        /// </summary>
        /// <returns>The files copied, as they are named in <paramref name="target"/>.</returns>
        public static IReadOnlyList<string> CopyAssembly(string assemblyPath, string target)
            => CopyAssembly(assemblyPath, target, IsHostAssembly);

        internal static IReadOnlyList<string> CopyAssembly(string assemblyPath, string target, Func<string, bool> isHostAssembly)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
            List<string> files = new List<string>();

            foreach (string assembly in Closure(assemblyPath, isHostAssembly))
            {
                files.Add(assembly);
                files.AddRange(CompanionsOf(assembly));
            }

            Directory.CreateDirectory(target);
            List<string> copied = new List<string>();
            foreach (string file in files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string destination = Path.Combine(target, Path.GetRelativePath(folder, file));
                File.Copy(file, destination, overwrite: true);
                copied.Add(destination);
            }

            return copied;
        }

        /// <summary>The assembly and its companions, for removing one loose assembly.</summary>
        public static IEnumerable<string> WithCompanions(string assemblyPath)
            => new[] { assemblyPath }.Concat(CompanionsOf(assemblyPath));

        private static IEnumerable<string> CompanionsOf(string assemblyPath)
        {
            string stem = Path.ChangeExtension(assemblyPath, null);
            foreach (string extension in Companions)
            {
                string companion = stem + extension;
                if (File.Exists(companion))
                    yield return companion;
            }
        }

        /// <summary>
        /// The assembly and every assembly beside it that it needs, read from metadata so nothing
        /// is loaded. Assemblies the host already has are left out: the host's copy is the one a
        /// plugin gets whatever it ships, and shipping the contract beside a plugin invites exactly
        /// the confusion the loader goes out of its way to prevent.
        /// </summary>
        internal static IReadOnlyList<string> Closure(string assemblyPath) => Closure(assemblyPath, IsHostAssembly);

        /// <param name="isHostAssembly">Whether the host supplies an assembly of this name itself.</param>
        internal static IReadOnlyList<string> Closure(string assemblyPath, Func<string, bool> isHostAssembly)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
            List<string> found = new List<string> { Path.GetFullPath(assemblyPath) };
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileNameWithoutExtension(assemblyPath) };
            Queue<string> pending = new Queue<string>(found);

            while (pending.Count > 0)
            {
                foreach (string reference in References(pending.Dequeue()))
                {
                    if (!seen.Add(reference) || isHostAssembly(reference))
                        continue;

                    string beside = Path.Combine(folder, reference + ".dll");
                    if (!File.Exists(beside))
                        continue;

                    found.Add(beside);
                    pending.Enqueue(beside);
                }
            }

            return found;
        }

        /// <summary>The names of the assemblies an assembly refers to, or none if it is not one.</summary>
        internal static IReadOnlyList<string> References(string assemblyPath)
        {
            try
            {
                using FileStream stream = File.OpenRead(assemblyPath);
                using PEReader pe = new PEReader(stream);
                if (!pe.HasMetadata)
                    return Array.Empty<string>();

                MetadataReader reader = pe.GetMetadataReader();
                return reader.AssemblyReferences
                    .Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is BadImageFormatException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                return Array.Empty<string>();
            }
        }

        private static bool IsHostAssembly(string name)
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, name + ".dll")))
                return true;

            return AssemblyLoadContext.Default.Assemblies.Any(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
        public static bool IsWithin(string path, string folder)
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
    }
}
