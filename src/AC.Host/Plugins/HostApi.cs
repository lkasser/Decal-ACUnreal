using System;

namespace AC.Host.Plugins
{
    /// <summary>
    /// The version of the contract in this assembly.
    /// </summary>
    /// <remarks>
    /// A plugin is compiled against <see cref="IHost"/>, and <see cref="IHost"/> grows.
    /// Without a declared version, a plugin built against an older host loads
    /// successfully and then throws <c>MissingMethodException</c> the first time the
    /// host calls something it does not have - which, for a host that injects into a
    /// running game, happens in the worst possible place at the worst possible time.
    ///
    /// So the version is declared, checked before anything is instantiated, and a
    /// mismatch is a refusal with a sentence explaining what to do. A plugin that will
    /// not work should say so while nothing is at stake.
    ///
    /// <para>
    /// Bump <see cref="Version"/> whenever a member is added to <see cref="IHost"/>,
    /// <see cref="IGameActions"/> or <see cref="IPlugin"/>. Raise
    /// <see cref="MinimumSupported"/> only when an older plugin genuinely cannot work -
    /// adding a member does not break a plugin that never calls it, so most additions
    /// leave the minimum alone.
    /// </para>
    /// </remarks>
    public static class HostApi
    {
        /// <summary>
        /// The current contract version.
        /// </summary>
        /// <remarks>
        /// 1: the contract as of the first version to declare one - world, character,
        /// actions including movement and casting, loot decisions, the combat and
        /// enchantment events, and ShowInGame.
        ///
        /// 2: adds LoadSettings, typed and persisted per plugin, and IOverlayPanels. A
        /// plugin built against 1 still works, which is why the minimum stays at 1.
        ///
        /// 3: adds ICharacterView.SelectedId. Not one of the three interfaces the rule
        /// names, but reached only through IHost, and a plugin that read it from an older
        /// host would fail the same way.
        ///
        /// 4: adds IHost.Input, the game's movement keys held down through the overlay -
        /// the only way to move a character whose client decides where it is. Acting also
        /// became a switch the player throws while the session runs, so
        /// IGameActions.IsAvailable can now change mid-session.
        ///
        /// 5: adds fighting - melee and missile attacks, cancelling an attack, untargeted
        /// casting and wielding to IGameActions, and the AttackFinished, DamageDealt,
        /// TargetEvaded and AttackCommenced events to IHost.
        ///
        /// 6: adds what a caster needs to know about itself and its spells. On
        /// ICharacterView: the Spellbook and KnowsSpell, LastCastSpellId, and
        /// GetAttributeCurrent, GetSkillCurrent and GetVitalMaximum - the buffed values the
        /// server works with, which it never sends. On IGameData: GetSpell, Spells,
        /// GetSpellsInCategory, FindSpell, GetComponent and TryGetVitalFormula, the client's
        /// spell, component and vital tables. Like 3, reached only through IHost.
        ///
        /// 7: adds IGameActions.StackableMergeAsync, merging one stack into another, and
        /// SalvageAsync, salvaging with an Ultimate Salvaging Tool; and
        /// ICharacterView.Fellowship, the fellowship with its members' vitals - what Virindi
        /// Tank's stacking, salvaging and helping of fellows need.
        ///
        /// 8: adds IHost.RunChatCommand - a line run as though typed, reaching other plugins'
        /// commands and the game client's own server commands - and the PortalSpaceChanged,
        /// VendorChanged and Died events; ICharacterView.InPortalSpace and OpenVendorId; and
        /// IChatCommands.CommandWords, which has a default so older plugins need nothing.
        ///
        /// 9: adds IHost.MessageSeen, every game message either way as its opcode and bytes -
        /// what Decal plugins' ServerDispatch and ClientDispatch are made from - and
        /// IHost.LoggedOff, the character leaving the world, which came in without a number.
        ///
        /// 10: adds IHost.MoveRefused, the server turning down a move of an item
        /// (InventoryServerSaveFailed) - what a looter needs to try a pick-up again at once - and
        /// WorldObject.ArrivalOrder, the order a container's contents came in, reached through
        /// IHost like 3; and IHost.ShowInGame(text, chatType), a line shown exactly as given in
        /// a chat type the plugin names - its own prefix, its own colour, as Decal's AddChatText
        /// - where ShowInGame(text) is the host's own line, now "[Decal] " rather than "[VT] ".
        ///
        /// 11: adds what Decal's character filter answers and the host had not kept - on
        /// ICharacterView the CharacterOptions and CharacterOptions2, the Shortcuts and the
        /// SpellBars; on IWorldView the ServerPopulation, AccountName and AccountCharacters; and
        /// IGameData.GetLevelExperience, the client's experience table, which has a default so
        /// another IGameData needs nothing. A session carried on now raises MessageSeen again for
        /// the messages its world was built from, before the first one relayed.
        /// </remarks>
        public const int Version = 11;

        /// <summary>
        /// The oldest contract this host will still load a plugin for.
        /// </summary>
        public const int MinimumSupported = 1;
    }

    /// <summary>
    /// Declares the contract version a plugin assembly was built against.
    /// </summary>
    /// <remarks>
    /// Written once per plugin assembly, referring to the host's own constant so it
    /// records what the plugin actually compiled against rather than what its author
    /// believed:
    /// <code>
    /// [assembly: PluginApi(HostApi.Version)]
    /// </code>
    /// An assembly without this is not loaded, because there is no way to tell a plugin
    /// that predates the check from one that will fail halfway through a session.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
    public sealed class PluginApiAttribute : Attribute
    {
        public PluginApiAttribute(int version)
        {
            Version = version;
        }

        /// <summary>The value of <see cref="HostApi.Version"/> at the time of building.</summary>
        public int Version { get; }
    }
}
