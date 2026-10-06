using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AC.Dat;
using AC.Host.Handover;
using AC.Host.Plugins;
using AC.Host.Transport;
using AC.Host.World;

namespace AC.Host.Runtime
{
    /// <summary>
    /// A whole running host: the relay (or a replay), the world, the game thread, the plugins
    /// and Decal's window, the injected overlay's pipe, and the pipe <c>achost ctl</c> talks to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what <c>achost run --overlay</c> starts, taken out of its command line so that the
    /// Decal Agent can start exactly the same thing in a window of its own. Two programs that
    /// each assembled a host from the parts would sooner or later disagree about what a host is
    /// - one would publish the overlay a little differently, or forget to route a command - and
    /// the player would see a game that worked under one and not the other.
    /// </para>
    /// <para>
    /// It is built in two steps. The constructor assembles everything and starts nothing, so a
    /// caller can listen to the host and the transport - achost's dumps and tests do - before
    /// the first message arrives; <see cref="StartAsync"/> then starts the plugins and the relay,
    /// and only once the relay has its ports opens the pipes, so a host that cannot have its
    /// ports never holds a pipe name another one is using.
    /// </para>
    /// <para>
    /// Everything that touches a plugin still happens on the host's game thread. The properties
    /// here hand out the parts; what may be done with them from which thread is the parts' own
    /// business, and <see cref="PluginManager"/> in particular is for the game thread once the
    /// host has started.
    /// </para>
    /// </remarks>
    public sealed class HostRuntime : IAsyncDisposable
    {
        /// <summary>How often the overlay is offered a snapshot: faster than anyone reads.</summary>
        private static readonly TimeSpan PublishEvery = TimeSpan.FromMilliseconds(250);

        private readonly IPluginLog _log;
        private AC.Host.Overlay.OverlayServer _overlay;
        private AC.Host.Overlay.OverlayImagePublisher _overlayImages;
        private AC.Host.Overlay.ViewLooks _viewLooks;
        private AC.Host.Overlay.OverlayDecalBar _decalBar;
        private TimeSpan _sincePublished;
        private bool _overlayAttached;
        private bool _started;
        private bool _disposed;

