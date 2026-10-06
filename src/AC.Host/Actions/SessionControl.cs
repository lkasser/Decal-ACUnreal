using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AC.Host.Decoding;
using AC.Host.Plugins;
using AC.Host.Transport;
using AC.Host.World;
using AC.Protocol;

namespace AC.Host.Actions
{
    /// <summary>Which way the character is taken out of the world, or brought into it.</summary>
    /// <remarks>
    /// AC:Unreal settles which way works, whatever is asked for: it takes a logoff it did not ask
    /// for, and has no key that logs out without moving the character; and it enters the world only
    /// from its own character select. So a logout always goes by the message, and entering always by
    /// the character select's own Enter, clicked. "keys" and "messages" are still read, and either
    /// that cannot be had is said in the log.
    /// </remarks>
    public enum SessionRoute
    {
        /// <summary>The way that works: the message to log out, the character select's Enter to come in.</summary>
        Auto,

        /// <summary>
        /// The client's own way. To come in: its character select clicked through the overlay - the
        /// character's row, then Enter - and the client sending what it always sends. To log out
        /// there is none to be had: AC:Unreal's Log Out is bound only with a modifier - the retail
        /// keymap's Ctrl+Q and Alt+X - and the modifier the overlay holds does not reach it, so live
        /// Ctrl+Q was taken as Q, its autorun, and ran the character into a fence. A logout asked
        /// for by keys goes by the message.
        /// </summary>
        Keys,

        /// <summary>
        /// The protocol's way: the message the client would send put into its stream by the relay,
        /// the client left to follow the server's answer. To log out: CharacterLogOff, which
        /// AC:Unreal takes up - it goes back to its character select, still connected. To come in
        /// there is none to be had: AC:Unreal took the server's login without asking - its log has
        /// the PlayerCreate, and it even sent LoginComplete - but its character select stayed up,
        /// since only its own Enter moves it to the world. Entering asked for by messages goes by
        /// the character select's Enter.
        /// </summary>
        Messages,
    }

    /// <summary>What the game client is showing, as its own log says - or Unknown when there is no log to read.</summary>
    public enum ClientScreen
    {
        Unknown,

        /// <summary>The login screen, where the password is asked for: nothing here goes past it.</summary>
        Login,

        /// <summary>The character select.</summary>
        CharacterSelect,

        /// <summary>Between two screens - the character select gone and the world not yet up.</summary>
        Changing,

        /// <summary>In the game, the world drawn.</summary>
        InGame,
    }

    /// <summary>One logout or one entering of the world, from being asked for until it is done or given up on.</summary>
    public sealed class SessionRequest
    {
        private readonly TaskCompletionSource<string> _finished = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        internal SessionRequest(bool loggingOut, SessionRoute asked, AccountCharacter character, DateTimeOffset now)
        {
            LoggingOut = loggingOut;
            Asked = asked;
            Character = character;
            Began = now;
        }

        /// <summary>True for a logout, false for entering the world.</summary>
        public bool LoggingOut { get; }

        /// <summary>The way it was asked to go.</summary>
        public SessionRoute Asked { get; }

        /// <summary>
        /// The way it is going: <see cref="SessionRoute.Messages"/> for a logout,
        /// <see cref="SessionRoute.Keys"/> - the character select clicked - for entering.
        /// </summary>
        public SessionRoute Route { get; internal set; }

        /// <summary>The character entering the world; null for a logout.</summary>
        public AccountCharacter Character { get; }

        public DateTimeOffset Began { get; }

        /// <summary>What it is waiting for now, in a few words.</summary>
        public string Waiting { get; internal set; } = string.Empty;

        /// <summary>Null while under way; then whether it did what it was asked.</summary>
        public bool? Succeeded { get; private set; }

        /// <summary>How it ended, in a sentence; null while under way.</summary>
        public string Outcome { get; private set; }

        /// <summary>Completes with <see cref="Outcome"/> once it has ended, either way.</summary>
        public Task<string> Finished => _finished.Task;

        /// <summary>"log out", or "enter the world as Testchar I".</summary>
        public string What => LoggingOut ? "log out" : "enter the world as " + Character.Name;

        internal Stage Stage { get; set; }

        internal DateTimeOffset StageSince { get; set; }

        /// <summary>The character in the world when a logout was asked for, named in what is said of it.</summary>
        internal string Leaving { get; set; }

        /// <summary>Whether the client's LoginComplete has come while its log still showed something other than the game.</summary>
        internal bool SaidLoginComplete { get; set; }

        internal void Finish(bool succeeded, string outcome)
        {
            if (Succeeded.HasValue)
                return;

            Succeeded = succeeded;
            Outcome = outcome;
            Waiting = string.Empty;
            _finished.TrySetResult(outcome);
        }

