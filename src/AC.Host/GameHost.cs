using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Decoding;
using AC.Host.Plugins;
using AC.Host.Plugins.Views;
using AC.Host.Transport;
using AC.Host.World;
using AC.Protocol;

namespace AC.Host
{
    /// <summary>Counts of what the host has done with the messages it was given.</summary>
    public sealed class HostStatistics
    {
        public long MessagesInbound;
        public long MessagesOutbound;
        public long Applied;
        public long Ignored;
        public long Malformed;
        public long PluginExceptions;

        /// <summary>Inbound opcodes the host had no decoder for, with counts.</summary>
        public ConcurrentDictionary<uint, long> IgnoredOpcodes { get; } = new ConcurrentDictionary<uint, long>();

        /// <summary>Opcodes whose decoder rejected the bytes, with counts.</summary>
        public ConcurrentDictionary<uint, long> MalformedOpcodes { get; } = new ConcurrentDictionary<uint, long>();

        /// <summary>
        /// GameAction types the client sent, with counts - the client's own actions and
        /// any the host injected. Movement lives in here, which is why it is worth
        /// counting separately from the opcode: every one of them is opcode 0xF7B1.
        /// </summary>
        public ConcurrentDictionary<uint, long> OutboundActions { get; } = new ConcurrentDictionary<uint, long>();

        /// <summary>
        /// GameEvent types with no decoder, with counts. Like actions, every one of
        /// these shares a single opcode, so the opcode count alone says nothing about
        /// what is being missed.
        /// </summary>
        public ConcurrentDictionary<uint, long> IgnoredGameEvents { get; } = new ConcurrentDictionary<uint, long>();

        /// <summary>Lines the host has put in the game's chat window.</summary>
        public long ShownInGame;

        /// <summary>Lines the player typed for a plugin, kept from the server and given to the plugins.</summary>
        public long TypedCommands;

        /// <summary>Overlay commands a plugin acted on.</summary>
        public long CommandsHandled;

        /// <summary>
        /// Overlay commands nobody acted on: an unknown owner, a plugin that does not
        /// take commands, or a name it did not recognise. Worth counting because a click
        /// that silently does nothing is the hardest kind of bug to notice.
        /// </summary>
        public long CommandsUnhandled;
    }

    /// <summary>
    /// The host: one transport, one world, one game thread, any number of plugins.
    /// </summary>
    /// <remarks>
    /// The game thread is the whole concurrency story. Messages arrive on transport
    /// threads and are queued; the game thread dequeues them one at a time, applies
    /// each to the world, and lets the world's events reach plugins - all before
    /// taking the next message. Plugins therefore never see a torn world and never
    /// run concurrently with each other or with a decoder. Anything a plugin wants
    /// done later comes back through <see cref="RunOnGameThread"/>.
    ///
    /// A plugin that throws is logged and skipped for that event; it is not unloaded,
    /// because a transient failure in one handler is not a reason to lose the
    /// plugin's other behaviour. The host itself never lets a plugin exception out.
    /// </remarks>
    public sealed partial class GameHost : IHost, IAsyncDisposable
    {
        private readonly IGameTransport _transport;
        private readonly WorldState _world;
        private readonly IPluginLog _log;
        private readonly Dictionary<string, string> _settings;
        private readonly string _dataRoot;
        private readonly List<IPlugin> _plugins = new List<IPlugin>();
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
        private readonly TaskCompletionSource _ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private Thread _gameThread;
        private Timer _tickTimer;
        private DateTimeOffset _lastTick;
        private bool _started;
        private bool _disposed;

        public GameHost(
            IGameTransport transport,
            IPluginLog log,
            IReadOnlyDictionary<string, string> settings = null,
            string dataRoot = null,
            WorldState world = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _settings = settings != null ? new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _dataRoot = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ACHost");
            _world = world ?? new WorldState();

            _world.ServerConnected += (s, e) => Raise(ServerConnected, e);
            _world.PlayerIdentified += (s, e) => Raise(PlayerIdentified, e);
            _world.ObjectCreated += (s, e) => Raise(ObjectCreated, e);
            _world.ObjectUpdated += (s, e) => Raise(ObjectUpdated, e);
            _world.ObjectAppraised += (s, e) => Raise(ObjectAppraised, e);
            _world.ObjectMoved += (s, e) => Raise(ObjectMoved, e);
            _world.ObjectRemoved += (s, e) =>
            {
                JournalRemoval(e);
                Raise(ObjectRemoved, e);
            };
            _world.ContainerViewed += (s, e) => Raise(ContainerViewed, e);
            _world.ChatReceived += (s, e) => Raise(ChatReceived, e);
            _world.CharacterUpdated += (s, e) => RaisePlain(CharacterUpdated);
            _world.UseFinished += (s, e) => Raise(UseFinished, e);
            _world.ContainerClosed += (s, e) => Raise(ContainerClosed, e);
            _world.MoveRefused += (s, e) => Raise(MoveRefused, e);
            _world.DamageTaken += (s, e) => Raise(DamageTaken, e);
            _world.AttackEvaded += (s, e) => Raise(AttackEvaded, e);
            _world.EnchantmentChanged += (s, e) => Raise(EnchantmentChanged, e);
            _world.EnchantmentRemoved += (s, e) => Raise(EnchantmentRemoved, e);
            _world.AttackFinished += (s, e) => Raise(AttackFinished, e);
            _world.DamageDealt += (s, e) => Raise(DamageDealt, e);
            _world.TargetEvaded += (s, e) => Raise(TargetEvaded, e);
            _world.AttackCommenced += (s, e) => RaisePlain(AttackCommenced);
            _world.PortalSpaceChanged += (s, e) => Raise(PortalSpaceChanged, e);
            _world.VendorChanged += (s, e) => Raise(VendorChanged, e);
            _world.Died += (s, e) => Raise(Died, e);
            _world.LoggedOff += (s, e) =>
            {
                // A plugin walking the character when it left has nothing to walk any more, and the
                // next one in must not set off with the keys still held.
                _input?.ReleaseAll();
                ForgetCharacterInJournal();
                Raise(LoggedOff, e);
            };
            _world.LoggingOff += (s, e) =>
            {
                // Nothing is walked out of a logoff: the client would only ask again.
                _input?.ReleaseAll();
                RaisePlain(LoggingOff);
            };

            // A transport that can send gets real actions; one that cannot gets a
            // stand-in that refuses politely, so a plugin written against the real
            // thing runs unchanged over a capture.
            if (_transport.CanSend)
            {
                AC.Host.Actions.ClientActions actions = new AC.Host.Actions.ClientActions(_transport, _log, _world) { Allowed = () => _actionsAllowed, Appraising = _appraisals.HostAsked };
                Session = new AC.Host.Actions.SessionControl(actions, _transport, _world, _log, () => InputKeys, () => GameWindow, () => ClientUiScale);
                actions.Session = Session;
                Actions = actions;
            }
            else
            {
                Actions = new UnavailableActions(_log);
            }

            // The answers to the host's own appraisals are kept from the client, whose examine
            // panel would open for each.
            if (_transport is IClientboundFilter clientbound)
                clientbound.WithholdFromClient = (opcode, payload) => _appraisals.Withhold(opcode, payload);

            // Lines run as though typed, and lines the player types for a plugin, which the
            // relay keeps from the server.
            _chatBox = new ChatBox(this, _world, _log, _settings);
            if (_transport is ITypedCommandSource typed)
            {
                typed.IsPluginCommand = _chatBox.IsPluginCommandLine;
                typed.CommandTyped += (_, line) => RunOnGameThread(() => _chatBox.RunTyped(line));
            }
        }

