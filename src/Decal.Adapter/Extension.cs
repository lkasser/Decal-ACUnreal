using System;
using System.Collections.Generic;
using System.Reflection;
using Decal.Adapter.Hosting;
using Decal.Adapter.Wrappers;

namespace Decal.Adapter
{
    public enum DecalExtensionType
    {
        Surrogate = 0,
        Service = 1,
        InputAction = 2,
        FileFilter = 3,
        NetworkFilter = 4,
        Plugin = 5,
    }

    public enum MessageDirection
    {
        Inbound = 0,
        Outbound = 1,
    }

    /// <summary>
    /// What every Decal plugin, filter and service derives from, through one of the three
    /// public bases.
    /// </summary>
    /// <remarks>
    /// The constructor is internal, as Decal's was, so nothing outside can derive from this
    /// directly: a plugin is a <see cref="PluginBase"/>, a filter a <see cref="FilterBase"/>,
    /// and the runtime tells them apart by that alone.
    ///
    /// <para>
    /// The protected events are the <see cref="CoreManager"/>'s own, reached from inside the
    /// plugin; Decal forwarded them the same way, which is what lets <see cref="BaseEventAttribute"/>
    /// name either and mean the same thing.
    /// </para>
    /// </remarks>
    public abstract class Extension : MarshalByRefObject
    {
        private readonly DecalExtensionType _type;

        internal Extension(DecalExtensionType type)
        {
            _type = type;
        }

        public DecalExtensionType ExtensionType => _type;

        /// <summary>The directory the extension was loaded from. Decal set it before Startup, and so does this.</summary>
        public string Path { get; set; }

        protected CoreManager Core => CoreManager.Current;

        /// <summary>A COM object model Decal exposed to scripts. There is none here.</summary>
        public virtual object ComOM => null;

        public virtual string ReferenceName => GetType().FullName;

        /// <summary>The runtime this extension was started in, or null before it was.</summary>
        internal DecalRuntime Runtime { get; set; }

        protected abstract void Startup();

        protected abstract void Shutdown();

        internal void InvokeStartup() => Startup();

        internal void InvokeShutdown() => Shutdown();

        /// <summary>
        /// Sent between Decal extensions to talk to each other. Nothing here listens, so it goes
        /// nowhere; it exists so code that sends one still compiles and runs.
        /// </summary>
        protected void SendAdapterMessage(EventArgs args)
        {
        }

        protected event EventHandler<DirectoryResolveEventArgs> DirectoryResolve
        {
            add { }
            remove { }
        }

        protected event EventHandler<WindowMessageEventArgs> WindowMessage
        {
            add => Core.WindowMessage += value;
            remove => Core.WindowMessage -= value;
        }

        protected event EventHandler<ChatClickInterceptEventArgs> ChatNameClicked
        {
            add => Core.ChatNameClicked += value;
            remove => Core.ChatNameClicked -= value;
        }

        protected event EventHandler<MessageProcessedEventArgs> MessageProcessed
        {
            add => Core.MessageProcessed += value;
            remove => Core.MessageProcessed -= value;
        }

        protected event EventHandler<RegionChange3DEventArgs> RegionChange3D
        {
            add => Core.RegionChange3D += value;
            remove => Core.RegionChange3D -= value;
        }

        protected event EventHandler<ChatTextInterceptEventArgs> ChatBoxMessage
        {
            add => Core.ChatBoxMessage += value;
            remove => Core.ChatBoxMessage -= value;
        }

        protected event EventHandler<ItemSelectedEventArgs> ItemSelected
        {
            add => Core.ItemSelected += value;
            remove => Core.ItemSelected -= value;
        }

        protected event EventHandler<ItemDestroyedEventArgs> ItemDestroyed
        {
            add => Core.ItemDestroyed += value;
            remove => Core.ItemDestroyed -= value;
        }

        protected event EventHandler<ChatParserInterceptEventArgs> CommandLineText
        {
            add => Core.CommandLineText += value;
            remove => Core.CommandLineText -= value;
        }

        protected event EventHandler<ContainerOpenedEventArgs> ContainerOpened
        {
            add => Core.ContainerOpened += value;
            remove => Core.ContainerOpened -= value;
        }
    }

