using System;
using System.Collections.Generic;
using AC.Dat;

namespace AC.Host.Overlay
{
    /// <summary>
    /// The images the overlay's themes - VVS's Decal, Float and Minimalist ones - draw with, by the keys the DLL asks for.
    /// </summary>
    /// <remarks>
    /// The same list as the constants at the top of
    /// native/ACUnrealOverlay/decal_view.cpp - VVS's themes, read out of its own theme
    /// classes, plus Decal's bar switches. A test reads that file and fails if the
    /// two lists disagree, because a key missing here is an image the overlay draws as a
    /// plain stand-in for no reason anybody would think to look for.
    /// </remarks>
    public static class DecalThemeImages
    {
        public static IReadOnlyList<string> Keys { get; } = new[]
        {
            "portal:0600126F", // view and title background
            "portal:06001276", // text box
            "portal:0600191F", // checkbox, off
            "portal:0600191A", // checkbox, on
            "portal:0600128A", // list and dropdown background
            "portal:060012B2", // arrow up
            "portal:060012B1", // arrow down
            "portal:06001286", // slider nub
            "portal:0600126E", // scrollbar track
            "portal:06004C60", // scrollbar bubble, top
            "portal:06004C63", // scrollbar bubble, middle
            "portal:06004C64", // scrollbar bubble, middle under the pointer
            "portal:06004C65", // scrollbar bubble, middle while dragged
            "portal:06004C66", // scrollbar bubble, bottom
            "portal:06005E64", // close button
            "portal:06005E65", // close button, held
            "portal:06004C7B", // more opaque
            "portal:06004C79", // more opaque, held
            "portal:06004C7E", // more transparent
            "portal:06004C7C", // more transparent, held
            "vvs:Decal_Theme_Images.TabActiveLeft.png",
            "vvs:Decal_Theme_Images.TabActiveCenter.png",
            "vvs:Decal_Theme_Images.TabActiveRight.png",
            "vvs:Decal_Theme_Images.TabInactiveLeft.png",
            "vvs:Decal_Theme_Images.TabInactiveCenter.png",
            "vvs:Decal_Theme_Images.TabInactiveRight.png",
            "vvs:Decal_Theme_Images.pin1.png",        // the pin that hudifies a window
            "vvs:Decal_Theme_Images.pin2.png",        // the pin, held
            "vvs:Decal_Theme_Images.redarrow.png",    // click-through, on
            "vvs:Decal_Theme_Images.redarrow_d.png",  // click-through, on, held
            "vvs:Decal_Theme_Images.whitearrow.png",  // click-through, off
            "vvs:Decal_Theme_Images.whitearrow_d.png",
            "portal:0600612A", // Float: the frame's rule, across
            "portal:0600612B", // Float: the frame's rule, down
            "portal:06006129", // Float: the frame's corners
            "portal:06001932", // Float: close button
            "portal:06001933", // Float: close button, held
            "portal:06004D15", // Float: checkbox, off
            "portal:06004D17", // Float: checkbox, on
            "portal:06004C6C", // Float: scrollbar arrow up
            "portal:06004C69", // Float: scrollbar arrow down
            "portal:06004C8A", // the bar's arrow, pointing left (fold)
            "portal:06004C89", // the bar's arrow, pointing left, held
            "portal:06004C8D", // the bar's arrow, pointing right (unfold)
            "portal:06004C8C", // the bar's arrow, pointing right, held
            "bar:open",
            "bar:closed",
            "bar:faulted",
            "decal:Switch-Active.bmp",      // the compact bar's squares: open
            "decal:Switch-Inactive.bmp",    // closed
            "decal:Switchbar Disabled.bmp", // faulted
            "portal:060012AA", // the bar's grey dock square
            "portal:060012A9", // the grey dock square, held
            "vvs:HudBar.Icon_TButton.png", // "ab" at the top of VVS's bar: Change Global Theme
            "vvs:HudBar.bluearrow_down_up.png",    // the VVS bar's orientation arrow, down the side
            "vvs:HudBar.bluearrow_down_down.png",
            "vvs:HudBar.bluearrow_right_up.png",   // and across
            "vvs:HudBar.bluearrow_right_down.png",
            "vvs:HudBar.hsicon.png",               // the H.S. button: VVS's hot-dog stand theme
            "vvs:Minimalist_Theme_Images.tickmark.png",  // the Minimalist themes' tick
            "vvs:Minimalist_Theme_Images.arrowup.png",   // their scroll arrows
            "vvs:Minimalist_Theme_Images.arrowdown.png",
            "vvs:Minimalist_Black_Images.x.png",         // Minimalist Black's close button
            "vvs:Minimalist_Black_Images.up.png",        // its alpha buttons
            "vvs:Minimalist_Black_Images.down.png",
            "vvs:Icons_Simple.9x9_x.png",          // Minimalist Green's title-bar buttons
            "vvs:Icons_Simple.9x9_plus.png",
            "vvs:Icons_Simple.9x9_minus.png",
            "vvs:Icons_Simple.9x9_p.png",
            "vvs:Icons_Simple.9x9_c.png",
            "portal:06002344", // Minimalist Green: a button
            "portal:06002345", // Minimalist Green: a button, held
        };
    }

