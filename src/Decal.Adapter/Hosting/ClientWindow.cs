using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using AC.Host.Plugins;
using AC.Host.World;

namespace Decal.Adapter.Hosting
{
    /// <summary>
    /// The game client's window as Decal plugins reach it: the handle <c>Decal.Hwnd</c> gives
    /// them, and the keys they post to it, pressed in the game.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Decal ran inside the client, and its Hwnd was the client's own window. Plugins posted
    /// keystrokes to it with user32's PostMessage: Mag-Tools' jumps ("/mt jumpw 400" holds the
    /// space bar and lets it go with W down), its "/mt send enter", "/mt send f12" and
    /// "/mt send msg ...", which type into the game. Here the client is another process, and its
    /// window is not this host's to hand out: a plugin that moved it, took its frame off or
    /// closed it - Mag-Tools' window options and its "/mt quit" do - would do that to the game.
    /// </para>
    /// <para>
    /// So Decal's Hwnd is a window of this host's own, a message-only one that is never seen:
    /// whatever a plugin does to it touches nothing, and what it posts to it arrives here, on the
    /// game thread. Keys - WM_KEYDOWN and WM_KEYUP and their system forms - are pressed in the
    /// game in the order they were posted, by their virtual-key code through the overlay
    /// (<see cref="IGameInput.HoldKey"/>), while plugins may act. Each is pressed as it arrives,
    /// so a key keeps the time it was held for - Mag-Tools' "/mt jumpw 100" holds space a tenth
    /// of a second - except that changes posted together go <see cref="Apart"/> apart: the
    /// overlay may fold two changes that come close together into one frame of the game's, and
    /// a jump goes where it goes by what is held at the moment space comes up. Between ticks
    /// that takes the host's timer (<see cref="DecalRuntime.Later"/>); without one, one change
    /// goes a tick. Mouse clicks, at places on the old client's panels, cannot be made, and are
    /// said once.
    /// </para>
    /// <para>
    /// Two things plugins did through the old client's window are read for what they meant, and
    /// done through the host instead (<see cref="ReadLine"/>, <see cref="Click"/>):
    /// </para>
    /// <list type="bullet">
    /// <item>A line typed into the chat - Enter, the line's keys, Enter, as Mag-Filter types the
    /// commands it queues for after a login - is run as the chat box ran a line: offered to the
    /// Decal plugins, then to the host's plugins, the game's commands and speech
    /// (<see cref="Wrappers.HooksWrapper.InvokeChatParser"/>). Pressed key by key it would need the
    /// game drawing and its chat box taking them, which a minimized game's does not.</item>
    /// <item>A character chosen on the old client's character select - its row clicked, then
    /// Enter - is the world entered as that character through the host
    /// (<see cref="IGameActions.EnterWorldAsync"/>, which <c>achost ctl login</c> uses): the clicks
    /// were at the retail client's places, as Mag-Filter's next-character and default-character
    /// logins make them. No password is involved: this is the character list, which the
    /// account is already at.</item>
    /// </list>
    /// <para>
    /// Off Windows, or when the window cannot be made, the handle is zero, as it was before -
    /// a post to it reaches nothing.
    /// </para>
    /// </remarks>
    public sealed class ClientWindow : IDisposable
    {
        private const int WmDestroy = 0x0002;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmChar = 0x0102;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        private const int WmMouseFirst = 0x0200;
        private const int WmMouseMove = 0x0200;
        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonUp = 0x0202;
        private const int WmMouseLast = 0x020E;
        private const uint PmRemove = 0x0001;
        private static readonly IntPtr HwndMessage = new IntPtr(-3);

        private const int VkReturn = 0x0D;
        private const int VkShift = 0x10;
        private const int VkLeftShift = 0xA0;
        private const int VkRightShift = 0xA1;

        /// <summary>
        /// How many ticks a line typed into the chat is waited on for its closing Enter before its
        /// keys are pressed after all: Mag-Filter posts the Enter that sends a line a frame after the
        /// line, and a frame here is a tick.
        /// </summary>
        public const int LineWaitTicks = 20;

