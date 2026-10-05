using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace AC.Dat
{
    /// <summary>What Virindi View Service remembered about one window, by VVS's own rules.</summary>
    public sealed class VirindiStoredView
    {
        /// <summary>VVS's key for the window: the plugin's name, a colon, the window's title.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// The theme the player picked for this window, or null when they left it on the
        /// default - VVS stored a name either way, but used it only when the player had chosen.
        /// </summary>
        public string Theme { get; set; }

        /// <summary>Hudified: only its body shows until left Ctrl is held.</summary>
        public bool Ghosted { get; set; }

        public bool ClickThrough { get; set; }

        /// <summary>
        /// Where the window was left, on screen - which VVS put a window back at only when it
        /// was hudified; otherwise the plugin placed it afresh each session.
        /// </summary>
        public int X { get; set; }

        public int Y { get; set; }

        /// <summary>The size the player gave a window they could resize.</summary>
        public int Width { get; set; }

        public int Height { get; set; }
    }

    /// <summary>
    /// Reads Virindi View Service's remembered windows - vvs.s3db's StoredViewInfo table - and
    /// its default theme, so windows open the way the player last had them in the standard
    /// client. Only read: the file and the registry are VVS's.
    /// </summary>
    public static class VirindiViewStore
    {
        /// <summary>VVS's service key under Decal's, in the 32-bit registry where Decal lives.</summary>
        public const string ServiceKey = @"SOFTWARE\Decal\Services\{DBAC9286-B38D-4570-961F-D4D9349AE3D4}";

        public const string StoreFileName = "vvs.s3db";

        /// <summary>
        /// VVS's built-in themes in the order it registered them, which is what the registry's
        /// Theme number and a stored ThemeID count through.
        /// </summary>
        public static IReadOnlyList<string> BuiltInThemes { get; } = new[]
        {
            "Minimalist", "Float", "Minimalist Transparent", "Decal", "Minimalist Black", "Minimalist Green",
        };

        /// <summary>
        /// The theme VVS draws a window in when the player picked none for it: Theme2 by name
        /// when it names one, else the Theme number into <see cref="BuiltInThemes"/>, else -
        /// with neither set, as on most machines - Float, the second.
        /// </summary>
        public static string DefaultTheme(string theme2, int? themeIndex)
        {
            if (!string.IsNullOrWhiteSpace(theme2))
            {
                foreach (string name in BuiltInThemes)
                {
                    if (string.Equals(name, theme2.Trim(), StringComparison.OrdinalIgnoreCase))
                        return name;
                }
            }

            int index = themeIndex ?? 1;
            if (index < 0 || index >= BuiltInThemes.Count)
                index = 1;
            return BuiltInThemes[index];
        }

        /// <summary>
        /// VVS's settings from the registry: where its store file is and its default theme.
        /// Nulls for anything not there, and everything null off Windows.
        /// </summary>
        public static void ReadRegistry(out string storeFile, out string theme2, out int? themeIndex)
        {
            storeFile = null;
            theme2 = null;
            themeIndex = null;
            if (!OperatingSystem.IsWindows())
                return;

            try
            {
                using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                using RegistryKey service = machine.OpenSubKey(ServiceKey);
                if (service == null)
                    return;
                storeFile = service.GetValue("StoredInfoFile") as string;
                theme2 = service.GetValue("Theme2") as string;
                if (service.GetValue("Theme") is int theme)
                    themeIndex = theme;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
            }
        }

        /// <summary>
        /// The store file: where the registry says, else beside VirindiViewService.dll. Null
        /// when there is none.
        /// </summary>
        public static string FindStoreFile(string registryPath, string virindiViewServicePath)
        {
            if (!string.IsNullOrWhiteSpace(registryPath) && File.Exists(registryPath))
                return Path.GetFullPath(registryPath);

            string folder = string.IsNullOrEmpty(virindiViewServicePath) ? null : Path.GetDirectoryName(virindiViewServicePath);
            string beside = folder == null ? null : Path.Combine(folder, StoreFileName);
            return beside != null && File.Exists(beside) ? beside : null;
        }

        /// <summary>
        /// Every window the store remembers, by <see cref="VirindiStoredView.Key"/>. False, with
        /// the reason, when the file cannot be read.
        /// </summary>
        public static bool TryRead(string path, out IReadOnlyDictionary<string, VirindiStoredView> views, out string error)
        {
            Dictionary<string, VirindiStoredView> read = new Dictionary<string, VirindiStoredView>(StringComparer.Ordinal);
            views = read;
            try
            {
                SqliteFile file = SqliteFile.Open(path);
                foreach (IReadOnlyDictionary<string, object> row in file.ReadTable("StoredViewInfo"))
                {
                    string key = Text(row, "ViewKey");
                    if (string.IsNullOrEmpty(key))
                        continue;

                    // As VVS's own loader: the theme counts only when the player chose it, by
                    // name when the name is a theme it knows, else by number.
                    string theme = null;
                    if (Number(row, "IsCustomTheme") != 0)
                    {
                        string named = Text(row, "ThemeID2");
                        int index = (int)Number(row, "ThemeID");
                        theme = !string.IsNullOrWhiteSpace(named) ? named.Trim()
                            : index >= 0 && index < BuiltInThemes.Count ? BuiltInThemes[index] : null;
                    }

                    read[key] = new VirindiStoredView
                    {
                        Key = key,
                        Theme = theme,
                        Ghosted = Number(row, "Ghost") != 0,
                        ClickThrough = Number(row, "ClickThrough") != 0,
                        X = (int)Number(row, "LocX"),
                        Y = (int)Number(row, "LocY"),
                        Width = (int)Number(row, "UserW"),
                        Height = (int)Number(row, "UserH"),
                    };
                }

                error = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is KeyNotFoundException || ex is UnauthorizedAccessException)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string Text(IReadOnlyDictionary<string, object> row, string column)
            => row.TryGetValue(column, out object value) ? value as string : null;

        private static long Number(IReadOnlyDictionary<string, object> row, string column)
            => row.TryGetValue(column, out object value) && value is long number ? number : 0;
    }
}
