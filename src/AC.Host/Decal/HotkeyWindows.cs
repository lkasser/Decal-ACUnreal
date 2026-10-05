using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AC.Host.Plugins;
using AC.Host.Plugins.Views;

namespace AC.Host.Decal
{
    /// <summary>
    /// The two hotkey windows of the standard client, over the host's one set of hotkeys: the
    /// Decal Hotkey System, on Decal's bar, for the hotkeys of plugins drawn by Decal; and the
    /// Virindi Hotkey System, on VVS's bar, for those drawn by VVS - Virindi Tank's, which it
    /// registered there. Each is laid out from its own plugin's view: DHS.dll's, and
    /// VirindiHotkeySystem.dll's with the "Add VVS" button its code added. That button opens VHS's
    /// "Add VVS..." window, a list of the VVS windows on the bar: ticking one adds a hotkey,
    /// "VVS"'s "Toggle:name", that shows the window if hidden and hides it if shown.
    /// </summary>
    /// <remarks>
    /// A key is bound as those windows bound one: choose the action, press Set (VHS) or type
    /// its name and press Add Hotkey (DHS), then press the key. The overlay catches that one key
    /// and keeps it from the game; Escape cancels.
    /// </remarks>
    internal sealed class HotkeyWindows
    {
        /// <summary>DHS.dll's view, as it ships.</summary>
        internal const string DhsXml =
            "<view icon=\"10179\" title=\"Decal Hotkey System\" width=\"524\" height=\"264\">" +
            "<control progid=\"DecalControls.FixedLayout\">" +
            "<control progid=\"DecalControls.List\" name=\"lstMain\" left=\"0\" top=\"0\" width=\"524\" height=\"200\">" +
            "<column progid=\"DecalControls.TextColumn\" fixedwidth=\"100\" />" +
            "<column progid=\"DecalControls.TextColumn\" fixedwidth=\"126\" />" +
            "<column progid=\"DecalControls.TextColumn\" fixedwidth=\"240\" />" +
            "<column progid=\"DecalControls.CheckColumn\" fixedwidth=\"16\" />" +
            "<column progid=\"DecalControls.IconColumn\" fixedwidth=\"16\" />" +
            "</control>" +
            "<control name=\"btnAddKey\" progid=\"DecalControls.PushButton\" left=\"3\" top=\"205\" height=\"20\" width=\"75\" text=\"Add Hotkey\" />" +
            "<control name=\"btnCancel\" progid=\"DecalControls.PushButton\" left=\"83\" top=\"205\" height=\"20\" width=\"60\" text=\"Cancel\" />" +
            "<control name=\"editName\" progid=\"DecalControls.Edit\" left=\"150\" top=\"205\" height=\"20\" width=\"300\" imageportalsrc=\"4523\" textcolor=\"16777215\" />" +
            "</control></view>";

        /// <summary>
        /// VHS's window, as this host draws it: the list of keys and the buttons below it, by the
        /// names and kinds VHS's own view gave them (with the "Add VVS" button its code put beside
        /// Reset), on a layout and with labels of this repository's own.
        /// </summary>
        internal const string VhsXml =
            "<view icon=\"10667\" title=\"Virindi Hotkey System\" width=\"520\" height=\"200\">" +
            "<control progid=\"DecalControls.FixedLayout\" clipped=\"\">" +
            "<control progid=\"DecalControls.List\" name=\"lstKeys\" left=\"6\" top=\"6\" width=\"508\" height=\"150\">" +
            "<column progid=\"DecalControls.CheckColumn\" name=\"clEnabled\" fixedwidth=\"16\"/>" +
            "<column progid=\"DecalControls.IconColumn\" name=\"clIcon\" fixedwidth=\"16\"/>" +
            "<column progid=\"DecalControls.TextColumn\" name=\"clKey\" fixedwidth=\"70\"/>" +
            "<column progid=\"DecalControls.TextColumn\" name=\"clAsm\" fixedwidth=\"70\"/>" +
            "<column progid=\"DecalControls.TextColumn\" name=\"clName\" fixedwidth=\"120\"/>" +
            "<column progid=\"DecalControls.TextColumn\" name=\"clDesc\" />" +
            "</control>" +
            "<control progid=\"DecalControls.PushButton\" name=\"cmdSetKey\" left=\"6\" top=\"162\" width=\"70\" height=\"20\" text=\"Set key\"/>" +
            "<control progid=\"DecalControls.PushButton\" name=\"cmdUnsetKey\" left=\"82\" top=\"162\" width=\"70\" height=\"20\" text=\"Clear key\"/>" +
            "<control progid=\"DecalControls.PushButton\" name=\"cmdResetKey\" left=\"158\" top=\"162\" width=\"70\" height=\"20\" text=\"Default\"/>" +
            "<control progid=\"DecalControls.PushButton\" name=\"cmdAddVvs\" left=\"234\" top=\"162\" width=\"90\" height=\"20\" text=\"Add window\"/>" +
            "</control></view>";

