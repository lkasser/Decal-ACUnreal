using System.Drawing;
using Decal.Adapter.Hosting;

namespace VirindiHUDs.UIs
{
    /// <summary>
    /// Virindi HUDs' Status HUD as plugins feed it: a row each, by plugin and entry, with a value
    /// and a colour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Virindi HUDs is hosted here by Virindi Tank, which draws the Status HUD; the real Virindi HUDs
    /// is never loaded. A Decal plugin built against it is given this instead, and each row it puts
    /// here goes, through the running Decal, to the host and on to the Status HUD
    /// (<see cref="AC.Host.Plugins.IHost.UpdateStatusRow"/>), where the player picks the rows to
    /// show as in Virindi HUDs' own list. Mag-Tools puts fourteen there at login - Mana, Comps Time,
    /// Net Profit, the DPS rates, Players, Monsters, Pack Slots, ID Queue - and keeps them up to date
    /// every second; without an assembly by this name its HUD failed to start and it said so in chat.
    /// </para>
    /// <para>
    /// A row given while no Decal is running - none can be, since plugins run inside one - goes
    /// nowhere.
    /// </para>
    /// </remarks>
    public static class StatusModel
    {
        /// <summary>A row, in white.</summary>
        public static void UpdateEntry(string pPluginName, string pEntryName, string pValue)
            => Update(pPluginName, pEntryName, pValue, 0xFFFFFFFF);

        /// <summary>A row in a colour of the plugin's.</summary>
        public static void UpdateEntry(string pPluginName, string pEntryName, string pValue, Color pColor)
            => Update(pPluginName, pEntryName, pValue, unchecked((uint)pColor.ToArgb()));

        private static void Update(string plugin, string entry, string value, long colour)
        {
            if (plugin == null || entry == null)
                return;

            DecalRuntime.Current?.Host.UpdateStatusRow(plugin, entry, value ?? string.Empty, colour);
        }
    }
}
