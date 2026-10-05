using System;

namespace AC.Host.Plugins
{
    /// <summary>
    /// Something the player did in the overlay, on its way to the plugin whose window it
    /// happened in.
    /// </summary>
    /// <remarks>
    /// Deliberately just names and strings, for the same reason panels are: it crosses a
    /// process boundary from a C++ DLL that knows nothing about any plugin. The plugin
    /// decides what a name means.
    /// </remarks>
    public sealed class OverlayCommand
    {
        public OverlayCommand(string name, string value = null, string rowId = null, string controlId = null)
        {
            Name = name ?? string.Empty;
            Value = value ?? string.Empty;
            RowId = rowId ?? string.Empty;
            ControlId = controlId ?? string.Empty;
        }

        /// <summary>
        /// The control acted on, when the command came from one - the <c>Id</c> the plugin
        /// gave it. The name is then "set", with the new value, or "press" for a button.
        /// Empty otherwise.
        /// </summary>
        public string ControlId { get; }

        /// <summary>What was done: "toggle-looting", "reload-profile", and so on.</summary>
        public string Name { get; }

        /// <summary>An argument, when the action carries one. Empty otherwise.</summary>
        public string Value { get; }

        /// <summary>
        /// The row it was done to, when it was done to one - the <c>Id</c> the plugin put
        /// on that row. Empty otherwise.
        /// </summary>
        public string RowId { get; }

        public override string ToString()
            => Name
             + (ControlId.Length > 0 ? $" {ControlId}" : string.Empty)
             + (Value.Length > 0 ? $" ({Value})" : string.Empty)
             + (RowId.Length > 0 ? $" on {RowId}" : string.Empty);
    }

    /// <summary>
    /// Implemented by a plugin that wants to hear what the player did in its window.
    /// </summary>
    /// <remarks>
    /// The other half of <see cref="IOverlayPanels"/>: that puts tables in front of the
    /// player, this brings clicks back. Optional and separate for the same reason - a
    /// plugin with nothing clickable carries no notion of commands at all.
    ///
    /// <para>
    /// Called on the game thread, like every other callback, so a handler may change
    /// the plugin's state freely. The host routes each command to the plugin whose
    /// window it came from and to no other: a plugin never sees another plugin's clicks.
    /// </para>
    /// </remarks>
    public interface IOverlayCommands
    {
        /// <summary>
        /// Acts on a command. Return true if it was recognised, so the host can say when
        /// one was not - a misspelt command name is otherwise a click that silently does
        /// nothing.
        /// </summary>
        bool HandleCommand(OverlayCommand command);
    }
}