        /// <summary>
        /// VHS's "Add VVS..." window, laid out as its code built it: 400x200, a list of a lamp and
        /// the window's name filling it but for a strip at the foot, and Close at the right of it.
        /// </summary>
        internal const string AddVvsXml =
            "<view title=\"Add VVS...\" width=\"400\" height=\"200\">" +
            "<control progid=\"DecalControls.FixedLayout\">" +
            "<control progid=\"DecalControls.List\" name=\"lstViews\" left=\"4\" top=\"4\" width=\"392\" height=\"172\">" +
            "<column progid=\"DecalControls.CheckColumn\" name=\"colImg\" fixedwidth=\"16\"/>" +
            "<column progid=\"DecalControls.TextColumn\" name=\"colText\"/>" +
            "</control>" +
            "<control progid=\"DecalControls.PushButton\" name=\"cmdClose\" left=\"346\" top=\"180\" width=\"50\" height=\"16\" text=\"Close\"/>" +
            "</control></view>";

        /// <summary>What begins the id of a hotkey that turns a VVS window over, as VHS named them.</summary>
        public const string TogglePrefix = "Toggle:";

        /// <summary>The command the overlay sends with the key it caught.</summary>
        public const string KeyCapturedCommand = "key-captured";

        /// <summary>VVS's colour for a list's chosen row: Purple.</summary>
        private const long Selected = 0xFF800080;

        private readonly GameHost _host;
        private readonly string _owner;
        private readonly Action<string, KeyChord> _bind;
        private readonly Action<string, bool> _enable;
        private readonly Func<IReadOnlyCollection<string>> _toggles;
        private readonly Action<string, bool> _setToggle;
        private readonly List<BoundHotkey> _dhsRows = new List<BoundHotkey>();
        private readonly List<BoundHotkey> _vhsRows = new List<BoundHotkey>();
        private readonly List<string> _addVvsRows = new List<string>();
        private string _vhsSelected;
        private string _capturing;

        /// <param name="host">The host whose hotkeys these are.</param>
        /// <param name="owner">The plugin the overlay sends the caught key to.</param>
        /// <param name="bind">Binds "owner/id" to a key, and keeps it; an empty chord unbinds.</param>
        /// <param name="enable">Switches "owner/id" on or off, and keeps it.</param>
        /// <param name="toggles">The windows, by name, that have a key to turn them over.</param>
        /// <param name="setToggle">Adds or removes a window's hotkey, and keeps it.</param>
        public HotkeyWindows(GameHost host, string owner, Action<string, KeyChord> bind, Action<string, bool> enable,
                             Func<IReadOnlyCollection<string>> toggles = null, Action<string, bool> setToggle = null)
        {
            _host = host;
            _owner = owner;
            _bind = bind;
            _enable = enable;
            _toggles = toggles ?? (() => Array.Empty<string>());
            _setToggle = setToggle ?? ((_, _) => { });

            Dhs = DecalView.Parse(DhsXml);
            Dhs.IconKey = ViewImages.Portal(0x060027C3);
            Dhs.Bar = ViewBar.Decal;
            Dhs.Get<List>("lstMain").Clicked += (_, e) => DhsClicked(e.Row, e.Column);
            Dhs.Get<PushButton>("btnAddKey").Clicked += (_, _) => AddFromName();
            Dhs.Get<PushButton>("btnCancel").Clicked += (_, _) => CancelCapture();

            Vhs = DecalView.Parse(VhsXml);
            Vhs.IconKey = "host:vhs-button_arrow";
            Vhs.Bar = ViewBar.Vvs;
            Vhs.BarGroup = "VirindiHotkeySystem";
            Vhs.BarAssembly = "VirindiHotkeySystem, Version=1.0.0.6, Culture=neutral, PublicKeyToken=null";
            Vhs.StoredKey = "VirindiHotkeySystem:Virindi Hotkey System";
            Vhs.Get<List>("lstKeys").Clicked += (_, e) => VhsClicked(e.Row, e.Column);
            Vhs.Get<PushButton>("cmdSetKey").Clicked += (_, _) => StartCapture(_vhsSelected);
            Vhs.Get<PushButton>("cmdUnsetKey").Clicked += (_, _) => Rebind(_vhsSelected, default);
            Vhs.Get<PushButton>("cmdResetKey").Clicked += (_, _) => ResetToDefault(_vhsSelected);
            Vhs.Get<PushButton>("cmdAddVvs").Clicked += (_, _) => OpenAddVvs();

            // Neither on the bar nor minimizable, as VHS made it; open only from Add VVS.
            AddVvs = DecalView.Parse(AddVvsXml);
            AddVvs.Bar = ViewBar.Vvs;
            AddVvs.BarGroup = Vhs.BarGroup;
            AddVvs.BarAssembly = Vhs.BarAssembly;
            AddVvs.StoredKey = "VirindiHotkeySystem:Add VVS...";
            AddVvs.ShowInBar = false;
            AddVvs.Minimizable = false;
            AddVvs.Get<List>("lstViews").Clicked += (_, e) => AddVvsClicked(e.Row, e.Column);
            AddVvs.Get<PushButton>("cmdClose").Clicked += (_, _) => AddVvs.RequestClose();
        }

