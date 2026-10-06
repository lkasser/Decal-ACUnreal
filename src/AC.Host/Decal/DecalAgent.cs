using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using AC.Host.Plugins;
using AC.Host.Plugins.Views;
using AC.Host.World;

namespace AC.Host.Decal
{
    /// <summary>
    /// Decal's own window: the plugin list, where plugins are switched on and off and reloaded,
    /// and the switch that lets plugins act at all.
    /// </summary>
    /// <remarks>
    /// Decal kept this in its agent, a window of its own outside the game. Here everything is
    /// in the game, so it is the first window on the bar, drawn in the same theme as every
    /// plugin's. It is the host's, not a plugin's: it is always there, cannot be switched off,
    /// and knows nothing about any particular plugin - Virindi Tank included.
    /// </remarks>
    public sealed class DecalAgent : IPlugin, IOverlayView, IOverlayHotkeys, IOverlayViews, IOverlayCommands
    {
        /// <summary>The name its window goes by on the bar and in commands.</summary>
        public const string PluginName = "Decal";

        internal const string ViewResource = "AC.Host.Decal.DecalAgent.xml";

        private const int ColumnOn = 0;
        private const int ColumnName = 1;
        private const int ColumnVersion = 2;
        private const int ColumnStatus = 3;
        private const int ColumnReload = 4;

        private readonly GameHost _host;
        private readonly PluginManager _plugins;
        private DecalView _view;
        private PluginSettings<DecalSettings> _settings;
        private string _iconKey = "host:decal";

        /// <summary>The movement keys this window binds, by <see cref="GameKey"/>, and their edit boxes.</summary>
        private static readonly (GameKey Key, string Edit)[] KeyEdits =
        {
            (GameKey.Forward, "txtKeyForward"),
            (GameKey.Backward, "txtKeyBackward"),
            (GameKey.TurnLeft, "txtKeyTurnLeft"),
            (GameKey.TurnRight, "txtKeyTurnRight"),
        };