        public override string ToString()
            => Succeeded.HasValue ? Outcome : $"{What} by {(Route == SessionRoute.Keys ? "the character select's Enter" : "messages")}: {Waiting}";
    }

    /// <summary>What a <see cref="SessionRequest"/> is waiting for.</summary>
    internal enum Stage
    {
        /// <summary>The character select was clicked; the client's own request to enter should follow.</summary>
        ClientToAsk,

        /// <summary>The logoff was asked for; the server's answer should follow in about six seconds.</summary>
        ServerToLogOff,

        /// <summary>The server has logged the character off; the client should be at its character select.</summary>
        ClientToSettle,

        /// <summary>The client stayed in the world, and the player was asked to log out in the game; the client's own logoff should follow.</summary>
        ClientToAskAgain,

        /// <summary>The client asked to enter; it should name its character once the server is ready.</summary>
        ClientToName,

        /// <summary>The character was named; the server should create it.</summary>
        CharacterToArrive,

        /// <summary>The server has created the character; the client should show the world.</summary>
        ClientToFinish,

        /// <summary>
        /// Asked for while a logout was being finished - the server had the character out, and the
        /// client was being watched - so it waits for that to finish, and starts once it has.
        /// </summary>
        AfterLogOut,
    }

    /// <summary>
    /// Takes the character out of the world to the character list and brings one back in, for
    /// plugins (<see cref="IGameActions.LogOutAsync"/>, <see cref="IGameActions.EnterWorldAsync"/>),
    /// Decal's Hooks.Logout and <c>achost ctl logout</c> / <c>login</c> - and watches that both ends
    /// agree afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Neither needs the password: logging out to the character list keeps the account connected,
    /// and entering the world names only the character. A session that has ended - the client
    /// disconnected, booted, closed - needs a full login, with the password, and nothing here
    /// attempts one.
    /// </para>
    /// <para>
    /// As captured: the client logs off by sending CharacterLogOff (0xF653, empty) - again every
    /// two seconds until answered - and ACE answers some six seconds later with CharacterLogOff,
    /// the character list and its name, in one packet. It enters by sending
    /// CharacterEnterWorldRequest (0xF7C8, empty); ACE answers CharacterEnterWorldServerReady
    /// (0xF7DF); the client names the character, CharacterEnterWorld (0xF657: the id and the
    /// account's name); and the login follows.
    /// </para>
    /// <para>
    /// As seen live (the Virindi Tank repository's docs/live-tests/live-2026-10-05-b.md, section 7): AC:Unreal takes a logoff it
    /// did not ask for - the relay puts CharacterLogOff in its stream, and it goes back to its
    /// character select, still connected - so a logout is always that message. It has no key that
    /// logs out safely: its Log Out is bound only with a modifier, and Ctrl+Q pressed through the
    /// overlay came to it as Q, its autorun, toggled. Before the message goes, every key held for
    /// plugins is let go, so the client is left moving nothing. It does not take a login it did not
    /// ask for: the character select stays up, though the client logs the PlayerCreate and sends
    /// LoginComplete. Its character select takes no Enter key either - the two live presses did
    /// nothing - so entering is the character select clicked as a player clicks it: the character's
    /// row, then its Enter button, by the overlay, at their places in the retail layout
    /// (charactermanagement, 0x21000004) that AC:Unreal draws centred - at its own size, or at the
    /// client's Desktop UI Scale when the player has raised it, which the click is given.
    /// </para>
    /// <para>
    /// Everything is checked once done: after a logout, that the client went back to its character
    /// select - by its own log when it can be read, else by its no longer acting in the world - and,
    /// if not, the player asked to log out in the game, whose request is answered with the server's
    /// own words; after entering, that the client is really in the world - its log showing the game
    /// when it can be read, its LoginComplete when not, after the server created the character - and,
    /// if the client is still at its character select when it should have arrived, the character
    /// logged out again so that both ends are at the character list.
    /// </para>
    /// <para>
    /// Entering asked for while a logout is being finished - the server has the character out, and
    /// the client is being watched to be back at its character select - waits for that and starts
    /// once the logout is done, rather than being refused: a plugin that logs one character out and
    /// another in, as Mag-Filter does for a meta that goes through the account's characters, asks
    /// as soon as the character list comes.
    /// </para>
    /// <para>
    /// Game thread only.
    /// </para>
    /// </remarks>
    public sealed class SessionControl
    {
        /// <summary>How long the client has to ask to enter after its character select's Enter is clicked: the click itself takes about a second.</summary>
        public TimeSpan ClientAnswers { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>How long the server has to log the character off. ACE takes about six seconds.</summary>
        public TimeSpan ServerLogsOff { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>
        /// How long after the server has logged the character off the client is watched before the
        /// logout is called done: long enough for its log to say which screen it shows, or its acting
        /// in the world to say it never left - and for a client that drops the session at its
        /// character select, as every captured logoff was followed by within a second and a half, to
        /// have done so.
        /// </summary>
        public TimeSpan ClientSettles { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>What the client sends in the moment the server's answer is on its way is not held against it.</summary>
        public TimeSpan InFlight { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>How long the server has to answer a request to enter, and then to create the character.</summary>
        public TimeSpan ServerAnswers { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>How long the client has to show the world - loading where the character stands - once the server has created it.</summary>
        public TimeSpan ClientFinishes { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>How long the player has to log out in the game when the client stayed in the world.</summary>
        public TimeSpan PlayerLogsOut { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>ACE's CharacterError for a character still in the world, as it can be just after logging off.</summary>
        public const uint CharacterStillInWorld = 13;

        /// <summary>
        /// AC:Unreal's character select: the retail layout charactermanagement (0x21000004), 800 by
        /// 600, which it draws centred - at 560,240 in the live 1920 by 1080 window at its own size.
        /// Since release 94 the client's Desktop UI Scale draws it larger, as it does every one of
        /// its widgets: 1400 by 1050 at 175%, as far as a 1920 by 1080 window lets 200% go.
        /// </summary>
        public const int SelectWidth = 800, SelectHeight = 600;

        /// <summary>The middle of the character select's Enter button (EnterGameButton: 239,289, 211 by 211).</summary>
        public static readonly (int X, int Y) SelectEnter = (344, 394);

        /// <summary>
        /// The character select's list (CharacterListBox: 42,212, 160 by 320, twenty rows of the
        /// 160 by 16 CharacterSlotTemplate) - each character's row, in the order the server lists
        /// them, is clicked in its middle.
        /// </summary>
        public static readonly (int X, int Y) SelectList = (42, 212);

        public const int SelectRowWidth = 160, SelectRowHeight = 16, SelectRows = 20;

        private readonly ClientActions _actions;
        private readonly IGameTransport _transport;
        private readonly WorldState _world;
        private readonly IPluginLog _log;
        private readonly Func<GameInput> _keys;
        private readonly Func<GameWindowState> _window;
        private readonly Func<double> _uiScale;

        private SessionRequest _request;

        /// <summary>Entering asked for while the logout under way was being finished, to start once it is; null when none waits.</summary>
        private SessionRequest _next;
        private byte[] _lastCharacterList;
        private byte[] _lastServerName;
        private DateTimeOffset _clientActedAt;

        /// <param name="keys">The game's keys and clicks, made through the overlay; none when null.</param>
        /// <param name="window">What the overlay says the game's window is doing; not known when null.</param>
        /// <param name="uiScale">The client's Desktop UI Scale, as its settings say; 1 when null.</param>
        public SessionControl(ClientActions actions, IGameTransport transport, WorldState world, IPluginLog log, Func<GameInput> keys = null, Func<GameWindowState> window = null,
                              Func<double> uiScale = null)
        {
            _actions = actions ?? throw new ArgumentNullException(nameof(actions));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _keys = keys ?? (() => null);
            _window = window ?? (() => GameWindowState.Unknown);
            _uiScale = uiScale ?? (() => ClientDisplay.DefaultUiScale);
        }

        /// <summary>The request under way, or the last one to finish; null before the first.</summary>
        public SessionRequest Current => _request;

        /// <summary>What the client is showing, by its own log; Unknown while that is not being read.</summary>
        public ClientScreen Screen { get; private set; }

        /// <summary>When <see cref="Screen"/> last changed.</summary>
        public DateTimeOffset ScreenSince { get; private set; }

        /// <summary>The point of the character select clicked to choose a character: the middle of its row.</summary>
        public static (int X, int Y) SelectRow(int index)
            => (SelectList.X + SelectRowWidth / 2, SelectList.Y + index * SelectRowHeight + SelectRowHeight / 2);

        // ------------------------------------------------------------------- asking

        /// <summary>Logs the character out. False when it cannot be asked for now; the log says why.</summary>
        public Task<bool> LogOutAsync() => Task.FromResult(BeginLogOut(SessionRoute.Auto, out _) != null);

        /// <summary>Enters the world as the character with this id, from the character select.</summary>
        public Task<bool> EnterWorldAsync(uint characterId) => Task.FromResult(BeginEnterWorld(characterId, SessionRoute.Auto, out _) != null);

        /// <summary>
        /// Starts logging the character out to the character list, by the message whatever the
        /// way asked for (<see cref="SessionRoute"/>). Null, with why, when it cannot be asked for
        /// now: acting off, no character in the world, one already leaving, or another request
        /// under way.
        /// </summary>
        public SessionRequest BeginLogOut(SessionRoute route, out string refusal)
        {
            refusal = Refusal();
            if (refusal == null && _world.Phase != SessionPhase.InWorld)
            {
                refusal = _world.Phase switch
                {
                    SessionPhase.LoggingOff => "the character is already logging off",
                    SessionPhase.CharacterList => "no character is in the world: the session is at the character list",
                    SessionPhase.EnteringWorld => "a character is still entering the world",
                    _ => "no character is known to be in the world",
                };
            }

            if (refusal != null)
            {
                _log.Warn("Cannot log out: " + refusal + ".");
                return null;
            }

            SessionRequest request = new SessionRequest(true, route, null, _world.Now)
            {
                Leaving = Describe(_world.Character),
            };
            _request = request;

            if (route == SessionRoute.Keys)
            {
                _log.Info($"Logging out {request.Leaving} by keys cannot be done safely in AC:Unreal: its Log Out is bound only with a modifier"
                    + " (the retail keymap's Ctrl+Q and Alt+X), which the overlay's keys do not carry to it - live, Ctrl+Q was taken as Q, its autorun."
                    + " Asking the server instead, as the client does.");
            }

            LetGoOfKeys(request);
            SendLogOff(request);
            return request;
        }

        /// <summary>
        /// Starts entering the world as one of the account's characters, from the character select:
        /// its row and Enter clicked, whatever the way asked for (<see cref="SessionRoute"/>). Null,
        /// with why, when it cannot be asked for now: acting off, not at the character list, no such
        /// character, one being deleted, the client at its login screen or still in the game, the
        /// overlay not there to click, the game minimized, or another request under way. Asked for
        /// while a logout is being finished - the server has the character out - it waits for that,
        /// and starts once the logout is done (<see cref="Stage.AfterLogOut"/>).
        /// </summary>
        public SessionRequest BeginEnterWorld(uint characterId, SessionRoute route, out string refusal)
        {
            SessionRequest settling = SettlingLogOut();
            refusal = settling != null && _next == null ? ActingRefusal() : Refusal();
            AccountCharacter character = _world.AccountCharacters.FirstOrDefault(c => c.Id == characterId);

            if (refusal == null)
            {
                // What the client shows is read again once the logout is done: until then it may
                // still be leaving the game.
                refusal = EnterRefusal(characterId, character, readScreen: settling == null);
            }

            if (refusal != null)
            {
                _log.Warn("Cannot enter the world: " + refusal + ".");
                return null;
            }

            SessionRequest request = new SessionRequest(false, route, character, _world.Now);
            if (settling != null)
            {
                request.Route = SessionRoute.Keys;
                _next = request;
                Enter(request, Stage.AfterLogOut, "the logout to be done");
                _log.Info($"Entering the world as {Describe(character)} once logging {settling.Leaving} out is done: the server has the character out,"
                    + " and the client is being watched to be back at its character select.");
                return request;
            }

            Start(request);
            return request;
        }

        /// <summary>Clicks the character select for an entering asked for now, or waiting until now.</summary>
        private void Start(SessionRequest request)
        {
            _request = request;

            if (request.Asked == SessionRoute.Messages)
            {
                _log.Info($"Entering the world as {Describe(request.Character)} by messages cannot be done in AC:Unreal: given the server's login unasked, it logs it"
                    + " and even sends LoginComplete, but its character select stays up. Clicking its Enter instead.");
            }

            ClickEnter(request);
        }

        /// <summary>Why the world cannot be entered as this character now, where the session stands; null when it can.</summary>
        private string EnterRefusal(uint characterId, AccountCharacter character, bool readScreen)
        {
            GameWindowState window = _window() ?? GameWindowState.Unknown;
            if (_world.Phase != SessionPhase.CharacterList)
            {
                return _world.Phase switch
                {
                    SessionPhase.InWorld => "a character is in the world: log out first",
                    SessionPhase.LoggingOff => "the character is still logging off; wait for the character list",
                    SessionPhase.EnteringWorld => "a character is already entering the world",
                    _ => "the session is not at the character list - an ended session needs a full login, with the password, which this never does",
                };
            }

            if (readScreen && Screen == ClientScreen.Login)
                return "the client is at its login screen, where the password is asked for - its session has ended, and this never logs in";

            if (readScreen && Screen == ClientScreen.InGame)
                return "the client still shows the game, by its own log, though the server has the character out of it: log out in the game (Esc, Log Out) first";

            if (character == null)
                return $"the account has no character 0x{characterId:X8}";

            if (character.DeleteTimeout != 0)
                return $"{character.Name} is being deleted";

            if (_keys()?.CanClick != true)
                return "AC:Unreal enters the world only from its own character select, whose Enter the overlay clicks, and the overlay is not attached";

            if (window.Minimized && !window.Parked)
            {
                return "the game window is minimized, where its character select takes no click: restore it - or keep playing while minimized"
                    + " (`ctl window keep on`) - to enter the world";
            }

            return null;
        }

        /// <summary>The logout under way whose server side is done, the client being watched; null when there is none.</summary>
        private SessionRequest SettlingLogOut()
            => _request is { LoggingOut: true, Succeeded: null, Stage: Stage.ClientToSettle } ? _request : null;

        /// <summary>What stands in the way of any request: acting, or another one under way or waiting.</summary>
        private string Refusal()
        {
            string acting = ActingRefusal();
            if (acting != null)
                return acting;

            if (_request != null && !_request.Succeeded.HasValue)
                return $"already busy: {_request}" + (_next != null ? $"; then {_next.What}" : string.Empty);

            return null;
        }

        private string ActingRefusal()
            => _actions.IsAvailable ? null : "plugins may not act - \"Let plugins act\" is off, or this is a replay";

        /// <summary>
        /// The logout an entering waited for is over: the entering starts if it still can - the
        /// client back at its character select - and otherwise ends, saying why.
        /// </summary>
        private void StartNext(SessionRequest logout)
        {
            SessionRequest next = _next;
            _next = null;
            if (next == null || next.Succeeded.HasValue)
                return;

            if (logout.Succeeded != true)
            {
                Finish(next, false, $"Could not enter the world as {Describe(next.Character)}: the logout it waited for did not finish - {logout.Outcome}");
                return;
            }

            string refusal = ActingRefusal() ?? EnterRefusal(next.Character.Id, _world.AccountCharacters.FirstOrDefault(c => c.Id == next.Character.Id), readScreen: true);
            if (refusal != null)
            {
                Finish(next, false, $"Could not enter the world as {Describe(next.Character)} once the logout was done: {refusal}.");
                return;
            }

            Start(next);
        }

        // ------------------------------------------------------------------- the steps

        /// <summary>
        /// Lets go of every key held for plugins before the character leaves, so the client is not
        /// left moving: a walk, a turn, a jump's charge. Nothing is pressed to stop a movement the
        /// client keeps up by itself - its autorun is a toggle, and a press would as soon start it.
        /// </summary>
        private void LetGoOfKeys(SessionRequest request)
        {
            GameInput keys = _keys();
            if (keys == null || (keys.Held.Count == 0 && keys.Pressed.Count == 0))
                return;

            string held = string.Join(" ", keys.Held);
            keys.ReleaseAll();
            _log.Info($"Logging out {request.Leaving}: let go of the keys held for plugins first{(held.Length == 0 ? string.Empty : " (" + held + ")")}.");
        }

        private void SendLogOff(SessionRequest request)
        {
            request.Route = SessionRoute.Messages;
            _ = _actions.SendLogOffAsync();
            _log.Info($"Logging out {request.Leaving}: asked the server to log the character off (CharacterLogOff), as the client does.");
            Enter(request, Stage.ServerToLogOff, "the server to log the character off");
        }

        /// <summary>The character select clicked as a player clicks it: the character's row, to choose it, then Enter.</summary>
        private void ClickEnter(SessionRequest request)
        {
            request.Route = SessionRoute.Keys;

            int index = _world.AccountCharacters.ToList().FindIndex(c => c.Id == request.Character.Id);
            double scale = ClientDisplay.QuarterStep(_uiScale());
            GameClick click = index >= 0 && index < SelectRows
                ? new GameClick(SelectWidth, SelectHeight, SelectRow(index), SelectEnter) { UiScale = scale }
                : new GameClick(SelectWidth, SelectHeight, SelectEnter) { UiScale = scale };

            if (!_keys().Click(click))
            {
                Finish(request, false, $"Could not enter the world as {Describe(request.Character)}: the overlay could not be asked to click the character select.");
                return;
            }

            _log.Info($"Entering the world as {Describe(request.Character)}: clicked "
                + (click.Points.Count > 1 ? $"its row, the {Ordinal(index + 1)} on the character select, then Enter" : "Enter on the character select")
                + $" ({click} centred in the game window).");
            Enter(request, Stage.ClientToAsk, "the client to ask to enter the world");
        }

        private void Enter(SessionRequest request, Stage stage, string waiting)
        {
            request.Stage = stage;
            request.StageSince = _world.Now;
            request.Waiting = waiting;
        }

        private void Finish(SessionRequest request, bool succeeded, string outcome)
        {
            if (succeeded)
                _log.Info(outcome);
            else
                _log.Warn(outcome);
            request.Finish(succeeded, outcome);

            // An entering asked for while this logout was being finished goes now, or not at all.
            if (request.LoggingOut && _next != null)
                StartNext(request);
        }

        // ------------------------------------------------------------------- watching

        /// <summary>
        /// A message either way, once the host has applied it to the world - the injected ones
        /// included, since the relay tells the host of them.
        /// </summary>
        internal void OnMessage(PacketDirection direction, AcMessage message)
        {
            if (direction == PacketDirection.Inbound)
            {
                // The server's own words at a logoff, kept to answer a client that asks again.
                if (message.Opcode == Opcodes.CharacterList)
                    _lastCharacterList = message.Payload.ToArray();
                else if (message.Opcode == Opcodes.ServerName)
                    _lastServerName = message.Payload.ToArray();
            }
            else if (message.Opcode == Opcodes.GameAction)
            {
                _clientActedAt = _world.Now;
            }

            SessionRequest request = _request;
            if (request == null || request.Succeeded.HasValue)
                return;

            if (request.LoggingOut)
                OnLogOutMessage(request, direction, message);
            else
                OnEnterMessage(request, direction, message);
        }

        private void OnLogOutMessage(SessionRequest request, PacketDirection direction, AcMessage message)
        {
            if (direction == PacketDirection.Outbound)
            {
                // The player's own Log Out, asked for when the client stayed in the world: already
                // logged off at the server, which drops it, so the client is given the server's own
                // answer.
                if (message.Opcode == Opcodes.CharacterLogOff && request.Stage == Stage.ClientToAskAgain)
                    AnswerClientLogOff(request);

                return;
            }

            if (message.Opcode == Opcodes.CharacterList && request.Stage == Stage.ClientToSettle)
            {
                _log.Info($"Logging out {request.Leaving}: the server listed the account's {_world.AccountCharacters.Count} character(s).");
                return;
            }

            // CharacterLogOff, then the list in the same packet; the list alone says the same.
            if ((message.Opcode == Opcodes.CharacterLogOff || message.Opcode == Opcodes.CharacterList) && request.Stage == Stage.ServerToLogOff)
            {
                _log.Info($"Logging out {request.Leaving}: the server has taken the character out of the world.");
                Enter(request, Stage.ClientToSettle, "the client to be back at its character select");
            }
        }

        private void OnEnterMessage(SessionRequest request, PacketDirection direction, AcMessage message)
        {
            if (direction == PacketDirection.Outbound)
            {
                switch (message.Opcode)
                {
                    case Opcodes.CharacterEnterWorldRequest when request.Stage == Stage.ClientToAsk:
                        _log.Info($"Entering the world as {Describe(request.Character)}: the client asked to enter.");
                        Enter(request, Stage.ClientToName, "the client to name its character");
                        break;

                    case Opcodes.CharacterEnterWorld when request.Stage is Stage.ClientToName or Stage.ClientToAsk:
                        uint named = _world.EnteringCharacterId;
                        _log.Info(named == request.Character.Id
                            ? $"Entering the world as {Describe(request.Character)}: the client named it."
                            : $"Entering the world: the client named 0x{named:X8}, the character its character select had chosen, not {Describe(request.Character)}.");
                        Enter(request, Stage.CharacterToArrive, "the server to create the character");
                        break;

                    case Opcodes.GameAction when request.Stage == Stage.ClientToFinish
                                                 && MessageDecoder.TryReadActionType(message, out uint action)
                                                 && action == GameActions.LoginComplete:
                        ClientLoginComplete(request);
                        break;
                }

                return;
            }

            switch (message.Opcode)
            {
                case Opcodes.CharacterError when request.Stage is Stage.ClientToAsk or Stage.ClientToName or Stage.CharacterToArrive:
                    uint error = _world.LastCharacterError;
                    Finish(request, false, $"Could not enter the world as {Describe(request.Character)}: the server refused it - {DescribeCharacterError(error)}"
                        + (error == CharacterStillInWorld ? "; the server may still be finishing with the last one: try again in a few seconds." : "."));
                    break;

                case Opcodes.PlayerCreate when request.Stage is Stage.ClientToAsk or Stage.ClientToName or Stage.CharacterToArrive:
                    _log.Info($"Entering the world: the server created {Describe(_world.Character)}.");
                    Enter(request, Stage.ClientToFinish, ReadsClientLog ? "the client to show the game, by its log" : "the client to finish arriving");
                    break;
            }
        }

        /// <summary>
        /// Whether the client's own log is being read here: it has named a screen. AC:Unreal sends
        /// LoginComplete when the server's login reaches it whether or not it leaves its character
        /// select, so where its log can say, the log has the last word.
        /// </summary>
        private bool ReadsClientLog => Screen != ClientScreen.Unknown;

        /// <summary>The client's LoginComplete: arrival where its log is not read; where it is, only said.</summary>
        private void ClientLoginComplete(SessionRequest request)
        {
            if (!ReadsClientLog)
            {
                Entered(request, "the client has finished arriving (its LoginComplete; its own log is not read here)");
                return;
            }

            if (Screen == ClientScreen.InGame && ScreenSince >= request.Began)
            {
                Entered(request, "the client shows the game, by its log");
                return;
            }

            if (!request.SaidLoginComplete)
            {
                request.SaidLoginComplete = true;
                _log.Info($"Entering the world as {Describe(request.Character)}: the client sent LoginComplete, but its log shows {Describe(Screen)};"
                    + " waiting for it to show the game.");
            }
        }

        /// <summary>Gives up on what has waited too long, and finishes what has waited long enough. On the host's tick.</summary>
        internal void Tick()
        {
            SessionRequest request = _request;
            if (request == null || request.Succeeded.HasValue)
                return;

            TimeSpan waited = _world.Now - request.StageSince;
            switch (request.Stage)
            {
                case Stage.ClientToAsk when waited >= ClientAnswers:
                    Finish(request, false, $"Could not enter the world as {Describe(request.Character)}: the client did not ask to enter within {ClientAnswers.TotalSeconds:0} s"
                        + " of its character select's Enter being clicked"
                        + (ReadsClientLog ? $" - its log says it shows {Describe(Screen)}" : string.Empty)
                        + ". ACUnrealOverlay.log, beside the overlay, says where it clicked. Enter in the game.");
                    break;

                case Stage.ServerToLogOff when waited >= ServerLogsOff:
                    Finish(request, false, $"Could not log out {request.Leaving}: the server did not log the character off within {ServerLogsOff.TotalSeconds:0} s"
                        + " (a player killer must wait out the fight's timer first).");
                    break;

                case Stage.ClientToSettle when waited >= ClientSettles:
                    if (Screen == ClientScreen.InGame || (Screen == ClientScreen.Unknown && _clientActedAt > request.StageSince + InFlight))
                    {
                        AskPlayerToLogOut(request);
                        break;
                    }

                    if (Screen == ClientScreen.Login)
                    {
                        Finish(request, false, $"Logged {request.Leaving} out, but the client went on to its login screen: its session is over, and entering"
                            + " the world again needs a full login, with the password, which this never does.");
                        break;
                    }

                    LoggedOut(request, Screen == ClientScreen.Unknown
                        ? "the client's own screen cannot be read here, and it has not acted in the world since - though an idle client seldom does"
                        : "the client shows " + Describe(Screen));
                    break;

                case Stage.ClientToAskAgain when waited >= PlayerLogsOut:
                    Finish(request, false, $"Logged {request.Leaving} out at the server, but the client is still showing the world, and nobody logged out in the game"
                        + $" within {PlayerLogsOut.TotalSeconds:0} s: log out in the game (Esc, Log Out) to bring it back to the character select.");
                    break;

                case Stage.ClientToName when waited >= ServerAnswers:
                case Stage.CharacterToArrive when waited >= ServerAnswers:
                    Finish(request, false, $"Could not enter the world as {Describe(request.Character)}: nothing came of it for {ServerAnswers.TotalSeconds:0} s while waiting for {request.Waiting}.");
                    break;

                case Stage.ClientToFinish when ReadsClientLog && Screen == ClientScreen.InGame && ScreenSince >= request.Began:
                    Entered(request, "the client shows the game, by its log");
                    break;

                case Stage.ClientToFinish when waited >= ClientFinishes:
                    ClientDidNotArrive(request);
                    break;
            }
        }

        private void LoggedOut(SessionRequest request, string how)
            => Finish(request, true, $"Logged {request.Leaving} out to the character list: {how}.");

        private void Entered(SessionRequest request, string how)
        {
            uint id = _world.Character.Id;
            Finish(request, true, id == request.Character.Id
                ? $"Entered the world as {Describe(request.Character)}: {how}."
                : $"Entered the world as {Describe(_world.Character)}, the character the character select had chosen - not {Describe(request.Character)}: {how}.");
        }

        /// <summary>
        /// The server has logged the character off but the client is still showing the world. No key
        /// is pressed for it - none logs out safely - so the player is asked to log out in the game,
        /// and the request the client then sends, which the server drops, is answered with the
        /// server's own words.
        /// </summary>
        private void AskPlayerToLogOut(SessionRequest request)
        {
            if (!_transport.CanShowInGame || _lastCharacterList == null)
            {
                Finish(request, false, $"Logged {request.Leaving} out at the server, but the client is still showing the world and cannot be put right from here:"
                    + " log out in the game (Esc, Log Out) to bring it back to the character select.");
                return;
            }

            _log.Warn($"Logged {request.Leaving} out at the server, but the client is still showing the world: log out in the game (Esc, Log Out),"
                + " and the server's answer will be given it again.");
            Enter(request, Stage.ClientToAskAgain, "the player to log out in the game");
        }

        /// <summary>Gives the client, which asked to log off, the answer the server gave earlier: CharacterLogOff, the list, the name.</summary>
        private void AnswerClientLogOff(SessionRequest request)
        {
            _ = _transport.ShowInGameAsync(AcMessage.Create(Opcodes.CharacterLogOff, Array.Empty<byte>()));
            _ = _transport.ShowInGameAsync(AcMessage.Create(Opcodes.CharacterList, _lastCharacterList));
            if (_lastServerName != null)
                _ = _transport.ShowInGameAsync(AcMessage.Create(Opcodes.ServerName, _lastServerName));

            _log.Info($"Logging out {request.Leaving}: the client asked to log off; gave it the server's answer again.");
            Enter(request, Stage.ClientToSettle, "the client to be back at its character select");
        }

        /// <summary>
        /// The character is in the world at the server, and the client did not show the game in
        /// time. One still at its character select, by its log, is not coming - as AC:Unreal was not,
        /// handed a login it had not asked for - so the character is logged out again, leaving both
        /// ends at the character list rather than the character standing in the world unplayed.
        /// </summary>
        private void ClientDidNotArrive(SessionRequest request)
        {
            string who = Describe(request.Character);
            if (Screen == ClientScreen.CharacterSelect)
            {
                _ = _actions.SendLogOffAsync();
                Finish(request, false, $"Could not enter the world as {who}: the server created the character, but the client still shows its character select,"
                    + $" by its log, {ClientFinishes.TotalSeconds:0} s on. Logged the character off again, so both ends are back at the character list;"
                    + " enter in the game.");
                return;
            }

            Finish(request, false, $"Entered the world as {who} at the server, but the client did not "
                + (ReadsClientLog ? $"show the game within {ClientFinishes.TotalSeconds:0} s - its log says it shows {Describe(Screen)}." : $"finish arriving within {ClientFinishes.TotalSeconds:0} s."));
        }

        /// <summary>The session ended: whatever was under way cannot finish.</summary>
        internal void OnSessionEnded(string how)
        {
            _lastCharacterList = null;
            _lastServerName = null;

            SessionRequest request = _request;
            if (request != null && !request.Succeeded.HasValue)
                Finish(request, false, $"Could not {request.What}: the session ended - {how}.");
        }

        // ------------------------------------------------------------------- the client's log

        private static readonly Regex UiFlow = new Regex(@"UIFlow mode -> (\d+)", RegexOptions.CultureInvariant);

        /// <summary>
        /// A line of the game client's own log (AC:Unreal's Saved\Logs\ACUnreal.log), as it is
        /// written. Its UIFlow lines say which screen it shows - 2 the login, 3 the character select,
        /// 0 between, 6 the game - and its [ACE] lines what it sends and hears.
        /// </summary>
        public void NoteClientLog(string line)
        {
            if (string.IsNullOrEmpty(line))
                return;

            ClientScreen screen = Screen;
            Match flow = UiFlow.Match(line);
            if (flow.Success)
            {
                screen = flow.Groups[1].Value switch
                {
                    "2" => ClientScreen.Login,
                    "3" => ClientScreen.CharacterSelect,
                    "6" => ClientScreen.InGame,
                    "0" => ClientScreen.Changing,
                    _ => screen,
                };
            }
            else if (line.Contains("[ACE] Logged off", StringComparison.Ordinal))
            {
                screen = ClientScreen.CharacterSelect;
            }

            if (screen == Screen)
                return;

            Screen = screen;
            ScreenSince = _world.Now;
            _log.Info($"The client shows {Describe(screen)}.");
        }

        // ------------------------------------------------------------------- saying

        /// <summary>
        /// A line for <c>ctl status</c>: what is under way, and what waits for it to be done, or how
        /// the last one ended; null before the first.
        /// </summary>
        public string Describe()
            => _request == null ? null : _request + (_next != null ? "; then " + _next : string.Empty);

        private static string Describe(ICharacterView character)
            => character == null || character.Id == 0
                ? "the character"
                : string.IsNullOrEmpty(character.Name) ? $"0x{character.Id:X8}" : $"{character.Name} (0x{character.Id:X8})";

        private static string Describe(AccountCharacter character)
            => character == null ? "the character" : character.ToString();

        private static string Ordinal(int n) => n switch
        {
            1 => "first",
            2 => "second",
            3 => "third",
            _ => n + "th",
        };

        /// <summary>What the client shows, to follow "shows": "its character select", "the game".</summary>
        public static string Describe(ClientScreen screen) => screen switch
        {
            ClientScreen.Login => "its login screen",
            ClientScreen.CharacterSelect => "its character select",
            ClientScreen.Changing => "a change of screen",
            ClientScreen.InGame => "the game",
            _ => "a screen its log has not named",
        };

        /// <summary>ACE's CharacterError, in words, as AC:Unreal says them where it has words for them.</summary>
        public static string DescribeCharacterError(uint error) => error switch
        {
            CharacterStillInWorld => "a character on this account is still in the world",
            11 => "it could not be entered",
            15 => "the character is not this account's, or is being deleted",
            16 => "the character is in the world on another server",
            20 => "the server could not place the character",
            21 => "the server is full or not open",
            23 => "the character is locked",
            _ => $"error {error}",
        };
    }
}
