using System.Collections.Generic;
using AC.Host.Plugins.Views;

namespace AC.Host.Plugins
{
    /// <summary>
    /// Implemented by a plugin whose window is a Decal view.
    /// </summary>
    /// <remarks>
    /// The plugin parses its view XML once - in <see cref="IPlugin.Startup"/>, typically,
    /// from the same embedded resource it shipped for Decal - and keeps the result. The host
    /// reads the view's current state each time it publishes, and the overlay draws the
    /// window as that view, in the Decal theme, rather than as controls and panel tabs.
    ///
    /// <para>
    /// A command naming one of the view's controls is applied to the view: the control's
    /// state is updated and its event raised, on the game thread, and the plugin hears about
    /// it through that event rather than through <see cref="IOverlayCommands"/>. A plugin may
    /// implement both; anything the view has no control for still goes to
    /// <see cref="IOverlayCommands.HandleCommand"/>.
    /// </para>
    ///
    /// <para>
    /// Read on the game thread, when publishing and when a command arrives, so it must be
    /// cheap: return the view already built. Null means no view, and the window is drawn as
    /// it would be without this interface.
    /// </para>
    /// </remarks>
    public interface IOverlayView
    {
        DecalView View { get; }
    }

    /// <summary>
    /// Implemented by a plugin that shows windows beyond its own - one that hosts other
    /// plugins, as the Decal compatibility layer hosts Decal's, each with windows of its own.
    /// </summary>
    /// <remarks>
    /// Each <see cref="OverlayViewWindow"/> becomes a window beside the plugin's own, owned by
    /// <see cref="OverlayViewWindow.OwnerFor"/>, and a command from one is applied to its view
    /// as a command to an <see cref="IOverlayView"/>'s is. The list is read on the game thread
    /// each time the host publishes, so windows come and go as the plugin opens and closes
    /// them; return the views already built, and an empty list rather than null.
    /// </remarks>
    public interface IOverlayViews
    {
        IReadOnlyList<OverlayViewWindow> Views { get; }
    }

    /// <summary>One of the extra windows an <see cref="IOverlayViews"/> plugin shows.</summary>
    public sealed class OverlayViewWindow
    {
        public OverlayViewWindow(string key, DecalView view, bool startsClosed = false)
        {
            Key = key ?? string.Empty;
            View = view;
            StartsClosed = startsClosed;
        }

        /// <summary>
        /// Whether the window waits on the bar until the player opens it, as a Decal plugin's
        /// did until its plugin activated it, rather than opening the first time it is seen.
        /// </summary>
        public bool StartsClosed { get; }

        /// <summary>
        /// Names the window among the plugin's others. Keep it stable from one session to the
        /// next, since it is how the overlay remembers where the window was left.
        /// </summary>
        public string Key { get; }

        public DecalView View { get; }

        /// <summary>
        /// The owner such a window goes by on the bar and in commands: the plugin's name, a
        /// slash, and the key. Plugin names have no slash in them, so the first one splits it.
        /// </summary>
        public static string OwnerFor(string pluginName, string key) => pluginName + "/" + key;
    }
}