        public DecalView Dhs { get; }

        public DecalView Vhs { get; }

        /// <summary>VHS's "Add VVS..." window.</summary>
        public DecalView AddVvs { get; }

        /// <summary>The hotkey waiting for its key, "owner/id", or null.</summary>
        public string Capturing => _capturing;

        public IReadOnlyList<OverlayViewWindow> Windows => new[]
        {
            new OverlayViewWindow("dhs", Dhs, startsClosed: true),
            new OverlayViewWindow("vhs", Vhs, startsClosed: true),
            new OverlayViewWindow("addvvs", AddVvs, startsClosed: true),
        };

        /// <summary>Fills both lists from the host's hotkeys as they are now. Game thread.</summary>
        public void Refresh()
        {
            IReadOnlyList<BoundHotkey> all = _host.CollectHotkeys();
            _dhsRows.Clear();
            _vhsRows.Clear();
            foreach (BoundHotkey hotkey in all)
                (IsWindowToggle(hotkey) || DrawnByVvs(hotkey.Owner) ? _vhsRows : _dhsRows).Add(hotkey);

            List dhs = Dhs.Get<List>("lstMain");
            Resize(dhs, _dhsRows.Count);
            for (int i = 0; i < _dhsRows.Count; i++)
            {
                BoundHotkey hotkey = _dhsRows[i];
                ListRow row = dhs[i];
                row[0].Text = hotkey.Keys.IsEmpty ? string.Empty : hotkey.Keys.ToString();
                row[1].Text = hotkey.Owner + ": " + hotkey.Definition.Name;
                row[2].Text = hotkey.Definition.Description;
                row[3].Checked = !_host.DisabledHotkeys.Contains(hotkey.Name);
                row[4].ImageKey = hotkey.Keys.IsEmpty ? string.Empty : ViewImages.Portal(0x06001932);
            }

            List vhs = Vhs.Get<List>("lstKeys");
            Resize(vhs, _vhsRows.Count);
            for (int i = 0; i < _vhsRows.Count; i++)
            {
                BoundHotkey hotkey = _vhsRows[i];
                ListRow row = vhs[i];
                long? colour = hotkey.Name == _vhsSelected ? Selected : null;
                row[0].Checked = !_host.DisabledHotkeys.Contains(hotkey.Name);
                row[1].ImageKey = string.Empty;
                row[2].Text = _capturing == hotkey.Name ? "..." : KeyText(hotkey.Keys);
                row[3].Text = hotkey.Definition.Source ?? AssemblyName(hotkey.Owner);
                row[4].Text = hotkey.Definition.Name;
                row[5].Text = hotkey.Definition.Description;
                for (int c = 2; c < 6; c++)
                    row[c].Color = colour;
            }

            // The Add VVS lamps, over the windows listed when it was opened: listing them here
            // would ask the host for every window while it is asking this for these.
            IReadOnlyCollection<string> toggles = _toggles();
            List views = AddVvs.Get<List>("lstViews");
            Resize(views, _addVvsRows.Count);
            for (int i = 0; i < _addVvsRows.Count; i++)
            {
                views[i][0].Checked = toggles.Contains(_addVvsRows[i], StringComparer.Ordinal);
                views[i][1].Text = _addVvsRows[i];
            }
        }

