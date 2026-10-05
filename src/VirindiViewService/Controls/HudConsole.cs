using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using AC.Host.Plugins.Views;
using Decal.Adapter.Hosting;

namespace VirindiViewService
{
    /// <summary>The kinds of line a VVS console sorts chat into, as VVS numbered them.</summary>
    public enum eConsoleColorClass
    {
        SystemMessage = 0,
        Magic = 1,
        MyMeleeAttack = 2,
        OtherMeleeAttack = 3,
        MyTell = 4,
        OtherTell = 5,
        GlobalChat = 6,
        AllegianceChat = 7,
        FellowChat = 8,
        OpenChat = 9,
        OpenEmote = 10,
        StatusError = 11,
        StatRaised = 12,
        RareFound = 13,
        PluginMessage = 96,
        PluginError = 97,
        Link = 98,
        Unknown = 99,
    }
}

namespace VirindiViewService.Controls
{
    /// <summary>
    /// A scrolling console of lines - VVS's, which chat windows and plugins' logs were made of -
    /// drawn here as a list with one line to a row, the newest at the bottom.
    /// </summary>
    /// <remarks>
    /// Lines keep the colour they were written in; a line written by class takes the list's own
    /// colour, since the colour schemes that turned classes into colours are VVS's themes, and
    /// every window here is drawn in the Decal theme. Links are written as their text, and are
    /// not links.
    /// </remarks>
    public class HudConsole : HudPictureBox
    {
        /// <summary>How many lines a console holds when it is not told: VVS's own default.</summary>
        private const uint DefaultBufferSize = 200;

        /// <summary>The most rows the host is given, whatever the buffer: the overlay draws every row it is sent.</summary>
        private const int MostRowsShown = 500;

        private readonly List<(string Text, long? Color)> _lines = new List<(string, long?)>();
        private string _partial = string.Empty;
        private long? _partialColor;

        public HudConsole()
            : this(DefaultBufferSize)
        {
        }

        public HudConsole(uint pBufferSize)
        {
            BufferSize = pBufferSize;
        }

        public interface IConsoleColorScheme
        {
            Color TranslateColor(eConsoleColorClass c);
        }

        /// <summary>The client's chat colours, as VVS numbered them.</summary>
        public enum eACTextColor
        {
            Green0 = 0,
            Green1 = 1,
            White2 = 2,
            Yellow3 = 3,
            DkYellow4 = 4,
            Purple5 = 5,
            Red6 = 6,
            Blue7 = 7,
            LtRed8 = 8,
            LtRed9 = 9,
            Yellow10 = 10,
            DkYellow11 = 11,
            Gray12 = 12,
            Teal13 = 13,
            LtBlue14 = 14,
            Red15 = 15,
            Green16 = 16,
            Blue17 = 17,
            Orange18 = 18,
            Yellow19 = 19,
            Green20 = 20,
            Red21 = 21,
            LtRed22 = 22,
            Green23 = 23,
            Green24 = 24,
            Green25 = 25,
            ErrorRed26 = 26,
            LtBlue27 = 27,
            LtBlue28 = 28,
            LtBlue29 = 29,
            LinkGreen = 99,
        }

        public delegate void delString(HudConsole Sender, string LinkInfo);

        /// <summary>A link in any console was clicked. Links are not clickable here, so it is never raised.</summary>
#pragma warning disable CS0067
        public static event delString LinkClicked;
#pragma warning restore CS0067

        /// <summary>How many lines it keeps; the oldest go first.</summary>
        public uint BufferSize { get; set; }

        public bool ShowTimestamp { get; set; }

        public bool AutoScroll { get; set; } = true;

        public int ScrollPosition { get; set; }

        public int ScrollMax => Math.Max(0, _lines.Count - 1);

        public int ScrollMin => 0;

        private List List => Bound as List;

        public void WriteLink(string pInnerText, string pLinkTarget) => Write(pInnerText, (long?)null);

        public void Write(string pText, Color pColor) => Write(pText, Argb(pColor));

        public void Write(string pText, eConsoleColorClass pColor) => Write(pText, (long?)null);

        public void WriteLine(string pText, Color pColor)
        {
            Write(pText, Argb(pColor));
            EndLine();
        }

        public void WriteLine(string pText, eConsoleColorClass pColor)
        {
            Write(pText, (long?)null);
            EndLine();
        }

        /// <summary>A line with Decal's chat links in it, "&lt;Tell:IIDString:id:name&gt;text&lt;\Tell&gt;", written as their text.</summary>
        public void WriteLineTellParsed(string pText, eConsoleColorClass pColor) => WriteLine(Untell(pText), pColor);

