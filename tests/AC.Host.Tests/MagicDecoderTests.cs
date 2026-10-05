using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AC.Host.Decoding;
using AC.Host.World;
using AC.Protocol;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// What the character knows and has: its spellbook, its enchantments at login and as
    /// they change, and the maxima the server never sends. Read from captured bytes - two
    /// whole logins of the same character, one fresh and one wearing buffs cast an hour and
    /// a half earlier - so a passing test means the decoder agrees with the server's traffic.
    /// </summary>
    public class MagicDecoderTests
    {
        private const uint Player = 0x50000006;

        /// <summary>A login at full health, stamina and mana, with only its items' spells on.</summary>
        private static byte[] FullVitalsLogin => Resource("login-full-vitals.hex");

        /// <summary>A login wearing the player's own buffs, cast 84 to 85 minutes before.</summary>
        private static byte[] SelfBuffedLogin => Resource("login-self-buffed.hex");

        private static byte[] Resource(string name)
        {
            using Stream stream = typeof(MagicDecoderTests).Assembly.GetManifestResourceStream("AC.Host.Tests.Resources." + name);
            using StreamReader reader = new StreamReader(stream);
            return Convert.FromHexString(reader.ReadToEnd().Trim());
        }

        private static WorldState LoggedIn(byte[] login)
        {
            WorldState world = new WorldState();
            world.SetPlayerId(Player);
            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(login, world));
            return world;
        }

        private static DecodeOutcome ApplyEvent(byte[] payload, WorldState world)
            => MessageDecoder.Apply(AcMessage.Create(Opcodes.GameEvent, payload), PacketDirection.Inbound, world);

        private static DecodeOutcome ApplyEvent(string hex, WorldState world) => ApplyEvent(Convert.FromHexString(hex), world);

        private static DecodeOutcome ApplyClient(string hex, WorldState world)
            => MessageDecoder.Apply(AcMessage.Create(Opcodes.GameAction, Convert.FromHexString(hex)), PacketDirection.Outbound, world);

        // ------------------------------------------------------------------- the spellbook

        [Fact]
        public void ALoginListsEverySpellTheCharacterKnows()
        {
            WorldState world = LoggedIn(FullVitalsLogin);

            Assert.Equal(783, world.Character.Spellbook.Count);

            // The first and last entries of the captured table.
            Assert.True(world.Character.KnowsSpell(320));
            Assert.True(world.Character.KnowsSpell(6135));

            // Mana Drain Other I, which this character learned later in the session.
            Assert.False(world.Character.KnowsSpell(1219));
        }

        /// <summary>
        /// Learning Mana Drain Other I from a scroll, as the server said it, beside "You learn
        /// the Mana Drain Other I spell."
        /// </summary>
        private const string LearnedManaDrainHex = "06000050D8010000C1020000C3040000";

        [Fact]
        public void ASpellLearnedJoinsTheSpellbook()
        {
            WorldState world = LoggedIn(FullVitalsLogin);
            int updates = 0;
            world.CharacterUpdated += (_, _) => updates++;

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(LearnedManaDrainHex, world));

            Assert.True(world.Character.KnowsSpell(1219));
            Assert.Equal(784, world.Character.Spellbook.Count);
            Assert.Equal(1, updates);
        }

        /// <summary>
        /// No capture has a spell being forgotten. ACE writes MagicRemoveSpell exactly as it
        /// writes MagicUpdateSpell - the spell as a half-word and a zero half-word - so this is
        /// the captured learning with only the event number changed.
        /// </summary>
        [Fact]
        public void ASpellForgottenLeavesTheSpellbook()
        {
            WorldState world = LoggedIn(FullVitalsLogin);
            ApplyEvent(LearnedManaDrainHex, world);

            string forgotten = LearnedManaDrainHex.Replace("C1020000", "A8010000");
            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(forgotten, world));

            Assert.False(world.Character.KnowsSpell(1219));
        }

        // ------------------------------------------------------------------- enchantments at login

        [Fact]
        public void ALoginCarriesTheEnchantmentsAlreadyOn()
        {
            Assert.Equal(55, LoggedIn(FullVitalsLogin).Character.Enchantments.Count);
            Assert.Equal(87, LoggedIn(SelfBuffedLogin).Character.Enchantments.Count);
        }

        [Fact]
        public void AnItemsSpellNeverRunsOut()
        {
            WorldState world = LoggedIn(FullVitalsLogin);

            // Legendary Endurance, from something worn: permanent, cast by the item.
            Enchantment legendary = world.Character.Enchantments.Values.Single(e => e.SpellId == 6104);
            Assert.True(legendary.IsPermanent);
            Assert.Equal(double.PositiveInfinity, legendary.RemainingWhenSent);
            Assert.Equal(263u, legendary.Category);
            Assert.Equal(35f, legendary.StatModValue);
            Assert.Equal(2u, legendary.StatModKey);                 // endurance
            Assert.True(legendary.CasterId >= 0x80000000);
        }

        [Fact]
        public void TheCharactersOwnBuffsSayHowLongTheyHaveLeft()
        {
            WorldState world = LoggedIn(SelfBuffedLogin);

            // Incantation of Willpower Self: 90 minutes, cast 5115 seconds before the login.
            Enchantment willpower = world.Character.Enchantments.Values.Single(e => e.SpellId == 4329);
            Assert.Equal(Player, willpower.CasterId);
            Assert.Equal(5400.0, willpower.Duration);
            Assert.Equal(-5115.0, willpower.StartTime);
            Assert.Equal(285.0, willpower.RemainingWhenSent);
            Assert.Equal(11u, willpower.Category);
            Assert.Equal(400u, willpower.PowerLevel);
            Assert.False(willpower.IsPermanent);
        }

        [Fact]
        public void ALoginAnnouncesEachEnchantmentAndForgetsWhatIsNoLongerThere()
        {
            WorldState world = LoggedIn(SelfBuffedLogin);
            List<uint> removed = new List<uint>();
            int changed = 0;
            world.EnchantmentRemoved += (_, id) => removed.Add(id);
            world.EnchantmentChanged += (_, _) => changed++;

            // Logging in again, later, without the buffs: what the player had cast is gone.
            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(FullVitalsLogin, world));

            Assert.Equal(55, changed);
            Assert.Equal(55, world.Character.Enchantments.Count);
            Assert.Contains(0x000D10E9u, removed);                  // Incantation of Willpower Self, layer 13
        }

        // ------------------------------------------------------------------- maxima

        /// <summary>
        /// The server never sends a maximum. This login was at full health, stamina and mana,
        /// so its current values are the maxima: 280 endurance and 290 self, 196 ranks in each
        /// vital, and item spells worth 71 endurance, 60 self and 20 maximum mana - of which
        /// two layers of Mind Blossom count only once.
        /// </summary>
        [Fact]
        public void MaximumVitalsComeOutAsTheFullValuesALoginReports()
        {
            WorldState world = LoggedIn(FullVitalsLogin);
            CharacterState me = world.Character;

            Assert.Equal(372, me.GetVitalMaximum(1));
            Assert.Equal(547, me.GetVitalMaximum(3));
            Assert.Equal(566, me.GetVitalMaximum(5));

            Assert.Equal(me.Vitals[1].Current, (uint)me.GetVitalMaximum(1));
            Assert.Equal(me.Vitals[3].Current, (uint)me.GetVitalMaximum(3));
            Assert.Equal(me.Vitals[5].Current, (uint)me.GetVitalMaximum(5));

            Assert.Equal(351, me.GetAttributeCurrent(2));           // endurance, buffed
            Assert.Equal(280, (int)me.Attributes[2].Base);
        }

        /// <summary>
        /// Buffed, the same character's endurance is 361: half of it is 180.5, and the server
        /// makes that 181 - ACE rounds half away from zero - for the 377 health it reported.
        /// </summary>
        [Fact]
        public void AHalfPointOfHealthRoundsUpAsTheServerRoundsIt()
        {
            WorldState world = LoggedIn(SelfBuffedLogin);

            Assert.Equal(361, world.Character.GetAttributeCurrent(2));
            Assert.Equal(377, world.Character.GetVitalMaximum(1));
            Assert.Equal(557, world.Character.GetVitalMaximum(3));
            Assert.Equal(377u, world.Character.Vitals[1].Current);
            Assert.Equal(557u, world.Character.Vitals[3].Current);
        }

        [Fact]
        public void BuffedSkillsCountTheirEnchantments()
        {
            WorldState fresh = LoggedIn(FullVitalsLogin);
            WorldState buffed = LoggedIn(SelfBuffedLogin);

            // Creature Enchantment: trained, 208 ranks, (focus + self) / 4 of attributes, and
            // in the buffed login Incantation of Creature Enchantment Mastery Self's 45 more,
            // on top of the attributes' own buffs.
            int before = fresh.Character.GetSkillCurrent(31);
            int after = buffed.Character.GetSkillCurrent(31);

            Assert.True(after > before + 45, $"{before} then {after}");
            Assert.True(fresh.Character.GetSkillBase(31) >= 208);
        }

        // ------------------------------------------------------------------- enchantments as they change

        // The live enchantment the decoder tests read: Surge of Protection, then its removal.
        private const string SurgeHex =
            "0600005028020000C2020000561401007502010001000000000000000000000000000000000028400600005000000000008026C4000000000000000004900002340100000000A04100000000";

        /// <summary>
        /// The family is a half-word: Surge of Protection's is 629, 0x0275, as in the client's
        /// spell table - not 0x10275, which is what reading it as a word gave.
        /// </summary>
        [Fact]
        public void AnEnchantmentsFamilyIsTheClientsSpellCategory()
        {
            WorldState world = new WorldState();
            ApplyEvent(SurgeHex, world);

            Enchantment surge = world.Character.Enchantments.Values.Single();
            Assert.Equal(629u, surge.Category);
            Assert.Equal(12.0, surge.RemainingWhenSent);
        }

        /// <summary>
        /// Never captured: ACE sends several enchantments at once as a count followed by the
        /// records, each the same record as MagicUpdateEnchantment's. So this is the captured
        /// record twice under a count of two.
        /// </summary>
        [Fact]
        public void SeveralEnchantmentsArriveTogether()
        {
            WorldState world = new WorldState();
            string record = SurgeHex.Substring(24);
            string second = record.Replace("56140100", "56140200");     // the same spell, layer 2
            string hex = "0600005028020000C4020000" + "02000000" + record + second;

            List<Enchantment> changed = new List<Enchantment>();
            world.EnchantmentChanged += (_, e) => changed.Add(e);

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(hex, world));

            Assert.Equal(2, world.Character.Enchantments.Count);
            Assert.Equal(new[] { (ushort)1, (ushort)2 }, changed.Select(e => e.Layer));
        }

        /// <summary>
        /// Also never captured: removals and dispels of several at once are a count and then
        /// spell-and-layer words, each as the captured single removal names one.
        /// </summary>
        [Fact]
        public void SeveralEnchantmentsGoTogether()
        {
            WorldState world = LoggedIn(SelfBuffedLogin);
            List<uint> removed = new List<uint>();
            world.EnchantmentRemoved += (_, id) => removed.Add(id);

            // Incantation of Willpower Self layer 13 and Incantation of Endurance Self layer 10.
            string hex = "0600005028020000C5020000" + "02000000" + "E9100D00" + "CB100A00";
            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(hex, world));

            Assert.Equal(new uint[] { 0x000D10E9, 0x000A10CB }, removed);
            Assert.Equal(85, world.Character.Enchantments.Count);

            string dispel = "0600005028020000C8020000" + "01000000" + "C9100700";
            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(dispel, world));
            Assert.Equal(84, world.Character.Enchantments.Count);
        }

        /// <summary>
        /// Dying purges enchantments, and the event says only that. What goes is what ACE takes
        /// away: everything but what items give, which never runs out - and, because ACE spares
        /// by spell rather than by layer, the player's own casting of a spell an item also gives.
        /// This login has one: Incantation of Bludgeoning Protection Self, from a worn item at layer 12
        /// and from the player at layer 14.
        /// </summary>
        [Fact]
        public void DyingTakesAwayEverythingButWhatItemsGive()
        {
            WorldState world = LoggedIn(SelfBuffedLogin);
            HashSet<ushort> itemSpells = world.Character.Enchantments.Values.Where(e => e.IsPermanent).Select(e => e.SpellId).ToHashSet();

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent("0600005028020000C6020000", world));

            Assert.NotEmpty(world.Character.Enchantments);
            Assert.All(world.Character.Enchantments.Values, e => Assert.Contains(e.SpellId, itemSpells));
            Assert.Equal(new ushort[] { 4464 }, world.Character.Enchantments.Values.Where(e => e.CasterId == Player).Select(e => e.SpellId));
        }

        // ------------------------------------------------------------------- the player's own casts

        /// <summary>The player casting Nether Arc VII at a Maelstrom Shadow: target, then spell.</summary>
        private const string CastNetherArcHex = "160400004A0000002F2B0080F7140000";

        [Fact]
        public void ThePlayersOwnCastIsNoted()
        {
            WorldState world = new WorldState();

            Assert.Equal(DecodeOutcome.Applied, ApplyClient(CastNetherArcHex, world));

            Assert.Equal(5367u, world.Character.LastCastSpellId);
        }
    }
}
