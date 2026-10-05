using System;
using System.Collections.Generic;

namespace AC.Dat
{
    /// <summary>What the spell table knows about one spell.</summary>
    /// <remarks>
    /// Everything the client's own table carries, since a buffing or fighting plugin needs
    /// most of it: the level (<see cref="Power"/>, which is also the difficulty the caster's
    /// skill is checked against), the family, what the spell does (<see cref="SpellType"/>),
    /// how long it lasts, its mana, what it may be cast on, and its components. What a spell
    /// does to a statistic is not here: that lives on the server, and arrives with the
    /// enchantment.
    /// </remarks>
    public sealed class SpellInfo
    {
        internal SpellInfo(uint id, string name, string description, uint school, uint icon, uint category, uint power)
        {
            Id = id;
            Name = name;
            Description = description;
            School = school;
            Icon = icon;
            Category = category;
            Power = power;
        }

        public uint Id { get; }

        public string Name { get; }

        public string Description { get; }

        /// <summary>1 war, 2 life, 3 item, 4 creature, 5 void.</summary>
        public uint School { get; }

        public uint Icon { get; }

        /// <summary>
        /// Groups the levels of one spell together, so that Strength Self I and II
        /// share a category and do not stack.
        /// </summary>
        public uint Category { get; }

        /// <summary>
        /// Ranks the levels within a category; higher is stronger. Also the spell's
        /// difficulty: the server checks the caster's skill against this number.
        /// </summary>
        public uint Power { get; }

        /// <summary>The spell's flags: see <see cref="SpellFlags"/>.</summary>
        public uint Flags { get; internal set; }

        /// <summary>Mana to cast, before the caster's Mana Conversion.</summary>
        public uint BaseMana { get; internal set; }

        /// <summary>Range in metres at zero skill.</summary>
        public float BaseRangeConstant { get; internal set; }

        /// <summary>Range added per point of the caster's skill.</summary>
        public float BaseRangeMod { get; internal set; }

        public float EconomyMod { get; internal set; }

        public uint FormulaVersion { get; internal set; }

        /// <summary>The chance each component is used up by a cast.</summary>
        public float ComponentLoss { get; internal set; }

        /// <summary>What the spell does: see <see cref="SpellTypes"/>.</summary>
        public uint SpellType { get; internal set; }

        public uint MetaSpellId { get; internal set; }

        /// <summary>Seconds an enchantment lasts; zero for any other kind of spell.</summary>
        public double Duration { get; internal set; }

        public float DegradeModifier { get; internal set; }

        public float DegradeLimit { get; internal set; }

        /// <summary>Seconds a summoned portal stays up; zero for any other kind of spell.</summary>
        public double PortalLifetime { get; internal set; }

        /// <summary>
        /// The components the spell uses, by component table id, in the client's order.
        /// Stored enciphered with the spell's name and description.
        /// </summary>
        public IReadOnlyList<uint> Components { get; internal set; } = Array.Empty<uint>();

        public uint CasterEffect { get; internal set; }

        public uint TargetEffect { get; internal set; }

        public uint FizzleEffect { get; internal set; }

        public double RecoveryInterval { get; internal set; }

        public float RecoveryAmount { get; internal set; }

        public uint DisplayOrder { get; internal set; }

        /// <summary>
        /// The kinds of object the spell may be cast on, as item type bits - creatures,
        /// weapons, armour - or zero for one the caster always receives.
        /// </summary>
        public uint TargetType { get; internal set; }

        /// <summary>Extra mana per target, for a spell that reaches several.</summary>
        public uint ManaMod { get; internal set; }

        /// <summary>What the server checks the caster's skill against.</summary>
        public uint Difficulty => Power;

        /// <summary>Lands on the caster, whatever is selected.</summary>
        public bool IsSelfTargeted => (Flags & SpellFlags.SelfTargeted) != 0;

        public bool IsBeneficial => (Flags & SpellFlags.Beneficial) != 0;

        public bool IsFellowship => (Flags & SpellFlags.FellowshipSpell) != 0;

        public bool IsEnchantment => SpellType == SpellTypes.Enchantment || SpellType == SpellTypes.FellowEnchantment;

        public override string ToString() => $"0x{Id:X4} {Name}";
    }

