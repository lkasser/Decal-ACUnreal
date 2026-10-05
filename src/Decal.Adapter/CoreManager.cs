using System;
using Decal.Adapter.Hosting;
using Decal.Adapter.Wrappers;

namespace Decal.Adapter
{
    /// <summary>
    /// Decal's hub: the filters, the actions, and the events every plugin listens to.
    /// </summary>
    /// <remarks>
    /// There is one per <see cref="DecalRuntime"/>, and <see cref="Current"/> is the running
    /// one's - Decal ran one per client, and plugins reach it statically from anywhere, so the
    /// same has to hold here. Events are raised on the host's game thread, one subscriber at a
    /// time, and a subscriber that throws is logged and does not stop the others hearing it.
    /// </remarks>
    public sealed class CoreManager : MarshalByRefObject
    {
        private static CoreManager _current;

        private readonly DecalRuntime _runtime;

        internal CoreManager(DecalRuntime runtime)
        {
            _runtime = runtime;
            WorldFilter = new WorldFilter(runtime);
            CharacterFilter = new CharacterFilter(runtime);
            Actions = new HooksWrapper(runtime);
            HotkeySystem = new HotkeySystem(runtime);
            Decal = new DecalWrapper(runtime);
            RenderService = new RenderServiceWrapper(runtime);
            EchoFilter = new EchoFilter2(runtime);
            D3DService = new D3DService(runtime);
            IDQueue = new IDQueue.FairIDQueue(runtime);
        }

        /// <summary>The running Decal's core, or null when none is running.</summary>
        public static CoreManager Current => _current;

        /// <summary>Whether Decal is running at all.</summary>
        public static bool ServiceRunning => _current != null;

        public static int TracingLevel => 0;

        public WorldFilter WorldFilter { get; }

        public CharacterFilter CharacterFilter { get; }

        public HooksWrapper Actions { get; }

        public HotkeySystem HotkeySystem { get; }

        public DecalWrapper Decal { get; }

        public RenderServiceWrapper RenderService { get; }

        public EchoFilter2 EchoFilter { get; }

        public D3DService D3DService { get; }

        public IDQueue.FairIDQueue IDQueue { get; }

        /// <summary>Decal's FileService filter, when one is loaded; null otherwise.</summary>
        public FilterBase FileService => _runtime.FindFilter("Decal.Filters.FileService");

        internal DecalRuntime Runtime => _runtime;

        internal static void SetCurrent(CoreManager core) => _current = core;

        internal static void ClearCurrent(CoreManager core)
        {
            if (ReferenceEquals(_current, core))
                _current = null;
        }

        /// <summary>A loaded filter by its type's full name, or null.</summary>
        public FilterBase Filter(string filterName) => _runtime.FindFilter(filterName);

        /// <summary>
        /// A loaded filter or service of the given type, or the default for none. Decal also
        /// answered with its own COM filters here; those do not exist, so asking for one
        /// gives null rather than a stand-in that would lie about the game.
        /// </summary>
        public T Filter<T>() => _runtime.FindExtension<T>();

        public ServiceBase Service(string serviceName) => _runtime.FindService(serviceName);

        public T Service<T>() => _runtime.FindExtension<T>();

        /// <summary>The key the client binds a command to. The host cannot read the client's keymap, so 0.</summary>
        public int QueryKeyBoardMap(string name) => 0;

        public void IncrementAssemblyPreloadLockCounter()
        {
        }

        public void DecrementAssemblyPreloadLockCounter()
        {
        }

        public event EventHandler<ItemSelectedEventArgs> ItemSelected;

        public event EventHandler<ItemDestroyedEventArgs> ItemDestroyed;

        public event EventHandler<ChatParserInterceptEventArgs> CommandLineText;

        public event EventHandler<StatusTextInterceptEventArgs> StatusBoxMessage;

        public event EventHandler<RegionChange3DEventArgs> RegionChange3D;

        public event EventHandler<ChatTextInterceptEventArgs> ChatBoxMessage;

        /// <summary>
        /// The client has dealt with a message from the server: raised for each, after every
        /// filter's and plugin's ServerDispatch has heard it, with the same <see cref="Message"/>.
        /// </summary>
        public event EventHandler<MessageProcessedEventArgs> MessageProcessed;

