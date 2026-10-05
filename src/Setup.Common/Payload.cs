using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace Setup.Common
{
    /// <summary>
    /// The files a setup carries: a zip, embedded in the setup program by
    /// tools\build-installers.ps1, of the folder as it is to be installed.
    /// </summary>
    /// <remarks>
    /// Every name in it is checked before anything is written: a name that is a full path, or
    /// climbs out with "..", or names a stream, would put a file outside the folder being
    /// installed into, and such a zip is refused whole.
    /// </remarks>
    public sealed class Payload : IDisposable
    {
        /// <summary>The name the build gives the embedded zip.</summary>
        public const string ResourceName = "Setup.Payload.zip";

        private readonly ZipArchive _zip;
        private readonly Dictionary<string, ZipArchiveEntry> _entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _files = new List<string>();

        /// <exception cref="InvalidDataException">It is not a zip, or a name in it would escape the folder.</exception>
        public Payload(Stream zip)
        {
            _zip = new ZipArchive(zip ?? throw new ArgumentNullException(nameof(zip)), ZipArchiveMode.Read, leaveOpen: false);

            foreach (ZipArchiveEntry entry in _zip.Entries)
            {
                // Folders are made as the files in them are written.
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                    continue;

                string name = Check(entry.FullName);
                if (_entries.ContainsKey(name))
                    throw new InvalidDataException($"The setup's files name {name} twice.");

                _entries[name] = entry;
                _files.Add(name);
                TotalLength += entry.Length;
            }
        }

        /// <summary>Each file, as a path relative to the folder it is installed into, with backslashes.</summary>
        public IReadOnlyList<string> Files => _files;

        /// <summary>What all of the files take up once written.</summary>
        public long TotalLength { get; }

        /// <summary>The payload built into this setup program, or null when it was built without one.</summary>
        public static Payload FromAssembly(Assembly assembly)
        {
            Stream stream = assembly?.GetManifestResourceStream(ResourceName);
            return stream == null ? null : new Payload(stream);
        }

        /// <summary>How large one file is once written.</summary>
        public long LengthOf(string file) => _entries[file].Length;

        /// <summary>What every name a file in use is moved to ends with: see <see cref="Extract"/>.</summary>
        public const string SetAsideSuffix = ".setup-old";

        /// <summary>The name a file in use is moved to, so that its new version can be written.</summary>
        public static string SetAsideName(string destination, int attempt)
            => destination + SetAsideSuffix + (attempt == 0 ? string.Empty : attempt.ToString(CultureInfo.InvariantCulture));

        /// <summary>
        /// Writes one file to <paramref name="destination"/>, replacing whatever is there; null,
        /// or the path the file in use there was moved to.
        /// </summary>
        /// <remarks>
        /// A file that already holds the same bytes is left alone: the overlay DLL is loaded in the
        /// running game, which locks it, and an upgrade that does not change it need not touch it.
        /// One that is locked and does change is moved aside first, as Windows lets a loaded DLL be
        /// renamed though not overwritten: the new one is in place for the game's next start, and
        /// the next install, or the uninstaller, deletes the old one once it is free.
        /// </remarks>
        /// <exception cref="IOException">The file there is in use, and may not even be moved.</exception>
        public string Extract(string file, string destination)
        {
            ZipArchiveEntry entry = _entries[file];
            Directory.CreateDirectory(Path.GetDirectoryName(destination));

            if (File.Exists(destination))
            {
                if (SameBytes(entry, destination))
                    return null;

                // A read-only file from an older install would refuse to be replaced.
                File.SetAttributes(destination, FileAttributes.Normal);
            }

            string setAside = null;
            FileStream target;
            try
            {
                target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(destination))
            {
                setAside = MoveAside(destination);
                if (setAside == null)
                    throw;

                target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            }

            using (Stream source = entry.Open())
            using (target)
                source.CopyTo(target);

            File.SetLastWriteTime(destination, entry.LastWriteTime.LocalDateTime);
            return setAside;
        }

        /// <summary>Whether the file there holds just what the entry does.</summary>
        private static bool SameBytes(ZipArchiveEntry entry, string path)
        {
            try
            {
                if (new FileInfo(path).Length != entry.Length)
                    return false;

                using Stream ours = entry.Open();
                using FileStream theirs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                byte[] a = new byte[81920];
                byte[] b = new byte[81920];
                while (true)
                {
                    int read = ours.ReadAtLeast(a, a.Length, throwOnEndOfStream: false);
                    if (read == 0)
                        return true;
                    if (theirs.ReadAtLeast(b, read, throwOnEndOfStream: false) != read || !a.AsSpan(0, read).SequenceEqual(b.AsSpan(0, read)))
                        return false;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Moves a file in use out of the way: the name it has now, or null when it may not be moved.</summary>
        private static string MoveAside(string path)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                // An older one still there may still be in use itself.
                string aside = SetAsideName(path, attempt);
                if (File.Exists(aside) && !FileRemoval.TryDelete(aside))
                    continue;

                try
                {
                    File.Move(path, aside);
                    return aside;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return null;
                }
            }

            return null;
        }

        /// <summary>A zip entry's name as a relative Windows path, or the reason it cannot be one.</summary>
        internal static string Check(string name)
        {
            string path = (name ?? string.Empty).Replace('/', '\\');
            if (path.Length == 0 || Path.IsPathRooted(path) || path.Contains(':'))
                throw new InvalidDataException($"The setup's files include '{name}', which is not a path inside the install folder.");

            foreach (string part in path.Split('\\'))
            {
                if (part.Length == 0 || part == "." || part == "..")
                    throw new InvalidDataException($"The setup's files include '{name}', which is not a path inside the install folder.");
            }

            return path;
        }

        public void Dispose() => _zip.Dispose();
    }
}
