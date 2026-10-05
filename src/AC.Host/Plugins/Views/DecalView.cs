using System;
using System.Collections.Generic;

namespace AC.Host.Plugins.Views
{
    /// <summary>The two bars of the standard client: Decal's, and Virindi View Service's.</summary>
    public enum ViewBar
    {
        Decal,
        Vvs,
    }

    /// <summary>
    /// The verbs the overlay uses for a view's controls, in <see cref="OverlayCommand.Name"/>.
    /// </summary>
    public static class ViewVerbs
    {
        /// <summary>A checkbox, edit box, dropdown or slider has a new value, in the command's value.</summary>
        public const string Set = "set";

        /// <summary>A button was pressed.</summary>
        public const string Press = "press";

        /// <summary>A notebook's tab was chosen; the value is the page index.</summary>
        public const string Page = "page";

        /// <summary>A list cell was clicked; the row id is the row index and the value the column index.</summary>
        public const string Click = "click";

        /// <summary>
        /// The player gave the window a new size, by its frame: no control, and the value its
        /// width and height in Decal pixels, "500,175".
        /// </summary>
        public const string Resize = "resize";

        /// <summary>The player pressed Enter in an edit box; the value is its text, as with <see cref="Set"/>.</summary>
        public const string Enter = "enter";
    }

    /// <summary>
    /// A Decal view: the window a plugin describes in view XML, parsed into named, typed
    /// controls the plugin reads and sets, and whose events it handles.
    /// </summary>
    /// <remarks>
    /// The same XML Decal plugins shipped, read the same way, so a view written for Decal
    /// needs no translation and code written against Decal's control wrappers - set a
    /// checkbox, fill a list, handle a click - keeps its shape:
    /// <code>
    /// DecalView view = DecalView.Parse(xml);
    /// view.Checkbox("cEnableBuffing").Changed += (s, e) => _buffing = e.Checked;
    /// view.StaticText("lblVitals_H_Self").Text = "74%";
    /// </code>
    ///
    /// <para>
    /// Unlike panels and <see cref="OverlayControl"/>s, a view holds state: it is the
    /// window's truth, and the host sends whatever it says each time it publishes. That is
    /// what lets a checkbox be set once and stay set, as Decal's did, rather than being
    /// re-declared several times a second.
    /// </para>
    ///
    /// <para>
    /// Everything about a view is touched on the game thread and only there. The plugin
    /// sets properties from its callbacks, which arrive on the game thread; the host reads
    /// them on the game thread when it publishes; and the player's commands are applied,
    /// and their events raised, on the game thread. Nothing ever touches a view from two
    /// threads, so nothing here locks, and a plugin that does its own work on another
    /// thread comes back through <see cref="IHost.RunOnGameThread"/> before touching one.
    /// </para>
    /// </remarks>
    public sealed class DecalView
    {
        private readonly Dictionary<string, ViewControl> _byName;
        private readonly List<ViewControl> _controls;
        private readonly List<string> _warnings;
        private string _title = string.Empty;
        private string _iconKey = string.Empty;

        internal DecalView(ViewControl root, Dictionary<string, ViewControl> byName, List<ViewControl> controls, List<string> warnings)
        {
            Root = root;
            _byName = byName;
            _controls = controls;
            _warnings = warnings;
        }

        /// <summary>
        /// Reads view XML as Decal plugins wrote it.
        /// </summary>
        /// <remarks>
        /// XML that is not well formed, or is not a view, throws
        /// <see cref="FormatException"/> naming the problem; what to do about that is the
        /// plugin's call. Anything merely unexpected inside a good file - a progid this
        /// host does not draw, a number that does not parse, two controls with one name -
        /// is kept as far as it can be and described in <see cref="Warnings"/>, because a
        /// view that is mostly right is worth more to the player than none.
        /// </remarks>
        public static DecalView Parse(string xml) => DecalViewParser.Parse(xml);

        /// <summary>What the title bar says.</summary>
        public string Title
        {
            get => _title;
            set => _title = value ?? string.Empty;
        }

        /// <summary>The title bar's icon as an image key, or empty for none.</summary>
        public string IconKey
        {
            get => _iconKey;
            set => _iconKey = value ?? string.Empty;
        }

        /// <summary>
        /// Which bar the window's switch goes on. Decal's own bar, across the top, for a view
        /// written for Decal; Virindi View Service's, down the side, for one of VVS's - Virindi
        /// Tank's among them, as in the standard client.
        /// </summary>
        public ViewBar Bar { get; set; } = ViewBar.Decal;

