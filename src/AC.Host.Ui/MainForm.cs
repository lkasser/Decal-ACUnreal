using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AC.Dat;
using AC.Host;
using AC.Host.Plugins;
using AC.Host.Transport;
using AC.Host.World;
using AC.Proxy;

namespace AC.Host.Ui
{
    /// <summary>
    /// A window, because the client cannot be drawn into.
    /// </summary>
    /// <remarks>
    /// Decal drew its panels inside the game by injecting into the client's rendering.
    /// AC:Unreal has no plugin API and loads no Decal, so nothing can draw in there -
    /// the closest honest equivalent is a window of our own that can be kept on top of
    /// the game. That is what this is.
    ///
    /// It is the same host as the console one, with the same plugins loaded the same
    /// way; only the reporting differs. Every host event arrives on the game thread, so
    /// everything here marshals onto the UI thread before touching a control.
    /// </remarks>
    public sealed class MainForm : Form
    {
        private const int MaxRows = 500;

        private readonly TextBox _server = new TextBox { Text = "127.0.0.1", Width = 110 };
        private readonly NumericUpDown _serverPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 9000, Width = 70 };
        private readonly NumericUpDown _listenPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 9100, Width = 70 };
        private readonly CheckBox _enableActions = new CheckBox { Text = "Let plugins act", AutoSize = true };
        private readonly CheckBox _loot = new CheckBox { Text = "Pick loot up", AutoSize = true };
        private readonly CheckBox _onTop = new CheckBox { Text = "Always on top", AutoSize = true, Checked = true };
        private readonly Button _start = new Button { Text = "Start", Width = 80 };
        private readonly Button _stop = new Button { Text = "Stop", Width = 80, Enabled = false };
        private readonly Label _status = new Label { AutoSize = true, Text = "Not running." };

        private readonly ListView _combat = MakeList("Attacker", 170, "Damage", 70, "Type", 90, "Where", 120, "Health", 70);

        /// <summary>
        /// Tabs for whatever the plugins are showing, keyed by panel so a tab is reused
        /// rather than rebuilt while someone is reading it.
        /// </summary>
        private readonly TabControl _pluginTabs = new TabControl { Dock = DockStyle.Fill };
        private readonly Dictionary<string, ListView> _pluginPanels = new Dictionary<string, ListView>(StringComparer.Ordinal);
        private readonly ListBox _chat = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly ListBox _log = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };

        private readonly Label _character = new Label { AutoSize = true, Text = "-" };
        private readonly Label _position = new Label { AutoSize = true, Text = "-" };
        private readonly Label _counters = new Label { AutoSize = true, Text = "-" };
        private readonly Label _lootCounts = new Label { AutoSize = true, Text = "-" };

        private readonly UiLog _uiLog;
        private GameHost _host;
        private PortalData _portalData;
        private CancellationTokenSource _stopping;
        private string _profilePath;

        public MainForm()
        {
            Text = "VirindiTank";
            Size = new Size(760, 560);
            MinimumSize = new Size(560, 380);
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(40, 40);

            _uiLog = new UiLog(this);
            _profilePath = DefaultProfilePath();

            Controls.Add(BuildTabs());
            Controls.Add(BuildToolbar());

            _start.Click += (_, __) => Start();
            _stop.Click += (_, __) => _ = StopAsync();
            _onTop.CheckedChanged += (_, __) => TopMost = _onTop.Checked;
            _loot.CheckedChanged += (_, __) =>
            {
                if (_loot.Checked && !_enableActions.Checked)
                    _enableActions.Checked = true;
            };

            FormClosing += (_, __) => StopAsync().GetAwaiter().GetResult();
        }

        // ---------------------------------------------------------------- layout

        private Control BuildToolbar()
        {
            TableLayoutPanel bar = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 1,
                Padding = new Padding(8, 8, 8, 4),
            };

            FlowLayoutPanel row1 = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty };
            row1.Controls.Add(new Label { Text = "Server", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
            row1.Controls.Add(_server);
            row1.Controls.Add(new Label { Text = "port", AutoSize = true, Padding = new Padding(6, 6, 4, 0) });
            row1.Controls.Add(_serverPort);
            row1.Controls.Add(new Label { Text = "listen on", AutoSize = true, Padding = new Padding(10, 6, 4, 0) });
            row1.Controls.Add(_listenPort);
            row1.Controls.Add(_start);
            row1.Controls.Add(_stop);

            FlowLayoutPanel row2 = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 0) };
            row2.Controls.Add(_enableActions);
            row2.Controls.Add(new Label { Width = 12, AutoSize = false });
            row2.Controls.Add(_loot);
            row2.Controls.Add(new Label { Width = 12, AutoSize = false });
            row2.Controls.Add(_onTop);

            Button profile = new Button { Text = "Profile...", Width = 90 };
            profile.Click += (_, __) => ChooseProfile();
            row2.Controls.Add(profile);

            bar.Controls.Add(row1);
            bar.Controls.Add(row2);
            bar.Controls.Add(_status);
            return bar;
        }

        private Control BuildTabs()
        {
            TabControl tabs = new TabControl { Dock = DockStyle.Fill };

            TabPage status = new TabPage("Status");
            FlowLayoutPanel panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10) };
            panel.Controls.Add(Heading("Character"));
            panel.Controls.Add(_character);
            panel.Controls.Add(Heading("Position"));
            panel.Controls.Add(_position);
            panel.Controls.Add(Heading("Session"));
            panel.Controls.Add(_counters);
            panel.Controls.Add(Heading("Looting"));
            panel.Controls.Add(_lootCounts);
            status.Controls.Add(panel);

            tabs.TabPages.Add(status);
            tabs.TabPages.Add(Page("Plugins", _pluginTabs));
            tabs.TabPages.Add(Page("Combat", _combat));
            tabs.TabPages.Add(Page("Chat", _chat));
            tabs.TabPages.Add(Page("Log", _log));
            return tabs;
        }

        private static Label Heading(string text)
            => new Label { Text = text, AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), Margin = new Padding(0, 8, 0, 2) };

        private static TabPage Page(string name, Control content)
        {
            TabPage page = new TabPage(name);
            content.Dock = DockStyle.Fill;
            page.Controls.Add(content);
            return page;
        }

        private static ListView MakeList(params object[] columns)
        {
            ListView list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                Dock = DockStyle.Fill,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
            };

            for (int i = 0; i < columns.Length; i += 2)
                list.Columns.Add((string)columns[i], (int)columns[i + 1]);

            return list;
        }

        // ---------------------------------------------------------------- running

        private void Start()
        {
            try
            {
                ProxyOptions options = new ProxyOptions
                {
                    ServerHost = _server.Text.Trim(),
                    ServerPort = (int)_serverPort.Value,
                    ListenPort = (int)_listenPort.Value,
                };

                _stopping = new CancellationTokenSource();

                // Client data is a nicety, never a requirement: without it spells have
                // numbers instead of names.
                _portalData = TryOpenPortalData();

                Dictionary<string, string> settings = new Dictionary<string, string>
                {
                    ["VirindiTank:Profile"] = _profilePath,
                };

                if (_loot.Checked && _enableActions.Checked)
                    settings["VirindiTank:Loot"] = "true";

                ProxyTransport transport = new ProxyTransport(options, enableActions: _enableActions.Checked);

                WorldState world = new WorldState();
                if (_portalData != null)
                    world.GameData = new PortalGameData(_portalData);

                _host = new GameHost(transport, _uiLog, settings, dataRoot: null, world: world);

                foreach (LoadedPlugin plugin in PluginLoader.LoadFrom(PluginDirectory(), _uiLog))
                    _host.AddPlugin(plugin.Plugin);

                Subscribe(_host);

                _ = _host.StartAsync(_stopping.Token).ContinueWith(
                    t => Report(t.Exception?.GetBaseException()),
                    TaskScheduler.Default);

                _start.Enabled = false;
                _stop.Enabled = true;
                _server.Enabled = _serverPort.Enabled = _listenPort.Enabled = false;
                _enableActions.Enabled = _loot.Enabled = false;

                SetStatus($"Relaying {options.ListenPort} to {options.ServerHost}:{options.ServerPort}."
                    + (_enableActions.Checked ? " Plugins can act." : " Watching only.")
                    + (settings.ContainsKey("VirindiTank:Loot") ? " Loot will be picked up." : string.Empty));
            }
            catch (Exception ex)
            {
                Report(ex);
                SetStatus("Could not start: " + ex.Message);
                _ = StopAsync();
            }
        }

        private async Task StopAsync()
        {
            GameHost host = _host;
            _host = null;

            if (host != null)
            {
                Unsubscribe(host);

                try
                {
                    await host.DisposeAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Report(ex);
                }
            }

            _stopping?.Cancel();
            _stopping?.Dispose();
            _stopping = null;

            _portalData?.Dispose();
            _portalData = null;

            if (!IsDisposed)
            {
                _start.Enabled = true;
                _stop.Enabled = false;
                _server.Enabled = _serverPort.Enabled = _listenPort.Enabled = true;
                _enableActions.Enabled = _loot.Enabled = true;
                SetStatus("Stopped.");
            }
        }

        private void Subscribe(GameHost host)
        {
            host.ChatReceived += OnChat;
            host.DamageTaken += OnDamage;
            host.CharacterUpdated += OnCharacter;
            host.Tick += OnTick;
        }

        private void Unsubscribe(GameHost host)
        {
            host.ChatReceived -= OnChat;
            host.DamageTaken -= OnDamage;
            host.CharacterUpdated -= OnCharacter;
            host.Tick -= OnTick;
        }

        // ---------------------------------------------------------------- events

        private void OnChat(object sender, ChatMessage message)
            => OnUi(() => Append(_chat, $"[{message.Kind}] {message.Text}"));

        private void OnDamage(object sender, DamageTaken damage)
            => OnUi(() => Add(
                _combat,
                damage.IsCritical ? Color.Firebrick : Color.Black,
                damage.Attacker,
                damage.Amount.ToString(CultureInfo.InvariantCulture) + (damage.IsCritical ? " crit" : string.Empty),
                DamageTypes.Name(damage.DamageType),
                BodyParts.Name(damage.Location),
                (damage.Percentage * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%"));

        private void OnCharacter(object sender, EventArgs e) => OnUi(RefreshStatus);

        private void OnTick(object sender, TimeSpan elapsed)
        {
            OnUi(RefreshStatus);

            // Tick arrives on the game thread, which is the only thread on which a
            // plugin's state is not moving - so the panels are collected here and only
            // the finished rows cross to the UI thread.
            GameHost host = _host;
            if (host == null)
                return;

            IReadOnlyList<OverlayPanel> panels = host.CollectPanels();
            OnUi(() => ShowPanels(panels));
        }

        /// <summary>
        /// Brings the plugin tabs into line with what the plugins are showing.
        /// </summary>
        /// <remarks>
        /// Tabs are matched by panel key and their rows replaced in place. Rebuilding the
        /// tab for every publish would reset the scroll position and the selected tab
        /// several times a second, which makes a panel unreadable.
        /// </remarks>
        private void ShowPanels(IReadOnlyList<OverlayPanel> panels)
        {
            foreach (OverlayPanel panel in panels)
            {
                if (!_pluginPanels.TryGetValue(panel.Key, out ListView list))
                {
                    list = new ListView
                    {
                        View = View.Details,
                        FullRowSelect = true,
                        Dock = DockStyle.Fill,
                        HeaderStyle = ColumnHeaderStyle.Nonclickable,
                    };

                    foreach (string column in panel.Columns)
                        list.Columns.Add(column, 150);

                    _pluginPanels[panel.Key] = list;
                    _pluginTabs.TabPages.Add(Page(string.IsNullOrEmpty(panel.Title) ? panel.Key : panel.Title, list));
                }

                Fill(list, panel);
            }
        }

        private static void Fill(ListView list, OverlayPanel panel)
        {
            list.BeginUpdate();

            try
            {
                list.Items.Clear();

                foreach (OverlayRow row in panel.Rows)
                {
                    string[] cells = new string[Math.Max(1, panel.Columns.Count)];
                    for (int i = 0; i < cells.Length; i++)
                        cells[i] = i < row.Cells.Count ? row.Cells[i] : string.Empty;

                    list.Items.Add(new ListViewItem(cells) { ForeColor = ColourFor(row.Tone), Tag = row.Id });
                }
            }
            finally
            {
                list.EndUpdate();
            }
        }

        private static Color ColourFor(OverlayTone tone) => tone switch
        {
            OverlayTone.Good => Color.DarkGreen,
            OverlayTone.Bad => Color.Firebrick,
            OverlayTone.Muted => Color.Gray,
            _ => SystemColors.WindowText,
        };

        private void RefreshStatus()
        {
            GameHost host = _host;
            if (host == null) return;

            ICharacterView character = host.Character;
            _character.Text = character.Id == 0
                ? "not logged in yet"
                : $"{character.Name} (0x{character.Id:X8}), level {character.Level}, {character.Skills.Count} skills, {character.Enchantments.Count} enchantments";

            _position.Text = character.Location.HasValue
                ? character.Location.Value.ToString() + "   " + (character.Motion?.ToString() ?? string.Empty)
                : "the client has not said yet";

            HostStatistics s = host.Statistics;
            _counters.Text = $"in {s.MessagesInbound}   out {s.MessagesOutbound}   applied {s.Applied}   ignored {s.Ignored}   malformed {s.Malformed}   objects {host.World.ObjectCount}";

            int rows = 0;
            foreach (ListView list in _pluginPanels.Values)
                rows += list.Items.Count;

            _lootCounts.Text = rows == 0 ? "nothing to show yet" : $"{rows} rows across {_pluginPanels.Count} panels";
        }

        // ---------------------------------------------------------------- plumbing

        private ListViewItem Add(ListView list, Color colour, params string[] cells)
        {
            ListViewItem row = new ListViewItem(cells) { ForeColor = colour };
            list.Items.Insert(0, row);

            // Newest first, and bounded: a long session must not grow without limit.
            while (list.Items.Count > MaxRows)
                list.Items.RemoveAt(list.Items.Count - 1);

            return row;
        }

        private static void Remove(ListView list, string tag)
        {
            for (int i = list.Items.Count - 1; i >= 0; i--)
            {
                if (Equals(list.Items[i].Tag, tag))
                    list.Items.RemoveAt(i);
            }
        }

        private static void Append(ListBox list, string line)
        {
            list.Items.Insert(0, line);
            while (list.Items.Count > MaxRows)
                list.Items.RemoveAt(list.Items.Count - 1);
        }

        internal void Write(string line) => OnUi(() => Append(_log, line));

        private void Report(Exception ex)
        {
            if (ex != null)
                Write("ERROR " + ex.Message);
        }

        private void SetStatus(string text) => OnUi(() => _status.Text = text);

        /// <summary>
        /// Host events arrive on the game thread; controls may only be touched on the
        /// UI thread. Everything visible goes through here.
        /// </summary>
        private void OnUi(Action action)
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            try
            {
                if (InvokeRequired)
                    BeginInvoke(action);
                else
                    action();
            }
            catch (ObjectDisposedException)
            {
                // The window closed while an event was in flight. Nothing to do.
            }
            catch (InvalidOperationException)
            {
                // Same, caught between the checks above and the call.
            }
        }

        private void ChooseProfile()
        {
            using OpenFileDialog dialog = new OpenFileDialog
            {
                Filter = "VirindiTank profiles (*.utl)|*.utl|All files (*.*)|*.*",
                FileName = Path.GetFileName(_profilePath),
                InitialDirectory = Path.GetDirectoryName(_profilePath),
            };

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _profilePath = dialog.FileName;
                SetStatus("Profile: " + _profilePath);
            }
        }

        private static string PluginDirectory()
            => Path.Combine(AppContext.BaseDirectory, "plugins");

        private static string DefaultProfilePath()
        {
            // The starter profile that ships beside the build, if it is there.
            string beside = Path.Combine(AppContext.BaseDirectory, "Starter.utl");
            if (File.Exists(beside))
                return beside;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ACHost", "plugins", "VirindiTank", "Default.utl");
        }

        private PortalData TryOpenPortalData()
        {
            try
            {
                string path = PortalData.FindPortalDat();
                if (string.IsNullOrEmpty(path))
                {
                    Write("No client_portal.dat found; spells will show as numbers.");
                    return null;
                }

                PortalData data = PortalData.Open(path);
                Write($"Client data: {data.Spells.Count} spells, {data.Skills.Count} skills from {path}");
                return data;
            }
            catch (Exception ex)
            {
                Write("Could not read the client's data files: " + ex.Message);
                return null;
            }
        }

        /// <summary>Sends the host's log to the Log tab.</summary>
        private sealed class UiLog : IPluginLog
        {
            private readonly MainForm _form;

            public UiLog(MainForm form) => _form = form;

            public void Info(string message) => _form.Write(message);

            public void Warn(string message) => _form.Write("WARN " + message);

            public void Error(string message, Exception exception = null)
                => _form.Write("ERROR " + message + (exception != null ? " - " + exception.Message : string.Empty));
        }
    }
}
