using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using AC.Host.Plugins.Views;
using Decal.Adapter.Hosting;

namespace VirindiViewService.Controls
{
    /// <summary>
    /// Images drawn one over another, and text over them: an item's icon on its underlay, a
    /// count over a picture. In a view, an image; in a list, an icon cell.
    /// </summary>
    /// <remarks>
    /// The host draws one image per picture, so a stack shows its topmost image - the last one
    /// added or set, which for an icon over its underlay is the icon - and keeps every layer,
    /// with its place, for a plugin that reads them back. Text in a stack is kept and not drawn,
    /// and the first plugin to write some is told so in the log.
    /// </remarks>
    public class HudImageStack : HudControl
    {
        private readonly List<object> _layers = new List<object>();
        private readonly List<Rectangle> _rectangles = new List<Rectangle>();
        private ListCell _cell;

        public HudImageStack()
        {
        }

        public ReadOnlyCollection<Rectangle> Rectangles => _rectangles.AsReadOnly();

        public int Count => _layers.Count;

        public void Add(Rectangle rect, ACImage img) => Push(rect, img?.Clone() ?? new ACImage());

        /// <summary>A theme's element; every window is drawn in the Decal theme here, so a blank layer.</summary>
        public void Add(Rectangle rect, string themeelement) => Push(rect, null);

        public void Add(Rectangle rect, string text, Color textcolor, Color shadowcolor, string fontname, int fontheight, int shadowsize, int shadowalpha, WriteTextFormats fmt)
            => Push(rect, Text(text));

        public void Add(Rectangle rect, string text, string textcolorelement, string shadowcolorelement, int fontheight, string shadowsizeelement, string shadowalphaelement, WriteTextFormats fmt)
            => Push(rect, Text(text));

        /// <summary>A blank layer, to be set later.</summary>
        public void Add(Rectangle rect) => Push(rect, null);

        public void SetValue(int index, ACImage img) => Set(index, img?.Clone() ?? new ACImage());

        public void SetValue(int index, string themeelement) => Set(index, null);

        public void SetValue(int index, string text, Color textcolor, Color shadowcolor, string fontname, int fontheight, int shadowsize, int shadowalpha, WriteTextFormats fmt)
            => Set(index, Text(text));

        public void SetValue(int index, string text, string textcolorelement, string shadowcolorelement, int fontheight, string shadowsizeelement, string shadowalphaelement, WriteTextFormats fmt)
            => Set(index, Text(text));

        public void SetValue(int index) => Set(index, null);

        public void SetRect(int index, Rectangle rect)
        {
            Check(index);
            _rectangles[index] = rect;
        }

        public Rectangle GetRectangle(int index)
        {
            Check(index);
            return _rectangles[index];
        }

        /// <summary>A layer's place in the view: its place in the stack, moved to where the stack is.</summary>
        public Rectangle GetSurfaceRectangle(int index)
        {
            Rectangle place = GetRectangle(index);
            place.Offset(ClipRegion.Location);
            return place;
        }

        public void Clear()
        {
            _layers.Clear();
            _rectangles.Clear();
            Show();
        }

        internal static HudImageStack ForCell(ListCell cell)
        {
            HudImageStack stack = new HudImageStack { _cell = cell };
            ACImage shown = ACImage.FromImageKey(cell.ImageKey);
            if (shown.ImageDataType != ACImage.eACImageUnderlyingType.Blank)
            {
                stack._layers.Add(shown);
                stack._rectangles.Add(Rectangle.Empty);
            }

            return stack;
        }

        internal override ViewControl CreateHostControl(DecalView view, FixedLayout parent, string name, Rectangle rect)
            => view.AddControl<ImageButton>(parent, name, rect.X, rect.Y, rect.Width, rect.Height);

        private protected override void ApplyDetachedState()
        {
            base.ApplyDetachedState();
            Show();
        }

        private void Push(Rectangle rect, object layer)
        {
            _layers.Add(layer);
            _rectangles.Add(rect);
            Show();
        }

        private void Set(int index, object layer)
        {
            Check(index);
            _layers[index] = layer;
            Show();
        }

        private void Check(int index)
        {
            // What VVS threw, word for word.
            if (index < 0 || index >= _layers.Count)
                throw new Exception("Invalid index.");
        }

        private static object Text(string text)
        {
            DecalRuntime.Current?.NoteUnsupported("HudImageStack text", "text in an image stack is kept, not drawn");
            return text ?? string.Empty;
        }

        /// <summary>Draws the topmost image, or nothing when there is none.</summary>
        private void Show()
        {
            string key = string.Empty;
            for (int i = _layers.Count - 1; i >= 0; i--)
            {
                if (_layers[i] is ACImage image && image.ImageDataType != ACImage.eACImageUnderlyingType.Blank)
                {
                    key = image.ToImageKey();
                    break;
                }
            }

            if (_cell != null)
                _cell.ImageKey = key;
            else if (Bound is ImageButton picture)
                picture.ImageKey = key;
        }
    }
}
