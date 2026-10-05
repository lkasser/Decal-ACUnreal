using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace AC.Dat
{
    /// <summary>
    /// Reads the resources embedded in a .NET assembly file without loading the assembly.
    /// </summary>
    /// <remarks>
    /// The assemblies in question are Decal-era plugins built for .NET Framework 2.0 and 4.0.
    /// Loading one into this process would resolve its references against a runtime it was
    /// never built for, run its static constructors, and hold the file for the life of the
    /// process - all to get at bytes that are sitting in the file already. So the PE file is
    /// read as data: the metadata names each resource and gives its offset into the CLR
    /// resources directory, and at that offset is a four-byte length and then the resource.
    ///
    /// <para>
    /// Nothing is cached here; each call opens the file, reads what it needs and closes it
    /// again, so a caller that wants caching can have it at whatever level suits.
    /// </para>
    /// </remarks>
    public static class ManifestResources
    {
        /// <summary>
        /// The names of the resources embedded in the assembly, in metadata order, or none
        /// when the file cannot be read. <see cref="TryList"/> says why.
        /// </summary>
        /// <remarks>
        /// Only embedded resources are listed. A manifest can also name resources that live in
        /// other files or other assemblies, but those cannot be read from this file, and listing
        /// them would offer names <see cref="TryRead"/> must then refuse.
        /// </remarks>
        public static IReadOnlyList<string> List(string assemblyPath)
            => TryList(assemblyPath, out IReadOnlyList<string> names, out _) ? names : Array.Empty<string>();

        /// <summary>As <see cref="List"/>, but saying why when the file cannot be read.</summary>
        public static bool TryList(string assemblyPath, out IReadOnlyList<string> names, out string error)
        {
            List<string> found = new List<string>();

            bool ok = Inspect(assemblyPath, (pe, metadata) =>
            {
                foreach (ManifestResourceHandle handle in metadata.ManifestResources)
                {
                    ManifestResource resource = metadata.GetManifestResource(handle);
                    if (resource.Implementation.IsNil)
                        found.Add(metadata.GetString(resource.Name));
                }

                return null;
            }, out error);

            names = ok ? found : Array.Empty<string>();
            return ok;
        }

        /// <summary>
        /// Reads one embedded resource by its exact name, which is case-sensitive as it is to
        /// the runtime.
        /// </summary>
        public static bool TryRead(string assemblyPath, string resourceName, out byte[] data, out string error)
        {
            byte[] result = null;

            bool ok = Inspect(assemblyPath, (pe, metadata) =>
            {
                foreach (ManifestResourceHandle handle in metadata.ManifestResources)
                {
                    ManifestResource resource = metadata.GetManifestResource(handle);
                    if (!metadata.StringComparer.Equals(resource.Name, resourceName ?? string.Empty))
                        continue;

                    if (!resource.Implementation.IsNil)
                        return $"{resourceName} is not embedded in {Path.GetFileName(assemblyPath)}; it lives in another file";

                    return ReadEmbedded(pe, resource, out result);
                }

                return $"{Path.GetFileName(assemblyPath)} has no resource named {resourceName}";
            }, out error);

            data = ok ? result : null;
            return ok;
        }

        /// <summary>
        /// Opens the file as a PE image with metadata and runs <paramref name="body"/> over it.
        /// The body returns an error, or null for success.
        /// </summary>
        private static bool Inspect(string assemblyPath, Func<PEReader, MetadataReader, string> body, out string error)
        {
            if (string.IsNullOrEmpty(assemblyPath))
            {
                error = "no assembly path was given";
                return false;
            }

            if (!File.Exists(assemblyPath))
            {
                error = $"{assemblyPath} does not exist";
                return false;
            }

            try
            {
                // Shared as widely as possible: the game may well have this very file loaded.
                using FileStream stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using PEReader pe = new PEReader(stream);

                if (!pe.HasMetadata)
                {
                    error = $"{Path.GetFileName(assemblyPath)} is not a .NET assembly";
                    return false;
                }

                error = body(pe, pe.GetMetadataReader());
                return error == null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is BadImageFormatException || ex is InvalidOperationException)
            {
                // A PE file with bad headers is found out lazily, on first use of whatever is
                // bad, so this covers every read above and not only the open.
                error = $"{Path.GetFileName(assemblyPath)} could not be read: {ex.Message}";
                return false;
            }
        }

        private static string ReadEmbedded(PEReader pe, ManifestResource resource, out byte[] data)
        {
            data = null;

            DirectoryEntry directory = pe.PEHeaders.CorHeader.ResourcesDirectory;
            if (directory.RelativeVirtualAddress == 0 || directory.Size < 4)
                return "the assembly has no resources directory";

            // Offsets are relative to the directory, and each resource is its length followed
            // by its bytes. Both are checked against the directory's own size, so a bad offset
            // reads nothing rather than whatever follows the directory in its section.
            long offset = resource.Offset;
            if (offset < 0 || offset > directory.Size - 4)
                return "the resource's offset lies outside the resources directory";

            PEMemoryBlock block = pe.GetSectionData(directory.RelativeVirtualAddress + (int)offset);
            if (block.Length < 4)
                return "the resources directory is not inside any section of the file";

            BlobReader reader = block.GetReader();
            int length = reader.ReadInt32();

            if (length < 0 || offset + 4 + length > directory.Size || length > reader.RemainingBytes)
                return $"the resource claims {length} bytes, which run past the end of the resources directory";

            data = reader.ReadBytes(length);
            return null;
        }
    }
}
