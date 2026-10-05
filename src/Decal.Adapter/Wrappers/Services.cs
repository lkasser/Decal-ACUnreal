using System;
using System.Collections.Generic;
using System.Drawing;
using Decal.Adapter.Hosting;

namespace Decal.Adapter.Wrappers
{
    // Decal's services that drew into, or read from, the client itself: huds and backgrounds,
    // 3D markers, hotkeys - and the raw message echo, which is real. The host is another
    // process and draws only through its overlay's windows, so the drawing services accept
    // everything asked of them and draw nothing. They exist so the plugins that use them start
    // and run their other features; each says once in the log that it was used.

    /// <summary>The render service: huds and backgrounds drawn over the 3D view.</summary>
    public class RenderServiceWrapper : MarshalByRefObject, IDisposable
    {
        private readonly DecalRuntime _runtime;

        internal RenderServiceWrapper(DecalRuntime runtime)
        {
            _runtime = runtime;
        }

        public object UnsafeDevice => null;

        public Hud CreateHud(Rectangle region)
        {
            _runtime.NoteUnsupported("RenderService.CreateHud", "huds are not drawn");
            return new Hud(region);
        }

        public Background CreateBackground(Rectangle region)
        {
            _runtime.NoteUnsupported("RenderService.CreateBackground", "backgrounds are not drawn");
            return new Background(region);
        }

