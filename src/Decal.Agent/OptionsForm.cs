using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Windows.Forms;
using AC.Host.Plugins;

namespace Decal.Agent
{
    /// <summary>
    /// Decal Options, in the manner of DenAgent's own dialog - framed groups, OK and Cancel at the
    /// foot on the right - holding what this Decal needs set rather than what that one did.
    /// </summary>
    internal sealed class OptionsForm : Form
    {
        private readonly TextBox _server = new TextBox();
        private readonly NumericUpDown _serverPort = new NumericUpDown { Minimum = 1, Maximum = 65534 };
        private readonly NumericUpDown _listenPort = new NumericUpDown { Minimum = 1, Maximum = 65534 };
        private readonly Label _pointAt = new Label();
        private readonly TextBox _dat = new TextBox();
        private readonly TextBox _overlay = new TextBox();
        private readonly CheckBox _autoInject = new CheckBox();
        private readonly CheckBox _actAtStart = new CheckBox();
        private readonly Dictionary<GameKey, TextBox> _keys = new Dictionary<GameKey, TextBox>();
        private readonly IReadOnlyDictionary<GameKey, string> _keysBefore;
        private readonly ToolTip _tips = new ToolTip();

        /// <param name="settings">A copy of the settings, which the dialog changes on OK.</param>
        /// <param name="keys">The movement keys as bound now, or null when no host is running to ask.</param>
        /// <param name="foundOverlay">The overlay DLL that would be used with the box left empty.</param>
        public OptionsForm(AgentSettings settings, IReadOnlyDictionary<GameKey, string> keys, string foundOverlay, AgentCommandLine commandLine)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _keysBefore = keys;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Decal Options";
            Icon = AgentForm.LoadIcon(SystemInformation.SmallIconSize);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = Dlu(0, 0, 271, 236).Size;

            // Game server
            GroupBox network = Group("Game Server", Dlu(7, 7, 257, 58));
            Add(network, new Label { Text = "Server", TextAlign = ContentAlignment.MiddleLeft }, Dlu(7, 12, 40, 12));
            Add(network, _server, Dlu(50, 11, 96, 14));
            Add(network, new Label { Text = "Port", TextAlign = ContentAlignment.MiddleRight }, Dlu(150, 12, 40, 12));
            Add(network, _serverPort, Dlu(194, 11, 55, 14));
            Add(network, new Label { Text = "Listen on port", TextAlign = ContentAlignment.MiddleLeft }, Dlu(7, 29, 60, 12));
            Add(network, _listenPort, Dlu(70, 28, 55, 14));
            Add(network, _pointAt, Dlu(7, 44, 242, 10));
            _pointAt.ForeColor = SystemColors.GrayText;
            _listenPort.ValueChanged += (_, _) => ShowPointAt();

            // Client
            GroupBox client = Group("AC:Unreal", Dlu(7, 69, 257, 76));
            Add(client, new Label { Text = "Folder holding client_portal.dat (empty: found by itself)" }, Dlu(7, 11, 242, 9));
            Add(client, _dat, Dlu(7, 21, 188, 13));
            Add(client, Browse(() => BrowseFolder(_dat, "The folder AC:Unreal's client_portal.dat is in")), Dlu(199, 20, 50, 14));
            Add(client, new Label { Text = "Overlay DLL (empty: the one beside Decal, or the build's)" }, Dlu(7, 37, 242, 9));
            Add(client, _overlay, Dlu(7, 47, 188, 13));
            Add(client, Browse(() => BrowseFile(_overlay)), Dlu(199, 46, 50, 14));
            _autoInject.Text = "Put the overlay into AC:Unreal when it starts";
            Add(client, _autoInject, Dlu(7, 62, 242, 10));

            // Plugins
            GroupBox plugins = Group("Plugins", Dlu(7, 149, 257, 62));
            _actAtStart.Text = "Let plugins act from the start";
            Add(plugins, _actAtStart, Dlu(7, 11, 150, 10));
            Add(plugins, new Label { Text = "Movement keys, which plugins walk with:" }, Dlu(7, 25, 242, 9));

            (GameKey Key, string Label, Rectangle LabelAt, Rectangle BoxAt)[] keyBoxes =
            {
                (GameKey.Forward, "Forward", Dlu(7, 37, 38, 12), Dlu(46, 36, 30, 13)),
                (GameKey.Backward, "Back", Dlu(80, 37, 26, 12), Dlu(107, 36, 30, 13)),
                (GameKey.TurnLeft, "Left", Dlu(141, 37, 20, 12), Dlu(162, 36, 30, 13)),
                (GameKey.TurnRight, "Right", Dlu(196, 37, 22, 12), Dlu(219, 36, 30, 13)),
            };

            foreach ((GameKey key, string label, Rectangle labelAt, Rectangle boxAt) in keyBoxes)
            {
                Add(plugins, new Label { Text = label, TextAlign = ContentAlignment.MiddleRight }, labelAt);
                TextBox box = new TextBox { Enabled = keys != null, Text = keys != null && keys.TryGetValue(key, out string bound) ? bound : string.Empty };
                Add(plugins, box, boxAt);
                _keys[key] = box;
                _tips.SetToolTip(box, "A letter, a digit, Space, Shift, Up, Down, Left, Right or a key number.");
            }

            if (keys == null)
                Add(plugins, new Label { Text = "The host is not running, so the keys cannot be changed now.", ForeColor = SystemColors.GrayText }, Dlu(7, 50, 242, 9));

            Button ok = new Button { Text = "OK", DialogResult = DialogResult.None, UseVisualStyleBackColor = true };
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, UseVisualStyleBackColor = true };
            Add(this, ok, Dlu(155, 216, 50, 14));
            Add(this, cancel, Dlu(214, 216, 50, 14));
            ok.Click += (_, _) => Accept();
            AcceptButton = ok;
            CancelButton = cancel;

