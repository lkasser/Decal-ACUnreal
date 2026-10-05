using System;
using System.Collections.Generic;

namespace AC.Host.Overlay
{
    /// <summary>
    /// Assembles a snapshot from the pieces a host has to hand.
    /// </summary>
    /// <remarks>
    /// The status arrives as ten separate arguments and the panels as tuples rather than
    /// this taking the host itself, because the snapshot must not depend on the host: the
    /// injected side of this pipe knows nothing about looting, spells or the wire
    /// protocol, and these types are what both ends share. A long parameter list is the
    /// price of that, and the cheaper of the two. <see cref="OverlayMapping"/> is the one
    /// place that reads the host's own types, and it only converts them.
    /// </remarks>
    public static class OverlayStateBuilder
    {
        /// <summary>
        /// Builds a snapshot. Null strings become empty ones, because the other end reads
        /// each of them into a std::string and a JSON null there is a parse failure, not
        /// an empty value.
        /// </summary>
        /// <param name="connected">Whether the host has a live session.</param>
        /// <param name="acting">Whether plugins may act, rather than only watch.</param>
        /// <param name="looting">Whether loot is being picked up.</param>
        /// <param name="server">The server, as the host was told to reach it.</param>
        /// <param name="character">The character's name, or empty before it is known.</param>
        /// <param name="position">Where the character is, already formatted for reading.</param>
        /// <param name="messagesIn">Messages received this session.</param>
        /// <param name="messagesOut">Messages sent this session.</param>
        /// <param name="malformed">Messages whose decoder rejected the bytes.</param>
        /// <param name="objects">How many objects the host knows about.</param>
        /// <param name="panels">The panels to draw, in the order they should appear.</param>
        public static OverlayState Build(
            bool connected,
            bool acting,
            bool looting,
            string server,
            string character,
            string position,
            long messagesIn,
            long messagesOut,
            long malformed,
            long objects,
            params (string Title, IReadOnlyList<string> Columns, IReadOnlyList<OverlayRow> Rows)[] panels)
        {
            OverlayState state = new OverlayState
            {
                PublishedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),

                Status = new OverlayStatus
                {
                    ServerConnected = connected,
                    Acting = acting,
                    Looting = looting,
                    Server = server ?? string.Empty,
                    Character = character ?? string.Empty,
                    Position = position ?? string.Empty,
                    MessagesIn = messagesIn,
                    MessagesOut = messagesOut,
                    Malformed = malformed,
                    Objects = objects,
                },
            };

            if (panels != null)
            {
                foreach ((string Title, IReadOnlyList<string> Columns, IReadOnlyList<OverlayRow> Rows) panel in panels)
                    state.Panels.Add(Panel(panel.Title, panel.Columns, panel.Rows));
            }

            return state;
        }

        /// <summary>One panel. Exposed because a caller building panels conditionally wants it.</summary>
        public static OverlayPanel Panel(string title, IReadOnlyList<string> columns, IReadOnlyList<OverlayRow> rows)
        {
            OverlayPanel panel = new OverlayPanel { Title = title ?? string.Empty };

            if (columns != null)
            {
                foreach (string column in columns)
                    panel.Columns.Add(column ?? string.Empty);
            }

            if (rows != null)
            {
                foreach (OverlayRow row in rows)
                {
                    if (row != null)
                        panel.Rows.Add(row);
                }
            }

            return panel;
        }

        /// <summary>One row, in a tone from <see cref="RowTones"/>.</summary>
        public static OverlayRow Row(int tone, params string[] cells)
        {
            OverlayRow row = new OverlayRow { Tone = tone };

            foreach (string cell in cells ?? Array.Empty<string>())
                row.Cells.Add(cell ?? string.Empty);

            return row;
        }
    }
}
