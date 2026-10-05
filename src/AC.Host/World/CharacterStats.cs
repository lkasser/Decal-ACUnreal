using System;
using System.Collections.Generic;
using System.Linq;

namespace AC.Host.World
{
    /// <summary>
    /// A character's buffed attributes, skills and maximum vitals, worked out the way the
    /// server works them out.
    /// </summary>
    /// <remarks>
    /// The server never sends these. It sends ranks, starting values and enchantments, and
    /// leaves the client to add them up - so a plugin that wants to know whether its health
    /// is at 75% of its maximum, or whether its skill is high enough for a spell, has to add
    /// them up too.
    ///
    /// <para>
    /// Every step here is ACE's, read from its own code (CreatureAttribute.GetCurrent,
    /// CreatureSkill.Current, CreatureVital.GetMaxValue, and the enchantment manager's
    /// top-layer rule): the formula from the client's tables over buffed attributes; the
    /// multiplicative enchantments, then vitae, then the additive ones; rounding half away
    /// from zero. Only the strongest enchantment of each spell family counts - highest
    /// level, then the most recent - which is why two layers of the same buff do not stack.
    /// </para>
    ///
    /// <para>
    /// A login's own vitals prove it: a character with 280 endurance, 290 self, 196 ranks in
    /// each vital and a handful of item spells reported 372, 547 and 566 as its current
    /// health, stamina and mana, full - and this arrives at exactly those maxima.
    /// </para>
    /// </remarks>
    internal static class CharacterStats
    {
        private const uint Endurance = 2;

        // The player's own integer properties that feed the formulas.
        private const uint AugmentationSkilledMelee = 0x012C;
        private const uint AugmentationSkilledMissile = 0x012D;
        private const uint AugmentationSkilledMagic = 0x012E;
        private const uint AugmentationJackOfAllTrades = 0x0146;
        private const uint LumAugSkilledSpec = 0x0158;
        private const uint LumAugAllSkills = 0x016D;
        private const uint Enlightenment = 0x0186;

        // ACE's own skill sets, from Player and SkillHelper.
        private static readonly HashSet<uint> MeleeSkills = new HashSet<uint> { 45, 44, 46, 49, 41, 1, 4, 5, 9, 10, 11, 13 };
        private static readonly HashSet<uint> MissileSkills = new HashSet<uint> { 47, 2, 3, 8, 12 };
        private static readonly HashSet<uint> MagicSkills = new HashSet<uint> { 31, 32, 33, 43, 34 };
        private static readonly HashSet<uint> AttackSkills = new HashSet<uint> { 1, 2, 3, 4, 5, 8, 9, 10, 11, 12, 13, 46, 44, 45, 47, 41, 34, 33, 43, 49 };
        private static readonly HashSet<uint> DefenseSkills = new HashSet<uint> { 6, 7, 15, 48 };

        /// <summary>An attribute with its enchantments: never below 10, or 1 for one that started below 10.</summary>
        public static int AttributeCurrent(CharacterState character, uint attributeId)
        {
            if (!character.Attributes.TryGetValue(attributeId, out AttributeState attribute))
                return 0;

            uint baseValue = attribute.Base;
            IReadOnlyCollection<Enchantment> all = character.Enchantments.Values.ToList();

            float multiplier = Product(TopLayer(all, EnchantmentTypes.Multiplicative | EnchantmentTypes.Attribute, attributeId, handleMultiple: true));
            int additive = (int)Sum(TopLayer(all, EnchantmentTypes.Additive | EnchantmentTypes.Attribute, attributeId, handleMultiple: true), truncateEach: true);

            float value = Round(baseValue * multiplier + additive);
            return (int)Math.Max(value, baseValue < 10 ? 1 : 10);
        }

        /// <summary>A skill as the server uses it: ranks, attributes, augmentations and enchantments.</summary>
        public static int SkillCurrent(CharacterState character, uint skillId, IGameData gameData)
        {
            if (!character.Skills.TryGetValue(skillId, out SkillState skill))
                return 0;

            IReadOnlyCollection<Enchantment> all = character.Enchantments.Values.ToList();

            // ACE leaves the attributes out of a skill that cannot be used - one untrained that
            // needs training - but the client's tables do not say which those are, so every
            // skill the character has gets its share.
            long value = Derived(character, gameData, skillId, isVital: false) + skill.InitLevel + skill.Ranks + AugmentationBase(character, skill);

            float multiplier = Product(TopLayer(all, EnchantmentTypes.Multiplicative | EnchantmentTypes.Skill, skillId, handleMultiple: true));
            float total = value * multiplier;

            float vitae = Vitae(all);
            if (vitae != 1f)
                total *= vitae;

            total += AugmentationCurrent(character, skill);

            int additive = (int)Sum(TopLayer(all, EnchantmentTypes.Additive | EnchantmentTypes.Skill, skillId, handleMultiple: true), truncateEach: true);
            if (DefenseSkills.Contains(skillId))
                additive += (int)Math.Round(Sum(TopLayer(all, EnchantmentTypes.Additive | EnchantmentTypes.Skill | EnchantmentTypes.DefenseSkills, 0, handleMultiple: false), truncateEach: false));
            if (AttackSkills.Contains(skillId))
                additive += (int)Math.Round(Sum(TopLayer(all, EnchantmentTypes.Additive | EnchantmentTypes.Skill | EnchantmentTypes.AttackSkills, 0, handleMultiple: false), truncateEach: false));

            return (int)Math.Max(0, Round(total + additive));
        }

