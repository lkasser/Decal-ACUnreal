using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Setup.Common
{
    /// <summary>
    /// Deletes what the uninstaller could not while it was running - its own program, and the
    /// runtime it shares with Decal Agent - once it has exited, by a batch file in %TEMP% that
    /// waits for the files to come free, deletes them and the folders they leave empty, and
    /// then deletes itself.
    /// </summary>
    /// <remarks>
    /// The files are named one by one and the folders are taken out with a plain rd, which
    /// refuses a folder with anything left in it: nothing is deleted that the uninstaller did
    /// not list. Windows can delete a file at the next restart only for an administrator, and
    /// these setups never ask to be one.
    /// </remarks>
    public static class DeferredDelete
    {
        /// <summary>How many seconds the batch file waits for the files to come free before it gives up.</summary>
        public const int PatienceSeconds = 120;

        /// <summary>The batch file's text.</summary>
        public static string Script(IReadOnlyList<string> files, IReadOnlyList<string> folders)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("@echo off");

            // Read as UTF-8 from here on, so a folder named in any language is named rightly.
            text.AppendLine("chcp 65001 >nul");
            text.AppendLine("rem Written by Decal Agent's uninstaller: deletes what it could not delete while it ran.");
            text.AppendLine("set /a tries=0");
            text.AppendLine(":again");
            foreach (string file in files)
                text.AppendLine($"del /f /q \"{Escape(file)}\" >nul 2>&1");
            foreach (string file in files)
                text.AppendLine($"if exist \"{Escape(file)}\" goto wait");
            foreach (string folder in folders)
                text.AppendLine($"rd \"{Escape(folder)}\" >nul 2>&1");
            text.AppendLine("goto done");
            text.AppendLine(":wait");
            text.AppendLine("set /a tries+=1");
            text.AppendLine($"if %tries% geq {PatienceSeconds} goto done");
            text.AppendLine("ping -n 2 127.0.0.1 >nul");
            text.AppendLine("goto again");
            text.AppendLine(":done");

            // Deletes the batch file without cmd then complaining that it cannot read its next line.
            text.AppendLine("(goto) 2>nul & del /f /q \"%~f0\"");
            return text.ToString();
        }

        /// <summary>Writes the batch file to %TEMP%; its path.</summary>
        public static string Write(IReadOnlyList<string> files, IReadOnlyList<string> folders, string directory = null)
        {
            string path = Path.Combine(directory ?? Path.GetTempPath(), "DecalAgent-uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".cmd");
            File.WriteAllText(path, Script(files, folders), new UTF8Encoding(false));
            return path;
        }

        /// <summary>Starts a batch file written by <see cref="Write"/>, hidden, from %TEMP% so that it holds no folder it is to delete.</summary>
        public static Process Start(string script)
        {
            ProcessStartInfo start = new ProcessStartInfo("cmd.exe", "/d /s /c \"\"" + script + "\"\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(script),
            };

            return Process.Start(start);
        }

        /// <summary>A path inside a batch file's quotes, where only % means anything.</summary>
        private static string Escape(string path) => path.Replace("%", "%%");
    }
}
