using System;
using System.Collections.Generic;
using System.Globalization;

namespace AC.Host.Plugins.Views
{
    /// <summary>
    /// One control in a Decal view: what the XML said about it, and whatever the plugin
    /// or the player has changed since.
    /// </summary>
    /// <remarks>
    /// Controls are made by <see cref="DecalView.Parse"/> and by nothing else, because a
    /// control's place in the tree and its name are what commands are routed by, and a
    /// control built outside a view would have neither. What can change afterwards is
    /// state - text, ticks, rows, positions - through plain properties the host reads
    /// each time it publishes.
    ///
    /// <para>
    /// Everything here is touched on the game thread only; see <see cref="DecalView"/>.
    /// </para>
    /// </remarks>
    public abstract class ViewControl
    {
        private protected ViewControl(string progId, string name)
        {
            ProgId = progId ?? string.Empty;
            Name = name ?? string.Empty;
        }

        /// <summary>The COM ProgID the XML named, such as "DecalControls.Checkbox".</summary>
        public string ProgId { get; }

        /// <summary>
        /// The name in the XML, and what a command's control id names. Empty for a control
        /// the XML left unnamed, which can be drawn but never looked up or clicked.
        /// </summary>
        public string Name { get; }

        /// <summary>In Decal pixels, relative to the parent layout.</summary>
        public int Left { get; set; }

        public int Top { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public bool Visible { get; set; } = true;

        /// <summary>
        /// False to grey the control out. A command arriving for a disabled control is
        /// refused rather than applied, since the player can only have sent it before the
        /// plugin disabled it.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>The kind and name together, as a plugin author would want them in a message.</summary>
        public override string ToString()
            => Name.Length > 0 ? $"{KindName} '{Name}'" : $"an unnamed {KindName}";

        /// <summary>What this kind of control is called in messages.</summary>
        internal virtual string KindName => GetType().Name;

        /// <summary>The controls directly inside this one, in drawing order.</summary>
        internal virtual IEnumerable<ViewControl> ChildControls => Array.Empty<ViewControl>();

        /// <summary>
        /// Applies something the player did to this control: the model first, then the
        /// event, so a handler that reads the control sees the new state.
        /// </summary>
        /// <remarks>
        /// Returns false with a reason, rather than throwing, for anything the player's
        /// side got wrong - the wrong verb, a value that does not parse, an index out of
        /// range - because those are counted as unhandled, not as faults. An exception from
        /// a plugin's own handler is a fault, and is left to propagate.
        /// </remarks>
        internal virtual bool Apply(OverlayCommand command, out string refusal)
        {
            refusal = $"{this} takes no commands";
            return false;
        }

        private protected bool WrongVerb(OverlayCommand command, out string refusal, string expected)
        {
            refusal = $"{this} answers '{expected}', not '{command.Name}'";
            return false;
        }

        private protected static bool TryParseIndex(string value, out int index)
            => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
    }

    /// <summary>
    /// DecalControls.FixedLayout: controls at absolute positions within it.
    /// </summary>
    public sealed class FixedLayout : ViewControl
    {
        private readonly List<ViewControl> _children = new List<ViewControl>();

        internal FixedLayout(string progId, string name)
            : base(progId, name)
        {
        }

        /// <summary>In drawing order, which is the order of the XML: later ones are on top.</summary>
        public IReadOnlyList<ViewControl> Children => _children;

        internal override IEnumerable<ViewControl> ChildControls => _children;

        internal void Add(ViewControl child) => _children.Add(child);
    }

    /// <summary>One tab of a <see cref="Notebook"/>.</summary>
    public sealed class NotebookPage
    {
        private string _label;

        internal NotebookPage(string label, ViewControl content)
        {
            _label = label ?? string.Empty;
            Content = content;
        }

        /// <summary>What the tab says.</summary>
        public string Label
        {
            get => _label;
            set => _label = value ?? string.Empty;
        }

        /// <summary>What the page shows - in practice always a FixedLayout.</summary>
        public ViewControl Content { get; }
    }

    /// <summary>
    /// DecalControls.Notebook: pages behind tabs, one showing at a time.
    /// </summary>
    public sealed class Notebook : ViewControl
    {
        private readonly List<NotebookPage> _pages = new List<NotebookPage>();
        private int _activePage = -1;

        internal Notebook(string progId, string name)
            : base(progId, name)
        {
        }

        /// <summary>In tab order.</summary>
        public IReadOnlyList<NotebookPage> Pages => _pages;