            _server.Text = settings.ServerHost;
            _serverPort.Value = Math.Clamp(settings.ServerPort, 1, 65534);
            _listenPort.Value = Math.Clamp(settings.ListenPort, 1, 65534);
            _dat.Text = settings.DatFolder ?? string.Empty;
            _overlay.Text = settings.OverlayDll ?? string.Empty;
            _overlay.PlaceholderText = foundOverlay ?? "not found - choose it";
            _autoInject.Checked = settings.AutoInject;
            _actAtStart.Checked = settings.ActAtStart;
            ShowPointAt();

            if (commandLine?.ServerHost != null || commandLine?.ServerPort != null || commandLine?.ListenPort != null)
                _tips.SetToolTip(network, "The command line overrides these for this run; what is set here is used when it does not.");
            if (commandLine?.NoInject == true)
                _tips.SetToolTip(_autoInject, "--no-inject is on the command line, so this run never injects unasked.");

            ResumeLayout(false);
            PerformLayout();
        }

        /// <summary>The settings as the player left them, once OK has been pressed.</summary>
        public AgentSettings Settings { get; }

        /// <summary>The movement keys the player changed, by the text typed for each.</summary>
        public IReadOnlyDictionary<GameKey, string> ChangedKeys { get; private set; } = new Dictionary<GameKey, string>();

        private static Rectangle Dlu(int x, int y, int width, int height)
            => Rectangle.FromLTRB(
                (int)Math.Round(x * 1.5),
                (int)Math.Round(y * 13 / 8.0),
                (int)Math.Round((x + width) * 1.5),
                (int)Math.Round((y + height) * 13 / 8.0));

        private GroupBox Group(string text, Rectangle bounds)
        {
            GroupBox group = new GroupBox { Text = text };
            Add(this, group, bounds);
            return group;
        }

        private static void Add(Control parent, Control control, Rectangle bounds)
        {
            control.Bounds = bounds;
            parent.Controls.Add(control);
        }

        private static Button Browse(Action browse)
        {
            Button button = new Button { Text = "Browse...", UseVisualStyleBackColor = true };
            button.Click += (_, _) => browse();
            return button;
        }

        private void ShowPointAt()
            => _pointAt.Text = "Point AC:Unreal at 127.0.0.1, port " + ((int)_listenPort.Value).ToString(CultureInfo.InvariantCulture) + ".";

        private void BrowseFolder(TextBox box, string description)
        {
            using FolderBrowserDialog dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true, ShowNewFolderButton = false };
            if (Directory.Exists(box.Text))
                dialog.InitialDirectory = box.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK)
                box.Text = dialog.SelectedPath;
        }

        private void BrowseFile(TextBox box)
        {
            using OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "The overlay DLL",
                Filter = OverlayInjection.DllName + "|" + OverlayInjection.DllName + "|DLLs (*.dll)|*.dll",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog(this) == DialogResult.OK)
                box.Text = dialog.FileName;
        }

        /// <summary>Checks what was typed, and closes only if all of it can be used.</summary>
        private void Accept()
        {
            string server = _server.Text.Trim();
            if (server.Length == 0)
            {
                Refuse(_server, "Name the game server: 127.0.0.1 for one on this machine.");
                return;
            }

            int serverPort = (int)_serverPort.Value;
            int listenPort = (int)_listenPort.Value;
            bool local = server == "localhost" || (IPAddress.TryParse(server, out IPAddress address) && IPAddress.IsLoopback(address));
            if (local && Math.Abs(serverPort - listenPort) < 2)
            {
                Refuse(_listenPort, $"Decal would be listening on the server's own ports ({serverPort} and {serverPort + 1}). Choose a port at least two away, such as {serverPort + 100}.");
                return;
            }

            string dat = _dat.Text.Trim();
            if (dat.Length > 0 && !Directory.Exists(dat) && !File.Exists(dat))
            {
                Refuse(_dat, "There is no such folder: " + dat);
                return;
            }

            string overlay = _overlay.Text.Trim();
            if (overlay.Length > 0 && (!Path.IsPathFullyQualified(overlay) || !File.Exists(overlay)))
            {
                Refuse(_overlay, "The overlay DLL has to be a full path to a file that is there: it is loaded inside AC:Unreal, where a relative path means something else.");
                return;
            }

            Dictionary<GameKey, string> changed = new Dictionary<GameKey, string>();
            foreach (KeyValuePair<GameKey, TextBox> pair in _keys)
            {
                string typed = pair.Value.Text.Trim();
                if (!pair.Value.Enabled || (_keysBefore != null && _keysBefore.TryGetValue(pair.Key, out string before) && string.Equals(before, typed, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (!GameInput.TryParseKey(typed, out _))
                {
                    Refuse(pair.Value, $"\"{typed}\" is not a key. Use a letter, a digit, Space, Shift, Up, Down, Left, Right or a key number.");
                    return;
                }

                changed[pair.Key] = typed;
            }

            Settings.ServerHost = server;
            Settings.ServerPort = serverPort;
            Settings.ListenPort = listenPort;
            Settings.DatFolder = dat;
            Settings.OverlayDll = overlay;
            Settings.AutoInject = _autoInject.Checked;
            Settings.ActAtStart = _actAtStart.Checked;
            ChangedKeys = changed;

            DialogResult = DialogResult.OK;
            Close();
        }

        private void Refuse(Control control, string why)
        {
            MessageBox.Show(this, why, "Decal Options", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            control.Focus();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _tips.Dispose();
            base.Dispose(disposing);
        }
    }
}
