///////////////////////////////////////////////////////////////////////////////
// File: LootPluginBase.cs
//
// The loot-plugin contract: what the host calls on a plugin, what a plugin may
// call back, and the decision type it returns.
//
// Clean-room reconstruction of the uTank2.LootPlugins surface that VTClassic
// consumes, derived solely from its call sites in virindi_public r162.
///////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

namespace uTank2.LootPlugins
{
    /// <summary>
    /// What the host should do with an item. Every accessor mints a fresh instance
    /// rather than handing out a shared one, because the caller stamps
    /// <see cref="RuleName"/> onto the decision it received.
    /// </summary>
    public sealed class LootAction
    {
        private LootAction(eLootActionKind kind, int keepUpToCount)
        {
            Kind = kind;
            KeepUpToCount = keepUpToCount;
        }

        public eLootActionKind Kind { get; }

        /// <summary>
        /// For <see cref="eLootActionKind.KeepUpTo"/>, the total quantity to hold.
        /// Zero for every other kind.
        /// </summary>
        public int KeepUpToCount { get; }

        /// <summary>
        /// Name of the rule that produced this decision, for the host's log. Set by
        /// the plugin after classification.
        /// </summary>
        public string RuleName { get; set; } = string.Empty;

        // As Virindi Tank's own LootAction answered other plugins - Mag-Tools' looter and item
        // info, Virindi Item Tool - which read the decision by these rather than by its kind.

        public bool IsNoLoot => Kind == eLootActionKind.NoLoot;

        public bool IsKeep => Kind == eLootActionKind.Keep;

        public bool IsSalvage => Kind == eLootActionKind.Salvage;

        public bool IsSell => Kind == eLootActionKind.Sell;

        public bool IsKeepUpTo => Kind == eLootActionKind.KeepUpTo;

        /// <summary>The decision's number: for Keep Up To, the count to hold.</summary>
        public int Data1 => KeepUpToCount;

        public static LootAction NoLoot => new LootAction(eLootActionKind.NoLoot, 0);

        public static LootAction Keep => new LootAction(eLootActionKind.Keep, 0);

        public static LootAction Salvage => new LootAction(eLootActionKind.Salvage, 0);

        public static LootAction Sell => new LootAction(eLootActionKind.Sell, 0);

        public static LootAction GetKeepUpTo(int count)
            => new LootAction(eLootActionKind.KeepUpTo, count);

        public override string ToString()
            => Kind == eLootActionKind.KeepUpTo
                ? $"KeepUpTo({KeepUpToCount})"
                : Kind.ToString();
    }

    public enum eLootActionKind
    {
        NoLoot = 0,
        Keep = 1,
        Salvage = 2,
        Sell = 3,
        KeepUpTo = 4,
    }

    /// <summary>Plugin identity returned from <see cref="LootPluginBase.Startup"/>.</summary>
    public sealed class LootPluginInfo
    {
        public LootPluginInfo(string profileExtension)
        {
            ProfileExtension = profileExtension;
        }

        /// <summary>
        /// Profile file extension the plugin owns, without a leading dot
        /// (VTClassic returns <c>"utl"</c>).
        /// </summary>
        public string ProfileExtension { get; }
    }

    /// <summary>
    /// Services the host offers a loaded plugin. Chat output is the only channel
    /// VTClassic uses; <paramref name="color"/> and <paramref name="window"/> are
    /// the retail client's chat colour and target window ids.
    /// </summary>
    public interface ILootPluginHost
    {
        void AddChatText(string text);

        void AddChatText(string text, int color);

        void AddChatText(string text, int color, int window);
    }

    /// <summary>
    /// Character and inventory state that loot rules can condition on. This is the
    /// one part of the contract that is genuinely client-specific: the retail
    /// implementation reads Decal's character and world filters, and a modern
    /// client supplies its own.
    /// </summary>
    public interface IGameStateProvider
    {
        int CharacterLevel { get; }

        /// <summary>
        /// Base and buffed values of one skill. <paramref name="skillId"/> uses the
        /// game's skill ids. Returns false when the character has no such skill.
        /// </summary>
        bool TryGetSkill(int skillId, out SkillValues values);

