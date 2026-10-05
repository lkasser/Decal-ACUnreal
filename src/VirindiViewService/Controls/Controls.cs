using System;
using System.Collections.Generic;
using System.Drawing;
using AC.Host.Plugins.Views;
using Decal.Adapter.Hosting;

namespace VirindiViewService.Controls
{
    // VVS's controls, each over the host's control of the kind the XML named - or, for one a
    // plugin made in code, the kind made for it when it was put in a view. See HudControl for
    // how state and events pass between them.

    /// <summary>
    /// A picture: in a view, drawn as an image button that nothing listens to; in a list, an
    /// icon cell; otherwise it holds its image for whoever asks.
    /// </summary>
    public class HudPictureBox : HudControl
    {
        private ACImage _image = new ACImage();
        private ListCell _cell;

        public HudPictureBox()
        {
        }

        public virtual ACImage Image
        {
            get => _cell != null ? ACImage.FromImageKey(_cell.ImageKey) : Bound is ImageButton picture ? ACImage.FromImageKey(picture.ImageKey) : _image;
            set
            {
                if (_cell != null)
                    _cell.ImageKey = value?.ToImageKey() ?? string.Empty;
                else if (Bound is ImageButton picture)
                    picture.ImageKey = value?.ToImageKey() ?? string.Empty;
                else
                    _image = value ?? new ACImage();
            }
        }

        internal static HudPictureBox ForCell(ListCell cell) => new HudPictureBox { _cell = cell };

        internal override ViewControl CreateHostControl(DecalView view, FixedLayout parent, string name, Rectangle rect)
            => view.AddControl<ImageButton>(parent, name, rect.X, rect.Y, rect.Width, rect.Height);

        private protected override void ApplyDetachedState()
        {
            base.ApplyDetachedState();
            if (Bound is ImageButton picture)
                picture.ImageKey = _image.ToImageKey();
        }
    }

    /// <summary>A button with text on it: DecalControls.PushButton.</summary>
    public class HudButton : HudPictureBox
    {
        private string _text = string.Empty;

        public HudButton()
        {
        }

        private PushButton Button => Bound as PushButton;

        public string Text
        {
            get => Button?.Text ?? _text;
            set
            {
                if (Button != null)
                    Button.Text = value;
                else
                    _text = value ?? string.Empty;
            }
        }

        public ACImage OverlayImage { get; set; }

        public ACImage ImagePressed { get; set; }

        public Rectangle OverlayImageRectangle { get; set; }

        internal override ViewControl CreateHostControl(DecalView view, FixedLayout parent, string name, Rectangle rect)
            => view.AddControl<PushButton>(parent, name, rect.X, rect.Y, rect.Width, rect.Height);

        private protected override void OnAttached()
        {
            if (Button != null)
                Button.Clicked += OnClicked;
        }

        private protected override void OnDetaching()
        {
            if (Button != null)
                Button.Clicked -= OnClicked;
        }

        private protected override void ApplyDetachedState()
        {
            base.ApplyDetachedState();
            Button.Text = _text;
        }

        private void OnClicked(object sender, ViewEventArgs e) => RaisePress();
    }

    /// <summary>A button that is only an image: DecalControls.Button.</summary>
    public class HudImageButton : HudControl
    {
        private ACImage _up = new ACImage();

        public HudImageButton()
        {
        }

        public delegate void delPressed(HudImageButton caller);

        public delegate void delStickyDownStateChanged(HudImageButton caller, bool stickystate);

        public event delPressed Pressed;

#pragma warning disable CS0067
        public event delStickyDownStateChanged StickyDownStateChanged;
#pragma warning restore CS0067

        private ImageButton Button => Bound as ImageButton;

        public bool CanSticky { get; set; }

        public bool StickyDown { get; set; }

        /// <summary>The face the overlay draws. The pressed and background images are kept but not drawn.</summary>
        public ACImage Image_Up
        {
            get => Button != null ? ACImage.FromImageKey(Button.ImageKey) : _up;
            set
            {
                if (Button != null)
                    Button.ImageKey = value?.ToImageKey() ?? string.Empty;
                else
                    _up = value ?? new ACImage();
            }
        }