        /// <summary>
        /// The index of the page showing, or -1 for a notebook with no pages at all.
        /// </summary>
        /// <remarks>
        /// Refuses an index with no page behind it rather than storing it, because a
        /// notebook always shows one of its pages and there is no sensible page to show
        /// for a number that names none.
        /// </remarks>
        public int ActivePage
        {
            get => _activePage;
            set
            {
                if (value < 0 || value >= _pages.Count)
                    throw new ArgumentOutOfRangeException(nameof(value), value, $"{this} has {_pages.Count} pages.");

                _activePage = value;
            }
        }

        /// <summary>The player chose a tab. <see cref="ActivePage"/> is already set.</summary>
        public event EventHandler<PageChangedEventArgs> PageChanged;

        internal override IEnumerable<ViewControl> ChildControls
        {
            get
            {
                foreach (NotebookPage page in _pages)
                    yield return page.Content;
            }
        }

        internal void Add(NotebookPage page)
        {
            _pages.Add(page);
            if (_activePage < 0)
                _activePage = 0;
        }

        internal override bool Apply(OverlayCommand command, out string refusal)
        {
            if (command.Name != ViewVerbs.Page)
                return WrongVerb(command, out refusal, ViewVerbs.Page);

            if (!TryParseIndex(command.Value, out int page) || page < 0 || page >= _pages.Count)
            {
                refusal = $"'{command.Value}' is not a page of {this}, which has {_pages.Count}";
                return false;
            }

            _activePage = page;
            PageChanged?.Invoke(this, new PageChangedEventArgs(this, page, _pages[page].Label));
            refusal = null;
            return true;
        }
    }

    /// <summary>How a <see cref="StaticText"/> lines its text up.</summary>
    public enum ViewJustify
    {
        /// <summary>Whatever the kind of control does by default.</summary>
        Default = 0,
        Left = 1,
        Center = 2,
        Right = 3,
    }

    /// <summary>DecalControls.StaticText: a label.</summary>
    public sealed class StaticText : ViewControl
    {
        private string _text = string.Empty;

        internal StaticText(string progId, string name)
            : base(progId, name)
        {
        }

        public string Text
        {
            get => _text;
            set => _text = value ?? string.Empty;
        }

        /// <summary>
        /// 0xAARRGGBB, or null for the theme's colour. The XML's COLORREF is converted on
        /// the way in; <see cref="ViewColor"/> makes one from parts.
        /// </summary>
        public long? TextColor { get; set; }

        /// <summary>In Decal pixels, or 0 for the theme's size.</summary>
        public int FontSize { get; set; }

        public bool Bold { get; set; }

        public ViewJustify Justify { get; set; }

        /// <summary>
        /// A one-pixel black shadow under the text, as VVS drew text given a shadow size of one -
        /// the Status HUD's rows, for one.
        /// </summary>
        public bool Shadow { get; set; }
    }

    /// <summary>DecalControls.Checkbox: a tick box with a label.</summary>
    public sealed class Checkbox : ViewControl
    {
        private string _text = string.Empty;

        internal Checkbox(string progId, string name)
            : base(progId, name)
        {
        }

        public string Text
        {
            get => _text;
            set => _text = value ?? string.Empty;
        }

        public bool Checked { get; set; }

        /// <summary>The player ticked or cleared it. <see cref="Checked"/> is already set.</summary>
        public event EventHandler<CheckboxChangedEventArgs> Changed;

        internal override bool Apply(OverlayCommand command, out string refusal)
        {
            if (command.Name != ViewVerbs.Set)
                return WrongVerb(command, out refusal, ViewVerbs.Set);

            // Only exactly "true" is on, the same reading OverlayControl.ParseToggle gives:
            // off is the safe way to misread a switch that may reach into the game.
            Checked = string.Equals(command.Value, "true", StringComparison.Ordinal);
            Changed?.Invoke(this, new CheckboxChangedEventArgs(this, Checked));
            refusal = null;
            return true;
        }
    }

    /// <summary>DecalControls.PushButton: a framed button with text on it.</summary>
    public sealed class PushButton : ViewControl
    {
        private string _text = string.Empty;

        internal PushButton(string progId, string name)
            : base(progId, name)
        {
        }

        public string Text
        {
            get => _text;
            set => _text = value ?? string.Empty;
        }

        public event EventHandler<ViewEventArgs> Clicked;

        internal override bool Apply(OverlayCommand command, out string refusal)
        {
            if (command.Name != ViewVerbs.Press)
                return WrongVerb(command, out refusal, ViewVerbs.Press);

            Clicked?.Invoke(this, new ViewEventArgs(this));
            refusal = null;
            return true;
        }
    }