    /// <summary>
    /// Sends the overlay the images it needs: the theme's, once, and any a view names - an
    /// icon, a button's face, a list's icons - the first time a published snapshot names it.
    /// </summary>
    /// <remarks>
    /// Called on the game thread, straight after a snapshot is built. Each key is resolved
    /// once for the session, found or not; the catalog caches the pixels and this remembers
    /// what was tried, so a view that names the same icon in every snapshot costs a set
    /// lookup per snapshot and nothing more. An image that cannot be found is reported once
    /// and the overlay draws its stand-in.
    /// </remarks>
    public sealed class OverlayImagePublisher
    {
        /// <summary>An image key that is a colour, "color:AARRGGBB", drawn as a plain square.</summary>
        public const string ColourPrefix = "color:";

        private readonly OverlayServer _server;
        private readonly ImageCatalog _catalog;
        private readonly Action<string> _onMissing;
        private readonly HashSet<string> _tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _wanted = new System.Collections.Concurrent.ConcurrentQueue<string>();

        public OverlayImagePublisher(OverlayServer server, ImageCatalog catalog, Action<string> onMissing = null)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _onMissing = onMissing;
        }

        /// <summary>How many images were sent.</summary>
        public int Published { get; private set; }

        /// <summary>How many keys could not be resolved.</summary>
        public int Missing { get; private set; }

        /// <summary>
        /// Notes an image the overlay asked for because it wanted to draw it and did not have
        /// it. Safe from the pipe thread; sent by <see cref="PublishWanted"/> on the game
        /// thread, like everything else here. What makes new art in the DLL work without a new
        /// host: the theme list is what the host sends unasked, not all it will ever send.
        /// </summary>
        public void Want(string key)
        {
            if (!string.IsNullOrWhiteSpace(key))
                _wanted.Enqueue(key.Trim());
        }

        public void PublishWanted()
        {
            while (_wanted.TryDequeue(out string key))
                Publish(key);
        }

        public void PublishTheme()
        {
            foreach (string key in DecalThemeImages.Keys)
                Publish(key);
        }

        public void PublishImagesNamedIn(OverlayState state)
        {
            if (state == null)
                return;

            foreach (OverlayWindow window in state.Windows)
            {
                if (window?.View == null)
                    continue;

                Publish(window.View.Icon);
                foreach (OverlayTitleButton button in window.View.TitleButtons)
                {
                    Publish(button?.Image);
                    Publish(button?.ImageDown);
                }

                Walk(window.View.Root);
            }
        }

        private void Walk(OverlayViewControl control)
        {
            if (control == null)
                return;

            Publish(control.Image);

            foreach (OverlayViewRow row in control.Rows)
            {
                foreach (OverlayViewCell cell in row.Cells)
                    Publish(cell?.Image);
            }

            foreach (OverlayViewControl child in control.Children)
                Walk(child);

            foreach (OverlayViewPage page in control.Pages)
                Walk(page?.Content);
        }

        private void Publish(string key)
        {
            // A plain colour - VVS's ACImage(Color), which Virindi HUDs used for its title
            // icons - is drawn by the overlay itself, so there is nothing to send.
            if (string.IsNullOrEmpty(key) || key.StartsWith(ColourPrefix, StringComparison.OrdinalIgnoreCase) || !_tried.Add(key))
                return;

            if (!_catalog.TryGet(key, out RgbaImage image, out string error))
            {
                Missing++;
                _onMissing?.Invoke($"The overlay will draw {key} as a stand-in: {error}.");
                return;
            }

            bool sent = _server.PublishImage(new OverlayImage
            {
                Key = key,
                Width = image.Width,
                Height = image.Height,
                Rgba = Convert.ToBase64String(image.Rgba),
            });

            if (sent)
                Published++;
            else
                Missing++;
        }
    }
}