        private readonly ChatBox _chatBox;

        /// <summary>
        /// Logging the character out to the character list and bringing one into the world - for
        /// plugins, Decal's Hooks.Logout and <c>achost ctl logout</c> / <c>login</c> - or null when the
        /// transport cannot send. Game thread only.
        /// </summary>
        public AC.Host.Actions.SessionControl Session { get; }

        /// <summary>
        /// Runs a line as though the player had typed it into the game's chat box: offered to
        /// the plugins that take commands, then sent as the action the game client itself would
        /// have sent for it. Game thread only.
        /// </summary>
        public ChatCommandOutcome RunChatCommand(string text, IPlugin from = null) => _chatBox.Run(text, from);

        /// <summary>
        /// Puts a row on every status HUD a plugin shows (<see cref="IStatusRows"/>). A plugin that
        /// throws is logged and counted, and the others still have the row. Game thread only.
        /// </summary>
        public void UpdateStatusRow(string plugin, string entry, string value, long colour = 0xFFFFFFFF)
        {
            foreach (IPlugin shower in _plugins.ToList())
            {
                if (shower is not IStatusRows rows)
                    continue;

                try
                {
                    rows.UpdateStatusRow(plugin, entry, value, colour);
                }
                catch (Exception ex)
                {
                    Statistics.PluginExceptions++;
                    _log.Error($"Plugin {shower.Name} threw taking the status row {plugin} - {entry}.", ex);
                }
            }
        }

        private volatile bool _actionsAllowed = true;

        /// <summary>
        /// Whether plugins may act, when the transport can carry actions at all. The player
        /// turns this on and off while the session runs - from Decal's window in the game - so
        /// a host can start out watching and be let loose later without a reconnect.
        /// </summary>
        public bool ActionsAllowed
        {
            get => _actionsAllowed;
            set
            {
                if (_actionsAllowed == value)
                    return;

                _actionsAllowed = value;
                _log.Info(value
                    ? "Acting is now ALLOWED: plugins can make the character act."
                    : "Acting is now off: plugins can watch only.");
                ActionsAllowedChanged?.Invoke(this, value);
            }
        }

        /// <summary>Raised when <see cref="ActionsAllowed"/> changes, with the new value.</summary>
        public event EventHandler<bool> ActionsAllowedChanged;

        /// <summary>Whether the transport could carry actions if they were allowed.</summary>
        public bool CanAct => _transport.CanSend;

        private GameInput _input;

        /// <summary>
        /// The movement keys. Whatever runs the overlay sets its <see cref="GameInput.Publish"/>
        /// and <see cref="GameInput.Attached"/>; until then no key can be held.
        /// </summary>
        public GameInput InputKeys => _input ??= new GameInput(_log, _settings)
        {
            Allowed = () => _actionsAllowed && CanAct,
            ClientReports = () => _world.Character.ClientReports,
            Window = () => GameWindow,
        };

        IGameInput IHost.Input => InputKeys;

        /// <summary>
        /// What the game's window is doing, as the overlay last said. Whatever runs the overlay
        /// passes on what it says through <see cref="NoteGameWindow"/>.
        /// </summary>
        public GameWindowState GameWindow { get; private set; } = GameWindowState.Unknown;

        /// <summary>Raised on the game thread when <see cref="GameWindow"/> changes.</summary>
        public event EventHandler<GameWindowState> GameWindowChanged;

        /// <summary>
        /// Takes in what the overlay says of the game's window - or <see cref="GameWindowState.Unknown"/>
        /// when it goes - and says so in the log when it changes. Game thread only.
        /// </summary>
        public void NoteGameWindow(GameWindowState state)
        {
            state ??= GameWindowState.Unknown;
            if (state.Equals(GameWindow))
                return;

            GameWindowState was = GameWindow;
            GameWindow = state;

            if (state.Known)
            {
                string line = "The game window is " + state.Describe() + ".";
                if (state.Minimized)
                    line += " Plugins go on acting over the network; walking needs the game to take keys while minimized"
                        + (KeepPlayingMinimized ? "." : "; \"Keep playing while minimized\" parks it off-screen instead, where it does.");
                else if (state.Parked)
                    line += " The game goes on taking keys there; bring it back from the taskbar.";
                _log.Info(line);
            }
            else if (was.Known)
            {
                _log.Info("The overlay no longer says what the game window is doing.");
            }

            GameWindowChanged?.Invoke(this, state);
        }

        private bool? _keepPlayingMinimized;

        /// <summary>
        /// The player's choice to keep playing while the game is minimized: minimizing the window
        /// then sends it off-screen at about thirty frames a second instead, where the game goes on
        /// taking the keys plugins hold - it comes back from the taskbar as a minimized window
        /// would. Off unless "Overlay:KeepPlayingMinimized" says otherwise or the player turns it
        /// on; the overlay reads it from each snapshot, so a change applies at once.
        /// </summary>
        public bool KeepPlayingMinimized
        {
            get => _keepPlayingMinimized ??= _settings.TryGetValue("Overlay:KeepPlayingMinimized", out string text)
                && (text.Trim().Equals("on", StringComparison.OrdinalIgnoreCase) || (bool.TryParse(text.Trim(), out bool on) && on) || text.Trim() == "1");
            set
            {
                if (KeepPlayingMinimized == value)
                    return;

                _keepPlayingMinimized = value;
                _log.Info(value
                    ? "Keep playing while minimized is ON: minimizing the game parks it off-screen at about 30 frames a second, where it goes on taking keys."
                    : "Keep playing while minimized is off: minimizing the game minimizes it.");
                KeepPlayingMinimizedChanged?.Invoke(this, value);
            }
        }

        /// <summary>Raised when <see cref="KeepPlayingMinimized"/> changes, with the new value.</summary>
        public event EventHandler<bool> KeepPlayingMinimizedChanged;

        public HostStatistics Statistics { get; } = new HostStatistics();

        public IWorldView World => _world;

        /// <summary>The mutable world, for tests and tooling. Plugins use <see cref="World"/>.</summary>
        public WorldState WorldState => _world;

        public ICharacterView Character => _world.Character;

