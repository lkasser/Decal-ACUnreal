using System;
using System.Collections.Generic;

using AC.Host.Decoding;

namespace AC.Host.World
{
    /// <summary>One of the character's skills, as the server describes it.</summary>
    public sealed class SkillState
    {
        public SkillState(uint skillId)
        {
            SkillId = skillId;
        }

        public uint SkillId { get; }

        /// <summary>Points raised.</summary>
        public ushort Ranks { get; internal set; }

        /// <summary>0 inactive, 1 untrained, 2 trained, 3 specialized.</summary>
        public uint AdvancementClass { get; internal set; }

        public uint ExperienceSpent { get; internal set; }

        /// <summary>Starting bonus from character creation.</summary>
        public uint InitLevel { get; internal set; }

        public bool IsTrained => AdvancementClass >= 2;

        public bool IsSpecialized => AdvancementClass == 3;
    }

    public sealed class AttributeState
    {
        public AttributeState(uint attributeId)
        {
            AttributeId = attributeId;
        }

        /// <summary>1 strength, 2 endurance, 3 quickness, 4 coordination, 5 focus, 6 self.</summary>
        public uint AttributeId { get; }

        public uint Ranks { get; internal set; }

        public uint StartingValue { get; internal set; }

        public uint ExperienceSpent { get; internal set; }

        /// <summary>The unbuffed attribute value.</summary>
        public uint Base => StartingValue + Ranks;
    }

    public sealed class VitalState
    {
        public VitalState(uint vitalId)
        {
            VitalId = vitalId;
        }

        /// <summary>1 max health, 3 max stamina, 5 max mana (server numbering).</summary>
        public uint VitalId { get; }

        public uint Ranks { get; internal set; }

        public uint StartingValue { get; internal set; }

        public uint ExperienceSpent { get; internal set; }

        public uint Current { get; internal set; }
    }

    /// <summary>
    /// The player character. Its object, once created, is an ordinary
    /// <see cref="WorldObject"/> in the world; this holds what only the player's own
    /// description carries.
    /// </summary>
    public interface ICharacterView
    {
        /// <summary>The player's object id, or 0 before it is known.</summary>
        uint Id { get; }

        WorldObject Object { get; }

        string Name { get; }

        /// <summary>Character level, or 0 if not yet known.</summary>
        int Level { get; }

        IReadOnlyDictionary<uint, SkillState> Skills { get; }

        IReadOnlyDictionary<uint, AttributeState> Attributes { get; }

        IReadOnlyDictionary<uint, VitalState> Vitals { get; }

        /// <summary>
        /// Where the client last said the character is, or null before it has said.
        /// </summary>
        Location? Location { get; }

        /// <summary>
        /// What the client last said the character's body is doing, or null before it
        /// has said.
        /// </summary>
        ClientMotionState Motion { get; }

        /// <summary>
        /// The object the player last selected or examined, or 0 before they have. Worked
        /// out from what the client asks the server about - a selected creature's health,
        /// an examined object's description - since the selection itself is never sent.
        /// Includes the host's own appraisals, which ask the same question.
        /// </summary>
        uint SelectedId { get; }

        /// <summary>
        /// The sequence numbers from the client's last movement message. Composing a
        /// movement message means sending these back, not inventing them.
        /// </summary>
        MovementSequences Sequences { get; }

        /// <summary>
        /// The highest ordering sequence seen on an action the client sent. An injected
        /// action must not reuse a number the server has already been given.
        /// </summary>
        uint LastActionSequence { get; }

        /// <summary>
        /// Active enchantments, keyed by spell id and layer packed into one word - which
        /// is how the server names one when it takes it away. Includes debuffs put there
        /// by something else.
        /// </summary>
        IReadOnlyDictionary<uint, Enchantment> Enchantments { get; }

        /// <summary>
        /// The effective base value of a skill, from ranks, starting bonus and the
        /// attribute formula for that skill. Zero for an unknown skill.
        /// </summary>
        int GetSkillBase(uint skillId);

        /// <summary>
        /// The spells the character knows, by id: the spellbook the login described, kept
        /// current as spells are learned and forgotten.
        /// </summary>
        IReadOnlyCollection<uint> Spellbook { get; }

        /// <summary>True when the character knows the spell.</summary>
        bool KnowsSpell(uint spellId);

        /// <summary>
        /// The spell the player last cast themselves, or 0. Read from what the client sends;
        /// what plugins cast is not counted.
        /// </summary>
        uint LastCastSpellId { get; }

        /// <summary>An attribute with its enchantments - what the server uses. Zero for an unknown attribute.</summary>
        int GetAttributeCurrent(uint attributeId);

        /// <summary>
        /// A skill as the server uses it: ranks, attributes as buffed, augmentations and
        /// enchantments. Zero for an unknown skill.
        /// </summary>
        int GetSkillCurrent(uint skillId);

        /// <summary>
        /// A vital's maximum - 1 health, 3 stamina, 5 mana - as the server works it out.
        /// The current value is <see cref="VitalState.Current"/>.
        /// </summary>
        int GetVitalMaximum(uint vitalId);