        /// <summary>What Decal's window keeps between sessions.</summary>
        public sealed class DecalSettings
        {
            /// <summary>Movement keys the player rebound, by <see cref="GameKey"/> name, as virtual-key codes.</summary>
            public Dictionary<string, int> Keys { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Hotkeys the player bound, by "plugin/action", as "Ctrl+F12"; empty for unbound.</summary>
            public Dictionary<string, string> Hotkeys { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Hotkeys switched off in a hotkey window, by "plugin/action": they keep their key.</summary>
            public List<string> DisabledHotkeys { get; set; } = new List<string>();

            /// <summary>
            /// The VVS windows given a hotkey in VHS's Add VVS, by the name VHS knew them by, as its
            /// VVSWindowToggleKeys table kept them.
            /// </summary>
            public List<string> WindowToggles { get; set; } = new List<string>();

            /// <summary>
            /// "Keep playing while the game is minimized": minimizing the game parks it off-screen
            /// instead, where it goes on taking the keys plugins hold. See <see cref="GameHost.KeepPlayingMinimized"/>.
            /// </summary>
            public bool KeepPlayingMinimized { get; set; }
        }

        private HotkeyWindows _hotkeyWindows;

        /// <summary>
        /// The Decal Hotkey System, on Decal's bar - the one switch the standard client's Decal
        /// bar had - and the Virindi Hotkey System, on VVS's, over the host's hotkeys.
        /// </summary>
        public IReadOnlyList<OverlayViewWindow> Views
        {
            get
            {
                if (_hotkeyWindows == null)
                    return Array.Empty<OverlayViewWindow>();
                _hotkeyWindows.Refresh();
                return _hotkeyWindows.Windows;
            }
        }

        /// <summary>The key the overlay caught for a hotkey window's Set.</summary>
        public bool HandleCommand(OverlayCommand command)
        {
            if (command != null && command.Name == HotkeyWindows.KeyCapturedCommand && _hotkeyWindows != null)
                return _hotkeyWindows.KeyCaptured(command.Value);
            return false;
        }

        /// <summary>Decal's own hotkey: the acting switch, for a player who wants it under a key.</summary>
        private static readonly HotkeyDefinition[] OwnHotkeys =
        {
            new HotkeyDefinition("ToggleActing", "Let plugins act (on/off)"),
        };

        /// <summary>The hotkeys the page lists, in its order, so a row or an option finds its hotkey.</summary>
        private readonly List<BoundHotkey> _hotkeysShown = new List<BoundHotkey>();

        /// <summary>Decal's own hotkey and one for each window given a key in VHS's Add VVS.</summary>
        private HotkeyDefinition[] _hotkeys = OwnHotkeys;

        public IReadOnlyList<HotkeyDefinition> Hotkeys => _hotkeys;

        public void HotkeyPressed(string id)
        {
            if (id == "ToggleActing" && _host.CanAct)
            {
                _host.ActionsAllowed = !_host.ActionsAllowed;
                _host.ShowInGame(_host.ActionsAllowed ? "Plugins may act." : "Plugins only watch.");
            }
            else if (id != null && id.StartsWith(HotkeyWindows.TogglePrefix, StringComparison.Ordinal))
            {
                // Showing or hiding a window is the player's own business, so it does not wait
                // for plugins to be let act.
                HotkeyWindows.FindToggled(_host.CollectWindows(), id.Substring(HotkeyWindows.TogglePrefix.Length))?.RequestToggle();
            }
        }

        private IReadOnlyCollection<string> WindowToggles => (IReadOnlyCollection<string>)_settings?.Value.WindowToggles ?? Array.Empty<string>();

        private void SetWindowToggle(string name, bool on)
        {
            List<string> toggles = _settings.Value.WindowToggles ??= new List<string>();
            toggles.RemoveAll(t => t == name);
            if (on)
                toggles.Add(name);
            _settings.Save();
            BuildHotkeys();
        }

        /// <summary>VHS's own words for them: "Toggle:name", "Toggle window: name", with no key until one is set.</summary>
        private void BuildHotkeys()
        {
            List<HotkeyDefinition> hotkeys = new List<HotkeyDefinition>(OwnHotkeys);
            foreach (string name in _settings?.Value.WindowToggles ?? new List<string>())
            {
                if (!string.IsNullOrEmpty(name))
                    hotkeys.Add(new HotkeyDefinition(HotkeyWindows.TogglePrefix + name, "Toggle window: " + name, source: "VVS"));
            }

            _hotkeys = hotkeys.ToArray();
        }

        // The walk test: where it started, and how long it has run.
        private Location? _walkFrom;
        private TimeSpan _walked;
        private bool _walking;

        /// <summary>How long the test holds the forward key.</summary>
        private static readonly TimeSpan WalkFor = TimeSpan.FromSeconds(1);

        /// <summary>How long after letting go before the distance is read: the client reports where it stopped.</summary>
        private static readonly TimeSpan SettleFor = TimeSpan.FromMilliseconds(700);

        /// <summary>
        /// The rows in the order the list shows them - each plugin, then the plugins it runs, as
        /// Decal's own are listed under DecalCompat - so a click's row finds its plugin.
        /// </summary>
        private readonly List<PluginListRow> _rows = new List<PluginListRow>();

        /// <summary>The row whose reason the line beneath the list shows, by <see cref="PluginListRow.Key"/>.</summary>
        private string _selectedKey;

        public DecalAgent(GameHost host, PluginManager plugins)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _plugins = plugins;
        }

        public string Name => PluginName;

        /// <summary>The window, brought up to date each time the host asks for it.</summary>
        public DecalView View
        {
            get
            {
                if (_view != null)
                    Refresh();
                return _view;
            }
        }