        public void RemoveHud(Hud hud) => hud?.Dispose();

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
        }
    }

    /// <summary>Something that can be drawn on. Here, something that remembers where it is and draws nothing.</summary>
    public class HudRenderTarget : MarshalByRefObject, IDisposable
    {
        internal HudRenderTarget(Rectangle region)
        {
            Region = region;
        }

        public bool Lost => false;

        public Rectangle Constraint => Region;

        public Rectangle Region { get; set; }

        public object UnsafeSurface => null;

        protected bool IsDisposed { get; private set; }

        public void BeginRender()
        {
        }

        public void BeginRender(bool enableTextureFilter)
        {
        }

        public void EndRender()
        {
        }

        public void BeginText(string fontName, int pixelHeight)
        {
        }

        public void BeginText(string fontName, int pixelHeight, FontWeight weight, bool italic)
        {
        }

        public void WriteText(string text)
        {
        }

        public void WriteText(string text, Color color)
        {
        }

        public void WriteText(string text, Color color, WriteTextFormats format, Rectangle region)
        {
        }

        public void WriteText(string text, int color, WriteTextFormats format, Rectangle region)
        {
        }

        public void EndText()
        {
        }

        public void Clear()
        {
        }

        public void Clear(Rectangle clearArea)
        {
        }

        public void DrawPortalImage(int portalFile, Rectangle destinationArea)
        {
        }

        public void DrawPortalImage(int portalFile, int alpha, Rectangle srcArea, Rectangle destinationArea)
        {
        }

        public void DrawPortalImage(int portalFile, Rectangle srcArea, Rectangle destinationArea)
        {
        }

        public void TilePortalImage(int portalFile, Rectangle destinationArea)
        {
        }

        public void TilePortalImage(int portalFile, Rectangle srcArea, Rectangle destinationArea)
        {
        }

        public void Fill(Color color)
        {
        }

        public void Fill(Rectangle fillArea, Color color)
        {
        }

        public void Fill(Rectangle fillArea, int color)
        {
        }

        public void UnsafeBeginText(string fontName, int pixelHeight, int weight, bool italic)
        {
        }

        public void UnsafeSetSurface(object pSurface)
        {
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
        }
    }

    public class HudRenderScalable : HudRenderTarget
    {
        internal HudRenderScalable(Rectangle region)
            : base(region)
        {
            ScaleRect = region;
        }

        public Rectangle ScaleRect { get; private set; }

        public float ScaleFactor { get; set; } = 1f;

        public void ScaleTo(Rectangle rect) => ScaleRect = rect;
    }

    public class Hud : HudRenderScalable
    {
        private static int _nextId;

        internal Hud(Rectangle region)
            : base(region)
        {
            Id = ++_nextId;
        }

        public bool Enabled { get; set; }

        public int Id { get; }

        public float Angle { get; set; }

        public int Alpha { get; set; } = 255;

        public void SetBackground(Background background)
        {
        }
    }

    public class Background : HudRenderTarget
    {
        internal Background(Rectangle region)
            : base(region)
        {
        }

        public Background Clone() => new Background(Region);
    }

    /// <summary>
    /// Decal's hotkey system. Hotkeys are registered and remembered, but the host never sees
    /// the keyboard, so none is ever pressed.
    /// </summary>
    public class HotkeySystem : MarshalByRefObject, IDisposable
    {
        private readonly DecalRuntime _runtime;
        private readonly Dictionary<string, HotkeyWrapper> _hotkeys = new Dictionary<string, HotkeyWrapper>(StringComparer.OrdinalIgnoreCase);

        internal HotkeySystem(DecalRuntime runtime)
        {
            _runtime = runtime;
        }

#pragma warning disable CS0067
        public event EventHandler<HotkeyEventArgs> Hotkey;
#pragma warning restore CS0067

        public void AddHotkey(string szPlugin, HotkeyWrapper hotkey)
        {
            if (hotkey == null)
                return;

            _hotkeys[hotkey.Title] = hotkey;
            _runtime.NoteUnsupported("HotkeySystem", "hotkeys are remembered but the host cannot see the keyboard");
        }

        public void AddHotkey(string szPlugin, string szTitle, string szDescription) => AddHotkey(szPlugin, new HotkeyWrapper(szTitle, szDescription));

        public void AddHotkey(string plugin, string title, string description, int virtualKey, bool altState, bool controlState, bool shiftState)
            => AddHotkey(plugin, new HotkeyWrapper(title, description, virtualKey, altState, controlState, shiftState));

        public void DeleteHotkey(string plugin, string hotkeyName) => _hotkeys.Remove(hotkeyName ?? string.Empty);

        public bool Exists(string hotkeyName) => hotkeyName != null && _hotkeys.ContainsKey(hotkeyName);

        public HotkeyWrapper GetHotkey(string plugin, string hotkeyName)
            => hotkeyName != null && _hotkeys.TryGetValue(hotkeyName, out HotkeyWrapper hotkey) ? hotkey : null;

        /// <summary>Where a keyboard would be read, if the host could read one.</summary>
        internal void Poll()
        {
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
        }
    }

    public class HotkeyWrapper : MarshalByRefObject, IDisposable
    {
        public HotkeyWrapper(string title, string description)
            : this(title, description, 0, false, false, false)
        {
        }

        public HotkeyWrapper(string title, string description, int virtualKey, bool altState, bool controlState, bool shiftState)
        {
            Title = title ?? string.Empty;
            Description = description ?? string.Empty;
            VirtualKey = virtualKey;
            AltState = altState;
            ControlState = controlState;
            ShiftState = shiftState;
        }

        public bool AltState { get; }

        public bool ControlState { get; }

        public bool ShiftState { get; }

        public int VirtualKey { get; }

        public string Title { get; }

        public string Description { get; }

        public void Dispose()
        {
        }

        protected virtual void Dispose(bool disposing)
        {
        }

        protected void EnforceDisposedOnce()
        {
        }
    }

    public class HotkeyEventArgs : EventArgs
    {
        internal HotkeyEventArgs(string title)
        {
            Title = title;
        }

        public string Title { get; }

        public bool Eat { get; set; }
    }

    /// <summary>
    /// The raw message echo: every message from the server (<see cref="ServerDispatch"/>) and
    /// from the client (<see cref="ClientDispatch"/>), as a <see cref="Message"/> read against
    /// Decal's messages.xml. What a plugin's own ServerDispatch and ClientDispatch subscribe to.
    /// </summary>
    /// <remarks>
    /// Raised on the game thread, in the order the messages went by, once the host has applied
    /// each to its world - so a plugin looking up an object a message names finds it. Nothing
    /// is made for a message nobody listens for, and a message is read only as far as the first
    /// handler asks of it. A handler that throws costs that handler that message, as under Decal.
    /// </remarks>
    public class EchoFilter2 : DisposableByRefObject
    {
        private readonly DecalRuntime _runtime;

        internal EchoFilter2(DecalRuntime runtime)
        {
            _runtime = runtime;
        }

        /// <summary>The client received a message from the server.</summary>
        public event EventHandler<NetworkMessageEventArgs> ServerDispatch;

        /// <summary>The client sent a message to the server.</summary>
        public event EventHandler<NetworkMessageEventArgs> ClientDispatch;

        internal bool HasListeners(MessageDirection direction)
            => (direction == MessageDirection.Inbound ? ServerDispatch : ClientDispatch) != null;

        internal void Dispatch(Message message, MessageDirection direction)
        {
            if (direction == MessageDirection.Inbound)
                _runtime.RaiseMessage(ServerDispatch, this, message, "EchoFilter.ServerDispatch");
            else
                _runtime.RaiseMessage(ClientDispatch, this, message, "EchoFilter.ClientDispatch");
        }

        /// <summary>Lets go of every handler whose code is in <paramref name="assembly"/>, for a plugin that has been stopped.</summary>
        internal void Release(System.Reflection.Assembly assembly)
        {
            ServerDispatch = DecalRuntime.Without(ServerDispatch, assembly);
            ClientDispatch = DecalRuntime.Without(ClientDispatch, assembly);
        }
    }

    /// <summary>3D markers in the game world. Accepted and never drawn.</summary>
    public class D3DService : DisposableByRefObject
    {
        private readonly DecalRuntime _runtime;

        internal D3DService(DecalRuntime runtime)
        {
            _runtime = runtime;
        }

        public D3DObj NewD3DObj() => Marker();

        public D3DObj PointToObject(int guid, int color) => Marker();

        public D3DObj PointToCoords(float lat, float lng, float alt, int color) => Marker();

        public D3DObj MarkObjectWithIcon(int guid, int icon) => Marker();

        public D3DObj MarkObjectWithShape(int guid, D3DShape shape, int color) => Marker();

        public D3DObj MarkObjectWithShapeFromFile(int guid, string filename, int color) => Marker();

        public D3DObj MarkObjectWith2DText(int guid, string szText, string szFont, int options) => Marker();

        public D3DObj MarkObjectWith3DText(int guid, string szText, string szFont, int options) => Marker();

        public D3DObj MarkCoordsWithIcon(float lat, float lng, float alt, int icon) => Marker();

        public D3DObj MarkCoordsWithIconFromFile(float lat, float lng, float alt, string file) => Marker();

        public D3DObj MarkCoordsWithShape(float lat, float lng, float alt, D3DShape shape, int color) => Marker();

        public D3DObj MarkCoordsWithShapeFromFile(float lat, float lng, float alt, string file, int color) => Marker();

        public D3DObj MarkCoordsWith2DText(float lat, float lng, float alt, string szText, string szFont, int options) => Marker();

        public D3DObj MarkCoordsWith3DText(float lat, float lng, float alt, string szText, string szFont, int options) => Marker();

        private D3DObj Marker()
        {
            _runtime.NoteUnsupported("D3DService", "3D markers are not drawn");
            return new D3DObj();
        }
    }

    public class D3DObj : DisposableByRefObject
    {
        internal D3DObj()
        {
        }

        public int Color { get; set; }

        public int Color2 { get; set; }

        public bool Autoscale { get; set; }

        public bool DrawBackface { get; set; }

        public float HBounce { get; set; }

        public float PBounce { get; set; }

        public float PFade { get; set; }

        public float POrbit { get; set; }

        public float PSpin { get; set; }

        public float ROrbit { get; set; }

        public float ScaleX { get; set; }

        public float ScaleY { get; set; }

        public float ScaleZ { get; set; }

        public bool Visible { get; set; }

        public float AnimationPhaseOffset { get; set; }

        public void Scale(float factor)
        {
        }

        public void OrientToCamera(bool verticalTilt)
        {
        }

        public void OrientToCoords(float lat, float lng, float alt, bool verticalTilt)
        {
        }

        public void OrientToObject(int guid, float fractHeight, bool verticalTilt)
        {
        }

        public void OrientToPlayer(bool verticalTilt)
        {
        }

        public void Anchor(float lat, float lng, float alt)
        {
        }

        public void Anchor(int id, float height, float dx, float dy, float dz)
        {
        }

        public void SetText(string text)
        {
        }

        public void SetText(string text, string fontName)
        {
        }

        public void SetText(string text, string fontName, int options)
        {
        }

        public void SetText(D3DTextType type, string text, string fontName, int options)
        {
        }

        public void SetIcon(int id)
        {
        }

        public void SetIcon(string fileName)
        {
        }

        public void SetIcon(int module, int res)
        {
        }

        public void SetShape(D3DShape shape)
        {
        }

        public void SetShape(string fileName)
        {
        }

        public void SetShape(int module, int res)
        {
        }
    }
}
