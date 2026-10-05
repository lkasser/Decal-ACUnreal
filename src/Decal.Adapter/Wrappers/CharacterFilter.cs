using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using AC.Host.World;
using Decal.Adapter.Hosting;

namespace Decal.Adapter.Wrappers
{
    /// <summary>
    /// Decal's character filter: the logged-in character's name, attributes, skills, vitals,
    /// experience, spellbook, options and enchantments, and the events for them changing.
    /// Answered from the host's character model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every member the host has the data for answers from it: the login's description - its
    /// properties, attributes, skills, spellbook, enchantments, options, shortcuts and spell
    /// bars, kept current by the server's updates and the client's own changes - the server's
    /// name and population, and the account and its characters from the character list. What
    /// needs the client's tables - the experience the next level needs - is worked out from them
    /// where the host has them.
    /// </para>
    /// <para>
    /// The allegiance is not decoded, so the monarch, patron and allegiance are the empty ones
    /// Decal gave a character in none, there are no vassals, and the monarch's followers are 0.
    /// </para>
    /// </remarks>
    public class CharacterFilter : DisposableByRefObject
    {
        /// <summary>The spell that is vitae, whose enchantment says how much of it there is.</summary>
        private const int VitaeSpell = 666;

        /// <summary>
        /// The character properties that are augmentations, as Decal's eAugmentations names them:
        /// what <see cref="Augmentations"/> looks among.
        /// </summary>
        private static readonly int[] AugmentationKeys =
        {
            218, 219, 220, 221, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 234, 235, 236, 237, 238,
            240, 241, 242, 243, 244, 245, 246,
            294, 295, 296, 297, 298, 299, 300, 301, 302, 309, 310, 326,
            328, 329, 330, 331, 332, 333, 334, 335, 336, 337, 338, 339, 340, 341, 342, 343, 344, 365,
        };

        /// <summary>The heritages, by the character's HeritageGroup (188), as the game names them.</summary>
        private static readonly string[] Heritages =
        {
            string.Empty, "Aluvian", "Gharu'ndim", "Sho", "Viamontian", "Umbraen", "Gear Knight", "Tumerok", "Lugian",
            "Empyrean", "Penumbraen", "Undead", "Olthoi", "Olthoi",
        };

        private readonly DecalRuntime _runtime;

        internal CharacterFilter(DecalRuntime runtime)
        {
            _runtime = runtime;
            Vitals = new IndexedCollection<CharFilterIndex, CharFilterVitalType, SkillInfoWrapper>(
                type => new SkillInfoWrapper(this, type), () => new[] { CharFilterVitalType.Health, CharFilterVitalType.Stamina, CharFilterVitalType.Mana });
            Attributes = new IndexedCollection<CharFilterIndex, CharFilterAttributeType, AttributeInfoWrapper>(
                type => new AttributeInfoWrapper(this, type), () => Enum.GetValues<CharFilterAttributeType>());
            Skills = new IndexedCollection<CharFilterIndex, CharFilterSkillType, SkillInfoWrapper>(
                type => new SkillInfoWrapper(this, type), () => Enum.GetValues<CharFilterSkillType>().Where(s => Character.Skills.ContainsKey((uint)s)));
            EffectiveAttribute = new IndexedCollection<CharFilterIndex, CharFilterAttributeType, int>(
                type => Character.GetAttributeCurrent((uint)type), () => Enum.GetValues<CharFilterAttributeType>());
            EffectiveSkill = new IndexedCollection<CharFilterIndex, CharFilterSkillType, int>(
                type => Character.GetSkillCurrent((uint)type), () => Enum.GetValues<CharFilterSkillType>().Where(s => Character.Skills.ContainsKey((uint)s)));
            EffectiveVital = new IndexedCollection<CharFilterIndex, CharFilterVitalType, int>(
                type => VitalMaximum(type), () => new[] { CharFilterVitalType.Health, CharFilterVitalType.Stamina, CharFilterVitalType.Mana });
            Enchantments = new IndexedCollection<CharFilterIndex, int, EnchantmentWrapper>(
                index => index >= 0 && index < Character.Enchantments.Count ? new EnchantmentWrapper(Character.Enchantments.Values.ElementAt(index)) : null,
                () => Enumerable.Range(0, Character.Enchantments.Count));
            Vassals = new IndexedCollection<CharFilterIndex, int, AllegianceInfoWrapper>(_ => null, Array.Empty<int>);
            Characters = new IndexedCollection<CharFilterIndex, int, AccountCharInfo>(
                index => index >= 0 && index < World.AccountCharacters.Count ? new AccountCharInfo(World.AccountCharacters[index], index) : null,
                () => Enumerable.Range(0, World.AccountCharacters.Count));
        }

