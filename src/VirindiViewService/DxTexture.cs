using System;
using System.Drawing;
using Decal.Adapter.Hosting;
using Microsoft.DirectX;

namespace VirindiViewService
{
    /// <summary>
    /// A picture a plugin draws itself, in code: VVS's Direct3D texture, which a custom-drawn
    /// control (<see cref="Controls.HudEmulator"/>) painted on and copied to its window.
    /// </summary>
    /// <remarks>
    /// Nothing here draws into the client's 3D view, and the overlay draws windows from the
    /// controls the host describes rather than from pictures a plugin paints, so a texture is
    /// its size and whether it has been disposed - which plugins read, to lay out and to know
    /// when to make a new one - and every drawing call is taken and does nothing. Nothing is
    /// thrown for calls out of order (text before BeginRender, say): VVS did, but with nothing
    /// drawn, the order harms nothing.
    ///
    /// <para>
    /// Left out: Underlying, DXDrawUserPrimitives and DXSetRenderState, which hand a plugin
    /// Direct3D itself - there is no device to hand it - and IRenderTarget with the overloads
    /// taking one, which VVS's own drawing went through and neither Integrator2 nor Virindi HUDs,
    /// the plugins known to draw for themselves, use. The first plugin to make a texture is
    /// told, once, that what it draws is not shown.
    /// </para>
    /// </remarks>
    public class DxTexture : IDisposable
    {
        private readonly Size _size;

        public DxTexture(Size pSize)
        {
            // What VVS refused, as it refused it.
            if (pSize.Width <= 0 || pSize.Height <= 0)
                throw new ArgumentException();

            _size = pSize;
            NoteNotShown();
        }

        /// <summary>A texture holding a picture; it takes the picture's size, and nothing else of it.</summary>
        public DxTexture(Bitmap bmp)
            : this(SizeOf(bmp))
        {
        }

        /// <summary>The same, with one colour to be left transparent.</summary>
        public DxTexture(Bitmap pbmp, Color colorkey)
            : this(SizeOf(pbmp))
        {
        }

        public int Width => _size.Width;

        public int Height => _size.Height;

        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;

        public void BeginRender()
        {
        }

        public void BeginRender(bool AlphaTestEnable, bool SeparateAlphaEnable, int SourceBlendAlpha, int DestinationBlendAlpha, int BlendOperation)
        {
        }

        public void EndRender()
        {
        }

        public void BeginText(string FontName, int lHeight, int lWeight, bool bItalic)
        {
        }

        public void BeginText(string FontName, float lHeight, int lWeight, bool bItalic, int shadowsize, int shadowalpha)
        {
        }

        public void EndText()
        {
        }

        public void WriteText(string Text, Color lColor, Color lShadowColor, WriteTextFormats lFormat, Rectangle Region)
        {
        }

        public void WriteText(string Text, Color lColor, WriteTextFormats lFormat, Rectangle Region)
        {
        }

        /// <summary>The room text would take: <see cref="Service.MeasureText"/>'s estimate, as VVS answered with its own.</summary>
        public Rectangle MeasureText(string Text, WriteTextFormats Format, string FontName, float Height, int Weight, bool Italic, int shadowsize)
            => Service.MeasureText(Text, Format, FontName, Height, Weight, Italic, shadowsize);

        public Rectangle MeasureText(string Text, WriteTextFormats Format, string FontName, int Height, int Weight, bool Italic)
            => Service.MeasureText(Text, Format, FontName, Height, Weight, Italic, 0);

        public virtual void Clear(Rectangle ClearArea)
        {
        }

        public virtual void Clear()
        {
        }

        public void FillUsingRender(Rectangle FillArea, Color fColor)
        {
        }

        public void Fill(Rectangle FillArea, Color fColor)
        {
        }

        public void DrawTexture(DxTexture pTexture, Rectangle pDestRegion)
        {
        }

        public void DrawTexture(DxTexture pTexture, Rectangle pSrcRegion, Rectangle pDestRegion)
        {
        }

        public void DrawTexture(DxTexture pTexture, Rectangle pSrcRegion, Rectangle pDestRegion, int alpha)
        {
        }

        public void DrawTextureTinted(DxTexture pTexture, Rectangle pSrcRegion, Rectangle pDestRegion, int tint)
        {
        }

        public void DrawTextureWithTransform(DxTexture pTexture, Matrix transmat, int tint)
        {
        }

        public void DrawTextureWithTransform(DxTexture pTexture, Rectangle pSrcRegion, Matrix transmat, int tint)
        {
        }

        public void DrawTextureRotated(DxTexture pTexture, Rectangle pSrcRegion, Point pDest, int tint, float angleradians)
        {
        }

        public void TileTexture(DxTexture pTexture, Rectangle destinationArea)
        {
        }

        public void DrawImage(Bitmap bmp, Rectangle DestRegion, Color lColorKey)
        {
        }

        public void DrawImageTinted(Bitmap bmp, Rectangle DestRegion, Color lColorKey, int tint)
        {
        }

        public void TileImage(Bitmap bmp, Rectangle DestRegion, Color lColorKey)
        {
        }

        public void DrawPortalImageNoBorder(int lPortalFile, Rectangle DestRegion)
        {
        }

        public void DrawPortalImage(int lPortalFile, Rectangle DestRegion)
        {
        }

        public void DrawPortalImage(int lPortalFile, Rectangle SrcRegion, Rectangle DestRegion)
        {
        }

        public void DrawPortalImage(int lPortalFile, bool noborder, Rectangle DestRegion)
        {
        }

        public void DrawPortalImage(int lPortalFile, bool noborder, Rectangle SrcRegion, Rectangle DestRegion)
        {
        }

        public void DrawPortalImageEx(int lPortalFile, int lAlpha, Rectangle SrcRegion, Rectangle DestRegion)
        {
        }

        public void DrawPortalImageEx(int lPortalFile, bool noborder, int lAlpha, Rectangle SrcRegion, Rectangle DestRegion)
        {
        }

        public void TilePortalImage(int lPortalFile, Rectangle DestRegion)
        {
        }

        public void TilePortalImage(int lPortalFile, bool noborder, Rectangle DestRegion)
        {
        }

        public void PushClipRect(Rectangle ClipRect)
        {
        }

        public void PopClipRect()
        {
        }

        public void FlushSprite()
        {
        }

        public void BeginUserDrawOperation()
        {
        }

        public void EndUserDrawOperation()
        {
        }

        public void DrawLine(PointF p1, PointF p2, Color color, float width)
        {
        }

        public void DrawLines(Vector2[] pt1, Vector2[] pt2, Matrix transform, Color[] colors, float width)
        {
        }

        /// <summary>
        /// A picture's size. Only read: the picture is the plugin's, made with System.Drawing,
        /// which is there for it on Windows, where Decal plugins run.
        /// </summary>
#pragma warning disable CA1416
        private static Size SizeOf(Bitmap bmp) => (bmp ?? throw new ArgumentNullException(nameof(bmp))).Size;
#pragma warning restore CA1416

        private static void NoteNotShown()
            => DecalRuntime.Current?.NoteUnsupported("DxTexture", "what a plugin draws itself is not shown");
    }
}