    /// <summary>
    /// DecalControls.Button: a button drawn as an image, with no text.
    /// </summary>
    /// <remarks>
    /// Named for what it is rather than for its progid, because "Button" beside
    /// <see cref="PushButton"/> says nothing about which of the two has the picture.
    /// </remarks>
    public sealed class ImageButton : ViewControl
    {
        private string _imageKey = string.Empty;

        internal ImageButton(string progId, string name)
            : base(progId, name)
        {
        }

        /// <summary>An image key such as "portal:06001276", or empty for the theme's.</summary>
        public string ImageKey
        {
            get => _imageKey;
            set => _imageKey = value ?? string.Empty;
        }

        /// <summary>
        /// Shows one of the client's own images, by portal id - what Decal's
        /// <c>ButtonWrapper.SetImages</c> was given.
        /// </summary>
        public void SetPortalImage(uint id) => ImageKey = ViewImages.Portal(id);

        public event EventHandler<ViewEventArgs> Clicked;

        internal override bool Apply(OverlayCommand command, out string refusal)
        {
            if (command.Name != ViewVerbs.Press)
                return WrongVerb(command, out refusal, ViewVerbs.Press);

            Clicked?.Invoke(this, new ViewEventArgs(this));
            refusal = null;
            return true;
        }
    }

    /// <summary>DecalControls.Edit: one line of text the player can type in.</summary>
    public sealed class Edit : ViewControl
    {
        private string _text = string.Empty;
        private string _imageKey = string.Empty;

        internal Edit(string progId, string name)
            : base(progId, name)
        {
        }

        public string Text
        {
            get => _text;
            set => _text = value ?? string.Empty;
        }

        /// <summary>The box's background, from the XML's imageportalsrc. Empty for the theme's.</summary>
        public string ImageKey
        {
            get => _imageKey;
            set => _imageKey = value ?? string.Empty;
        }

        /// <summary>
        /// The player committed a new value. Raised once per edit, not per keystroke:
        /// the overlay sends the text when the player is done with it.
        /// </summary>
        public event EventHandler<EditChangedEventArgs> Changed;

        internal override bool Apply(OverlayCommand command, out string refusal)
        {
            if (command.Name != ViewVerbs.Set)
                return WrongVerb(command, out refusal, ViewVerbs.Set);

            Text = command.Value;
            Changed?.Invoke(this, new EditChangedEventArgs(this, Text));
            refusal = null;
            return true;
        }
    }

    /// <summary>One entry in a <see cref="Choice"/>.</summary>
    public sealed class ChoiceOption
    {
        internal ChoiceOption(string text, string data)
        {
            Text = text ?? string.Empty;
            Data = data ?? string.Empty;
        }

        /// <summary>What the dropdown shows.</summary>
        public string Text { get; }

        /// <summary>Whatever the plugin wants to keep with it. Never sent to the overlay.</summary>
        public string Data { get; }
    }

    /// <summary>DecalControls.Choice: a dropdown.</summary>
    public sealed class Choice : ViewControl
    {
        private readonly List<ChoiceOption> _options = new List<ChoiceOption>();
        private int _selected = -1;

        internal Choice(string progId, string name)
            : base(progId, name)
        {
        }

        public IReadOnlyList<ChoiceOption> Options => _options;

        public int Count => _options.Count;

        /// <summary>The index of the chosen option, or -1 for none.</summary>
        /// <remarks>
        /// Refuses an index with no option behind it rather than storing it, so the
        /// dropdown never claims a choice it cannot show.
        /// </remarks>
        public int Selected
        {
            get => _selected;
            set
            {
                if (value < -1 || value >= _options.Count)
                    throw new ArgumentOutOfRangeException(nameof(value), value, $"{this} has {_options.Count} options.");

                _selected = value;
            }
        }

        /// <summary>The chosen option's text, or empty when none is chosen.</summary>
        public string SelectedText => _selected >= 0 ? _options[_selected].Text : string.Empty;

        /// <summary>The player chose an option. <see cref="Selected"/> is already set.</summary>
        public event EventHandler<ChoiceChangedEventArgs> Changed;

        public void Add(string text, string data = null) => _options.Add(new ChoiceOption(text, data));

        /// <summary>Adds an option at <paramref name="index"/>, moving the rest down. The chosen option stays chosen.</summary>
        public void Insert(int index, string text, string data = null)
        {
            _options.Insert(index, new ChoiceOption(text, data));
            if (_selected >= index)
                _selected++;
        }

        /// <summary>Removes one option. Removing the chosen one leaves nothing chosen.</summary>
        public void RemoveAt(int index)
        {
            _options.RemoveAt(index);
            if (_selected == index)
                _selected = -1;
            else if (_selected > index)
                _selected--;
        }

