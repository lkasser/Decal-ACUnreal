using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using AC.Dat;

namespace AC.Host.World
{
    /// <summary>
    /// The client's own data files, where the protocol's ids become names, colours
    /// and formulas.
    /// </summary>
    /// <remarks>
    /// The server sends a spell as a number and a palette slot as a number, because
    /// the client already has the tables. A host that never reads them can tell you
    /// an item has spell 2650 but not that it is Impenetrability, and a loot rule
    /// written against names or colours can never match.
    ///
    /// Optional throughout: a host with no DAT still decodes everything else, and
    /// says so rather than inventing answers.
    /// </remarks>
    public interface IGameData
    {
        /// <summary>False when no client data is loaded; every lookup then returns nothing.</summary>
        bool IsAvailable { get; }

        /// <summary>The spell's name, or null if unknown.</summary>
        string GetSpellName(uint spellId);

        /// <summary>A representative colour for one of an object's palette slots, or null.</summary>
        Color? GetSlotColor(uint paletteId, int offset, int length);

        /// <summary>
        /// How a skill derives from attributes: the sum of one or two attributes over
        /// a divisor. Attribute 0 means none.
        /// </summary>
        bool TryGetSkillFormula(uint skillId, out uint attribute1, out uint attribute2, out uint divisor);

        /// <summary>
        /// How a maximum vital - 1 health, 3 stamina, 5 mana - derives from attributes, as
        /// <see cref="TryGetSkillFormula"/> does for a skill.
        /// </summary>
        bool TryGetVitalFormula(uint vitalId, out uint attribute1, out uint attribute2, out uint divisor);

        /// <summary>Everything the client's table says about a spell, or null if it has no such spell.</summary>
        SpellInfo GetSpell(uint spellId);

        /// <summary>Every spell in the client's table.</summary>
        IReadOnlyCollection<SpellInfo> Spells { get; }

        /// <summary>
        /// The spells of one family - every level of Strength Self, say - weakest first.
        /// Empty for an unknown family.
        /// </summary>
        IReadOnlyList<SpellInfo> GetSpellsInCategory(uint category);

        /// <summary>A spell by its exact name, ignoring case, or null. Where two share a name, the lower id.</summary>
        SpellInfo FindSpell(string name);

        /// <summary>A spell component - a scarab, a herb, a taper - by the id spells list it under, or null.</summary>
        SpellComponentInfo GetComponent(uint componentId);
    }

    /// <summary>Answers nothing, for a host running without the client's files.</summary>
    public sealed class NullGameData : IGameData
    {
        public static readonly NullGameData Instance = new NullGameData();

        private NullGameData()
        {
        }

        public bool IsAvailable => false;

        public string GetSpellName(uint spellId) => null;

        public Color? GetSlotColor(uint paletteId, int offset, int length) => null;

        public bool TryGetSkillFormula(uint skillId, out uint attribute1, out uint attribute2, out uint divisor)
        {
            attribute1 = 0;
            attribute2 = 0;
            divisor = 0;
            return false;
        }

        public bool TryGetVitalFormula(uint vitalId, out uint attribute1, out uint attribute2, out uint divisor)
            => TryGetSkillFormula(vitalId, out attribute1, out attribute2, out divisor);

        public SpellInfo GetSpell(uint spellId) => null;

        public IReadOnlyCollection<SpellInfo> Spells => Array.Empty<SpellInfo>();

        public IReadOnlyList<SpellInfo> GetSpellsInCategory(uint category) => Array.Empty<SpellInfo>();

        public SpellInfo FindSpell(string name) => null;

        public SpellComponentInfo GetComponent(uint componentId) => null;
    }

    /// <summary>Client data read from <c>client_portal.dat</c>.</summary>
    public sealed class PortalGameData : IGameData
    {
        private readonly PortalData _portal;

        public PortalGameData(PortalData portal)
        {
            _portal = portal;
        }

        public bool IsAvailable => _portal != null;

        public int SpellCount => _portal?.Spells.Count ?? 0;

        public int SkillCount => _portal?.Skills.Count ?? 0;

        public string Path => _portal?.Path;

        public string GetSpellName(uint spellId) => _portal?.GetSpellName(spellId);

        public Color? GetSlotColor(uint paletteId, int offset, int length)
            => _portal?.GetSlotColor(paletteId, offset, length);

        public bool TryGetSkillFormula(uint skillId, out uint attribute1, out uint attribute2, out uint divisor)
        {
            attribute1 = 0;
            attribute2 = 0;
            divisor = 0;

            if (_portal == null || !_portal.TryGetSkillFormula(skillId, out SkillFormulaInfo formula))
                return false;

            attribute1 = formula.Attribute1;
            attribute2 = formula.Attribute2;
            divisor = formula.Divisor;
            return true;
        }

        public bool TryGetVitalFormula(uint vitalId, out uint attribute1, out uint attribute2, out uint divisor)
        {
            attribute1 = 0;
            attribute2 = 0;
            divisor = 0;

            if (_portal == null || !_portal.TryGetVitalFormula(vitalId, out SkillFormulaInfo formula))
                return false;

            attribute1 = formula.Attribute1;
            attribute2 = formula.Attribute2;
            divisor = formula.Divisor;
            return true;
        }

        public SpellInfo GetSpell(uint spellId) => _portal?.GetSpell(spellId);

        public IReadOnlyCollection<SpellInfo> Spells => Index.All;

        public IReadOnlyList<SpellInfo> GetSpellsInCategory(uint category)
            => Index.Families.TryGetValue(category, out List<SpellInfo> family) ? family : (IReadOnlyList<SpellInfo>)Array.Empty<SpellInfo>();

        public SpellInfo FindSpell(string name)
            => name != null && Index.Names.TryGetValue(name.Trim(), out SpellInfo spell) ? spell : null;

        public SpellComponentInfo GetComponent(uint componentId) => _portal?.GetComponent(componentId);

        /// <summary>The families and names, indexed the first time anything asks: six thousand spells, looked up often.</summary>
        private SpellIndex Index => _index ??= new SpellIndex(_portal);

        private SpellIndex _index;

        private sealed class SpellIndex
        {
            public SpellIndex(PortalData portal)
            {
                if (portal == null)
                    return;

                All = portal.Spells.Values.OrderBy(s => s.Id).ToArray();

                foreach (SpellInfo spell in All)
                {
                    if (!Families.TryGetValue(spell.Category, out List<SpellInfo> family))
                        Families[spell.Category] = family = new List<SpellInfo>();
                    family.Add(spell);

                    if (!string.IsNullOrEmpty(spell.Name) && !Names.ContainsKey(spell.Name))
                        Names[spell.Name] = spell;
                }

                foreach (List<SpellInfo> family in Families.Values)
                    family.Sort((a, b) => a.Power != b.Power ? a.Power.CompareTo(b.Power) : a.Id.CompareTo(b.Id));
            }

            public IReadOnlyCollection<SpellInfo> All { get; } = Array.Empty<SpellInfo>();

            public Dictionary<uint, List<SpellInfo>> Families { get; } = new Dictionary<uint, List<SpellInfo>>();

            public Dictionary<string, SpellInfo> Names { get; } = new Dictionary<string, SpellInfo>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