        public void Startup(IHost host)
        {
            _settings = host.LoadSettings<DecalSettings>(this);

            // Decal's own icon, the round C and its gold cross, on the bar and the title bar.
            _iconKey = "host:decal";

            // Keys the player rebound here, unless the command line named one for this run.
            foreach (KeyValuePair<string, int> saved in _settings.Value.Keys ?? new Dictionary<string, int>())
            {
                if (Enum.TryParse(saved.Key, ignoreCase: true, out GameKey key) && host.GetSetting(this, "Key." + key) == null)
                    _host.InputKeys.Bind(key, saved.Value);
            }

            foreach (KeyValuePair<string, string> saved in _settings.Value.Hotkeys ?? new Dictionary<string, string>())
            {
                if (KeyChord.TryParse(saved.Value, out KeyChord chord))
                    _host.HotkeyBindings[saved.Key] = chord;
            }

            foreach (string disabled in _settings.Value.DisabledHotkeys ?? new List<string>())
                _host.DisabledHotkeys.Add(disabled);

            // Keeping on while minimized as the player left it, unless the command line said for
            // this run; and remembered whenever it changes, from here or from the control pipe.
            if (!_host.Settings.ContainsKey("Overlay:KeepPlayingMinimized"))
                _host.KeepPlayingMinimized = _settings.Value.KeepPlayingMinimized;
            _host.KeepPlayingMinimizedChanged += OnKeepPlayingMinimizedChanged;

            BuildHotkeys();
            _hotkeyWindows = new HotkeyWindows(_host, PluginName, Bind, EnableHotkey, () => WindowToggles, SetWindowToggle);

            // Decal's own window was never on Decal's bar - the agent was a program beside the
            // game - so it has no switch; Ctrl and a click on the bar's second grip opens it.
            _view = LoadView();
            _view.IconKey = _iconKey;
            _view.ShowInBar = false;
            _view.OpensFromBarGrip = true;
            Bind();
            Refresh();
            ShowKeys();
            host.Tick += OnTick;
        }

        public void Shutdown()
        {
            _host.Tick -= OnTick;
            _host.KeepPlayingMinimizedChanged -= OnKeepPlayingMinimizedChanged;
            if (_walking)
                _host.InputKeys.Hold(GameKey.Forward, false);
        }

        internal static DecalView LoadView()
        {
            using Stream stream = typeof(DecalAgent).Assembly.GetManifestResourceStream(ViewResource)
                ?? throw new InvalidOperationException($"The host was built without {ViewResource}.");
            using StreamReader reader = new StreamReader(stream);
            return DecalView.Parse(reader.ReadToEnd());
        }

        private void Bind()
        {
            if (_view.TryGet("lstPlugins", out List list))
                list.Clicked += (_, e) => OnPluginClicked(list, e.Row, e.Column);

            if (_view.TryGet("btnRescan", out PushButton rescan))
                rescan.Clicked += (_, _) => Rescan();

            if (_view.TryGet("btnReloadAll", out PushButton reloadAll))
                reloadAll.Clicked += (_, _) => ReloadAll();

            if (_view.TryGet("chkAct", out Checkbox act))
                act.Changed += (_, e) => SetActing(act, e.Checked);

            if (_view.TryGet("chkKeepPlaying", out Checkbox keepPlaying))
                keepPlaying.Changed += (_, e) => _host.KeepPlayingMinimized = e.Checked;

            foreach ((GameKey key, string name) in KeyEdits)
            {
                if (_view.TryGet(name, out Edit edit))
                    edit.Changed += (_, e) => Rebind(key, e.Text);
            }

            if (_view.TryGet("btnTestWalk", out PushButton walk))
                walk.Clicked += (_, _) => StartWalkTest();

            if (_view.TryGet("lstHotkeys", out List hotkeys))
                hotkeys.Clicked += (_, e) => PickHotkey(e.Row);

            if (_view.TryGet("cmbHotkey", out Choice pick))
                pick.Changed += (_, e) => PickHotkey(e.Selected);

            if (_view.TryGet("btnSetHotkey", out PushButton set))
                set.Clicked += (_, _) => SetHotkey(clear: false);

            if (_view.TryGet("btnClearHotkey", out PushButton clear))
                clear.Clicked += (_, _) => SetHotkey(clear: true);
        }

