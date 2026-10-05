using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using AC.Host.Plugins.Views;
using Decal.Adapter.Hosting;

namespace Decal.Adapter.Wrappers
{
    // Decal's control wrappers, each over the host's control of the same kind. State goes
    // straight to the host's control, which the overlay draws; the player's changes come back
    // as the host control's events and leave as Decal's, with the control's id, so a plugin's
    // handlers see exactly what they did under Decal.
    //
    // Destroy and a few others are never raised - nothing here destroys a control behind a
    // plugin's back - but plugins subscribe to them, so they exist.

    /// <summary>
    /// What every wrapper shares: the control it wraps and the id it answers to.
    /// </summary>
    /// <remarks>
    /// Decal made this generic over the COM class it wrapped; here the type argument is the
    /// host's control type, which plays the same part.
    /// </remarks>
    public abstract class ControlWrapperBase<T> : MarshalByRefObject, IControlWrapper, IDisposable
        where T : ViewControl
    {
        private HostedView _hosted;

        protected ControlWrapperBase()
        {
        }

        public object Underlying => Control;

        public int Id => _hosted?.IdOf(Control) ?? 0;

        public int ChildCount => Control is FixedLayout layout ? layout.Children.Count : Control is Notebook notebook ? notebook.Pages.Count : 0;

        protected bool Disposed { get; private set; }

        protected T Control { get; private set; }

        internal HostedView Hosted => _hosted;

        /// <summary>Takes the control to wrap. The runtime calls this; plugins had no need to.</summary>
        public virtual void Initialize(object control)
        {
            Control = control as T ?? throw new ArgumentException($"{GetType().Name} wraps a {typeof(T).Name}, not {control?.GetType().Name ?? "nothing"}.", nameof(control));
        }

        internal void Bind(HostedView hosted, T control)
        {
            _hosted = hosted;
            Initialize(control);
            Subscribe();
        }

        /// <summary>Hooks the host control's events, once, when the wrapper is bound.</summary>
        private protected virtual void Subscribe()
        {
        }

        private protected virtual void Unsubscribe()
        {
        }

        public object ChildById(int id)
            => _hosted?.View.Controls.FirstOrDefault(c => _hosted.IdOf(c) == id);

        public object ChildByIndex(int index)
        {
            if (Control is FixedLayout layout && index >= 0 && index < layout.Children.Count)
                return layout.Children[index];

            if (Control is Notebook notebook && index >= 0 && index < notebook.Pages.Count)
                return notebook.Pages[index].Content;

            return null;
        }

        public void Dispose()
        {
            if (Disposed)
                return;

            Dispose(true);
            Disposed = true;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing && Control != null)
                Unsubscribe();
        }

        private protected static long ToViewColor(Color color) => unchecked((uint)color.ToArgb());

