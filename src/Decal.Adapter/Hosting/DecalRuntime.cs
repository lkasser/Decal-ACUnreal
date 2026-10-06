using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using AC.Host.Decoding;
using AC.Host.Plugins;
using AC.Host.Plugins.Views;
using AC.Host.Transport;
using AC.Host.World;
using AC.Protocol;
using Decal.Adapter.Wrappers;
using HostObject = AC.Host.World.WorldObject;

namespace Decal.Adapter.Hosting
{
    /// <summary>
    /// One running Decal, over one AC host: the <see cref="CoreManager"/> plugins reach
    /// statically, the extensions it has started, and the windows they have opened.
    /// </summary>
    /// <remarks>
    /// This is the seam between the two worlds. Everything a Decal plugin can call lands in
    /// here or in a wrapper holding one of these, and everything the host tells us - objects,
    /// chat, the character, the tick - is turned into the Decal event a plugin was written to
    /// expect. It is public because the host-side loader and Virindi View Service's stand-in
    /// both need it and neither may be a friend of an assembly carrying Decal's strong name;
    /// plugins never had it, and nothing they were compiled against mentions it.
    ///
    /// <para>
    /// Like the host, it is single-threaded: construct it, start extensions, and feed it on
    /// the host's game thread, and every Decal event is raised there too. There is one per
    /// process at a time - Decal ran one per client, and <see cref="CoreManager.Current"/>
    /// is static because plugins treat it so - and constructing a second while the first is
    /// running replaces it as current.
    /// </para>
    /// </remarks>
    public sealed class DecalRuntime : IDisposable
    {
        private static DecalRuntime _current;

        private readonly IHost _host;
        private readonly List<Extension> _extensions = new List<Extension>();
        private readonly Dictionary<Extension, string> _names = new Dictionary<Extension, string>();
        private readonly Dictionary<Assembly, string> _ownersByAssembly = new Dictionary<Assembly, string>();
        private readonly List<HostedView> _views = new List<HostedView>();
        private readonly HashSet<string> _unsupportedNoted = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<uint, HostObject> _known = new Dictionary<uint, HostObject>();
        private readonly Dictionary<uint, ObjectSnapshot> _snapshots = new Dictionary<uint, ObjectSnapshot>();
        private readonly Dictionary<uint, Enchantment> _enchantments = new Dictionary<uint, Enchantment>();
        private readonly uint[] _lastVitals = new uint[3];
        private string _starting;

        /// <summary>The character the host has named whose Login waits for its description; 0 when none does.</summary>
        private uint _entering;
        private uint _loggedInAs;

        /// <summary>Whether the client has said, this login, that it has finished entering the world.</summary>
        private bool _clientEntered;
        private bool _loginCompleteRaised;
        private uint _lastSelection;
        private long _lastTotalXp;
        private long _lastUnassignedXp;
        private readonly HashSet<uint> _knownSpells = new HashSet<uint>();
        private bool _countingChanges;
        private MessageSchema _messages;
        private string _declinedLine;
        private bool _startupCompleted;
        private uint _openedContainer;
        private readonly HashSet<string> _messageFaultsSaid = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<(int Type, int Kind, MessageDirection Direction)> _unfitSaid = new HashSet<(int, int, MessageDirection)>();
        private bool _disposed;

        public DecalRuntime(IHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));

            Core = new CoreManager(this);
            Site = new DecalPluginSite(this);
            ClientWindow = new ClientWindow(this);
            Facing = new Facing(this);

            _host.ObjectCreated += OnObjectCreated;
            _host.ObjectUpdated += OnObjectUpdated;
            _host.ObjectAppraised += OnObjectAppraised;
            _host.ObjectMoved += OnObjectMoved;
            _host.ObjectRemoved += OnObjectRemoved;
            _host.ContainerViewed += OnContainerViewed;
            _host.ContainerClosed += OnContainerClosed;
            _host.ChatReceived += OnChatReceived;
            _host.PlayerIdentified += OnPlayerIdentified;
            _host.LoggedOff += OnLoggedOff;
            _host.LoggingOff += OnLoggingOff;
            _host.CharacterUpdated += OnCharacterUpdated;
            _host.EnchantmentChanged += OnEnchantmentChanged;
            _host.EnchantmentRemoved += OnEnchantmentRemoved;
            _host.UseFinished += OnUseFinished;
            _host.PortalSpaceChanged += OnPortalSpaceChanged;
            _host.Died += OnDied;
            _host.Tick += OnTick;
            _host.MessageSeen += OnMessageSeen;

            // Whatever the host already knows is what Decal's filters would have had by now.
            foreach (HostObject obj in _host.World.Objects)
                Remember(obj);

            _lastSelection = _host.Character.SelectedId;