        public ACImage Image_Up_Pressing { get; set; }

        public ACImage Image_Down { get; set; }

        public ACImage Image_Down_Pressing { get; set; }

        public ACImage Image_Background { get; set; }

        public ACImage Image_Background2 { get; set; }

        internal override ViewControl CreateHostControl(DecalView view, FixedLayout parent, string name, Rectangle rect)
            => view.AddControl<ImageButton>(parent, name, rect.X, rect.Y, rect.Width, rect.Height);

        private protected override void OnAttached()
        {
            if (Button != null)
                Button.Clicked += OnClicked;
        }

        private protected override void OnDetaching()
        {
            if (Button != null)
                Button.Clicked -= OnClicked;
        }

        private protected override void ApplyDetachedState()
        {
            base.ApplyDetachedState();
            Button.ImageKey = _up.ToImageKey();
        }

        private void OnClicked(object sender, ViewEventArgs e)
        {
            RaisePress();
            Pressed?.Invoke(this);
        }
    }

    /// <summary>A tick box, in a view or in a list's check column.</summary>
    public class HudCheckBox : HudControl
    {
        private bool _checked;
        private string _text = string.Empty;
        private ListCell _cell;

        public HudCheckBox()
        {
        }

        private Checkbox Box => Bound as Checkbox;

        public bool Checked
        {
            get => _cell?.Checked ?? Box?.Checked ?? _checked;
            set
            {
                if (_cell != null)
                    _cell.Checked = value;
                else if (Box != null)
                    Box.Checked = value;
                else
                    _checked = value;
            }
        }

        public string Text
        {
            get => Box?.Text ?? _text;
            set
            {
                if (Box != null)
                    Box.Text = value;
                else
                    _text = value ?? string.Empty;
            }
        }

        public bool UserChangeable { get; set; } = true;

        /// <summary>The player ticked or cleared it.</summary>
        public event EventHandler Change;

        internal static HudCheckBox ForCell(ListCell cell) => new HudCheckBox { _cell = cell };

        internal override ViewControl CreateHostControl(DecalView view, FixedLayout parent, string name, Rectangle rect)
            => view.AddControl<Checkbox>(parent, name, rect.X, rect.Y, rect.Width, rect.Height);

        private protected override void OnAttached()
        {
            if (Box != null)
                Box.Changed += OnChanged;
        }

        private protected override void OnDetaching()
        {
            if (Box != null)
                Box.Changed -= OnChanged;
        }

        private protected override void ApplyDetachedState()
        {
            base.ApplyDetachedState();
            Box.Checked = _checked;
            Box.Text = _text;
        }

        private void OnChanged(object sender, CheckboxChangedEventArgs e) => Change?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A line of text the player types in: DecalControls.Edit.</summary>
    public class HudTextBox : HudPictureBox
    {
        private string _text = string.Empty;

        public HudTextBox()
        {
        }

        private Edit Box => Bound as Edit;

        public string Text
        {
            get => Box?.Text ?? _text;
            set
            {
                if (Box != null)
                    Box.Text = value;
                else
                    _text = value ?? string.Empty;
            }
        }

        public bool UserChangeable { get; set; } = true;

        /// <summary>
        /// The text changed. VVS raised this on every keystroke; the overlay sends the text when
        /// the player is done with it, so it is raised once per edit, followed by the loss of
        /// focus that VVS's view wrappers take as the end of the edit.
        /// </summary>
        public event EventHandler Change;

        internal override ViewControl CreateHostControl(DecalView view, FixedLayout parent, string name, Rectangle rect)
            => view.AddControl<Edit>(parent, name, rect.X, rect.Y, rect.Width, rect.Height);

        private protected override void OnAttached()
        {
            if (Box != null)
                Box.Changed += OnChanged;
        }

        private protected override void OnDetaching()
        {
            if (Box != null)
                Box.Changed -= OnChanged;
        }

        private protected override void ApplyDetachedState()
        {
            base.ApplyDetachedState();
            Box.Text = _text;
        }

        private void OnChanged(object sender, EditChangedEventArgs e)
        {
            Change?.Invoke(this, EventArgs.Empty);
            RaiseFocusCycle();
        }
    }