        // ------------------------------------------------------------------- hotkeys

        private void PickHotkey(int index)
        {
            if (index < 0 || index >= _hotkeysShown.Count)
                return;

            if (_view.TryGet("cmbHotkey", out Choice pick) && pick.Selected != index)
                pick.Selected = index;
            if (_view.TryGet("txtHotkeyKeys", out Edit keys))
                keys.Text = _hotkeysShown[index].Keys.ToString();
        }

        private void SetHotkey(bool clear)
        {
            if (!_view.TryGet("cmbHotkey", out Choice pick) || pick.Selected < 0 || pick.Selected >= _hotkeysShown.Count)
            {
                _host.Log.Info("Decal: pick the action to bind first.");
                return;
            }

            BoundHotkey hotkey = _hotkeysShown[pick.Selected];
            string text = clear ? string.Empty : _view.TryGet("txtHotkeyKeys", out Edit keys) ? keys.Text : string.Empty;
            if (!KeyChord.TryParse(text, out KeyChord chord))
            {
                _host.Log.Info("Decal: \"" + text + "\" is not a key. Try Ctrl+F12, Alt+1, F5 or Shift+Home.");
                return;
            }

            // The key goes to this action alone: whatever had it before loses it.
            if (!chord.IsEmpty)
            {
                foreach (BoundHotkey other in _host.CollectHotkeys())
                {
                    if (other.Name != hotkey.Name && other.Keys.Equals(chord))
                        Bind(other.Name, default);
                }
            }

            Bind(hotkey.Name, chord);
            _host.Log.Info(chord.IsEmpty
                ? "Decal: " + hotkey.Owner + "'s \"" + hotkey.Definition.Description + "\" has no key now."
                : "Decal: " + chord + " now does " + hotkey.Owner + "'s \"" + hotkey.Definition.Description + "\".");
            RefreshHotkeys();
            PickHotkey(pick.Selected);
        }

        private void EnableHotkey(string name, bool on)
        {
            if (on)
                _host.DisabledHotkeys.Remove(name);
            else
                _host.DisabledHotkeys.Add(name);
            _settings.Value.DisabledHotkeys = _host.DisabledHotkeys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            _settings.Save();
        }

        private void Bind(string name, KeyChord chord)
        {
            _host.HotkeyBindings[name] = chord;
            _settings.Value.Hotkeys ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _settings.Value.Hotkeys[name] = chord.ToString();
            _settings.Save();
        }

        private void RefreshHotkeys()
        {
            IReadOnlyList<BoundHotkey> all = _host.CollectHotkeys();
            bool changed = all.Count != _hotkeysShown.Count
                || all.Where((h, i) => h.Name != _hotkeysShown[i].Name || !h.Keys.Equals(_hotkeysShown[i].Keys)).Any();
            if (!changed)
                return;

            _hotkeysShown.Clear();
            _hotkeysShown.AddRange(all);

            if (_view.TryGet("lstHotkeys", out List list))
            {
                list.Clear();
                foreach (BoundHotkey hotkey in _hotkeysShown)
                {
                    ListRow row = list.Add();
                    row[0].Text = hotkey.Owner;
                    row[1].Text = hotkey.Definition.Description;
                    row[2].Text = hotkey.Keys.IsEmpty ? "(none)" : hotkey.Keys.ToString();
                }
            }

            if (_view.TryGet("cmbHotkey", out Choice pick))
            {
                int selected = pick.Selected;
                pick.Clear();
                foreach (BoundHotkey hotkey in _hotkeysShown)
                    pick.Add(hotkey.Owner + ": " + hotkey.Definition.Description, hotkey.Name);
                if (selected >= 0 && selected < pick.Count)
                    pick.Selected = selected;
            }
        }

        // ------------------------------------------------------------------- movement keys

