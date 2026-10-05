using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Setup.Common
{
    /// <summary>Deleting files one at a time, and the folders they leave empty - never a folder with anything still in it.</summary>
    internal static class FileRemoval
    {
        /// <summary>Deletes a file; true if it is gone, false if it is still there (in use, most likely).</summary>
        public static bool TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return true;

                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// The folders between these files and <paramref name="root"/>, deepest first: the ones
        /// to take out once the files are gone.
        /// </summary>
        public static List<string> FoldersOf(string root, IEnumerable<string> relativeFiles)
        {
            HashSet<string> folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in relativeFiles)
            {
                for (string folder = Path.GetDirectoryName(file); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
                    folders.Add(Path.Combine(root, folder));
            }

            return folders.OrderByDescending(folder => folder.Count(c => c == Path.DirectorySeparatorChar)).ThenBy(folder => folder, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Deletes the folders these files were in that are now empty, deepest first.</summary>
        public static void PruneFolders(string root, IEnumerable<string> relativeFiles)
        {
            foreach (string folder in FoldersOf(root, relativeFiles))
                TryDeleteEmptyFolder(folder);
        }

        /// <summary>Deletes a folder if there is nothing in it; true if it is gone.</summary>
        public static bool TryDeleteEmptyFolder(string folder)
        {
            try
            {
                if (!Directory.Exists(folder))
                    return true;

                if (Directory.EnumerateFileSystemEntries(folder).Any())
                    return false;

                Directory.Delete(folder, recursive: false);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