        /// <summary>
        /// True from the server's teleport until the client says it has arrived: in portal
        /// space, where the character can neither act nor be reached.
        /// </summary>
        bool InPortalSpace { get; }

        /// <summary>
        /// The vendor whose window is open, or 0: set when the server shows a vendor's wares,
        /// and cleared when the character walks out of its reach, goes through a portal or
        /// opens something else, as the game client closes the window.
        /// </summary>
        uint OpenVendorId { get; }

        /// <summary>
        /// The fellowship the character is in, with each member's vitals as last sent. Never
        /// null; <see cref="IFellowshipView.IsMember"/> says whether there is one.
        /// </summary>
        IFellowshipView Fellowship { get; }

        /// <summary>
        /// The character's first word of options - the game's CharacterOptions1, the boxes of its
        /// options panel - as the login described them and the client has set them since. 0
        /// before the login says.
        /// </summary>
        uint CharacterOptions { get; }

        /// <summary>
        /// The second word of options, CharacterOptions2: fast missiles (0x10000), the chat rooms
        /// heard, showing the helm and the rest. 0 before the login says, or where it does not.
        /// </summary>
        uint CharacterOptions2 { get; }

        /// <summary>
        /// The objects on the shortcut bar, by slot from 0, as the login described them and the
        /// client has changed them since. A slot with nothing on it is not there.
        /// </summary>
        IReadOnlyDictionary<int, uint> Shortcuts { get; }

        /// <summary>
        /// The spells on each of the eight spell bars, in their order on the bar: the login's,
        /// kept current as the client adds and takes them away. Eight lists, empty before the
        /// login says.
        /// </summary>
        IReadOnlyList<IReadOnlyList<uint>> SpellBars { get; }
    }

    public sealed class CharacterState : ICharacterView
    {
        private readonly Dictionary<uint, SkillState> _skills = new Dictionary<uint, SkillState>();
        private readonly Dictionary<uint, AttributeState> _attributes = new Dictionary<uint, AttributeState>();
        private readonly Dictionary<uint, VitalState> _vitals = new Dictionary<uint, VitalState>();

        public uint Id { get; internal set; }

        public WorldObject Object { get; internal set; }

        public string Name => Object?.Name ?? string.Empty;

        public int Level => Object != null && Object.Ints.TryGetValue(25, out int level) ? level : 0;

        public IReadOnlyDictionary<uint, SkillState> Skills => _skills;

        public IReadOnlyDictionary<uint, AttributeState> Attributes => _attributes;

        public IReadOnlyDictionary<uint, VitalState> Vitals => _vitals;

        public Location? Location { get; internal set; }

        public ClientMotionState Motion { get; internal set; }

        public uint SelectedId { get; internal set; }

        public MovementSequences Sequences { get; internal set; }

        public uint LastActionSequence { get; internal set; }

        public bool InPortalSpace { get; internal set; }

        public uint OpenVendorId { get; internal set; }

        /// <summary>The number the server gave its last teleport of the character.</summary>
        internal ushort TeleportSequence { get; set; }

        public IReadOnlyDictionary<uint, Enchantment> Enchantments => _enchantments;

        private readonly Dictionary<uint, Enchantment> _enchantments = new Dictionary<uint, Enchantment>();

        internal Enchantment SetEnchantment(Enchantment enchantment)
        {
            _enchantments[enchantment.PackedId] = enchantment;
            return enchantment;
        }

        internal bool RemoveEnchantment(uint packedId) => _enchantments.Remove(packedId);

        internal void ClearEnchantments() => _enchantments.Clear();

        internal SkillState GetOrAddSkill(uint skillId)
        {
            if (!_skills.TryGetValue(skillId, out SkillState skill))
            {
                skill = new SkillState(skillId);
                _skills[skillId] = skill;
            }

            return skill;
        }

        internal AttributeState GetOrAddAttribute(uint attributeId)
        {
            if (!_attributes.TryGetValue(attributeId, out AttributeState attribute))
            {
                attribute = new AttributeState(attributeId);
                _attributes[attributeId] = attribute;
            }

            return attribute;
        }

        internal VitalState GetOrAddVital(uint vitalId)
        {
            if (!_vitals.TryGetValue(vitalId, out VitalState vital))
            {
                vital = new VitalState(vitalId);
                _vitals[vitalId] = vital;
            }

            return vital;
        }

        /// <summary>The client's data files, when the host has them.</summary>
        internal IGameData GameData { get; set; } = NullGameData.Instance;

        public int GetSkillBase(uint skillId)
        {
            if (!_skills.TryGetValue(skillId, out SkillState skill))
                return 0;

            return SkillFormula.Base(skillId, skill, this, GameData);
        }

        internal uint AttributeBase(uint attributeId)
            => attributeId != 0 && _attributes.TryGetValue(attributeId, out AttributeState a) ? a.Base : 0;

        public IReadOnlyCollection<uint> Spellbook => _spellbook;

