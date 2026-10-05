using System;
using System.Collections.Generic;
using System.Drawing;
using Decal.Adapter.Hosting;

namespace VirindiViewService
{
    /// <summary>
    /// Virindi View Service itself: whether it is running, and a few services it offered
    /// plugins outside their windows.
    /// </summary>
    /// <remarks>
    /// Plugins look for VVS before using it - for this assembly among those loaded, then for
    /// <see cref="Running"/> - and fall back to Decal's own views when it is missing. So
    /// <see cref="Running"/> is true only while a Decal runtime is up and has VVS switched on;
    /// with it off, the same plugin runs through Decal's views instead, and both end up as the
    /// same kind of window in the host's overlay.
    /// </remarks>
    public static class Service
    {
        private static readonly HudBar.cHudBarHud _hudBar = new HudBar.cHudBarHud();

        public static bool Running => DecalRuntime.Current?.VirindiViewServiceRunning == true;

        /// <summary>The client's window. The host is another process and has none.</summary>
        public static int Game_HWND => 0;

        /// <summary>VVS's bar of window buttons, which plugins could add their own huds to.</summary>
        public static HudBar.cHudBarHud HudBarInstance => _hudBar;

#pragma warning disable CS0067
        /// <summary>The Direct3D device was lost. There is no device here.</summary>
        public static event EventHandler DeviceLost;
#pragma warning restore CS0067

        /// <summary>
        /// Whether the portal file has an image - which VVS checked before drawing one. The host
        /// sends image ids to the overlay, which draws what the client has; anything in the
        /// portal image range is taken to exist.
        /// </summary>
        public static bool PortalBitmapExists(int pfile) => (pfile & 0xFF000000) == 0x06000000 || (pfile > 0 && pfile <= 0x00FFFFFF);

        /// <summary>
        /// The size text would take. The overlay does the drawing, so this is an estimate from
        /// the font's height - enough for a plugin laying out labels to leave room.
        /// </summary>
        public static Rectangle MeasureText(string Text, WriteTextFormats Format, string FontName, float Height, int Weight, bool Italic, int shadowsize)
        {
            int width = (int)Math.Ceiling((Text ?? string.Empty).Length * Height * 0.55);
            return new Rectangle(0, 0, width, (int)Math.Ceiling(Height));
        }

        public static void KeyPassthroughIncrement()
        {
        }

        public static void KeyPassthroughDecrement()
        {
        }

        public static void MousePassthroughIncrement()
        {
        }

        public static void MousePassthroughDecrement()
        {
        }
    }

    [Flags]
    public enum WriteTextFormats
    {
        None = 0,
        Center = 1,
        Right = 2,
        VerticalCenter = 4,
        Bottom = 8,
        WordBreak = 16,
        SingleLine = 32,
        ExpandTabs = 64,
        NoClip = 256,
        RightToLeftReading = 131072,
    }

    public enum FontWeight
    {
        DoNotCare = 0,
        Thin = 100,
        ExtraLight = 200,
        UltraLight = 200,
        Light = 300,
        Normal = 400,
        Regular = 400,
        Medium = 500,
        DemiBold = 600,
        SemiBold = 600,
        Bold = 700,
        ExtraBold = 800,
        UltraBold = 800,
        Black = 900,
        Heavy = 900,
    }

    /// <summary>
    /// A VVS theme. The overlay draws every window in the Decal theme, so there is one theme,
    /// and asking for another gives it.
    /// </summary>
    public class HudViewDrawStyle
    {
        private static readonly HudViewDrawStyle _decal = new HudViewDrawStyle("Decal");

        public HudViewDrawStyle()
            : this("Decal")
        {
        }

        private HudViewDrawStyle(string name)
        {
            Name = name;
        }

        public static HudViewDrawStyle Theme_Default => _decal;

        public static HudViewDrawStyle Theme_OldDecal => _decal;

        public static int StyleCount => 1;

        public static int NumStyles => 1;

        public string Name { get; }

        public static HudViewDrawStyle GetStyle(int i) => _decal;

        public static HudViewDrawStyle NextTheme(HudViewDrawStyle cur) => _decal;

        public static int GetIndexOf(HudViewDrawStyle cur) => 0;

        public static bool HasThemeName(string str) => string.Equals(str, _decal.Name, StringComparison.OrdinalIgnoreCase);

        public static HudViewDrawStyle GetThemeByName(string str) => _decal;

        public static void AddGlobalTheme(HudViewDrawStyle s)
        {
        }

        public Color GetColor(string n) => Color.White;

        /// <summary>
        /// A theme value by name - a colour, a font, a size. The overlay's Decal theme is not
        /// described in VVS's terms, so there are none, and every value is its type's default.
        /// </summary>
        public T GetVal<T>(string k) => default;

        public object GetValObject(string v) => null;

        public bool HasVal(string k) => false;

        public bool HasVal(string k, Type t) => false;

        public System.Collections.ObjectModel.ReadOnlyCollection<string> GetValList() => new System.Collections.ObjectModel.ReadOnlyCollection<string>(Array.Empty<string>());

        protected void SetVal<T>(string k, T value)
        {
        }
    }
}

namespace VirindiViewService.HudBar
{
    public enum eIconType
    {
        Portal = 0,
        Bitmap = 1,
    }

    /// <summary>One button on VVS's bar.</summary>
    public class sHudInfo
    {
        public ACImage icon;
        public bool hudvisible;
        public int group;
        public int zorder;
        public string EntryName;

        public sHudInfo()
        {
        }
    }

    /// <summary>
    /// VVS's bar, where plugins could add buttons for huds of their own. The host's bar lists
    /// windows, not huds, so buttons added here are remembered and never shown or clicked.
    /// </summary>
    public class cHudBarHud
    {
        private readonly Dictionary<int, sHudInfo> _huds = new Dictionary<int, sHudInfo>();
        private int _next;

        internal cHudBarHud()
        {
        }

        public delegate void ClickedDelegate(int handle);

#pragma warning disable CS0067
        public event ClickedDelegate Clicked;
#pragma warning restore CS0067

        public bool Horizontal { get; set; }

        public int AddHud(sHudInfo hudinfo)
        {
            DecalRuntime.Current?.NoteUnsupported("VirindiViewService.HudBar", "buttons on VVS's bar are not shown");
            _huds[++_next] = hudinfo;
            return _next;
        }

        public void RemoveHud(int handle) => _huds.Remove(handle);

        public void SetHudInfo(int handle, sHudInfo hudinfo) => _huds[handle] = hudinfo;

        public void SetHudIcon(int handle, ACImage icon)
        {
            if (_huds.TryGetValue(handle, out sHudInfo info))
                info.icon = icon;
        }

        public void SetHudEnabled(int handle, bool enabled)
        {
            if (_huds.TryGetValue(handle, out sHudInfo info))
                info.hudvisible = enabled;
        }

        public sHudInfo GetHudInfo(int handle) => _huds.TryGetValue(handle, out sHudInfo info) ? info : null;
    }
}