        /// <summary>
        /// Items held directly in the character's main pack. Used to count free
        /// slots, so equipped items and sub-containers must be included here and
        /// are filtered by the caller.
        /// </summary>
        IEnumerable<IMainPackItem> EnumerateMainPackItems();
    }

    public readonly struct SkillValues
    {
        public SkillValues(int baseValue, int buffedValue)
        {
            Base = baseValue;
            Buffed = buffedValue;
        }

        public int Base { get; }

        public int Buffed { get; }
    }

    /// <summary>The slice of an inventory item that free-slot counting needs.</summary>
    public interface IMainPackItem
    {
        ObjectClass ObjectClass { get; }

        /// <summary>Non-zero when the item is equipped or wielded.</summary>
        int EquippedSlots { get; }
    }

    /// <summary>Optional plugin behaviours the host queries for.</summary>
    [Flags]
    public enum eLootPluginExtraOption
    {
        None = 0,

        /// <summary>
        /// The plugin has no in-client editor, so the host should not offer the
        /// "open editor" checkbox.
        /// </summary>
        HideEditorCheckbox = 1,
    }

    /// <summary>
    /// Implemented by a plugin that can decide which salvage bags to combine.
    /// </summary>
    public interface ILootPluginCapability_SalvageCombineDecision2
    {
        /// <summary>
        /// Indices into <paramref name="availablebags"/> that should be combined,
        /// or an empty list for none.
        /// </summary>
        List<int> ChooseBagsToCombine(List<GameItemInfo> availablebags);
    }

    /// <summary>Implemented by a plugin that reports optional behaviours.</summary>
    public interface ILootPluginCapability_GetExtraOptions
    {
        eLootPluginExtraOption GetExtraOptions();
    }

    /// <summary>
    /// Base class for a loot plugin. The host constructs one instance, calls
    /// <see cref="Startup"/>, then drives profile loading and per-item decisions.
    /// </summary>
    public abstract class LootPluginBase
    {
        /// <summary>Host services. Assigned by the host before <see cref="Startup"/>.</summary>
        public ILootPluginHost Host { get; set; } = NullLootPluginHost.Instance;

        /// <summary>
        /// Character and inventory state. Assigned by the host before
        /// <see cref="Startup"/>.
        /// </summary>
        public IGameStateProvider GameState { get; set; } = NullGameStateProvider.Instance;

        public abstract LootPluginInfo Startup();

        public abstract void Shutdown();

        public abstract void LoadProfile(string filename, bool newprofile);

        public abstract void UnloadProfile();

        public abstract void OpenEditorForProfile();

        public abstract void CloseEditorForProfile();

        /// <summary>
        /// Whether the host should spend an identify on this item before asking for
        /// a decision. Called before appraisal data is available.
        /// </summary>
        public abstract bool DoesPotentialItemNeedID(GameItemInfo item);

        public abstract LootAction GetLootDecision(GameItemInfo item);
    }

    /// <summary>Discards chat output, so an unhosted plugin still runs.</summary>
    public sealed class NullLootPluginHost : ILootPluginHost
    {
        public static readonly NullLootPluginHost Instance = new NullLootPluginHost();

        private NullLootPluginHost()
        {
        }

        public void AddChatText(string text)
        {
        }

        public void AddChatText(string text, int color)
        {
        }

        public void AddChatText(string text, int color, int window)
        {
        }
    }

    /// <summary>
    /// A character with no skills, no level and an empty pack. Rules that depend on
    /// character state evaluate as if nothing is known, which keeps profile loading
    /// and item classification usable outside a running client (tests, the editor).
    /// </summary>
    public sealed class NullGameStateProvider : IGameStateProvider
    {
        public static readonly NullGameStateProvider Instance = new NullGameStateProvider();

        private NullGameStateProvider()
        {
        }

        public int CharacterLevel => 0;

        public bool TryGetSkill(int skillId, out SkillValues values)
        {
            values = default;
            return false;
        }

        public IEnumerable<IMainPackItem> EnumerateMainPackItems()
            => Array.Empty<IMainPackItem>();
    }
}
