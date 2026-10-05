using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml;
using AC.Host.Plugins.Views;
using Decal.Adapter.Hosting;

namespace Decal.Adapter.Wrappers
{
    /// <summary>What <see cref="PluginHost"/> and <see cref="NetServiceHost"/> share.</summary>
    public abstract class HostBase : MarshalByRefObject, IDisposable
    {
        protected HostBase()
        {
        }

        protected bool IsDisposed { get; private set; }

        protected DecalWrapper MyDecal { get; set; }

        protected RenderServiceWrapper MyRender { get; set; }

        public void Dispose()
        {
            if (IsDisposed)
                return;

            Dispose(true);
            IsDisposed = true;
        }

        protected virtual void Dispose(bool disposing)
        {
        }
    }

    /// <summary>
    /// A plugin's window onto Decal: its actions, its views, and Decal itself.
    /// </summary>
    /// <remarks>
    /// One per plugin, made before the plugin starts. Views loaded through it are labelled
    /// as that plugin's windows, and go when it does.
    /// </remarks>
    public sealed class PluginHost : HostBase
    {
        private readonly DecalRuntime _runtime;
        private readonly PluginBase _plugin;

        internal PluginHost(DecalRuntime runtime, PluginBase plugin)
        {
            _runtime = runtime;
            _plugin = plugin;
            MyDecal = runtime.Core.Decal;
            MyRender = runtime.Core.RenderService;
        }

        public HooksWrapper Actions => _runtime.Core.Actions;

        public DecalWrapper Decal => MyDecal;

        public RenderServiceWrapper Render => MyRender;

        /// <summary>The COM plugin site underneath - here only a way to the client hooks' RenderPreUI.</summary>
        public global::Decal.Interop.Core.IPluginSite2 Underlying => _runtime.Site;

        /// <summary>
        /// Loads a view from an embedded resource in the plugin's own assembly - or, failing
        /// that, the assembly calling, which for plugins whose view code lives in a shared
        /// library is where the resource is.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public ViewWrapper LoadViewResource(string resourcePath)
            => LoadViewResource(resourcePath, FindResourceAssembly(resourcePath, Assembly.GetCallingAssembly()));

        public ViewWrapper LoadViewResource(string resourcePath, Assembly resourceAssembly)
        {
            if (resourcePath == null) throw new ArgumentNullException(nameof(resourcePath));

            Assembly assembly = resourceAssembly ?? _plugin.GetType().Assembly;
            using Stream stream = assembly.GetManifestResourceStream(resourcePath)
                ?? throw new ArgumentException($"{assembly.GetName().Name} has no embedded resource '{resourcePath}'.", nameof(resourcePath));
            using StreamReader reader = new StreamReader(stream);
            return LoadView(reader.ReadToEnd());
        }

        public ViewWrapper LoadView(XmlElement viewSchema)
        {
            if (viewSchema == null) throw new ArgumentNullException(nameof(viewSchema));
            return LoadView(viewSchema.OuterXml);
        }

        /// <summary>Parses view XML and opens it as one of the plugin's windows.</summary>
        public ViewWrapper LoadView(string viewSchema)
        {
            DecalView view = DecalView.Parse(viewSchema);
            HostedView hosted = _runtime.AddView(view, _runtime.NameOf(_plugin));
            return new ViewWrapper(_runtime, hosted);
        }

        /// <summary>A handler class that loads its own views. Made with the host, as Decal made it.</summary>
        public ViewHandler LoadViewHandler(Type handlerType)
        {
            if (handlerType == null) throw new ArgumentNullException(nameof(handlerType));
            return (ViewHandler)Activator.CreateInstance(handlerType, this);
        }

        /// <summary>A COM object by Decal's path for it. There are no COM objects here.</summary>
        public object GetObject(string path) => MyDecal.GetObject(path);

        public int GetKeyboardMapping(string name) => 0;

        public object ComFilter(string progId)
        {
            _runtime.NoteUnsupported("PluginHost.ComFilter", progId);
            return null;
        }