        /// <summary>
        /// The name VHS knew a window by: what follows the assembly in its stored key, or its
        /// title when it has none.
        /// </summary>
        public static string WindowName(DecalView view)
        {
            if (view == null)
                return string.Empty;
            if (string.IsNullOrEmpty(view.StoredKey))
                return view.Title;
            string[] parts = view.StoredKey.Split(new[] { ':' }, 2);
            return parts.Length == 2 ? parts[1] : parts[0];
        }

        /// <summary>
        /// The VVS window a toggle hotkey names, as VHS found it: on the bar, minimizable, and
        /// called that. Null when there is none now.
        /// </summary>
        public static DecalView FindToggled(IEnumerable<OverlayWindowInfo> windows, string name)
        {
            foreach (OverlayWindowInfo window in windows)
            {
                DecalView view = window.View;
                if (view != null && view.Bar == ViewBar.Vvs && view.ShowInBar && view.Minimizable && WindowName(view) == name)
                    return view;
            }

            return null;
        }

        private bool IsWindowToggle(BoundHotkey hotkey) =>
            string.Equals(hotkey.Owner, _owner, StringComparison.OrdinalIgnoreCase)
            && hotkey.Definition.Id.StartsWith(TogglePrefix, StringComparison.Ordinal);

        /// <summary>Add VVS: the window listing every VVS window on the bar, opened.</summary>
        private void OpenAddVvs()
        {
            _addVvsRows.Clear();
            foreach (OverlayWindowInfo window in _host.CollectWindows())
            {
                DecalView view = window.View;
                if (view == null || view.Bar != ViewBar.Vvs || !view.ShowInBar)
                    continue;
                string name = WindowName(view);
                if (!string.IsNullOrEmpty(name) && !_addVvsRows.Contains(name, StringComparer.Ordinal))
                    _addVvsRows.Add(name);
            }

            AddVvs.RequestOpen();
            Refresh();
        }

        /// <summary>A lamp in Add VVS: the window gets its hotkey, or loses it.</summary>
        private void AddVvsClicked(int row, int column)
        {
            if (column != 0 || row < 0 || row >= _addVvsRows.Count)
                return;
            string name = _addVvsRows[row];
            bool had = _toggles().Contains(name, StringComparer.Ordinal);
            _setToggle(name, !had);
            if (!had)
                _host.ShowInGame("Virindi Hotkey System: \"Toggle:" + name + "\" is in the list; choose it and Set to give it a key.");
            Refresh();
        }

        /// <summary>The key the overlay caught, as "vk,ctrl,shift,alt", or "cancel". Game thread.</summary>
        public bool KeyCaptured(string value)
        {
            string name = _capturing;
            CancelCapture(quiet: true);
            if (name == null || string.Equals(value, "cancel", StringComparison.OrdinalIgnoreCase))
                return true;

            string[] parts = (value ?? string.Empty).Split(',');
            if (parts.Length != 4 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int vk) || vk <= 0)
                return false;

            KeyChord chord = new KeyChord(vk, parts[1] == "1", parts[2] == "1", parts[3] == "1");
            Rebind(name, chord);
            return true;
        }

        private void DhsClicked(int row, int column)
        {
            if (row < 0 || row >= _dhsRows.Count)
                return;
            BoundHotkey hotkey = _dhsRows[row];

            // The green dot switches it off and on; the X unbinds it.
            if (column == 3)
            {
                _enable(hotkey.Name, _host.DisabledHotkeys.Contains(hotkey.Name));
            }
            else if (column == 4)
            {
                if (!hotkey.Keys.IsEmpty)
                    Rebind(hotkey.Name, default);
            }
            else if (Dhs.TryGet("editName", out Edit edit))
            {
                edit.Text = hotkey.Owner + ": " + hotkey.Definition.Name;
            }

            Refresh();
        }

        private void VhsClicked(int row, int column)
        {
            if (row < 0 || row >= _vhsRows.Count)
                return;
            BoundHotkey hotkey = _vhsRows[row];
            _vhsSelected = hotkey.Name;

            if (column == 0)
                _enable(hotkey.Name, _host.DisabledHotkeys.Contains(hotkey.Name));

            Refresh();
        }