    /// <summary>A label, in a view, in a list's text column, or as a dropdown's entry.</summary>
    public class HudStaticText : HudControl
    {
        private string _text = string.Empty;
        private Color? _color;
        private Func<string> _getText;
        private Action<string> _setText;
        private Func<long?> _getColor;
        private Action<long?> _setColor;

        public HudStaticText()
        {
        }

        public string Text
        {
            get => _getText != null ? _getText() : _text;
            set
            {
                if (_setText != null)
                    _setText(value ?? string.Empty);
                else
                    _text = value ?? string.Empty;
            }
        }

        /// <summary>The text's colour; white, the theme's, until set.</summary>
        public Color TextColor
        {
            get
            {
                if (_getColor == null)
                    return _color ?? Color.White;

                long? color = _getColor();
                return color.HasValue ? Color.FromArgb(unchecked((int)color.Value)) : Color.White;
            }
            set
            {
                if (_setColor != null)
                    _setColor(unchecked((uint)value.ToArgb()));
                else
                    _color = value;
            }
        }

        public int FontHeight { get; set; }

        public string FontName { get; set; } = string.Empty;

        public WriteTextFormats TextAlignment { get; set; }

        public void ResetTextColor()
        {
            if (_setColor != null)
                _setColor(null);
            else
                _color = null;
        }

        public void SetDefaultFontHeight() => FontHeight = 0;

        public void SetDefaultFontName() => FontName = string.Empty;

        internal override ViewControl CreateHostControl(DecalView view, FixedLayout parent, string name, Rectangle rect)
            => view.AddControl<StaticText>(parent, name, rect.X, rect.Y, rect.Width, rect.Height);

        private protected override void OnAttached()
        {
            if (Bound is StaticText label)
            {
                _getText = () => label.Text;
                _setText = text => label.Text = text;
                _getColor = () => label.TextColor;
                _setColor = color => label.TextColor = color;
            }
        }

        private protected override void ApplyDetachedState()
        {
            base.ApplyDetachedState();
            Text = _text;
            if (_color.HasValue)
                TextColor = _color.Value;
        }

        internal static HudStaticText ForCell(ListCell cell)
            => new HudStaticText
            {
                _getText = () => cell.Text,
                _setText = text => cell.Text = text,
                _getColor = () => cell.Color,
                _setColor = color => cell.Color = color,
            };

        internal static HudStaticText ForOption(Choice choice, int index)
            => new HudStaticText
            {
                _getText = () => choice.Options[index].Text,
                _setText = text => choice.SetText(index, text),
            };
    }

    /// <summary>A dropdown: DecalControls.Choice.</summary>
    /// <remarks>
    /// <para>
    /// Which entry is current is kept here, by VVS's rules rather than the host dropdown's, since
    /// plugins were written against VVS's. The first entry added to an empty dropdown becomes
    /// current, so a dropdown filled in code shows - and reads as - its first entry without the
    /// plugin ever choosing it. Adding or inserting more leaves the number alone, even where an
    /// entry goes in before the current one; deleting the current entry leaves none current, and
    /// deleting another leaves the number alone. Current is 0 until it has been anything else, as
    /// VVS's field started, and setting it to a number with no entry behind it does nothing.
    /// </para>
    /// <para>Change is raised only for the player's choice, never for the plugin's own changes.</para>
    /// </remarks>
    public class HudCombo : HudControl, IDisposable
    {
        private readonly List<string> _items = new List<string>();
        private int _current;

        public HudCombo(ControlGroup ic)
        {
        }

        private Choice Choice => Bound as Choice;

        public int Count => Choice?.Count ?? _items.Count;

        public int Current
        {
            get => _current;
            set
            {
                if (_current == value || value < 0 || value >= Count)
                    return;

                _current = value;
                ShowCurrent();
            }
        }

        /// <summary>An entry, as a label whose text can be read and changed; null for a number with no entry.</summary>
        /// <remarks>Setting one inserts it there, as VVS's did, rather than replacing what is there.</remarks>
        public HudControl this[int i]
        {
            get
            {
                if (i < 0 || i >= Count)
                    return null;

                return Choice != null ? HudStaticText.ForOption(Choice, i) : new HudStaticText { Text = _items[i] };
            }
            set
            {
                if (i >= 0 && i < Count)
                    InsertItem(i, value, null);
            }
        }