        /// <summary>
        /// The theme the plugin asks for, by Virindi View Service's name - "Decal", "Float" - or
        /// null to leave it to VVS's rules: the player's own pick, else VVS's default for a VVS
        /// view and the Decal theme for one of Decal's.
        /// </summary>
        public string Theme { get; set; }

        /// <summary>
        /// The key Virindi View Service stored this window under in vvs.s3db - the plugin's
        /// name, a colon, the window's title, as "uTank2:uTank2" - so it opens in the theme,
        /// and hudified or not, as the player last had it in the standard client. Null for a
        /// window VVS never knew.
        /// </summary>
        public string StoredKey { get; set; }

        /// <summary>
        /// Whether the player may resize the window. In the Decal theme a window that can be
        /// resized has a frame and one that cannot has none, so this also decides the frame.
        /// True unless the plugin says otherwise, which is how every window was drawn before.
        /// </summary>
        public bool Resizeable { get; set; } = true;

        /// <summary>Whether the title bar offers the pin that hudifies the window, as VVS's did by default.</summary>
        public bool Ghostable { get; set; } = true;

        /// <summary>Whether a hudified window can also be made click-through.</summary>
        public bool ClickThroughable { get; set; } = true;

        /// <summary>
        /// Whether the window starts hudified - only its body showing until left Ctrl is held -
        /// until the player says otherwise, from its title bar or in vvs.s3db.
        /// </summary>
        public bool Ghosted { get; set; }

        /// <summary>Whether a hudified window starts click-through.</summary>
        public bool ClickThrough { get; set; }

        /// <summary>
        /// Whether the window has a switch on its bar. A HUD has none: it is shown and hidden
        /// from its plugin's own list, as Virindi HUDs' were.
        /// </summary>
        public bool ShowInBar { get; set; } = true;

        /// <summary>Whether the title bar has a close button.</summary>
        public bool Minimizable { get; set; } = true;

        /// <summary>Whether the title bar has the buttons that make the window more and less see-through, where the theme has them.</summary>
        public bool AlphaChangeable { get; set; } = true;

        /// <summary>
        /// The plugin's own buttons on the title bar, left of the window's, as VVS's
        /// CreateWindowButton made them - a HUD's add and remove, say. Pressing one raises its
        /// <see cref="ViewTitleButton.Pressed"/>.
        /// </summary>
        public IList<ViewTitleButton> TitleButtons { get; } = new List<ViewTitleButton>();

        /// <summary>
        /// How many times the plugin has asked for the window to be shown. The overlay opens the
        /// window each time this goes up - after the player closed it with its X, say - as a
        /// Decal plugin's view.Activate() or VVS's Visible = true did.
        /// </summary>
        public int OpenRequests { get; private set; }

        /// <summary>Asks for the window to be shown, and brought back if the player closed it.</summary>
        public void RequestOpen() => OpenRequests++;

        /// <summary>
        /// How many times something has asked for the window to be shown if hidden and hidden if
        /// shown - a key bound to it in the Virindi Hotkey System. The overlay turns it over each
        /// time this goes up.
        /// </summary>
        public int ToggleRequests { get; private set; }

        /// <summary>Asks for the window to be shown if it is hidden, and hidden if it is shown.</summary>
        public void RequestToggle() => ToggleRequests++;

        /// <summary>
        /// How many times the plugin has asked for the window to be hidden - a Close button of its
        /// own, as VVS's Visible = false did. The overlay closes the window each time this goes up.
        /// </summary>
        public int CloseRequests { get; private set; }

        /// <summary>Asks for the window to be hidden; it stays on its bar, from where it opens.</summary>
        public void RequestClose() => CloseRequests++;

        /// <summary>
        /// Which group of Virindi View Service's bar the window is in: VVS grouped by the plugin
        /// assembly that made the view, with a rule between one group and the next. Null for the
        /// owning plugin's own group.
        /// </summary>
        public string BarGroup { get; set; }

        /// <summary>
        /// The full name of the assembly VVS knew the window's plugin by - "uTank2,
        /// Version=1.0.0.0, Culture=neutral, PublicKeyToken=null" - which VVS ordered its bar's
        /// groups by (see <see cref="ViewBarOrder"/>). Null leaves the group where its plugin
        /// comes, after those with a name.
        /// </summary>
        public string BarAssembly { get; set; }

        /// <summary>
        /// A window with no switch on either bar that Ctrl and a click on the Decal bar's second
        /// grip opens - the in-game Decal window, which Decal itself never put on its bar.
        /// </summary>
        public bool OpensFromBarGrip { get; set; }

        /// <summary>In Decal pixels.</summary>
        public int Width { get; set; }