            _current = this;
            CoreManager.SetCurrent(Core);
        }

        /// <summary>The running Decal, or null when none is.</summary>
        public static DecalRuntime Current => _current;

        public IHost Host => _host;

        public CoreManager Core { get; }

        /// <summary>The window Decal.Hwnd names, and the keys plugins post to it.</summary>
        public ClientWindow ClientWindow { get; }

        /// <summary>
        /// Runs something on the game thread after a delay, so a key can be let go between ticks -
        /// a turn's pulse, which in a tick turns eighteen degrees. Set by whoever runs Decal; without
        /// it keys are let go on the tick.
        /// </summary>
        public Action<TimeSpan, Action> Later { get; set; }

        /// <summary>Decal's FaceHeading, turning the character by the game's keys.</summary>
        internal Facing Facing { get; }

        /// <summary>Every window a Decal plugin has open, in the order they were opened.</summary>
        public IReadOnlyList<HostedView> Views => _views;

        /// <summary>The extensions started so far, in the order they were started.</summary>
        public IReadOnlyList<Extension> Extensions => _extensions;

        /// <summary>
        /// Whether Virindi View Service is to count as running. Plugins that find it use its
        /// windows and the rest use Decal's; both are drawn alike, so this only decides which
        /// of a plugin's two code paths runs. Set before starting plugins, since they look once.
        /// </summary>
        public bool VirindiViewServiceRunning { get; set; } = true;

        /// <summary>
        /// The messages.xml that network messages are read against, for ServerDispatch,
        /// ClientDispatch and MessageProcessed. The built-in copy until something better is set;
        /// whoever starts Decal sets it from <see cref="MessageSchema.Choose"/>, which knows
        /// where Decal is installed.
        /// </summary>
        public MessageSchema Messages
        {
            get => _messages ??= MessageSchema.Shipped;
            set
            {
                _messages = value ?? throw new ArgumentNullException(nameof(value));

                // Decal's Message.GetParser is static, and plugins reached it by reflection.
                Message.Schema = value;
            }
        }

        /// <summary>A window was opened or closed.</summary>
        public event EventHandler ViewsChanged;

        /// <summary>
        /// A plugin's handler threw - for an event, the tick, a hook - and was skipped for that
        /// plugin, as the host skips its own plugins' faults. Says whose code it was, so whoever
        /// loaded that plugin can say which one is in trouble and why.
        /// </summary>
        public event EventHandler<DecalFaultEventArgs> Faulted;

        /// <summary>
        /// The host's tick, relayed once everything Decal does on a tick is done - for Virindi
        /// View Service's stand-in and anything else that needs a clock on the game thread.
        /// </summary>
        public event EventHandler<TimeSpan> Tick;

        /// <summary>
        /// What a plugin is called: its <see cref="FriendlyNameAttribute"/>, or its type's name
        /// when it has none, as Decal's plugin list showed it.
        /// </summary>
        public static string FriendlyNameOf(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            FriendlyNameAttribute friendly = type.GetCustomAttribute<FriendlyNameAttribute>();
            return !string.IsNullOrWhiteSpace(friendly?.Name) ? friendly.Name.Trim() : type.Name;
        }

        public string NameOf(Extension extension)
            => extension != null && _names.TryGetValue(extension, out string name) ? name : null;

        /// <summary>
        /// Starts an extension the way Decal did: the host it talks through, then - for a
        /// plugin - its views and its attribute wiring, then its own Startup.
        /// </summary>
        /// <remarks>
        /// Throws whatever the extension's Startup throws, after undoing what was set up for
        /// it, so a plugin that fails to start leaves no window and no handler behind. The
        /// caller decides whether one failure stops the rest.
        /// </remarks>
        /// <param name="name">
        /// What to call it in the log and its windows' owner - the name Decal's registry lists it
        /// under, say, where every Virindi plugin's class is called PluginCore. Its friendly name
        /// when null.
        /// </param>
        public void Start(Extension extension, string directory, string name = null)
        {
            if (extension == null) throw new ArgumentNullException(nameof(extension));
            if (_disposed) throw new ObjectDisposedException(nameof(DecalRuntime));
            if (extension.Runtime != null) throw new InvalidOperationException($"{extension.GetType().FullName} has already been started.");

            name = UniqueName(string.IsNullOrWhiteSpace(name) ? FriendlyNameOf(extension.GetType()) : name.Trim());

            extension.Path = directory;
            extension.Runtime = this;
            _names[extension] = name;
            _ownersByAssembly.TryAdd(extension.GetType().Assembly, name);

            string previous = _starting;
            _starting = name;
            try
            {
                switch (extension)
                {
                    case PluginBase plugin:
                        plugin.HostInternal = new PluginHost(this, plugin);
                        AttributeWiring.LoadViews(plugin);
                        AttributeWiring.WireBaseEvents(plugin, this);
                        AttributeWiring.WireControls(plugin);
                        break;

                    case FilterBase filter:
                        filter.HostInternal = new NetServiceHost(this);
                        break;
                }

                extension.InvokeStartup();
                _extensions.Add(extension);
            }
            catch
            {
                Forget(extension);
                throw;
            }
            finally
            {
                _starting = previous;
            }
        }

        /// <summary>
        /// Tells everything started that everything has been: the init-complete events Decal
        /// raised once all its extensions were up, and - when the character is already in the
        /// world - the login events a plugin started before login would have seen.
        /// </summary>
        public void CompleteStartup()
        {
            _startupCompleted = true;
            Core.OnFilterInitComplete();
            Core.OnServiceInitComplete();

            foreach (ServiceBase service in _extensions.OfType<ServiceBase>().ToList())
                Guard(NameOf(service), () => service.InvokeAfterPlugins(), service.GetType().Assembly);

            Core.OnPluginInitComplete();

            // A character already in the world logged in before Decal was here to hear it: its
            // description and the client's word that it had arrived have gone by, so both events
            // come at once. One whose login Decal has seen begin goes on as any login does.
            if (_host.Character.Id != 0 && _entering == 0 && _loggedInAs == 0)
            {
                _clientEntered = true;
                RaiseLogin(_host.Character.Id);
            }
        }

        /// <summary>
        /// Tells extensions started after <see cref="CompleteStartup"/> - a plugin switched on, or
        /// reloaded, while the host runs - what the others were told then: the init-complete
        /// events, and with the character in the world its Login, and its LoginComplete if the
        /// others have had theirs. Raised to their own handlers alone; nothing before
        /// <see cref="CompleteStartup"/>, which tells them itself.
        /// </summary>
        /// <remarks>
        /// Decal loaded its plugins as the client started, so every plugin heard all of these;
        /// one that missed them waits for ever for what it set up its windows and its login
        /// work on. Mag-Tools makes its window on PluginInitComplete and starts its timers,
        /// hotkeys and status rows on LoginComplete. A handler is the extensions' when its code
        /// is in their assembly - or, for a plugin in a load context of its own, in that context.
        /// </remarks>
        public void CatchUp(IReadOnlyCollection<Extension> started)
        {
            if (!_startupCompleted || started == null || started.Count == 0)
                return;

            HashSet<Assembly> assemblies = new HashSet<Assembly>(started.Select(e => e.GetType().Assembly));
            HashSet<AssemblyLoadContext> contexts = new HashSet<AssemblyLoadContext>(
                assemblies.Select(AssemblyLoadContext.GetLoadContext).Where(c => c != null && c != AssemblyLoadContext.Default));
            bool Theirs(Assembly assembly)
                => assembly != null && (assemblies.Contains(assembly) || contexts.Contains(AssemblyLoadContext.GetLoadContext(assembly)));

            Core.CatchUpInitComplete(Theirs, started.OfType<ServiceBase>().ToList());

            if (_loggedInAs != 0)
                Core.CharacterFilter.CatchUpLogin(Theirs, unchecked((int)_loggedInAs), _loginCompleteRaised);
        }

        /// <summary>The subscribers of an event whose code <paramref name="theirs"/> picks out; null when there are none.</summary>
        internal static TDelegate Only<TDelegate>(TDelegate handler, Func<Assembly, bool> theirs) where TDelegate : Delegate
        {
            if (handler == null)
                return null;

            Delegate kept = null;
            foreach (Delegate subscriber in handler.GetInvocationList())
            {
                if (theirs(subscriber.Method.Module.Assembly) || theirs(subscriber.Target?.GetType().Assembly))
                    kept = Delegate.Combine(kept, subscriber);
            }

            return (TDelegate)kept;
        }

        /// <summary>Stops one extension: its Shutdown, then its windows.</summary>
        public void Stop(Extension extension)
        {
            if (extension == null || !_extensions.Remove(extension))
                return;

            try
            {
                extension.InvokeShutdown();
            }
            finally
            {
                Forget(extension);
            }
        }

        /// <summary>
        /// Offers a line the player typed to every plugin, as Decal did before the client saw
        /// it. Returns true when one of them ate it - recognised it as its own command.
        /// </summary>
        public bool InvokeCommandLine(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            ChatParserInterceptEventArgs args = new ChatParserInterceptEventArgs(text);
            Core.OnCommandLineText(args);
            return args.Eat;
        }

        /// <summary>
        /// The host plugin this Decal runs inside - Decal Compat - so that a line a Decal plugin
        /// hands on to the game is not offered back to it as a host plugin's command. Null when
        /// there is none.
        /// </summary>
        public IPlugin Owner { get; set; }

        /// <summary>
        /// Offers a line to the plugins as Decal.dll's DispatchOnChatCommand did, remembering one
        /// that none of them took, so that the InvokeChatParser a plugin follows it with does not
        /// offer the plugins the same line twice.
        /// </summary>
        internal bool DispatchCommandLine(string text)
        {
            bool eaten = InvokeCommandLine(text);
            _declinedLine = eaten ? null : text;
            return eaten;
        }

        /// <summary>Whether this very line was just offered to the plugins and none took it; forgotten either way.</summary>
        internal bool TakeDeclined(string text)
        {
            bool declined = _declinedLine != null && string.Equals(_declinedLine, text, StringComparison.Ordinal);
            _declinedLine = null;
            return declined;
        }

        /// <summary>
        /// Runs a line no Decal plugin took as the game's chat box would have: the host's other
        /// plugins' commands, the game client's own commands the server carries out, and plain
        /// speech (<see cref="IHost.RunChatCommand"/>). What could not be sent is said once.
        /// </summary>
        internal void RunGameCommand(string text)
        {
            ChatCommandOutcome outcome = _host.RunChatCommand(text, Owner);
            switch (outcome)
            {
                case ChatCommandOutcome.ClientOnly:
                    NoteUnsupported("InvokeChatParser for the game client's own windows and settings", text);
                    break;

                case ChatCommandOutcome.NotSent:
                    _host.Log.Info($"[Decal] {OwnerOfCaller()} ran \"{text}\", which was not sent: plugins may not act now, or it needs what is not known yet.");
                    break;

                case ChatCommandOutcome.Unknown:
                    _host.Log.Info($"[Decal] {OwnerOfCaller()} ran \"{text}\", which names no command.");
                    break;
            }
        }

        /// <summary>
        /// Puts a plugin's line of text where the player can see it: the game's chat window
        /// when the host can reach it, and the host's log regardless.
        /// </summary>
        /// <remarks>
        /// As Decal's AddChatText put it: the text as the plugin wrote it - its own "[VGI] " or
        /// "[VI] " and nothing of the host's - and <paramref name="color"/> as the client's chat
        /// type, which is all Decal's colour was; the client drew the line in that type's colour,
        /// in the windows the player gave that type. Decal's target window - 0 wherever the
        /// player sends that type, 1 the main chat, 2 to 5 the four floating chat windows - is not
        /// passed on: the line reaches the client as a message from the server, which names a
        /// chat type and no window, so it always goes where target 0 sent it. A closing newline
        /// is dropped, since each call is a line of its own here.
        /// </remarks>
        public void ShowChat(string text, int color)
        {
            if (string.IsNullOrEmpty(text))
                return;

            string line = text.TrimEnd('\r', '\n');
            _host.Log.Info("[Decal] " + line);
            _host.ShowInGame(line, color);

            // Raised from inside the plugin's own call, so a listener can tell whose it was.
            try
            {
                ChatShown?.Invoke(this, line);
            }
            catch (Exception ex)
            {
                _host.Log.Error("[Decal] A listener to plugins' chat failed.", ex);
            }
        }

        /// <summary>
        /// A plugin put a line in the chat window - raised on the plugin's own call, so whoever
        /// listens can look at the stack to tell whose it is. Plugins that catch their own
        /// exceptions report them this way, and it is often the only word of what went wrong.
        /// </summary>
        public event EventHandler<string> ChatShown;

        /// <summary>
        /// Registers a parsed view as a window. Called by Decal's own view loading and by the
        /// Virindi View Service stand-in; the owner is the plugin that asked, or, when the
        /// caller cannot say, whichever plugin's code is on the stack.
        /// </summary>
        public HostedView AddView(DecalView view, string owner = null)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));

            owner ??= OwnerOfCaller();

            // The first key not in use, so a plugin that closes and reopens its window - or is
            // reloaded - gets the same key back, and the overlay puts the window where it was.
            string key = owner;
            for (int n = 2; _views.Any(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)); n++)
                key = $"{owner} {n}";

            HostedView hosted = new HostedView(owner, key, view);
            _views.Add(hosted);

            foreach (string warning in view.Warnings)
                _host.Log.Warn($"[Decal] {owner}'s view '{view.Title}': {warning}");

            ViewsChanged?.Invoke(this, EventArgs.Empty);
            return hosted;
        }

        public void RemoveView(HostedView view)
        {
            if (view == null || !_views.Remove(view))
                return;

            view.IsOpen = false;
            ViewsChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// The friendly name of the plugin whose code is calling, found by walking the stack
        /// for the first frame in an assembly a started plugin came from.
        /// </summary>
        /// <remarks>
        /// Needed because Virindi View Service's HudView is made with nothing but XML - it never
        /// learns which plugin made it - and the window still has to be labelled as that
        /// plugin's. Only done when a window is created, which is rare enough for a stack walk.
        /// </remarks>
        public string OwnerOfCaller()
        {
            foreach (StackFrame frame in new StackTrace(false).GetFrames())
            {
                Assembly assembly = frame.GetMethod()?.DeclaringType?.Assembly;
                if (assembly != null && _ownersByAssembly.TryGetValue(assembly, out string owner))
                    return owner;
            }

            return _starting ?? "Decal";
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            for (int i = _extensions.Count - 1; i >= 0; i--)
            {
                Extension extension = _extensions[i];
                Guard(NameOf(extension), () => Stop(extension), extension.GetType().Assembly);
            }

            Core.OnPluginTermComplete();
            Core.OnServiceTermComplete();
            Core.OnFilterTermComplete();

            _host.ObjectCreated -= OnObjectCreated;
            _host.ObjectUpdated -= OnObjectUpdated;
            _host.ObjectAppraised -= OnObjectAppraised;
            _host.ObjectMoved -= OnObjectMoved;
            _host.ObjectRemoved -= OnObjectRemoved;
            _host.ContainerViewed -= OnContainerViewed;
            _host.ContainerClosed -= OnContainerClosed;
            _host.ChatReceived -= OnChatReceived;
            _host.PlayerIdentified -= OnPlayerIdentified;
            _host.LoggedOff -= OnLoggedOff;
            _host.LoggingOff -= OnLoggingOff;
            _host.CharacterUpdated -= OnCharacterUpdated;
            _host.EnchantmentChanged -= OnEnchantmentChanged;
            _host.EnchantmentRemoved -= OnEnchantmentRemoved;
            _host.UseFinished -= OnUseFinished;
            _host.PortalSpaceChanged -= OnPortalSpaceChanged;
            _host.Died -= OnDied;
            _host.Tick -= OnTick;
            _host.MessageSeen -= OnMessageSeen;

            foreach (HostedView view in _views)
                view.IsOpen = false;

            _views.Clear();
            Facing.Stop();
            ClientWindow.Dispose();
            _disposed = true;

            if (ReferenceEquals(_current, this))
                _current = null;

            CoreManager.ClearCurrent(Core);
        }

        // ------------------------------------------------------------------- for the shim

        internal IPluginLog Log => _host.Log;

        /// <summary>What PluginHost.Underlying gives plugins: the plugin site, for its hooks.</summary>
        internal DecalPluginSite Site { get; }

        /// <summary>
        /// Raises a Decal event one subscriber at a time, so one plugin's fault costs that
        /// plugin its event and not everyone else theirs - the rule the host itself follows.
        /// </summary>
        internal void Raise<T>(EventHandler<T> handler, object sender, T args, string what)
        {
            if (handler == null)
                return;

            foreach (Delegate subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((EventHandler<T>)subscriber)(sender, args);
                }
                catch (Exception ex)
                {
                    _host.Log.Error($"[Decal] {DescribeSubscriber(subscriber)} threw handling {what}.", ex);
                    OnFaulted(subscriber.Method.DeclaringType?.Assembly, what, ex);
                }
            }
        }

        internal void Raise(EventHandler handler, object sender, string what)
        {
            if (handler == null)
                return;

            foreach (Delegate subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((EventHandler)subscriber)(sender, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    _host.Log.Error($"[Decal] {DescribeSubscriber(subscriber)} threw handling {what}.", ex);
                    OnFaulted(subscriber.Method.DeclaringType?.Assembly, what, ex);
                }
            }
        }

        /// <summary>A network message to a ServerDispatch or ClientDispatch; see <see cref="RaiseQuietly"/>.</summary>
        internal void RaiseMessage(EventHandler<NetworkMessageEventArgs> handler, object sender, Message message, string what)
        {
            if (handler != null)
                RaiseQuietly(handler, sender, new NetworkMessageEventArgs(message), what, message);
        }

        /// <summary>
        /// Raises an event that comes with every network message, one subscriber at a time as
        /// <see cref="Raise{T}"/> does, but says a fault only the first time that handler throws
        /// that exception: a handler that cannot cope with a kind of message throws on every one
        /// of them, many a second, and Decal said nothing at all.
        /// </summary>
        internal void RaiseQuietly<T>(EventHandler<T> handler, object sender, T args, string what, Message message)
        {
            if (handler == null)
                return;

            foreach (Delegate subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((EventHandler<T>)subscriber)(sender, args);
                }
                catch (Exception ex)
                {
                    string who = DescribeSubscriber(subscriber);
                    if (!_messageFaultsSaid.Add(who + "|" + what + "|" + ex.GetType().FullName))
                        continue;

                    _host.Log.Error($"[Decal] {who} threw handling {what} for message {Describe(message)}; the same fault again is not logged.", ex);
                    OnFaulted(subscriber.Method.DeclaringType?.Assembly, what, ex);
                }
            }
        }

        /// <summary>
        /// <paramref name="handler"/> without the subscribers whose code is in <paramref name="assembly"/>,
        /// so a stopped plugin that never unsubscribed hears no more messages and can unload.
        /// </summary>
        internal static EventHandler<T> Without<T>(EventHandler<T> handler, Assembly assembly)
        {
            if (handler == null || assembly == null)
                return handler;

            EventHandler<T> kept = null;
            foreach (Delegate subscriber in handler.GetInvocationList())
            {
                if (subscriber.Method.Module.Assembly != assembly && subscriber.Target?.GetType().Assembly != assembly)
                    kept += (EventHandler<T>)subscriber;
            }

            return kept;
        }

        /// <summary>
        /// Says once, per kind of thing, that a plugin asked for something the host cannot do.
        /// Once, because plugins ask for the same thing on every tick, and a log that repeats
        /// itself is a log nobody reads.
        /// </summary>
        public void NoteUnsupported(string what, string detail = null)
        {
            if (_unsupportedNoted.Add(what))
                _host.Log.Info($"[Decal] {OwnerOfCaller()} used {what}{(detail != null ? " (" + detail + ")" : string.Empty)}, which this host does not provide; it does nothing.");
        }

        /// <summary>
        /// Says something about what a plugin asked for once, by its kind, in the log: for what the
        /// host does otherwise than Decal did rather than not at all. The plugin is named first.
        /// </summary>
        internal void SayOnce(string kind, string text)
        {
            if (_unsupportedNoted.Add("say:" + kind))
                _host.Log.Info($"[Decal] {OwnerOfCaller()} {text}");
        }

        internal FilterBase FindFilter(string name)
        {
            foreach (Extension extension in _extensions)
            {
                if (extension is FilterBase filter && Matches(extension, name))
                    return filter;
            }

            return null;
        }

        internal ServiceBase FindService(string name)
        {
            foreach (Extension extension in _extensions)
            {
                if (extension is ServiceBase service && Matches(extension, name))
                    return service;
            }

            return null;
        }

        internal T FindExtension<T>()
        {
            foreach (Extension extension in _extensions)
            {
                if (extension is T typed)
                    return typed;
            }

            return default;
        }

        /// <summary>The host's object with this id, or the last one known by it if it has just gone.</summary>
        internal HostObject Lookup(uint id)
        {
            if (_host.World.TryGet(id, out HostObject live))
                return live;

            return _known.TryGetValue(id, out HostObject gone) ? gone : null;
        }

        internal Enchantment LookupEnchantment(uint packedId)
            => _enchantments.TryGetValue(packedId, out Enchantment enchantment) ? enchantment : null;

        /// <param name="owner">The assembly whose code <paramref name="action"/> runs, when known, for <see cref="Faulted"/>.</param>
        internal void Guard(string who, Action action, Assembly owner = null)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _host.Log.Error($"[Decal] {who ?? "A Decal extension"} threw.", ex);
                OnFaulted(owner, who, ex);
            }
        }

        private void OnFaulted(Assembly assembly, string what, Exception exception)
        {
            EventHandler<DecalFaultEventArgs> faulted = Faulted;
            if (faulted == null)
                return;

            try
            {
                faulted(this, new DecalFaultEventArgs(assembly, what, exception));
            }
            catch (Exception ex)
            {
                _host.Log.Error("[Decal] Reporting a plugin's fault failed.", ex);
            }
        }

        // ------------------------------------------------------------------- host events

        private void OnObjectCreated(object sender, HostObject obj)
        {
            Remember(obj);

            // The character's own object, told of after the host named the character - a session
            // carried on, say - may be what Login waits for. Raised before the world filter has it,
            // as at a login, whose description came before the character's object.
            if (obj.Id == _entering)
                CheckLogin();

            Core.WorldFilter.OnCreateObject(obj);
        }

        private void OnObjectUpdated(object sender, HostObject obj)
        {
            if (obj.Id == _host.Character.Id)
                CheckCharacterChanges();

            _known[obj.Id] = obj;
            ObjectSnapshot now = ObjectSnapshot.Of(obj);

            if (!_snapshots.TryGetValue(obj.Id, out ObjectSnapshot before))
                before = now;

            _snapshots[obj.Id] = now;

            // Decal said what kind of change it was, and plugins filter on it - an inventory
            // sorter wants storage changes and nothing else. The host only says "changed", so
            // the kind is worked out from what did; a change Decal had no word for is not raised.
            if (now.Container != before.Container || now.Wielder != before.Wielder || now.Slot != before.Slot)
                Core.WorldFilter.OnChangeObject(obj, WorldChangeType.StorageChange);
            else if (now.Stack != before.Stack)
                Core.WorldFilter.OnChangeObject(obj, WorldChangeType.SizeChange);
            else if (now.Mana != before.Mana)
                Core.WorldFilter.OnChangeObject(obj, WorldChangeType.ManaChange);
        }

        private void OnObjectAppraised(object sender, HostObject obj)
        {
            Remember(obj);
            Core.WorldFilter.OnChangeObject(obj, WorldChangeType.IdentReceived);
            Core.IDQueue.OnIdentified(obj.Id);
        }

        private void OnObjectMoved(object sender, HostObject obj)
        {
            _known[obj.Id] = obj;
            Core.WorldFilter.OnMoveObject(obj);
        }

        private void OnObjectRemoved(object sender, uint id)
        {
            if (_known.TryGetValue(id, out HostObject gone))
                Core.WorldFilter.OnReleaseObject(gone);

            _known.Remove(id);
            _snapshots.Remove(id);
        }

        private void OnContainerViewed(object sender, ContainerContents contents)
        {
            // A chest or a corpse; not one of the character's own packs, which the server lists too.
            bool carried = _host.World.TryGet(contents.ContainerId, out HostObject container)
                           && (container.ContainerId.HasValue || container.WielderId.HasValue || container.Id == _host.Character.Id);
            if (!carried)
                _openedContainer = contents.ContainerId;

            Core.OnContainerOpened(new ContainerOpenedEventArgs(unchecked((int)contents.ContainerId)));
        }

        private void OnContainerClosed(object sender, uint id)
        {
            if (id == _openedContainer)
                _openedContainer = 0;
        }

        /// <summary>
        /// The container on the ground whose contents the server last listed - a corpse, a chest -
        /// until it closes: Decal's Hooks.OpenedContainer, which Mag-Tools' "/mt loot" looks in.
        /// </summary>
        internal uint OpenedContainer => _openedContainer;

        private void OnChatReceived(object sender, ChatMessage message)
        {
            string text = ChatText.Format(message, _host.Character?.Id ?? 0, ChatLines.PlayerLookup(_host.World), _host.Character?.Name);
            if (text == null)
                return;

            Core.OnChatBoxMessage(new ChatTextInterceptEventArgs(text, ChatText.Color(message), 0));
        }

        /// <summary>
        /// The host has named the character: a login has begun. Decal's Login waits for the
        /// character's description, which is where Decal raised it (<see cref="CheckLogin"/>).
        /// </summary>
        private void OnPlayerIdentified(object sender, uint id)
        {
            if (id == 0 || id == _loggedInAs || id == _entering)
                return;

            _entering = id;
            _loggedInAs = 0;
            _clientEntered = false;
            _loginCompleteRaised = false;
            _countingChanges = false;
            CheckLogin();
        }

        /// <summary>
        /// The character left the world: Decal's Logoff, while the filters still describe it. The
        /// next character in - the same one again, as often as not - is a new login, with its own
        /// Login and LoginComplete.
        /// </summary>
        private void OnLoggedOff(object sender, string reason)
        {
            _entering = 0;
            _clientEntered = false;
            if (_loggedInAs == 0)
                return;

            _loggedInAs = 0;
            _loginCompleteRaised = false;
            _countingChanges = false;
            _openedContainer = 0;
            Array.Clear(_lastVitals);
            _enchantments.Clear();
            Facing.Stop();
            Core.CharacterFilter.OnLogoff(LogoffEventType.Authorized);
        }

        /// <summary>
        /// The client asked to log the character off - the player's Log Out, or Hooks.Logout - and
        /// the server has yet to agree: Decal's Logoff with Requested, before the Authorized that
        /// <see cref="OnLoggedOff"/> raises once it has. Virindi Tank stopped its macro on this one.
        /// </summary>
        private void OnLoggingOff(object sender, EventArgs e)
        {
            if (_loggedInAs == 0)
                return;

            Core.CharacterFilter.OnLogoff(LogoffEventType.Requested);
        }

        private void OnCharacterUpdated(object sender, EventArgs e)
        {
            uint selected = _host.Character.SelectedId;
            if (selected != _lastSelection)
            {
                _lastSelection = selected;
                Core.OnItemSelected(new ItemSelectedEventArgs(unchecked((int)selected)));
            }

            // Vitals are the one character change plugins listen to by kind; the rest they
            // read when they need it.
            for (int i = 0; i < _lastVitals.Length; i++)
            {
                uint vitalId = (uint)(1 + i * 2);
                uint current = _host.Character.Vitals.TryGetValue(vitalId, out VitalState vital) ? vital.Current : 0;
                if (current == _lastVitals[i])
                    continue;

                _lastVitals[i] = current;
                Core.CharacterFilter.OnChangeVital((CharFilterVitalType)(vitalId + 1), unchecked((int)current));
            }

            CheckCharacterChanges();
            CheckLogin();
            CheckLoginComplete();
        }

        private void OnPortalSpaceChanged(object sender, bool entering)
        {
            if (entering)
            {
                _openedContainer = 0;
                Facing.Stop();
            }

            Core.CharacterFilter.OnChangePortalMode(entering ? PortalEventType.EnterPortal : PortalEventType.ExitPortal);
        }

        private void OnDied(object sender, string message) => Core.CharacterFilter.OnDeath(message);

        /// <summary>
        /// Decal's ChangeExperience and SpellbookChange: the total and the unspent experience each
        /// time either changes, with how much it did - the host has nothing else to say of it - and
        /// each spell learned or forgotten. Counted from what the character had once it was in the
        /// world and its own description had come: what the login itself says is not a change.
        /// </summary>
        private void CheckCharacterChanges()
        {
            if (!_loginCompleteRaised || !_countingChanges)
            {
                TakeCharacterBaseline();
                _countingChanges = _loginCompleteRaised && Described;
                return;
            }

            long total = Core.CharacterFilter.TotalXP;
            if (total != _lastTotalXp)
            {
                long change = total - _lastTotalXp;
                _lastTotalXp = total;
                Core.CharacterFilter.OnChangeExperience(PlayerXPEventType.Total, (int)Math.Clamp(change, int.MinValue, int.MaxValue));
            }

            long unassigned = Core.CharacterFilter.UnassignedXP;
            if (unassigned != _lastUnassignedXp)
            {
                long change = unassigned - _lastUnassignedXp;
                _lastUnassignedXp = unassigned;
                Core.CharacterFilter.OnChangeExperience(PlayerXPEventType.Unassigned, (int)Math.Clamp(change, int.MinValue, int.MaxValue));
            }

            IReadOnlyCollection<uint> spellbook = _host.Character.Spellbook;
            List<uint> learned = spellbook.Where(id => !_knownSpells.Contains(id)).OrderBy(id => id).ToList();
            List<uint> forgotten = _knownSpells.Where(id => !spellbook.Contains(id)).OrderBy(id => id).ToList();
            foreach (uint spell in learned)
            {
                _knownSpells.Add(spell);
                Core.CharacterFilter.OnSpellbookChange(AddRemoveEventType.Add, unchecked((int)spell));
            }

            foreach (uint spell in forgotten)
            {
                _knownSpells.Remove(spell);
                Core.CharacterFilter.OnSpellbookChange(AddRemoveEventType.Delete, unchecked((int)spell));
            }
        }

        /// <summary>Whether the character's own description has come: it always brings the attributes.</summary>
        private bool Described => _host.Character.Attributes.Count > 0;

        private void TakeCharacterBaseline()
        {
            _lastTotalXp = Core.CharacterFilter.TotalXP;
            _lastUnassignedXp = Core.CharacterFilter.UnassignedXP;
            _knownSpells.Clear();
            _knownSpells.UnionWith(_host.Character.Spellbook);
        }

        private void OnEnchantmentChanged(object sender, Enchantment enchantment)
        {
            _enchantments[enchantment.PackedId] = enchantment;
            Core.CharacterFilter.OnChangeEnchantments(AddRemoveEventType.Add, enchantment);
        }

        private void OnEnchantmentRemoved(object sender, uint packedId)
        {
            if (_enchantments.Remove(packedId, out Enchantment gone))
                Core.CharacterFilter.OnChangeEnchantments(AddRemoveEventType.Delete, gone);
        }

        private void OnUseFinished(object sender, uint error) => Core.CharacterFilter.OnActionComplete();

        /// <summary>
        /// A game message went by: to every filter's ServerDispatch or ClientDispatch, then
        /// every plugin's through the echo filter, then - for the server's - MessageProcessed,
        /// as the client would have finished with it last.
        /// </summary>
        /// <remarks>
        /// Every message comes through here, so nothing is made unless someone listens, and
        /// then one <see cref="Message"/> for all of them, read only when one asks. The client's
        /// word that it has finished entering the world is Decal's LoginComplete, raised once the
        /// word itself has gone by.
        /// </remarks>
        private void OnMessageSeen(object sender, GameMessageEventArgs e)
        {
            Dispatch(e);

            if (e.Direction == PacketDirection.Outbound
                && MessageDecoder.TryReadActionType(e.Message, out uint action) && action == GameActions.LoginComplete)
            {
                OnClientEntered();
            }

            // The character the client's own character select chose, for a plugin that clicks the
            // old client's Enter without choosing one: its CharacterEnterWorld names it first.
            if (e.Direction == PacketDirection.Outbound && e.Message.Opcode == Opcodes.CharacterEnterWorld && e.Message.Payload.Length >= 4)
                ClientWindow.NoteEntering(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(e.Message.Payload.Span));
        }

        private void Dispatch(GameMessageEventArgs e)
        {
            MessageDirection direction = e.Direction == PacketDirection.Inbound ? MessageDirection.Inbound : MessageDirection.Outbound;
            bool processed = direction == MessageDirection.Inbound && Core.HasMessageProcessedListeners;
            bool echo = Core.EchoFilter.HasListeners(direction);
            bool filters = false;
            foreach (Extension extension in _extensions)
                filters |= extension is FilterBase filter && filter.HasListeners(direction);

            if (!processed && !echo && !filters)
                return;

            Message message = Messages.Parse(e.Message.Opcode, e.Message.Payload.Span, direction);
            message.ReportUnfitTo(OnMessageUnfit);

            if (filters)
            {
                foreach (FilterBase filter in _extensions.OfType<FilterBase>().ToList())
                    filter.Dispatch(message, direction);
            }

            if (echo)
                Core.EchoFilter.Dispatch(message, direction);

            if (processed)
                Core.OnMessageProcessed(message);
        }

        /// <summary>
        /// A message turned out not to fit messages.xml - the server sent something the schema
        /// does not describe, or describes differently. Said once per kind of message: plugins
        /// read what did fit, and the rest reads as absent.
        /// </summary>
        private void OnMessageUnfit(Message message, Exception problem)
        {
            if (!_unfitSaid.Add((message.Type, KindOf(message), message.Direction)))
                return;

            _host.Log.Warn($"[Decal] Message {Describe(message)} does not fit {Messages.Source} messages.xml (revision {Messages.Revision}): {problem.Message} Plugins reading it get the {message.Count} field(s) before that, and nothing after.");
        }

        /// <summary>The event of a game event or the action of a game action; 0 for any other message.</summary>
        private static int KindOf(Message message)
            => message.Type == 0xF7B0 ? message.Value<int>("event") : message.Type == 0xF7B1 ? message.Value<int>("action") : 0;

        private static string Describe(Message message)
        {
            if (message == null)
                return "?";

            int kind = KindOf(message);
            string what = message.Type == 0xF7B0 ? $" (event 0x{kind:X4})" : message.Type == 0xF7B1 ? $" (action 0x{kind:X4})" : string.Empty;
            return $"0x{message.Type:X4}{what}";
        }

        private void OnTick(object sender, TimeSpan elapsed)
        {
            CheckLogin();
            CheckLoginComplete();
            ClientWindow.Tick();
            Facing.Tick(elapsed);
            Core.OnRenderFrame();
            Site.RaiseRenderPreUI();
            Core.IDQueue.Pump(elapsed);
            Core.HotkeySystem.Poll();

            EventHandler<TimeSpan> tick = Tick;
            if (tick == null)
                return;

            foreach (Delegate subscriber in tick.GetInvocationList())
            {
                try
                {
                    ((EventHandler<TimeSpan>)subscriber)(this, elapsed);
                }
                catch (Exception ex)
                {
                    _host.Log.Error($"[Decal] {DescribeSubscriber(subscriber)} threw on the tick.", ex);
                    OnFaulted(subscriber.Method.DeclaringType?.Assembly, "the tick", ex);
                }
            }
        }

        // ------------------------------------------------------------------- helpers

        /// <summary>
        /// Decal's Login, once the character's description has filled the character filter: its
        /// attributes and its own object, which holds the rest - name, level, experience, burden.
        /// </summary>
        /// <remarks>
        /// Decal's character filter (DecalFilters.dll's CharacterStats) raised Login as it finished
        /// reading the character's description, game event 0x0013 - so a plugin's Login handler
        /// found the filter filled - and before the plugins' own ServerDispatch and
        /// MessageProcessed had the description. Here the description fills the host's world,
        /// which says so before plugins have the message, so Login comes at the same point. ACE
        /// names the character in that very message, so the host's word that it has named it
        /// comes a moment earlier, with nothing filled yet. A session carried on names a character
        /// already described and tells of its object straight after, so Login comes with that.
        /// </remarks>
        private void CheckLogin()
        {
            if (_entering == 0 || !Described || _host.Character.Object == null)
                return;

            RaiseLogin(_entering);
        }

        private void RaiseLogin(uint id)
        {
            _entering = 0;
            _loggedInAs = id;
            _loginCompleteRaised = false;
            _countingChanges = false;
            Core.CharacterFilter.OnLogin(unchecked((int)id));
            CheckLoginComplete();
        }

        /// <summary>
        /// The client has said it has finished entering the world - its LoginComplete action,
        /// 0x00A1, which it sends at every login and after every portal. A login whose
        /// description never came has its Login now, so that no plugin hears LoginComplete
        /// without it.
        /// </summary>
        private void OnClientEntered()
        {
            if (_entering == 0 && _loggedInAs == 0)
                return;

            _clientEntered = true;
            if (_entering != 0)
                RaiseLogin(_entering);
            else
                CheckLoginComplete();
        }

        /// <summary>
        /// Decal's LoginComplete: the first time the client says it has finished entering the
        /// world after a login, once the character's own object is known.
        /// </summary>
        /// <remarks>
        /// Decal's character filter raised it on the client's LoginComplete action and on nothing
        /// else, once a login. By then the login's every message had been through every plugin -
        /// the description first of all - so what a plugin kept of the character for itself from
        /// MessageProcessed was there to count from: Virindi Reporter's luminance, taken at
        /// LoginComplete as where this session's earnings start.
        /// </remarks>
        private void CheckLoginComplete()
        {
            if (_loggedInAs == 0 || _loginCompleteRaised || !_clientEntered || _host.Character.Object == null)
                return;

            // What the character has as it arrives is where Decal's change events count from - or,
            // where its description is still to come, what that description says.
            TakeCharacterBaseline();
            _loginCompleteRaised = true;
            _countingChanges = Described;
            Core.CharacterFilter.OnLoginComplete();
        }

        private void Remember(HostObject obj)
        {
            _known[obj.Id] = obj;
            _snapshots[obj.Id] = ObjectSnapshot.Of(obj);
        }

        private void Forget(Extension extension)
        {
            if (extension is PluginBase plugin)
            {
                plugin.DisposeViews();
                plugin.HostInternal?.Dispose();
            }

            // Anything else it opened - through Virindi View Service, say - goes with it.
            string name = NameOf(extension);
            if (name != null)
            {
                foreach (HostedView view in _views.Where(v => v.Owner == name).ToList())
                    RemoveView(view);
            }

            _names.Remove(extension);
            extension.Runtime = null;

            // Holding on to its assembly would keep a plugin switched off from ever unloading.
            Assembly assembly = extension.GetType().Assembly;
            if (!_names.Keys.Any(e => e.GetType().Assembly == assembly))
            {
                _ownersByAssembly.Remove(assembly);

                // Nor may it go on hearing every message: a plugin that never unsubscribed its
                // ServerDispatch would otherwise run on, switched off, for every one.
                Core.ReleaseMessageHandlers(assembly);
            }
        }

        private string UniqueName(string name)
        {
            string unique = name;
            for (int n = 2; _names.ContainsValue(unique); n++)
                unique = $"{name} ({n})";

            return unique;
        }

        private static bool Matches(Extension extension, string name)
            => name != null
            && (string.Equals(extension.GetType().FullName, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension.GetType().Name, name, StringComparison.OrdinalIgnoreCase));

        private string DescribeSubscriber(Delegate subscriber)
        {
            Assembly assembly = subscriber.Method.DeclaringType?.Assembly;
            string owner = assembly != null && _ownersByAssembly.TryGetValue(assembly, out string name) ? name : null;
            string method = subscriber.Method.DeclaringType?.Name + "." + subscriber.Method.Name;
            return owner != null ? $"{owner} ({method})" : method;
        }

        /// <summary>The parts of an object whose changes Decal told apart.</summary>
        private readonly struct ObjectSnapshot
        {
            private ObjectSnapshot(uint container, uint wielder, uint slot, int stack, int mana)
            {
                Container = container;
                Wielder = wielder;
                Slot = slot;
                Stack = stack;
                Mana = mana;
            }

            public uint Container { get; }

            public uint Wielder { get; }

            public uint Slot { get; }

            public int Stack { get; }

            public int Mana { get; }

            public static ObjectSnapshot Of(HostObject obj)
                => new ObjectSnapshot(
                    obj.ContainerId ?? 0,
                    obj.WielderId ?? 0,
                    obj.CurrentlyWieldedLocation ?? 0,
                    obj.StackSize ?? 0,
                    obj.Ints.TryGetValue(107, out int mana) ? mana : 0);
        }
    }
}
