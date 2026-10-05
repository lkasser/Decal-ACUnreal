using System;
using System.Collections.Generic;
using System.Globalization;
using AC.Host.Plugins.Views;

namespace AC.Host.Plugins
{
    /// <summary>What kind of control a plugin wants drawn.</summary>
    public enum OverlayControlKind
    {
        /// <summary>On or off. The value is "true" or "false".</summary>
        Toggle = 0,

        /// <summary>Does something when pressed. Has no value.</summary>
        Button = 1,

        /// <summary>A number between two bounds. The value is the number.</summary>
        Slider = 2,

        /// <summary>One of a fixed list. The value is the option chosen.</summary>
        Choice = 3,

        /// <summary>Free text. The value is the text.</summary>
        Text = 4,
    }

    /// <summary>
    /// A control a plugin wants in its window: the vocabulary real uTank2 pages are built
    /// from, which a table of strings cannot express.
    /// </summary>
    /// <remarks>
    /// The value is always a string so that one field serves every kind, and the plugin
    /// owns the truth of it. The overlay draws what it is given and sends back what the
    /// player did; a change the plugin refuses shows as the control springing back on the
    /// next publish rather than the overlay disagreeing with the plugin about what is set.
    ///
    /// <para>
    /// Built through the static factories, which keep each kind's value in the form the
    /// overlay expects - a toggle's value must be exactly "true" or "false", and a slider's
    /// must parse as a number in the invariant culture, or the overlay draws it wrongly.
    /// A record so that <c>with { Tooltip = ... }</c> works on what a factory returns;
    /// the tooltip is the only thing that can be changed that way.
    /// </para>
    /// </remarks>
    public sealed record OverlayControl
    {
        private OverlayControl(string id, string label, OverlayControlKind kind, string value)
        {
            Id = id ?? string.Empty;
            Label = label ?? string.Empty;
            Kind = kind;
            Value = value ?? string.Empty;
        }

        /// <summary>
        /// What comes back as <see cref="OverlayCommand.ControlId"/>. Must be unique within
        /// the plugin's window, or two controls will be indistinguishable when clicked.
        /// </summary>
        public string Id { get; }

        public string Label { get; }

        public OverlayControlKind Kind { get; }

        public string Value { get; }

        /// <summary>Choice only.</summary>
        public IReadOnlyList<string> Options { get; private set; } = Array.Empty<string>();

        public double Min { get; private set; }

        public double Max { get; private set; } = 1.0;

        /// <summary>Slider only. Zero means continuous.</summary>
        public double Step { get; private set; }

        /// <summary>Shown when the pointer rests on the control. Empty means none.</summary>
        public string Tooltip { get; init; } = string.Empty;

        public static OverlayControl Toggle(string id, string label, bool on)
            => new OverlayControl(id, label, OverlayControlKind.Toggle, on ? "true" : "false");

        public static OverlayControl Button(string id, string label)
            => new OverlayControl(id, label, OverlayControlKind.Button, string.Empty);

        public static OverlayControl Slider(string id, string label, double value, double min, double max, double step = 0)
        {
            if (max < min)
                throw new ArgumentException("A slider's maximum cannot be below its minimum.", nameof(max));

            double clamped = Math.Min(Math.Max(value, min), max);

            return new OverlayControl(
                id,
                label,
                OverlayControlKind.Slider,
                clamped.ToString("R", CultureInfo.InvariantCulture))
            {
                Min = min,
                Max = max,
                Step = step < 0 ? 0 : step,
            };
        }

        public static OverlayControl Choice(string id, string label, string chosen, IReadOnlyList<string> options)
            => new OverlayControl(id, label, OverlayControlKind.Choice, chosen)
            {
                Options = options ?? Array.Empty<string>(),
            };

        public static OverlayControl Text(string id, string label, string text)
            => new OverlayControl(id, label, OverlayControlKind.Text, text);

        /// <summary>
        /// Reads a toggle's new value out of a "set" command, as the overlay writes it.
        /// Anything other than exactly "true" is off, which is the safe reading for a
        /// switch that might mean "reach into the game".
        /// </summary>
        public static bool ParseToggle(string value)
            => string.Equals(value, "true", StringComparison.Ordinal);

        /// <summary>Reads a slider's new value, or returns false if it is not a number.</summary>
        public static bool TryParseSlider(string value, out double number)
            => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>
    /// Implemented by a plugin that wants controls in its window.
    /// </summary>
    /// <remarks>
    /// Asked for when the host is about to publish, several times a second, on the game
    /// thread - the same contract as <see cref="IOverlayPanels"/>, and for the same reason:
    /// the plugin holds no overlay state, so the controls always show whatever it would
    /// say right now. When the player changes one, the plugin hears about it through
    /// <see cref="IOverlayCommands"/> as a "set" or "press" command naming the control.
    /// </remarks>
    public interface IOverlayControls
    {
        IReadOnlyList<OverlayControl> GetControls();
    }

    /// <summary>
    /// A plugin's window as the host declares it: every loaded plugin has one, whether or
    /// not it has anything to show this frame.
    /// </summary>
    /// <remarks>
    /// The explicit plugin list Decal had. Deriving windows from whichever panels happened
    /// to arrive meant a quiet plugin had no window and the bar listed only the talkative
    /// ones.
    /// </remarks>
    public sealed class OverlayWindowInfo
    {
        internal OverlayWindowInfo(string owner, bool enabled, IReadOnlyList<OverlayControl> controls, DecalView view = null, bool startsClosed = false)
        {
            Owner = owner ?? string.Empty;
            Enabled = enabled;
            Controls = controls ?? Array.Empty<OverlayControl>();
            View = view;
            StartsClosed = startsClosed;
        }

        /// <summary>
        /// Whether the window is closed until the player opens it from the bar, rather than open
        /// the first time it is seen. Decal's own window is; plugins' are not.
        /// </summary>
        public bool StartsClosed { get; }

        /// <summary>The plugin's name, as the host knows it.</summary>
        public string Owner { get; }

        /// <summary>False for a plugin whose controls or view could not be read this time.</summary>
        public bool Enabled { get; }

        public IReadOnlyList<OverlayControl> Controls { get; }

        /// <summary>
        /// The plugin's Decal view, when it has one; null otherwise. The live object, not a
        /// copy, so it is read on the game thread like everything else about the view.
        /// </summary>
        public DecalView View { get; }
    }
}
