using System;
using System.Collections.Generic;

namespace AC.Host.Plugins
{
    /// <summary>
    /// Implemented by a plugin that answers chat commands - "/mt ...", "/sort ..." - so that a
    /// line meant for it can reach it from elsewhere than the chat bar: another plugin running
    /// a scripted command, as Virindi Tank's meta ran Mag-Tools'.
    /// </summary>
    /// <remarks>
    /// Called on the game thread. Return true only for a line the plugin recognised as its
    /// own; the host offers the line to each such plugin in turn until one does.
    /// </remarks>
    public interface IChatCommands
    {
        bool TryCommand(string text);

        /// <summary>
        /// The words this plugin's commands begin with, without the slash - "vt" for
        /// "/vt start" and "@vt start". A line the player types that begins with one is kept
        /// from the server and given to the plugin, where the relay can do that (see
        /// <see cref="AC.Host.Transport.ITypedCommandSource"/>). None by default, since a plugin
        /// that parses every line itself, as Decal's did, cannot say in advance which it wants.
        /// </summary>
        /// <remarks>Read on the game thread when plugins come and go, not on every line.</remarks>
        IReadOnlyCollection<string> CommandWords => Array.Empty<string>();
    }

    /// <summary>What became of a line run as though the player had typed it (<see cref="IHost.RunChatCommand"/>).</summary>
    public enum ChatCommandOutcome
    {
        /// <summary>Nothing was run: the line was empty.</summary>
        Empty,

        /// <summary>A plugin took it as one of its own commands.</summary>
        Plugin,

        /// <summary>Sent to the server as what the game client itself would have sent for it.</summary>
        Sent,

        /// <summary>
        /// A command the game client carries out by itself - its own windows, its own
        /// settings - which nothing outside the client can reach.
        /// </summary>
        ClientOnly,

        /// <summary>
        /// A command the server would carry out, not sent: acting is off, or it needs what is
        /// not known yet - whom a "/r" replies to, a channel the server has not named.
        /// </summary>
        NotSent,

        /// <summary>No plugin took it, and it is not a command the game client has.</summary>
        Unknown,
    }
}