        /// <summary>
        /// How many ticks after one entering is asked for a click on Enter asks for nothing: a plugin
        /// retrying a login clicks it five times a second (Mag-Filter's, at "a character is still in
        /// the world"), and the host's entering takes seconds.
        /// </summary>
        public const int EnterAgainTicks = 50;

        /// <summary>
        /// The old client's character select, as plugins clicked it in the retail client's window:
        /// the list of characters (CharacterListBox, from x 42, 160 wide), its rows the height of
        /// the list - from 209 to 532, as Mag-Filter places them - shared among the account's
        /// slots, the characters in it by name; and the Enter button (EnterGameButton, 239,289,
        /// 211 by 211) - the retail layout charactermanagement, 0x21000004, at 800 by 600.
        /// </summary>
        internal const int OldListLeft = 42, OldListRight = 202, OldListTop = 209, OldListBottom = 532;

        internal const int OldEnterLeft = 239, OldEnterRight = 450, OldEnterTop = 289, OldEnterBottom = 500;

        /// <summary>
        /// How far apart two key changes posted together are pressed: over a frame of the game's
        /// at thirty frames a second, so the overlay presses each and the game sees each - W down
        /// before space comes up - and short beside the hold times plugins ask for.
        /// </summary>
        public static readonly TimeSpan Apart = TimeSpan.FromMilliseconds(50);

        /// <summary>Every window made, by handle, for the window procedure every one shares.</summary>
        private static readonly Dictionary<IntPtr, ClientWindow> Windows = new Dictionary<IntPtr, ClientWindow>();

        /// <summary>The window procedure, kept for as long as this copy of Decal.Adapter is loaded.</summary>
        private static readonly WndProc Procedure = Proc;

        private readonly DecalRuntime _runtime;
        private readonly List<(int Key, bool Down)> _keys = new List<(int, bool)>();
        private readonly HashSet<int> _down = new HashSet<int>();
        private readonly HashSet<string> _said = new HashSet<string>(StringComparer.Ordinal);
        private string _className;
        private IntPtr _handle;
        private bool _made;
        private bool _closing;
        private long _ticks;

        /// <summary>The tick a line typed into the chat began to be waited on for its closing Enter; -1 when none is.</summary>
        private long _lineSince = -1;

        /// <summary>Whether the next Enter let go is the one that sent a line already run, and goes nowhere.</summary>
        private bool _dropReturnUp;

        /// <summary>
        /// The character the old client's character select has chosen, as far as plugins can tell:
        /// the row last clicked, else the one the client last asked to enter as, else the one last in
        /// the world, which the retail select chose for the player; 0 for none.
        /// </summary>
        private uint _selected;

        /// <summary>The tick the last entering was asked for at; -1 before the first.</summary>
        private long _enterAskedAt = -1;

        /// <summary>When the last change was pressed, by <see cref="Stopwatch"/>; the next waits until <see cref="Apart"/> after it.</summary>
        private long _pressedAt = long.MinValue;

        /// <summary>Whether the host's timer has been asked to press the next change.</summary>
        private bool _waiting;

        internal ClientWindow(DecalRuntime runtime)
        {
            _runtime = runtime;
        }

        /// <summary>
        /// What Decal.Hwnd gives a plugin: a message-only window of this host's, made on the game
        /// thread the first time a plugin asks; zero where there can be none.
        /// </summary>
        public IntPtr Handle
        {
            get
            {
                if (!_made && !_closing)
                {
                    _made = true;
                    _handle = Make();
                }

                return _handle;
            }
        }

        private IntPtr Make()
        {
            if (!OperatingSystem.IsWindows())
                return IntPtr.Zero;

            // A class of its own for each, so that no window outlives the copy of this code its
            // class points at - a reloaded Decal is a new copy.
            _className = "DecalClientWindow-" + Guid.NewGuid().ToString("N");
            WndClassEx type = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
                Instance = GetModuleHandleW(null),
                ClassName = _className,
            };

