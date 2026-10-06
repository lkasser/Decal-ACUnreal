using System;
using System.IO;
using System.Text;
using AC.Host.Actions;
using AC.Host.Plugins;

namespace AC.Host.Runtime
{
    /// <summary>
    /// Watches the game client's own settings, read-only: which of AC:Unreal's own plugins the
    /// player has enabled, and with which permissions - its Unattended Combat Manager among them -
    /// from <c>Saved\ClientPlugins\settings.json</c>; its Desktop UI Scale from
    /// <c>Saved\Config\Windows\GameUserSettings.ini</c>; and where its plugin bar is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cheap poll rather than a file-system watcher: every <see cref="LookEvery"/> each file's
    /// length and time of writing are looked at, and only when either has changed are the files read
    /// again - opened sharing everything, read whole and closed, so the client can always write and
    /// replace them. What they say is handed on only when it differs from the last time.
    /// </para>
    /// <para>
    /// Nothing is ever written under the client's folders. A settings.json that is not there yet -
    /// the client's plugin list never opened - is no plugin enabled and the plugin bar where the
    /// client starts it; a Saved folder that is not there is a client not known.
    /// </para>
    /// </remarks>
    public sealed class ClientSettingsWatcher
    {
        /// <summary>How often the files are looked at.</summary>
        public static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(2);

        /// <summary>Where AC:Unreal's installer puts its Saved folder.</summary>
        public static string DefaultSavedFolder
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ACUnreal", "ACUnreal", "Saved");

        /// <summary>The client's own plugins' settings: their grants and window positions.</summary>
        public static string PluginSettingsPath(string saved) => Path.Combine(saved, "ClientPlugins", "settings.json");

        /// <summary>The client's own user settings, its Desktop UI Scale among them.</summary>
        public static string GameUserSettingsPath(string saved) => Path.Combine(saved, "Config", "Windows", "GameUserSettings.ini");

        private readonly Func<string> _findSaved;
        private readonly Action<ClientPluginsState, double, ClientPluginBar> _changed;
        private string _folder;
        private Stamp _plugins;
        private Stamp _display;
        private bool _handed;
        private ClientPluginsState _lastPlugins;
        private double _lastScale;
        private ClientPluginBar _lastBar;

        /// <param name="findSaved">The client's Saved folder now, or null when there is no client to read.</param>
        /// <param name="changed">Given what the settings say whenever it changes - on the thread that polls.</param>
        public ClientSettingsWatcher(Func<string> findSaved, Action<ClientPluginsState, double, ClientPluginBar> changed)
        {
            _findSaved = findSaved ?? throw new ArgumentNullException(nameof(findSaved));
            _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        }

        /// <summary>The Saved folder read last, or null when none was found.</summary>
        public string SavedFolder => _folder;

        /// <summary>
        /// The client's Saved folder: beside the log the running client writes, when that is known,
        /// else where the installer puts it if it is there; null when there is none.
        /// </summary>
        public static string FindSavedFolder(string clientLogPath)
        {
            if (!string.IsNullOrEmpty(clientLogPath))
            {
                string logs = Path.GetDirectoryName(clientLogPath);
                string saved = logs != null ? Path.GetDirectoryName(logs) : null;
                if (saved != null && Directory.Exists(saved))
                    return saved;
            }

            string installed = DefaultSavedFolder;
            return Directory.Exists(installed) ? installed : null;
        }

        /// <summary>Looks at the two files, and reads them and hands on what they say when either has changed.</summary>
        public void Poll()
        {
            string folder = _findSaved();
            if (folder == null)
            {
                _folder = null;
                _plugins = _display = default;
                Hand(ClientPluginsState.Unknown, ClientDisplay.DefaultUiScale, null);
                return;
            }

            string pluginsPath = PluginSettingsPath(folder);
            string displayPath = GameUserSettingsPath(folder);
            Stamp plugins = Stamp.Of(pluginsPath);
            Stamp display = Stamp.Of(displayPath);
            if (_handed && string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase) && plugins.Equals(_plugins) && display.Equals(_display))
                return;

            // Being written just now: the next look reads it.
            if (!TryRead(pluginsPath, out string pluginsText) || !TryRead(displayPath, out string displayText))
                return;

            _folder = folder;
            _plugins = plugins;
            _display = display;
            Hand(pluginsText == null ? ClientPluginsState.NoneEnabled : ClientPluginsState.Parse(pluginsText),
                 ClientDisplay.ReadDesktopUiScale(displayText),
                 ClientPluginBar.Read(pluginsText));
        }

        private void Hand(ClientPluginsState plugins, double scale, ClientPluginBar bar)
        {
            if (_handed && plugins.Equals(_lastPlugins) && scale == _lastScale && Equals(bar, _lastBar))
                return;

            _handed = true;
            _lastPlugins = plugins;
            _lastScale = scale;
            _lastBar = bar;
            _changed(plugins, scale, bar);
        }

        /// <summary>The whole file, or null when it is not there; false when it could not be read just now.</summary>
        private static bool TryRead(string path, out string text)
        {
            text = null;
            try
            {
                if (!File.Exists(path))
                    return true;

                using FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using StreamReader reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                text = reader.ReadToEnd();
                return true;
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        /// <summary>A file's length and time of writing, which change whenever it is written; default for one not there.</summary>
        private readonly record struct Stamp(bool Exists, long Length, DateTime Written)
        {
            public static Stamp Of(string path)
            {
                try
                {
                    FileInfo info = new FileInfo(path);
                    return info.Exists ? new Stamp(true, info.Length, info.LastWriteTimeUtc) : default;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
                {
                    return default;
                }
            }
        }
    }
}