        public int Height { get; set; }

        /// <summary>
        /// The smallest the player may make a window they can resize, in Decal pixels: VVS's
        /// MinimumClientArea, 100 by 100 unless the plugin said otherwise.
        /// </summary>
        public int MinWidth { get; set; } = 100;

        public int MinHeight { get; set; } = 100;

        /// <summary>The largest: VVS's MaximumClientArea, 1000 by 1000 unless the plugin said otherwise.</summary>
        public int MaxWidth { get; set; } = 1000;

        public int MaxHeight { get; set; } = 1000;

        /// <summary>
        /// Where on screen the window first opens, as VVS's view.Location put it - unless the
        /// player's vvs.s3db says where they left it hudified. Null leaves it to the overlay.
        /// </summary>
        public (int X, int Y)? Location { get; set; }

        /// <summary>
        /// The player gave the window a new size, by its frame, or the overlay gave it the size
        /// the player left it at: <see cref="Width"/> and <see cref="Height"/> are already set,
        /// as VVS's Resize came after the size had changed. A plugin that lays its controls out
        /// by the window's size does it again here.
        /// </summary>
        public event EventHandler Resized;

        /// <summary>The outermost control: a Notebook or a FixedLayout, in every view seen so far.</summary>
        public ViewControl Root { get; }

        /// <summary>
        /// Every control, root first, in the order of the XML. Named or not, drawn or not.
        /// </summary>
        public IReadOnlyList<ViewControl> Controls => _controls;

        /// <summary>
        /// What the parser kept but could not fully honour, in sentences. Empty for a view
        /// that parsed cleanly; worth logging otherwise, since each is something the player
        /// will see drawn differently from Decal, or not at all.
        /// </summary>
        public IReadOnlyList<string> Warnings => _warnings;

        /// <summary>Whether a control has this name. Names are matched exactly, case included.</summary>
        public bool Contains(string name) => name != null && _byName.ContainsKey(name);

        /// <summary>
        /// The control with this name, as the type the plugin expects it to be.
        /// </summary>
        /// <remarks>
        /// Throws <see cref="KeyNotFoundException"/> both when there is no such control and
        /// when it is of another type, with a message saying which - because this is
        /// usually called once at startup with a name copied from the XML, and the message
        /// is what the plugin's author will be reading when they got it wrong.
        /// </remarks>
        public T Get<T>(string name) where T : ViewControl
        {
            if (name == null || !_byName.TryGetValue(name, out ViewControl control))
                throw new KeyNotFoundException($"The view '{Title}' has no control named '{name}'.");

            if (control is not T typed)
            {
                throw new KeyNotFoundException(
                    $"The control '{name}' in view '{Title}' is {WithArticle(control.GetType().Name)} ({control.ProgId}), not {WithArticle(typeof(T).Name)}.");
            }

            return typed;
        }

        private static string WithArticle(string noun)
            => ("AEIOU".IndexOf(noun.Length > 0 ? noun[0] : 'x') >= 0 ? "an " : "a ") + noun;

        /// <summary>The control with this name if there is one and it is a <typeparamref name="T"/>.</summary>
        public bool TryGet<T>(string name, out T control) where T : ViewControl
        {
            if (name != null && _byName.TryGetValue(name, out ViewControl found) && found is T typed)
            {
                control = typed;
                return true;
            }

            control = null;
            return false;
        }

        public Checkbox Checkbox(string name) => Get<Checkbox>(name);

        public PushButton PushButton(string name) => Get<PushButton>(name);

        public ImageButton ImageButton(string name) => Get<ImageButton>(name);

        public Edit Edit(string name) => Get<Edit>(name);

        public Choice Choice(string name) => Get<Choice>(name);

        public Slider Slider(string name) => Get<Slider>(name);

        public List List(string name) => Get<List>(name);

        public StaticText StaticText(string name) => Get<StaticText>(name);

        public Notebook Notebook(string name) => Get<Notebook>(name);

        public Progress Progress(string name) => Get<Progress>(name);