            if (RegisterClassExW(ref type) == 0)
                return IntPtr.Zero;

            IntPtr handle = CreateWindowExW(0, _className, "Decal", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, type.Instance, IntPtr.Zero);
            if (handle == IntPtr.Zero)
            {
                UnregisterClassW(_className, type.Instance);
                return IntPtr.Zero;
            }

            lock (Windows)
                Windows[handle] = this;
            return handle;
        }

        /// <summary>The keys posted and not yet pressed, in order.</summary>
        public int Waiting => _keys.Count;

        /// <summary>
        /// On the tick: takes what plugins posted since the last, and presses the next key change
        /// waiting in the game - with no timer of the host's, one a tick. Game thread only.
        /// </summary>
        internal void Tick()
        {
            _ticks++;

            // Who the old client's character select had chosen once a character is in the world:
            // the retail select chose the last one played when the player came back to it.
            IWorldView world = _runtime.Host.World;
            if (world.Phase == SessionPhase.InWorld && _runtime.Host.Character.Id != 0)
                _selected = _runtime.Host.Character.Id;

            if (_handle == IntPtr.Zero)
                return;

            // What waits for this window, whoever else pumps the thread's messages.
            for (int i = 0; i < 256 && PeekMessageW(out Msg message, _handle, 0, 0, PmRemove); i++)
                DispatchMessageW(ref message);

            Press();
        }

        /// <summary>
        /// The next key change posted, pressed in the game - unless the keys waiting are a line typed
        /// into the chat, which is run as one instead, or may be once its closing Enter comes.
        /// </summary>
        internal void Press() => Press(onTick: true);

        /// <summary>
        /// The next key change posted, pressed if it is time: at once for one posted on its own,
        /// <see cref="Apart"/> after the last for one posted with others. With the host's timer the
        /// rest wait on it; without, each waits for a tick of its own. Game thread only.
        /// </summary>
        private void Press(bool onTick)
        {
            if (_keys.Count == 0)
            {
                _lineSince = -1;
                return;
            }

            Action<TimeSpan, Action> later = _runtime.Later;
            if (later == null && !onTick)
                return;

            switch (ReadLine(_keys, out string line, out int length))
            {
                case LineRead.Whole:
                    _lineSince = -1;
                    _dropReturnUp = _keys[length - 1] != (VkReturn, false);
                    _keys.RemoveRange(0, length);
                    RunLine(line);
                    return;

                case LineRead.Begun:
                    if (_lineSince < 0)
                        _lineSince = _ticks;
                    if (_ticks - _lineSince < LineWaitTicks)
                        return;

                    // Its Enter never came: they were keys after all, and are pressed as keys.
                    _runtime.Log.Info($"[Decal] A Decal plugin posted Enter and \"{line}\" to the game's window, and no Enter after it in {LineWaitTicks} ticks, so they are pressed as keys.");
                    break;

                default:
                    _lineSince = -1;
                    break;
            }

            IGameInput input = _runtime.Host.Input;
            if (input == null || !input.IsAvailable)
            {
                Note("keys", "A Decal plugin posted keys to the game's window while the game's keys cannot be pressed - the overlay is not attached, or plugins may not act - so they were dropped.");
                _keys.Clear();
                _lineSince = -1;
                LetGo();
                return;
            }

            long now = Stopwatch.GetTimestamp();
            TimeSpan since = _pressedAt == long.MinValue ? TimeSpan.MaxValue : Stopwatch.GetElapsedTime(_pressedAt, now);
            if (later != null && since < Apart)
            {
                Wait(later, Apart - since);
                return;
            }

            (int key, bool down) = _keys[0];
            _keys.RemoveAt(0);
            _pressedAt = now;
            if (input.HoldKey(key, down))
            {
                if (down)
                    _down.Add(key);
                else
                    _down.Remove(key);
            }

            if (later != null && _keys.Count > 0)
                Wait(later, Apart);
        }

