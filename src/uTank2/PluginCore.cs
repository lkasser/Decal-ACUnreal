using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using uTank2.LootPlugins;

// The loot types are the host's own (UTank2.Abstractions), which its VTClassic is built on: a
// plugin that names them in "uTank2" is given those.
[assembly: TypeForwardedTo(typeof(LootAction))]
[assembly: TypeForwardedTo(typeof(eLootActionKind))]
[assembly: TypeForwardedTo(typeof(GameItemInfo))]
[assembly: TypeForwardedTo(typeof(LootPluginBase))]
[assembly: TypeForwardedTo(typeof(LootPluginInfo))]
[assembly: TypeForwardedTo(typeof(ILootPluginHost))]
[assembly: TypeForwardedTo(typeof(IGameStateProvider))]
[assembly: TypeForwardedTo(typeof(SkillValues))]
[assembly: TypeForwardedTo(typeof(IMainPackItem))]
[assembly: TypeForwardedTo(typeof(eLootPluginExtraOption))]
[assembly: TypeForwardedTo(typeof(ILootPluginCapability_SalvageCombineDecision2))]
[assembly: TypeForwardedTo(typeof(ILootPluginCapability_GetExtraOptions))]
[assembly: TypeForwardedTo(typeof(StringValueKey))]
[assembly: TypeForwardedTo(typeof(IntValueKey))]
[assembly: TypeForwardedTo(typeof(DoubleValueKey))]
[assembly: TypeForwardedTo(typeof(ObjectClass))]
[assembly: TypeForwardedTo(typeof(uTank2.MySpell))]

namespace uTank2
{
    /// <summary>
    /// Virindi Tank as other Decal plugins called it: <see cref="PC"/>, and the loot plugin's
    /// answers about an item - whether it needs an ID first, and what the loot rules make of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This host's Virindi Tank is a plugin of the host's, not Decal's utank2-i.dll, and does not
    /// offer its loot rules to Decal plugins. Without an assembly by this name, Mag-Tools' looter -
    /// which the player's settings start on every chest and their own corpse - failed to compile
    /// its every look at an open container, ten times a second for as long as the host ran, each
    /// failure written to its error log and said in the chat.
    /// </para>
    /// <para>
    /// So this answers as Virindi Tank did with no loot profile loaded: no item needs an ID, and no
    /// rule keeps anything. Mag-Tools' looter then says there is nothing to loot and stops, though
    /// it still empties the player's own corpse, which it does without asking any rule; its item
    /// info names no rule. The real world tracker's items are not offered: an item asked for by id
    /// is not there.
    /// </para>
    /// </remarks>
    public class PluginCore
    {
        /// <summary>The running Virindi Tank, as plugins found it.</summary>
        public static PluginCore PC = new PluginCore();

        /// <summary>What a loot decision asked for later is told: the item, the decision, and whether one was made.</summary>
        public delegate void delFLootPluginClassifyCallback(int obj, LootAction result, bool getsuccess);

        /// <summary>Whether the loot rules need the item identified before they can decide: never, with none.</summary>
        public bool FLootPluginQueryNeedsID(int id) => false;

        /// <summary>The loot rules' decision on an item: nothing keeps it.</summary>
        public LootAction FLootPluginClassifyImmediate(int id) => LootAction.NoLoot;

        /// <summary>The decision, told at once: nothing keeps it.</summary>
        public void FLootPluginClassifyCallback(int id, delFLootPluginClassifyCallback callback) => callback?.Invoke(id, LootAction.NoLoot, true);

        /// <summary>Virindi Tank's own record of an item, by id. Not offered here: none.</summary>
        public GameItemInfo FWorldTracker_GetWithID(int id) => null;

        /// <summary>A vendor's item by its template. Not offered here: none.</summary>
        public GameItemInfo FWorldTracker_GetWithVendorObjectTemplateID(int id) => null;

        /// <summary>Virindi Tank's own record of what is in a container. Not offered here: nothing.</summary>
        public ReadOnlyCollection<GameItemInfo> FWorldTracker_GetInContainer(int id) => new List<GameItemInfo>().AsReadOnly();

        /// <summary>
        /// Virindi Item Tool's way of switching off Virindi Tank's stacking and cramming while it
        /// moves things. This host's Virindi Tank takes no such word from Decal plugins: nothing.
        /// </summary>
        public void PushStackCramSettings(bool stack, bool cram)
        {
        }

        /// <summary>Their settings back, after <see cref="PushStackCramSettings"/>: nothing.</summary>
        public void PopStackCramSettings()
        {
        }
    }
}