        /// <summary>DHS's Add Hotkey: the action named in the box, by "Plugin: name", its name or its description.</summary>
        private void AddFromName()
        {
            string typed = Dhs.TryGet("editName", out Edit edit) ? (edit.Text ?? string.Empty).Trim() : string.Empty;
            BoundHotkey found = _dhsRows.Concat(_vhsRows).FirstOrDefault(h =>
                string.Equals(h.Owner + ": " + h.Definition.Name, typed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(h.Definition.Name, typed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(h.Definition.Description, typed, StringComparison.OrdinalIgnoreCase));
            if (found == null)
            {
                _host.ShowInGame(typed.Length == 0
                    ? "Decal Hotkey System: type or click the function to bind, then Add Hotkey."
                    : "Decal Hotkey System: there is no function called \"" + typed + "\".");
                return;
            }

            StartCapture(found.Name);
        }

        private void StartCapture(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                _host.ShowInGame("Choose the hotkey to set first.");
                return;
            }

            BoundHotkey hotkey = _host.CollectHotkeys().FirstOrDefault(h => h.Name == name);
            if (hotkey == null)
                return;

            _capturing = name;
            _host.KeyCaptureOwner = _owner;
            _host.ShowInGame("Press the key for " + hotkey.Owner + "'s \"" + hotkey.Definition.Name + "\", or Escape to cancel.");
            Refresh();
        }

        private void CancelCapture(bool quiet = false)
        {
            bool was = _capturing != null;
            _capturing = null;
            if (_host.KeyCaptureOwner == _owner)
                _host.KeyCaptureOwner = null;
            if (was && !quiet)
                _host.ShowInGame("Hotkey not set.");
        }

        private void ResetToDefault(string name)
        {
            BoundHotkey hotkey = name == null ? null : _host.CollectHotkeys().FirstOrDefault(h => h.Name == name);
            if (hotkey == null)
                return;
            KeyChord.TryParse(hotkey.Definition.DefaultKeys, out KeyChord chord);
            Rebind(name, chord);
        }

        /// <summary>Binds a key to one action alone: whatever had the key before loses it.</summary>
        private void Rebind(string name, KeyChord chord)
        {
            if (string.IsNullOrEmpty(name))
                return;

            BoundHotkey hotkey = _host.CollectHotkeys().FirstOrDefault(h => h.Name == name);
            if (hotkey == null)
                return;

            if (!chord.IsEmpty)
            {
                foreach (BoundHotkey other in _host.CollectHotkeys())
                {
                    if (other.Name != name && other.Keys.Equals(chord))
                        _bind(other.Name, default);
                }
            }

            _bind(name, chord);
            _host.ShowInGame(chord.IsEmpty
                ? hotkey.Owner + "'s \"" + hotkey.Definition.Name + "\" has no key now."
                : chord + " now does " + hotkey.Owner + "'s \"" + hotkey.Definition.Name + "\".");
            Refresh();
        }

        /// <summary>Whether a plugin's windows are VVS's, so its hotkeys were Virindi Hotkey System's.</summary>
        private bool DrawnByVvs(string owner)
        {
            foreach (IPlugin plugin in _host.Plugins)
            {
                if (!string.Equals(plugin.Name, owner, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    return plugin is IOverlayView viewer && viewer.View?.Bar == ViewBar.Vvs;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>The name VHS's plugin column showed: the assembly's own short name - "VTank" for Virindi Tank.</summary>
        private static string AssemblyName(string owner) => string.Equals(owner, "VirindiTank", StringComparison.OrdinalIgnoreCase) ? "VTank" : owner;

        /// <summary>A key as VHS's key column wrote it: short and in lower case.</summary>
        internal static string KeyText(KeyChord keys)
        {
            if (keys.IsEmpty)
                return string.Empty;
            string text = keys.ToString().ToLowerInvariant();
            return text.Replace("scrolllock", "scrlk").Replace("pagedown", "pgdn").Replace("pageup", "pgup")
                       .Replace("delete", "del").Replace("insert", "ins").Replace("escape", "esc");
        }

        private static void Resize(List list, int rows)
        {
            while (list.RowCount < rows)
                list.Add();
            while (list.RowCount > rows)
                list.RemoveAt(list.RowCount - 1);
        }
    }
}