        /// <summary>The movement keys the Options page binds, in its order.</summary>
        public static IReadOnlyList<GameKey> MovementKeys { get; } = KeyEdits.Select(k => k.Key).ToArray();

        /// <summary>
        /// Binds a movement key and remembers it, exactly as typing into the Options page's box
        /// does - for a window outside the game, such as the Decal Agent's. Game thread only,
        /// once the host has started.
        /// </summary>
        /// <returns>False, with the reason in the log, when <paramref name="text"/> names no key.</returns>
        public bool BindKey(GameKey key, string text)
        {
            if (_settings == null)
                throw new InvalidOperationException("Decal's window has not started yet.");

            if (!GameInput.TryParseKey(text, out _))
            {
                _host.Log.Info($"Decal: \"{text}\" is not a key. Use a letter, a digit, Space, Shift, Up, Down, Left, Right or a key number.");
                return false;
            }

            Rebind(key, text);
            return true;
        }

        private void Rebind(GameKey key, string text)
        {
            if (!GameInput.TryParseKey(text, out int code))
            {
                _host.Log.Info($"Decal: \"{text}\" is not a key. Use a letter, a digit, Space, Shift, Up, Down, Left, Right or a key number.");
                ShowKeys();
                return;
            }

            _host.InputKeys.Bind(key, code);
            _settings.Value.Keys ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _settings.Value.Keys[key.ToString()] = code;
            _settings.Save();
            _host.Log.Info($"Decal: {key} is now {GameInput.Describe(code)}.");
            ShowKeys();
        }

        private void OnKeepPlayingMinimizedChanged(object sender, bool on)
        {
            if (_settings == null)
                return;

            _settings.Value.KeepPlayingMinimized = on;
            _settings.Save();
            if (_view != null && _view.TryGet("chkKeepPlaying", out Checkbox box))
                box.Checked = on;
        }

        private void ShowKeys()
        {
            foreach ((GameKey key, string name) in KeyEdits)
            {
                if (_view.TryGet(name, out Edit edit))
                    edit.Text = GameInput.Describe(_host.InputKeys.VirtualKey(key));
            }
        }

        /// <summary>
        /// Holds the forward key for a second and says how far the character went: the one
        /// honest check that the keys are the game's and that the overlay is pressing them.
        /// </summary>
        private void StartWalkTest()
        {
            if (_walking)
                return;

            if (!_host.InputKeys.IsAvailable)
            {
                SetText("lblTestWalk", !_host.ActionsAllowed || !_host.CanAct ? "Tick 'Let plugins act' first." : "The overlay is not attached.");
                return;
            }

            _walkFrom = _host.Character.Location;
            if (!_walkFrom.HasValue)
            {
                SetText("lblTestWalk", "The character has not said where it is yet.");
                return;
            }

            _walking = true;
            _walked = TimeSpan.Zero;
            _host.InputKeys.Hold(GameKey.Forward, true);
            SetText("lblTestWalk", "Walking forward for a second...");
        }

        private void OnTick(object sender, TimeSpan elapsed)
        {
            if (!_walking)
                return;

            _walked += elapsed;
            if (_walked >= WalkFor && _host.InputKeys.Held.Contains(GameKey.Forward))
                _host.InputKeys.Hold(GameKey.Forward, false);

            if (_walked < WalkFor + SettleFor)
                return;

            _walking = false;
            Location? now = _host.Character.Location;
            double moved = now.HasValue && _walkFrom.HasValue ? Distance(_walkFrom.Value, now.Value) : 0;
            string said = moved > 0.5
                ? string.Create(CultureInfo.InvariantCulture, $"Moved {moved:0.0} m forward: the keys work.")
                : _host.GameWindow.Minimized
                    ? "Did not move: the game is minimized."
                    : $"Did not move. Is {GameInput.Describe(_host.InputKeys.VirtualKey(GameKey.Forward))} the game's forward key?";
            SetText("lblTestWalk", said);
            _host.Log.Info("Decal walk test: " + said);
        }