        public IGameActions Actions { get; }

        public IGameData GameData => _world.GameData;

        public IPluginLog Log => _log;

        public IReadOnlyDictionary<string, string> Settings => _settings;

        public IReadOnlyList<IPlugin> Plugins => _plugins;

        /// <summary>Where plugins keep their files and the host its own: %LOCALAPPDATA%\ACHost unless told otherwise.</summary>
        public string DataDirectory => _dataRoot;

        /// <summary>Completes when the transport ends and the queue has drained.</summary>
        public Task Ended => _ended.Task;

        public event EventHandler<TimeSpan> Tick;
        public event EventHandler<string> ServerConnected;
        public event EventHandler<uint> PlayerIdentified;
        public event EventHandler<string> LoggedOff;
        public event EventHandler LoggingOff;
        public event EventHandler<WorldObject> ObjectCreated;
        public event EventHandler<WorldObject> ObjectUpdated;
        public event EventHandler<WorldObject> ObjectAppraised;
        public event EventHandler<WorldObject> ObjectMoved;
        public event EventHandler<uint> ObjectRemoved;
        public event EventHandler<ContainerContents> ContainerViewed;
        public event EventHandler<ChatMessage> ChatReceived;
        public event EventHandler CharacterUpdated;
        public event EventHandler<uint> UseFinished;
        public event EventHandler<uint> ContainerClosed;
        public event EventHandler<MoveRefusal> MoveRefused;
        public event EventHandler<AC.Host.World.DamageTaken> DamageTaken;
        public event EventHandler<string> AttackEvaded;
        public event EventHandler<AC.Host.World.Enchantment> EnchantmentChanged;
        public event EventHandler<uint> EnchantmentRemoved;
        public event EventHandler<uint> AttackFinished;
        public event EventHandler<AC.Host.World.DamageDealt> DamageDealt;
        public event EventHandler<string> TargetEvaded;
        public event EventHandler AttackCommenced;
        public event EventHandler<bool> PortalSpaceChanged;
        public event EventHandler<uint> VendorChanged;
        public event EventHandler<string> Died;
        public event EventHandler<GameMessageEventArgs> MessageSeen;

        public string GetSetting(IPlugin plugin, string key)
            => _settings.TryGetValue($"{plugin.Name}:{key}", out string value) ? value : null;

        public string GetDataDirectory(IPlugin plugin)
        {
            string directory = Path.Combine(_dataRoot, "plugins", plugin.Name);
            Directory.CreateDirectory(directory);
            return directory;
        }

        public PluginSettings<T> LoadSettings<T>(IPlugin plugin) where T : class, new()
        {
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));

            PluginSettings<T> settings = new PluginSettings<T>(
                Path.Combine(GetDataDirectory(plugin), "settings.json"),
                _log);

            // Remembered so shutdown can save it. A plugin that changes a setting and
            // never calls Save has still changed it, and losing that on exit would make
            // every switch in the overlay lie the next time the host starts.
            lock (_settingsToSave)
                _settingsToSave.Add((plugin, () => settings.Save()));

