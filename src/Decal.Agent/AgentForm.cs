using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AC.Host;
using AC.Host.Plugins;
using AC.Host.Runtime;
using AC.Proxy;

namespace Decal.Agent
{
    /// <summary>
    /// Decal Agent's window: the plugin list with its switches, the buttons down the right, and
    /// three lines at the foot saying how things stand - laid out as Decal Agent 2.9.8.3's own
    /// dialog was, from the dialog template in DenAgent.exe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The host runs in this process, on its own game thread. Nothing here touches a plugin from
    /// the window's thread: every look at the plugin list and every switch thrown goes through
    /// <see cref="AgentHost.OnGameThread{T}"/>, and what comes back is copied into rows before the
    /// window shows it.
    /// </para>
    /// <para>
    /// Closing the window hides it; the host keeps running and the notification-area icon brings
    /// it back. Only Exit, on that icon's menu, stops the host.
    /// </para>
    /// </remarks>
    internal sealed class AgentForm : Form
    {
        // The pictures in DenAgent's plugin list, in the order of its strip.
        private const int ImagePlugins = 0;
        private const int ImageOff = 5;
        private const int ImageOn = 6;
        private const int ImageFailed = 7;

        private readonly AgentHost _host;
        private readonly FileLog _log;
        private readonly AgentCommandLine _commandLine;
        private readonly string _dataDirectory;
        private readonly ClientWatcher _watcher;
        private volatile AgentSettings _settings;

        private readonly Label _intro = new Label();
        private readonly ListView _list = new ListView();
        private readonly Button _update = new Button();
        private readonly Button _options = new Button();
        private readonly Button _add = new Button();
        private readonly Button _remove = new Button();
        private readonly Button _export = new Button();
        private readonly Button _refresh = new Button();
        private readonly Button _close = new Button();
        private readonly Label _serverCaption = new Label();
        private readonly Label _clientCaption = new Label();
        private readonly Label _actingCaption = new Label();
        private readonly Label _serverValue = new Label();
        private readonly Label _clientValue = new Label();
        private readonly Label _actingValue = new Label();
        private readonly ToolTip _tips = new ToolTip();
        private readonly ContextMenuStrip _addMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip _rowMenu = new ContextMenuStrip();

        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly ContextMenuStrip _trayMenu = new ContextMenuStrip();
        private readonly ToolStripMenuItem _trayAct;
        private readonly ToolStripMenuItem _trayInject;

        private readonly System.Windows.Forms.Timer _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        private System.Threading.Timer _watchTimer;
        private int _polling;

        private List<PluginListRow> _shownRows;
        private Snapshot _last;
        private bool _collapsed;
        private bool _toldAboutTray;
        private bool _exiting;
        private string _trayText;

        public AgentForm(AgentHost host, FileLog log, AgentSettings settings, AgentCommandLine commandLine, string dataDirectory)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _settings = settings ?? new AgentSettings();
            _commandLine = commandLine ?? new AgentCommandLine();
            _dataDirectory = dataDirectory;

            _watcher = new ClientWatcher(pid => OverlayInjection.Inject(pid, _settings.OverlayDll));
            _watcher.Attempted += OnInjectionAttempted;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = "Decal Agent " + Version;
            Icon = LoadIcon(SystemInformation.IconSize);
            ClientSize = Dlu(0, 0, 266, 214).Size;
            MinimumSize = SizeFromClientSize(ClientSize);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;

            BuildControls();
            BuildTray();
            _trayAct = (ToolStripMenuItem)_trayMenu.Items["act"];
            _trayInject = (ToolStripMenuItem)_trayMenu.Items["inject"];

            // Wider than DenAgent's to start with, for the status column its list did not have;
            // it still goes down to DenAgent's own size.
            ClientSize = Dlu(0, 0, 330, 214).Size;

            AcceptButton = _close;
            CancelButton = _close;
            ResumeLayout(false);
            PerformLayout();

            _refreshTimer.Tick += (_, _) => RequestRefresh();
        }

        private static string Version => typeof(AgentForm).Assembly.GetName().Version?.ToString() ?? "1.0.0.0";

        // ------------------------------------------------------------------- layout

        /// <summary>
        /// A rectangle in dialog units, as DenAgent's template gives every control, in pixels at
        /// 96 dots to the inch for MS Sans Serif 8 - six pixels to four units across, thirteen to
        /// eight down. The form scales the lot for the screen it is on.
        /// </summary>
        private static Rectangle Dlu(int x, int y, int width, int height)
            => Rectangle.FromLTRB(
                (int)Math.Round(x * 1.5),
                (int)Math.Round(y * 13 / 8.0),
                (int)Math.Round((x + width) * 1.5),
                (int)Math.Round((y + height) * 13 / 8.0));

        private void Place(Control control, Rectangle bounds, AnchorStyles anchor)
        {
            control.Bounds = bounds;
            control.Anchor = anchor;
            Controls.Add(control);
        }