        public void WriteLineTellParsed(string pText, Color pColor) => WriteLine(Untell(pText), pColor);

        public void ClearBuffer()
        {
            _lines.Clear();
            _partial = string.Empty;
            List?.Clear();
        }

        /// <summary>A list with one column as wide as the console, a line to a row.</summary>
        internal override ViewControl CreateHostControl(DecalView view, FixedLayout parent, string name, Rectangle rect)
        {
            List list = view.AddControl<List>(parent, name, rect.X, rect.Y, rect.Width, rect.Height);
            view.AddColumn(list, ListColumnKind.Text, Math.Max(16, rect.Width - 16), "clLine");
            return list;
        }

        private protected override void ApplyDetachedState()
        {
            base.ApplyDetachedState();
            Show();
        }

        private void Write(string text, long? color)
        {
            // Text with line breaks in it is several lines, as the console drew it.
            string[] parts = (text ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                    EndLine();

                _partial += parts[i];
                _partialColor ??= color;
            }
        }

        private void EndLine()
        {
            string line = ShowTimestamp ? DateTime.Now.ToString("[HH:mm] ", System.Globalization.CultureInfo.InvariantCulture) + _partial : _partial;
            _lines.Add((line, _partialColor));
            _partial = string.Empty;
            _partialColor = null;

            int keep = (int)Math.Min(Math.Max(1u, BufferSize), (uint)MostRowsShown);
            if (_lines.Count > keep)
                _lines.RemoveRange(0, _lines.Count - keep);

            Show();
        }

        private void Show()
        {
            List list = List;
            if (list == null || list.Columns.Count == 0)
                return;

            list.Clear();
            foreach ((string text, long? color) in _lines)
            {
                ListRow row = list.Add();
                row[0].Text = text;
                row[0].Color = color;
            }

            ScrollPosition = ScrollMax;
        }

        private static long? Argb(Color color) => color.IsEmpty ? null : unchecked((uint)color.ToArgb());

        private static string Untell(string text)
            => Regex.Replace(text ?? string.Empty, @"<Tell:[^>]*>(.*?)<\\Tell>", "$1");
    }

    /// <summary>
    /// A console that shows chat: lines plugins send it, by class, through
    /// <see cref="SendChatText(string, eConsoleColorClass)"/>, filtered as the player chose.
    /// </summary>
    /// <remarks>
    /// VVS's also copied in the game's own chat as the client drew it. The host sees the game's
    /// chat as messages rather than as the client's lines, so here a chat box shows what plugins
    /// send it, and says once in the log that the game's own lines are not in it.
    /// </remarks>
    public class HudChatbox : HudConsole
    {
        private static readonly List<HudChatbox> Open = new List<HudChatbox>();

        private readonly HashSet<eConsoleColorClass> _enabled = new HashSet<eConsoleColorClass>();
        private bool _all = true;
        private bool _disposed;

        public HudChatbox()
        {
            lock (Open)
                Open.Add(this);

            DecalRuntime.Current?.NoteUnsupported("HudChatbox's copy of the game's chat", "a chat box shows what plugins send it");
        }

        public bool FilterIsEnabled(eConsoleColorClass c) => _all || _enabled.Contains(c);

        public void FilterClearAll()
        {
            _all = false;
            _enabled.Clear();
        }

        public void FilterSetAll()
        {
            _all = true;
            _enabled.Clear();
        }

        public void FilterAddEnabled(eConsoleColorClass c) => _enabled.Add(c);

        public void FilterRemoveEnabled(eConsoleColorClass c) => _enabled.Remove(c);

        /// <summary>Writes a line to every open chat box whose filter lets its class through.</summary>
        public static void SendChatText(string t, eConsoleColorClass c) => SendChatText(t, c, c);

        public static void SendChatText(string t, eConsoleColorClass c, eConsoleColorClass filterby)
        {
            HudChatbox[] boxes;
            lock (Open)
                boxes = Open.ToArray();

            foreach (HudChatbox box in boxes)
            {
                if (box.FilterIsEnabled(filterby))
                    box.WriteLineTellParsed(t, c);
            }
        }

        public static void SendChatText(string t, eACTextColor color, eConsoleColorClass filterby)
            => SendChatText(t, eConsoleColorClass.PluginMessage, filterby);

        public override void Dispose()
        {
            base.Dispose();
            if (_disposed)
                return;

            _disposed = true;
            lock (Open)
                Open.Remove(this);
        }
    }
}