        // Window messages: the host has none to give - the client's window is another
        // process's - so this is never raised, but plugins that subscribe must still load.
#pragma warning disable CS0067
        public event EventHandler<WindowMessageEventArgs> WindowMessage;
#pragma warning restore CS0067

        public event EventHandler<ContainerOpenedEventArgs> ContainerOpened;

        public event EventHandler<ChatClickInterceptEventArgs> ChatNameClicked;

        public event EventHandler<EventArgs> FilterInitComplete;

        public event EventHandler<EventArgs> ServiceInitComplete;

        public event EventHandler<EventArgs> PluginInitComplete;

        public event EventHandler<EventArgs> FilterTermComplete;

        public event EventHandler<EventArgs> ServiceTermComplete;

        public event EventHandler<EventArgs> PluginTermComplete;

        /// <summary>
        /// Raised on every frame the client drew. The host has no frames, so it is raised on
        /// the host's tick instead - roughly ten times a second, which is what plugins that
        /// poll from it actually need, if slower than they were used to.
        /// </summary>
        public event EventHandler<EventArgs> RenderFrame;

        internal void OnItemSelected(ItemSelectedEventArgs e) => _runtime.Raise(ItemSelected, this, e, nameof(ItemSelected));

        internal void OnItemDestroyed(ItemDestroyedEventArgs e) => _runtime.Raise(ItemDestroyed, this, e, nameof(ItemDestroyed));

        internal void OnCommandLineText(ChatParserInterceptEventArgs e) => _runtime.Raise(CommandLineText, this, e, nameof(CommandLineText));

        internal void OnStatusBoxMessage(StatusTextInterceptEventArgs e) => _runtime.Raise(StatusBoxMessage, this, e, nameof(StatusBoxMessage));

        internal void OnChatBoxMessage(ChatTextInterceptEventArgs e) => _runtime.Raise(ChatBoxMessage, this, e, nameof(ChatBoxMessage));

        internal void OnContainerOpened(ContainerOpenedEventArgs e) => _runtime.Raise(ContainerOpened, this, e, nameof(ContainerOpened));

        internal void OnChatNameClicked(ChatClickInterceptEventArgs e) => _runtime.Raise(ChatNameClicked, this, e, nameof(ChatNameClicked));

        internal void OnRegionChange3D(RegionChange3DEventArgs e) => _runtime.Raise(RegionChange3D, this, e, nameof(RegionChange3D));

        internal void OnFilterInitComplete() => _runtime.Raise(FilterInitComplete, this, EventArgs.Empty, nameof(FilterInitComplete));

        internal void OnServiceInitComplete() => _runtime.Raise(ServiceInitComplete, this, EventArgs.Empty, nameof(ServiceInitComplete));

        internal void OnPluginInitComplete() => _runtime.Raise(PluginInitComplete, this, EventArgs.Empty, nameof(PluginInitComplete));

        internal void OnFilterTermComplete() => _runtime.Raise(FilterTermComplete, this, EventArgs.Empty, nameof(FilterTermComplete));

        internal void OnServiceTermComplete() => _runtime.Raise(ServiceTermComplete, this, EventArgs.Empty, nameof(ServiceTermComplete));

        internal void OnPluginTermComplete() => _runtime.Raise(PluginTermComplete, this, EventArgs.Empty, nameof(PluginTermComplete));

        internal void OnRenderFrame() => _runtime.Raise(RenderFrame, this, EventArgs.Empty, nameof(RenderFrame));

        internal bool HasMessageProcessedListeners => MessageProcessed != null;

        internal void OnMessageProcessed(Message message)
            => _runtime.RaiseQuietly(MessageProcessed, this, new MessageProcessedEventArgs(message, 0, message.RawLength), nameof(MessageProcessed), message);

        /// <summary>Lets go of every message handler whose code is in <paramref name="assembly"/>, for a plugin that has been stopped.</summary>
        internal void ReleaseMessageHandlers(System.Reflection.Assembly assembly)
        {
            MessageProcessed = DecalRuntime.Without(MessageProcessed, assembly);
            EchoFilter.Release(assembly);
        }
    }
}