        internal ICharacterView Character => _runtime.Host.Character;

        private IWorldView World => _runtime.Host.World;

        public int Id => unchecked((int)Character.Id);

        public string Name => Character.Name ?? string.Empty;

        /// <summary>The account's name, as the server's character list gave it.</summary>
        public string AccountName => World.AccountName ?? string.Empty;

        public string Server => World.ServerName ?? string.Empty;

        /// <summary>How many were playing when the server announced itself.</summary>
        public int ServerPopulation => World.ServerPopulation;

        public int Level => Character.Level;

        public int Health => VitalCurrent(CharFilterVitalType.Health);

        public int Stamina => VitalCurrent(CharFilterVitalType.Stamina);

        public int Mana => VitalCurrent(CharFilterVitalType.Mana);

        /// <summary>The heritage: the server's word for it where it sends one (4), else the game's name for the HeritageGroup (188).</summary>
        public string Race
        {
            get
            {
                string said = PropertyString(4);
                if (said.Length > 0)
                    return said;

                int heritage = Property(188);
                return heritage > 0 && heritage < Heritages.Length ? Heritages[heritage] : string.Empty;
            }
        }

        /// <summary>The server's word for it where it sends one (3), else the Gender (113): 1 male, 2 female.</summary>
        public string Gender
        {
            get
            {
                string said = PropertyString(3);
                if (said.Length > 0)
                    return said;

                return Property(113) switch
                {
                    1 => "Male",
                    2 => "Female",
                    _ => string.Empty,
                };
            }
        }

        /// <summary>The template the character was made from (5): "Adventurer", "Bow Hunter"...</summary>
        public string ClassTemplate => PropertyString(5);

        public int Age => Property(125);

        public DateTime Birth => DateTimeOffset.FromUnixTimeSeconds(Property(98)).LocalDateTime;

        /// <summary>
        /// The burden, as a percentage of what the character can carry: the game's capacity of 150
        /// burden for each point of strength, with enchantments, and 30 more for each point for
        /// every Might of the Seventh Mule (230).
        /// </summary>
        public int Burden
        {
            get
            {
                long strength = Character.GetAttributeCurrent((uint)CharFilterAttributeType.Strength);
                long capacity = 150 * strength + 30L * Property(230) * strength;
                return capacity > 0 ? (int)(BurdenUnits * 100L / capacity) : 0;
            }
        }

        /// <summary>What the character carries and wears weighs (5).</summary>
        public int BurdenUnits => Property(5);

        public int Deaths => Property(43);

        public int Rank => Property(30);

        public int Followers => Property(35);

        public int MonarchFollowers => 0;

        public int SkillPoints => Property(24);

        /// <summary>The vitae penalty, in percent: 5 for a character at 95%; 0 without vitae.</summary>
        public int Vitae
        {
            get
            {
                foreach (Enchantment enchantment in Character.Enchantments.Values)
                {
                    if (enchantment.SpellId == VitaeSpell)
                        return (int)Math.Round((1.0 - enchantment.StatModValue) * 100.0);
                }

                return 0;
            }
        }

        /// <summary>The first word of the character's options, CharacterOptions1.</summary>
        public int CharacterOptions => unchecked((int)Character.CharacterOptions);

        /// <summary>The second word of the character's options, CharacterOptions2: fast missiles are 0x10000.</summary>
        public int CharacterOptionFlags => unchecked((int)Character.CharacterOptions2);

        public int LoginStatus => Character.Id != 0 ? 1 : 0;

        /// <summary>All the experience the character has earned (quad property 1).</summary>
        public long TotalXP => Property64(1);

        /// <summary>The experience earned and not yet spent (quad property 2).</summary>
        public long UnassignedXP => Property64(2);

