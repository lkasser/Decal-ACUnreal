using System;
using System.IO;

namespace Setup.Common
{
    /// <summary>Folder names compared the way Windows compares them, and the folders never to delete whole.</summary>
    public static class Paths
    {
        /// <summary>A full path without a trailing separator, so two spellings of one folder compare equal.</summary>
        public static string Normalize(string path)
            => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        /// <summary>Whether two paths name the same file or folder.</summary>
        public static bool Same(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return false;

            try
            {
                return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }
        }

        /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or somewhere inside it.</summary>
        public static bool IsWithin(string path, string folder)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder))
                return false;

            try
            {
                string full = Normalize(path);
                string root = Normalize(folder);
                return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }
        }

        /// <summary>
        /// Whether a folder may be deleted with everything in it: a full path, below a drive's
        /// root, and none of the folders Windows keeps a person's or the system's things in. The
        /// player's settings folder passes; %LOCALAPPDATA% itself, by a slip of a command line,
        /// does not.
        /// </summary>
        public static bool IsSafeToDeleteTree(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
                return false;

            string full;
            try
            {
                full = Normalize(folder);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }

            if (Path.GetPathRoot(full) is string root && Same(root, full))
                return false;

            Environment.SpecialFolder[] kept =
            {
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.Desktop,
                Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.Programs,
                Environment.SpecialFolder.StartMenu,
                Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.System,
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData,
            };

            foreach (Environment.SpecialFolder special in kept)
            {
                string path = Environment.GetFolderPath(special);
                if (path.Length > 0 && Same(path, full))
                    return false;
            }

            // %LOCALAPPDATA%\Programs, where per-user programs are installed side by side.
            string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            if (Same(programs, full) || Same(Path.GetTempPath(), full))
                return false;

            return true;
        }
    }
}