        /// <summary>The player chose an entry.</summary>
        public event EventHandler Change;

        public void AddItem(string s, object tag)
        {
            if (Choice != null)
                Choice.Add(s, tag as string);
            else
                _items.Add(s ?? string.Empty);

            if (Count == 1)
                _current = 0;

            ShowCurrent();
        }

        public void AddItem(HudControl ctrl, object tag) => AddItem((ctrl as HudStaticText)?.Text ?? string.Empty, tag);

        public void InsertItem(int index, string s, object tag)
        {
            if (Choice != null)
                Choice.Insert(index, s, tag as string);
            else
                _items.Insert(index, s ?? string.Empty);

            if (Count == 1)
                _current = 0;

            ShowCurrent();
        }

        public void InsertItem(int index, HudControl ctrl, object tag) => InsertItem(index, (ctrl as HudStaticText)?.Text ?? string.Empty, tag);

        /// <summary>Removes an entry. Throws for a number with no entry behind it, as VVS's did.</summary>
        public void DeleteItem(int ind)
        {
            if (ind < 0 || ind >= Count)
                throw new ArgumentException();

            if (Choice != null)
                Choice.RemoveAt(ind);
            else
                _items.RemoveAt(ind);

            if (_current == ind)
                _current = -1;

            ShowCurrent();
        }

        /// <summary>
        /// Removes every entry, one at a time from the first, as VVS's did - so a current entry
        /// other than the first leaves its number behind.
        /// </summary>
        public void Clear()
        {
            while (Count > 0)
                DeleteItem(0);
        }

        internal override ViewControl CreateHostControl(DecalView view, FixedLayout parent, string name, Rectangle rect)
            => view.AddControl<Choice>(parent, name, rect.X, rect.Y, rect.Width, rect.Height);

        private protected override void OnAttached()
        {
            if (Choice == null)
                return;

            // A dropdown from a view's XML: VVS added its options one by one, the first made current.
            Choice.Changed += OnChanged;
            ShowCurrent();
        }

        private protected override void OnDetaching()
        {
            if (Choice != null)
                Choice.Changed -= OnChanged;
        }

        private protected override void ApplyDetachedState()
        {
            base.ApplyDetachedState();
            foreach (string item in _items)
                Choice.Add(item);

            _items.Clear();
            ShowCurrent();
        }

        /// <summary>Has the host dropdown show the current entry, or none where there is no entry behind the number.</summary>
        private void ShowCurrent()
        {
            if (Choice != null)
                Choice.Selected = _current >= 0 && _current < Choice.Count ? _current : -1;
        }