        /// <summary>Asks the host's timer to press the next change after a while, unless it already has been.</summary>
        private void Wait(Action<TimeSpan, Action> later, TimeSpan delay)
        {
            if (_waiting)
                return;

            _waiting = true;
            later(delay, () =>
            {
                _waiting = false;
                if (!_closing)
                    Press(onTick: false);
            });
        }

        /// <summary>A message posted or sent to the window, as its procedure has it. Game thread.</summary>
        internal void Take(int message, IntPtr wParam, IntPtr lParam = default)
        {
            switch (message)
            {
                case WmKeyDown:
                case WmSysKeyDown:
                case WmKeyUp:
                case WmSysKeyUp:
                    int key = unchecked((int)wParam.ToInt64()) & 0xFF;
                    bool down = message == WmKeyDown || message == WmSysKeyDown;
                    if (key == VkReturn && !down && _dropReturnUp)
                    {
                        // The Enter that sent a line already run: it was never pressed.
                        _dropReturnUp = false;
                        break;
                    }

                    if (key != 0)
                    {
                        _keys.Add((key, down));

                        // Pressed as soon as what was posted with it has come too - a line typed
                        // into the chat is read whole - by the host's timer, which runs after this.
                        if (_runtime.Later is Action<TimeSpan, Action> later)
                            Wait(later, TimeSpan.Zero);
                    }

                    break;

                case WmChar:
                    // What TranslateMessage makes of a posted key-down, when something pumps the
                    // thread's messages; the key itself is already on its way.
                    break;

                case WmDestroy:
                    if (!_closing)
                        Note("close", "A Decal plugin asked the game's window to close; the game is the player's to close, so nothing was done.");
                    break;

                case WmMouseMove:
                    break;

                case WmLButtonDown:
                    // A click is read from its button coming up, where it is; one that could not be
                    // taken is said from the start.
                    if (!OnOldSelect(X(lParam), Y(lParam)))
                        NoteClicksDropped();
                    break;

                case WmLButtonUp:
                    Click(X(lParam), Y(lParam));
                    break;

                case >= WmMouseFirst and <= WmMouseLast:
                    NoteClicksDropped();
                    break;
            }
        }

        // ------------------------------------------------------------------- a line typed into the chat

        internal enum LineRead
        {
            /// <summary>The keys waiting are not a line typed into the chat.</summary>
            None,

            /// <summary>They begin one - Enter, then a line's keys - whose closing Enter has not come yet.</summary>
            Begun,

            /// <summary>They begin a whole one: Enter, a line's keys, Enter.</summary>
            Whole,
        }

        /// <summary>
        /// Whether <paramref name="keys"/> begin with a line typed into the old client's chat: Enter
        /// pressed and let go, which opened its chat box; keys that type - letters, digits, space,
        /// the punctuation keys, Shift - each pressed and let go; and Enter pressed, which sent it.
        /// <paramref name="length"/> is how many key changes the whole line is, its closing Enter's
        /// release included when it has come.
        /// </summary>
        internal static LineRead ReadLine(IReadOnlyList<(int Key, bool Down)> keys, out string line, out int length)
        {
            line = null;
            length = 0;
            if (keys.Count < 2 || keys[0] != (VkReturn, true) || keys[1] != (VkReturn, false))
                return LineRead.None;

            StringBuilder text = new StringBuilder();
            bool shift = false;
            for (int i = 2; i < keys.Count; i++)
            {
                (int key, bool down) = keys[i];
                if (key == VkReturn && down)
                {
                    if (text.Length == 0)
                        return LineRead.None;

                    line = text.ToString();
                    length = i + 1 < keys.Count && keys[i + 1] == (VkReturn, false) ? i + 2 : i + 1;
                    return LineRead.Whole;
                }

                if (key is VkShift or VkLeftShift or VkRightShift)
                {
                    shift = down;
                    continue;
                }

                char? typed = Typed(key, shift);
                if (typed == null)
                    return LineRead.None;

                if (down)
                    text.Append(typed.Value);
            }

            line = text.ToString();
            return text.Length == 0 ? LineRead.None : LineRead.Begun;
        }

