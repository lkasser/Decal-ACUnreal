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
    /// Decal's character filter: the logged-in character's name, attributes, skills, vitals
    /// and enchantments, and the events for them changing. Answered from the host's
    /// character model.
    /// </summary>
    /// <remarks>
    /// The host decodes the character's description but not everything in it - not the
    /// spellbook, the spell bars, the allegiance or experience totals - so those answer empty
    /// or zero, and <see cref="IsSpellKnown"/> answers yes, since a plugin told no spell is
    /// known hides everything it would have offered, and one told wrongly that a spell is
    /// known only has the server refuse the cast.
    /// </remarks>
    public class CharacterFilter : DisposableByRefObject
    {
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
                type => AttributeBase(type), () => Enum.GetValues<CharFilterAttributeType>());
            EffectiveSkill = new IndexedCollection<CharFilterIndex, CharFilterSkillType, int>(
                type => Character.GetSkillBase((uint)type), () => Enum.GetValues<CharFilterSkillType>().Where(s => Character.Skills.ContainsKey((uint)s)));
            EffectiveVital = new IndexedCollection<CharFilterIndex, CharFilterVitalType, int>(
                type => VitalBase(type), () => new[] { CharFilterVitalType.Health, CharFilterVitalType.Stamina, CharFilterVitalType.Mana });
            Enchantments = new IndexedCollection<CharFilterIndex, int, EnchantmentWrapper>(
                index => index >= 0 && index < Character.Enchantments.Count ? new EnchantmentWrapper(Character.Enchantments.Values.ElementAt(index)) : null,
                () => Enumerable.Range(0, Character.Enchantments.Count));
            Vassals = new IndexedCollection<CharFilterIndex, int, AllegianceInfoWrapper>(_ => null, Array.Empty<int>);
            Characters = new IndexedCollection<CharFilterIndex, int, AccountCharInfo>(_ => null, Array.Empty<int>);
        }

        internal ICharacterView Character => _runtime.Host.Character;

        public int Id => unchecked((int)Character.Id);

        public string Name => Character.Name ?? string.Empty;

        /// <summary>The account's name. The host never sees it, so empty.</summary>
        public string AccountName => string.Empty;

        public string Server => _runtime.Host.World.ServerName ?? string.Empty;

        public int ServerPopulation => 0;

        public int Level => Character.Level;

        public int Health => VitalCurrent(CharFilterVitalType.Health);

        public int Stamina => VitalCurrent(CharFilterVitalType.Stamina);

        public int Mana => VitalCurrent(CharFilterVitalType.Mana);

        public string Race => string.Empty;

        public string Gender => string.Empty;

        public string ClassTemplate => string.Empty;

        public int Age => Property(125);

        public DateTime Birth => DateTimeOffset.FromUnixTimeSeconds(Property(98)).LocalDateTime;

        public int Burden => Property(5);

        public int BurdenUnits => Property(5);

        public int Deaths => Property(43);

        public int Rank => Property(30);

        public int Followers => Property(35);

        public int MonarchFollowers => 0;

        public int SkillPoints => Property(24);

        public int Vitae => 0;

        public int CharacterOptions => 0;

        public int CharacterOptionFlags => 0;

        public int LoginStatus => Character.Id != 0 ? 1 : 0;

        public long TotalXP => 0;

        public long UnassignedXP => 0;

        public long XPToNextLevel => 0;

        public AllegianceInfoWrapper Monarch => null;

        public AllegianceInfoWrapper Patron => null;

        public AllegianceInfoWrapper Allegiance => null;

        public ReadOnlyCollection<int> SpellBook => new ReadOnlyCollection<int>(Array.Empty<int>());

        public ReadOnlyCollection<int> Augmentations => new ReadOnlyCollection<int>(Array.Empty<int>());

        public IndexedCollection<CharFilterIndex, CharFilterVitalType, SkillInfoWrapper> Vitals { get; }

        public IndexedCollection<CharFilterIndex, CharFilterAttributeType, AttributeInfoWrapper> Attributes { get; }

        public IndexedCollection<CharFilterIndex, CharFilterSkillType, SkillInfoWrapper> Skills { get; }

        public IndexedCollection<CharFilterIndex, CharFilterAttributeType, int> EffectiveAttribute { get; }

        public IndexedCollection<CharFilterIndex, CharFilterSkillType, int> EffectiveSkill { get; }

        public IndexedCollection<CharFilterIndex, CharFilterVitalType, int> EffectiveVital { get; }

        public IndexedCollection<CharFilterIndex, int, EnchantmentWrapper> Enchantments { get; }

        public IndexedCollection<CharFilterIndex, int, AllegianceInfoWrapper> Vassals { get; }

        public IndexedCollection<CharFilterIndex, int, AccountCharInfo> Characters { get; }

        public bool IsSpellKnown(int spellId) => true;

        public ReadOnlyCollection<int> SpellBar(int barNumber) => new ReadOnlyCollection<int>(Array.Empty<int>());

        public int Shortcut(int slot) => 0;

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

        // What the host does not decode: the spellbook and bars, deaths, portals, fellowships,
        // experience, settings. Plugins subscribe to these at startup and must be able to.
#pragma warning disable CS0067
        public event EventHandler<SpellbookEventArgs> SpellbookChange;

        public event EventHandler<DeathEventArgs> Death;

        public event EventHandler<ChangePortalModeEventArgs> ChangePortalMode;

        public event EventHandler<ChangePlayerEventArgs> ChangePlayer;

        public event EventHandler<ChangeFellowshipEventArgs> ChangeFellowship;

        public event EventHandler<ChangeExperienceEventArgs> ChangeExperience;

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

        internal int Property(uint key)
            => Character.Object != null && Character.Object.Ints.TryGetValue(key, out int value) ? value : 0;

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

        /// <summary>Before enchantments.</summary>
        public int Base => _skill.HasValue ? _filter.Character.GetSkillBase((uint)_skill.Value) : _filter.VitalBase(_vital.Value);

        /// <summary>
        /// After enchantments. The host does not work enchantments into skills, so this is the
        /// base; plugins that compare the two see no buffs rather than wrong ones.
        /// </summary>
        public int Buffed => Base;

        public int Bonus => 0;

        /// <summary>A vital's current value; a skill's, which is its buffed value.</summary>
        public int Current => _vital.HasValue ? _filter.VitalCurrent(_vital.Value) : Buffed;

        public int XP => _skill.HasValue && _filter.Character.Skills.TryGetValue((uint)_skill.Value, out SkillState skill)
            ? unchecked((int)skill.ExperienceSpent)
            : _vital.HasValue && _filter.Character.Vitals.TryGetValue((uint)_vital.Value - 1, out VitalState vital) ? unchecked((int)vital.ExperienceSpent) : 0;

        public int Increment => _skill.HasValue && _filter.Character.Skills.TryGetValue((uint)_skill.Value, out SkillState skill) ? skill.Ranks : 0;

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

        public int Buffed => Base;

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

    public class AccountCharInfo : MarshalByRefObject
    {
        internal AccountCharInfo()
        {
        }

        public string Name => string.Empty;

        public int Id => 0;

        public int Index => 0;
    }
}