        /// <summary>
        /// Adds a control to a layout already in the view, for a plugin that builds its window in
        /// code rather than all of it in XML - as Virindi View Service let plugins do.
        /// </summary>
        /// <remarks>
        /// The control is registered as a parsed one is, so it is found by name, drawn, and
        /// takes the overlay's commands. The name must be new to the view and not empty, since
        /// a name is the only way a click finds its control. Any control kind the parser makes
        /// can be added except an <see cref="UnknownControl"/>, which would never be drawn.
        /// </remarks>
        public T AddControl<T>(FixedLayout parent, string name, int left, int top, int width, int height)
            where T : ViewControl
        {
            RequireOwn(parent);

            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("A control added at run time needs a name, or nothing could click it.", nameof(name));
            if (_byName.ContainsKey(name))
                throw new ArgumentException($"The view '{Title}' already has a control named '{name}'.", nameof(name));

            Type type = typeof(T);
            ViewControl control =
                type == typeof(FixedLayout) ? new FixedLayout("DecalControls.FixedLayout", name)
                : type == typeof(Notebook) ? new Notebook("DecalControls.Notebook", name)
                : type == typeof(StaticText) ? new StaticText("DecalControls.StaticText", name)
                : type == typeof(Checkbox) ? new Checkbox("DecalControls.Checkbox", name)
                : type == typeof(PushButton) ? new PushButton("DecalControls.PushButton", name)
                : type == typeof(ImageButton) ? new ImageButton("DecalControls.Button", name)
                : type == typeof(Edit) ? new Edit("DecalControls.Edit", name)
                : type == typeof(Choice) ? new Choice("DecalControls.Choice", name)
                : type == typeof(Slider) ? new Slider("DecalControls.Slider", name)
                : type == typeof(List) ? new List("DecalControls.List", name)
                : type == typeof(Progress) ? new Progress("DecalControls.Progress", name)
                : type == typeof(Picture) ? new Picture("VirindiViewService.Controls.HudPictureBox", name)
                : type == typeof(TextConsole) ? new TextConsole("VirindiViewService.Controls.HudConsole", name)
                : throw new ArgumentException($"A {type.Name} cannot be added at run time.", nameof(T));

            control.Left = left;
            control.Top = top;
            control.Width = width;
            control.Height = height;
            parent.Add(control);
            Register(control);
            return (T)control;
        }

        /// <summary>
        /// Adds a page to a notebook already in the view, with an empty layout on it to hold
        /// whatever the plugin puts there.
        /// </summary>
        public NotebookPage AddPage(Notebook notebook, string label)
        {
            RequireOwn(notebook);

            FixedLayout content = new FixedLayout("DecalControls.FixedLayout", string.Empty);
            Register(content);
            NotebookPage page = new NotebookPage(label, content);
            notebook.Add(page);
            return page;
        }

        /// <summary>
        /// Adds a column to a list already in the view. Only while it has no rows: every row has
        /// one cell per column, and a column added under existing rows would leave them short.
        /// </summary>
        public ListColumn AddColumn(List list, ListColumnKind kind, int fixedWidth, string name = null)
        {
            RequireOwn(list);

            if (list.RowCount > 0)
                throw new InvalidOperationException($"{list} has rows; a column can only be added to an empty list.");

            ListColumn column = new ListColumn(kind, fixedWidth, name);
            list.AddColumn(column);
            return column;
        }

        /// <summary>Removes every row and column from a list in the view, for a plugin that lays it out afresh.</summary>
        public void ClearColumns(List list)
        {
            RequireOwn(list);
            list.Clear();
            list.ClearColumns();
        }

        private void Register(ViewControl control)
        {
            _controls.Add(control);
            if (control.Name.Length > 0)
                _byName.Add(control.Name, control);
        }

        private void RequireOwn(ViewControl control)
        {
            if (control == null)
                throw new ArgumentNullException(nameof(control));
            if (!_controls.Contains(control))
                throw new ArgumentException($"{control} is not in the view '{Title}'.", nameof(control));
        }

        /// <summary>
        /// Applies something the player did, to the control the command names: the control's
        /// state first, then its event. Returns false if the view could not take it.
        /// </summary>
        /// <remarks>
        /// False for a control the view does not have, one that is disabled or hidden (the
        /// player can only have acted on it before the plugin changed that), the wrong verb
        /// for the kind of control, or a value that does not parse or is out of range. None
        /// of those raise an event. An exception thrown by the plugin's own handler is not
        /// caught here: it is a fault in the plugin, and the host counts it as one.
        /// </remarks>
        public bool Apply(OverlayCommand command) => Apply(command, out _);

