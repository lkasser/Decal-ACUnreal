using System.Drawing;
using Decal.Adapter.Hosting;

namespace VirindiViewService.Controls
{
    /// <summary>
    /// A control a plugin draws itself: VVS handed it the window's surface on every frame, as a
    /// <see cref="DxTexture"/>, through <see cref="Draw"/>. Integrator2's map is one, and Virindi
    /// HUDs' bars.
    /// </summary>
    /// <remarks>
    /// The overlay draws windows from the controls the host describes, and has no surface to
    /// lend a plugin, so in a view this is an empty space of its size, and <see cref="Draw"/> is
    /// never raised by the host; the first plugin to put one in a view is told so in the log.
    /// What a plugin sets on it is kept, and <see cref="DrawNow"/>, called with a texture of the
    /// plugin's own, runs its drawing as VVS ran it.
    /// </remarks>
    public class HudEmulator : HudControl
    {
        public HudEmulator()
        {
        }

        public delegate void delClearRegion(DxTexture ClearTarget, Rectangle ClearRegion);

        public delegate void delDraw(HudEmulator Caller, DxTexture Target, Rectangle TargetRegion, delClearRegion dClearOp);

        /// <summary>The plugin's drawing. Raised only by <see cref="DrawNow"/>: the host has no frames to draw it in.</summary>
        public event delDraw Draw;

        /// <summary>
        /// Whether the surface is left ready to draw on when <see cref="Draw"/> is raised; when it
        /// is not, VVS ended the frame's rendering first and began it again after.
        /// </summary>
        public bool LeaveSurfaceInBeginRender { get; set; }

        /// <summary>Where the plugin's drawing goes: the control's place in its view.</summary>
        public Rectangle DrawCommandRegion => ClipRegion;

        /// <summary>
        /// Raises <see cref="Draw"/> on the given surface, clipped to the control, as VVS did on
        /// each frame; with nothing listening, clears the control's place instead.
        /// </summary>
        public override void DrawNow(DxTexture iSavedTarget)
        {
            if (!CanDraw || !Visible || iSavedTarget == null)
                return;

            base.DrawNow(iSavedTarget);
            Rectangle region = DrawCommandRegion;
            delDraw draw = Draw;
            if (draw == null)
            {
                ClearRegion(iSavedTarget, region);
                return;
            }

            if (!LeaveSurfaceInBeginRender)
                iSavedTarget.EndRender();

            iSavedTarget.PushClipRect(region);
            try
            {
                draw(this, iSavedTarget, region, ClearRegion);
            }
            finally
            {
                iSavedTarget.PopClipRect();
                if (!LeaveSurfaceInBeginRender)
                    iSavedTarget.BeginRender();
            }
        }

        private protected override void OnAttached()
            => DecalRuntime.Current?.NoteUnsupported("HudEmulator", "a control a plugin draws itself is shown as an empty space");

        /// <summary>What a plugin's drawing calls to blank its place: VVS filled it with the theme's background.</summary>
        private static void ClearRegion(DxTexture target, Rectangle region) => target?.Clear(region);
    }
}
