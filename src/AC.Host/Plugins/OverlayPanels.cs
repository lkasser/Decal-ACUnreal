using System;
using System.Collections.Generic;

namespace AC.Host.Plugins
{
    /// <summary>How a row should read against a dark, moving background.</summary>
    public enum OverlayTone
    {
        Normal = 0,

        /// <summary>Something went the player's way: kept, buffed, taken.</summary>
        Good = 1,

        /// <summary>Something did not: refused, debuffed, a blow landed.</summary>
        Bad = 2,

        /// <summary>Present but not interesting: ignored, expired, left behind.</summary>
        Muted = 3,
    }

    /// <summary>One line in a panel.</summary>
    public sealed class OverlayRow
    {
        public OverlayRow(OverlayTone tone, params string[] cells)
        {
            Tone = tone;
            Cells = cells ?? Array.Empty<string>();
        }

        public OverlayRow(params string[] cells)
            : this(OverlayTone.Normal, cells)
        {
        }

        public IReadOnlyList<string> Cells { get; }

        public OverlayTone Tone { get; }

        /// <summary>
        /// Opaque identity, meaningful only to the plugin that set it. Empty means the
        /// row cannot be acted on.
        /// </summary>
        /// <remarks>
        /// Present so panels need not be permanently read-only: a row with an identity
        /// can be selected, and a command can name it. Retrofitting this after panels
        /// were assumed to be display-only would mean revisiting every one of them.
        /// </remarks>
        public string Id { get; init; } = string.Empty;
    }

    /// <summary>
    /// A table a plugin wants shown.
    /// </summary>
    /// <remarks>
    /// Deliberately just strings. A plugin that wanted to draw would have to link a UI
    /// framework, and the thing actually doing the drawing is a C++ DLL inside the game's
    /// process - so nothing about how this looks can cross that boundary. What crosses is
    /// column headings and rows of text, which is enough for every panel Decal's plugins
    /// ever showed, and leaves the plugin knowing nothing about ImGui, windows or frames.
    /// </remarks>
    public sealed class OverlayPanel
    {
        public OverlayPanel(string title, IReadOnlyList<string> columns, IReadOnlyList<OverlayRow> rows, string key = null)
        {
            Title = title ?? string.Empty;
            Columns = columns ?? Array.Empty<string>();
            Rows = rows ?? Array.Empty<OverlayRow>();

            // Defaults to the title, which is right until a plugin publishes two panels
            // with the same one.
            Key = string.IsNullOrEmpty(key) ? Title : key;
        }

        public string Title { get; }

        /// <summary>
        /// The plugin this came from.
        /// </summary>
        /// <remarks>
        /// Set by the host, not by the plugin: a plugin cannot be trusted to name itself
        /// honestly, and the display groups panels into one window per plugin on the
        /// strength of this. A plugin naming another plugin's window would be a way to
        /// put a panel somewhere it does not belong.
        /// </remarks>
        public string Owner { get; internal set; } = string.Empty;

        /// <summary>
        /// Stable across publishes, unlike the title.
        /// </summary>
        /// <remarks>
        /// A display that remembers anything per panel - a column width, which tab was
        /// open - has to key it to something that does not move when a title is edited or
        /// two panels swap places.
        /// </remarks>
        public string Key { get; }

        public IReadOnlyList<string> Columns { get; }

        public IReadOnlyList<OverlayRow> Rows { get; }
    }

    /// <summary>
    /// Implemented by a plugin that wants panels in the overlay.
    /// </summary>
    /// <remarks>
    /// Optional, and separate from <see cref="IPlugin"/> so that a plugin with nothing to
    /// show carries no UI concepts at all.
    ///
    /// <para>
    /// Asked rather than told: the host calls this when it is about to publish, several
    /// times a second, on the game thread. That means a plugin holds no overlay state and
    /// there is nothing to keep in step - the panels are always whatever the plugin would
    /// say right now. The cost is that this must be cheap and must not block; building a
    /// few dozen rows from state the plugin already has is the intended shape, and
    /// anything that needs computing should be computed when it changes and merely read
    /// here.
    /// </para>
    /// </remarks>
    public interface IOverlayPanels
    {
        /// <summary>
        /// The panels to show now. Return an empty list to contribute nothing this time;
        /// returning null is treated the same way.
        /// </summary>
        IReadOnlyList<OverlayPanel> GetPanels();
    }
}