        /// <summary>
        /// Whether a command is this view's to take: it names one of its controls or one of its
        /// title-bar buttons, or it is a new size for the window.
        /// </summary>
        public bool Takes(OverlayCommand command)
        {
            if (command == null)
                return false;
            if (Contains(command.ControlId))
                return true;
            if (command.ControlId.Length == 0)
                return string.Equals(command.Name, ViewVerbs.Resize, StringComparison.Ordinal);
            foreach (ViewTitleButton button in TitleButtons)
            {
                if (button != null && string.Equals(button.Name, command.ControlId, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Sets the size the player gave the window, kept within its least and most, and raises
        /// <see cref="Resized"/> when that changed it. False for a value that is not "width,height".
        /// </summary>
        private bool Resize(string value, out string refusal)
        {
            string[] parts = (value ?? string.Empty).Split(',');
            if (!Resizeable)
            {
                refusal = $"the view '{Title}' cannot be resized";
                return false;
            }

            if (parts.Length != 2
                || !int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int width)
                || !int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int height))
            {
                refusal = $"'{value}' is not a width and a height";
                return false;
            }

            width = Math.Min(Math.Max(width, MinWidth), Math.Max(MinWidth, MaxWidth));
            height = Math.Min(Math.Max(height, MinHeight), Math.Max(MinHeight, MaxHeight));
            refusal = null;
            if (width == Width && height == Height)
                return true;

            Width = width;
            Height = height;
            Resized?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary><see cref="Apply(OverlayCommand)"/>, saying why when it refuses.</summary>
        internal bool Apply(OverlayCommand command, out string refusal)
        {
            if (command == null)
            {
                refusal = "there was no command";
                return false;
            }

            if (command.ControlId.Length == 0 && string.Equals(command.Name, ViewVerbs.Resize, StringComparison.Ordinal))
                return Resize(command.Value, out refusal);

            if (!_byName.TryGetValue(command.ControlId, out ViewControl control))
            {
                foreach (ViewTitleButton button in TitleButtons)
                {
                    if (button != null && button.Name.Length > 0 && string.Equals(button.Name, command.ControlId, StringComparison.Ordinal))
                    {
                        if (!string.Equals(command.Name, ViewVerbs.Press, StringComparison.Ordinal))
                        {
                            refusal = $"the title bar's {button.Name} can only be pressed";
                            return false;
                        }

                        button.RaisePressed();
                        refusal = null;
                        return true;
                    }
                }

                refusal = $"the view has no control named '{command.ControlId}'";
                return false;
            }

            if (!control.Enabled)
            {
                refusal = $"{control} is disabled";
                return false;
            }

            if (!control.Visible)
            {
                refusal = $"{control} is hidden";
                return false;
            }

            return control.Apply(command, out refusal);
        }
    }

    /// <summary>
    /// Where a group goes on VVS's bar: VVS sorted by the plugin assembly's full name's hash
    /// code, as .NET Framework computed it in the 32-bit game client, with randomised hashing
    /// off - and nudged away from the bottom, which its own "Change Global Theme" entry held.
    /// </summary>
    public static class ViewBarOrder
    {
        public static int Of(string assemblyFullName)
        {
            string s = assemblyFullName ?? string.Empty;
            int hash1 = (5381 << 16) + 5381;
            int hash2 = hash1;
            int length = s.Length;
            int at = 0;
            unchecked
            {
                // Two characters to an int, little end first, as the framework read the string's
                // memory; past its end the terminator and nothing.
                int Pair(int index) => (index < s.Length ? s[index] : 0) | ((index + 1 < s.Length ? s[index + 1] : 0) << 16);
                while (length > 2)
                {
                    hash1 = ((hash1 << 5) + hash1 + (hash1 >> 27)) ^ Pair(at);
                    hash2 = ((hash2 << 5) + hash2 + (hash2 >> 27)) ^ Pair(at + 2);
                    at += 4;
                    length -= 4;
                }

                if (length > 0)
                    hash1 = ((hash1 << 5) + hash1 + (hash1 >> 27)) ^ Pair(at);

                int hash = hash1 + (hash2 * 1566083941);
                return hash < int.MinValue + 100 ? hash + 100 : hash;
            }
        }
    }

    /// <summary>
    /// A plugin's own button on a view's title bar, as VVS's CreateWindowButton made one: an
    /// image, the image while held, and a tooltip. Raised on the game thread when pressed.
    /// </summary>
    public sealed class ViewTitleButton
    {
        public ViewTitleButton(string name, string imageKey, string pressedImageKey = null, string tooltip = null)
        {
            Name = name ?? string.Empty;
            ImageKey = imageKey ?? string.Empty;
            PressedImageKey = string.IsNullOrEmpty(pressedImageKey) ? ImageKey : pressedImageKey;
            Tooltip = tooltip ?? string.Empty;
        }

        /// <summary>Names the button in the command a press sends; unique within the view.</summary>
        public string Name { get; }

        public string ImageKey { get; set; }

        public string PressedImageKey { get; set; }

        public string Tooltip { get; set; }

        public event EventHandler Pressed;

        internal void RaisePressed() => Pressed?.Invoke(this, EventArgs.Empty);
    }
}
