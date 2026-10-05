using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Decal.Compat
{
    /// <summary>
    /// The folder a plugin from Decal's registry runs in: a copy of its install folder, kept in
    /// the compatibility plugin's own data folder, so the player's install is only ever read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Decal plugin finds its files beside itself - through its assembly's location, or the
    /// Path Decal gave it - and writes them back there: settings, rules, error logs. Run from its
    /// install, it would hold its DLLs open while it ran and change the player's files whenever it
    /// saved. So it runs from here instead, and is told this is its folder.
    /// </para>
    /// <para>
    /// Each time it is loaded its code - DLLs and programs - is brought up to date from the install,
    /// with the x86-only mark cleared as it is for every Decal plugin and its reads of
    /// HKEY_LOCAL_MACHINE pointed at the 32-bit registry (<see cref="RegistryRewrite"/>); its other
    /// files are copied only the first time, so what it has written here since is kept. Archives, and any file over
    /// <see cref="LargestFile"/>, are left behind: an install folder can be a downloads folder, and
    /// a plugin does not read its own installer.
    /// </para>
    /// </remarks>
    internal static class WorkingCopy
    {
        /// <summary>The largest file copied: well above any plugin's data, well below a client archive.</summary>
        public const long LargestFile = 32L * 1024 * 1024;

        private const int DeepestFolder = 4;

        /// <summary>
        /// Goes up when what is done to a DLL on its way here changes, so copies made the old way
        /// are made again.
        /// </summary>
        private const int Treatment = 2;

        /// <summary>Beside each DLL copied: what it was copied from, and how it was treated.</summary>
        internal const string StampSuffix = ".source";

        private static readonly HashSet<string> Code = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".dll", ".exe" };

        private static readonly HashSet<string> Archives = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z", ".cab", ".msi" };

        /// <summary>
        /// Where the working copy of <paramref name="installDirectory"/> goes under
        /// <paramref name="root"/>: named for the install folder, and told apart from another of
        /// the same name by its full path.
        /// </summary>
        public static string DirectoryFor(string root, string installDirectory)
        {
            string trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
            string name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name))
                name = "install";

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed.ToUpperInvariant()));
            return Path.Combine(root, name + "-" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant());
        }

        /// <summary>
        /// Brings the working copy up to date with the install, and says what it could not bring.
        /// Reads the install and nothing more.
        /// </summary>
        /// <returns>What was left out or could not be refreshed, one line each; empty when nothing was.</returns>
        public static IReadOnlyList<string> Refresh(string installDirectory, string workingDirectory)
        {
            List<string> problems = new List<string>();
            Directory.CreateDirectory(workingDirectory);
            Copy(new DirectoryInfo(installDirectory), workingDirectory, 0, problems);
            return problems;
        }

        private static void Copy(DirectoryInfo source, string target, int depth, List<string> problems)
        {
            FileInfo[] files;
            DirectoryInfo[] folders;
            try
            {
                files = source.GetFiles();
                folders = depth < DeepestFolder ? source.GetDirectories() : Array.Empty<DirectoryInfo>();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                problems.Add($"{source.FullName} could not be read: {ex.Message}");
                return;
            }

            Directory.CreateDirectory(target);

            foreach (FileInfo file in files)
            {
                if (Archives.Contains(file.Extension) || file.Length > LargestFile)
                {
                    problems.Add($"{file.Name} ({file.Length / 1024} KB) was left behind: an archive or too large to be a plugin's own file.");
                    continue;
                }

                string copy = Path.Combine(target, file.Name);
                try
                {
                    if (Code.Contains(file.Extension))
                        CopyCode(file, copy, problems);
                    else if (!File.Exists(copy))
                        file.CopyTo(copy);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Most likely the copy is still loaded from an earlier start, not yet unloaded:
                    // the one already here runs.
                    problems.Add($"{file.Name} could not be brought up to date: {ex.Message}");
                }
            }

            foreach (DirectoryInfo folder in folders.Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0))
            {
                // A plugin's own folders are small - its maps, its HUD images. One that is not is
                // someone else's: a plugin installed beside the client, say, has the client's.
                if (Fits(folder))
                    Copy(folder, Path.Combine(target, folder.Name), depth + 1, problems);
                else
                    problems.Add($"The folder {folder.Name} was left behind: too large to be the plugin's own.");
            }
        }

        /// <summary>Whether a folder, and everything in it, is small enough to be a plugin's own.</summary>
        private static bool Fits(DirectoryInfo folder)
        {
            const int MostFiles = 500;
            long bytes = 0;
            int count = 0;
            try
            {
                foreach (FileInfo file in folder.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    bytes += file.Length;
                    if (++count > MostFiles || bytes > LargestFile)
                        return false;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Copies a DLL or program when the install's differs - by size or time - from the one it
        /// was made from, clearing an IL-only assembly's x86-only mark and pointing its registry
        /// reads at the 32-bit registry on the way, and stamps the copy with what it was made from.
        /// </summary>
        private static void CopyCode(FileInfo file, string copy, List<string> problems)
        {
            string stamp = copy + StampSuffix;
            string source = string.Join(";", file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                        file.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                        Treatment.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (File.Exists(copy) && File.Exists(stamp) && File.ReadAllText(stamp) == source)
                return;

            byte[] image = File.ReadAllBytes(file.FullName);
            if (file.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                DecalPluginLoadContext.TryClearRequires32Bit(image);
                image = RegistryRewrite.Rewrite(image, out _, out string problem);
                if (problem != null)
                    problems.Add($"{file.Name}: {problem}.");
            }

            File.WriteAllBytes(copy, image);
            File.SetLastWriteTimeUtc(copy, file.LastWriteTimeUtc);
            File.WriteAllText(stamp, source);
        }
    }
}
