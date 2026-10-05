using System;

namespace AC.Host.World
{
    /// <summary>
    /// The attribute contribution to each skill's base value.
    /// </summary>
    /// <remarks>
    /// A skill's unbuffed value is its starting bonus plus points raised plus a
    /// share of one or two attributes - the share is what makes an untrained skill
    /// non-zero. The authoritative pairs and divisors are the SkillTable inside the
    /// client's portal DAT, which ACE reads at runtime; this is that table as the
    /// game shipped it, hard-coded until the host has a DAT reader. Division rounds
    /// to nearest, as ACE's does. A skill not listed here (Salvaging, Loyalty,
    /// Leadership, Deception...) takes nothing from attributes.
    ///
    /// <para>
    /// Division rounds half away from zero: ACE's rounding (its FloatExtensions.Round), not
    /// the banker's rounding .NET's Math.Round defaults to. The two differ whenever the sum
    /// divides to exactly one half - 394 focus and self over 4 is 99 to the server, 98 the
    /// other way - and a magic skill a point low can pick the wrong level of spell.
    /// </para>
    /// </remarks>
    public static class SkillFormula
    {
        private const uint Strength = 1;
        private const uint Endurance = 2;
        private const uint Quickness = 3;
        private const uint Coordination = 4;
        private const uint Focus = 5;
        private const uint Self = 6;

        public static int Base(uint skillId, SkillState skill, CharacterState character)
            => Base(skillId, skill, character, NullGameData.Instance);

        /// <summary>
        /// The skill's unbuffed value, using the client's own formula table when it
        /// is loaded and the built-in copy otherwise.
        /// </summary>
        public static int Base(uint skillId, SkillState skill, CharacterState character, IGameData gameData)
        {
            long value = skill.InitLevel + skill.Ranks + AttributeShare(skillId, character, gameData);
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        /// <summary>The attribute-derived part alone.</summary>
        public static uint AttributeShare(uint skillId, CharacterState character)
            => AttributeShare(skillId, character, NullGameData.Instance);

        /// <summary>
        /// The attribute-derived part, from the client's table when available.
        /// </summary>
        /// <remarks>
        /// The table in the portal file is what the game itself uses, so it is
        /// preferred wherever it can be read; the hard-coded version below is the
        /// same data as the game shipped it, kept for hosts running without the
        /// client's files.
        /// </remarks>
        public static uint AttributeShare(uint skillId, CharacterState character, IGameData gameData)
            => AttributeShare(skillId, character, gameData, buffed: false);

        /// <summary>
        /// The attribute-derived part, over the attributes as they stand with their
        /// enchantments when <paramref name="buffed"/>, which is what the server casts with.
        /// </summary>
        public static uint AttributeShare(uint skillId, CharacterState character, IGameData gameData, bool buffed)
        {
            uint A(uint id) => buffed ? (uint)Math.Max(0, character.AttributeCurrent(id)) : character.AttributeBase(id);

            if (gameData != null
                && gameData.TryGetSkillFormula(skillId, out uint attribute1, out uint attribute2, out uint divisor))
            {
                uint sum = (attribute1 != 0 ? A(attribute1) : 0) + (attribute2 != 0 ? A(attribute2) : 0);

                if (sum == 0)
                    return 0;

                return divisor <= 1 ? sum : Divide(sum, divisor);
            }

            return AttributeShareFromBuiltInTable(skillId, A);
        }

        private static uint Divide(uint sum, uint divisor) => (uint)Math.Round(sum / (double)divisor, MidpointRounding.AwayFromZero);

        private static uint AttributeShareFromBuiltInTable(uint skillId, Func<uint, uint> A)
        {
            uint Share(uint sum, uint divisor) => divisor == 1 ? sum : Divide(sum, divisor);

            switch (skillId)
            {
                case 6: return Share(A(Quickness) + A(Coordination), 3);   // Melee Defense
                case 7: return Share(A(Quickness) + A(Coordination), 5);   // Missile Defense
                case 14: return Share(A(Focus), 3);                          // Arcane Lore
                case 15: return Share(A(Focus) + A(Self), 7);              // Magic Defense
                case 16: return Share(A(Focus) + A(Self), 6);              // Mana Conversion
                case 18: return Share(A(Coordination) + A(Focus), 2);      // Item Tinkering
                case 19: return Share(A(Focus) + A(Self), 2);              // Assess Person
                case 21: return Share(A(Coordination) + A(Focus), 3);      // Healing
                case 22: return Share(A(Strength) + A(Coordination), 2);   // Jump
                case 23: return Share(A(Coordination) + A(Focus), 3);      // Lockpick
                case 24: return A(Quickness);                          // Run
                case 27: return Share(A(Focus) + A(Self), 2);              // Assess Creature
                case 28: return Share(A(Strength) + A(Focus), 2);          // Weapon Tinkering
                case 29: return Share(A(Endurance) + A(Focus), 2);         // Armor Tinkering
                case 30: return A(Focus);                              // Magic Item Tinkering
                case 31:                                               // Creature Enchantment
                case 32:                                               // Item Enchantment
                case 33:                                               // Life Magic
                case 34:                                               // War Magic
                case 43: return Share(A(Focus) + A(Self), 4);              // Void Magic
                case 37:                                               // Fletching
                case 38:                                               // Alchemy
                case 39: return Share(A(Coordination) + A(Focus), 3);      // Cooking
                case 41: return Share(A(Strength) + A(Coordination), 3);   // Two Handed Combat
                case 44:                                               // Heavy Weapons
                case 45:                                               // Light Weapons
                case 46: return Share(A(Strength) + A(Coordination), 3);   // Finesse Weapons
                case 47: return Share(A(Coordination), 2);                   // Missile Weapons
                case 48: return Share(A(Strength) + A(Coordination), 2);   // Shield
                case 49: return Share(A(Strength) + A(Coordination), 3);   // Dual Wield
                case 50: return Share(A(Strength) + A(Quickness), 3);      // Recklessness
                case 51: return Share(A(Coordination) + A(Quickness), 3);  // Sneak Attack
                case 52: return Share(A(Strength) + A(Coordination), 3);   // Dirty Fighting
                case 54: return Share(A(Endurance) + A(Self), 3);          // Summoning

                // Retired weapon skills, still present on old characters.
                case 1: case 4: case 5: case 9: case 10: case 11: case 13:
                    return Share(A(Strength) + A(Coordination), 3);
                case 2: case 3: case 8: case 12:
                    return Share(A(Coordination), 2);

                default: return 0;
            }
        }
    }
}