    /// <summary>The bits of <see cref="SpellInfo.Flags"/>, by the server's own names (its SpellFlags).</summary>
    public static class SpellFlags
    {
        public const uint Resistable = 0x0001;
        public const uint PKSensitive = 0x0002;
        public const uint Beneficial = 0x0004;
        public const uint SelfTargeted = 0x0008;
        public const uint Reversed = 0x0010;
        public const uint NotIndoor = 0x0020;
        public const uint NotOutdoor = 0x0040;
        public const uint NotResearchable = 0x0080;
        public const uint Projectile = 0x0100;
        public const uint CreatureSpell = 0x0200;
        public const uint ExcludedFromItemDescriptions = 0x0400;
        public const uint IgnoresManaConversion = 0x0800;
        public const uint NonTrackingProjectile = 0x1000;
        public const uint FellowshipSpell = 0x2000;
        public const uint FastCast = 0x4000;
        public const uint IndoorLongRange = 0x8000;
        public const uint DamageOverTime = 0x10000;
    }

    /// <summary>The values of <see cref="SpellInfo.SpellType"/>, by the server's own names (its SpellType).</summary>
    public static class SpellTypes
    {
        public const uint Enchantment = 1;
        public const uint Projectile = 2;
        public const uint Boost = 3;
        public const uint Transfer = 4;
        public const uint PortalLink = 5;
        public const uint PortalRecall = 6;
        public const uint PortalSummon = 7;
        public const uint PortalSending = 8;
        public const uint Dispel = 9;
        public const uint LifeProjectile = 10;
        public const uint FellowBoost = 11;
        public const uint FellowEnchantment = 12;
        public const uint FellowPortalSending = 13;
        public const uint FellowDispel = 14;
        public const uint EnchantmentProjectile = 15;
    }

    /// <summary>
    /// The spell table, file <c>0x0E00000E</c> of the portal archive.
    /// </summary>
    /// <remarks>
    /// Records are variable length and stored back to back, so every field of every
    /// spell has to be walked even though only the name is wanted - including the
    /// three fields that appear for enchantments and the one for portal summons.
    /// Reading a record wrong does not corrupt that record alone, it desynchronizes
    /// the rest of the table, which is why parsing stops at the first record that
    /// does not fit rather than trying to continue.
    ///
    /// <para>
    /// The field order, and the cipher on the components, are those of ACE's own reader
    /// of this file (ACE.DatLoader's SpellBase), which the server uses to decide what a
    /// cast costs.
    /// </para>
    /// </remarks>
    public static class SpellTable
    {
        public const uint FileId = 0x0E00000E;

        public static IReadOnlyDictionary<uint, SpellInfo> Parse(ReadOnlySpan<byte> data)
        {
            Dictionary<uint, SpellInfo> spells = new Dictionary<uint, SpellInfo>();
            DatBinaryReader reader = new DatBinaryReader(data);

            if (!reader.TryReadUInt32(out _))       // the file's own id
                return spells;

            if (!reader.TryReadHashTableHeader(out int count))
                return spells;

            for (int i = 0; i < count; i++)
            {
                if (!reader.TryReadUInt32(out uint id))
                    break;

                if (!TryReadSpell(ref reader, id, out SpellInfo spell))
                    break;

                spells[id] = spell;
            }

            return spells;
        }

        private static bool TryReadSpell(ref DatBinaryReader reader, uint id, out SpellInfo spell)
        {
            spell = null;

            if (!reader.TryReadObfuscatedString(out string name)) return false;
            if (!reader.TryAlign()) return false;
            if (!reader.TryReadObfuscatedString(out string description)) return false;
            if (!reader.TryAlign()) return false;

            if (!reader.TryReadUInt32(out uint school)) return false;
            if (!reader.TryReadUInt32(out uint icon)) return false;
            if (!reader.TryReadUInt32(out uint category)) return false;
            if (!reader.TryReadUInt32(out uint flags)) return false;
            if (!reader.TryReadUInt32(out uint baseMana)) return false;
            if (!reader.TryReadSingle(out float rangeConstant)) return false;
            if (!reader.TryReadSingle(out float rangeMod)) return false;
            if (!reader.TryReadUInt32(out uint power)) return false;
            if (!reader.TryReadSingle(out float economyMod)) return false;
            if (!reader.TryReadUInt32(out uint formulaVersion)) return false;
            if (!reader.TryReadSingle(out float componentLoss)) return false;
            if (!reader.TryReadUInt32(out uint spellType)) return false;
            if (!reader.TryReadUInt32(out uint metaSpellId)) return false;

            SpellInfo read = new SpellInfo(id, name, description, school, icon, category, power)
            {
                Flags = flags,
                BaseMana = baseMana,
                BaseRangeConstant = rangeConstant,
                BaseRangeMod = rangeMod,
                EconomyMod = economyMod,
                FormulaVersion = formulaVersion,
                ComponentLoss = componentLoss,
                SpellType = spellType,
                MetaSpellId = metaSpellId,
            };

            // Only these three carry the extra block, and mistaking one for another
            // puts every later record out of step.
            if (spellType == SpellTypes.Enchantment || spellType == SpellTypes.FellowEnchantment)
            {
                if (!reader.TryReadDouble(out double duration)) return false;
                if (!reader.TryReadSingle(out float degradeModifier)) return false;
                if (!reader.TryReadSingle(out float degradeLimit)) return false;
                read.Duration = duration;
                read.DegradeModifier = degradeModifier;
                read.DegradeLimit = degradeLimit;
            }
            else if (spellType == SpellTypes.PortalSummon)
            {
                if (!reader.TryReadDouble(out double lifetime)) return false;
                read.PortalLifetime = lifetime;
            }

            // Eight component slots, present whether used or not; an unused one is zero.
            uint[] slots = new uint[8];
            int used = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                if (!reader.TryReadUInt32(out uint slot)) return false;
                if (slot != 0)
                    slots[used++] = slot;
            }

