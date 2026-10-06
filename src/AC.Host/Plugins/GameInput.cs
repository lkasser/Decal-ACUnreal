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
    /// A click the overlay makes in the game's window: points of a layout the game draws centred
    /// in its window - at the layout's own size where it fits, shrunk to fit where it does not -
    /// each clicked in turn, a moment apart. AC:Unreal's character select is such a layout: the
    /// retail 800 by 600 one, centred, and drawn at the client's Desktop UI Scale.
    /// </summary>
    public sealed class GameClick
    {
        public GameClick(int layoutWidth, int layoutHeight, params (int X, int Y)[] points)
        {
            LayoutWidth = layoutWidth;
            LayoutHeight = layoutHeight;
            Points = points ?? Array.Empty<(int X, int Y)>();
        }

        public int LayoutWidth { get; }

        public int LayoutHeight { get; }

        public IReadOnlyList<(int X, int Y)> Points { get; }

        /// <summary>
        /// The Desktop UI Scale the player chose in AC:Unreal - 1 to 3, in quarter steps - which the
        /// client draws the layout at, as far as the window lets it: the overlay, which knows the
        /// window, takes it down to the largest quarter step at which an 800 by 600 layout fits, as
        /// the client does. 1, the layout's own size, unless the client's settings say otherwise.
        /// </summary>
        public double UiScale { get; init; } = 1.0;

        public override string ToString()
            => string.Join(", then ", Points.Select(p => $"{p.X},{p.Y}")) + $" of {LayoutWidth}x{LayoutHeight}"
               + (UiScale == 1.0 ? string.Empty : FormattableString.Invariant($" at {UiScale * 100:0.##}%"));
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

        /// <summary>
        /// True when movement keys have been held for three seconds since a change the client has
        /// not answered - it has not once said where the character is since - so the game is not
        /// acting on them. A minimized game may not, which <see cref="IHost.GameWindow"/> says. A
        /// character walking into a wall still answers - the client reports every change of keys -
        /// so this is not a snag; it is the cue to stop holding keys that do nothing. False again
        /// as soon as the client answers, or every movement key is let go.
        /// </summary>
        bool Unheeded { get; }

        /// <summary>
        /// Holds any key down by its Windows virtual-key code, or lets it go - not a movement key
        /// by what it does, but the key itself, whatever the game binds to it: Enter, F12, a
        /// letter. For a Decal plugin that posted keystrokes to the game's window, as Mag-Tools'
        /// jumps and "/mt send" did. Held with the movement keys, until let go or
        /// <see cref="ReleaseAll"/>. False when keys cannot be held now, or for a code outside 1
        /// to 254; an implementation that cannot hold keys by code says false.
        /// </summary>
        bool HoldKey(int virtualKey, bool down) => false;
    }

    /// <summary>
    /// The host's side of <see cref="IGameInput"/>: which keys are held, what each is bound to,
    /// and repeating the set to the overlay while anything is held.
    /// </summary>
    public sealed class GameInput : IGameInput
    {
        /// <summary>How often the held set is repeated. Well inside the overlay's second of patience.</summary>
        public static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(300);

        /// <summary>
        /// How long movement keys may be held, after they last changed, without the client saying
        /// where the character is before they count as <see cref="Unheeded"/>. The client reports
        /// a change of keys within a frame or two, so three seconds is patience, not latency.
        /// </summary>
        public static readonly TimeSpan UnheededAfter = TimeSpan.FromSeconds(3);

        private readonly HashSet<GameKey> _held = new HashSet<GameKey>();
        private readonly Dictionary<GameKey, int> _bindings = new Dictionary<GameKey, int>();
        private readonly List<int> _pressed = new List<int>();
        private readonly List<int> _heldCodes = new List<int>();
        private readonly IPluginLog _log;
        private TimeSpan _sinceSent;
        private TimeSpan _pressedFor;

        // Watching for keys the game does not act on: the client's report count when the keys
        // last changed, how long ago that was, and whether it has answered since.
        private long _reportsAtChange;
        private TimeSpan _sinceChange;
        private bool _answered = true;

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
        /// The keys the retail client bound, which AC:Unreal kept for movement: W and X, A and D
        /// to turn, Z and C to step aside, space to jump, shift to walk. Not S for backward: S is
        /// the retail keymap's Stop, and AC:Unreal's ("Retail X moves backward", its keyboard help
        /// "W/X move"), so holding it held a weaving DoJiggle still.
        /// </summary>
        public static readonly IReadOnlyList<(GameKey Key, int VirtualKey)> Defaults = new[]
        {
            (GameKey.Forward, (int)'W'),
            (GameKey.Backward, (int)'X'),
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

        /// <summary>
        /// Counts the client's own reports of where the character is (CharacterState.ClientReports).
        /// Set by the host; without it nothing is ever <see cref="Unheeded"/>.
        /// </summary>
        public Func<long> ClientReports { get; set; }

        /// <summary>What the game's window is doing, for saying why keys go unheeded. Set by the host.</summary>
        public Func<GameWindowState> Window { get; set; }

        public bool IsAvailable => Publish != null && (Attached?.Invoke() ?? false) && (Allowed?.Invoke() ?? true);

        public IReadOnlyCollection<GameKey> Held => _held.ToArray();

        public bool Unheeded { get; private set; }

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
            {
                Changed();
                Send();
            }

            return true;
        }

        public void ReleaseAll()
        {
            if (_held.Count == 0 && _pressed.Count == 0 && _heldCodes.Count == 0)
                return;

            _held.Clear();
            _pressed.Clear();
            _heldCodes.Clear();
            Changed();
            Send();
        }

        /// <summary>Keys held by their code (<see cref="HoldKey"/>), in the order they went down.</summary>
        public IReadOnlyList<int> HeldKeys => _heldCodes.ToArray();

        public bool HoldKey(int virtualKey, bool down)
        {
            if (virtualKey is < 1 or > 254 || (down && !IsAvailable))
                return false;

            bool changed = down ? !_heldCodes.Contains(virtualKey) : _heldCodes.Remove(virtualKey);
            if (down && changed)
                _heldCodes.Add(virtualKey);

            if (changed)
                Send();

            return true;
        }

        /// <summary>
        /// How long a key pressed once stays down: a few of the game's frames even when it draws
        /// slowly, and well short of the half second before a held key would start repeating.
        /// </summary>
        public static readonly TimeSpan PressLength = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// Presses keys once, together and in the order given, and lets them go a moment later
        /// (<see cref="PressLength"/>). The keys being held stay held. False when keys cannot be
        /// pressed now, or nothing was given to press.
        /// </summary>
        /// <remarks>
        /// Not for a chord: a modifier pressed this way does not reach AC:Unreal's keymap as one.
        /// Ctrl+Q, its Log Out in the retail keymap, came to it live as Q - its autorun, toggled -
        /// and ran the character off. Nor for Enter at its character select, which takes no key;
        /// <see cref="Click"/> enters the world.
        /// </remarks>
        /// <param name="virtualKeys">Windows virtual-key codes, 1 to 254.</param>
        public bool Press(params int[] virtualKeys)
        {
            if (!IsAvailable || virtualKeys == null)
                return false;

            List<int> keys = virtualKeys.Where(code => code is >= 1 and <= 254).Distinct().ToList();
            if (keys.Count == 0)
                return false;

            // A press under way is let go of first, so the two are two presses and not one chord.
            if (_pressed.Count > 0)
            {
                _pressed.Clear();
                Send();
            }

            _pressed.AddRange(keys);
            _pressedFor = TimeSpan.Zero;
            Send();
            return true;
        }

        /// <summary>The keys pressed once and not yet let go of, in the order they went down.</summary>
        public IReadOnlyList<int> Pressed => _pressed.ToArray();

        /// <summary>Sends a click to the overlay, to be made in the game's window. Set by whatever runs the overlay; none can be made while it is null.</summary>
        public Action<GameClick> PublishClick { get; set; }

        /// <summary>Whether a click can be made now: as for keys, and the overlay takes clicks.</summary>
        public bool CanClick => IsAvailable && PublishClick != null;

        /// <summary>
        /// Clicks in the game's window, through the overlay: each point of a layout the game draws
        /// centred in its window, in turn (<see cref="GameClick"/>). For the game's own screens that
        /// take no key - AC:Unreal's character select. False when it cannot be made now.
        /// </summary>
        public bool Click(GameClick click)
        {
            if (!CanClick || click == null || click.Points.Count == 0)
                return false;

            try
            {
                PublishClick(click);
                return true;
            }
            catch (Exception ex)
            {
                _log.Error("Could not send a click to the overlay.", ex);
                return false;
            }
        }

        /// <summary>
        /// Call on the host's tick. Repeats the held set while anything is held, and lets go of
        /// everything once acting is switched off or the overlay goes.
        /// </summary>
        public void Tick(TimeSpan elapsed)
        {
            WatchForAnswer(elapsed);

            if (_held.Count == 0 && _pressed.Count == 0 && _heldCodes.Count == 0)
                return;

            if (!IsAvailable)
            {
                _log.Info("Released the movement keys: " + (!(Allowed?.Invoke() ?? true) ? "acting was switched off." : "the overlay is not attached."));
                ReleaseAll();
                return;
            }

            // A key pressed once goes back up once it has been down long enough to be seen.
            if (_pressed.Count > 0)
            {
                _pressedFor += elapsed;
                if (_pressedFor >= PressLength)
                {
                    _pressed.Clear();
                    Send();
                    return;
                }
            }

            _sinceSent += elapsed;
            if (_sinceSent >= RepeatInterval)
                Send();
        }

        /// <summary>The keys that move the character, and so make the client report where it is when they change.</summary>
        private static bool Moves(GameKey key) => key is not (GameKey.Jump or GameKey.Walk);

        /// <summary>
        /// A change of keys: the client should say where the character is within a frame or two.
        /// The wait is timed from the first change it has not answered, so keys that change often
        /// - a mover's turns - cannot keep putting the question off.
        /// </summary>
        private void Changed()
        {
            // Every movement key let go ends the question.
            if (!_held.Any(Moves))
            {
                Unheeded = false;
                _answered = true;
                return;
            }

            if (!_answered)
                return;

            _reportsAtChange = ClientReports?.Invoke() ?? 0;
            _sinceChange = TimeSpan.Zero;
            _answered = false;
        }

        /// <summary>
        /// Marks the held keys <see cref="Unheeded"/> once the client has said nothing for
        /// <see cref="UnheededAfter"/> since the first change it has not answered, and says so once
        /// - with the game window's state, which is the likeliest reason - and again when the client
        /// answers. Letting go of every movement key ends the question.
        /// </summary>
        private void WatchForAnswer(TimeSpan elapsed)
        {
            if (ClientReports == null || !_held.Any(Moves) || !IsAvailable)
            {
                Unheeded = false;
                _answered = true;
                return;
            }

            if (_answered)
                return;

            if (ClientReports() != _reportsAtChange)
            {
                _answered = true;
                if (Unheeded)
                {
                    Unheeded = false;
                    _log.Info("The game is acting on the movement keys again: the client has said where the character is.");
                }

                return;
            }

            _sinceChange += elapsed;
            if (_sinceChange < UnheededAfter || Unheeded)
                return;

            Unheeded = true;
            GameWindowState window = Window?.Invoke() ?? GameWindowState.Unknown;
            string keys = string.Join(" ", _held.Select(key => Describe(VirtualKey(key))));
            string why = !window.Known
                ? " The overlay has not said what the game window is doing."
                : window.Minimized
                    ? $" The game window is {window.Describe()}: the game is not taking keys while it is minimized."
                    : window.Parked || !window.Drawing
                        ? $" The game window is {window.Describe()}."
                        : " The game window is shown and drawing; check that the keys are the game's own, on Decal's Options page.";
            _log.Warn($"Movement keys {keys} held {UnheededAfter.TotalSeconds:0} s and the client has not said where the character is since: the game is not acting on them.{why}");
        }

        private void Send()
        {
            _sinceSent = TimeSpan.Zero;
            try
            {
                Publish?.Invoke(_held.Select(VirtualKey).Where(code => code != 0).Concat(_heldCodes).Concat(_pressed).Distinct().ToArray());
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
