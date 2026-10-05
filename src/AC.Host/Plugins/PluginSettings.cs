using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AC.Host.Plugins
{
    /// <summary>
    /// A plugin's settings: one typed object, kept in one file, in the plugin's own
    /// directory.
    /// </summary>
    /// <remarks>
    /// Before this, a plugin's only channel was <c>--set Name:Key=Value</c> into a
    /// read-only string dictionary. Plugins compared strings by hand, nothing was ever
    /// written back, nothing survived a restart, and a display with switches on it had no
    /// way to keep them switched. Decal had the same gap and every plugin solved it its
    /// own way; Chorizite's answer - one serialisable settings type per plugin - is the
    /// one worth copying, and this is that.
    ///
    /// <para>
    /// <typeparamref name="T"/> is the plugin's own class with public properties and a
    /// parameterless constructor. Its defaults are the defaults: a missing file, or a
    /// missing property in an old file, simply leaves the constructor's value in place,
    /// so a plugin can add a setting without migrating anything. A file that cannot be
    /// parsed at all is set aside under a <c>.broken</c> name rather than overwritten,
    /// because the player's settings are worth more than a clean start.
    /// </para>
    ///
    /// <para>
    /// Saved when the plugin asks, and again by the host at shutdown, so a plugin that
    /// never calls <see cref="Save"/> still keeps what it changed.
    /// </para>
    /// </remarks>
    public sealed class PluginSettings<T> where T : class, new()
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private readonly IPluginLog _log;

        internal PluginSettings(string path, IPluginLog log)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            _log = log;
            Value = Load();
        }

        /// <summary>The settings themselves. Change them here and call <see cref="Save"/>.</summary>
        public T Value { get; private set; }

        /// <summary>Where they live on disk.</summary>
        public string Path { get; }

        /// <summary>
        /// True if the file was read successfully; false if this is the first run, or the
        /// file could not be read and the defaults are in use.
        /// </summary>
        public bool LoadedFromDisk { get; private set; }

        /// <summary>
        /// Writes the settings out. Never throws into the plugin: a settings file that
        /// cannot be written is logged and the session carries on with the values in
        /// memory, which is the least bad outcome available.
        /// </summary>
        public bool Save()
        {
            try
            {
                string directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                // Written beside and then moved into place, so a crash mid-write leaves
                // the previous file intact rather than a truncated one.
                string temporary = Path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(Value, Options));
                File.Move(temporary, Path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                _log?.Error($"Could not save settings to {Path}.", ex);
                return false;
            }
        }

        /// <summary>Discards the in-memory values and re-reads the file.</summary>
        public void Reload()
        {
            Value = Load();
        }

        private T Load()
        {
            LoadedFromDisk = false;

            if (!File.Exists(Path))
                return new T();

            string text;
            try
            {
                text = File.ReadAllText(Path);
            }
            catch (Exception ex)
            {
                _log?.Error($"Could not read settings from {Path}; using defaults.", ex);
                return new T();
            }

            try
            {
                T loaded = JsonSerializer.Deserialize<T>(text, Options);
                if (loaded == null)
                    return new T();

                LoadedFromDisk = true;
                return loaded;
            }
            catch (JsonException ex)
            {
                // Set aside rather than overwritten: whatever is in there was the
                // player's, and the next Save would otherwise erase it for good.
                string aside = Path + ".broken";
                try
                {
                    File.Copy(Path, aside, overwrite: true);
                }
                catch (Exception)
                {
                    aside = "(could not be set aside)";
                }

                _log?.Error($"{Path} is not valid JSON and was set aside as {aside}; using defaults.", ex);
                return new T();
            }
        }
    }
}
