using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using AC.Dat;
using Xunit;

namespace AC.Dat.Tests
{
    /// <summary>
    /// These read the real client_portal.dat, because that is the only thing that can
    /// show whether the parsers agree with the format. A synthetic DAT would only
    /// prove the reader agrees with the writer I would have written from the same
    /// assumptions.
    ///
    /// They skip when no DAT is present rather than fail, so the suite still runs on
    /// a machine without a game install.
    /// </summary>
    public class PortalDataTests
    {
        private static readonly string PortalPath = PortalData.FindPortalDat();

        private static bool Available => PortalPath != null;

        [SkippableFact]
        public void TheArchiveIndexesAndHoldsTheTablesWeNeed()
        {
            Skip.IfNot(Available, "No client_portal.dat on this machine.");

            using DatDatabase dat = DatDatabase.Open(PortalPath);

            // A wrong directory layout does not fail loudly - it yields a smaller,
            // plausible-looking index missing the files that matter.
            Assert.True(dat.FileCount > 50000, $"only indexed {dat.FileCount} files");
            Assert.True(dat.Contains(SpellTable.FileId), "spell table missing");
            Assert.True(dat.Contains(SkillTable.FileId), "skill table missing");
            Assert.Equal(1024u, dat.BlockSize);
        }

        [SkippableFact]
        public void SpellNamesComeOutAsWords()
        {
            Skip.IfNot(Available, "No client_portal.dat on this machine.");

            using PortalData portal = PortalData.Open(PortalPath);

            Assert.True(portal.Spells.Count > 2000, $"only parsed {portal.Spells.Count} spells");

            // Names are nibble-swapped on disk. Getting that wrong, or losing
            // alignment, yields high-byte noise rather than words - so assert the
            // whole table reads as text, not just that it parsed.
            int printable = portal.Spells.Values.Count(s =>
                !string.IsNullOrWhiteSpace(s.Name)
                && s.Name.All(c => c >= ' ' && c <= '~'));

            Assert.True(
                printable > portal.Spells.Count * 0.99,
                $"only {printable} of {portal.Spells.Count} spell names are printable text");
        }

        [SkippableFact]
        public void KnownSpellsAreFoundByName()
        {
            Skip.IfNot(Available, "No client_portal.dat on this machine.");

            using PortalData portal = PortalData.Open(PortalPath);

            // Spells every Asheron's Call install has. If the record walk drifted,
            // later spells would be garbage while early ones still looked fine, so
            // these are deliberately from across the table.
            foreach (string expected in new[] { "Strength Self I", "Heal Self I", "Lightning Bolt I", "Impenetrability I" })
            {
                Assert.True(
                    portal.Spells.Values.Any(s => string.Equals(s.Name, expected, StringComparison.Ordinal)),
                    $"did not find a spell named \"{expected}\"");
            }
        }

        [SkippableFact]
        public void SpellCategoriesGroupTheLevelsOfOneSpell()
        {
            Skip.IfNot(Available, "No client_portal.dat on this machine.");

            using PortalData portal = PortalData.Open(PortalPath);

            SpellInfo first = portal.Spells.Values.First(s => s.Name == "Strength Self I");
            SpellInfo second = portal.Spells.Values.First(s => s.Name == "Strength Self II");

            Assert.Equal(first.Category, second.Category);
            Assert.True(second.Power > first.Power, "the higher level should be the stronger");
        }

        [SkippableFact]
        public void SkillFormulasMatchTheGamesOwn()
        {
            Skip.IfNot(Available, "No client_portal.dat on this machine.");

            using PortalData portal = PortalData.Open(PortalPath);

            Assert.True(portal.Skills.Count > 30, $"only parsed {portal.Skills.Count} skills");

            // Melee Defense is (quickness + coordination) / 3 - the formula the
            // host had hard-coded. Reading it from the client is the point of this.
            Assert.True(portal.TryGetSkillFormula(6, out SkillFormulaInfo melee));
            Assert.Equal(3u, melee.Attribute1);   // quickness
            Assert.Equal(4u, melee.Attribute2);   // coordination
            Assert.Equal(3u, melee.Divisor);

            // Run is quickness alone, undivided.
            Assert.True(portal.TryGetSkillFormula(24, out SkillFormulaInfo run));
            Assert.Equal(3u, run.Attribute1);
            Assert.Equal(0u, run.Attribute2);
            Assert.Equal(1u, run.Divisor);
        }

