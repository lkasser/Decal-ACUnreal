using System;
using System.Drawing;
using System.Globalization;
using AC.Host.Plugins.Views;

namespace VirindiViewService
{
    /// <summary>
    /// An image as VVS drew one: a picture from the client's portal file, a solid colour, or
    /// nothing.
    /// </summary>
    /// <remarks>
    /// The overlay draws portal images by id, so that is what an ACImage carries to it. The
    /// bitmap constructors are left out: they need System.Drawing's GDI+ images, which a
    /// plugin host on .NET does not carry, and an image the overlay could never draw is
    /// better missing at build time than blank at run time.
    /// </remarks>
    public class ACImage : IDisposable
    {
        public enum eACImageDrawOptions
        {
            DrawStretch = 0,
            DrawTiled = 1,
        }

        [Flags]
        public enum eACIconHighlight
        {
            None = 0,
            Enchanted = 1,
            Healing = 4,
            Mana = 8,
            HeartyStamina = 16,
            Fire = 32,
            Lightning = 64,
            Cold = 128,
            Acid = 256,
            Bludgeon = 512,
            Slash = 1024,
            Pierce = 2048,
        }

        public enum eACImageUnderlyingType
        {
            PortalImage = 0,
            PortalImageWithSourceRect = 1,
            Bitmap = 2,
            SolidColor = 3,
            ThemeElement = 4,
            Blank = 5,
        }

        public ACImage()
        {
            ImageDataType = eACImageUnderlyingType.Blank;
        }

        public ACImage(int portalfile)
            : this(portalfile, eACImageDrawOptions.DrawStretch)
        {
        }

        public ACImage(int portalfile, bool noborder)
            : this(portalfile, eACImageDrawOptions.DrawStretch)
        {
        }

        public ACImage(int portalfile, eACImageDrawOptions opt)
        {
            PortalImageID = portalfile;
            DrawOptions = opt;
            ImageDataType = portalfile != 0 ? eACImageUnderlyingType.PortalImage : eACImageUnderlyingType.Blank;
        }

        public ACImage(int portalfile, bool noborder, eACImageDrawOptions opt)
            : this(portalfile, opt)
        {
        }

        public ACImage(int portalfile, bool noborder, eACImageDrawOptions opt, Rectangle pSourceRect)
            : this(portalfile, opt)
        {
            PortalImageSourceRect = pSourceRect;
            ImageDataType = eACImageUnderlyingType.PortalImageWithSourceRect;
        }

        public ACImage(int portalfile, eACIconHighlight borderhighlight, eACImageDrawOptions opt)
            : this(portalfile, opt)
        {
        }

        public ACImage(int portalfile, int bordermaskportalfile, eACImageDrawOptions opt)
            : this(portalfile, opt)
        {
        }

        public ACImage(int portalfile, Color BorderColor, eACImageDrawOptions opt)
            : this(portalfile, opt)
        {
        }

        public ACImage(Color c)
        {
            SolidColor = c;
            ImageDataType = eACImageUnderlyingType.SolidColor;
        }

        /// <summary>A theme's named element. Every window is drawn in the Decal theme, so this is blank.</summary>
        public ACImage(HudView p, string pThemeElement)
        {
            ImageDataType = eACImageUnderlyingType.ThemeElement;
        }

        public eACImageUnderlyingType ImageDataType { get; }

        public int PortalImageID { get; }

        public Rectangle PortalImageSourceRect { get; }

        public Color SolidColor { get; }

        public eACImageDrawOptions DrawOptions { get; }

        public bool CachedBMPTextureExists => false;

        public bool AutoRegenerateCachedTexture { get; set; }

        public static implicit operator ACImage(int p) => new ACImage(p);

        /// <summary>An icon from a DLL's resources, which cannot be drawn here: a blank image.</summary>
        public static ACImage FromIconLibrary(int icon, string iconlibrary) => new ACImage();

        /// <summary>An icon from a module; a module of zero means the portal file, as it did to Decal.</summary>
        public static ACImage FromIconLibrary(int icon, int iconlibrary) => iconlibrary == 0 ? new ACImage(icon) : new ACImage();

        public ACImage Clone()
            => ImageDataType switch
            {
                eACImageUnderlyingType.SolidColor => new ACImage(SolidColor),
                eACImageUnderlyingType.Blank or eACImageUnderlyingType.ThemeElement => new ACImage(),
                _ => new ACImage(PortalImageID, DrawOptions),
            };

        public void GenerateCachedBMPTexture()
        {
        }

        public void ClearCachedBMPTexture()
        {
        }

        public void Dispose()
        {
        }

        /// <summary>The image key the host's views use: a portal image's, or empty for anything else.</summary>
        internal string ToImageKey()
            => ImageDataType is eACImageUnderlyingType.PortalImage or eACImageUnderlyingType.PortalImageWithSourceRect
                ? ViewImages.Portal(unchecked((uint)PortalImageID))
                : string.Empty;

        internal static ACImage FromImageKey(string imageKey)
        {
            const string prefix = "portal:";
            if (imageKey != null
                && imageKey.StartsWith(prefix, StringComparison.Ordinal)
                && uint.TryParse(imageKey.AsSpan(prefix.Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id))
            {
                return new ACImage(unchecked((int)id));
            }

            return new ACImage();
        }
    }
}