        /// <summary>
        /// The experience still to earn before the next level, from the client's experience table;
        /// 0 at the highest level, or without the client's data.
        /// </summary>
        public long XPToNextLevel
        {
            get
            {
                long? next = Character.Level > 0 ? _runtime.Host.GameData?.GetLevelExperience(Character.Level + 1) : null;
                return next.HasValue ? Math.Max(0, next.Value - TotalXP) : 0;
            }
        }

        /// <summary>A character in no allegiance, as Decal gave one: every field empty.</summary>
        public AllegianceInfoWrapper Monarch => new AllegianceInfoWrapper();

        public AllegianceInfoWrapper Patron => new AllegianceInfoWrapper();

        public AllegianceInfoWrapper Allegiance => new AllegianceInfoWrapper();

        /// <summary>The spells the character knows, by id, lowest first.</summary>
        public ReadOnlyCollection<int> SpellBook
            => new ReadOnlyCollection<int>(Character.Spellbook.Select(id => unchecked((int)id)).OrderBy(id => id).ToList());

        /// <summary>The augmentations the character has, as the property ids Decal's eAugmentations names.</summary>
        public ReadOnlyCollection<int> Augmentations
            => new ReadOnlyCollection<int>(AugmentationKeys.Distinct().Where(key => Property(unchecked((uint)key)) > 0).ToList());

        public IndexedCollection<CharFilterIndex, CharFilterVitalType, SkillInfoWrapper> Vitals { get; }

        public IndexedCollection<CharFilterIndex, CharFilterAttributeType, AttributeInfoWrapper> Attributes { get; }

        public IndexedCollection<CharFilterIndex, CharFilterSkillType, SkillInfoWrapper> Skills { get; }

        /// <summary>Each attribute with its enchantments, as the server uses it.</summary>
        public IndexedCollection<CharFilterIndex, CharFilterAttributeType, int> EffectiveAttribute { get; }

        /// <summary>Each skill as the server uses it: attributes as buffed, augmentations and enchantments.</summary>
        public IndexedCollection<CharFilterIndex, CharFilterSkillType, int> EffectiveSkill { get; }

        /// <summary>Each vital's maximum with its enchantments.</summary>
        public IndexedCollection<CharFilterIndex, CharFilterVitalType, int> EffectiveVital { get; }

        public IndexedCollection<CharFilterIndex, int, EnchantmentWrapper> Enchantments { get; }

        public IndexedCollection<CharFilterIndex, int, AllegianceInfoWrapper> Vassals { get; }

        /// <summary>The account's characters, as the character list gave them.</summary>
        public IndexedCollection<CharFilterIndex, int, AccountCharInfo> Characters { get; }

        public bool IsSpellKnown(int spellId) => Character.KnowsSpell(unchecked((uint)spellId));

        /// <summary>The spells on a spell bar, in their order. Decal let a plugin ask for bars 0 to 6.</summary>
        public ReadOnlyCollection<int> SpellBar(int barNumber)
        {
            if (barNumber < 0 || barNumber > 6)
                throw new ArgumentOutOfRangeException(nameof(barNumber), barNumber, "barNumber must be between 0 and 6 inclusive");

            IReadOnlyList<uint> bar = barNumber < Character.SpellBars.Count ? Character.SpellBars[barNumber] : Array.Empty<uint>();
            return new ReadOnlyCollection<int>(bar.Select(id => unchecked((int)id)).ToList());
        }

        /// <summary>The object on a slot of the shortcut bar, or 0.</summary>
        public int Shortcut(int slot) => Character.Shortcuts.TryGetValue(slot, out uint objectId) ? unchecked((int)objectId) : 0;

        /// <summary>One of the character's own whole-number properties, by the game's id.</summary>
        public int GetCharProperty(int key) => Property(unchecked((uint)key));

        public event EventHandler ActionComplete;

        public event EventHandler LoginComplete;

        public event EventHandler<LoginEventArgs> Login;

        public event EventHandler<ChangeVitalEventArgs> ChangeVital;

        public event EventHandler<ChangeEnchantmentsEventArgs> ChangeEnchantments;

        /// <summary>
        /// The character has left the world. Raised as Authorized - the logoff done - whatever
        /// took it out; the client's own request to log off is not raised.
        /// </summary>
        public event EventHandler<LogoffEventArgs> Logoff;

        /// <summary>A spell learned or forgotten, once the character is in the world.</summary>
        public event EventHandler<SpellbookEventArgs> SpellbookChange;

