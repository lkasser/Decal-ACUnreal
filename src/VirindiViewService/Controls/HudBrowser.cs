using Decal.Adapter.Hosting;

namespace VirindiViewService.Controls
{
    /// <summary>
    /// VVS's web browser, which drew pages with Awesomium onto its window.
    /// </summary>
    /// <remarks>
    /// There is no browser in the overlay, so <see cref="IsAvailable"/> is false - what VVS said
    /// where Awesomium was not installed - and plugins that ask first, as Integrator2 does, offer
    /// no browser. One made anyway is an empty space that goes nowhere when told to navigate,
    /// and the first plugin to try is told so in the log.
    /// </remarks>
    public class HudBrowser : HudControl
    {
        public HudBrowser()
            : this(500, 500)
        {
        }

        public HudBrowser(int initialw, int initialh)
        {
        }

        public delegate void delTC();

        public static bool IsAvailable => false;

        /// <summary>The page's title; there is never a page.</summary>
        public string Title => string.Empty;

#pragma warning disable CS0067
        /// <summary>Never raised: no page is ever loaded.</summary>
        public event delTC TitleChanged;
#pragma warning restore CS0067

        public void Navigate(string url)
            => DecalRuntime.Current?.NoteUnsupported("HudBrowser", "there is no web browser in the overlay");
    }
}