        private void BuildControls()
        {
            const AnchorStyles TopRight = AnchorStyles.Top | AnchorStyles.Right;
            const AnchorStyles BottomRight = AnchorStyles.Bottom | AnchorStyles.Right;
            const AnchorStyles BottomLeft = AnchorStyles.Bottom | AnchorStyles.Left;

            _intro.Text = "Choose the Plugins you'd like to run.  Click Update to load the latest build of each one from its folder.";
            _intro.UseMnemonic = false;
            Place(_intro, Dlu(7, 7, 252, 17), AnchorStyles.Top | AnchorStyles.Left);

            _list.View = View.Details;
            _list.HeaderStyle = ColumnHeaderStyle.None;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.ShowItemToolTips = true;
            _list.SmallImageList = LoadListImages();
            _list.Columns.Add("Name");
            _list.Columns.Add("Version", 70, HorizontalAlignment.Right);
            _list.Columns.Add("Status", 130);
            _list.TabIndex = 0;
            Place(_list, Dlu(7, 30, 194, 140), AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);
            _list.ClientSizeChanged += (_, _) => SizeColumns();
            _list.MouseClick += OnListClick;
            _list.MouseDoubleClick += OnListDoubleClick;
            _list.KeyDown += OnListKey;
            _list.SelectedIndexChanged += (_, _) => UpdateButtons();
            _list.ContextMenuStrip = _rowMenu;

            SetUpButton(_update, "Update", Dlu(208, 30, 51, 14), TopRight, 1,
                "Decal's Update downloaded new Messages and MemLocs files from the web. There is nothing to download here, so Update reloads every running plugin from its folder instead - a new build, without logging out.");
            SetUpButton(_options, "Options", Dlu(208, 48, 51, 14), TopRight, 2,
                "The server, the ports, the client's data, the overlay and the movement keys.");
            SetUpButton(_add, "Add", Dlu(208, 65, 51, 14), TopRight, 3,
                "Install a plugin into the plugin folder and start it.");
            SetUpButton(_remove, "Remove", Dlu(208, 83, 51, 14), TopRight, 4,
                "Switch the chosen plugin off and delete it from the plugin folder. Its settings are kept.");
            SetUpButton(_export, "Export", Dlu(208, 128, 51, 16), TopRight, 5,
                "Save the list of plugins to a text file.");
            SetUpButton(_refresh, "Refresh List", Dlu(208, 176, 51, 14), BottomRight, 6,
                "Look in the plugin folder for plugins installed since the host started, and start them.");
            SetUpButton(_close, "Close", Dlu(208, 193, 51, 14), BottomRight, 7,
                "Hide this window. Decal keeps running in the notification area.");

            _update.Click += async (_, _) => await ReloadAllAsync();
            _options.Click += async (_, _) => await ShowOptionsAsync();
            _add.Click += (_, _) => _addMenu.Show(_add, new Point(0, _add.Height));
            _remove.Click += async (_, _) => await RemoveSelectedAsync();
            _export.Click += (_, _) => Export();
            _refresh.Click += async (_, _) => await RescanAsync();
            _close.Click += (_, _) => HideToTray();

            _addMenu.Items.Add("A plugin's assembly (.dll)...", null, async (_, _) => await AddAssemblyAsync());
            _addMenu.Items.Add("A plugin's folder, with everything in it...", null, async (_, _) => await AddFolderAsync());

            _rowMenu.Opening += (_, e) => e.Cancel = !BuildRowMenu();

            SetUpStatus(_serverCaption, "Server:", Dlu(7, 175, 35, 8), _serverValue, Dlu(74, 175, 127, 8));
            SetUpStatus(_clientCaption, "Client:", Dlu(7, 187, 60, 8), _clientValue, Dlu(74, 187, 127, 8));
            SetUpStatus(_actingCaption, "Acting:", Dlu(7, 199, 59, 8), _actingValue, Dlu(74, 199, 127, 8));
            _serverValue.Text = "Starting...";

            void SetUpButton(Button button, string text, Rectangle bounds, AnchorStyles anchor, int tab, string tip)
            {
                button.Text = text;
                button.TabIndex = tab;
                button.UseVisualStyleBackColor = true;
                Place(button, bounds, anchor);
                _tips.SetToolTip(button, tip);
            }

            void SetUpStatus(Label caption, string text, Rectangle captionBounds, Label value, Rectangle valueBounds)
            {
                caption.Text = text;
                caption.UseMnemonic = false;
                Place(caption, captionBounds, BottomLeft);

                value.UseMnemonic = false;
                value.AutoEllipsis = true;
                Place(value, valueBounds, BottomLeft | AnchorStyles.Right);
            }
        }

        private void SizeColumns()
        {
            int version = (int)Math.Round(70 * DeviceDpi / 96.0);
            int status = (int)Math.Round(130 * DeviceDpi / 96.0);
            _list.Columns[1].Width = version;
            _list.Columns[2].Width = status;
            _list.Columns[0].Width = Math.Max(40, _list.ClientSize.Width - version - status);
        }

        private static ImageList LoadListImages()
        {
            ImageList images = new ImageList
            {
                ImageSize = new Size(16, 16),
                ColorDepth = ColorDepth.Depth24Bit,
                TransparentColor = Color.Magenta,
            };

            using Stream stream = typeof(AgentForm).Assembly.GetManifestResourceStream("Decal.Agent.PluginList.bmp");
            if (stream != null)
            {
                // Copied off the stream, and kept: a bitmap read from a stream needs the stream for
                // as long as it lives, and the image list reads the strip only when the list's
                // window is made - the first time the window is shown, which may be much later.
                using Bitmap loaded = new Bitmap(stream);
                images.Images.AddStrip(new Bitmap(loaded));
            }

            return images;
        }