        private Assembly FindResourceAssembly(string resourcePath, Assembly caller)
        {
            Assembly own = _plugin.GetType().Assembly;
            if (own.GetManifestResourceInfo(resourcePath) != null)
                return own;

            return caller != null && caller.GetManifestResourceInfo(resourcePath) != null ? caller : own;
        }
    }

    /// <summary>A filter's window onto Decal. Filters mostly just listen, so it offers little.</summary>
    public sealed class NetServiceHost : HostBase
    {
        private readonly DecalRuntime _runtime;

        internal NetServiceHost(DecalRuntime runtime)
        {
            _runtime = runtime;
            MyDecal = runtime.Core.Decal;
            MyRender = runtime.Core.RenderService;
        }

        public DecalWrapper Decal => MyDecal;

        public HooksWrapper Actions => _runtime.Core.Actions;

        public object GetComFilter(string progId) => null;

        public object GetComFilter(Guid clsid, Guid riid) => null;
    }

    /// <summary>Decal itself: its install, its window, its COM objects.</summary>
    public class DecalWrapper : MarshalByRefObject, IDisposable
    {
        private readonly DecalRuntime _runtime;

        internal DecalWrapper(DecalRuntime runtime)
        {
            _runtime = runtime;
        }

        /// <summary>The client's window. The host is another process and has none.</summary>
        public IntPtr Hwnd => IntPtr.Zero;

        public bool Focus => false;

        /// <summary>
        /// Decal's objects by path - "services\\DecalPlugins.InjectService" and the like, all of
        /// them COM. None exist here, so null, which is what Decal gave for a path it did not know.
        /// </summary>
        public object GetObject(string path)
        {
            _runtime.NoteUnsupported("Decal.GetObject", path);
            return null;
        }

        public object GetObject(string path, string iid) => GetObject(path);

        public object GetObject(string path, Guid iid) => GetObject(path);

        /// <summary>
        /// Decal's path shorthand, where "%decal%" and the like stood for its folders. There
        /// are no such folders here, so the path comes back as given.
        /// </summary>
        public string MapPath(string path) => path;

        public void Dispose()
        {
        }
    }

    /// <summary>A plugin's views, loaded from resources on request, as Decal's own handler base did.</summary>
    public class ViewHandler : IViewHandler, IDisposable
    {
        private readonly System.Collections.Generic.Dictionary<string, ViewWrapper> _views = new System.Collections.Generic.Dictionary<string, ViewWrapper>(StringComparer.Ordinal);

        public ViewHandler(PluginHost host)
        {
            Host = host ?? throw new ArgumentNullException(nameof(host));
            OnLoad();
        }

        protected PluginHost Host { get; }

        protected bool IsDisposed { get; private set; }

        public ViewWrapper DefaultView => GetView(ViewBaseAttribute.DefaultViewName);

        public BindingFlags BindingFlags => BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public void LoadView(string name, string resource)
        {
            ViewWrapper view = Host.LoadViewResource(resource, GetType().Assembly);
            if (_views.TryGetValue(name, out ViewWrapper previous))
                previous.Dispose();

            _views[name] = view;
        }

        public ViewWrapper GetView(string name) => name != null && _views.TryGetValue(name, out ViewWrapper view) ? view : null;

        protected virtual void OnLoad()
        {
            foreach (ViewAttribute attribute in GetType().GetCustomAttributes<ViewAttribute>(true))
                LoadView(attribute.ViewName, attribute.Resource);
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;

            Dispose(true);
            IsDisposed = true;
        }

        protected virtual void Dispose(bool disposing)
        {
            foreach (ViewWrapper view in _views.Values)
                view.Dispose();

            _views.Clear();
        }
    }

    /// <summary>What plugins and view handlers offer: their views, by name.</summary>
    public interface IViewHandler
    {
        void LoadView(string name, string resource);

        ViewWrapper GetView(string name);

        ViewWrapper DefaultView { get; }

        BindingFlags BindingFlags { get; }
    }
}
