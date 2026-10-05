using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AC.Host.Plugins
{
    /// <summary>The game's movement keys, by what they do rather than where they are.</summary>
    public enum GameKey
    {
        Forward,
        Backward,
        TurnLeft,
        TurnRight,
        StrafeLeft,
        StrafeRight,
        Jump,

        /// <summary>Held to walk rather than run.</summary>
        Walk,
    }

    /// <summary>
    /// Holding the game's own movement keys down, as a player would.
    /// </summary>
    /// <remarks>
    /// In Asheron's Call the client decides where its own character is and tells the server;
    /// the server does not move a player about on request. So a plugin that wants to walk does
    /// what a player does: it holds the keys down, the client moves, and the client's own
    /// reports - the character's location - say where it went. The overlay inside the game does
    /// the pressing, so this works only while it is attached, and only while the player lets
    /// plugins act.
    ///
    /// A key stays down until released. The overlay releases everything if the host goes
    /// quiet, so a host that stops does not leave the character running.
    /// </remarks>
    public interface IGameInput
    {
        /// <summary>True when keys can be held: the overlay is attached and acting is allowed.</summary>
        bool IsAvailable { get; }

        /// <summary>Holds a key down, or lets it go. Returns false when keys cannot be held now.</summary>
        bool Hold(GameKey key, bool down);

        /// <summary>Lets go of every key.</summary>
        void ReleaseAll();

        /// <summary>The keys held now.</summary>
        IReadOnlyCollection<GameKey> Held { get; }
    }

    /// <summary>
    /// The host's side of <see cref="IGameInput"/>: which keys are held, what each is bound to,
    /// and repeating the set to the overlay while anything is held.
    /// </summary>
    public sealed class GameInput : IGameInput
    {
        /// <summary>How often the held set is repeated. Well inside the overlay's second of patience.</summary>
        public static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(300);

        private readonly HashSet<GameKey> _held = new HashSet<GameKey>();
        private readonly Dictionary<GameKey, int> _bindings = new Dictionary<GameKey, int>();
        private readonly IPluginLog _log;
        private TimeSpan _sinceSent;

        public GameInput(IPluginLog log, IReadOnlyDictionary<string, string> settings = null)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));

            foreach ((GameKey key, int code) in Defaults)
                _bindings[key] = code;

            // "Decal:Key.Forward=W", "Decal:Key.Jump=32": a letter, a digit, or a virtual-key number.
            if (settings != null)
            {
                foreach (GameKey key in Enum.GetValues<GameKey>())
                {
                    if (settings.TryGetValue("Decal:Key." + key, out string value) && TryParseKey(value, out int code))
                        _bindings[key] = code;
                }
            }
        }

        /// <summary>
        /// The keys the retail client bound, which AC:Unreal kept for movement: W and S, A and D
        /// to turn, Z and C to step aside, space to jump, shift to walk.
        /// </summary>
        public static readonly IReadOnlyList<(GameKey Key, int VirtualKey)> Defaults = new[]
        {
            (GameKey.Forward, (int)'W'),
            (GameKey.Backward, (int)'S'),
            (GameKey.TurnLeft, (int)'A'),
            (GameKey.TurnRight, (int)'D'),
            (GameKey.StrafeLeft, (int)'Z'),
            (GameKey.StrafeRight, (int)'C'),
            (GameKey.Jump, 0x20),
            (GameKey.Walk, 0x10),
        };

        /// <summary>Sends the held set to the overlay, as virtual-key codes. Set by whatever runs the overlay.</summary>
        public Action<IReadOnlyCollection<int>> Publish { get; set; }

        /// <summary>Whether the overlay is attached to receive them.</summary>
        public Func<bool> Attached { get; set; } = () => false;

        /// <summary>Whether the player lets plugins act.</summary>
        public Func<bool> Allowed { get; set; } = () => true;

        public bool IsAvailable => Publish != null && (Attached?.Invoke() ?? false) && (Allowed?.Invoke() ?? true);

        public IReadOnlyCollection<GameKey> Held => _held.ToArray();

        /// <summary>The virtual-key code a key is bound to.</summary>
        public int VirtualKey(GameKey key) => _bindings.TryGetValue(key, out int code) ? code : 0;

        /// <summary>
        /// Binds a key to a virtual-key code - the player's choice in Decal's window, when the
        /// game's keys are not the retail ones. A key held down at the time is released first.
        /// </summary>
        public void Bind(GameKey key, int virtualKey)
        {
            if (virtualKey is < 1 or > 254 || VirtualKey(key) == virtualKey)
                return;

            bool held = _held.Contains(key);
            if (held)
                Hold(key, false);

            _bindings[key] = virtualKey;
        }

        /// <summary>How a key reads in a window: its letter or digit, or its number.</summary>
        public static string Describe(int virtualKey) => virtualKey switch
        {
            >= '0' and <= '9' or >= 'A' and <= 'Z' => ((char)virtualKey).ToString(),
            0x20 => "Space",
            0x10 => "Shift",
            0x11 => "Ctrl",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            _ => virtualKey.ToString(CultureInfo.InvariantCulture),
        };

        public bool Hold(GameKey key, bool down)
        {
            if (down && !IsAvailable)
                return false;

            bool changed = down ? _held.Add(key) : _held.Remove(key);
            if (changed)
                Send();

            return true;
        }

        public void ReleaseAll()
        {
            if (_held.Count == 0)
                return;

            _held.Clear();
            Send();
        }

        /// <summary>
        /// Call on the host's tick. Repeats the held set while anything is held, and lets go of
        /// everything once acting is switched off or the overlay goes.
        /// </summary>
        public void Tick(TimeSpan elapsed)
        {
            if (_held.Count == 0)
                return;

            if (!IsAvailable)
            {
                _log.Info("Released the movement keys: " + (!(Allowed?.Invoke() ?? true) ? "acting was switched off." : "the overlay is not attached."));
                ReleaseAll();
                return;
            }

            _sinceSent += elapsed;
            if (_sinceSent >= RepeatInterval)
                Send();
        }

        private void Send()
        {
            _sinceSent = TimeSpan.Zero;
            try
            {
                Publish?.Invoke(_held.Select(VirtualKey).Where(code => code != 0).ToArray());
            }
            catch (Exception ex)
            {
                _log.Error("Could not send the held keys to the overlay.", ex);
            }
        }

        /// <summary>A key as a setting names it: one letter or digit, or a virtual-key number.</summary>
        public static bool TryParseKey(string text, out int code)
        {
            code = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            text = text.Trim();
            if (text.Length == 1 && char.IsLetterOrDigit(text[0]))
            {
                code = char.ToUpperInvariant(text[0]);
                return true;
            }

            switch (text.ToUpperInvariant())
            {
                case "SPACE": code = 0x20; return true;
                case "SHIFT": code = 0x10; return true;
                case "CTRL": code = 0x11; return true;
                case "LEFT": code = 0x25; return true;
                case "UP": code = 0x26; return true;
                case "RIGHT": code = 0x27; return true;
                case "DOWN": code = 0x28; return true;
            }

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                return code is >= 1 and <= 254;

            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out code) && code is >= 1 and <= 254;
        }
    }
}