            read.Components = Decipher(slots.AsSpan(0, used), name, description);

            if (!reader.TryReadUInt32(out uint casterEffect)) return false;
            if (!reader.TryReadUInt32(out uint targetEffect)) return false;
            if (!reader.TryReadUInt32(out uint fizzleEffect)) return false;
            if (!reader.TryReadDouble(out double recoveryInterval)) return false;
            if (!reader.TryReadSingle(out float recoveryAmount)) return false;
            if (!reader.TryReadUInt32(out uint displayOrder)) return false;
            if (!reader.TryReadUInt32(out uint targetType)) return false;
            if (!reader.TryReadUInt32(out uint manaMod)) return false;

            read.CasterEffect = casterEffect;
            read.TargetEffect = targetEffect;
            read.FizzleEffect = fizzleEffect;
            read.RecoveryInterval = recoveryInterval;
            read.RecoveryAmount = recoveryAmount;
            read.DisplayOrder = displayOrder;
            read.TargetType = targetType;
            read.ManaMod = manaMod;

            spell = read;
            return true;
        }

        /// <summary>
        /// Recovers the component ids. Each slot is stored plus a key made from hashes of
        /// the spell's name and description, and a result past the last component id keeps
        /// only its low byte - both as ACE reads them to charge for a cast.
        /// </summary>
        internal static uint[] Decipher(ReadOnlySpan<uint> slots, string name, string description)
        {
            uint key = (Hash(name) % 0x12107680u) + (Hash(description) % 0xBEADCF45u);
            uint[] components = new uint[slots.Length];

            for (int i = 0; i < slots.Length; i++)
            {
                uint component = unchecked(slots[i] - key);
                components[i] = component > 198 ? component & 0xFF : component;
            }

            return components;
        }