        /// <summary>Decal's icon, at the size asked for, from the one taken out of DenAgent.exe.</summary>
        internal static Icon LoadIcon(Size size)
        {
            using Stream stream = typeof(AgentForm).Assembly.GetManifestResourceStream("Decal.Agent.Decal.ico");
            return stream == null ? SystemIcons.Application : new Icon(stream, size);
        }

        // ------------------------------------------------------------------- the notification area

        private void BuildTray()
        {
            ToolStripMenuItem show = new ToolStripMenuItem("Show Decal Agent", null, (_, _) => ShowWindow());
            show.Font = new Font(show.Font, FontStyle.Bold);
            _trayMenu.Items.Add(show);
            _trayMenu.Items.Add(new ToolStripMenuItem("Let plugins act", null, async (_, _) => await ToggleActingAsync()) { Name = "act" });
            _trayMenu.Items.Add(new ToolStripMenuItem("Put the overlay into AC:Unreal", null, async (_, _) => await InjectNowAsync()) { Name = "inject" });
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(new ToolStripMenuItem("Open the log", null, (_, _) => OpenLog()));
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(new ToolStripMenuItem("Exit", null, async (_, _) => await ExitAsync()));
            _trayMenu.Opening += (_, _) => UpdateTrayMenu();

            _tray.Icon = LoadIcon(SystemInformation.SmallIconSize);
            _tray.Text = "Decal Agent";
            _tray.ContextMenuStrip = _trayMenu;
            _tray.MouseDoubleClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    ShowWindow();
            };
        }

        private void UpdateTrayMenu()
        {
            Snapshot last = _last;
            _trayAct.Enabled = last != null && last.CanAct;
            _trayAct.Checked = last != null && last.Acting;
            _trayInject.Enabled = _watcher.Client.HasValue;
        }

        public void ShowWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
        }

        private void HideToTray()
        {
            Hide();
            if (!_toldAboutTray)
            {
                _toldAboutTray = true;
                _tray.ShowBalloonTip(4000, "Decal Agent", "Decal is still running. Double-click its icon to open this window again; right-click it to exit.", ToolTipIcon.Info);
            }
        }

        // ------------------------------------------------------------------- starting and stopping

        /// <summary>
        /// Starts everything: the icon, the host and the watch for the client. Called once, before
        /// the window is first shown or while it is hidden in the notification area.
        /// </summary>
        public async Task BeginAsync()
        {
            // A handle now, so the host's threads can reach the window even if it is never shown.
            _ = Handle;
            _tray.Visible = true;
            _refreshTimer.Start();
            _watchTimer = new System.Threading.Timer(_ => PollClient(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
            await StartHostAsync();
        }

        private HostRuntimeOptions RuntimeOptions()
        {
            AgentSettings settings = _settings;
            HostRuntimeOptions options = new HostRuntimeOptions
            {
                Proxy = new ProxyOptions
                {
                    ServerHost = _commandLine.ServerHost ?? settings.ServerHost,
                    ServerPort = _commandLine.ServerPort ?? settings.ServerPort,
                    ListenPort = _commandLine.ListenPort ?? settings.ListenPort,
                },
                PluginDirectory = _commandLine.PluginDirectory ?? HostRuntimeOptions.DefaultPluginDirectory,
                DataDirectory = _commandLine.DataDirectory,
                EnableActions = settings.ActAtStart,
                Overlay = true,
                DatPath = string.IsNullOrWhiteSpace(settings.DatFolder) ? null : settings.DatFolder,
                NoDat = _commandLine.NoDat,
            };

            if (_commandLine.ControlPipe != null)
                options.ControlPipeName = _commandLine.ControlPipe;
            if (_commandLine.OverlayPipe != null)
                options.OverlayPipeName = _commandLine.OverlayPipe;
            foreach (KeyValuePair<string, string> setting in _commandLine.Settings)
                options.Settings[setting.Key] = setting.Value;

            return options;
        }

        private async Task StartHostAsync()
        {
            _serverValue.Text = "Starting...";
            UpdateButtons();

            if (await _host.StartAsync(RuntimeOptions()))
            {
                HostRuntime runtime = _host.Runtime;
                if (runtime?.Plugins != null)
                    runtime.Plugins.Changed += OnPluginsChanged;

                // `achost ctl exit`: as Exit on the tray icon's menu.
                if (runtime?.Control != null)
                    runtime.Control.ExitRequested += (_, _) => BeginInvoke(new Action(async () => await ExitAsync()));
            }
            else
            {
                MessageBox.Show(this,
                    "The host could not start:\n\n" + _host.Problem + "\n\nIf another host - achost, or a second Decal Agent - is using the same ports, stop it, or choose other ports in Options.",
                    "Decal Agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            _shownRows = null;
            RequestRefresh();
        }

        private async Task RestartHostAsync()
        {
            HostRuntime old = _host.Runtime;
            if (old?.Plugins != null)
                old.Plugins.Changed -= OnPluginsChanged;

            _serverValue.Text = "Restarting...";
            await _host.StopAsync();
            await StartHostAsync();
        }

        private async Task ExitAsync()
        {
            if (_exiting)
                return;

            _exiting = true;
            _refreshTimer.Stop();
            _watchTimer?.Dispose();
            Hide();
            _tray.Visible = false;

            await _host.StopAsync();

            _tray.Dispose();
            Close();
            Application.ExitThread();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing)
            {
                // The title bar's close is Close: the window goes, Decal stays.
                e.Cancel = true;
                HideToTray();
                return;
            }

            if (!_exiting)
            {
                // Windows is shutting down, or something asked the program to end: stop the host
                // properly - it saves every plugin's settings on the way - without waiting forever.
                _exiting = true;
                _refreshTimer.Stop();
                _watchTimer?.Dispose();
                _tray.Visible = false;
                Task.Run(() => _host.StopAsync()).Wait(TimeSpan.FromSeconds(15));
                _tray.Dispose();
            }

            base.OnFormClosing(e);
        }

        // ------------------------------------------------------------------- the client

        private void PollClient()
        {
            // Injecting can take seconds; one look at a time.
            if (Interlocked.Exchange(ref _polling, 1) == 1)
                return;

            try
            {
                // Only while this host serves the overlay: put in without it, the overlay would
                // find nobody - or someone else's host - at the other end of its pipe.
                _watcher.AutoInject = _settings.AutoInject && !_commandLine.NoInject && _host.Runtime?.Overlay != null;
                _watcher.Poll(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _log.Error("Decal Agent: looking for AC:Unreal failed.", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
            }
        }

        private void OnInjectionAttempted(object sender, ClientInjection outcome)
        {
            if (outcome.Success)
                _log.Info("Decal Agent: " + outcome.Message);
            else
                _log.Warn("Decal Agent: the overlay was not put into AC:Unreal. " + outcome.Message);

            OnWindowThread(RequestRefresh);
        }

        private async Task InjectNowAsync()
        {
            ClientInjection outcome = await Task.Run(() => _watcher.InjectNow());
            if (!outcome.Success)
                MessageBox.Show(this, outcome.Message, "Decal Agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
                _tray.ShowBalloonTip(3000, "Decal Agent", outcome.Message, ToolTipIcon.Info);
        }

        // ------------------------------------------------------------------- what the window shows

        /// <summary>
        /// DenAgent's box for a row: ticked while it runs, empty while it is off, a red cross when
        /// it was wanted and could not run. A plugin the host replaces shows Decal's own tick.
        /// </summary>
        private static int ImageOf(PluginListRow row) => row.State switch
        {
            HostedPluginState.Running => ImageOn,
            HostedPluginState.Off => ImageOff,
            HostedPluginState.Replaced => row.Enabled ? ImageOn : ImageOff,
            HostedPluginState.CannotRun => row.Enabled ? ImageFailed : ImageOff,
            _ => ImageFailed,
        };

        /// <summary>Whether a row has anything to reload: not a plugin the host replaces, nor one that cannot run here.</summary>
        private static bool CanReload(PluginListRow row) => row.State != HostedPluginState.Replaced && row.State != HostedPluginState.CannotRun;

        /// <summary>Everything the window shows, read on the game thread in one go.</summary>
        private sealed class Snapshot
        {
            public List<PluginListRow> Rows { get; } = new List<PluginListRow>();

            public string Server { get; set; }

            public string Character { get; set; }

            public bool CanAct { get; set; }

            public bool Acting { get; set; }

            public bool OverlayAttached { get; set; }

            public string PluginFolder { get; set; }

            public string Listening { get; set; }
        }

        /// <summary>Game thread only.</summary>
        private static Snapshot Take(HostRuntime runtime)
        {
            GameHost host = runtime.Host;
            Snapshot snapshot = new Snapshot
            {
                Server = host.World.ServerName,
                Character = host.Character.Id == 0 ? null : host.Character.Name,
                CanAct = host.CanAct,
                Acting = host.CanAct && host.ActionsAllowed,
                OverlayAttached = runtime.OverlayAttached,
                PluginFolder = runtime.Plugins?.Directory,
                Listening = runtime.Options.Proxy.ListenAddress + ":" + runtime.Options.Proxy.ListenPort.ToString(CultureInfo.InvariantCulture),
            };

            if (runtime.Plugins == null)
                return snapshot;

            // Each plugin, and beneath DecalCompat the plugins Decal's registry lists and its own
            // folder holds - the rows the Decal window in the game shows, in the same order.
            snapshot.Rows.AddRange(PluginList.Build(runtime.Plugins.Entries, null));
            return snapshot;
        }

        private async void RequestRefresh()
        {
            Snapshot snapshot = null;
            try
            {
                snapshot = await _host.OnGameThread(Take);
            }
            catch (Exception ex) when (ex is TimeoutException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                // The host went while it was being asked; the next tick asks again.
            }

            if (IsDisposed || _exiting)
                return;

            _last = snapshot;
            ShowRows(snapshot?.Rows ?? new List<PluginListRow>());
            ShowStatus(snapshot);
        }

        private void OnPluginsChanged(object sender, EventArgs e) => OnWindowThread(RequestRefresh);

        /// <summary>Runs something on the window's thread, from any thread, if the window is still there.</summary>
        private void OnWindowThread(Action action)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // Closing.
            }
        }

        private void ShowRows(List<PluginListRow> rows)
        {
            if (_shownRows != null && rows.SequenceEqual(_shownRows))
                return;

            string selected = SelectedRow?.Key;
            _shownRows = rows;

            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();

                ListViewItem group = new ListViewItem("Plugins", ImagePlugins) { ToolTipText = _collapsed ? "Double-click to show the plugins." : "Double-click to hide the plugins." };
                group.SubItems.Add(string.Empty);
                group.SubItems.Add(string.Empty);
                _list.Items.Add(group);

                if (!_collapsed)
                {
                    foreach (PluginListRow row in rows)
                    {
                        ListViewItem item = new ListViewItem(row.Name, ImageOf(row))
                        {
                            IndentCount = row.Hosted ? 2 : 1,
                            Tag = row,
                            ToolTipText = Describe(row),
                        };
                        item.SubItems.Add(row.Version);
                        item.SubItems.Add(row.Status);
                        _list.Items.Add(item);

                        if (row.Key == selected)
                            item.Selected = true;
                    }
                }
            }
            finally
            {
                _list.EndUpdate();
            }

            SizeColumns();
            UpdateButtons();
        }

        /// <summary>The row's whole reason, for its tooltip: why it runs, or why not, and whose it is.</summary>
        private static string Describe(PluginListRow row)
            => row.Hosted
                ? $"{row.Name}: {row.Status}.{Environment.NewLine}{Wrap(row.Detail)}{Environment.NewLine}A Decal plugin, run by {row.HostName}."
                : $"{row.Name}: {row.Status}.{Environment.NewLine}{Wrap(row.Detail)}";

        /// <summary>Long reasons broken into lines a tooltip can show.</summary>
        private static string Wrap(string text, int width = 90)
        {
            StringBuilder wrapped = new StringBuilder();
            int line = 0;
            foreach (string word in (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line > 0 && line + 1 + word.Length > width)
                {
                    wrapped.Append(Environment.NewLine);
                    line = 0;
                }
                else if (line > 0)
                {
                    wrapped.Append(' ');
                    line++;
                }

                wrapped.Append(word);
                line += word.Length;
            }

            return wrapped.ToString();
        }

        private void ShowStatus(Snapshot snapshot)
        {
            // The server, and who is playing on it.
            string server;
            if (snapshot == null)
                server = _host.Starting ? "Starting..." : "Not running: " + (_host.Problem ?? "stopped");
            else if (string.IsNullOrEmpty(snapshot.Server))
                server = "Waiting for AC:Unreal on " + snapshot.Listening;
            else
                server = snapshot.Character == null ? snapshot.Server : snapshot.Server + ", " + snapshot.Character;

            // The client, and whether the overlay is in it.
            string client;
            ClientWindow? found = _watcher.Client;
            ClientInjection outcome = _watcher.Outcome;
            string clientTip;
            if (!found.HasValue)
            {
                client = "AC:Unreal is not running";
                clientTip = "Decal puts its overlay into AC:Unreal when it starts." + (AutoInjecting ? string.Empty : " (Turned off: use the icon's menu to do it by hand.)");
            }
            else if (snapshot != null && snapshot.OverlayAttached)
            {
                client = "AC:Unreal, overlay attached";
                clientTip = found.Value.Title + " (process " + found.Value.ProcessId.ToString(CultureInfo.InvariantCulture) + ")";
            }
            else if (outcome != null && !outcome.Success && outcome.Message.EndsWith("...", StringComparison.Ordinal))
            {
                client = "AC:Unreal, putting the overlay in...";
                clientTip = outcome.Message;
            }
            else
            {
                client = outcome != null && !outcome.Success ? "AC:Unreal, overlay not attached (see why)" : "AC:Unreal, overlay not attached";
                clientTip = outcome?.Message ?? (AutoInjecting ? "The overlay goes in a few seconds after the game's window appears." : "Use the icon's menu to put the overlay in.");
            }

            // Whether plugins may act.
            string acting = snapshot == null ? string.Empty
                : !snapshot.CanAct ? "Plugins only watch (a replay cannot act)"
                : snapshot.Acting ? "Plugins may act" : "Plugins only watch";

            SetStatus(_serverValue, server, server);
            SetStatus(_clientValue, client, clientTip);
            SetStatus(_actingValue, acting, "Switch it from the icon's menu, or from Decal's window in the game.");

            string trayText = "Decal Agent - " + server;
            if (trayText.Length > 63)
                trayText = trayText.Substring(0, 60) + "...";
            if (trayText != _trayText)
            {
                _trayText = trayText;
                _tray.Text = trayText;
            }

            UpdateButtons();
        }

        private bool AutoInjecting => _settings.AutoInject && !_commandLine.NoInject;

        private void SetStatus(Label label, string text, string tip)
        {
            if (label.Text != text)
                label.Text = text;
            if (_tips.GetToolTip(label) != tip)
                _tips.SetToolTip(label, tip);
        }

        private PluginListRow SelectedRow => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as PluginListRow : null;

        private void UpdateButtons()
        {
            HostRuntime runtime = _host.Runtime;
            bool running = runtime != null;
            _update.Enabled = running;
            _add.Enabled = running && runtime.Plugins != null;
            _refresh.Enabled = running;
            _remove.Enabled = running && SelectedRow is PluginListRow row && !row.Hosted;
        }

        // ------------------------------------------------------------------- the list

        private async void OnListClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;

            ListViewHitTestInfo hit = _list.HitTest(e.Location);
            if (hit.Item?.Tag is PluginListRow row && hit.Location == ListViewHitTestLocations.Image)
                await ToggleAsync(row);
        }

        private void OnListDoubleClick(object sender, MouseEventArgs e)
        {
            ListViewHitTestInfo hit = _list.HitTest(e.Location);
            if (hit.Item != null && hit.Item.Tag == null)
            {
                _collapsed = !_collapsed;
                _shownRows = null;
                RequestRefresh();
            }
        }

        private async void OnListKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space && SelectedRow is PluginListRow row)
            {
                e.Handled = true;
                await ToggleAsync(row);
            }
            else if (e.KeyCode == Keys.Delete && SelectedRow is PluginListRow chosen && !chosen.Hosted)
            {
                e.Handled = true;
                await RemoveSelectedAsync();
            }
        }

        /// <summary>
        /// Throws a plugin's switch, as a click on its box does - in this host only: a Decal
        /// plugin's choice is kept in DecalCompat's settings, and Decal's own list is left as it is.
        /// </summary>
        private async Task ToggleAsync(PluginListRow row)
        {
            if (!row.CanSwitch)
            {
                Say($"{row.Name} has no switch here.{Environment.NewLine}{Environment.NewLine}{row.Detail}", MessageBoxIcon.Information);
                return;
            }

            await Guard(() => _host.OnGameThread(runtime =>
            {
                if (!row.Hosted)
                {
                    PluginEntry entry = FindEntry(runtime, row);
                    if (entry != null)
                        runtime.Plugins.SetEnabled(entry, !entry.Enabled);
                }
                else if (runtime.Plugins?.Find(row.HostName)?.Plugin is IHostedPlugins hosting)
                {
                    HostedPluginInfo hosted = hosting.HostedPlugins.FirstOrDefault(p => p.Name == row.Name);
                    if (hosted != null)
                        hosting.SetHostedEnabled(hosted.Name, !hosted.Enabled);
                }

                return true;
            }));

            RequestRefresh();
        }

        /// <summary>Game thread only.</summary>
        private static PluginEntry FindEntry(HostRuntime runtime, PluginListRow row)
            => runtime.Plugins?.Entries.FirstOrDefault(e => string.Equals(e.AssemblyPath, row.AssemblyPath, StringComparison.OrdinalIgnoreCase));

        private bool BuildRowMenu()
        {
            _rowMenu.Items.Clear();
            if (SelectedRow is not PluginListRow row || _host.Runtime == null)
                return false;

            if (row.CanSwitch)
                _rowMenu.Items.Add(row.Enabled ? "Switch off" : "Switch on", null, async (_, _) => await ToggleAsync(row));
            if (CanReload(row))
                _rowMenu.Items.Add("Reload", null, async (_, _) => await ReloadAsync(row));
            _rowMenu.Items.Add("Why?", null, (_, _) => Say($"{row.Name}: {row.Status}.{Environment.NewLine}{Environment.NewLine}{row.Detail}", MessageBoxIcon.Information));
            if (!row.Hosted)
            {
                _rowMenu.Items.Add("Open its folder", null, (_, _) => OpenFolder(row.AssemblyPath));
                _rowMenu.Items.Add(new ToolStripSeparator());
                _rowMenu.Items.Add("Remove...", null, async (_, _) => await RemoveSelectedAsync());
            }

            return true;
        }

        private async Task ReloadAsync(PluginListRow row)
        {
            if (!CanReload(row))
                return;

            await Guard(() => _host.OnGameThread(runtime =>
            {
                if (!row.Hosted)
                {
                    PluginEntry entry = FindEntry(runtime, row);
                    if (entry != null)
                        runtime.Plugins.Reload(entry);
                }
                else if (runtime.Plugins?.Find(row.HostName)?.Plugin is IHostedPlugins hosting)
                {
                    hosting.ReloadHosted(row.Name);
                }

                return true;
            }));

            RequestRefresh();
        }

        // ------------------------------------------------------------------- the buttons

        private async Task ReloadAllAsync()
        {
            int reloaded = await Guard(() => _host.OnGameThread(runtime =>
            {
                List<PluginEntry> running = runtime.Plugins?.Entries.Where(e => e.Enabled).ToList() ?? new List<PluginEntry>();
                foreach (PluginEntry entry in running)
                    runtime.Plugins.Reload(entry);
                return running.Count;
            }));

            _log.Info($"Decal Agent: Update reloaded {reloaded} plugin(s).");
            RequestRefresh();
        }

        private async Task RescanAsync()
        {
            int found = await Guard(() => _host.OnGameThread(runtime =>
            {
                if (runtime.Plugins == null)
                    return 0;

                int before = runtime.Plugins.Entries.Count;
                runtime.Plugins.Rescan();

                // DenAgent's Refresh List read Decal's registry again; DecalCompat does.
                foreach (IHostedPlugins hosting in runtime.Plugins.Entries.Select(e => e.Plugin).OfType<IHostedPlugins>())
                    hosting.RescanHosted();

                return runtime.Plugins.Entries.Count - before;
            }));

            _log.Info(found > 0 ? $"Decal Agent found {found} new plugin(s)." : "Decal Agent found no new plugins.");
            _shownRows = null;
            RequestRefresh();
        }

        private async Task AddAssemblyAsync()
        {
            using OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "Add a plugin",
                Filter = "Plugin assemblies (*.dll)|*.dll|All files (*.*)|*.*",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog(this) == DialogResult.OK)
                await InstallAsync(dialog.FileName);
        }

        private async Task AddFolderAsync()
        {
            using FolderBrowserDialog dialog = new FolderBrowserDialog
            {
                Description = "Choose the folder the plugin was built or unpacked into. All of it is copied.",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
            };

            if (dialog.ShowDialog(this) == DialogResult.OK)
                await InstallAsync(dialog.SelectedPath);
        }

        private async Task InstallAsync(string path)
        {
            try
            {
                (string Name, string Status, bool Enabled, bool Found) result = await _host.OnGameThread(runtime =>
                {
                    PluginEntry entry = runtime.Plugins.Install(path);
                    return entry == null ? (null, null, false, false) : (entry.Name, entry.Status, entry.Enabled, true);
                });

                if (!result.Found)
                {
                    Say($"{Path.GetFileName(path)} was copied into the plugin folder, but would not load. The log says why.", MessageBoxIcon.Warning);
                }
                else if (result.Enabled && result.Status != "running")
                {
                    Say($"{result.Name} was installed, but did not start: {result.Status}.", MessageBoxIcon.Warning);
                }
                else
                {
                    _log.Info($"Decal Agent: {result.Name} added.");
                }
            }
            catch (InvalidOperationException ex)
            {
                if (File.Exists(path) && IsDecalPlugin(path))
                {
                    string folder = await DecalPluginFolderAsync();
                    Say($"{Path.GetFileName(path)} is a Decal plugin rather than one of this host's.{Environment.NewLine}{Environment.NewLine}"
                        + "Decal plugins are run by DecalCompat: every one registered with Decal, from where Decal installed it, and any other from "
                        + (folder ?? "the \"Decal Plugins\" folder in its data folder")
                        + ". Register it with Decal, or put it there in a folder of its own, then press Refresh List so DecalCompat looks again.", MessageBoxIcon.Information);
                }
                else
                {
                    Say(ex.Message, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Say("The plugin could not be copied into the plugin folder: " + ex.Message, MessageBoxIcon.Warning);
            }
            catch (TimeoutException)
            {
                Say("The host did not answer. Is it running?", MessageBoxIcon.Warning);
            }

            _shownRows = null;
            RequestRefresh();
        }

        /// <summary>Whether an assembly is built against Decal.Adapter: a Decal plugin, for DecalCompat.</summary>
        private static bool IsDecalPlugin(string path)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                using PEReader pe = new PEReader(stream);
                if (!pe.HasMetadata)
                    return false;

                MetadataReader reader = pe.GetMetadataReader();
                return reader.AssemblyReferences.Any(handle => reader.GetString(reader.GetAssemblyReference(handle).Name) == "Decal.Adapter");
            }
            catch (Exception ex) when (ex is IOException || ex is BadImageFormatException || ex is InvalidOperationException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Where the running Decal compatibility plugin looks for Decal plugins, if one is running.</summary>
        private async Task<string> DecalPluginFolderAsync()
        {
            try
            {
                return await _host.OnGameThread(runtime => runtime.Plugins?.Entries
                    .Select(e => e.Plugin)
                    .OfType<IHostedPlugins>()
                    .Select(p => p.GetType().GetProperty("Folder")?.GetValue(p) as string)
                    .FirstOrDefault(folder => folder != null));
            }
            catch (Exception ex) when (ex is TimeoutException || ex is System.Reflection.TargetInvocationException)
            {
                return null;
            }
        }

        private async Task RemoveSelectedAsync()
        {
            if (SelectedRow is not PluginListRow row || row.Hosted)
                return;

            string folder = Path.GetDirectoryName(row.AssemblyPath);
            DialogResult answer = MessageBox.Show(this,
                $"Remove {row.Name}?{Environment.NewLine}{Environment.NewLine}It will be switched off and deleted from {folder}. Its settings are kept, so adding it again finds them as they were.",
                "Decal Agent", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
                return;

            try
            {
                await _host.OnGameThread(runtime =>
                {
                    PluginEntry entry = FindEntry(runtime, row);
                    if (entry != null)
                        runtime.Plugins.Uninstall(entry);
                    return true;
                });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Say($"{row.Name} is switched off, but some of its files could not be deleted: {ex.Message}", MessageBoxIcon.Warning);
            }
            catch (TimeoutException)
            {
                Say("The host did not answer. Is it running?", MessageBoxIcon.Warning);
            }

            _shownRows = null;
            RequestRefresh();
        }

        private void Export()
        {
            using SaveFileDialog dialog = new SaveFileDialog
            {
                Title = "Export the plugin list",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = "Decal Plugins.txt",
                OverwritePrompt = true,
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                File.WriteAllText(dialog.FileName, ExportText(_last), new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Say("The list could not be saved: " + ex.Message, MessageBoxIcon.Warning);
            }
        }

        private static string ExportText(Snapshot snapshot)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Decal Agent " + Version + " - plugins, " + DateTime.Now.ToString("d MMMM yyyy HH:mm", CultureInfo.InvariantCulture));
            if (snapshot?.PluginFolder != null)
                text.AppendLine("Plugin folder: " + snapshot.PluginFolder);
            if (!string.IsNullOrEmpty(snapshot?.Server))
                text.AppendLine("Server: " + snapshot.Server + (snapshot.Character != null ? ", " + snapshot.Character : string.Empty));
            text.AppendLine();
            text.AppendLine("Plugins");

            foreach (PluginListRow row in snapshot?.Rows ?? new List<PluginListRow>())
            {
                string box = ImageOf(row) switch
                {
                    ImageOn => "[x]",
                    ImageOff => "[ ]",
                    _ => "[!]",
                };
                string indent = row.Hosted ? "      " : "  ";
                text.Append(indent).Append(box).Append(' ')
                    .Append(row.Name.PadRight(row.Hosted ? 26 : 30)).Append(' ')
                    .Append((row.Version ?? string.Empty).PadRight(14)).Append(' ')
                    .Append(row.Status.PadRight(22));
                if (!row.Hosted)
                    text.Append(' ').Append(row.AssemblyPath);
                else
                    text.Append(" run by ").Append(row.HostName);
                text.AppendLine();

                // Why, for anything not simply running or off: what a player sending the list
                // to someone for help would be asked next.
                if (row.State != HostedPluginState.Running || row.Status != "running")
                {
                    if (row.State != HostedPluginState.Off)
                        text.Append(indent).Append("      ").AppendLine(row.Detail);
                }
            }

            return text.ToString();
        }

        // ------------------------------------------------------------------- options

        private async Task ShowOptionsAsync()
        {
            HostRuntime runtime = _host.Runtime;
            Dictionary<GameKey, string> keys = null;
            if (runtime?.Decal != null)
            {
                try
                {
                    keys = await _host.OnGameThread(r => AC.Host.Decal.DecalAgent.MovementKeys
                        .ToDictionary(key => key, key => GameInput.Describe(r.Host.InputKeys.VirtualKey(key))));
                }
                catch (TimeoutException)
                {
                    keys = null;
                }
            }

            AgentSettings before = _settings;
            using OptionsForm options = new OptionsForm(before.Copy(), keys, OverlayInjection.Resolve(null), _commandLine);
            if (options.ShowDialog(this) != DialogResult.OK)
                return;

            AgentSettings after = options.Settings;
            _settings = after;
            after.Save(_dataDirectory, _log.Warn);

            foreach (KeyValuePair<GameKey, string> changed in options.ChangedKeys)
            {
                try
                {
                    await _host.OnGameThread(r => r.Decal?.BindKey(changed.Key, changed.Value) ?? false);
                }
                catch (TimeoutException)
                {
                    // Said below: a host that does not answer did not take the key either.
                }
            }

            bool needsRestart = !string.Equals(before.ServerHost, after.ServerHost, StringComparison.OrdinalIgnoreCase)
                || before.ServerPort != after.ServerPort
                || before.ListenPort != after.ListenPort
                || !string.Equals(before.DatFolder ?? string.Empty, after.DatFolder ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            if (needsRestart)
            {
                DialogResult answer = MessageBox.Show(this,
                    "The server, the ports and the client's data are read when the host starts. Restart it now to use the new ones?"
                    + Environment.NewLine + Environment.NewLine + "AC:Unreal will lose its connection and have to log in again. Otherwise they are used next time Decal Agent starts.",
                    "Decal Agent", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

                if (answer == DialogResult.Yes)
                    await RestartHostAsync();
            }

            RequestRefresh();
        }

        // ------------------------------------------------------------------- the rest

        private async Task ToggleActingAsync()
        {
            await Guard(() => _host.OnGameThread(runtime =>
            {
                if (runtime.Host.CanAct)
                    runtime.Host.ActionsAllowed = !runtime.Host.ActionsAllowed;
                return true;
            }));

            RequestRefresh();
        }

        private void OpenLog()
        {
            string path = _log.CurrentPath;
            try
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo(_log.Folder) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                Say("The log could not be opened: " + ex.Message + Environment.NewLine + path, MessageBoxIcon.Warning);
            }
        }

        private void OpenFolder(string assemblyPath)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + assemblyPath + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                Say("The folder could not be opened: " + ex.Message, MessageBoxIcon.Warning);
            }
        }

        /// <summary>Runs a request of the host, and says so rather than throwing if it went wrong.</summary>
        private async Task<T> Guard<T>(Func<Task<T>> request)
        {
            try
            {
                return await request();
            }
            catch (TimeoutException)
            {
                Say("The host did not answer. Is it running?", MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                _log.Error("Decal Agent: a request of the host failed.", ex);
                Say(ex.Message, MessageBoxIcon.Warning);
            }

            return default;
        }

        private void Say(string message, MessageBoxIcon icon)
            => MessageBox.Show(Visible ? this : null, message, "Decal Agent", MessageBoxButtons.OK, icon);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _refreshTimer.Dispose();
                _watchTimer?.Dispose();
                _tray.Dispose();
                _tips.Dispose();
                _addMenu.Dispose();
                _rowMenu.Dispose();
                _trayMenu.Dispose();
                _list.SmallImageList?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
