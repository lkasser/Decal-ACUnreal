using System;
using System.Collections.Generic;

namespace AC.Host.Plugins.Views
{
    /// <summary>What a list column holds.</summary>
    public enum ListColumnKind
    {
        /// <summary>DecalControls.TextColumn.</summary>
        Text = 0,

        /// <summary>DecalControls.CheckColumn: a tick box the player can click.</summary>
        Check = 1,

        /// <summary>DecalControls.IconColumn: an image, usually a button in disguise.</summary>
        Icon = 2,
    }

    /// <summary>One column of a <see cref="List"/>, as the XML declared it.</summary>
    public sealed class ListColumn
    {
        internal ListColumn(ListColumnKind kind, int fixedWidth, string name)
        {
            Kind = kind;
            FixedWidth = fixedWidth;
            Name = name ?? string.Empty;
        }

        public ListColumnKind Kind { get; }

        /// <summary>In Decal pixels, or 0 to share whatever the fixed columns leave.</summary>
        public int FixedWidth { get; }

        /// <summary>The XML's name for it. Only for the plugin's reference; columns are addressed by index.</summary>
        public string Name { get; }
    }

    /// <summary>One cell of a <see cref="ListRow"/>. Which fields matter depends on the column's kind.</summary>
    public sealed class ListCell
    {
        private string _text = string.Empty;
        private string _imageKey = string.Empty;

        internal ListCell()
        {
        }

        /// <summary>A text column's text.</summary>
        public string Text
        {
            get => _text;
            set => _text = value ?? string.Empty;
        }

        /// <summary>A check column's tick.</summary>
        public bool Checked { get; set; }

        /// <summary>An icon column's image key, or empty for none.</summary>
        public string ImageKey
        {
            get => _imageKey;
            set => _imageKey = value ?? string.Empty;
        }

        /// <summary>0xAARRGGBB, or null for the theme's list colour. See <see cref="ViewColor"/>.</summary>
        public long? Color { get; set; }

        /// <summary>Shows one of the client's own images, by portal id.</summary>
        public void SetPortalImage(uint id) => ImageKey = ViewImages.Portal(id);
    }

    /// <summary>One row of a <see cref="List"/>: a cell for every column.</summary>
    public sealed class ListRow
    {
        private readonly ListCell[] _cells;

        internal ListRow(int columns)
        {
            _cells = new ListCell[columns];
            for (int i = 0; i < columns; i++)
                _cells[i] = new ListCell();
        }

        public IReadOnlyList<ListCell> Cells => _cells;

        public int Count => _cells.Length;

        /// <summary>The cell under the given column, which is how Decal's list was addressed.</summary>
        public ListCell this[int column] => _cells[column];
    }

    /// <summary>
    /// DecalControls.List: rows of cells under columns the XML fixes.
    /// </summary>
    /// <remarks>
    /// Rows are the plugin's to fill; the XML only declares the columns, so a row always
    /// has exactly one cell per column and the overlay never has to guess at a short one.
    /// </remarks>
    public sealed class List : ViewControl
    {
        private readonly List<ListColumn> _columns = new List<ListColumn>();
        private readonly List<ListRow> _rows = new List<ListRow>();

        internal List(string progId, string name)
            : base(progId, name)
        {
        }

        public IReadOnlyList<ListColumn> Columns => _columns;

        public IReadOnlyList<ListRow> Rows => _rows;

        public int RowCount => _rows.Count;

        public ListRow this[int row] => _rows[row];

        /// <summary>
        /// The player clicked a cell. A check column's tick has already been toggled,
        /// as Decal's list did itself, so the handler reads the new state from the cell.
        /// </summary>
        public event EventHandler<ListClickedEventArgs> Clicked;

        /// <summary>Adds an empty row at the bottom and returns it for filling in.</summary>
        public ListRow Add()
        {
            ListRow row = new ListRow(_columns.Count);
            _rows.Add(row);
            return row;
        }

        /// <summary>Adds an empty row at <paramref name="index"/>, moving the rest down.</summary>
        public ListRow Insert(int index)
        {
            ListRow row = new ListRow(_columns.Count);
            _rows.Insert(index, row);
            return row;
        }

        public void RemoveAt(int index) => _rows.RemoveAt(index);

        public bool Remove(ListRow row) => _rows.Remove(row);

        public void Clear() => _rows.Clear();

        internal void AddColumn(ListColumn column) => _columns.Add(column);

        internal void ClearColumns() => _columns.Clear();

        internal override bool Apply(OverlayCommand command, out string refusal)
        {
            if (command.Name != ViewVerbs.Click)
                return WrongVerb(command, out refusal, ViewVerbs.Click);

            // By index, which is only as good as the rows being where they were when the
            // snapshot went out. A plugin that reorders rows between publishes can have a
            // click land on the wrong one; Decal's list had the same property.
            if (!TryParseIndex(command.RowId, out int row) || row < 0 || row >= _rows.Count)
            {
                refusal = $"'{command.RowId}' is not a row of {this}, which has {_rows.Count}";
                return false;
            }

            if (!TryParseIndex(command.Value, out int column) || column < 0 || column >= _columns.Count)
            {
                refusal = $"'{command.Value}' is not a column of {this}, which has {_columns.Count}";
                return false;
            }

            if (_columns[column].Kind == ListColumnKind.Check)
            {
                ListCell cell = _rows[row][column];
                cell.Checked = !cell.Checked;
            }

            Clicked?.Invoke(this, new ListClickedEventArgs(this, row, column));
            refusal = null;
            return true;
        }
    }
}