        /// <summary>A maximum vital - 1 health, 3 stamina, 5 mana - with its enchantments.</summary>
        public static int VitalMaximum(CharacterState character, uint vitalId, IGameData gameData)
        {
            if (vitalId % 2 == 0)
                vitalId--;

            character.Vitals.TryGetValue(vitalId, out VitalState vital);

            long total = Derived(character, gameData, vitalId, isVital: true) + (vital?.StartingValue ?? 0) + (vital?.Ranks ?? 0);

            // Each level of enlightenment is two more health. Gear with a health rating adds
            // more still, but that rating is only known once each item has been appraised.
            if (vitalId == 1)
                total += Int(character, Enlightenment) * 2;

            IReadOnlyCollection<Enchantment> all = character.Enchantments.Values.ToList();

            float value = total * Product(TopLayer(all, EnchantmentTypes.Multiplicative | EnchantmentTypes.SecondAtt, vitalId, handleMultiple: true));

            float vitae = Vitae(all);
            if (vitae != 1f)
                value *= vitae;

            value += Sum(TopLayer(all, EnchantmentTypes.Additive | EnchantmentTypes.SecondAtt, vitalId, handleMultiple: true), truncateEach: false);

            return (int)Math.Max(Round(value), total < 5 ? 1 : 5);
        }

        /// <summary>
        /// The enchantments that count for one statistic: those of the right kind on the right
        /// key - or, where a statistic can be raised by a spell on several at once, those on
        /// all of them - and of those only the strongest of each family.
        /// </summary>
        internal static IEnumerable<Enchantment> TopLayer(IEnumerable<Enchantment> enchantments, uint type, uint key, bool handleMultiple)
        {
            uint single = handleMultiple ? type | EnchantmentTypes.SingleStat : type;
            uint multiple = type | EnchantmentTypes.MultipleStat;

            return enchantments
                .Where(e => ((e.StatModType & single) == single && e.StatModKey == key)
                    || (handleMultiple && (e.StatModType & multiple) == multiple && (e.StatModType & EnchantmentTypes.Vitae) == 0 && e.StatModKey == 0))
                .GroupBy(e => e.Category)
                .Select(family => family.OrderByDescending(e => e.PowerLevel).ThenByDescending(e => e.StartTime).First());
        }

        /// <summary>The vitae multiplier: one when the character has none.</summary>
        internal static float Vitae(IEnumerable<Enchantment> enchantments)
        {
            Enchantment vitae = enchantments.FirstOrDefault(e => e.IsVitae);
            return vitae != null ? vitae.StatModValue : 1f;
        }

        /// <summary>
        /// A formula from the client's tables over the character's buffed attributes. The
        /// host's built-in table stands in for a skill when no client data is loaded, and
        /// the well-known vital formulas for a vital.
        /// </summary>
        private static long Derived(CharacterState character, IGameData gameData, uint id, bool isVital)
        {
            uint attribute1 = 0, attribute2 = 0, divisor = 0;
            bool found = isVital
                ? gameData != null && gameData.TryGetVitalFormula(id, out attribute1, out attribute2, out divisor)
                : gameData != null && gameData.TryGetSkillFormula(id, out attribute1, out attribute2, out divisor);

            if (!found)
            {
                if (!isVital)
                    return SkillFormula.AttributeShare(id, character, gameData, buffed: true);

                // What the client's secondary attribute table holds: half of endurance for
                // health, all of it for stamina, all of self for mana.
                (attribute1, attribute2, divisor) = id switch
                {
                    1 => (Endurance, 0u, 2u),
                    3 => (Endurance, 0u, 1u),
                    5 => (6u, 0u, 1u),
                    _ => (0u, 0u, 0u),
                };
            }

            if (attribute1 == 0 && attribute2 == 0)
                return 0;

            long sum = (attribute1 != 0 ? AttributeCurrent(character, attribute1) : 0)
                     + (attribute2 != 0 ? AttributeCurrent(character, attribute2) : 0);

            return divisor <= 1 ? sum : (long)Round(sum / (float)divisor);
        }

        private static uint AugmentationBase(CharacterState character, SkillState skill)
        {
            uint bonus = (uint)Math.Max(0, Int(character, LumAugAllSkills));

            if (MeleeSkills.Contains(skill.SkillId))
                bonus += (uint)Math.Max(0, Int(character, AugmentationSkilledMelee) * 10);
            else if (MissileSkills.Contains(skill.SkillId))
                bonus += (uint)Math.Max(0, Int(character, AugmentationSkilledMissile) * 10);
            else if (MagicSkills.Contains(skill.SkillId))
                bonus += (uint)Math.Max(0, Int(character, AugmentationSkilledMagic) * 10);

            if (skill.AdvancementClass >= 2)
                bonus += (uint)Math.Max(0, Int(character, Enlightenment));

            return bonus;
        }

        private static uint AugmentationCurrent(CharacterState character, SkillState skill)
        {
            int bonus = Int(character, AugmentationJackOfAllTrades) * 5;
            if (skill.AdvancementClass == 3)
                bonus += Int(character, LumAugSkilledSpec) * 2;
            return (uint)Math.Max(0, bonus);
        }

        private static int Int(CharacterState character, uint property)
            => character.Object != null && character.Object.Ints.TryGetValue(property, out int value) ? value : 0;

        private static float Product(IEnumerable<Enchantment> enchantments)
        {
            float product = 1f;
            foreach (Enchantment e in enchantments)
                product *= e.StatModValue;
            return product;
        }

        private static float Sum(IEnumerable<Enchantment> enchantments, bool truncateEach)
        {
            float sum = 0f;
            foreach (Enchantment e in enchantments)
                sum += truncateEach ? (int)e.StatModValue : e.StatModValue;
            return sum;
        }

        /// <summary>ACE's rounding: half away from zero, not the banker's rounding .NET defaults to.</summary>
        internal static float Round(float value) => (float)Math.Round((double)value, MidpointRounding.AwayFromZero);
    }
}