        /// <summary>
        /// The string hash the cipher is keyed with, over the text's own bytes. Bytes are
        /// taken as signed, which matters only for the accented letters a few descriptions have.
        /// </summary>
        internal static uint Hash(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            long result = 0;
            foreach (byte b in AcEncoding.Text.GetBytes(text))
            {
                result = (sbyte)b + (result << 4);
                if ((result & 0xF0000000L) != 0)
                    result = (result ^ ((result & 0xF0000000L) >> 24)) & 0x0FFFFFFFL;
            }

            return unchecked((uint)result);
        }
    }

    /// <summary>One spell component: a scarab, a herb, a taper.</summary>
    public sealed class SpellComponentInfo
    {
        internal SpellComponentInfo(uint id, string name, uint category, uint icon, uint type, uint gesture, float time, string text, float cdm)
        {
            Id = id;
            Name = name;
            Category = category;
            Icon = icon;
            Type = type;
            Gesture = gesture;
            Time = time;
            Text = text;
            Cdm = cdm;
        }

        public uint Id { get; }

        /// <summary>What the item in a pack is called: "Lead Scarab", "Prismatic Taper".</summary>
        public string Name { get; }

        public uint Category { get; }

        public uint Icon { get; }

        /// <summary>Scarab, herb, powder, potion, talisman or taper, by the client's numbering.</summary>
        public uint Type { get; }

        public uint Gesture { get; }

        public float Time { get; }

        /// <summary>The syllable spoken for it.</summary>
        public string Text { get; }

        public float Cdm { get; }

        public override string ToString() => $"{Id} {Name}";
    }

    /// <summary>
    /// The spell component table, file <c>0x0E00000F</c>: what each component id in a
    /// spell's formula is. Laid out as ACE's SpellComponentsTable reads it - a count, a
    /// word of padding, then the entries.
    /// </summary>
    public static class SpellComponentTable
    {
        public const uint FileId = 0x0E00000F;

        public static IReadOnlyDictionary<uint, SpellComponentInfo> Parse(ReadOnlySpan<byte> data)
        {
            Dictionary<uint, SpellComponentInfo> components = new Dictionary<uint, SpellComponentInfo>();
            DatBinaryReader reader = new DatBinaryReader(data);

            if (!reader.TryReadUInt32(out _))       // the file's own id
                return components;

            if (!reader.TryReadUInt16(out ushort count) || !reader.TryAlign())
                return components;

            for (int i = 0; i < count; i++)
            {
                if (!reader.TryReadUInt32(out uint id)) break;
                if (!reader.TryReadObfuscatedString(out string name) || !reader.TryAlign()) break;
                if (!reader.TryReadUInt32(out uint category)) break;
                if (!reader.TryReadUInt32(out uint icon)) break;
                if (!reader.TryReadUInt32(out uint type)) break;
                if (!reader.TryReadUInt32(out uint gesture)) break;
                if (!reader.TryReadSingle(out float time)) break;
                if (!reader.TryReadObfuscatedString(out string text) || !reader.TryAlign()) break;
                if (!reader.TryReadSingle(out float cdm)) break;

                components[id] = new SpellComponentInfo(id, name, category, icon, type, gesture, time, text, cdm);
            }

            return components;
        }
    }

    /// <summary>
    /// The secondary attribute table, file <c>0x0E000003</c>: how maximum health, stamina
    /// and mana derive from attributes. Three formulas in that order, each laid out as a
    /// skill's is (see <see cref="SkillTable"/>); ACE reads it the same way to work out a
    /// character's maximum vitals.
    /// </summary>
    public static class VitalTable
    {
        public const uint FileId = 0x0E000003;

        /// <summary>The formulas keyed by vital: 1 maximum health, 3 maximum stamina, 5 maximum mana.</summary>
        public static IReadOnlyDictionary<uint, SkillFormulaInfo> Parse(ReadOnlySpan<byte> data)
        {
            Dictionary<uint, SkillFormulaInfo> formulas = new Dictionary<uint, SkillFormulaInfo>();
            DatBinaryReader reader = new DatBinaryReader(data);

            if (!reader.TryReadUInt32(out _))       // the file's own id
                return formulas;

            foreach (uint vital in new uint[] { 1, 3, 5 })
            {
                if (!reader.TryReadUInt32(out _)) break;                 // w
                if (!reader.TryReadUInt32(out uint used)) break;         // x: zero when the formula is unused
                if (!reader.TryReadUInt32(out _)) break;                 // y
                if (!reader.TryReadUInt32(out uint divisor)) break;      // z
                if (!reader.TryReadUInt32(out uint attribute1)) break;
                if (!reader.TryReadUInt32(out uint attribute2)) break;

                formulas[vital] = used == 0
                    ? new SkillFormulaInfo(0, 0, 0)
                    : new SkillFormulaInfo(attribute1, attribute2, divisor);
            }

            return formulas;
        }
    }

    /// <summary>
    /// The experience table, file <c>0x0E000018</c>: what each level, and each rank of an
    /// attribute, vital or skill, costs. Laid out as ACE's ExperienceTable reads it - five counts,
    /// each one less than its table's length (attributes, vitals, trained skills, specialized
    /// skills, levels), then the tables in that order: 32-bit costs, but 64-bit totals for the
    /// levels.
    /// </summary>
    public static class ExperienceTable
    {
        public const uint FileId = 0x0E000018;

        /// <summary>
        /// The total experience each level needs, indexed by level: level 1 needs none, and the
        /// last entry is the highest level there is. Empty when the file cannot be read.
        /// </summary>
        public static IReadOnlyList<long> ParseLevels(ReadOnlySpan<byte> data)
        {
            DatBinaryReader reader = new DatBinaryReader(data);
            if (!reader.TryReadUInt32(out _))       // the file's own id
                return Array.Empty<long>();

            uint[] counts = new uint[5];
            for (int i = 0; i < counts.Length; i++)
            {
                if (!reader.TryReadUInt32(out uint last))
                    return Array.Empty<long>();

                counts[i] = last + 1;
            }

            // The four tables of ranks before the levels, four bytes an entry.
            long ranks = (long)counts[0] + counts[1] + counts[2] + counts[3];
            if (ranks * 4 + (long)counts[4] * 8 > reader.Remaining || !reader.TrySkip((int)(ranks * 4)))
                return Array.Empty<long>();

            long[] levels = new long[counts[4]];
            for (int i = 0; i < levels.Length; i++)
            {
                reader.TryReadUInt32(out uint low);
                reader.TryReadUInt32(out uint high);
                levels[i] = (long)(((ulong)high << 32) | low);
            }

            return levels;
        }
    }

    /// <summary>
    /// How one skill derives from attributes: the sum of one or two attributes
    /// divided by a constant.
    /// </summary>
    public readonly struct SkillFormulaInfo
    {
        internal SkillFormulaInfo(uint attribute1, uint attribute2, uint divisor)
        {
            Attribute1 = attribute1;
            Attribute2 = attribute2;
            Divisor = divisor;
        }

        /// <summary>1 strength, 2 endurance, 3 quickness, 4 coordination, 5 focus, 6 self. 0 for none.</summary>
        public uint Attribute1 { get; }

        public uint Attribute2 { get; }

        public uint Divisor { get; }

        public bool IsDerived => Attribute1 != 0 || Attribute2 != 0;
    }

    public sealed class SkillInfo
    {
        internal SkillInfo(uint id, string name, string description, SkillFormulaInfo formula)
        {
            Id = id;
            Name = name;
            Description = description;
            Formula = formula;
        }

        public uint Id { get; }

        public string Name { get; }

        public string Description { get; }

        public SkillFormulaInfo Formula { get; }

        public override string ToString() => $"{Id} {Name}";
    }

    /// <summary>
    /// The skill table, file <c>0x0E000004</c>. Its formulas are what the game
    /// actually uses to derive a skill's base value from attributes.
    /// </summary>
    public static class SkillTable
    {
        public const uint FileId = 0x0E000004;

        public static IReadOnlyDictionary<uint, SkillInfo> Parse(ReadOnlySpan<byte> data)
        {
            Dictionary<uint, SkillInfo> skills = new Dictionary<uint, SkillInfo>();
            DatBinaryReader reader = new DatBinaryReader(data);

            if (!reader.TryReadUInt32(out _))
                return skills;

            if (!reader.TryReadHashTableHeader(out int count))
                return skills;

            for (int i = 0; i < count; i++)
            {
                if (!reader.TryReadUInt32(out uint id))
                    break;

                if (!TryReadSkill(ref reader, id, out SkillInfo skill))
                    break;

                skills[id] = skill;
            }

            return skills;
        }

        private static bool TryReadSkill(ref DatBinaryReader reader, uint id, out SkillInfo skill)
        {
            skill = null;

            if (!reader.TryReadString(out string description)) return false;
            if (!reader.TryAlign()) return false;
            if (!reader.TryReadString(out string name)) return false;
            if (!reader.TryAlign()) return false;

            if (!reader.TrySkip(sizeof(uint))) return false;         // icon
            if (!reader.TrySkip(2 * sizeof(int))) return false;      // trained, specialized cost
            if (!reader.TrySkip(3 * sizeof(uint))) return false;     // category, chargen use, min level

            // The formula: w, x, y, z, then the two attributes. z is the divisor.
            if (!reader.TrySkip(3 * sizeof(uint))) return false;     // w, x, y
            if (!reader.TryReadUInt32(out uint divisor)) return false;
            if (!reader.TryReadUInt32(out uint attribute1)) return false;
            if (!reader.TryReadUInt32(out uint attribute2)) return false;

            if (!reader.TrySkip(3 * sizeof(double))) return false;   // upper, lower bound, learn mod

            skill = new SkillInfo(id, name, description, new SkillFormulaInfo(attribute1, attribute2, divisor));
            return true;
        }
    }

    /// <summary>
    /// A colour palette, file type <c>0x04......</c>: a flat list of colours that
    /// an object's palette slots index into.
    /// </summary>
    public static class PaletteFile
    {
        public static IReadOnlyList<uint> Parse(ReadOnlySpan<byte> data)
        {
            DatBinaryReader reader = new DatBinaryReader(data);

            if (!reader.TryReadUInt32(out _))                        // the file's own id
                return Array.Empty<uint>();

            if (!reader.TryReadInt32(out int count) || count < 0 || count > reader.Remaining / 4)
                return Array.Empty<uint>();

            uint[] colors = new uint[count];

            for (int i = 0; i < count; i++)
            {
                if (!reader.TryReadUInt32(out colors[i]))
                    return Array.Empty<uint>();
            }

            return colors;
        }
    }
}
