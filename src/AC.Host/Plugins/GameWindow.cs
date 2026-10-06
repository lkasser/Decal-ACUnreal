using System;
using System.Collections.Generic;

namespace AC.Host.Plugins
{
    /// <summary>
    /// What the game's window is doing, as the overlay inside the game says: minimized or not,
    /// drawing or not, and whether it is parked off-screen in place of minimized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The player may put the game away to spare the machine and leave the plugins playing.
    /// Everything a plugin does over the network goes on regardless - casting, attacking, using,
    /// looting, chat - since the client keeps its connection whatever its window does. Walking
    /// is the exception: it is done with the game's own keys, which the game reads from its
    /// window, and whether Unreal reads them while that window is minimized is up to Unreal.
    /// <see cref="IGameInput.Unheeded"/> says when it does not.
    /// </para>
    /// <para>
    /// A player who asks to keep playing while minimized gets the window parked instead: moved
    /// off every screen and slowed to about thirty frames a second, never minimized, so the game
    /// goes on taking its keys. It comes back where it was when the player brings it back.
    /// </para>
    /// </remarks>
    public sealed class GameWindowState : IEquatable<GameWindowState>
    {
        /// <summary>Nothing said yet: no overlay, or one too old to say.</summary>
        public static readonly GameWindowState Unknown = new GameWindowState(known: false, minimized: false, drawing: true, parked: false);

        public GameWindowState(bool known, bool minimized, bool drawing, bool parked)
        {
            Known = known;
            Minimized = minimized;
            Drawing = drawing;
            Parked = parked;
        }

        /// <summary>Whether the overlay has said; false while there is no overlay.</summary>
        public bool Known { get; }

        /// <summary>The window is minimized.</summary>
        public bool Minimized { get; }

        /// <summary>The game has presented a frame in the last two seconds.</summary>
        public bool Drawing { get; }

        /// <summary>The window is off-screen in place of minimized, for a player who asked to keep playing.</summary>
        public bool Parked { get; }

        /// <summary>Minimized or parked: the player has put the game away.</summary>
        public bool PutAway => Minimized || Parked;

        /// <summary>
        /// Reads the overlay's "game-window" command: "minimized,drawing,parked", each 0 or 1.
        /// False, and <see cref="Unknown"/>, for anything else.
        /// </summary>
        public static bool TryParse(string value, out GameWindowState state)
        {
            state = Unknown;
            string[] parts = (value ?? string.Empty).Split(',');
            if (parts.Length < 3)
                return false;

            bool?[] flags = new bool?[3];
            for (int i = 0; i < 3; i++)
                flags[i] = parts[i].Trim() switch { "1" => true, "0" => false, _ => null };

            if (flags[0] == null || flags[1] == null || flags[2] == null)
                return false;

            state = new GameWindowState(known: true, minimized: flags[0].Value, drawing: flags[1].Value, parked: flags[2].Value);
            return true;
        }

        /// <summary>A few words for a log line or a status line: "minimized, no frames", "shown, drawing".</summary>
        public string Describe()
        {
            if (!Known)
                return "not known (no overlay)";

            List<string> words = new List<string>
            {
                Parked ? "parked off-screen in place of minimized" : Minimized ? "minimized" : "shown",
                Drawing ? "drawing" : "no frames",
            };
            return string.Join(", ", words);
        }

        public bool Equals(GameWindowState other)
            => other is not null && Known == other.Known && Minimized == other.Minimized && Drawing == other.Drawing && Parked == other.Parked;

        public override bool Equals(object obj) => Equals(obj as GameWindowState);

        public override int GetHashCode() => HashCode.Combine(Known, Minimized, Drawing, Parked);

        public override string ToString() => Describe();
    }
}