        private protected static Color FromViewColor(long? color, Color fallback)
            => color.HasValue ? Color.FromArgb(unchecked((int)color.Value)) : fallback;
    }

    /// <summary>Anything without a wrapper of its own - a layout, a control this host does not draw.</summary>
    public class ControlWrapper : ControlWrapperBase<ViewControl>
    {
        public ControlWrapper()
        {
        }
    }

    /// <summary>DecalControls.PushButton.</summary>
    public class PushButtonWrapper : ControlWrapperBase<PushButton>
    {
        public PushButtonWrapper()
        {
        }

        public string Text
        {
            get => Control.Text;
            set => Control.Text = value;
        }

        /// <summary>Kept for the plugin to read back; the overlay draws buttons in the theme's colours.</summary>
        public Color TextColor { get; set; } = Color.White;

        public Color FaceColor { get; set; }

        /// <summary>The button was pressed.</summary>
        public event EventHandler<ControlEventArgs> Hit;

        /// <summary>The button was released over itself - a click.</summary>
        public event EventHandler<ControlEventArgs> Click;

#pragma warning disable CS0067
        public event EventHandler<ControlEventArgs> Unhit;

        public event EventHandler<ControlEventArgs> Canceled;

        public event EventHandler<ControlEventArgs> Destroy;
#pragma warning restore CS0067

        private protected override void Subscribe() => Control.Clicked += OnClicked;

        private protected override void Unsubscribe() => Control.Clicked -= OnClicked;

        /// <summary>
        /// The overlay reports a whole press, so a plugin hears what Decal told it of one: the
        /// button going down, then the click.
        /// </summary>
        private void OnClicked(object sender, ViewEventArgs e)
        {
            ControlEventArgs args = new ControlEventArgs(Id);
            Hit?.Invoke(this, args);
            Click?.Invoke(this, args);
        }
    }

    /// <summary>DecalControls.Button: an image that is a button.</summary>
    public class ButtonWrapper : ControlWrapperBase<ImageButton>
    {
        public ButtonWrapper()
        {
        }

        public Color Matte { get; set; }

        public int Background { get; set; }

        public event EventHandler<ControlEventArgs> Hit;

        public event EventHandler<ControlEventArgs> Click;

#pragma warning disable CS0067
        public event EventHandler<ControlEventArgs> Unhit;

        public event EventHandler<ControlEventArgs> Canceled;

        public event EventHandler<ControlEventArgs> Destroy;
#pragma warning restore CS0067

        /// <summary>The button's face, from the client's portal file. The pressed image is not drawn.</summary>
        public void SetImages(int released, int pressed) => Control.SetPortalImage(unchecked((uint)released));

        /// <summary>
        /// The button's face from a module's resources, which cannot be drawn here - unless the
        /// module is zero, which Decal took to mean the portal file.
        /// </summary>
        public void SetImages(int module, int released, int pressed)
        {
            if (module == 0)
                SetImages(released, pressed);
        }

        private protected override void Subscribe() => Control.Clicked += OnClicked;

        private protected override void Unsubscribe() => Control.Clicked -= OnClicked;

        private void OnClicked(object sender, ViewEventArgs e)
        {
            ControlEventArgs args = new ControlEventArgs(Id);
            Hit?.Invoke(this, args);
            Click?.Invoke(this, args);
        }
    }

    /// <summary>DecalControls.Checkbox.</summary>
    public class CheckBoxWrapper : ControlWrapperBase<Checkbox>
    {
        public CheckBoxWrapper()
        {
        }

        public bool Checked
        {
            get => Control.Checked;
            set => Control.Checked = value;
        }

        public string Text
        {
            get => Control.Text;
            set => Control.Text = value;
        }

        public Color TextColor { get; set; } = Color.White;

        public bool RightToLeft { get; set; }

        public event EventHandler<CheckBoxChangeEventArgs> Change;

#pragma warning disable CS0067
        public event EventHandler<ControlEventArgs> Destroy;
#pragma warning restore CS0067

        private protected override void Subscribe() => Control.Changed += OnChanged;

        private protected override void Unsubscribe() => Control.Changed -= OnChanged;

        private void OnChanged(object sender, CheckboxChangedEventArgs e) => Change?.Invoke(this, new CheckBoxChangeEventArgs(Id, e.Checked));
    }

    /// <summary>DecalControls.Edit.</summary>
    public class TextBoxWrapper : ControlWrapperBase<Edit>
    {
        public TextBoxWrapper()
        {
        }

        public string Text
        {
            get => Control.Text;
            set => Control.Text = value;
        }

        public Color TextColor { get; set; } = Color.White;

        public int Caret { get; set; }

        public string SelectedText { get; set; } = string.Empty;

        public event EventHandler<TextBoxChangeEventArgs> Change;

        public event EventHandler<TextBoxEndEventArgs> End;

#pragma warning disable CS0067
        public event EventHandler<ControlEventArgs> Begin;

        public event EventHandler<ControlEventArgs> Destroy;
#pragma warning restore CS0067

        /// <summary>Gives the box the keyboard. The overlay owns the keyboard, so this does nothing.</summary>
        public void Capture()
        {
        }

        public void Select(int start, int end)
        {
        }

        public void SetMargins(int x, int y)
        {
        }

        private protected override void Subscribe() => Control.Changed += OnChanged;

        private protected override void Unsubscribe() => Control.Changed -= OnChanged;

        /// <summary>
        /// The overlay sends a text box's text when the player is done with it, so a plugin
        /// hears the change and then the end of editing, as Decal told it when enter was pressed.
        /// </summary>
        private void OnChanged(object sender, EditChangedEventArgs e)
        {
            Change?.Invoke(this, new TextBoxChangeEventArgs(Id, e.Text));
            End?.Invoke(this, new TextBoxEndEventArgs(Id, true));
        }
    }

    /// <summary>DecalControls.Choice: a dropdown.</summary>
    public class ChoiceWrapper : ControlWrapperBase<Choice>
    {
        private readonly List<object> _data = new List<object>();

        public ChoiceWrapper()
        {
        }

        public int Count => Control.Count;

        public int Selected
        {
            get => Control.Selected;
            set => Control.Selected = value;
        }

        public ChoiceTextIndexer Text => new ChoiceTextIndexer(this);

        public ChoiceDataIndexer Data => new ChoiceDataIndexer(this);

        public int DropLines { get; set; } = 8;

        public bool Dropped { get; set; }

        public event EventHandler<IndexChangeEventArgs> Change;

#pragma warning disable CS0067
        public event EventHandler<ControlEventArgs> DropDown;

        public event EventHandler<ControlEventArgs> Destroy;
#pragma warning restore CS0067

        /// <summary>
        /// Takes the control, and the data its options came with from the XML: Decal's data is
        /// any object, the host's a string, so each option's data is kept here and the XML's
        /// strings are where it starts.
        /// </summary>
        public override void Initialize(object control)
        {
            base.Initialize(control);
            _data.Clear();
            foreach (ChoiceOption option in Control.Options)
                _data.Add(option.Data.Length > 0 ? option.Data : null);
        }

        public void Add(string display, object data)
        {
            Control.Add(display, data as string);
            _data.Add(data);
        }

        public void Remove(int index)
        {
            Control.RemoveAt(index);
            _data.RemoveAt(index);
        }

        public void Clear()
        {
            Control.Clear();
            _data.Clear();
        }

        internal string GetText(int index) => Control.Options[index].Text;

        internal void SetText(int index, string text) => Control.SetText(index, text);

        internal object GetData(int index) => _data[index];

        internal void SetData(int index, object data) => _data[index] = data;

        private protected override void Subscribe() => Control.Changed += OnChanged;

        private protected override void Unsubscribe() => Control.Changed -= OnChanged;

        private void OnChanged(object sender, ChoiceChangedEventArgs e) => Change?.Invoke(this, new IndexChangeEventArgs(Id, e.Selected));
    }

    public sealed class ChoiceTextIndexer : IDisposable
    {
        private readonly ChoiceWrapper _choice;

        internal ChoiceTextIndexer(ChoiceWrapper choice)
        {
            _choice = choice;
        }

        public string this[int index]
        {
            get => _choice.GetText(index);
            set => _choice.SetText(index, value);
        }

        public void Dispose()
        {
        }
    }

    public sealed class ChoiceDataIndexer : IDisposable
    {
        private readonly ChoiceWrapper _choice;

        internal ChoiceDataIndexer(ChoiceWrapper choice)
        {
            _choice = choice;
        }

        public object this[int index]
        {
            get => _choice.GetData(index);
            set => _choice.SetData(index, value);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>DecalControls.Slider.</summary>
    public class SliderWrapper : ControlWrapperBase<Slider>
    {
        public SliderWrapper()
        {
        }

        public int Minimum
        {
            get => (int)Control.Minimum;
            set => Control.Minimum = value;
        }

        public int Maximum
        {
            get => (int)Control.Maximum;
            set => Control.Maximum = value;
        }

        /// <summary>Decal's own spelling, which plugins used.</summary>
        public int SliderPostition
        {
            get => (int)Math.Round(Control.Position);
            set => Control.Position = value;
        }

        public int Position
        {
            get => SliderPostition;
            set => SliderPostition = value;
        }

        public Color TextColor { get; set; } = Color.White;

        public event EventHandler<IndexChangeEventArgs> Change;

#pragma warning disable CS0067
        public event EventHandler<ControlEventArgs> Destroy;
#pragma warning restore CS0067

        private protected override void Subscribe() => Control.Changed += OnChanged;

        private protected override void Unsubscribe() => Control.Changed -= OnChanged;

        private void OnChanged(object sender, SliderChangedEventArgs e)
            => Change?.Invoke(this, new IndexChangeEventArgs(Id, (int)Math.Round(e.Position)));
    }

    /// <summary>DecalControls.List.</summary>
    public class ListWrapper : ControlWrapperBase<List>
    {
        public ListWrapper()
        {
        }

        public ListRow this[int row] => new ListRow(Control, row);

        public int RowCount => Control.RowCount;

        public int ColCount => Control.Columns.Count;

        public bool AutoScroll { get; set; }

        /// <summary>How many rows the plugin expects to add. Only ever a hint to Decal, and to nobody here.</summary>
        public int RowEstimate
        {
            set { }
        }

        public int ScrollPosition { get; set; }

        public event EventHandler<ListSelectEventArgs> Selected;

#pragma warning disable CS0067
        public event EventHandler<ControlEventArgs> Destroy;
#pragma warning restore CS0067

        public ListRow Add()
        {
            Control.Add();
            return new ListRow(Control, Control.RowCount - 1);
        }

        public ListRow Insert(int row)
        {
            Control.Insert(row);
            return new ListRow(Control, row);
        }

        public void Delete(int index) => Control.RemoveAt(index);

        public void Clear() => Control.Clear();

        public void JumpToPosition(int row) => ScrollPosition = row;

        private protected override void Subscribe() => Control.Clicked += OnClicked;

        private protected override void Unsubscribe() => Control.Clicked -= OnClicked;

        private void OnClicked(object sender, ListClickedEventArgs e) => Selected?.Invoke(this, new ListSelectEventArgs(Id, e.Row, e.Column));
    }

    /// <summary>One row of a list, addressed by column.</summary>
    public class ListRow
    {
        private readonly List _list;
        private readonly int _row;

        internal ListRow(List list, int row)
        {
            _list = list;
            _row = row;
        }

        public ListColumn this[int column] => new ListColumn(_list, _row, column);
    }

    /// <summary>
    /// One cell of a list - Decal called it a column, since a row's cells were its columns.
    /// </summary>
    /// <remarks>
    /// A cell's values were numbered: a text column's text is value 0; an icon column's icon
    /// is value 1 (value 0 was the icon's library); a check column's tick is value 0. Plugins
    /// write these by number, so they are read and written by number here.
    /// </remarks>
    public class ListColumn
    {
        private readonly List _list;
        private readonly int _row;
        private readonly int _column;

        internal ListColumn(List list, int row, int column)
        {
            _list = list;
            _row = row;
            _column = column;
        }

        private ListCell Cell => _list[_row][_column];

        private ListColumnKind Kind => _list.Columns[_column].Kind;

        public object this[int subVal]
        {
            get
            {
                switch (Kind)
                {
                    case ListColumnKind.Check:
                        return subVal == 0 ? Cell.Checked : null;
                    case ListColumnKind.Icon:
                        return subVal == 1 ? PortalId(Cell.ImageKey) : 0;
                    default:
                        return subVal == 0 ? Cell.Text : null;
                }
            }

            set
            {
                switch (Kind)
                {
                    case ListColumnKind.Check:
                        if (subVal == 0)
                            Cell.Checked = Convert.ToBoolean(value);
                        break;
                    case ListColumnKind.Icon:
                        if (subVal == 1)
                            Cell.SetPortalImage(unchecked((uint)Convert.ToInt32(value)));
                        break;
                    default:
                        if (subVal == 0)
                            Cell.Text = value?.ToString() ?? string.Empty;
                        break;
                }
            }
        }

        public Color Color
        {
            get => Cell.Color.HasValue ? Color.FromArgb(unchecked((int)Cell.Color.Value)) : Color.White;
            set => Cell.Color = unchecked((uint)value.ToArgb());
        }

        /// <summary>The column's width. The XML fixes it; a plugin's change is remembered and not drawn.</summary>
        public int Width
        {
            get => _list.Columns[_column].FixedWidth;
            set { }
        }

        private static int PortalId(string imageKey)
        {
            const string prefix = "portal:";
            return imageKey.StartsWith(prefix, StringComparison.Ordinal)
                && uint.TryParse(imageKey.AsSpan(prefix.Length), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint id)
                    ? unchecked((int)id)
                    : 0;
        }
    }

    /// <summary>DecalControls.StaticText.</summary>
    public class StaticWrapper : ControlWrapperBase<StaticText>
    {
        public StaticWrapper()
        {
        }

        public string Text
        {
            get => Control.Text;
            set => Control.Text = value;
        }

        public Color TextColor
        {
            get => FromViewColor(Control.TextColor, Color.White);
            set => Control.TextColor = ToViewColor(value);
        }

#pragma warning disable CS0067
        public event EventHandler<ControlEventArgs> Destroy;
#pragma warning restore CS0067
    }

    /// <summary>DecalControls.Notebook.</summary>
    public class NotebookWrapper : ControlWrapperBase<Notebook>
    {
        public NotebookWrapper()
        {
        }

        public int ActiveTab
        {
            get => Control.ActivePage;
            set => Control.ActivePage = value;
        }

        public PageTextIndexer PageText => new PageTextIndexer(Control);

        public event EventHandler<IndexChangeEventArgs> Change;

#pragma warning disable CS0067
        public event EventHandler<ControlEventArgs> Destroy;
#pragma warning restore CS0067

        private protected override void Subscribe() => Control.PageChanged += OnPageChanged;

        private protected override void Unsubscribe() => Control.PageChanged -= OnPageChanged;

        private void OnPageChanged(object sender, PageChangedEventArgs e) => Change?.Invoke(this, new IndexChangeEventArgs(Id, e.Page));
    }

    public sealed class PageTextIndexer : IDisposable
    {
        private readonly Notebook _notebook;

        internal PageTextIndexer(Notebook notebook)
        {
            _notebook = notebook;
        }

        public string this[int index]
        {
            get => _notebook.Pages[index].Label;
            set => _notebook.Pages[index].Label = value;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>DecalControls.Progress.</summary>
    public class ProgressWrapper : ControlWrapperBase<Progress>
    {
        public ProgressWrapper()
        {
        }

        public int Value
        {
            get => (int)Control.Value;
            set => Control.Value = value;
        }

        public int MaxValue
        {
            get => (int)Control.Maximum;
            set => Control.Maximum = value;
        }

        public string PreText { get; set; } = string.Empty;

        public string PostText { get; set; } = string.Empty;

        public bool DrawText { get; set; }

        public string Alignment { get; set; } = string.Empty;

        public int BorderWidth { get; set; }

        public Color BorderColor { get; set; }

        public Color FaceColor { get; set; }

        public Color FillColor { get; set; }

        public Color TextColor { get; set; } = Color.White;

#pragma warning disable CS0067
        public event EventHandler<ControlEventArgs> Destroy;
#pragma warning restore CS0067
    }

    /// <summary>Decal's registry of custom control wrappers. Nothing here can host a custom control.</summary>
    public static class ControlRegistry
    {
        public static void RegisterControls(System.Reflection.Assembly assembly)
        {
        }
    }
}