        /// <summary>What a key types on a US keyboard, as the old client's chat box took it; null for one that types nothing.</summary>
        internal static char? Typed(int key, bool shift)
        {
            const string Digits = "0123456789", ShiftedDigits = ")!@#$%^&*(";
            const string Punctuation = ";=,-./`", ShiftedPunctuation = ":+<_>?~";
            const string Brackets = "[\\]'", ShiftedBrackets = "{|}\"";

            return key switch
            {
                0x20 => ' ',
                >= 0x30 and <= 0x39 => (shift ? ShiftedDigits : Digits)[key - 0x30],
                >= 0x41 and <= 0x5A => (char)(shift ? key : key + 0x20),
                >= 0x60 and <= 0x69 => (char)('0' + key - 0x60),
                0x6A => '*',
                0x6B => '+',
                0x6D => '-',
                0x6E => '.',
                0x6F => '/',
                >= 0xBA and <= 0xC0 => (shift ? ShiftedPunctuation : Punctuation)[key - 0xBA],
                >= 0xDB and <= 0xDE => (shift ? ShiftedBrackets : Brackets)[key - 0xDB],
                _ => null,
            };
        }

        /// <summary>Runs a line a plugin typed into the chat as the chat box ran one.</summary>
        private void RunLine(string line)
        {
            _runtime.Log.Info($"[Decal] A Decal plugin typed \"{line}\" into the chat - Enter, the line, Enter, posted to the game's window: run as the chat box runs a line.");
            _runtime.Core.Actions.InvokeChatParser(line);
        }

        // ------------------------------------------------------------------- the character select

        /// <summary>
        /// A click a plugin posted, at a place in the old client's window. On its character select,
        /// with the session at the character list: a character's row chooses it, and Enter enters
        /// the world as the one chosen, through the host. Anywhere else it cannot be made.
        /// </summary>
        internal void Click(int x, int y)
        {
            IWorldView world = _runtime.Host.World;
            if (!OnOldSelect(x, y))
                NoteClicksDropped();
            else if (InOldList(x, y))
                Choose(world, y);
            else
                EnterChosen(world);
        }

        /// <summary>Whether a click there is one taken: on the old client's character list or its Enter, with the session at the character list.</summary>
        private bool OnOldSelect(int x, int y)
            => _runtime.Host.World.Phase == SessionPhase.CharacterList && (InOldList(x, y) || InOldEnter(x, y));

        private static bool InOldList(int x, int y) => x >= OldListLeft && x < OldListRight && y >= OldListTop && y < OldListBottom;

        private static bool InOldEnter(int x, int y) => x >= OldEnterLeft && x < OldEnterRight && y >= OldEnterTop && y < OldEnterBottom;

        /// <summary>A mouse message's point, as its lParam carries it: x in the low word, y in the high, each signed.</summary>
        private static int X(IntPtr lParam) => unchecked((short)(lParam.ToInt64() & 0xFFFF));

