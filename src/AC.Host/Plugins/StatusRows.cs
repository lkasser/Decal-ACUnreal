namespace AC.Host.Plugins
{
    /// <summary>
    /// A plugin that shows a status HUD other plugins put rows on - Virindi HUDs' Status HUD, which
    /// Virindi Tank hosts here - and takes the rows they give it (<see cref="IHost.UpdateStatusRow"/>).
    /// </summary>
    /// <remarks>
    /// Under Decal, Virindi HUDs' StatusModel was a static any plugin could call: Mag-Tools put its
    /// mana time, damage rates and free pack slots there, Virindi Reporter its experience rates.
    /// Here the HUD belongs to one plugin and the rows come from others, so the host carries them.
    /// Optional, like the other plugin interfaces; called on the game thread.
    /// </remarks>
    public interface IStatusRows
    {
        /// <summary>
        /// A row given or changed: whose it is, which entry, what it says now, and its colour as
        /// 0xAARRGGBB - white for a plugin that names none. Rows are never taken away while
        /// running, as Virindi HUDs never took them away.
        /// </summary>
        void UpdateStatusRow(string plugin, string entry, string value, long colour);
    }
}