        private void OnChanged(object sender, ChoiceChangedEventArgs e)
        {
            _current = e.Selected;
            Change?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A list of rows under columns: DecalControls.List.</summary>
    public class HudList : HudControl
    {
        private readonly List<cColInfo> _pendingColumns = new List<cColInfo>();

        /// <summary>
        /// The control each column added in code was declared to hold, by index: the host's list
        /// knows only text, tick or picture, and a plugin casts a cell back to what it declared.
        /// </summary>
        private readonly Dictionary<int, Type> _columnTypes = new Dictionary<int, Type>();

        public HudList()
        {
        }

        public HudList(ControlGroup iparent)
        {
        }

        public delegate void delClickedControl(object sender, int row, int col);

        /// <summary>The player clicked a cell. A check column's tick has already been changed.</summary>
        public event delClickedControl Click;

        private List List => Bound as List;

        public int RowCount => List?.RowCount ?? 0;

        public int ColumnCount => List?.Columns.Count ?? _pendingColumns.Count;

        public HudListRowAccessor this[int c] => new HudListRowAccessor(this, c);

        public int ScrollPosition { get; set; }

        public int Padding { get; set; }

        public int WPadding { get; set; }

        public int WPaddingOuter { get; set; }

        public int ControlHeight { get; set; } = 16;

        public int ScrollBarWidth => 16;

        public int VisibleRows => ControlHeight > 0 && Bound != null ? Math.Max(1, Bound.Height / ControlHeight) : 0;

        public int MaxScroll => Math.Max(0, RowCount - VisibleRows);

        public HudListRowAccessor AddRow()
        {
            RequireList().Add();
            return this[RowCount - 1];
        }

        public HudListRowAccessor InsertRow(int ind)
        {
            RequireList().Insert(ind);
            return this[ind];
        }

        public void RemoveRow(int row) => RequireList().RemoveAt(row);

        public void ClearRows() => List?.Clear();

        /// <summary>
        /// Adds a column holding controls of the given kind: a check box, a picture, or - for
        /// anything else - text. Only while the list has no rows.
        /// </summary>
        public void AddColumn(Type T, int Width, string name)
        {
            if (List == null || Group?.View == null)
            {
                _pendingColumns.Add(new cColInfo(T, Width, name));
                _columnTypes[_pendingColumns.Count - 1] = T;
                return;
            }

            if (List.RowCount > 0)
            {
                DecalRuntime.Current?.NoteUnsupported("HudList.AddColumn on a list with rows");
                return;
            }

            Group.View.AddColumn(List, KindOf(T), Width, name);
            _columnTypes[List.Columns.Count - 1] = T;
        }

        public void AddColumn(cColInfo colinfo)
        {
            if (colinfo != null)
                AddColumn(colinfo.ControlType, colinfo.Width, colinfo.Name);
        }

        public void RemoveColumnAndClearRows(int col) => DecalRuntime.Current?.NoteUnsupported("HudList.RemoveColumnAndClearRows");

        public void ClearColumnsAndRows()
        {
            if (List != null && Group?.View != null)
                Group.View.ClearColumns(List);
            else
                _pendingColumns.Clear();

            _columnTypes.Clear();
        }

        public cColInfo GetColumnInfo(int col)
        {
            if (List == null)
                return _pendingColumns[col];

            ListColumn column = List.Columns[col];
            Type type = _columnTypes.TryGetValue(col, out Type declared) ? declared : column.Kind switch
            {
                ListColumnKind.Check => typeof(HudCheckBox),
                ListColumnKind.Icon => typeof(HudPictureBox),
                _ => typeof(HudStaticText),
            };

            return new cColInfo(type, column.FixedWidth, column.Name);
        }

        public void SetColumnWidth(int col, int width)
        {
        }

        /// <summary>The cell as the kind of control its column holds.</summary>
        internal HudControl Cell(int row, int col)
        {
            List list = RequireList();
            ListCell cell = list[row][col];
            return list.Columns[col].Kind switch
            {
                ListColumnKind.Check => HudCheckBox.ForCell(cell),
                ListColumnKind.Icon when _columnTypes.TryGetValue(col, out Type declared) && typeof(HudImageStack).IsAssignableFrom(declared) => HudImageStack.ForCell(cell),
                ListColumnKind.Icon => HudPictureBox.ForCell(cell),
                _ => HudStaticText.ForCell(cell),
            };
        }

        internal override ViewControl CreateHostControl(DecalView view, FixedLayout parent, string name, Rectangle rect)
            => view.AddControl<List>(parent, name, rect.X, rect.Y, rect.Width, rect.Height);

        private static ListColumnKind KindOf(Type type)
            => type != null && typeof(HudCheckBox).IsAssignableFrom(type) ? ListColumnKind.Check
            : type != null && typeof(HudImageStack).IsAssignableFrom(type) ? ListColumnKind.Icon
            : type != null && typeof(HudPictureBox).IsAssignableFrom(type) && !typeof(HudButton).IsAssignableFrom(type) && !typeof(HudTextBox).IsAssignableFrom(type) && !typeof(HudConsole).IsAssignableFrom(type) ? ListColumnKind.Icon
            : ListColumnKind.Text;

        private List RequireList()
            => List ?? throw new ControlNotInitializedException("This list is not in a view, so it has no rows.");

        private protected override void OnAttached()
        {
            if (List != null)
                List.Clicked += OnClicked;
        }

        private protected override void OnDetaching()
        {
            if (List != null)
                List.Clicked -= OnClicked;
        }

        private protected override void ApplyDetachedState()
        {
            base.ApplyDetachedState();
            foreach (cColInfo column in _pendingColumns)
                Group.View.AddColumn(List, KindOf(column.ControlType), column.Width, column.Name);

            _pendingColumns.Clear();
        }

        private void OnClicked(object sender, ListClickedEventArgs e) => Click?.Invoke(this, e.Row, e.Column);

        /// <summary>One row, addressed by column.</summary>
        public class HudListRowAccessor
        {
            private readonly HudList _list;
            private readonly int _row;

            public HudListRowAccessor()
            {
            }

            internal HudListRowAccessor(HudList list, int row)
            {
                _list = list;
                _row = row;
            }

            /// <summary>
            /// A cell, as the kind of control its column holds. VVS let a plugin put any control
            /// in a cell; here a cell holds text, a tick or a picture, so a control put in one
            /// gives it whichever of those it has.
            /// </summary>
            public HudControl this[int c]
            {
                get => _list?.Cell(_row, c);
                set
                {
                    if (_list == null || value == null)
                        return;

                    HudControl cell = _list.Cell(_row, c);
                    switch (value)
                    {
                        case HudStaticText text when cell is HudStaticText target:
                            target.Text = text.Text;
                            target.TextColor = text.TextColor;
                            break;
                        case HudButton button when cell is HudStaticText target:
                            target.Text = button.Text;
                            break;
                        case HudCheckBox box when cell is HudCheckBox target:
                            target.Checked = box.Checked;
                            break;
                        case HudPictureBox picture when cell is HudPictureBox target:
                            target.Image = picture.Image;
                            break;
                        case HudImageButton image when cell is HudPictureBox target:
                            target.Image = image.Image_Up;
                            break;
                        default:
                            DecalRuntime.Current?.NoteUnsupported("HudList cells holding a control their column does not", value.GetType().Name);
                            break;
                    }
                }
            }
        }

        public class cColInfo
        {
            public cColInfo(Type t, int w, string n)
            {
                ControlType = t;
                Width = w;
                Name = n ?? string.Empty;
            }

            public Type ControlType { get; }

            public int Width { get; }

            public string Name { get; }
        }
    }

    /// <summary>Pages behind tabs: DecalControls.Notebook.</summary>
    public class HudTabView : HudPictureBox
    {
        private readonly List<(HudControl Top, string Name)> _pendingTabs = new List<(HudControl, string)>();

        public HudTabView()
        {
        }

        private Notebook Notebook => Bound as Notebook;

        public int CurrentTab
        {
            get => Notebook?.ActivePage ?? -1;
            set
            {
                if (Notebook != null && value >= 0 && value < Notebook.Pages.Count)
                    Notebook.ActivePage = value;
            }
        }

        public int TabCount => Notebook?.Pages.Count ?? _pendingTabs.Count;

        /// <summary>A page's content: the layout on it.</summary>
        public HudControl this[int tab] => Notebook != null && tab >= 0 && tab < Notebook.Pages.Count ? Group?.Find(Notebook.Pages[tab].Content) : null;

        public HudControl this[string tab]
        {
            get
            {
                if (Notebook == null)
                    return null;

                for (int i = 0; i < Notebook.Pages.Count; i++)
                {
                    if (string.Equals(Notebook.Pages[i].Label, tab, StringComparison.Ordinal))
                        return this[i];
                }

                return null;
            }
        }

        /// <summary>The player chose a tab.</summary>
        public event EventHandler OpenTabChange;

        public string GetTabName(int tab) => Notebook != null && tab >= 0 && tab < Notebook.Pages.Count ? Notebook.Pages[tab].Label : string.Empty;

        /// <summary>
        /// Adds a page showing <paramref name="top"/> - usually a layout the plugin has filled -
        /// under a tab named <paramref name="tabname"/>.
        /// </summary>
        public void AddTab(HudControl top, string tabname)
        {
            if (Notebook == null || Group?.View == null)
            {
                _pendingTabs.Add((top, tabname));
                return;
            }

            NotebookPage page = Group.View.AddPage(Notebook, tabname);
            FixedLayout content = (FixedLayout)page.Content;

            if (top is HudFixedLayout layout && !layout.Attached)
            {
                // The page's own layout becomes the plugin's, and gets what was put in it.
                Group.Adopt(layout, content, this);
                return;
            }

            HudFixedLayout holder = new HudFixedLayout();
            Group.Adopt(holder, content, this);
            if (top != null)
                holder.AddControl(top, new Rectangle(0, 0, Notebook.Width, Math.Max(0, Notebook.Height - 20)));
        }

        internal override ViewControl CreateHostControl(DecalView view, FixedLayout parent, string name, Rectangle rect)
            => view.AddControl<Notebook>(parent, name, rect.X, rect.Y, rect.Width, rect.Height);

        private protected override void OnAttached()
        {
            if (Notebook != null)
                Notebook.PageChanged += OnPageChanged;
        }

        private protected override void OnDetaching()
        {
            if (Notebook != null)
                Notebook.PageChanged -= OnPageChanged;
        }

        private protected override void ApplyDetachedState()
        {
            base.ApplyDetachedState();

            List<(HudControl Top, string Name)> tabs = new List<(HudControl, string)>(_pendingTabs);
            _pendingTabs.Clear();
            foreach ((HudControl top, string name) in tabs)
                AddTab(top, name);
        }

        private void OnPageChanged(object sender, PageChangedEventArgs e) => OpenTabChange?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Controls at fixed places: DecalControls.FixedLayout.</summary>
    public class HudFixedLayout : HudPictureBox
    {
        private readonly List<(HudControl Control, Rectangle Rect)> _pending = new List<(HudControl, Rectangle)>();

        public HudFixedLayout()
        {
        }

        public bool CanScrollV { get; set; }

        public Rectangle GetControlRect(HudControl ctrl)
        {
            if (ctrl == null)
                return Rectangle.Empty;

            if (ctrl.Attached)
                return ctrl.ClipRegion;

            foreach ((HudControl control, Rectangle rect) in _pending)
            {
                if (ReferenceEquals(control, ctrl))
                    return rect;
            }

            return Rectangle.Empty;
        }

        public Rectangle GetControlRect(string ctrl) => GetControlRect(Group?[ctrl]);

        public void SetControlRect(HudControl ctrl, Rectangle rect)
        {
            if (ctrl?.Bound != null)
            {
                ctrl.Bound.Left = rect.Left;
                ctrl.Bound.Top = rect.Top;
                ctrl.Bound.Width = rect.Width;
                ctrl.Bound.Height = rect.Height;
                return;
            }

            for (int i = 0; i < _pending.Count; i++)
            {
                if (ReferenceEquals(_pending[i].Control, ctrl))
                    _pending[i] = (ctrl, rect);
            }
        }

        public void SetControlRect(string ctrl, Rectangle rect) => SetControlRect(Group?[ctrl], rect);

        /// <summary>
        /// Places a control made in code. In a layout already in a view it is drawn at once;
        /// in one not yet in a view, it is drawn when the layout is.
        /// </summary>
        public void AddControl(HudControl ctrl, Rectangle rect)
        {
            if (ctrl == null)
                return;

            if (ctrl.Attached)
            {
                DecalRuntime.Current?.NoteUnsupported("HudFixedLayout.AddControl of a control already in a view");
                return;
            }

            if (Bound is FixedLayout layout && Group?.View != null)
                Group.Place(ctrl, layout, this, rect);
            else
                _pending.Add((ctrl, rect));
        }

        internal override ViewControl CreateHostControl(DecalView view, FixedLayout parent, string name, Rectangle rect)
            => view.AddControl<FixedLayout>(parent, name, rect.X, rect.Y, rect.Width, rect.Height);

        private protected override void ApplyDetachedState()
        {
            base.ApplyDetachedState();

            List<(HudControl Control, Rectangle Rect)> pending = new List<(HudControl, Rectangle)>(_pending);
            _pending.Clear();
            foreach ((HudControl control, Rectangle rect) in pending)
                AddControl(control, rect);
        }
    }

    /// <summary>What sliders, scroll bars and progress bars share: a position between two bounds.</summary>
    public class LinearPositionControl : HudControl
    {
        private int _min;
        private int _max = 100;
        private int _position;

        public LinearPositionControl()
        {
        }

        public delegate void delScrollChanged(int min, int max, int pos);

        /// <summary>The position changed.</summary>
        public event delScrollChanged Changed;

        public int Min
        {
            get => Bound is Slider s ? (int)s.Minimum : Bound is Progress p ? (int)p.Minimum : _min;
            set
            {
                if (Bound is Slider s)
                    s.Minimum = value;
                else if (Bound is Progress p)
                    p.Minimum = value;
                else
                    _min = value;
            }
        }

        public int Max
        {
            get => Bound is Slider s ? (int)s.Maximum : Bound is Progress p ? (int)p.Maximum : _max;
            set
            {
                if (Bound is Slider s)
                    s.Maximum = value;
                else if (Bound is Progress p)
                    p.Maximum = value;
                else
                    _max = value;
            }
        }

        public int Position
        {
            get => Bound is Slider s ? (int)Math.Round(s.Position) : Bound is Progress p ? (int)p.Value : _position;
            set
            {
                if (Bound is Slider s)
                    s.Position = value;
                else if (Bound is Progress p)
                    p.Value = value;
                else
                    _position = value;
            }
        }

        public int Percentage => Max > Min ? (Position - Min) * 100 / (Max - Min) : 0;

        protected virtual void MinChanged(bool iSilent)
        {
        }

        protected virtual void MaxChanged(bool iSilent)
        {
        }

        protected virtual void PositionChanged(bool iSilent)
        {
            if (!iSilent)
                Changed?.Invoke(Min, Max, Position);
        }

        protected internal void SetMinWithEvent(int newval)
        {
            Min = newval;
            MinChanged(false);
        }

        protected internal void SetMaxWithEvent(int newval)
        {
            Max = newval;
            MaxChanged(false);
        }

        protected internal void SetPositionWithEvent(int newval)
        {
            Position = newval;
            PositionChanged(false);
        }

        internal override ViewControl CreateHostControl(DecalView view, FixedLayout parent, string name, Rectangle rect)
            => view.AddControl<Slider>(parent, name, rect.X, rect.Y, rect.Width, rect.Height);

        private protected override void OnAttached()
        {
            if (Bound is Slider slider)
                slider.Changed += OnSliderChanged;
        }

        private protected override void OnDetaching()
        {
            if (Bound is Slider slider)
                slider.Changed -= OnSliderChanged;
        }

        private protected override void ApplyDetachedState()
        {
            base.ApplyDetachedState();
            Min = _min;
            Max = _max;
            Position = _position;
        }

        private void OnSliderChanged(object sender, SliderChangedEventArgs e) => PositionChanged(false);
    }

    /// <summary>A horizontal slider: DecalControls.Slider.</summary>
    public class HudHSlider : LinearPositionControl
    {
        public HudHSlider()
        {
        }

        public bool UserChangeable { get; set; } = true;
    }

    /// <summary>A vertical scroll bar, drawn as a slider.</summary>
    public class HudVScrollBar : LinearPositionControl
    {
        public HudVScrollBar()
        {
        }
    }

    /// <summary>A horizontal scroll bar, drawn as a slider.</summary>
    public class HudHScrollBar : LinearPositionControl
    {
        public HudHScrollBar()
        {
        }
    }

    /// <summary>A bar the plugin fills: DecalControls.Progress.</summary>
    public class HudProgressBar : LinearPositionControl
    {
        public HudProgressBar()
        {
        }

        public enum ProgressTextStyle
        {
            Percentage = 0,
            Value = 1,
            Custom = 2,
        }

        public string PreText { get; set; } = string.Empty;

        public ProgressTextStyle PreTextStyle { get; set; }

        public Color TextColor { get; set; } = Color.White;

        public int FontHeight { get; set; }

        public string FontName { get; set; } = string.Empty;

        public ACImage ProgressFilled
        {
            set { }
        }

        public ACImage ProgressEmpty
        {
            set { }
        }

        public void ResetTextColor() => TextColor = Color.White;

        public void ResetProgressFilled()
        {
        }

        public void ResetProgressEmpty()
        {
        }

        internal override ViewControl CreateHostControl(DecalView view, FixedLayout parent, string name, Rectangle rect)
            => view.AddControl<Progress>(parent, name, rect.X, rect.Y, rect.Width, rect.Height);
    }
}