        /// <summary>Metres between two positions on the ground, across landblocks.</summary>
        internal static double Distance(Location a, Location b)
        {
            double ax = ((a.LandblockCell >> 24) & 0xFF) * 192.0 + a.X;
            double ay = ((a.LandblockCell >> 16) & 0xFF) * 192.0 + a.Y;
            double bx = ((b.LandblockCell >> 24) & 0xFF) * 192.0 + b.X;
            double by = ((b.LandblockCell >> 16) & 0xFF) * 192.0 + b.Y;
            return Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        }

        // ------------------------------------------------------------------- what the player does

        private void OnPluginClicked(List list, int row, int column)
        {
            if (row < 0 || row >= _rows.Count)
                return;

            PluginListRow shown = _rows[row];
            _selectedKey = shown.Key;

            if (column == ColumnOn && row < list.RowCount)
            {
                // The list has already toggled the lamp, as Decal's did; do what it now shows. A
                // plugin the host replaces refuses, and its lamp goes back on the next refresh.
                bool on = list[row][ColumnOn].Checked;
                if (!shown.Hosted)
                {
                    PluginEntry entry = EntryFor(shown);
                    if (entry != null)
                        _plugins.SetEnabled(entry, on);
                }
                else
                {
                    HostOf(shown)?.SetHostedEnabled(shown.Name, on);
                }
            }
            else if (column == ColumnReload && CanReload(shown))
            {
                if (!shown.Hosted)
                {
                    PluginEntry entry = EntryFor(shown);
                    if (entry != null)
                        _plugins.Reload(entry);
                }
                else
                {
                    HostOf(shown)?.ReloadHosted(shown.Name);
                }
            }

            Refresh();
        }

        /// <summary>One of the host's own plugins, as a row names it.</summary>
        private PluginEntry EntryFor(PluginListRow row)
            => _plugins?.Entries.FirstOrDefault(e => string.Equals(e.AssemblyPath, row.AssemblyPath, StringComparison.OrdinalIgnoreCase));

        /// <summary>The plugin that runs a hosted row's plugin.</summary>
        private IHostedPlugins HostOf(PluginListRow row)
            => _host.Plugins.FirstOrDefault(p => p is IHostedPlugins && string.Equals(p.Name, row.HostName, StringComparison.OrdinalIgnoreCase)) as IHostedPlugins;

        /// <summary>Whether a row has a Reload: not for a plugin the host replaces, nor one that cannot run here.</summary>
        private static bool CanReload(PluginListRow row) => row.State != HostedPluginState.Replaced && row.State != HostedPluginState.CannotRun;

        private void Rescan()
        {
            int before = _plugins?.Entries.Count ?? 0;
            _plugins?.Rescan();
            int found = (_plugins?.Entries.Count ?? 0) - before;

            // And what the plugins that run plugins can find: Decal's registry, read again.
            foreach (IHostedPlugins hosting in _host.Plugins.OfType<IHostedPlugins>())
            {
                try
                {
                    hosting.RescanHosted();
                }
                catch (Exception ex)
                {
                    _host.Log.Error($"Plugin {((IPlugin)hosting).Name} threw while looking for the plugins it runs.", ex);
                }
            }

            _host.Log.Info(found > 0 ? $"Decal found {found} new plugin(s)." : "Decal found no new plugins of its own.");
            Refresh();
        }

        private void ReloadAll()
        {
            if (_plugins == null)
                return;

            foreach (PluginEntry entry in _plugins.Entries.Where(e => e.Enabled).ToList())
                _plugins.Reload(entry);

            Refresh();
        }

        private void SetActing(Checkbox box, bool on)
        {
            if (!_host.CanAct)
            {
                _host.Log.Info("This session cannot act: it is a replay, not a live game.");
                box.Checked = false;
                return;
            }

            _host.ActionsAllowed = on;
            box.Checked = _host.ActionsAllowed;
        }

        // ------------------------------------------------------------------- what the window shows