        /// <summary>The character died, with the server's message to it.</summary>
        public event EventHandler<DeathEventArgs> Death;

        /// <summary>Into portal space - the server teleporting the character - or out of it, the client arrived.</summary>
        public event EventHandler<ChangePortalModeEventArgs> ChangePortalMode;

        /// <summary>The total or the unspent experience changed, once the character is in the world. The amount is the change.</summary>
        public event EventHandler<ChangeExperienceEventArgs> ChangeExperience;

        // What the host does not raise: allegiance and fellowship changes, the bars' and the
        // options' changes one by one, casts. Plugins subscribe to these at startup and must be able to.
#pragma warning disable CS0067
        public event EventHandler<ChangePlayerEventArgs> ChangePlayer;

        public event EventHandler<ChangeFellowshipEventArgs> ChangeFellowship;

        public event EventHandler<ChangeSpellbarEventArgs> ChangeSpellbar;

        public event EventHandler<ChangeShortcutEventArgs> ChangeShortcut;

        public event EventHandler<SettingsEventArgs> ChangeSettings;

        public event EventHandler<ChangeSettingsFlagsEventArgs> ChangeSettingsFlags;

        public event EventHandler<ChangeOptionEventArgs> ChangeOption;

        public event EventHandler<SpellCastEventArgs> SpellCast;

        public event EventHandler<StatusMessageEventArgs> StatusMessage;
#pragma warning restore CS0067

        internal void OnLogin(int id) => _runtime.Raise(Login, this, new LoginEventArgs(id), nameof(Login));

        internal void OnLoginComplete() => _runtime.Raise(LoginComplete, this, nameof(LoginComplete));

        internal void OnLogoff(LogoffEventType type) => _runtime.Raise(Logoff, this, new LogoffEventArgs(type), nameof(Logoff));

        internal void OnActionComplete() => _runtime.Raise(ActionComplete, this, nameof(ActionComplete));

        internal void OnChangeVital(CharFilterVitalType type, int amount)
            => _runtime.Raise(ChangeVital, this, new ChangeVitalEventArgs(type, amount), nameof(ChangeVital));

        internal void OnChangeEnchantments(AddRemoveEventType type, Enchantment enchantment)
            => _runtime.Raise(ChangeEnchantments, this, new ChangeEnchantmentsEventArgs(type, new EnchantmentWrapper(enchantment)), nameof(ChangeEnchantments));

        internal void OnSpellbookChange(AddRemoveEventType type, int spellId)
            => _runtime.Raise(SpellbookChange, this, new SpellbookEventArgs(type, spellId), nameof(SpellbookChange));

        internal void OnDeath(string text) => _runtime.Raise(Death, this, new DeathEventArgs(text), nameof(Death));

        internal void OnChangePortalMode(PortalEventType type)
            => _runtime.Raise(ChangePortalMode, this, new ChangePortalModeEventArgs(type), nameof(ChangePortalMode));

        internal void OnChangeExperience(PlayerXPEventType type, int amount)
            => _runtime.Raise(ChangeExperience, this, new ChangeExperienceEventArgs(type, amount), nameof(ChangeExperience));

        internal int Property(uint key)
            => Character.Object != null && Character.Object.Ints.TryGetValue(key, out int value) ? value : 0;

        internal long Property64(uint key)
            => Character.Object != null && Character.Object.Int64s.TryGetValue(key, out long value) ? value : 0;

        private string PropertyString(uint key)
            => Character.Object != null && Character.Object.Strings.TryGetValue(key, out string value) && value != null ? value : string.Empty;

        internal int AttributeBase(CharFilterAttributeType type)
            => Character.Attributes.TryGetValue((uint)type, out AttributeState attribute) ? unchecked((int)attribute.Base) : 0;

        /// <summary>
        /// A vital's maximum before enchantments: half endurance for health, endurance for
        /// stamina, self for mana, plus what the character was created with and has raised it by.
        /// </summary>
        internal int VitalBase(CharFilterVitalType type)
        {
            if (!Character.Vitals.TryGetValue((uint)type - 1, out VitalState vital))
                return 0;

            int fromAttributes = type switch
            {
                CharFilterVitalType.Health => AttributeBase(CharFilterAttributeType.Endurance) / 2,
                CharFilterVitalType.Stamina => AttributeBase(CharFilterAttributeType.Endurance),
                CharFilterVitalType.Mana => AttributeBase(CharFilterAttributeType.Self),
                _ => 0,
            };

            return fromAttributes + unchecked((int)(vital.StartingValue + vital.Ranks));
        }