        private readonly HashSet<uint> _spellbook = new HashSet<uint>();

        public bool KnowsSpell(uint spellId) => _spellbook.Contains(spellId);

        public uint LastCastSpellId { get; internal set; }

        internal void ReplaceSpellbook(IEnumerable<uint> spells)
        {
            _spellbook.Clear();
            _spellbook.UnionWith(spells);
        }

        internal bool LearnSpell(uint spellId) => _spellbook.Add(spellId);

        internal bool ForgetSpell(uint spellId) => _spellbook.Remove(spellId);

        public int GetAttributeCurrent(uint attributeId) => AttributeCurrent(attributeId);

        internal int AttributeCurrent(uint attributeId) => CharacterStats.AttributeCurrent(this, attributeId);

        public int GetSkillCurrent(uint skillId) => CharacterStats.SkillCurrent(this, skillId, GameData);

        public int GetVitalMaximum(uint vitalId) => CharacterStats.VitalMaximum(this, vitalId, GameData);

        public IFellowshipView Fellowship => FellowshipData;

        /// <summary>The fellowship, for the decoder to change.</summary>
        internal FellowshipState FellowshipData { get; } = new FellowshipState();

        /// <summary>How many spell bars the game has.</summary>
        public const int SpellBarCount = 8;

        public uint CharacterOptions { get; internal set; }

        public uint CharacterOptions2 { get; internal set; }

        public IReadOnlyDictionary<int, uint> Shortcuts => _shortcuts;

        private readonly Dictionary<int, uint> _shortcuts = new Dictionary<int, uint>();

        public IReadOnlyList<IReadOnlyList<uint>> SpellBars => _spellBars;

        private readonly List<uint>[] _spellBars = NewSpellBars();

        private static List<uint>[] NewSpellBars()
        {
            List<uint>[] bars = new List<uint>[SpellBarCount];
            for (int i = 0; i < bars.Length; i++)
                bars[i] = new List<uint>();
            return bars;
        }

        /// <summary>
        /// Takes the options, shortcuts and spell bars a login or the client's own options message
        /// gave. The second word of options only where it was given.
        /// </summary>
        internal void SetOptions(CharacterOptionData options)
        {
            CharacterOptions = options.Options;
            if (options.Options2.HasValue)
                CharacterOptions2 = options.Options2.Value;

            _shortcuts.Clear();
            foreach (KeyValuePair<int, uint> shortcut in options.Shortcuts)
                _shortcuts[shortcut.Key] = shortcut.Value;

            for (int i = 0; i < _spellBars.Length; i++)
            {
                _spellBars[i].Clear();
                if (i < options.SpellBars.Count)
                    _spellBars[i].AddRange(options.SpellBars[i]);
            }
        }

        internal void SetShortcut(int slot, uint objectId) => _shortcuts[slot] = objectId;

        internal bool RemoveShortcut(int slot) => _shortcuts.Remove(slot);

        /// <summary>Puts a spell on a bar at a place, as the client's AddSpellFavorite does: taken from wherever it was on that bar first.</summary>
        internal bool AddToSpellBar(int bar, int position, uint spellId)
        {
            if (bar < 0 || bar >= _spellBars.Length)
                return false;

            List<uint> spells = _spellBars[bar];
            spells.Remove(spellId);
            spells.Insert(Math.Clamp(position, 0, spells.Count), spellId);
            return true;
        }

        internal bool RemoveFromSpellBar(int bar, uint spellId)
            => bar >= 0 && bar < _spellBars.Length && _spellBars[bar].Remove(spellId);

        /// <summary>
        /// Forgets the character: it has left the world, and the next one in may be another. Kept
        /// in place rather than replaced, since plugins hold on to it. The highest action sequence
        /// stays, because what matters about it is never to reuse a number the server has seen.
        /// </summary>
        internal void Forget()
        {
            Id = 0;
            Object = null;
            _skills.Clear();
            _attributes.Clear();
            _vitals.Clear();
            _enchantments.Clear();
            _spellbook.Clear();
            Location = null;
            Motion = null;
            SelectedId = 0;
            Sequences = default;
            InPortalSpace = false;
            OpenVendorId = 0;
            TeleportSequence = 0;
            LastCastSpellId = 0;
            FellowshipData.Clear();
            FellowshipData.PanelOpen = false;
            CharacterOptions = 0;
            CharacterOptions2 = 0;
            _shortcuts.Clear();
            foreach (List<uint> bar in _spellBars)
                bar.Clear();
        }
    }

    /// <summary>
    /// The options, shortcuts and spell bars, as the login's description and the client's own
    /// SetCharacterOptions both carry them.
    /// </summary>
    internal sealed class CharacterOptionData
    {
        public uint Options { get; set; }

        /// <summary>Null where the message did not carry the second word.</summary>
        public uint? Options2 { get; set; }

        public Dictionary<int, uint> Shortcuts { get; } = new Dictionary<int, uint>();

        public List<List<uint>> SpellBars { get; } = new List<List<uint>>();
    }
}