        private void Refresh()
        {
            RefreshPlugins();
            RefreshOptions();
            RefreshHotkeys();
        }

        private void RefreshPlugins()
        {
            if (!_view.TryGet("lstPlugins", out List list))
                return;

            _rows.Clear();
            _rows.AddRange(PluginList.Build(_plugins?.Entries, _host.Plugins,
                (plugin, ex) => _host.Log.Error($"Plugin {plugin.Name} threw while listing the plugins it runs.", ex)));

            // Rebuilt only when it would read differently, so a click in flight still lands on
            // the row it was aimed at.
            while (list.RowCount > _rows.Count)
                list.RemoveAt(list.RowCount - 1);
            while (list.RowCount < _rows.Count)
                list.Add();

            for (int i = 0; i < _rows.Count; i++)
            {
                PluginListRow shown = _rows[i];
                ListRow row = list[i];
                row[ColumnOn].Checked = shown.Enabled;

                // Indented, so a hosted plugin reads as belonging to the one above that runs it.
                row[ColumnName].Text = shown.Hosted ? "   " + shown.Name : shown.Name;
                row[ColumnVersion].Text = shown.Version;
                row[ColumnStatus].Text = shown.Status;
                row[ColumnReload].Text = CanReload(shown) ? "Reload" : string.Empty;
            }

            PluginListRow selected = _rows.FirstOrDefault(r => r.Key == _selectedKey);
            if (selected != null)
                SetText("lblPluginDetail", $"{selected.Name}: {selected.Detail}");
        }

        private void RefreshOptions()
        {
            if (_view.TryGet("chkAct", out Checkbox act))
                act.Checked = _host.CanAct && _host.ActionsAllowed;

            if (_view.TryGet("chkKeepPlaying", out Checkbox keepPlaying))
                keepPlaying.Checked = _host.KeepPlayingMinimized;

            ICharacterView character = _host.Character;
            SetText("lblServer", string.IsNullOrEmpty(_host.World.ServerName) ? "not connected" : _host.World.ServerName);
            SetText("lblCharacter", character.Id == 0 ? "not logged in" : character.Level > 0 ? $"{character.Name}, level {character.Level}" : character.Name);

            Location? location = character.Location;
            SetText("lblPosition", location.HasValue ? Coordinates(location.Value) : string.Empty);

            HostStatistics s = _host.Statistics;
            SetText("lblTraffic", string.Create(CultureInfo.InvariantCulture, $"{s.MessagesInbound} in, {s.MessagesOutbound} out, {_host.World.ObjectCount} objects known"));
            SetText("lblActions", !_host.CanAct ? "not possible in a replay" : _host.ActionsAllowed ? "allowed" : "off - plugins only watch");

            SetText("lblAboutVersion", "Host " + (typeof(GameHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(GameHost).Assembly.GetName().Version?.ToString() ?? string.Empty) + ", plugin contract " + HostApi.Version.ToString(CultureInfo.InvariantCulture));
            SetText("lblAboutFolder", _plugins == null ? string.Empty : "Plugins are found in " + _plugins.Directory);
        }

        private void SetText(string name, string text)
        {
            if (_view.TryGet(name, out StaticText label))
                label.Text = text ?? string.Empty;
        }

        /// <summary>The game's map coordinates for a position, as its radar shows them.</summary>
        internal static string Coordinates(Location location)
        {
            double x = ((location.LandblockCell >> 24) & 0xFF) * 192.0 + location.X;
            double y = ((location.LandblockCell >> 16) & 0xFF) * 192.0 + location.Y;
            double ns = y / 240.0 - 101.95;
            double ew = x / 240.0 - 101.95;
            return Math.Abs(ns).ToString("0.0", CultureInfo.InvariantCulture) + (ns >= 0 ? "N" : "S") + ", "
                + Math.Abs(ew).ToString("0.0", CultureInfo.InvariantCulture) + (ew >= 0 ? "E" : "W");
        }
    }
}