        /// <summary>A vital's maximum with its enchantments, as the server works it out.</summary>
        internal int VitalMaximum(CharFilterVitalType type)
            => Character.Vitals.ContainsKey((uint)type - 1) ? Character.GetVitalMaximum((uint)type - 1) : 0;

        internal int VitalCurrent(CharFilterVitalType type)
            => Character.Vitals.TryGetValue((uint)type - 1, out VitalState vital) ? unchecked((int)vital.Current) : 0;
    }

    /// <summary>
    /// A read-only collection Decal indexed by an enum - skills by skill, vitals by vital -
    /// and could also walk.
    /// </summary>
    public sealed class IndexedCollection<IndexType, KeyType, RType> : MarshalByRefObject, IEnumerable<RType>
    {
        private readonly Func<KeyType, RType> _lookup;
        private readonly Func<IEnumerable<KeyType>> _keys;

        internal IndexedCollection(Func<KeyType, RType> lookup, Func<IEnumerable<KeyType>> keys)
        {
            _lookup = lookup;
            _keys = keys;
        }

        public RType this[KeyType index] => _lookup(index);

        public int Count => _keys().Count();

        public IEnumerator<RType> GetEnumerator()
        {
            foreach (KeyType key in _keys().ToList())
                yield return _lookup(key);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A skill, or a vital - Decal described both with this one type.</summary>
    public class SkillInfoWrapper : MarshalByRefObject, IDisposable
    {
        private readonly CharacterFilter _filter;
        private readonly CharFilterSkillType? _skill;
        private readonly CharFilterVitalType? _vital;

        internal SkillInfoWrapper(CharacterFilter filter, CharFilterSkillType skill)
        {
            _filter = filter;
            _skill = skill;
        }

        internal SkillInfoWrapper(CharacterFilter filter, CharFilterVitalType vital)
        {
            _filter = filter;
            _vital = vital;
        }

        /// <summary>Before enchantments: a skill's base, a vital's maximum.</summary>
        public int Base => _skill.HasValue ? _filter.Character.GetSkillBase((uint)_skill.Value) : _filter.VitalBase(_vital.Value);

        /// <summary>
        /// After enchantments, as the server uses it: a skill with its attributes as buffed, its
        /// augmentations and its enchantments; a vital's maximum with its enchantments.
        /// </summary>
        public int Buffed => _skill.HasValue
            ? (_filter.Character.Skills.ContainsKey((uint)_skill.Value) ? _filter.Character.GetSkillCurrent((uint)_skill.Value) : 0)
            : _filter.VitalMaximum(_vital.Value);

        public int Bonus => 0;

        /// <summary>A vital's current value; a skill's, which is its buffed value.</summary>
        public int Current => _vital.HasValue ? _filter.VitalCurrent(_vital.Value) : Buffed;

        public int XP => _skill.HasValue && _filter.Character.Skills.TryGetValue((uint)_skill.Value, out SkillState skill)
            ? unchecked((int)skill.ExperienceSpent)
            : _vital.HasValue && _filter.Character.Vitals.TryGetValue((uint)_vital.Value - 1, out VitalState vital) ? unchecked((int)vital.ExperienceSpent) : 0;

        /// <summary>How many times it has been raised.</summary>
        public int Increment => _skill.HasValue
            ? (_filter.Character.Skills.TryGetValue((uint)_skill.Value, out SkillState skill) ? skill.Ranks : 0)
            : _filter.Character.Vitals.TryGetValue((uint)_vital.Value - 1, out VitalState vital) ? unchecked((int)vital.Ranks) : 0;

        public string Formula => string.Empty;

        public bool Known => _vital.HasValue || (_skill.HasValue && _filter.Character.Skills.ContainsKey((uint)_skill.Value));

        public string Name => Spaced(_skill?.ToString() ?? _vital?.ToString() ?? string.Empty);

        public string ShortName => _skill?.ToString() ?? _vital?.ToString() ?? string.Empty;

        public TrainingType Training
        {
            get
            {
                if (_vital.HasValue)
                    return TrainingType.Trained;

                if (!_filter.Character.Skills.TryGetValue((uint)_skill.Value, out SkillState skill))
                    return TrainingType.Unusable;

                return skill.AdvancementClass switch
                {
                    3 => TrainingType.Specialized,
                    2 => TrainingType.Trained,
                    1 => TrainingType.Untrained,
                    _ => TrainingType.Unusable,
                };
            }
        }

        public void Dispose()
        {
        }

        protected virtual void Dispose(bool disposing)
        {
        }

        protected void EnforceDisposedOnce()
        {
        }

        internal static string Spaced(string name) => Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
    }

    public class AttributeInfoWrapper : MarshalByRefObject, IDisposable
    {
        private readonly CharacterFilter _filter;
        private readonly CharFilterAttributeType _type;

        internal AttributeInfoWrapper(CharacterFilter filter, CharFilterAttributeType type)
        {
            _filter = filter;
            _type = type;
        }

        public int Base => _filter.AttributeBase(_type);

        /// <summary>With its enchantments, as the server uses it.</summary>
        public int Buffed => _filter.Character.Attributes.ContainsKey((uint)_type) ? _filter.Character.GetAttributeCurrent((uint)_type) : 0;

        /// <summary>What the character was created with.</summary>
        public int Creation => _filter.Character.Attributes.TryGetValue((uint)_type, out AttributeState a) ? unchecked((int)a.StartingValue) : 0;

        public int Exp => _filter.Character.Attributes.TryGetValue((uint)_type, out AttributeState a) ? unchecked((int)a.ExperienceSpent) : 0;

        public string Name => _type.ToString();

        public void Dispose()
        {
        }

        protected virtual void Dispose(bool disposing)
        {
        }

        protected void EnforceDisposedOnce()
        {
        }
    }

    /// <summary>A spell on the character.</summary>
    public class EnchantmentWrapper : MarshalByRefObject, IDisposable
    {
        private readonly Enchantment _enchantment;

        internal EnchantmentWrapper(Enchantment enchantment)
        {
            _enchantment = enchantment;
        }

        public int SpellId => _enchantment.SpellId;

        public int Layer => _enchantment.Layer;

        /// <summary>The spell's family: two spells of one family do not stack.</summary>
        public int Family => unchecked((int)_enchantment.Category);

        public double Duration => _enchantment.Duration;

        public double Adjustment => _enchantment.StatModValue;

        public int Affected => unchecked((int)_enchantment.StatModKey);

        public int AffectedMask => unchecked((int)_enchantment.StatModType);

        /// <summary>
        /// Seconds left, as of the message that described it: the game sends a start time as
        /// seconds before now, so what remains is the duration less that. -1 for a spell that
        /// never runs out.
        /// </summary>
        public int TimeRemaining => _enchantment.Duration < 0 ? -1 : (int)Math.Max(0, _enchantment.Duration + _enchantment.StartTime);

        public DateTime Expires => TimeRemaining < 0 ? DateTime.MaxValue : DateTime.Now.AddSeconds(TimeRemaining);

        public void Dispose()
        {
        }

        protected virtual void Dispose(bool disposing)
        {
        }

        protected void EnforceDisposedOnce()
        {
        }
    }

    public class AllegianceInfoWrapper : MarshalByRefObject, IDisposable
    {
        internal AllegianceInfoWrapper()
        {
        }

        public int Id => 0;

        public int Gender => 0;

        public int Leadership => 0;

        public int Loyalty => 0;

        public string Name => string.Empty;

        public int Race => 0;

        public int Rank => 0;

        public int ParentId => 0;

        public int Type => 0;

        public double Unknown => 0;

        public long XP => 0;

        public void Dispose()
        {
        }

        protected virtual void Dispose(bool disposing)
        {
        }

        protected void EnforceDisposedOnce()
        {
        }
    }

    /// <summary>One of the account's characters, as the server's character list named it.</summary>
    public class AccountCharInfo : MarshalByRefObject
    {
        internal AccountCharInfo(AccountCharacter character, int index)
        {
            Name = character?.Name ?? string.Empty;
            Id = unchecked((int)(character?.Id ?? 0));
            Index = index;
        }

        public string Name { get; }

        public int Id { get; }

        /// <summary>Its place in the list, from 0.</summary>
        public int Index { get; }
    }
}