        /// <summary>Changes what an option says, keeping its data and whether it is chosen.</summary>
        public void SetText(int index, string text) => _options[index] = new ChoiceOption(text, _options[index].Data);

        /// <summary>Removes every option, which leaves nothing chosen.</summary>
        public void Clear()
        {
            _options.Clear();
            _selected = -1;
        }

        internal override bool Apply(OverlayCommand command, out string refusal)
        {
            if (command.Name != ViewVerbs.Set)
                return WrongVerb(command, out refusal, ViewVerbs.Set);

            // An index rather than the option's text, because two options may read the
            // same and the plugin's options can change between the publish and the click.
            if (!TryParseIndex(command.Value, out int index) || index < 0 || index >= _options.Count)
            {
                refusal = $"'{command.Value}' is not an option of {this}, which has {_options.Count}";
                return false;
            }

            _selected = index;
            Changed?.Invoke(this, new ChoiceChangedEventArgs(this, index, _options[index].Text));
            refusal = null;
            return true;
        }
    }

    /// <summary>DecalControls.Slider.</summary>
    public sealed class Slider : ViewControl
    {
        internal Slider(string progId, string name)
            : base(progId, name)
        {
        }

        public double Minimum { get; set; }

        public double Maximum { get; set; } = 100;

        /// <summary>
        /// Where the thumb is. Not clamped when set, so the range and the position can be
        /// changed in either order; the player's changes are clamped when they arrive.
        /// </summary>
        public double Position { get; set; }

        public bool Vertical { get; set; }

        /// <summary>The player let go of the slider. <see cref="Position"/> is already set.</summary>
        public event EventHandler<SliderChangedEventArgs> Changed;

        internal override bool Apply(OverlayCommand command, out string refusal)
        {
            if (command.Name != ViewVerbs.Set)
                return WrongVerb(command, out refusal, ViewVerbs.Set);

            // The overlay writes numbers in the invariant form whatever the machine's
            // culture, so that is the only form read back. NaN parses but means nothing.
            if (!double.TryParse(command.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || double.IsNaN(value))
            {
                refusal = $"'{command.Value}' is not a number for {this}";
                return false;
            }

            // Min and Max rather than Math.Clamp, which throws on a range a plugin has
            // left upside down; a slider set wrongly should still take a click.
            Position = Math.Min(Math.Max(value, Minimum), Maximum);
            Changed?.Invoke(this, new SliderChangedEventArgs(this, Position));
            refusal = null;
            return true;
        }
    }

    /// <summary>DecalControls.Progress: a bar the plugin fills. The player cannot change it.</summary>
    public sealed class Progress : ViewControl
    {
        internal Progress(string progId, string name)
            : base(progId, name)
        {
        }

        public double Minimum { get; set; }

        public double Maximum { get; set; } = 100;

        public double Value { get; set; }
    }

    /// <summary>
    /// A control whose progid this host does not know. Kept so the tree matches the XML
    /// and its name can still be found, but it is not drawn.
    /// </summary>
    public sealed class UnknownControl : ViewControl
    {
        internal UnknownControl(string progId, string name)
            : base(progId, name)
        {
        }

        internal override string KindName => ProgId.Length > 0 ? ProgId : "control with no progid";
    }

    /// <summary>
    /// Image keys for the client's own art, as both ends of the overlay pipe spell them.
    /// </summary>
    public static class ViewImages
    {
        /// <summary>
        /// The key for a portal image: "portal:" and the full id in eight hex digits.
        /// </summary>
        /// <remarks>
        /// Accepts the full id (0x06001276) or only its low part (4726, which is how
        /// Decal's XML writes it), because both conventions are in use and nothing in the
        /// portal file below 0x06000000 is an image. Zero means no image.
        /// </remarks>
        public static string Portal(uint id)
        {
            if (id == 0)
                return string.Empty;

            uint full = id <= 0x00FFFFFF ? 0x06000000u | id : id;
            return "portal:" + full.ToString("X8", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Colours as a view holds them: 0xAARRGGBB in a long.</summary>
    public static class ViewColor
    {
        /// <summary>An opaque colour from its parts.</summary>
        public static long Rgb(byte red, byte green, byte blue)
            => 0xFF000000L | ((long)red << 16) | ((long)green << 8) | blue;

        /// <summary>
        /// From a Windows COLORREF, 0x00BBGGRR, which is what Decal's XML writes - so 192
        /// is a dark red, not a dark blue.
        /// </summary>
        public static long FromColorRef(long colorRef)
            => Rgb((byte)(colorRef & 0xFF), (byte)((colorRef >> 8) & 0xFF), (byte)((colorRef >> 16) & 0xFF));
    }
}