        private static int Y(IntPtr lParam) => unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));

        /// <summary>
        /// The account's characters in the order the old client's select listed them, which is the
        /// order plugins count them in: by name, as Mag-Filter sorts them.
        /// </summary>
        internal static IReadOnlyList<AccountCharacter> AsListed(IWorldView world)
            => world.AccountCharacters.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();

        /// <summary>The row of the old client's character list at <paramref name="y"/>: the list's height shared among the account's slots.</summary>
        internal static int RowAt(int y, int slots, int characters)
        {
            int rows = slots > 0 ? slots : Math.Max(1, characters);
            return (int)((y - OldListTop) * rows / (float)(OldListBottom - OldListTop));
        }

        private void Choose(IWorldView world, int y)
        {
            IReadOnlyList<AccountCharacter> listed = AsListed(world);
            int row = RowAt(y, world.CharacterSlots, listed.Count);
            if (row < 0 || row >= listed.Count)
            {
                _runtime.Log.Info($"[Decal] A Decal plugin clicked row {row + 1} of the old client's character select, which holds no character; the choice stands.");
                return;
            }

            _selected = listed[row].Id;
            _runtime.Log.Info($"[Decal] A Decal plugin chose {listed[row]} on the old client's character select (row {row + 1} of {listed.Count}, by name).");
        }

        private void EnterChosen(IWorldView world)
        {
            AccountCharacter chosen = world.AccountCharacters.FirstOrDefault(c => c.Id == _selected);
            if (chosen == null)
            {
                Note("enter-none", "A Decal plugin clicked Enter on the old client's character select with no character of this account chosen, so nothing was entered.");
                return;
            }

            // A plugin retrying clicks it again and again; the entering asked for is still going.
            if (_enterAskedAt >= 0 && _ticks - _enterAskedAt < EnterAgainTicks)
                return;

            _enterAskedAt = _ticks;
            IGameActions actions = _runtime.Host.Actions;
            _runtime.Log.Info($"[Decal] A Decal plugin clicked Enter on the old client's character select: entering the world as {chosen} through the host, as `achost ctl login` does.");
            Task<bool> asked = actions?.EnterWorldAsync(chosen.Id);
            if (asked == null || (asked.IsCompleted && !asked.Result))
                Note("enter-refused", "The host could not enter the world for a Decal plugin just now; the log says why. A later click on Enter asks again.");
        }

        /// <summary>The client named the character it is entering as: what its character select had chosen.</summary>
        internal void NoteEntering(uint characterId)
        {
            if (characterId != 0)
                _selected = characterId;
        }

        private void NoteClicksDropped()
            => Note("mouse", "A Decal plugin posted mouse clicks to the game's window; the overlay presses keys but cannot click, and the places clicked were the old client's, so they were dropped"
                + " - all but a character's row and Enter on its character select, which enter the world through the host.");

        /// <summary>Says something once a session, by its kind: plugins repeat what they do.</summary>
        private void Note(string kind, string text)
        {
            if (_said.Add(kind))
                _runtime.Log.Info("[Decal] " + text);
        }

        /// <summary>Lets go of every key a plugin's posts left down.</summary>
        private void LetGo()
        {
            IGameInput input = _runtime.Host.Input;
            foreach (int key in _down)
                input?.HoldKey(key, false);
            _down.Clear();
        }

        public void Dispose()
        {
            _keys.Clear();
            LetGo();
            _closing = true;
            if (_handle == IntPtr.Zero)
                return;

            DestroyWindow(_handle);
            lock (Windows)
                Windows.Remove(_handle);
            UnregisterClassW(_className, GetModuleHandleW(null));
            _handle = IntPtr.Zero;
        }

        private static IntPtr Proc(IntPtr window, int message, IntPtr wParam, IntPtr lParam)
        {
            ClientWindow owner;
            lock (Windows)
                Windows.TryGetValue(window, out owner);

            if (owner != null)
            {
                try
                {
                    owner.Take(message, wParam, lParam);
                }
                catch (Exception ex)
                {
                    owner._runtime.Log.Error("[Decal] A message to Decal's window could not be taken.", ex);
                }
            }

            return DefWindowProcW(window, message, wParam, lParam);
        }

        private delegate IntPtr WndProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WndClassEx
        {
            public uint Size;
            public uint Style;
            public IntPtr Procedure;
            public int ClassExtra;
            public int WindowExtra;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            public string MenuName;
            public string ClassName;
            public IntPtr SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public IntPtr Hwnd;
            public uint Message;
            public UIntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X;
            public int Y;
            public uint Private;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassExW(ref WndClassEx type);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterClassW(string className, IntPtr instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height,
                                                     IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessageW(out Msg message, IntPtr window, uint filterMin, uint filterMax, uint remove);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessageW(ref Msg message);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string moduleName);
    }
}