        /// <summary>
        /// The rest of a spell's record: every field of all 6,266 spells in this file was
        /// compared with what ACE's own DAT reader makes of it, and these are a few of the
        /// values that comparison agreed on. A drift anywhere in the record shows up here.
        /// </summary>
        [SkippableFact]
        public void ASpellsLevelDurationTargetAndComponentsAreRead()
        {
            Skip.IfNot(Available, "No client_portal.dat on this machine.");

            using PortalData portal = PortalData.Open(PortalPath);

            SpellInfo strength = portal.GetSpell(2);
            Assert.Equal("Strength Self I", strength.Name);
            Assert.Equal(SpellTypes.Enchantment, strength.SpellType);
            Assert.Equal(1800.0, strength.Duration);
            Assert.Equal(15u, strength.BaseMana);
            Assert.True(strength.IsSelfTargeted);
            Assert.True(strength.IsBeneficial);
            Assert.Equal(new[] { "Lead Scarab", "Hyssop", "Powdered Moonstone", "Realgar", "Rowan Talisman" },
                strength.Components.Select(c => portal.GetComponent(c)?.Name));

            // The top of the Willpower family: level 400, an hour and a half, a Mana Scarab.
            SpellInfo willpower = portal.GetSpell(4329);
            Assert.Equal("Incantation of Willpower Self", willpower.Name);
            Assert.Equal(400u, willpower.Power);
            Assert.Equal(400u, willpower.Difficulty);
            Assert.Equal(5400.0, willpower.Duration);
            Assert.Equal(11u, willpower.Category);
            Assert.Equal("Mana Scarab", portal.GetComponent(willpower.Components[0]).Name);

            // Heal Self is a boost, not an enchantment: it lasts no time at all.
            SpellInfo heal = portal.GetSpell(6);
            Assert.Equal("Heal Self I", heal.Name);
            Assert.Equal(SpellTypes.Boost, heal.SpellType);
            Assert.Equal(0.0, heal.Duration);
            Assert.False(heal.IsEnchantment);

            // A war spell is cast at a creature.
            SpellInfo arc = portal.GetSpell(5367);
            Assert.Equal("Nether Arc VII", arc.Name);
            Assert.Equal(5u, arc.School);
            Assert.Equal(SpellTypes.Projectile, arc.SpellType);
            Assert.Equal(0x10u, arc.TargetType);
            Assert.False(arc.IsSelfTargeted);
        }

        [SkippableFact]
        public void ComponentsReadAsWords()
        {
            Skip.IfNot(Available, "No client_portal.dat on this machine.");

            using PortalData portal = PortalData.Open(PortalPath);

            Assert.Equal(163, portal.Components.Count);
            Assert.Contains(portal.Components.Values, c => c.Name == "Lead Scarab");
            Assert.Contains(portal.Components.Values, c => c.Name == "Prismatic Taper");
            Assert.All(portal.Components.Values, c => Assert.True(c.Name.All(ch => ch >= ' ' && ch <= '~'), c.Name));
        }

        /// <summary>
        /// Maximum health is half of endurance, stamina all of it, mana all of self - read
        /// from the file rather than assumed, as ACE reads it to work out a character's vitals.
        /// </summary>
        [SkippableFact]
        public void VitalFormulasMatchTheGamesOwn()
        {
            Skip.IfNot(Available, "No client_portal.dat on this machine.");

            using PortalData portal = PortalData.Open(PortalPath);

            Assert.True(portal.TryGetVitalFormula(1, out SkillFormulaInfo health));
            Assert.Equal((2u, 0u, 2u), (health.Attribute1, health.Attribute2, health.Divisor));

            Assert.True(portal.TryGetVitalFormula(3, out SkillFormulaInfo stamina));
            Assert.Equal((2u, 0u, 1u), (stamina.Attribute1, stamina.Attribute2, stamina.Divisor));

            Assert.True(portal.TryGetVitalFormula(5, out SkillFormulaInfo mana));
            Assert.Equal((6u, 0u, 1u), (mana.Attribute1, mana.Attribute2, mana.Divisor));
        }

        [SkippableFact]
        public void SkillNamesReadAsWords()
        {
            Skip.IfNot(Available, "No client_portal.dat on this machine.");

            using PortalData portal = PortalData.Open(PortalPath);

            Assert.True(portal.Skills.TryGetValue(6, out SkillInfo melee));
            Assert.Contains("Defense", melee.Name, StringComparison.OrdinalIgnoreCase);

            Assert.True(portal.Skills.TryGetValue(33, out SkillInfo life));
            Assert.Contains("Life", life.Name, StringComparison.OrdinalIgnoreCase);
        }

        [SkippableFact]
        public void PalettesYieldColours()
        {
            Skip.IfNot(Available, "No client_portal.dat on this machine.");

            using DatDatabase dat = DatDatabase.Open(PortalPath);
            using PortalData portal = PortalData.Open(PortalPath);

            // Any palette will do; take the first the archive actually holds.
            uint paletteId = dat.Files.Keys.Where(id => (id >> 24) == 0x04).OrderBy(id => id).First();

            IReadOnlyList<uint> colors = portal.GetPalette(paletteId);
            Assert.True(colors.Count > 0, $"palette 0x{paletteId:X8} came back empty");

            Color? sample = portal.GetSlotColor(paletteId, 0, 1);
            Assert.NotNull(sample);

            // A palette of nothing but transparent black would mean the colours were
            // read at the wrong offset.
            Assert.True(colors.Any(c => (c & 0x00FFFFFF) != 0), "every colour was black");
        }

        [SkippableFact]
        public void AnUnknownFileReadsAsNothingRatherThanThrowing()
        {
            Skip.IfNot(Available, "No client_portal.dat on this machine.");

            using DatDatabase dat = DatDatabase.Open(PortalPath);

            Assert.Null(dat.Read(0xDEADBEEF));
            Assert.False(dat.Contains(0xDEADBEEF));
        }

        [Fact]
        public void OpeningSomethingThatIsNotADatIsRejected()
        {
            string path = Path.Combine(Path.GetTempPath(), "not-a-dat-" + Guid.NewGuid().ToString("N") + ".bin");
            File.WriteAllBytes(path, new byte[0x200]);

            try
            {
                Assert.Throws<InvalidDataException>(() => DatDatabase.Open(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void AMissingFileIsReportedAsMissing()
        {
            Assert.Throws<FileNotFoundException>(
                () => DatDatabase.Open(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".dat")));
        }
    }
}
