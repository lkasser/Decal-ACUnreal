using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using AC.Dat;
using AC.Host.Decoding;
using AC.Host.World;
using AC.Protocol;
using Decal.Adapter;
using Decal.Adapter.Hosting;
using Decal.Adapter.Wrappers;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// Decal's character filter, answered from the host's character model: a whole login of the
    /// player's character, captured, read through the decoders and then asked what Decal's
    /// plugins ask - Virindi Reporter its experience, Global Inventory everything it keeps of a
    /// character, Virindi HUDs the experience to the next level.
    /// </summary>
    [Collection(DecalCollection.Name)]
    public class DecalCharacterFilterTests
    {
        /// <summary>The experience table's levels, with a level past 275 so that one is still to earn.</summary>
        private sealed class Levels : IGameData
        {
            private readonly Dictionary<int, long> _levels = new Dictionary<int, long>
            {
                [1] = 0,
                [274] = 187_000_000_000,
                [275] = 191_226_310_247,
                [276] = 194_665_544_181,
            };

            public bool IsAvailable => true;

            public string GetSpellName(uint spellId) => null;

            public Color? GetSlotColor(uint paletteId, int offset, int length) => null;

            public bool TryGetSkillFormula(uint skillId, out uint attribute1, out uint attribute2, out uint divisor)
            {
                attribute1 = attribute2 = divisor = 0;
                return false;
            }

            public bool TryGetVitalFormula(uint vitalId, out uint attribute1, out uint attribute2, out uint divisor)
            {
                attribute1 = attribute2 = divisor = 0;
                return false;
            }

            public SpellInfo GetSpell(uint spellId) => null;

            public IReadOnlyCollection<SpellInfo> Spells => Array.Empty<SpellInfo>();

            public IReadOnlyList<SpellInfo> GetSpellsInCategory(uint category) => Array.Empty<SpellInfo>();

            public SpellInfo FindSpell(string name) => null;

            public SpellComponentInfo GetComponent(uint componentId) => null;

            public long? GetLevelExperience(int level) => _levels.TryGetValue(level, out long xp) ? xp : null;
        }

        private static byte[] SelfBuffedLogin()
        {
            using Stream stream = typeof(DecalCharacterFilterTests).Assembly.GetManifestResourceStream("AC.Host.Tests.Resources.login-self-buffed.hex");
            using StreamReader reader = new StreamReader(stream);
            return Convert.FromHexString(reader.ReadToEnd().Trim());
        }

        private static void Apply(WorldState world, WireWriter message, PacketDirection direction = PacketDirection.Inbound)
            => Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(message.ToMessage(), direction, world));

        /// <summary>
        /// A host that has seen what a login shows: the character list, the server's name, then
        /// the character - Testchar I, level 275, as captured.
        /// </summary>
        private static MacroTestHost LoggedIn(IGameData levels = null)
        {
            MacroTestHost host = new MacroTestHost();
            WorldState world = host.WorldState;
            world.GameData = levels ?? new Levels();

            Apply(world, new WireWriter(Opcodes.CharacterList).U32(0).U32(2)
                .U32(MacroTestHost.PlayerId).String16L("Testchar I").U32(0)
                .U32(0x50000007).String16L("Testchar II").U32(0)
                .U32(0).U32(11).String16L("testacct").U32(1).U32(1));
            Apply(world, new WireWriter(Opcodes.ServerName).I32(7).I32(128).String16L("Example Server"));

            world.GetOrAdd(MacroTestHost.PlayerId, out _).Name = "Testchar I";
            world.SetPlayerId(MacroTestHost.PlayerId);
            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(AcMessage.Create(Opcodes.GameEvent, SelfBuffedLogin()), PacketDirection.Inbound, world));
            return host;
        }

        [Fact]
        public void TheExperienceIsTheCharactersAndTheNextLevelIsWhatTheTableStillAsks()
        {
            MacroTestHost host = LoggedIn();
            using DecalRuntime runtime = new DecalRuntime(host);
            CharacterFilter character = runtime.Core.CharacterFilter;

            Assert.Equal(275, character.Level);
            Assert.Equal(191_226_310_247L, character.TotalXP);
            Assert.Equal(80_012_393_436L, character.UnassignedXP);

            // What Virindi Reporter worked out from TotalXP: 3.44 billion to go, not 194 billion.
            Assert.Equal(194_665_544_181L - 191_226_310_247L, character.XPToNextLevel);

            // At the table's last level, and without the client's data, none.
            MacroTestHost topped = new MacroTestHost();
            topped.WorldState.GameData = new Levels();
            topped.WorldState.Character.Object.Ints[25] = 276;
            topped.WorldState.Character.Object.Int64s[1] = 194_665_544_181L;
            using (DecalRuntime second = new DecalRuntime(topped))
                Assert.Equal(0, second.Core.CharacterFilter.XPToNextLevel);
        }

        [Fact]
        public void WithoutTheClientsDataTheNextLevelIsNotWorkedOut()
        {
            MacroTestHost host = LoggedIn(NullGameData.Instance);
            using DecalRuntime runtime = new DecalRuntime(host);

            Assert.Equal(0, runtime.Core.CharacterFilter.XPToNextLevel);
            Assert.Equal(191_226_310_247L, runtime.Core.CharacterFilter.TotalXP);
        }

        /// <summary>Everything Virindi Global Inventory keeps of a character, as Decal answered it.</summary>
        [Fact]
        public void WhatGlobalInventoryKeepsOfACharacterIsFilledIn()
        {
            MacroTestHost host = LoggedIn();
            using DecalRuntime runtime = new DecalRuntime(host);
            CharacterFilter character = runtime.Core.CharacterFilter;
            ICharacterView model = host.WorldState.Character;

            // The account, from the character list; the server, as it announced itself.
            Assert.Equal("testacct", character.AccountName);
            Assert.Equal("Example Server", character.Server);
            Assert.Equal(7, character.ServerPopulation);
            Assert.Equal(new[] { "Testchar I", "Testchar II" }, character.Characters.Select(c => c.Name));
            Assert.Equal(new[] { 0, 1 }, character.Characters.Select(c => c.Index));
            Assert.Equal(0x50000007, character.Characters[1].Id);

            // Who the character is: a Sho, male, made as an Adventurer.
            Assert.Equal("Testchar I", character.Name);
            Assert.Equal("Sho", character.Race);
            Assert.Equal("Male", character.Gender);
            Assert.Equal("Adventurer", character.ClassTemplate);
            Assert.Equal(48, character.Deaths);
            Assert.Equal(4, character.SkillPoints);
            Assert.Equal(6249033, character.Age);

            // The burden, in units and as a share of what 150 a point of strength, and 30 more a
            // point for each of the three Might of the Seventh Mule, can carry.
            Assert.Equal(29035, character.BurdenUnits);
            int strength = model.GetAttributeCurrent(1);
            Assert.Equal(29035 * 100 / (150 * strength + 30 * 3 * strength), character.Burden);
            Assert.InRange(character.Burden, 1, 99);

            // The spellbook, and the spells known by it.
            Assert.Equal(model.Spellbook.Count, character.SpellBook.Count);
            Assert.True(character.IsSpellKnown(320));
            Assert.False(character.IsSpellKnown(1219));
            Assert.Equal(character.SpellBook.OrderBy(s => s), character.SpellBook);

            // The augmentations are the eAugmentations properties the character has.
            Assert.Contains(230, character.Augmentations);
            Assert.Equal(3, character.GetCharProperty(230));

            // No vitae; and no allegiance, which Decal gave as empty rather than as nothing.
            Assert.Equal(0, character.Vitae);
            Assert.NotNull(character.Monarch);
            Assert.Equal(0, character.Monarch.Id);
            Assert.Equal(string.Empty, character.Patron.Name);
            Assert.Empty(character.Vassals);
        }

        /// <summary>The login's options, shortcuts and spell bars, as its description carries them.</summary>
        [Fact]
        public void TheOptionsShortcutsAndSpellBarsAreTheLogins()
        {
            MacroTestHost host = LoggedIn();
            using DecalRuntime runtime = new DecalRuntime(host);
            CharacterFilter character = runtime.Core.CharacterFilter;

            Assert.Equal(0x70F4A542, character.CharacterOptions);
            Assert.Equal(0x01108700, character.CharacterOptionFlags);

            Assert.Equal(unchecked((int)2147551481u), character.Shortcut(0));
            Assert.Equal(unchecked((int)2147487104u), character.Shortcut(8));
            Assert.Equal(0, character.Shortcut(2));

            Assert.Equal(17, character.SpellBar(0).Count);
            Assert.Equal(new[] { 2073, 193, 1679, 1636, 2644 }, character.SpellBar(0).Take(5));
            Assert.Equal(11, character.SpellBar(1).Count);
            Assert.Empty(character.SpellBar(6));

            // Decal let a plugin ask for bars 0 to 6 only.
            Assert.Throws<ArgumentOutOfRangeException>(() => character.SpellBar(7));
            Assert.Throws<ArgumentOutOfRangeException>(() => character.SpellBar(-1));
        }

        /// <summary>
        /// Attributes, skills and vitals before and after enchantments - this login wears the
        /// player's own buffs - as the host works out what the server uses.
        /// </summary>
        [Fact]
        public void BuffedAttributesSkillsAndVitalsAreWhatTheServerUses()
        {
            MacroTestHost host = LoggedIn();
            using DecalRuntime runtime = new DecalRuntime(host);
            CharacterFilter character = runtime.Core.CharacterFilter;
            ICharacterView model = host.WorldState.Character;

            foreach (CharFilterAttributeType attribute in Enum.GetValues<CharFilterAttributeType>())
            {
                Assert.Equal(model.GetAttributeCurrent((uint)attribute), character.Attributes[attribute].Buffed);
                Assert.Equal(model.GetAttributeCurrent((uint)attribute), character.EffectiveAttribute[attribute]);
            }

            Assert.Contains(Enum.GetValues<CharFilterAttributeType>(), a => character.Attributes[a].Buffed > character.Attributes[a].Base);

            foreach (SkillInfoWrapper skill in character.Skills)
                Assert.True(skill.Buffed >= skill.Base, skill.Name);

            Assert.Contains(character.Skills, s => s.Buffed > s.Base);
            Assert.Equal(model.GetSkillCurrent((uint)CharFilterSkillType.MeleeDefense), character.EffectiveSkill[CharFilterSkillType.MeleeDefense]);

            SkillInfoWrapper health = character.Vitals[CharFilterVitalType.Health];
            Assert.Equal(model.GetVitalMaximum(1), health.Buffed);
            Assert.Equal(model.GetVitalMaximum(1), character.EffectiveVital[CharFilterVitalType.Health]);
            Assert.Equal((int)model.Vitals[1].Current, health.Current);
            Assert.Equal((int)model.Vitals[1].Ranks, health.Increment);
        }

        /// <summary>
        /// A character with vitae: the login's registry lists the cooldowns (8) before the vitae
        /// (4), which is one enchantment rather than a list - and Decal's Vitae is the penalty.
        /// </summary>
        [Fact]
        public void VitaeIsReadFromTheLoginAndIsThePenalty()
        {
            MacroTestHost host = new MacroTestHost();
            WireWriter description = WireWriter.GameEvent(MacroTestHost.PlayerId, GameEvents.PlayerDescription)
                .U32(0)                  // no property tables
                .U32(1)                  // weenie type
                .U32(0x0200)             // enchantments only
                .U32(1)                  // has health
                .U32(0x0004 | 0x0008);   // vitae and cooldowns
            description.U32(1);
            Enchantment(description, 0x8000_0000 | 1, 0, 1.0f);   // a cooldown
            Enchantment(description, 666, 204, 0.95f);             // vitae
            description.U32(0).U32(0);                             // the options: no flags, no options
            for (int bar = 0; bar < 8; bar++)
                description.U32(0);

            Apply(host.WorldState, description);
            using DecalRuntime runtime = new DecalRuntime(host);

            Assert.Equal(2, host.WorldState.Character.Enchantments.Count);
            Assert.Equal(5, runtime.Core.CharacterFilter.Vitae);
        }

        private static void Enchantment(WireWriter w, uint packedId, ushort category, float statModValue)
            => w.U32(packedId).U16(category).U16(0).U32(1)
                .F64(-10).F64(3600).U32(0)
                .F32(0).F32(0).F64(0)
                .U32(0).U32(0).F32(statModValue);

        /// <summary>
        /// The client's own changes, which the server never says back: a shortcut placed and taken
        /// away, a spell put on a bar and taken off, and the options panel as a whole.
        /// </summary>
        [Fact]
        public void TheClientsOwnChangesToShortcutsBarsAndOptionsAreKept()
        {
            MacroTestHost host = LoggedIn();
            WorldState world = host.WorldState;
            using DecalRuntime runtime = new DecalRuntime(host);
            CharacterFilter character = runtime.Core.CharacterFilter;

            Apply(world, new WireWriter(Opcodes.GameAction).U32(1).U32(GameActions.AddShortCut).U32(2).U32(0x80001234).U32(0), PacketDirection.Outbound);
            Apply(world, new WireWriter(Opcodes.GameAction).U32(2).U32(GameActions.RemoveShortCut).U32(0), PacketDirection.Outbound);
            Assert.Equal(unchecked((int)0x80001234u), character.Shortcut(2));
            Assert.Equal(0, character.Shortcut(0));

            Apply(world, new WireWriter(Opcodes.GameAction).U32(3).U32(GameActions.AddSpellFavorite).U32(4321).U32(1).U32(2), PacketDirection.Outbound);
            Apply(world, new WireWriter(Opcodes.GameAction).U32(4).U32(GameActions.RemoveSpellFavorite).U32(2073).U32(0), PacketDirection.Outbound);
            Assert.Equal(new[] { 4321 }, character.SpellBar(2));
            Assert.Equal(16, character.SpellBar(0).Count);
            Assert.DoesNotContain(2073, character.SpellBar(0));

            WireWriter options = new WireWriter(Opcodes.GameAction).U32(5).U32(GameActions.SetCharacterOptions)
                .U32(0x0001 | 0x0040).U32(0x12345678)
                .U32(1).U32(3).U32(0x80005555).U32(0);
            options.U32(1).U32(1234);
            for (int bar = 1; bar < 8; bar++)
                options.U32(0);
            options.U32(0x00010000);
            Apply(world, options, PacketDirection.Outbound);

            Assert.Equal(0x12345678, character.CharacterOptions);
            Assert.Equal(0x00010000, character.CharacterOptionFlags);
            Assert.Equal(unchecked((int)0x80005555u), character.Shortcut(3));
            Assert.Equal(0, character.Shortcut(2));
            Assert.Equal(new[] { 1234 }, character.SpellBar(0));
            Assert.Empty(character.SpellBar(1));
        }

        /// <summary>
        /// LoginComplete can come before the character's own description - the character's object
        /// described first - and what the description then says is the starting point, not 191
        /// billion experience gained and 783 spells learned.
        /// </summary>
        [Fact]
        public void WhatTheDescriptionSaysAfterLoginCompleteIsNotAChange()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            CharacterFilter character = runtime.Core.CharacterFilter;
            List<string> heard = new List<string>();
            character.LoginComplete += (_, _) => heard.Add("complete");
            character.ChangeExperience += (_, e) => heard.Add($"xp {e.Type} {e.Amount}");
            character.SpellbookChange += (_, e) => heard.Add($"spell {e.Type} {e.Spell}");
            runtime.CompleteStartup();

            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(AcMessage.Create(Opcodes.GameEvent, SelfBuffedLogin()), PacketDirection.Inbound, host.WorldState));
            Apply(host.WorldState, new WireWriter(Opcodes.PrivateUpdatePropertyInt64).U8(1).U32(1).U64(191_226_310_347UL));

            Assert.Equal(new[] { "complete", "xp Total 100" }, heard);
        }

        /// <summary>
        /// A login in ACE's order, as the live journal has it: the character list, the character's
        /// description - which names the character - then the server's word of it, its object, and
        /// the client's word that it has entered the world. Decal raised Login as the description
        /// filled its character filter, and LoginComplete on the client's word; so Virindi
        /// Reporter, which keeps the luminance itself from the description it hears in
        /// MessageProcessed and counts this session's from what it had at LoginComplete, counts
        /// from 36,200 - not from 0, which made all of it earned this session.
        /// </summary>
        [Fact]
        public void LoginCompleteComesOnTheClientsWordAfterTheDescriptionIsHeard()
        {
            MacroTestHost host = new MacroTestHost();
            host.WorldState.LeaveWorld("before this login");
            using DecalRuntime runtime = new DecalRuntime(host);
            CharacterFilter character = runtime.Core.CharacterFilter;
            runtime.CompleteStartup();

            // A listener that behaves as Virindi Reporter's luminance counting (`cXPCounting`) was
            // seen to: the luminance taken from each description it hears.
            List<string> heard = new List<string>();
            long luminance = 0;
            long luminanceAtStart = -1;
            runtime.Core.MessageProcessed += (_, e) =>
            {
                if (e.Message.Type != 0xF7B0 || e.Message.Value<int>("event") != 0x0013)
                    return;

                MessageStruct qwords = e.Message.Struct("properties").Struct("qwords");
                for (int i = 0; i < qwords.Count; i++)
                {
                    if (qwords.Struct(i).Value<int>("key") == 6)
                        luminance = qwords.Struct(i).Value<long>("value");
                }

                heard.Add($"described {luminance}");
            };
            character.Login += (_, e) => heard.Add($"login {e.Id:X8} {character.Name} level {character.Level} xp {character.TotalXP}");
            character.LoginComplete += (_, _) =>
            {
                luminanceAtStart = luminance;
                heard.Add("complete");
            };

            const uint Me = 0x50000006;
            host.Receive(new WireWriter(Opcodes.CharacterList).U32(0).U32(1)
                .U32(Me).String16L("Testchar I").U32(0)
                .U32(0).U32(11).String16L("testacct").U32(1).U32(1).ToMessage());
            host.Receive(AcMessage.Create(Opcodes.GameEvent, SelfBuffedLogin()));
            host.Receive(new WireWriter(Opcodes.PlayerCreate).U32(Me).ToMessage());
            host.Receive(WireWriter.ObjectCreate(Me, "Testchar I", 1, ItemTypes.Creature, 0).ToMessage());
            host.RaiseTick(TimeSpan.FromMilliseconds(50));
            Assert.Equal(new[] { $"login {Me:X8} Testchar I level 275 xp 191226310247", "described 36200" }, heard);

            host.Receive(MacroTestHost.ClientEntered().ToMessage(), PacketDirection.Outbound);
            Assert.Equal(new[] { $"login {Me:X8} Testchar I level 275 xp 191226310247", "described 36200", "complete" }, heard);
            Assert.Equal(36_200, luminanceAtStart);

            // Every portal after says the same; LoginComplete was once a login.
            host.Receive(MacroTestHost.ClientEntered(2).ToMessage(), PacketDirection.Outbound);
            Assert.Single(heard, h => h == "complete");
        }

        /// <summary>
        /// A login whose description never comes still has its Login before LoginComplete, on the
        /// client's word - and the character list names the character before anything else does.
        /// </summary>
        [Fact]
        public void TheClientsWordBringsLoginWhereNoDescriptionCame()
        {
            MacroTestHost host = new MacroTestHost();
            host.WorldState.LeaveWorld("before this login");
            using DecalRuntime runtime = new DecalRuntime(host);
            CharacterFilter character = runtime.Core.CharacterFilter;
            runtime.CompleteStartup();
            List<string> heard = new List<string>();
            character.Login += (_, e) => heard.Add($"login {e.Id:X8} {character.Name}");
            character.LoginComplete += (_, _) => heard.Add("complete");

            host.Receive(new WireWriter(Opcodes.CharacterList).U32(0).U32(1)
                .U32(MacroTestHost.PlayerId).String16L("Listed").U32(0)
                .U32(0).U32(11).String16L("testacct").U32(1).U32(1).ToMessage());
            host.Receive(new WireWriter(Opcodes.PlayerCreate).U32(MacroTestHost.PlayerId).ToMessage());
            host.RaiseTick(TimeSpan.FromMilliseconds(50));
            Assert.Empty(heard);

            host.Receive(MacroTestHost.ClientEntered().ToMessage(), PacketDirection.Outbound);
            Assert.Equal(new[] { $"login {MacroTestHost.PlayerId:X8} Listed" }, heard);

            // LoginComplete waits for the character's own object, then comes.
            host.Receive(WireWriter.ObjectCreate(MacroTestHost.PlayerId, "Tester", 1, ItemTypes.Creature, 0).ToMessage());
            host.RaiseTick(TimeSpan.FromMilliseconds(50));
            Assert.Equal(new[] { $"login {MacroTestHost.PlayerId:X8} Listed", "complete" }, heard);
            Assert.Equal("Tester", character.Name);
        }

        /// <summary>
        /// Decal's events for what the host hears: portal space, death, experience and the
        /// spellbook changing - counted from what the character had when it arrived.
        /// </summary>
        [Fact]
        public void PortalSpaceDeathExperienceAndTheSpellbookAreRaised()
        {
            MacroTestHost host = LoggedIn();
            WorldState world = host.WorldState;
            using DecalRuntime runtime = new DecalRuntime(host);
            CharacterFilter character = runtime.Core.CharacterFilter;
            List<string> heard = new List<string>();
            character.ChangePortalMode += (_, e) => heard.Add("portal " + e.Type);
            character.Death += (_, e) => heard.Add("death " + e.Text);
            character.ChangeExperience += (_, e) => heard.Add($"xp {e.Type} {e.Amount}");
            character.SpellbookChange += (_, e) => heard.Add($"spell {e.Type} {e.Spell}");
            runtime.CompleteStartup();
            Assert.Empty(heard);

            world.EnterPortalSpace(1);
            world.NoteLoginComplete();
            world.NotifyDied("You died!");

            Apply(world, new WireWriter(Opcodes.PrivateUpdatePropertyInt64).U8(1).U32(1).U64(191_226_311_247UL));
            Apply(world, new WireWriter(Opcodes.PrivateUpdatePropertyInt64).U8(2).U32(2).U64(80_012_393_436UL - 500));

            world.LearnSpell(1219);
            world.ForgetSpell(320);

            Assert.Equal(new[]
            {
                "portal EnterPortal",
                "portal ExitPortal",
                "death You died!",
                "xp Total 1000",
                "xp Unassigned -500",
                "spell Add 1219",
                "spell Delete 320",
            }, heard);
        }
    }
}
