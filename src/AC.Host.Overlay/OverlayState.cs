using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AC.Host.Overlay
{
    // These types are the .NET half of native/ACUnrealOverlay/overlay_state.h, and the
    // C++ half parses the JSON by key. Every property therefore names its key outright
    // rather than leaving it to a naming policy: renaming a property here would
    // otherwise silently stop the overlay from finding the field, and nothing in this
    // solution compiles the C++ side, so nothing would catch it.
    //
    // The collections are List<T> and the strings are never null, because the other end
    // reads them into std::vector and std::string. A JSON null where a string is
    // expected makes that parser throw, which on the far side of a pipe looks like the
    // overlay having simply stopped.

    /// <summary>
    /// How the overlay draws a row. The values are the ones the C++ side switches on.
    /// </summary>
    public static class RowTones
    {
        public const int Normal = 0;

        /// <summary>Something went the player's way: an item kept, a buff applied.</summary>
        public const int Good = 1;

        /// <summary>Something did not: an action refused, a debuff landed.</summary>
        public const int Bad = 2;

        /// <summary>Present but not interesting, such as an item the rules ignored.</summary>
        public const int Muted = 3;
    }

    /// <summary>
    /// One row of a panel. Deliberately just cells: the host decides what the columns
    /// mean, so a new panel needs no change on the overlay side.
    /// </summary>
    public sealed class OverlayRow
    {
        [JsonPropertyName("cells")]
        public List<string> Cells { get; set; } = new List<string>();

        /// <summary>One of <see cref="RowTones"/>.</summary>
        /// <summary>
        /// Opaque, and meaningful only to whatever produced the row. Empty means the row
        /// cannot be acted on.
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("tone")]
        public int Tone { get; set; }
    }

    /// <summary>A titled table the overlay draws.</summary>
    public sealed class OverlayPanel
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// The plugin that produced this panel, which is how panels are grouped into one
        /// window per plugin. Empty means the host itself.
        /// </summary>
        [JsonPropertyName("owner")]
        public string Owner { get; set; } = string.Empty;

        /// <summary>
        /// Stable across publishes, unlike the title. A display that remembers anything
        /// per panel needs something that does not move when a title is edited or two
        /// panels swap places.
        /// </summary>
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("columns")]
        public List<string> Columns { get; set; } = new List<string>();

        [JsonPropertyName("rows")]
        public List<OverlayRow> Rows { get; set; } = new List<OverlayRow>();
    }

    /// <summary>What the host says about itself, for the overlay's status line.</summary>
    public sealed class OverlayStatus
    {
        /// <summary>
        /// Connected to the game server - not to the overlay's pipe. Two separate
        /// readers of the native contract each took one field to mean the other, which is
        /// why the name says which.
        /// </summary>
        [JsonPropertyName("server_connected")]
        public bool ServerConnected { get; set; }

        /// <summary>Whether plugins are allowed to act, rather than only watch.</summary>
        [JsonPropertyName("acting")]
        public bool Acting { get; set; }

        [JsonPropertyName("looting")]
        public bool Looting { get; set; }

        [JsonPropertyName("server")]
        public string Server { get; set; } = string.Empty;

        [JsonPropertyName("character")]
        public string Character { get; set; } = string.Empty;

        [JsonPropertyName("position")]
        public string Position { get; set; } = string.Empty;

        [JsonPropertyName("messages_in")]
        public long MessagesIn { get; set; }

        [JsonPropertyName("messages_out")]
        public long MessagesOut { get; set; }

        [JsonPropertyName("malformed")]
        public long Malformed { get; set; }

        /// <summary>How many objects the host currently knows about.</summary>
        /// <summary>
        /// Anything the host wants to say in words: why it stopped, what it is waiting
        /// for. Without it a display can see the host go quiet but never say why.
        /// </summary>
        [JsonPropertyName("notice")]
        public string Notice { get; set; } = string.Empty;

        [JsonPropertyName("objects")]
        public long Objects { get; set; }
    }

    /// <summary>What kind of control a plugin wants drawn. Values match the native enum.</summary>
    public static class ControlKinds
    {
        public const int Toggle = 0;
        public const int Button = 1;
        public const int Slider = 2;
        public const int Choice = 3;
        public const int Text = 4;
    }

    /// <summary>
    /// A control a plugin owns, drawn in that plugin's window: the vocabulary the real
    /// uTank2 pages are made of, which rows of strings cannot express.
    /// </summary>
    /// <remarks>
    /// The value is always a string so one field serves every kind: "true"/"false" for a
    /// toggle, the number for a slider, the chosen option for a choice, the text for a
    /// text box, nothing for a button. The host owns the truth of it; the overlay draws
    /// what it is given and sends back what the player did.
    /// </remarks>
    public sealed class OverlayControl
    {
        /// <summary>What comes back as the command's control id. Unique within the window.</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("kind")]
        public int Kind { get; set; } = ControlKinds.Toggle;

        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        /// <summary>Choice only.</summary>
        [JsonPropertyName("options")]
        public List<string> Options { get; set; } = new List<string>();

        /// <summary>Slider only. A step of zero means continuous.</summary>
        [JsonPropertyName("min")]
        public double Min { get; set; }

        [JsonPropertyName("max")]
        public double Max { get; set; } = 1.0;

        [JsonPropertyName("step")]
        public double Step { get; set; }

        [JsonPropertyName("tooltip")]
        public string Tooltip { get; set; } = string.Empty;
    }

    /// <summary>
    /// A plugin's window, declared rather than inferred from whichever panels arrived
    /// this frame - the explicit plugin list Decal had. It is also where a plugin's
    /// controls live, so a switch arrives already owned.
    /// </summary>
    public sealed class OverlayWindow
    {
        /// <summary>
        /// The plugin, and what a command from this window is routed to. An entry with an
        /// empty owner is ignored: the host's own window is drawn from the status, and has
        /// no controls to declare.
        /// </summary>
        [JsonPropertyName("owner")]
        public string Owner { get; set; } = string.Empty;

        /// <summary>
        /// What the bar and the title bar say. Empty means the owner's name. Only what is
        /// shown: the window is remembered, and commands routed, by the owner.
        /// </summary>
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// False when the plugin's controls could not be read this time. It stays on the bar,
        /// its name drawn in the bad tone, with a line in its window pointing at the log.
        /// </summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>Closed until opened from the bar, rather than open when first seen.</summary>
        [JsonPropertyName("starts_closed")]
        public bool StartsClosed { get; set; }

        [JsonPropertyName("controls")]
        public List<OverlayControl> Controls { get; set; } = new List<OverlayControl>();

        /// <summary>
        /// A Decal view, when the plugin has one: the window is then drawn as that view, in
        /// the Decal theme, instead of as controls and panel tabs. Null for none.
        /// </summary>
        [JsonPropertyName("view")]
        public OverlayView View { get; set; }
    }

    /// <summary>The kinds of Decal control a view can hold, as the wire spells them.</summary>
    /// <remarks>
    /// One per DecalControls progid that plugins actually use. The overlay draws an unknown
    /// kind as nothing, so a new one can be added here before the DLL knows how to draw it.
    /// </remarks>
    public static class ViewControlTypes
    {
        /// <summary>DecalControls.FixedLayout: children at absolute positions.</summary>
        public const string Fixed = "fixed";

        /// <summary>DecalControls.Notebook: one page showing at a time, chosen by tab.</summary>
        public const string Notebook = "notebook";

        /// <summary>DecalControls.StaticText.</summary>
        public const string Static = "static";

        /// <summary>DecalControls.Checkbox.</summary>
        public const string Checkbox = "checkbox";

        /// <summary>DecalControls.PushButton: a framed button with text.</summary>
        public const string PushButton = "pushbutton";

        /// <summary>DecalControls.Button: a button drawn as an image.</summary>
        public const string Button = "button";

        /// <summary>DecalControls.Edit: one line of text.</summary>
        public const string Edit = "edit";

        /// <summary>DecalControls.Choice: a dropdown.</summary>
        public const string Choice = "choice";

        /// <summary>DecalControls.Slider.</summary>
        public const string Slider = "slider";

        /// <summary>DecalControls.List: rows of cells under typed columns.</summary>
        public const string List = "list";

        /// <summary>DecalControls.Progress.</summary>
        public const string Progress = "progress";
    }

    /// <summary>The kinds of list column, as the wire spells them.</summary>
    public static class ViewColumnTypes
    {
        public const string Text = "text";
        public const string Check = "check";
        public const string Icon = "icon";
    }

    /// <summary>
    /// A Decal view, as the plugin's view XML describes it, with every control's current
    /// state filled in. Positions and sizes are Decal's own pixels; the overlay scales them.
    /// </summary>
    /// <remarks>
    /// The host parses the XML, not the DLL: the plugin needs the parsed controls anyway to
    /// set values by name, and a malformed file is then a log line in the host rather than
    /// something a parser in the game process has to survive.
    /// </remarks>
    public sealed class OverlayView
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>An image key for the title bar, or empty for none.</summary>
        [JsonPropertyName("icon")]
        public string Icon { get; set; } = string.Empty;

        /// <summary>"decal" for Decal's bar across the top, "vvs" for Virindi View Service's down the side.</summary>
        [JsonPropertyName("bar")]
        public string Bar { get; set; } = "decal";

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        /// <summary>
        /// The theme to draw it in by VVS's name - "Decal", "Float" - resolved by the host from
        /// the player's vvs.s3db, the plugin and VVS's default. The player's pick from the
        /// window's own title bar, which the overlay keeps, outranks it.
        /// </summary>
        [JsonPropertyName("theme")]
        public string Theme { get; set; } = string.Empty;

        /// <summary>Starts hudified: only the body shows until left Ctrl is held.</summary>
        [JsonPropertyName("ghosted")]
        public bool Ghosted { get; set; }

        /// <summary>A hudified window that starts click-through.</summary>
        [JsonPropertyName("click_through")]
        public bool ClickThrough { get; set; }

        [JsonPropertyName("resizeable")]
        public bool Resizeable { get; set; } = true;

        [JsonPropertyName("ghostable")]
        public bool Ghostable { get; set; } = true;

        [JsonPropertyName("click_throughable")]
        public bool ClickThroughable { get; set; } = true;

        /// <summary>False for a HUD: no switch on any bar.</summary>
        [JsonPropertyName("show_in_bar")]
        public bool ShowInBar { get; set; } = true;

        [JsonPropertyName("minimizable")]
        public bool Minimizable { get; set; } = true;

        [JsonPropertyName("alpha_changeable")]
        public bool AlphaChangeable { get; set; } = true;

        /// <summary>The plugin's own title-bar buttons, rightmost first after the window's.</summary>
        [JsonPropertyName("title_buttons")]
        public List<OverlayTitleButton> TitleButtons { get; set; } = new List<OverlayTitleButton>();

        /// <summary>
        /// Goes up each time the plugin asks for the window to be shown; the overlay opens it
        /// whenever this is higher than it last saw.
        /// </summary>
        [JsonPropertyName("open_request")]
        public int OpenRequest { get; set; }

        /// <summary>Goes up each time something asks for the window to be turned over, shown or hidden.</summary>
        [JsonPropertyName("toggle_request")]
        public int ToggleRequest { get; set; }

        /// <summary>Goes up each time the plugin asks for the window to be hidden.</summary>
        [JsonPropertyName("close_request")]
        public int CloseRequest { get; set; }

        /// <summary>Which of VVS's bar groups it is in - its plugin, in VVS; empty for the owner's plugin.</summary>
        [JsonPropertyName("bar_group")]
        public string BarGroup { get; set; } = string.Empty;

        /// <summary>Where the group goes on VVS's bar, lowest first; absent to follow those that have one.</summary>
        [JsonPropertyName("bar_order")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? BarOrder { get; set; }

        /// <summary>A window with no switch, opened by Ctrl and a click on the Decal bar's second grip.</summary>
        [JsonPropertyName("opens_from_grip")]
        public bool OpensFromGrip { get; set; }

        /// <summary>
        /// Where on screen the window first opens, as VVS put a hudified window back where it
        /// was left. Absent to leave it to the overlay. After that the overlay remembers.
        /// </summary>
        [JsonPropertyName("x")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? X { get; set; }

        [JsonPropertyName("y")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Y { get; set; }

        [JsonPropertyName("root")]
        public OverlayViewControl Root { get; set; }
    }

    /// <summary>Decal's bar settings, from Decal's registry: BarState, BarDock, BarStart, BarLength.</summary>
    public sealed class OverlayDecalBar
    {
        /// <summary>1 compact, 0 expanded.</summary>
        [JsonPropertyName("state")]
        public int State { get; set; } = 1;

        /// <summary>0 top, 1 left, 2 right.</summary>
        [JsonPropertyName("dock")]
        public int Dock { get; set; }

        [JsonPropertyName("start")]
        public int Start { get; set; }

        [JsonPropertyName("length")]
        public int Length { get; set; } = 250;

        /// <summary>Decal's own values, from the 32-bit registry where Decal keeps them; null when there are none.</summary>
        public static OverlayDecalBar ReadRegistry()
        {
            if (!OperatingSystem.IsWindows())
                return null;
            try
            {
                using Microsoft.Win32.RegistryKey machine = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32);
                using Microsoft.Win32.RegistryKey decal = machine.OpenSubKey(@"SOFTWARE\Decal");
                if (decal == null || decal.GetValue("BarLength") == null)
                    return null;
                int Read(string name, int fallback) => decal.GetValue(name) is int value ? value : fallback;
                return new OverlayDecalBar
                {
                    State = Read("BarState", 0) == 1 ? 1 : 0,
                    Dock = Math.Clamp(Read("BarDock", 0), 0, 2),
                    Start = Math.Max(0, Read("BarStart", 0)),
                    Length = Math.Max(112, Read("BarLength", 250)),
                };
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is System.IO.IOException)
            {
                return null;
            }
        }
    }

    /// <summary>A plugin's own button on a view's title bar. Pressing it sends "press" naming it.</summary>
    public sealed class OverlayTitleButton
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("image")]
        public string Image { get; set; } = string.Empty;

        [JsonPropertyName("image_down")]
        public string ImageDown { get; set; } = string.Empty;

        [JsonPropertyName("tooltip")]
        public string Tooltip { get; set; } = string.Empty;
    }

    /// <summary>
    /// One control in a view. A single shape for every kind, with the fields a kind does not
    /// use left at their defaults, because the DLL's parser is simplest reading one shape.
    /// </summary>
    public sealed class OverlayViewControl
    {
        /// <summary>One of <see cref="ViewControlTypes"/>.</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>The name in the XML, and what comes back as a command's control id.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("x")]
        public int X { get; set; }

        [JsonPropertyName("y")]
        public int Y { get; set; }

        [JsonPropertyName("w")]
        public int W { get; set; }

        [JsonPropertyName("h")]
        public int H { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        /// <summary>0xAARRGGBB, or -1 for the theme's colour. Decal writes a COLORREF; the host converts.</summary>
        [JsonPropertyName("text_color")]
        public long TextColor { get; set; } = -1;

        /// <summary>In Decal pixels, or 0 for the theme's size.</summary>
        [JsonPropertyName("font_size")]
        public int FontSize { get; set; }

        [JsonPropertyName("bold")]
        public bool Bold { get; set; }

        /// <summary>A one-pixel black shadow under a label's text.</summary>
        [JsonPropertyName("shadow")]
        public bool Shadow { get; set; }

        /// <summary>"left", "center" or "right"; empty for the kind's default.</summary>
        [JsonPropertyName("justify")]
        public string Justify { get; set; } = string.Empty;

        /// <summary>
        /// An image key: a Button's face, or an Edit's background. Empty for the theme's.
        /// </summary>
        [JsonPropertyName("image")]
        public string Image { get; set; } = string.Empty;

        [JsonPropertyName("checked")]
        public bool Checked { get; set; }

        /// <summary>An Edit's text, or a Slider's or Progress's number.</summary>
        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        /// <summary>A Choice's options.</summary>
        [JsonPropertyName("options")]
        public List<string> Options { get; set; } = new List<string>();

        /// <summary>A Choice's chosen option, or a Notebook's page; -1 for none.</summary>
        [JsonPropertyName("selected")]
        public int Selected { get; set; } = -1;

        [JsonPropertyName("min")]
        public double Min { get; set; }

        [JsonPropertyName("max")]
        public double Max { get; set; } = 100.0;

        [JsonPropertyName("vertical")]
        public bool Vertical { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("visible")]
        public bool Visible { get; set; } = true;

        /// <summary>A Notebook's pages, in tab order.</summary>
        [JsonPropertyName("pages")]
        public List<OverlayViewPage> Pages { get; set; } = new List<OverlayViewPage>();

        /// <summary>A FixedLayout's controls, in drawing order.</summary>
        [JsonPropertyName("children")]
        public List<OverlayViewControl> Children { get; set; } = new List<OverlayViewControl>();

        /// <summary>A List's columns.</summary>
        [JsonPropertyName("columns")]
        public List<OverlayViewColumn> Columns { get; set; } = new List<OverlayViewColumn>();

        /// <summary>A List's rows. Each row has one cell per column; a short row leaves the rest blank.</summary>
        [JsonPropertyName("rows")]
        public List<OverlayViewRow> Rows { get; set; } = new List<OverlayViewRow>();
    }

    public sealed class OverlayViewPage
    {
        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public OverlayViewControl Content { get; set; }
    }

    public sealed class OverlayViewColumn
    {
        /// <summary>One of <see cref="ViewColumnTypes"/>.</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = ViewColumnTypes.Text;

        /// <summary>In Decal pixels; 0 shares what the fixed columns leave.</summary>
        [JsonPropertyName("width")]
        public int Width { get; set; }
    }

    public sealed class OverlayViewRow
    {
        [JsonPropertyName("cells")]
        public List<OverlayViewCell> Cells { get; set; } = new List<OverlayViewCell>();
    }

    public sealed class OverlayViewCell
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("checked")]
        public bool Checked { get; set; }

        /// <summary>An image key, for an icon column.</summary>
        [JsonPropertyName("image")]
        public string Image { get; set; } = string.Empty;

        /// <summary>0xAARRGGBB, or -1 for the theme's list text colour.</summary>
        [JsonPropertyName("color")]
        public long Color { get; set; } = -1;
    }

    /// <summary>
    /// An image the overlay can draw, sent once in a frame of its own rather than inside a
    /// snapshot: snapshots go out several times a second, images once per connection.
    /// </summary>
    /// <remarks>
    /// Keys name where the pixels came from - "portal:0600126F" for the client's own art,
    /// "vvs:..." and "decal:..." for the theme files on this machine - so a key in a view
    /// means the same image to both sides without the DLL knowing how to read any of them.
    /// </remarks>
    public sealed class OverlayImage
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        /// <summary>Width times height pixels, R, G, B, A, base64.</summary>
        [JsonPropertyName("rgba")]
        public string Rgba { get; set; } = string.Empty;
    }

    /// <summary>A key the overlay catches for a plugin: pressed with exactly these modifiers, it becomes a "hotkey" command.</summary>
    public sealed class OverlayHotkey
    {
        [JsonPropertyName("owner")]
        public string Owner { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>The virtual-key code.</summary>
        [JsonPropertyName("key")]
        public int Key { get; set; }

        [JsonPropertyName("ctrl")]
        public bool Ctrl { get; set; }

        [JsonPropertyName("shift")]
        public bool Shift { get; set; }

        [JsonPropertyName("alt")]
        public bool Alt { get; set; }
    }

    /// <summary>The frame an image travels in: an object with one "image" member.</summary>
    public sealed class OverlayImageFrame
    {
        [JsonPropertyName("image")]
        public OverlayImage Image { get; set; }
    }

    /// <summary>
    /// The keys the host wants held down in the game, by Windows virtual-key code. The whole
    /// set every time, never a change, so a lost or repeated frame cannot leave a key stuck:
    /// the overlay presses what is new, releases what has gone, and releases everything if
    /// the host stops repeating itself.
    /// </summary>
    public sealed class OverlayInput
    {
        [JsonPropertyName("held")]
        public List<int> Held { get; set; } = new List<int>();

        /// <summary>Counts up with each frame, so a repeat is told from a new instruction in a log.</summary>
        [JsonPropertyName("sequence")]
        public long Sequence { get; set; }
    }

    /// <summary>The frame keys travel in: an object with one "input" member.</summary>
    public sealed class OverlayInputFrame
    {
        [JsonPropertyName("input")]
        public OverlayInput Input { get; set; }
    }

    /// <summary>Everything the overlay needs in order to draw one frame.</summary>
    public sealed class OverlayState
    {
        [JsonPropertyName("status")]
        public OverlayStatus Status { get; set; } = new OverlayStatus();

        /// <summary>
        /// Every window there is, in bar order. A panel whose owner has no entry here
        /// still gets a window, so an older host that sends none behaves as before.
        /// </summary>
        [JsonPropertyName("windows")]
        public List<OverlayWindow> Windows { get; set; } = new List<OverlayWindow>();

        [JsonPropertyName("panels")]
        public List<OverlayPanel> Panels { get; set; } = new List<OverlayPanel>();

        /// <summary>Every key bound to a plugin's hotkey. The overlay tells the plugin, and keeps the key from the game.</summary>
        [JsonPropertyName("hotkeys")]
        public List<OverlayHotkey> Hotkeys { get; set; } = new List<OverlayHotkey>();

        /// <summary>
        /// VVS's primary theme on this machine, for a VVS window with no theme of its own; the
        /// player can step it on with the VVS bar's "ab" square.
        /// </summary>
        [JsonPropertyName("default_theme")]
        public string DefaultTheme { get; set; } = string.Empty;

        /// <summary>Decal's bar as the player left it in the standard client; absent when unknown.</summary>
        [JsonPropertyName("decal_bar")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OverlayDecalBar DecalBar { get; set; }

        /// <summary>
        /// Who wants the next key the player presses, or empty: the overlay catches it, keeps it
        /// from the game and sends "key-captured" to that owner with "vk,ctrl,shift,alt", or
        /// "cancel" for Escape.
        /// </summary>
        [JsonPropertyName("key_capture")]
        public string KeyCapture { get; set; } = string.Empty;

        /// <summary>
        /// Moves only when the snapshot differs from the last one published, so the
        /// overlay can tell a stale frame from a quiet session and redraw on the change.
        /// <see cref="OverlayServer.Publish"/> fills this in; callers leave it alone.
        /// </summary>
        /// <summary>
        /// Bumped only when a published snapshot actually differs. Zero means nothing has
        /// been published yet.
        /// </summary>
        /// <remarks>
        /// Signed, like every other number here. JSON has no integer types, so a mixture
        /// would mean two readers on the receiving side for no benefit.
        /// </remarks>
        [JsonPropertyName("revision")]
        public long Revision { get; set; }

        /// <summary>Unix milliseconds at the moment of publishing.</summary>
        /// <remarks>
        /// A revision that stops moving says the host went quiet; it cannot distinguish a
        /// host that hung from one with nothing to report. The receiver cannot keep its own
        /// clock without holding state it is designed not to hold, so the time travels
        /// with the snapshot.
        /// </remarks>
        [JsonPropertyName("published_ms")]
        public long PublishedMs { get; set; }
    }

    /// <summary>
    /// Something the player did in the overlay. One per action; what any of them mean is
    /// entirely the host's business.
    /// </summary>
    public sealed class OverlayCommand
    {
        /// <summary>"toggle-acting", "toggle-looting", "reload-profile", and so on.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>An argument, where the command takes one. Empty otherwise.</summary>
        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// The control acted on, when the command came from one. The name is then "set"
        /// with the new value, or "press" for a button. Empty when the command did not
        /// come from a control.
        /// </summary>
        [JsonPropertyName("control_id")]
        public string ControlId { get; set; } = string.Empty;

        /// <summary>The row the command came from, when it came from one.</summary>
        [JsonPropertyName("row_id")]
        public string RowId { get; set; } = string.Empty;

        /// <summary>
        /// The plugin whose window the command came from. Empty means the host's own
        /// window. It is what the host routes on, so a click reaches the one plugin it
        /// belongs to and no other.
        /// </summary>
        [JsonPropertyName("owner")]
        public string Owner { get; set; } = string.Empty;
    }
}