            return settings;
        }

        private readonly List<(IPlugin Plugin, Func<bool> Save)> _settingsToSave = new List<(IPlugin, Func<bool>)>();

        /// <summary>
        /// Puts one of the host's own lines in the game's chat window, prefixed so it cannot
        /// be mistaken for something the server said.
        /// </summary>
        /// <remarks>
        /// "[Decal] ", because the host stands where Decal did and these are its words - its
        /// hotkey windows, its acting switch, a typed line no plugin took. Decal's own Hotkey
        /// System asked in a dialog ("Press key and click OK.") rather than in the chat, so
        /// there is no prefix of its to copy. A plugin's own lines go through
        /// <see cref="ShowInGame(string, int)"/>, under the plugin's.
        /// </remarks>
        public bool ShowInGame(string text)
            => !string.IsNullOrEmpty(text) && ShowInGame(OwnPrefix + text, OwnChatType);

        /// <summary>Puts a line in the game's chat window as given, in chat type <paramref name="chatType"/>.</summary>
        /// <remarks>
        /// <para>
        /// A line longer than <see cref="LongestLineShown"/> goes as several, broken at the last
        /// space that keeps each short enough - or, with none, where it has to be: the relay slips
        /// a message into the server's stream only whole, in one fragment, and one that needs more
        /// would never arrive.
        /// </para>
        /// <para>
        /// A link the old client drew - "&lt;Tell:IIDString:...&gt;text&lt;\Tell&gt;", around the
        /// item lines Mag-Tools and Virindi Tank print - goes as its text alone, as the old client
        /// showed it: AC:Unreal's chat has no links, and would show the markup (<see cref="ChatMarkup"/>).
        /// </para>
        /// </remarks>
        public bool ShowInGame(string text, int chatType)
        {
            text = ChatMarkup.Visible(text);
            if (string.IsNullOrEmpty(text) || !_transport.CanShowInGame)
                return false;

            try
            {
                foreach (string line in LinesToShow(text))
                {
                    // A ServerMessage is text and a chat type, which the client draws the line in
                    // the colour of - Decal's AddChatText colour, the same numbers.
                    AC.Host.Actions.PayloadWriter payload = new AC.Host.Actions.PayloadWriter();
                    payload.String(line);
                    payload.UInt32(unchecked((uint)chatType));

                    _ = _transport.ShowInGameAsync(AcMessage.Create(Opcodes.ServerMessage, payload.ToArray()));
                    Statistics.ShownInGame++;
                }

                return true;
            }
            catch (Exception ex)
            {
                // Never worth taking a plugin down over; the line simply does not appear.
                _log.Error("Could not show a line in the game.", ex);
                return false;
            }
        }

        /// <summary>
        /// The longest line one ServerMessage can carry and still fit one fragment, which is all
        /// the relay will slip into the server's stream: 448 bytes, less the opcode, the text's
        /// length and the chat type. The client's text is a byte a character.
        /// </summary>
        public const int LongestLineShown = 438;

        /// <summary>
        /// <paramref name="text"/> as lines of at most <see cref="LongestLineShown"/> characters,
        /// each broken at the last space that fits - the space itself dropped - or, where there is
        /// none, at the limit.
        /// </summary>
        internal static IEnumerable<string> LinesToShow(string text)
        {
            while (text.Length > LongestLineShown)
            {
                int space = text.LastIndexOf(' ', LongestLineShown);
                if (space > 0)
                {
                    yield return text.Substring(0, space);
                    text = text.Substring(space + 1);
                    continue;
                }

                // Never between the two halves of a character outside the basic plane.
                int cut = char.IsHighSurrogate(text[LongestLineShown - 1]) ? LongestLineShown - 1 : LongestLineShown;
                yield return text.Substring(0, cut);
                text = text.Substring(cut);
            }

            if (text.Length > 0)
                yield return text;
        }

        /// <summary>What the host's own lines begin with.</summary>
        internal const string OwnPrefix = "[Decal] ";

        /// <summary>
        /// The chat type the host's own lines arrive in: 0x0D, the channel the retail client
        /// used for a plugin's own output, which is where someone looking for them would
        /// expect to find them.
        /// </summary>
        internal const int OwnChatType = 0x0D;

        /// <summary>
        /// One window for every loaded plugin, with whatever controls it declares.
        /// </summary>
        /// <remarks>
        /// Every plugin, not only those with something to show. Deriving windows from the
        /// panels that happened to arrive this frame meant a quiet plugin had no window and
        /// the bar listed only the talkative ones - Decal registered its plugins up front,
        /// and this is that list.
        ///
        /// Must be called on the game thread, like <see cref="CollectPanels"/>, and so must
        /// anything that reads a window's view afterwards: the view is the plugin's live
        /// object, not a copy. A plugin that throws while declaring its controls or giving
        /// its view still gets its window, marked disabled, so its absence from the screen is
        /// never the symptom of its failure: the window is there, greyed, and the log says why.
        /// </remarks>
        public IReadOnlyList<OverlayWindowInfo> CollectWindows()
        {
            List<OverlayWindowInfo> windows = new List<OverlayWindowInfo>(_plugins.Count);

            foreach (IPlugin plugin in _plugins)
            {
                IReadOnlyList<OverlayControl> controls = Array.Empty<OverlayControl>();
                DecalView view = null;
                bool enabled = true;

                if (plugin is IOverlayControls provider)
                {
                    try
                    {
                        IReadOnlyList<OverlayControl> theirs = provider.GetControls();
                        if (theirs != null)
                        {
                            List<OverlayControl> kept = new List<OverlayControl>(theirs.Count);
                            foreach (OverlayControl control in theirs)
                            {
                                if (control != null)
                                    kept.Add(control);
                            }

                            controls = kept;
                        }
                    }
                    catch (Exception ex)
                    {
                        enabled = false;
                        Statistics.PluginExceptions++;
                        _log.Error($"Plugin {plugin.Name} threw while declaring its controls.", ex);
                    }
                }

                if (plugin is IOverlayView viewer)
                {
                    try
                    {
                        view = viewer.View;
                    }
                    catch (Exception ex)
                    {
                        enabled = false;
                        Statistics.PluginExceptions++;
                        _log.Error($"Plugin {plugin.Name} threw while giving its view.", ex);
                    }
                }

                // A plugin that cannot show anything of its own - one that only hosts other
                // plugins' windows, say - has no window, and so no switch on the bar: a switch
                // that opens an empty window is nothing the standard client ever showed.
                if (plugin is IOverlayControls || plugin is IOverlayView || plugin is IOverlayPanels)
                    windows.Add(new OverlayWindowInfo(plugin.Name, enabled, controls, view, startsClosed: plugin is AC.Host.Decal.DecalAgent));

                if (plugin is IOverlayViews hosting)
                    CollectHostedWindows(plugin, hosting, windows);
            }

            return windows;
        }

        /// <summary>
        /// The further windows of a plugin that hosts plugins of its own, after its own window.
        /// A fault costs those windows for this publish, not the plugin's own.
        /// </summary>
        private void CollectHostedWindows(IPlugin plugin, IOverlayViews hosting, List<OverlayWindowInfo> windows)
        {
            try
            {
                foreach (OverlayViewWindow window in hosting.Views ?? Array.Empty<OverlayViewWindow>())
                {
                    if (window?.View != null)
                        windows.Add(new OverlayWindowInfo(OverlayViewWindow.OwnerFor(plugin.Name, window.Key), true, Array.Empty<OverlayControl>(), window.View, window.StartsClosed));
                }
            }
            catch (Exception ex)
            {
                Statistics.PluginExceptions++;
                _log.Error($"Plugin {plugin.Name} threw while giving the windows it hosts.", ex);
            }
        }

        /// <summary>
        /// Keys the player has bound in Decal's window, by "owner/id", overriding what each
        /// plugin suggested. An empty chord unbinds. Game thread only.
        /// </summary>
        public Dictionary<string, KeyChord> HotkeyBindings { get; } = new Dictionary<string, KeyChord>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Hotkeys the player switched off in a hotkey window, by "owner/id": they keep their key
        /// but the overlay is not told of them, so the key goes to the game. Game thread only.
        /// </summary>
        public HashSet<string> DisabledHotkeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Who is waiting for the next key the player presses - a hotkey window's Set - or null.
        /// The overlay catches that key, keeps it from the game, and sends it to this owner as a
        /// "key-captured" command. Game thread only.
        /// </summary>
        public string KeyCaptureOwner { get; set; }

        /// <summary>
        /// Every hotkey every plugin offers, with the key bound to it now: the player's choice
        /// if there is one, otherwise the plugin's suggestion. Game thread only.
        /// </summary>
        /// <remarks>
        /// Two actions bound to the same key would both be pressed by it, which is never what
        /// was meant, so the first keeps the key and the later loses it - and the log says so,
        /// once.
        /// </remarks>
        public IReadOnlyList<BoundHotkey> CollectHotkeys()
        {
            List<BoundHotkey> hotkeys = new List<BoundHotkey>();
            HashSet<KeyChord> taken = new HashSet<KeyChord>();

            foreach (IPlugin plugin in _plugins)
            {
                if (plugin is not IOverlayHotkeys provider)
                    continue;

                IReadOnlyList<HotkeyDefinition> theirs;
                try
                {
                    theirs = provider.Hotkeys;
                }
                catch (Exception ex)
                {
                    Statistics.PluginExceptions++;
                    _log.Error($"Plugin {plugin.Name} threw while listing its hotkeys.", ex);
                    continue;
                }

                if (theirs == null)
                    continue;

                foreach (HotkeyDefinition definition in theirs)
                {
                    if (definition == null)
                        continue;

                    string name = plugin.Name + "/" + definition.Id;
                    if (!HotkeyBindings.TryGetValue(name, out KeyChord keys) && !KeyChord.TryParse(definition.DefaultKeys, out keys))
                        keys = default;

                    if (!keys.IsEmpty && !taken.Add(keys))
                    {
                        if (_saidHotkeyClash.Add(name))
                            _log.Warn($"{keys} is already bound to another hotkey, so {plugin.Name}'s \"{definition.Description}\" has no key.");
                        keys = default;
                    }

                    hotkeys.Add(new BoundHotkey(plugin.Name, definition, keys));
                }
            }

            return hotkeys;
        }

        private readonly HashSet<string> _saidHotkeyClash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Collects the panels every plugin wants shown, in plugin order.
        /// </summary>
        /// <remarks>
        /// Must be called on the game thread, because that is the only thread on which a
        /// plugin's state is not moving underneath it.
        ///
        /// A plugin that throws while building its panels is logged and skipped for that
        /// publish, exactly as it would be for any other callback. A broken panel costs
        /// the overlay one table; it must not cost the session.
        /// </remarks>
        public IReadOnlyList<OverlayPanel> CollectPanels()
        {
            List<OverlayPanel> panels = new List<OverlayPanel>();

            foreach (IPlugin plugin in _plugins)
            {
                if (plugin is not IOverlayPanels provider)
                    continue;

                try
                {
                    IReadOnlyList<OverlayPanel> theirs = provider.GetPanels();
                    if (theirs == null)
                        continue;

                    foreach (OverlayPanel panel in theirs)
                    {
                        if (panel == null)
                            continue;

                        // Stamped here rather than taken on trust, so a panel cannot
                        // claim to belong to a different plugin's window.
                        panel.Owner = plugin.Name;
                        panels.Add(panel);
                    }
                }
                catch (Exception ex)
                {
                    Statistics.PluginExceptions++;
                    _log.Error($"Plugin {plugin.Name} threw while building its panels.", ex);
                }
            }

            return panels;
        }

        /// <summary>
        /// Routes something the player did in the overlay to the plugin whose window it
        /// happened in.
        /// </summary>
        /// <remarks>
        /// May be called from any thread - the overlay's commands arrive on a pipe thread
        /// - because it queues onto the game thread before touching any plugin. An empty
        /// owner means the host's own window; the host has no commands of its own yet, so
        /// that is reported rather than swallowed.
        ///
        /// A plugin is found by name, which is what the host stamped on its panels, so a
        /// command cannot reach a plugin other than the one whose window it came from.
        ///
        /// A command naming a control in the plugin's Decal view is applied to the view,
        /// which raises that control's event; anything else goes to the plugin's
        /// <see cref="IOverlayCommands"/> as before. The view is asked first because its
        /// names come from the plugin's own XML, so a name it has is certainly meant for it.
        /// </remarks>
        public void DispatchCommand(string owner, OverlayCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));

            RunOnGameThread(() =>
            {
                if (string.IsNullOrEmpty(owner))
                {
                    Statistics.CommandsUnhandled++;
                    _log.Warn($"The overlay sent '{command}' to the host itself, which has nothing that answers to it.");
                    return;
                }

                foreach (IPlugin plugin in _plugins)
                {
                    if (!string.Equals(plugin.Name, owner, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // A hotkey the overlay caught in the game, for the plugin that offered it.
                    if (command.Name == "hotkey" && plugin is IOverlayHotkeys hotkeys)
                    {
                        if (DisabledHotkeys.Contains(plugin.Name + "/" + command.Value))
                            return;

                        try
                        {
                            hotkeys.HotkeyPressed(command.Value);
                            Statistics.CommandsHandled++;
                        }
                        catch (Exception ex)
                        {
                            Statistics.PluginExceptions++;
                            _log.Error($"Plugin {plugin.Name} threw handling its hotkey '{command.Value}'.", ex);
                        }

                        return;
                    }

                    if (plugin is IOverlayView viewer && TryApplyToView(plugin, viewer, command))
                        return;

                    if (plugin is not IOverlayCommands handler)
                    {
                        Statistics.CommandsUnhandled++;
                        _log.Warn($"Plugin {plugin.Name} shows panels but does not take commands; '{command}' was ignored.");
                        return;
                    }

                    try
                    {
                        if (handler.HandleCommand(command))
                        {
                            Statistics.CommandsHandled++;
                        }
                        else
                        {
                            Statistics.CommandsUnhandled++;
                            _log.Warn($"Plugin {plugin.Name} did not recognise '{command}'.");
                        }
                    }
                    catch (Exception ex)
                    {
                        // The same rule as every other callback: one bad handler costs
                        // one click, not the session.
                        Statistics.PluginExceptions++;
                        Statistics.CommandsUnhandled++;
                        _log.Error($"Plugin {plugin.Name} threw handling '{command}'.", ex);
                    }

                    return;
                }

                if (TryApplyToHostedView(owner, command))
                    return;

                Statistics.CommandsUnhandled++;
                _log.Warn($"The overlay sent '{command}' to '{owner}', and no plugin of that name is loaded.");
            });
        }

        /// <summary>
        /// Applies a command from one of the windows an <see cref="IOverlayViews"/> plugin hosts,
        /// named "plugin/key". Returns false when the owner names no such window.
        /// </summary>
        private bool TryApplyToHostedView(string owner, OverlayCommand command)
        {
            int slash = owner.IndexOf('/');
            if (slash <= 0)
                return false;

            string pluginName = owner.Substring(0, slash);
            string key = owner.Substring(slash + 1);

            foreach (IPlugin plugin in _plugins)
            {
                if (plugin is not IOverlayViews hosting || !string.Equals(plugin.Name, pluginName, StringComparison.OrdinalIgnoreCase))
                    continue;

                DecalView view = null;
                try
                {
                    foreach (OverlayViewWindow window in hosting.Views ?? Array.Empty<OverlayViewWindow>())
                    {
                        if (window != null && string.Equals(window.Key, key, StringComparison.Ordinal))
                        {
                            view = window.View;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Statistics.PluginExceptions++;
                    Statistics.CommandsUnhandled++;
                    _log.Error($"Plugin {plugin.Name} threw while giving the windows it hosts, so '{command}' was dropped.", ex);
                    return true;
                }

                if (view == null)
                    return false;

                if (!TryApplyToView(plugin, new FixedView(view), command))
                {
                    Statistics.CommandsUnhandled++;
                    _log.Warn($"The overlay sent '{command}' to '{owner}', whose view has no control of that name.");
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Offers a chat command to every plugin that takes them (<see cref="IChatCommands"/>),
        /// in plugin order, until one claims it; true when one did. Game thread only.
        /// </summary>
        /// <remarks>
        /// For a plugin that runs other plugins' commands - Virindi Tank's meta ran "/mt" lines
        /// through Decal - and for whatever feeds the player's own typed commands in. The
        /// plugin asking is passed so it is not offered its own line back. A plugin that throws
        /// is logged and counted, and the line goes on to the next.
        /// </remarks>
        public bool DispatchChatCommand(string text, IPlugin from = null)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            foreach (IPlugin plugin in _plugins.ToList())
            {
                if (plugin is not IChatCommands commands || ReferenceEquals(plugin, from))
                    continue;

                try
                {
                    if (commands.TryCommand(text))
                        return true;
                }
                catch (Exception ex)
                {
                    Statistics.PluginExceptions++;
                    _log.Error($"Plugin {plugin.Name} threw handling the command '{text}'.", ex);
                }
            }

            return false;
        }

        /// <summary>A view already in hand, given where <see cref="TryApplyToView"/> wants one to ask for.</summary>
        private sealed class FixedView : IOverlayView
        {
            internal FixedView(DecalView view)
            {
                View = view;
            }

            public DecalView View { get; }
        }

        /// <summary>
        /// Applies a command to the plugin's view if the view has the control it names.
        /// Returns true when the command has been dealt with - applied, refused or faulted -
        /// and false when it is not the view's, so the caller tries the plugin's handler.
        /// </summary>
        /// <remarks>
        /// Game thread only, like everything else that touches a view.
        /// </remarks>
        private bool TryApplyToView(IPlugin plugin, IOverlayView viewer, OverlayCommand command)
        {
            DecalView view;
            try
            {
                view = viewer.View;
            }
            catch (Exception ex)
            {
                // Without the view there is no telling whose the command was, so it is not
                // passed on: one fault is one unhandled click, not two warnings about it.
                Statistics.PluginExceptions++;
                Statistics.CommandsUnhandled++;
                _log.Error($"Plugin {plugin.Name} threw while giving its view, so '{command}' was dropped.", ex);
                return true;
            }

            // One of its controls, one of its own title-bar buttons - a HUD's add and remove - or
            // a new size from its frame.
            if (view == null || !view.Takes(command))
                return false;

            try
            {
                if (view.Apply(command, out string refusal))
                {
                    Statistics.CommandsHandled++;
                }
                else
                {
                    Statistics.CommandsUnhandled++;
                    _log.Warn($"Plugin {plugin.Name}'s view did not take '{command}': {refusal}.");
                }
            }
            catch (Exception ex)
            {
                // Thrown by the plugin's own event handler, after the view had already
                // changed. The same rule as every other callback: it costs one click.
                Statistics.PluginExceptions++;
                Statistics.CommandsUnhandled++;
                _log.Error($"Plugin {plugin.Name} threw handling '{command}'.", ex);
            }

            return true;
        }

        public void RunOnGameThread(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            try
            {
                _queue.Add(action);
            }
            catch (InvalidOperationException)
            {
                // Either CompleteAdding has run, or - as ObjectDisposedException, which
                // derives from this - the collection is gone. Both mean there is no
                // longer a thread to run this on, so dropping it is the whole of the
                // correct behaviour. Throwing would take down the calling thread
                // instead, and the tick timer calls this from the thread pool, where an
                // exception ends the process. Checking a flag first would not help: the
                // state can change between the check and the Add.
            }
        }

        /// <summary>
        /// Adds a plugin while the host runs, and starts it. Before the host starts this is just
        /// <see cref="AddPlugin"/>; afterwards it must be called on the game thread - from a
        /// plugin's own callback, or through <see cref="RunOnGameThread"/> - because that is the
        /// only thread that touches the plugin list. Returns whether it started.
        /// </summary>
        /// <remarks>
        /// A plugin added this way has missed everything that happened before it arrived - the
        /// server announcing itself, the player's creation, every object already in view. What
        /// it needs of that it reads from <see cref="World"/> and <see cref="Character"/>, which
        /// are current; the events carry on from here.
        /// </remarks>
        public bool Attach(IPlugin plugin)
        {
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));

            if (!_started)
            {
                AddPlugin(plugin);
                return true;
            }

            RequireGameThread();

            if (_plugins.Contains(plugin))
                return true;

            _plugins.Add(plugin);
            try
            {
                plugin.Startup(this);
                _log.Info($"Plugin {plugin.Name} started.");
                _chatBox.RefreshCommandWords(_plugins);
                return true;
            }
            catch (Exception ex)
            {
                Statistics.PluginExceptions++;
                _log.Error($"Plugin {plugin.Name} failed to start.", ex);
                Detach(plugin, callShutdown: true);
                return false;
            }
        }

        /// <summary>
        /// Takes a plugin out while the host runs: it is shut down, its settings saved, and
        /// anything of its still listening to the host's events is let go, so it can be
        /// unloaded. Before the host starts it is simply removed; afterwards, game thread only,
        /// as for <see cref="Attach"/>.
        /// </summary>
        public void Detach(IPlugin plugin)
        {
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));

            if (!_started)
            {
                _plugins.Remove(plugin);
                return;
            }

            RequireGameThread();
            Detach(plugin, callShutdown: true);
        }

        /// <summary>True on the game thread, where plugins may be touched.</summary>
        public bool IsGameThread => _gameThread != null && Thread.CurrentThread == _gameThread;

        private void RequireGameThread()
        {
            if (!IsGameThread)
                throw new InvalidOperationException("Plugins are added and removed on the game thread; use RunOnGameThread.");
        }

        /// <summary>Game thread only.</summary>
        private void Detach(IPlugin plugin, bool callShutdown)
        {
            if (!_plugins.Remove(plugin))
                return;

            // A plugin that goes may have been walking; nothing it held stays held. Nor are
            // its commands kept from the server any longer.
            _input?.ReleaseAll();
            _chatBox.RefreshCommandWords(_plugins);

            if (callShutdown)
            {
                try
                {
                    plugin.Shutdown();
                }
                catch (Exception ex)
                {
                    Statistics.PluginExceptions++;
                    _log.Error($"Plugin {plugin.Name} failed to shut down.", ex);
                }
            }

            (IPlugin Plugin, Func<bool> Save)[] theirs;
            lock (_settingsToSave)
            {
                theirs = _settingsToSave.Where(s => s.Plugin == plugin).ToArray();
                _settingsToSave.RemoveAll(s => s.Plugin == plugin);
            }

            foreach ((IPlugin _, Func<bool> save) in theirs)
            {
                try
                {
                    save();
                }
                catch (Exception ex)
                {
                    _log.Error($"Plugin {plugin.Name}'s settings could not be saved.", ex);
                }
            }

            int released = ReleaseHandlersFrom(plugin.GetType().Assembly);
            if (released > 0)
                _log.Info($"Plugin {plugin.Name} left {released} event handler(s) attached; they have been removed.");
        }

        /// <summary>
        /// Removes every handler on the host's events whose code lives in <paramref name="assembly"/>.
        /// A plugin that forgets to unsubscribe would otherwise keep running after it was switched
        /// off, and keep its assembly from ever being unloaded.
        /// </summary>
        private int ReleaseHandlersFrom(System.Reflection.Assembly assembly)
        {
            int released = 0;

            // The host's own plugins live beside the host; their handlers cannot be told from
            // the host's, and they are never unloaded anyway.
            if (assembly == typeof(GameHost).Assembly)
                return 0;

            foreach (System.Reflection.FieldInfo field in typeof(GameHost).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
            {
                if (!typeof(Delegate).IsAssignableFrom(field.FieldType) || field.GetValue(this) is not Delegate current)
                    continue;

                Delegate kept = null;
                foreach (Delegate subscriber in current.GetInvocationList())
                {
                    if (subscriber.Method.Module.Assembly == assembly || subscriber.Target?.GetType().Assembly == assembly)
                        released++;
                    else
                        kept = Delegate.Combine(kept, subscriber);
                }

                field.SetValue(this, kept);
            }

            return released;
        }

        /// <summary>Registers a plugin. Must be called before <see cref="StartAsync"/>.</summary>
        public void AddPlugin(IPlugin plugin)
        {
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));
            if (_started) throw new InvalidOperationException("Plugins are added before the host starts.");

            _plugins.Add(plugin);
        }

        /// <summary>
        /// Starts plugins, the game thread, the tick timer and the transport, in that
        /// order - so a plugin is fully up before the first message can reach it.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_started) throw new InvalidOperationException("Already started.");
            _started = true;

            _transport.MessageReceived += OnMessageReceived;
            _transport.Ended += OnTransportEnded;
            if (_transport is ISessionBoundaries sessions)
                sessions.SessionEnded += OnSessionEnded;
            if (_transport is ISessionStarts starts)
                starts.SessionStarted += OnSessionStarted;

            _gameThread = new Thread(GameLoop) { Name = "AC.Host game thread", IsBackground = true };
            _gameThread.Start();

            // Plugin startup happens on the game thread like every other callback.
            TaskCompletionSource pluginsUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RunOnGameThread(() =>
            {
                foreach (IPlugin plugin in _plugins)
                {
                    try
                    {
                        plugin.Startup(this);
                        _log.Info($"Plugin {plugin.Name} started.");
                    }
                    catch (Exception ex)
                    {
                        Statistics.PluginExceptions++;
                        _log.Error($"Plugin {plugin.Name} failed to start.", ex);
                    }
                }

                _chatBox.RefreshCommandWords(_plugins);
                pluginsUp.SetResult();
            });
            await pluginsUp.Task.ConfigureAwait(false);

            _lastTick = DateTimeOffset.UtcNow;
            _tickTimer = new Timer(_ => RunOnGameThread(RaiseTick), null, 100, 100);

            await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        private void OnMessageReceived(object sender, GameMessageEventArgs e)
        {
            // Here, on the relay's thread, so the player's own appraisal is known before its answer
            // is judged - against a server on this machine that can be a millisecond later. The
            // host's own, which the relay sees go on in the client's packets too, are not the
            // player's: their answers stay the host's.
            if (e.Direction == PacketDirection.Outbound && !e.FromHost)
                _appraisals.Sent(e.Message.Opcode, e.Message.Payload.Span);

            bool fromHost = e.FromHost;
            RunOnGameThread(() => ApplyMessage(e.Direction, e.Message, fromHost));
        }

        private readonly AppraisalRequests _appraisals = new AppraisalRequests();

        /// <summary>
        /// The answers to the host's own appraisals kept from the client so far, and how many of
        /// them came in several fragments; null when the transport cannot keep anything from it.
        /// </summary>
        public (int Answers, int Split)? AppraisalsWithheld
            => _transport is IClientboundFilter filter && filter.CanWithholdFromClient
                ? (filter.MessagesWithheldFromClient, filter.SplitMessagesWithheldFromClient)
                : null;

        /// <summary>
        /// The session ended, which no message says: the character is out of the world, and the
        /// server gone. Queued behind the messages before it, like any of them.
        /// </summary>
        private void OnSessionEnded(object sender, SessionEnd end)
            => RunOnGameThread(() =>
            {
                _world.EndSession(SessionBoundary.Describe(end));
                ForgetSessionInJournal();
                Session?.OnSessionEnded(SessionBoundary.Describe(end));
            });

        private void OnTransportEnded(object sender, EventArgs e)
        {
            // Let everything already queued run, then finish.
            RunOnGameThread(() => _queue.CompleteAdding());
        }

        /// <param name="fromHost">
        /// Whether a message going to the server is the host's own, sent as the client: what the
        /// host asked about is not what the player selected (<see cref="MessageDecoder.Apply(AcMessage, PacketDirection, WorldState, bool)"/>),
        /// and is not kept for the next host as the player's selection either.
        /// </param>
        private void ApplyMessage(PacketDirection direction, AcMessage message, bool fromHost = false)
        {
            if (direction == PacketDirection.Inbound)
                Statistics.MessagesInbound++;
            else
                Statistics.MessagesOutbound++;

            uint vendorBefore = _world.Character.OpenVendorId;
            DecodeOutcome outcome;
            _applyingMessage = true;
            try
            {
                outcome = MessageDecoder.Apply(message, direction, _world, fromHost);
            }
            catch (Exception ex)
            {
                // A decoder must not throw; if one does, that is a bug to see, not hide.
                _log.Error($"Decoder threw on opcode 0x{message.Opcode:X4}.", ex);
                outcome = DecodeOutcome.Malformed;
            }
            finally
            {
                _applyingMessage = false;
            }

            // Kept for the next host, whatever the decoder made of it: a newer one may make more.
            // Not the host's own questions about an object, which the next host would take for
            // the player's selection; their answers are kept, as everything the server says is.
            if (!(fromHost && MessageDecoder.IsSelectionQuestion(message)))
                Journal(direction, message, vendorBefore);

            // A logout or an entering of the world under way moves on with what either end says.
            try
            {
                Session?.OnMessage(direction, message);
            }
            catch (Exception ex)
            {
                _log.Error($"Following the character list threw on opcode 0x{message.Opcode:X4}.", ex);
            }

            // Every client action shares one opcode, so the opcode alone says nothing
            // about what was sent.
            if (direction == PacketDirection.Outbound
                && message.Opcode == Opcodes.GameAction
                && MessageDecoder.TryReadActionType(message, out uint actionType))
            {
                Statistics.OutboundActions.AddOrUpdate(actionType, 1, (_, n) => n + 1);
            }

            switch (outcome)
            {
                case DecodeOutcome.Applied:
                    Statistics.Applied++;
                    break;
                case DecodeOutcome.Ignored:
                    Statistics.Ignored++;
                    if (direction == PacketDirection.Inbound)
                    {
                        Statistics.IgnoredOpcodes.AddOrUpdate(message.Opcode, 1, (_, n) => n + 1);

                        if (MessageDecoder.TryReadGameEventType(message, out uint eventType))
                            Statistics.IgnoredGameEvents.AddOrUpdate(eventType, 1, (_, n) => n + 1);
                    }

                    break;
                case DecodeOutcome.Malformed:
                    Statistics.Malformed++;
                    Statistics.MalformedOpcodes.AddOrUpdate(message.Opcode, 1, (_, n) => n + 1);
                    break;
            }

            // After the world, as every other event is, so a plugin reading the bytes and looking
            // the object up finds the world already agreeing with them. Whatever the decoder made
            // of the message, its bytes went by, so they are given either way.
            if (MessageSeen != null)
                Raise(MessageSeen, new GameMessageEventArgs(direction, message));
        }

        private void GameLoop()
        {
            try
            {
                foreach (Action action in _queue.GetConsumingEnumerable(_stopping.Token))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        _log.Error("Unhandled exception on the game thread.", ex);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stopping.
            }
            finally
            {
                foreach (IPlugin plugin in _plugins)
                {
                    try
                    {
                        plugin.Shutdown();
                    }
                    catch (Exception ex)
                    {
                        Statistics.PluginExceptions++;
                        _log.Error($"Plugin {plugin.Name} failed to shut down.", ex);
                    }
                }

                _ended.TrySetResult();
            }
        }

        private void RaiseTick()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan elapsed = now - _lastTick;
            _lastTick = now;

            try
            {
                _input?.Tick(elapsed);
            }
            catch (Exception ex)
            {
                _log.Error("The movement keys could not be kept up to date.", ex);
            }

            try
            {
                Session?.Tick();
            }
            catch (Exception ex)
            {
                _log.Error("A logout or an entering of the world could not be followed.", ex);
            }

            // What has been out of the character's sight long enough is let go of, as the client
            // lets go of it; the server says nothing either way.
            try
            {
                _world.ForgetOutOfView();
            }
            catch (Exception ex)
            {
                _log.Error("Objects out of view could not be let go of.", ex);
            }

            Raise(Tick, elapsed);
        }

        private void Raise<T>(EventHandler<T> handler, T args)
        {
            // Nothing is said while a session handed over is replayed: plugins hear it as a login,
            // once the world has been rebuilt.
            if (handler == null || _replaying)
                return;

            foreach (Delegate subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((EventHandler<T>)subscriber)(this, args);
                }
                catch (Exception ex)
                {
                    Statistics.PluginExceptions++;
                    _log.Error($"A plugin handler threw on {typeof(T).Name}.", ex);
                }
            }
        }

        private void RaisePlain(EventHandler handler)
        {
            if (handler == null || _replaying)
                return;

            foreach (Delegate subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((EventHandler)subscriber)(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Statistics.PluginExceptions++;
                    _log.Error("A plugin handler threw.", ex);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            // Settings first, while every plugin is still loaded and its values are
            // whatever it last set them to. A plugin still changing settings after the
            // host has begun disposing is a plugin bug; anything that matters mid-session
            // is the plugin's to Save itself.
            (IPlugin Plugin, Func<bool> Save)[] toSave;
            lock (_settingsToSave)
                toSave = _settingsToSave.ToArray();

            foreach ((IPlugin _, Func<bool> save) in toSave)
            {
                try
                {
                    save();
                }
                catch (Exception ex)
                {
                    _log.Error("A plugin's settings could not be saved at shutdown.", ex);
                }
            }

            // DisposeAsync waits for a callback that is already running; Dispose does
            // not. Without the wait, a tick in flight reaches the queue after it has
            // gone - and does so from a thread-pool thread, where the resulting
            // exception is unhandled and the process dies.
            if (_tickTimer != null)
            {
                await _tickTimer.DisposeAsync().ConfigureAwait(false);
                _tickTimer = null;
            }

            _transport.MessageReceived -= OnMessageReceived;
            _transport.Ended -= OnTransportEnded;
            if (_transport is ISessionBoundaries sessions)
                sessions.SessionEnded -= OnSessionEnded;
            if (_transport is ISessionStarts starts)
                starts.SessionStarted -= OnSessionStarted;

            await _transport.DisposeAsync().ConfigureAwait(false);

            if (!_queue.IsAddingCompleted)
                _queue.CompleteAdding();

            if (_gameThread != null)
            {
                if (!_gameThread.Join(TimeSpan.FromSeconds(5)))
                    _stopping.Cancel();
            }

            _stopping.Dispose();
            _queue.Dispose();
        }

        /// <summary>
        /// Stands in until the host can inject client messages. Reports failure rather
        /// than throwing, so a plugin written against the real thing keeps running.
        /// </summary>
        private sealed class UnavailableActions : IGameActions
        {
            private readonly IPluginLog _log;
            private bool _warned;

            internal UnavailableActions(IPluginLog log)
            {
                _log = log;
            }

            public bool IsAvailable => false;

            public Task<bool> AppraiseAsync(uint objectId) => Decline("appraise");

            public Task<bool> UseAsync(uint objectId) => Decline("use");

            public Task<bool> UseOnAsync(uint sourceId, uint targetId) => Decline("use on");

            public Task<bool> MoveToContainerAsync(uint objectId, uint containerId, int slot = 0) => Decline("move");

            public Task<bool> DropAsync(uint objectId) => Decline("drop");

            public Task<bool> SayAsync(string text) => Decline("say");

            public Task<bool> MoveAsync(AC.Host.Decoding.ClientMotionState motion) => Decline("move");

            public Task<bool> WalkForwardAsync(float speed = 1.0f) => Decline("walk");

            public Task<bool> TurnAsync(float speed = 1.0f) => Decline("turn");

            public Task<bool> StopAsync() => Decline("stop");

            public Task<bool> CastAsync(uint targetId, uint spellId) => Decline("cast");

            public Task<bool> SetCombatModeAsync(AC.Host.Decoding.CombatMode mode) => Decline("change stance");

            public Task<bool> QueryHealthAsync(uint objectId) => Decline("query health");

            public Task<bool> MeleeAttackAsync(uint targetId, AC.Host.Decoding.AttackHeight height, float power) => Decline("attack");

            public Task<bool> MissileAttackAsync(uint targetId, AC.Host.Decoding.AttackHeight height, float accuracy) => Decline("shoot");

            public Task<bool> CancelAttackAsync() => Decline("cancel an attack");

            public Task<bool> CastUntargetedAsync(uint spellId) => Decline("cast");

            public Task<bool> WieldAsync(uint objectId, uint slot) => Decline("wield");

            public Task<bool> StackableMergeAsync(uint fromStackId, uint toStackId, int amount) => Decline("merge stacks");

            public Task<bool> SalvageAsync(uint toolId, IReadOnlyList<uint> itemIds) => Decline("salvage");

            public Task<bool> GiveAsync(uint objectId, uint targetId, int amount) => Decline("give");

            public Task<bool> LogOutAsync() => Decline("log out");

            public Task<bool> EnterWorldAsync(uint characterId) => Decline("enter the world");

            public Task<bool> FellowshipRecruitAsync(uint playerId) => Decline("recruit to the fellowship");

            public Task<bool> FellowshipQuitAsync(bool disband) => Decline(disband ? "disband the fellowship" : "leave the fellowship");

            public Task<bool> FellowshipDismissAsync(uint playerId) => Decline("dismiss a fellow");

            public Task<bool> FellowshipAssignLeaderAsync(uint playerId) => Decline("hand on the fellowship's leadership");

            public Task<bool> FellowshipSetOpenAsync(bool open) => Decline("open or close the fellowship");

            private Task<bool> Decline(string what)
            {
                if (!_warned)
                {
                    _warned = true;
                    _log.Warn($"A plugin asked to {what}; actions are not available until message injection exists.");
                }

                return Task.FromResult(false);
            }
        }
    }
}