        /// <summary>Assembles a host from <paramref name="options"/>. Nothing is started, bound or opened yet.</summary>
        /// <exception cref="ArgumentException">The options cannot describe a host: a live one with no server to relay to.</exception>
        public HostRuntime(HostRuntimeOptions options, IPluginLog log)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));
            _log = log ?? throw new ArgumentNullException(nameof(log));

            try
            {
                Transport = CreateTransport(options);

                World = new WorldState();
                Portal = options.NoDat ? null : OpenPortalData(options.DatPath, log);
                if (Portal != null)
                    World.GameData = new PortalGameData(Portal);

                Cells = Portal != null ? OpenCellData(Portal.Path, log) : null;
                World.Cells = Cells;

                // The ground, walls and objects a plugin tests a projectile's path against, read
                // from the same two archives on workers as places are first needed.
                if (Portal != null && Cells != null)
                {
                    ArchiveGeometry geometry = new ArchiveGeometry(AC.Dat.Geometry.WorldGeometry.Open(Portal, Cells), World);
                    geometry.ReadFailed += (_, ex) => log.Warn($"Could not read the world's geometry: {ex.Message}");
                    World.Geometry = geometry;
                }

                Host = new GameHost(Transport, log, options.Settings, options.DataDirectory, World);
                Host.ActionsAllowed = options.EnableActions;

                // A host before this one on the same ports may have left the session it was
                // relaying, the game still connected; the relay takes up its numbering before it
                // relays anything, and the host the world, if the session turns out to go on.
                if (HandsOver)
                    TakeHandover();
                else if (IsLive)
                    log.Info("This host was told not to hand sessions over: it neither looks for one the host before it left nor leaves one for the next.");

                if (!options.NoPlugins)
                {
                    // Decal's own window first, so it is first on the bar; then every installed
                    // plugin the player has not switched off, each in a copy that can be reloaded.
                    Plugins = new PluginManager(Host, options.PluginDirectory, log, options.DataDirectory);
                    Decal = new AC.Host.Decal.DecalAgent(Host, Plugins);
                    Host.AddPlugin(Decal);
                    Plugins.LoadAll();

                    foreach (PluginEntry entry in Plugins.Entries)
                        log.Info($"Found plugin {entry.Name} in {entry.AssemblyPath}: {entry.Status}");

                    if (Plugins.Entries.Count == 0)
                        log.Warn($"No plugins found in {options.PluginDirectory}.");
                }

                // `achost ctl ...` from another window: reload a rebuilt plugin, switch acting.
                // A replay is over by itself and has nothing to drive.
                if (!options.IsReplay && !string.IsNullOrEmpty(options.ControlPipeName))
                    Control = new ControlPipe(Host, Plugins, options.ControlPipeName);

                if (options.Overlay)
                    SetUpOverlay();

                // The client's own word on which screen it shows, which no message carries: what a
                // logout or a login checks it ended where it should.
                string clientLog = options.ClientLogPath ?? (Transport is ProxyTransport ? null : string.Empty);
                if (clientLog != string.Empty && Host.Session != null)
                {
                    Func<string> find = clientLog == null ? ClientLog.FindPath : () => System.IO.File.Exists(clientLog) ? clientLog : null;
                    AC.Host.Actions.SessionControl session = Host.Session;
                    ClientLog = new ClientLog(find, line => Host.RunOnGameThread(() => session.NoteClientLog(line)));
                }

                // The client's own settings, read only: whether its Unattended Combat Manager - or
                // another of its plugins that can play the character - is switched on, and the
                // Desktop UI Scale its character select is drawn at.
                string clientSettings = options.ClientSettingsFolder ?? (Transport is ProxyTransport ? null : string.Empty);
                if (clientSettings != string.Empty)
                {
                    Func<string> findSaved = clientSettings == null
                        ? () => ClientSettingsWatcher.FindSavedFolder(ClientLog?.Path)
                        : () => System.IO.Directory.Exists(clientSettings) ? clientSettings : null;
                    ClientSettings = new ClientSettingsWatcher(findSaved,
                        (plugins, scale, bar) => Host.RunOnGameThread(() => Host.NoteClientSettings(plugins, scale, bar)));
                }

                Host.ServerConnected += (_, name) => log.Info($"Connected to server \"{name}\".");
                Host.PlayerIdentified += (_, id) => log.Info($"Player object is 0x{id:X8}.");

                // Said while the character is still described, so the line names who left.
                Host.LoggedOff += (_, why) =>
                {
                    string who = string.IsNullOrEmpty(Host.Character.Name) ? "The character" : Host.Character.Name;
                    log.Info($"{who} (0x{Host.Character.Id:X8}) has left the world: {why}.");
                };
            }
            catch
            {
                // Whatever was made before the failure holds files, and perhaps a capture open;
                // a half-built host is let go of here rather than left to the finaliser.
                DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw;
            }
        }

        public HostRuntimeOptions Options { get; }

        /// <summary>The host plugins run in.</summary>
        public GameHost Host { get; }

        /// <summary>Where the host's messages come from: the relay, a replay, or what a test gave it.</summary>
        public IGameTransport Transport { get; }

        /// <summary>The world the host keeps, which only the game thread changes.</summary>
        public WorldState World { get; }

        /// <summary>The client's portal data, or null when there is none to read.</summary>
        public PortalData Portal { get; }

        /// <summary>The client's indoor cells, beside its portal data, or null when there are none to read.</summary>
        public CellData Cells { get; }

        /// <summary>Decal's plugin list, or null for a host run without plugins.</summary>
        public PluginManager Plugins { get; }

        /// <summary>Decal's own window in the game, or null for a host run without plugins.</summary>
        public AC.Host.Decal.DecalAgent Decal { get; }

        /// <summary>The pipe to the injected overlay, or null when the overlay is not served.</summary>
        public AC.Host.Overlay.OverlayServer Overlay => _overlay;

        /// <summary>The pipe <c>achost ctl</c> talks to, or null when none is served.</summary>
        public ControlPipe Control { get; }

        /// <summary>The game client's own log, read for which screen it shows; null when it is not read.</summary>
        public ClientLog ClientLog { get; }

        /// <summary>How often the client's log is read for what it has written since.</summary>
        private static readonly TimeSpan ReadClientLogEvery = TimeSpan.FromMilliseconds(250);

        private System.Threading.Timer _clientLogTimer;
        private int _readingClientLog;

        /// <summary>Reads the client's log off the game thread; one read at a time.</summary>
        private void ReadClientLog()
        {
            if (Interlocked.Exchange(ref _readingClientLog, 1) == 1)
                return;

            try
            {
                ClientLog?.Poll();
            }
            catch (Exception ex)
            {
                _log.Warn("Could not read the game client's log: " + ex.Message);
            }
            finally
            {
                Volatile.Write(ref _readingClientLog, 0);
            }
        }

        /// <summary>The game client's own settings, watched for its plugins and its UI scale; null when they are not read.</summary>
        public ClientSettingsWatcher ClientSettings { get; }

        private System.Threading.Timer _clientSettingsTimer;
        private int _readingClientSettings;

        /// <summary>Looks at the client's settings off the game thread; one look at a time.</summary>
        private void ReadClientSettings()
        {
            if (Interlocked.Exchange(ref _readingClientSettings, 1) == 1)
                return;

            try
            {
                ClientSettings?.Poll();
            }
            catch (Exception ex)
            {
                _log.Warn("Could not read the game client's settings: " + ex.Message);
            }
            finally
            {
                Volatile.Write(ref _readingClientSettings, 0);
            }
        }

        /// <summary>Whether the injected overlay is attached right now.</summary>
        public bool OverlayAttached => Overlay?.IsConnected == true;

        /// <summary>Whether this host relays a live game rather than a recording.</summary>
        public bool IsLive => !Options.IsReplay;

        /// <summary>
        /// Whether the session is handed from host to host: a live host takes up what the one
        /// before it left on the same ports, and leaves the same for the next when it stops.
        /// </summary>
        public bool HandsOver => IsLive && !Options.NoHandover;

        /// <summary>Where this host's session is handed over, or null when it is not.</summary>
        public string HandoverPath => _handoverPath;

        private string _handoverPath;

        /// <summary>What the host before this one handed over and this one took, or null.</summary>
        private HandoverSnapshot _taken;

        /// <summary>
        /// Starts the plugins and the relay, and then the pipes. Returns once everything is up;
        /// the relay and the game thread carry on until the runtime is disposed.
        /// </summary>
        /// <remarks>
        /// When the relay cannot have its ports - most often because another host is already on
        /// them - this throws, and the runtime is left to be disposed; nothing of it is listening.
        /// </remarks>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HostRuntime));
            if (_started) throw new InvalidOperationException("Already started.");
            _started = true;

            _log.Info($"Transport: {Transport.Description}");
            _log.Info(!Transport.CanSend
                ? "Watching only: a replay cannot act."
                : Host.ActionsAllowed
                    ? "Actions ENABLED: plugins can make your character act."
                    : "Watching only for now; plugins cannot act until \"Let plugins act\" is ticked in Decal's window (Ctrl+click the right-hand grip of Decal's bar, or the Decal Agent's tray icon), or pass --enable-actions.");

            if (Transport is ProxyTransport)
                _log.Info($"In the client, add a custom server with host {Options.Proxy.ListenAddress} and port {Options.Proxy.ListenPort}.");

            await Host.StartAsync(cancellationToken).ConfigureAwait(false);

            Control?.Start();
            Overlay?.Start();

            if (ClientLog != null)
                _clientLogTimer = new System.Threading.Timer(_ => ReadClientLog(), null, ReadClientLogEvery, ReadClientLogEvery);

            if (ClientSettings != null)
                _clientSettingsTimer = new System.Threading.Timer(_ => ReadClientSettings(), null, TimeSpan.Zero, ClientSettingsWatcher.LookEvery);
        }

        private static IGameTransport CreateTransport(HostRuntimeOptions options)
        {
            if (options.Transport != null)
                return options.Transport;

            if (options.IsReplay)
                return new CaptureTransport(options.ReplayPath);

            if (string.IsNullOrWhiteSpace(options.Proxy?.ServerHost))
                throw new ArgumentException("There is no server to relay to.");

            // The rewriters are always fitted, and cost nothing while nothing is queued;
            // whether plugins may use them is the player's switch, which starts where
            // --enable-actions puts it and can be thrown from Decal's window in the game.
            return new ProxyTransport(options.Proxy, enableActions: true);
        }

        // ------------------------------------------------------------------- the overlay

        private void SetUpOverlay()
        {
            AC.Host.Overlay.OverlayServer server = new AC.Host.Overlay.OverlayServer(
                Options.OverlayPipeName,
                (message, error) => _log.Warn("[overlay] " + message + (error != null ? " - " + error.Message : string.Empty)));
            _overlay = server;

            // Routed to the plugin whose window the click happened in. The host
            // queues it onto the game thread itself, so the pipe thread this arrives
            // on never touches a plugin.
            server.CommandReceived += (_, command) =>
            {
                // The overlay asking for art it wants to draw, not a click for a plugin.
                if (command.Owner.Length == 0 && command.Name == "need-image")
                {
                    _overlayImages?.Want(command.Value);
                    return;
                }

                // A line the overlay has for the game's chat - VVS's word on a hudified window, the
                // only one it sends - as VVS wrote its own: "[VVS] " and chat type 6 (Service.a).
                if (command.Owner.Length == 0 && command.Name == "say")
                {
                    string line = command.Value;
                    Host.RunOnGameThread(() => Host.ShowInGame("[VVS] " + line, 6));
                    return;
                }

                // What the game's window is doing - minimized, drawing, parked - said when it
                // changes and to each host that connects.
                if (command.Owner.Length == 0 && command.Name == "game-window")
                {
                    if (GameWindowState.TryParse(command.Value, out GameWindowState window))
                        Host.RunOnGameThread(() => Host.NoteGameWindow(window));
                    else
                        _log.Warn($"[overlay] The overlay said the game window is \"{command.Value}\", which is not a state.");
                    return;
                }

                Host.DispatchCommand(command.Owner, new OverlayCommand(command.Name, command.Value, command.RowId, command.ControlId));
            };

            // Walking is done with the game's own keys, which the overlay presses; entering the
            // world, with a click on the character select, which it makes.
            Host.InputKeys.Publish = held => server.PublishInput(held);
            Host.InputKeys.PublishClick = click => server.PublishClick(new AC.Host.Overlay.OverlayClick
            {
                LayoutWidth = click.LayoutWidth,
                LayoutHeight = click.LayoutHeight,
                Points = click.Points.Select(p => new[] { p.X, p.Y }).ToList(),
                UiScale = click.UiScale == 1.0 ? null : click.UiScale,
            });
            Host.InputKeys.Attached = () => server.IsConnected;

            _log.Info($"Overlay: publishing on the named pipe {server.PipeName}.");
            _log.Info("Inject ACUnrealOverlay.dll into the client and it will attach on its own.");

            // The Decal theme's artwork, from wherever Decal, Virindi View Service and the
            // client are installed. Said once, in full, because a theme that draws as plain
            // stand-ins has exactly one cause and it is on this list.
            DecalInstall install = DecalInstall.Detect();
            foreach (string line in install.Describe())
                _log.Info("Overlay artwork: " + line);

            _overlayImages = new AC.Host.Overlay.OverlayImagePublisher(
                server,
                new ImageCatalog(Portal, install),
                message => _log.Warn("[overlay] " + message));
            _overlayImages.PublishTheme();
            _log.Info($"Overlay artwork: {_overlayImages.Published} theme images ready, {_overlayImages.Missing} missing.");

            // How each window first looks - its theme, whether it is hudified - as the player
            // last had it in the standard client, from Virindi View Service's own store.
            _viewLooks = AC.Host.Overlay.ViewLooks.Load(install.VirindiViewServicePath, line => _log.Info("Overlay: " + line));

            // Decal's bar as the player left it: compact or not, which edge, how long.
            _decalBar = AC.Host.Overlay.OverlayDecalBar.ReadRegistry();
            if (_decalBar != null)
                _log.Info($"Overlay: Decal's bar from its registry: {(_decalBar.State == 1 ? "compact" : "expanded")}, {(_decalBar.Dock == 0 ? "top" : _decalBar.Dock == 1 ? "left" : "right")}, {_decalBar.Length} long.");

            // Published on the tick rather than on every change: the tick is already
            // on the game thread, which is the only place a plugin's panels can be
            // read safely, and four times a second is faster than anyone reads.
            Host.Tick += PublishOverlay;
        }

        private void PublishOverlay(object sender, TimeSpan elapsed)
        {
            _sincePublished += elapsed;
            if (_sincePublished < PublishEvery)
                return;

            _sincePublished = TimeSpan.Zero;
            OverlaySnapshot.Publish(Host, _overlay, _overlayImages, _viewLooks, _decalBar);

            // Worth saying out loud: whether the injected side is actually there
            // is otherwise invisible from here, and "the overlay is not showing
            // anything" has two completely different causes.
            if (_overlay.IsConnected != _overlayAttached)
            {
                _overlayAttached = _overlay.IsConnected;
                _log.Info(_overlayAttached
                    ? "Overlay attached."
                    : "Overlay detached.");

                // An overlay that has gone says nothing more about the window.
                if (!_overlayAttached)
                    Host.NoteGameWindow(GameWindowState.Unknown);
            }
        }

        // ------------------------------------------------------------------- client data

        /// <summary>
        /// Opens the client's portal data, or returns null with a word about why.
        /// </summary>
        /// <remarks>
        /// Never fatal. Without it the host still decodes everything; it just cannot
        /// turn a spell id into a name or a palette slot into a colour, and says so
        /// rather than letting a loot rule quietly never match.
        /// </remarks>
        internal static PortalData OpenPortalData(string datPath, IPluginLog log)
        {
            string path = datPath;

            if (path != null && System.IO.Directory.Exists(path))
                path = System.IO.Path.Combine(path, "client_portal.dat");

            path ??= PortalData.FindPortalDat();

            if (path == null)
            {
                log.Warn("No client_portal.dat found. Spell names and palette colours will be unavailable; pass --dat to point at one.");
                return null;
            }

            try
            {
                PortalData portal = PortalData.Open(path);
                log.Info($"Client data: {portal.Spells.Count} spells, {portal.Skills.Count} skills from {path}");
                return portal;
            }
            catch (Exception ex)
            {
                log.Warn($"Could not read {path}: {ex.Message}. Continuing without client data.");
                return null;
            }
        }

        /// <summary>
        /// Opens the client's cell archive from beside its portal data, or returns null with a word
        /// about why.
        /// </summary>
        /// <remarks>
        /// Never fatal either. It says which cells of a building or a dungeon can be seen from
        /// which, and so when the client lets go of an object indoors; without it, everything
        /// within a landblock of the character is kept, which never lets go of what the client
        /// still has.
        /// </remarks>
        internal static CellData OpenCellData(string portalDatPath, IPluginLog log)
        {
            string path = CellData.FindBeside(portalDatPath);
            if (path == null)
            {
                log.Warn($"No {CellData.FileName} beside {portalDatPath}. Indoors, objects will be kept while within a landblock of the character.");
                return null;
            }

            try
            {
                CellData cells = CellData.Open(path);
                log.Info($"Client cells from {path}");
                return cells;
            }
            catch (Exception ex)
            {
                log.Warn($"Could not read {path}: {ex.Message}. Indoors, objects will be kept while within a landblock of the character.");
                return null;
            }
        }

        // ------------------------------------------------------------------- handing over

        /// <summary>
        /// Reads what the host before this one on the same ports left, and deletes it: the relay
        /// resumes its numbering now, before relaying anything, and the host is offered the world.
        /// </summary>
        /// <remarks>
        /// Always says, in the log and to <c>ctl status</c>, the whole path it looked at and what it
        /// found there: nothing, the session taken, or why what was there was not. A handover that
        /// went missing between two hosts is otherwise invisible - the next host simply says it
        /// joined a session it knows nothing of - and the two paths side by side in the log are what
        /// tell a file written somewhere else from a file never written. Nothing found here is ever a
        /// reason for the host not to start.
        /// </remarks>
        private void TakeHandover()
        {
            _handoverPath = System.IO.Path.GetFullPath(HandoverSnapshot.PathIn(Host.DataDirectory, Options.Proxy.ListenPort));

            try
            {
                HandoverSnapshot found = HandoverSnapshot.Take(_handoverPath, Options.Proxy, DateTimeOffset.UtcNow, out string refusal);

                // Read, and meant to be gone: one left behind would be taken again by a host started next.
                string leftBehind = (found != null || refusal != null) && System.IO.File.Exists(_handoverPath)
                    ? $" It could not be deleted, so a host started within {HandoverSnapshot.MaxAge.TotalMinutes:0} minutes would take it again."
                    : string.Empty;

                if (refusal != null)
                {
                    _log.Info(refusal + leftBehind);
                    Host.HandoverLookup = "not taken: " + refusal;
                    return;
                }

                if (found == null)
                {
                    _log.Info($"No session was handed over: there is nothing at {_handoverPath}. {WhatIsBeside(_handoverPath)}");
                    Host.HandoverLookup = "none at " + _handoverPath;
                    return;
                }

                // Harmless if the session turns out to be a new one: a login starts the numbering over.
                if (found.Relay != null && Transport is ProxyTransport relay)
                    relay.Resume(found.Relay);

                Host.OfferHandover(found);
                _taken = found;
                Host.HandoverLookup = $"taken from {_handoverPath}, written at {found.WrittenAt.ToLocalTime():HH:mm:ss} ({found.Journal.Count} message(s))";
                _log.Info($"The host before this one handed over the session it was relaying at {found.WrittenAt.ToLocalTime():HH:mm:ss}, in {_handoverPath} "
                    + $"({found.Journal.Count} message(s)); it is carried on if the game is still connected.{leftBehind}");
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the player is better served by a host that starts knowing
                // nothing - and asks for a login - than by one that does not start at all.
                _log.Warn($"The session handed over in {_handoverPath} could not be taken up ({ex.Message}); the host starts without it.");
                Host.HandoverLookup = $"not taken from {_handoverPath}: {ex.Message}";
            }
        }

        /// <summary>
        /// What the folder a handover was looked for in holds, in a sentence: so that a host told
        /// there is nothing there can be seen to be looking at the folder the host before it wrote
        /// to, or at one where that host's files are not.
        /// </summary>
        private static string WhatIsBeside(string path)
        {
            string folder = System.IO.Path.GetDirectoryName(path);
            try
            {
                if (!System.IO.Directory.Exists(folder))
                    return $"There is no {folder}.";

                List<string> names = System.IO.Directory.EnumerateFileSystemEntries(folder)
                    .Select(System.IO.Path.GetFileName)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                const int Listed = 12;
                return names.Count == 0
                    ? $"{folder} is empty."
                    : $"{folder} holds {string.Join(", ", names.Take(Listed))}{(names.Count > Listed ? $" and {names.Count - Listed} more" : string.Empty)}.";
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                return $"{folder} could not be listed ({ex.Message}).";
            }
        }

        /// <summary>
        /// Leaves the session for the next host on these ports: once the host has stopped, so the
        /// world and the relay are as they will stay. Nothing is written when there was no session,
        /// and the log says so, with the path the next host will look at.
        /// </summary>
        private void HandOver()
        {
            if (_handoverPath == null || Host == null || !Host.Ended.IsCompleted)
                return;

            try
            {
                HandoverSnapshot snapshot = Host.CreateHandover(DateTimeOffset.UtcNow);
                if (snapshot == null)
                {
                    _log.Info($"Nothing was handed over in {_handoverPath}: "
                        + (Host.SessionJournal.Overflowed
                            ? "the session had outgrown what can be handed over, so the next host starts as if the game had just connected."
                            : "no session was going on."));
                    return;
                }

                snapshot.SetEndpoints(Options.Proxy);
                if (Transport is ProxyTransport relay)
                    snapshot.Relay = relay.SaveState();

                snapshot.Save(_handoverPath);
                _log.Info(ReferenceEquals(snapshot, _taken)
                    ? $"Handed on the session the host before this one handed over at {snapshot.WrittenAt.ToLocalTime():HH:mm:ss}, never taken up because the game was "
                        + $"not heard from, in {_handoverPath} ({snapshot.Journal.Count} message(s)): a host started on the same ports within "
                        + $"{HandoverSnapshot.MaxAge.TotalMinutes:0} minutes of that time carries it on."
                    : $"Handed the session over in {_handoverPath} ({snapshot.Journal.Count} message(s), written at {snapshot.WrittenAt.ToLocalTime():HH:mm:ss}): "
                        + $"a host started on the same ports within {HandoverSnapshot.MaxAge.TotalMinutes:0} minutes carries it on.");
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                _log.Error($"The session could not be handed over in {_handoverPath}; a host started next will not know the character.", ex);
            }
        }

        // ------------------------------------------------------------------- stopping

        /// <summary>
        /// Stops everything, in the order that loses nothing: the control pipe, so nobody drives
        /// a host that is going; the host, which saves every plugin's settings and shuts plugins
        /// down on the game thread and closes the relay; the session, handed over for the next
        /// host; then the overlay's pipe and the client's data. Safe to call twice, and on a
        /// runtime that never started.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_clientLogTimer != null)
                await _clientLogTimer.DisposeAsync().ConfigureAwait(false);

            if (_clientSettingsTimer != null)
                await _clientSettingsTimer.DisposeAsync().ConfigureAwait(false);

            if (Control != null)
                await Control.DisposeAsync().ConfigureAwait(false);

            if (Host != null)
            {
                await Host.DisposeAsync().ConfigureAwait(false);
                HandOver();
            }
            else if (Transport != null && Transport != Options.Transport)
            {
                await Transport.DisposeAsync().ConfigureAwait(false);
            }

            if (_overlay != null)
                await _overlay.DisposeAsync().ConfigureAwait(false);

            Portal?.Dispose();
            Cells?.Dispose();
        }
    }
}