    /// <summary>
    /// A Decal plugin: what every plugin's PluginCore derives from.
    /// </summary>
    /// <remarks>
    /// Started by the runtime in the order Decal used - views from <see cref="ViewAttribute"/>
    /// loaded, base events and control events wired where the class asks for it, and only
    /// then <see cref="Extension.Startup"/> - so a plugin's Startup finds its control
    /// references already set, as it always did.
    /// </remarks>
    public abstract class PluginBase : Extension, IViewHandler
    {
        private readonly Dictionary<string, ViewWrapper> _views = new Dictionary<string, ViewWrapper>(StringComparer.Ordinal);

        protected PluginBase()
            : base(DecalExtensionType.Plugin)
        {
        }

        /// <summary>The plugin's own window onto the game: actions, views, rendering.</summary>
        protected PluginHost Host => HostInternal;

        internal PluginHost HostInternal { get; set; }

        /// <summary>The view named "Default" - the one a lone <see cref="ViewAttribute"/> loads.</summary>
        public ViewWrapper DefaultView => GetView(ViewBaseAttribute.DefaultViewName);

        /// <summary>What <see cref="WireUpControlEventsAttribute"/> scans for fields and methods with.</summary>
        public BindingFlags BindingFlags => BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal IEnumerable<ViewWrapper> Views => _views.Values;

        public void LoadView(string name, string resource)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));

            ViewWrapper view = HostInternal.LoadViewResource(resource, GetType().Assembly);
            if (_views.TryGetValue(name, out ViewWrapper previous))
                previous.Dispose();

            _views[name] = view;
        }

        public ViewWrapper GetView(string name)
            => name != null && _views.TryGetValue(name, out ViewWrapper view) ? view : null;

        internal void DisposeViews()
        {
            foreach (ViewWrapper view in _views.Values)
                view.Dispose();

            _views.Clear();
        }

        /// <summary>
        /// Every message from the server, read against Decal's messages.xml. Decal's own way:
        /// a subscription to <see cref="Wrappers.EchoFilter2.ServerDispatch"/>, so a plugin
        /// subscribed both ways hears each message twice, as it did under Decal.
        /// </summary>
        protected event EventHandler<NetworkMessageEventArgs> ServerDispatch
        {
            add { if (Core != null) Core.EchoFilter.ServerDispatch += value; }
            remove { if (Core != null) Core.EchoFilter.ServerDispatch -= value; }
        }

        /// <summary>Every message from the client, the same way as <see cref="ServerDispatch"/>.</summary>
        protected event EventHandler<NetworkMessageEventArgs> ClientDispatch
        {
            add { if (Core != null) Core.EchoFilter.ClientDispatch += value; }
            remove { if (Core != null) Core.EchoFilter.ClientDispatch -= value; }
        }

        /// <summary>The Direct3D device was reset. There is no device here, so it never is.</summary>
        protected event EventHandler GraphicsReset
        {
            add { }
            remove { }
        }
    }

    /// <summary>
    /// A Decal network filter: something that watches the protocol for others, such as the
    /// FileService every spell-table plugin asked for.
    /// </summary>
    public abstract class FilterBase : Extension
    {
        protected FilterBase()
            : base(DecalExtensionType.NetworkFilter)
        {
        }

        protected NetServiceHost Host => HostInternal;

        internal NetServiceHost HostInternal { get; set; }

        /// <summary>Every message from the server, read against messages.xml; raised for each filter before plugins hear it.</summary>
        protected event EventHandler<NetworkMessageEventArgs> ServerDispatch;

        /// <summary>Every message from the client, the same way.</summary>
        protected event EventHandler<NetworkMessageEventArgs> ClientDispatch;

        internal bool HasListeners(MessageDirection direction)
            => (direction == MessageDirection.Inbound ? ServerDispatch : ClientDispatch) != null;

        internal void Dispatch(Message message, MessageDirection direction)
        {
            if (direction == MessageDirection.Inbound)
                Runtime?.RaiseMessage(ServerDispatch, this, message, "ServerDispatch");
            else
                Runtime?.RaiseMessage(ClientDispatch, this, message, "ClientDispatch");
        }
    }

    /// <summary>A Decal service: started before plugins and stopped after them.</summary>
    public abstract class ServiceBase : Extension
    {
        public ServiceBase()
            : base(DecalExtensionType.Service)
        {
        }

        protected virtual void OnBeforePlugins()
        {
        }

        protected virtual void OnAfterPlugins()
        {
        }

        internal void InvokeBeforePlugins() => OnBeforePlugins();

        internal void InvokeAfterPlugins() => OnAfterPlugins();
    }
}
