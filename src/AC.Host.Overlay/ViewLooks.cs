using System;
using System.Collections.Generic;
using AC.Dat;
using AC.Host.Plugins.Views;

namespace AC.Host.Overlay
{
    /// <summary>
    /// Settles how each window is first drawn, by Virindi View Service's rules: the theme the
    /// player picked for it in the standard client, else the one its plugin asks for, else
    /// VVS's default - for a VVS window; a Decal window keeps the Decal theme. Whether it is
    /// hudified and click-through comes from the player's vvs.s3db too, and where it was left
    /// when it was hudified, which is when VVS put a window back.
    /// </summary>
    /// <remarks>
    /// Only how a window starts. What the player then picks from the window's own title bar the
    /// overlay keeps, and that outranks everything here.
    /// </remarks>
    public sealed class ViewLooks
    {
        /// <summary>The theme of a window written for Decal rather than for VVS.</summary>
        public const string DecalTheme = "Decal";

        private readonly IReadOnlyDictionary<string, VirindiStoredView> _stored;

        public ViewLooks(string defaultTheme, IReadOnlyDictionary<string, VirindiStoredView> stored, IReadOnlyDictionary<string, long> extraInfo = null)
        {
            DefaultTheme = string.IsNullOrWhiteSpace(defaultTheme) ? VirindiViewStore.DefaultTheme(null, null) : defaultTheme;
            _stored = stored ?? new Dictionary<string, VirindiStoredView>();
            VvsBar = BarFrom(_stored, extraInfo);
        }

        /// <summary>VVS's default theme, for a VVS window the player never picked one for.</summary>
        public string DefaultTheme { get; }

        /// <summary>How many windows the player's store remembers.</summary>
        public int StoredCount => _stored.Count;

        /// <summary>
        /// The VVS bar as the store left it: its own row, read as VVS read a hudified view's - where
        /// and against which edges - and ExtraInfo's VVSBarHorizontal. Null when the store has neither.
        /// </summary>
        public OverlayVvsBar VvsBar { get; }

        private static OverlayVvsBar BarFrom(IReadOnlyDictionary<string, VirindiStoredView> stored, IReadOnlyDictionary<string, long> extraInfo)
        {
            OverlayVvsBar bar = new OverlayVvsBar();
            bool any = false;
            if (stored.TryGetValue(VirindiViewStore.BarKey, out VirindiStoredView row) && row.Ghosted)
            {
                bar.X = row.X;
                bar.Y = row.Y;
                bar.Stuck = row.StuckEdges;
                any = true;
            }

            if (extraInfo != null && extraInfo.TryGetValue("VVSBarHorizontal", out long horizontal))
            {
                bar.Horizontal = horizontal != 0;
                any = true;
            }

            return any ? bar : null;
        }

        /// <summary>
        /// Reads VVS's registry settings and store, beside the given VirindiViewService.dll or
        /// where the registry says; says what it found, or why it found nothing, through
        /// <paramref name="log"/>. Never throws: with nothing read, every VVS window starts in
        /// VVS's default theme.
        /// </summary>
        public static ViewLooks Load(string virindiViewServicePath, Action<string> log)
        {
            VirindiViewStore.ReadRegistry(out string registryFile, out string theme2, out int? themeIndex);
            string defaultTheme = VirindiViewStore.DefaultTheme(theme2, themeIndex);

            string file = VirindiViewStore.FindStoreFile(registryFile, virindiViewServicePath);
            if (file == null)
            {
                log?.Invoke($"No Virindi View Service store (vvs.s3db) was found, so VVS windows open in its default theme, {defaultTheme}.");
                return new ViewLooks(defaultTheme, null);
            }

            if (!VirindiViewStore.TryRead(file, out IReadOnlyDictionary<string, VirindiStoredView> stored, out string error))
            {
                log?.Invoke($"Virindi View Service's store {file} could not be read ({error}), so VVS windows open in its default theme, {defaultTheme}.");
                return new ViewLooks(defaultTheme, null);
            }

            if (!VirindiViewStore.TryReadExtraInfo(file, out IReadOnlyDictionary<string, long> extra, out error))
            {
                log?.Invoke($"Virindi View Service's store {file} has an ExtraInfo table that could not be read ({error}), so the VVS bar keeps its own orientation.");
                extra = null;
            }

            log?.Invoke($"Read {stored.Count} windows from Virindi View Service's store {file}; its default theme is {defaultTheme}. The file is only read.");
            return new ViewLooks(defaultTheme, stored, extra);
        }

        /// <summary>Fills in how a view starts, over what the plugin said.</summary>
        public void Apply(OverlayView dto, DecalView view)
        {
            if (dto == null || view == null)
                return;

            VirindiStoredView stored = null;
            if (!string.IsNullOrEmpty(view.StoredKey))
                _stored.TryGetValue(view.StoredKey, out stored);

            // A VVS window with no theme of its own is sent none: it follows VVS's primary theme,
            // which the overlay is told once and the player can step on from the VVS bar.
            string asked = string.IsNullOrWhiteSpace(view.Theme) ? null : view.Theme.Trim();
            dto.Theme = stored?.Theme ?? asked ?? (view.Bar == ViewBar.Vvs ? string.Empty : DecalTheme);

            if (stored == null)
                return;

            dto.Ghosted = view.Ghostable && stored.Ghosted;
            dto.ClickThrough = view.ClickThroughable && stored.ClickThrough;
            if (dto.Ghosted)
            {
                // As VVS's LoadUserSettings: a hudified window goes back where it was left, and
                // against the edges it was left against.
                dto.X = stored.X;
                dto.Y = stored.Y;
                string stuck = stored.StuckEdges;
                dto.Stuck = stuck.Length > 0 ? stuck : null;
            }

            // And a window the player may resize opens at the size they left it, kept within
            // what the plugin allows.
            if (view.Resizeable && stored.Width > 0 && stored.Height > 0)
            {
                dto.StoredWidth = Math.Min(Math.Max(stored.Width, view.MinWidth), Math.Max(view.MinWidth, view.MaxWidth));
                dto.StoredHeight = Math.Min(Math.Max(stored.Height, view.MinHeight), Math.Max(view.MinHeight, view.MaxHeight));
            }
        }
    }
}
