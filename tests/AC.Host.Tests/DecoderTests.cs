using System;
using System.Collections.Generic;
using System.Linq;
using AC.Host.Decoding;
using AC.Host.World;
using AC.Protocol;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// Every message here is built the way ACE writes it, so a passing test means the
    /// decoder agrees with the reference server's layout - not merely with itself.
    /// </summary>
    public class DecoderTests
    {
        private const uint Player = 0x50000001;
        private const uint Corpse = 0x80000010;
        private const uint Cap = 0x80000011;

        private static DecodeOutcome Apply(WorldState world, WireWriter w)
            => MessageDecoder.Apply(w.ToMessage(), PacketDirection.Inbound, world);

        [Fact]
        public void ObjectCreateProducesANamedTypedObjectWithItsCreationFields()
        {
            WorldState world = new WorldState();
            List<WorldObject> created = new List<WorldObject>();
            world.ObjectCreated += (_, o) => created.Add(o);

            WireWriter w = WireWriter.ObjectCreate(
                Cap, "Leather Cap", wcid: 1234, itemType: ItemTypes.Armor, descriptionFlags: 0,
                containerId: Corpse, value: 120, burden: 50, materialType: 0x35, workmanship: 7.5f,
                palettes: new[] { (0x0400123Au, (byte)2, (byte)40) });

            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));

            WorldObject obj = world.Get(Cap);
            Assert.NotNull(obj);
            Assert.Equal("Leather Cap", obj.Name);
            Assert.Equal(1234u, obj.WeenieClassId);
            Assert.Equal(0x06001234u, obj.IconId);
            Assert.Equal(ItemTypes.Armor, obj.ItemType);
            Assert.Equal(Corpse, obj.ContainerId);
            Assert.Equal(120, obj.Value);
            Assert.Equal((ushort)50, obj.Burden);
            Assert.Equal(0x35u, obj.MaterialType);
            Assert.Equal(7.5f, obj.Workmanship);
            Assert.NotNull(obj.Location);
            Assert.Equal(0xA9B40019u, obj.Location.Value.LandblockCell);
            Assert.Equal(84.0f, obj.Location.Value.X);
            Assert.Equal(0x09000001u, obj.MotionTableId);
            Assert.Equal(0x02000001u, obj.SetupId);
            Assert.Single(obj.Palettes);
            Assert.Equal(0x0400123Au, obj.Palettes[0].PaletteId);
            Assert.Equal(40, obj.Palettes[0].Length);
            Assert.Single(created);
            Assert.False(obj.HasAppraisalData);
        }

        [Fact]
        public void AWeenieClassIdAbove15BitsUsesThePackedTwoWordForm()
        {
            WorldState world = new WorldState();
            WireWriter w = WireWriter.ObjectCreate(Cap, "Big", wcid: 40000, itemType: ItemTypes.Misc, descriptionFlags: 0, value: 1);

            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));
            Assert.Equal(40000u, world.Get(Cap).WeenieClassId);
            Assert.Equal(1, world.Get(Cap).Value);
        }

        [Fact]
        public void ReCreatingAnObjectKeepsItsAppraisalData()
        {
            WorldState world = new WorldState();
            Apply(world, WireWriter.ObjectCreate(Cap, "Cap", 1, ItemTypes.Armor, 0));
            world.Get(Cap).Ints[28] = 200;
            world.Get(Cap).HasAppraisalData = true;

            Apply(world, WireWriter.ObjectCreate(Cap, "Cap", 1, ItemTypes.Armor, 0, value: 5));

            Assert.True(world.Get(Cap).HasAppraisalData);
            Assert.Equal(200, world.Get(Cap).Ints[28]);
            Assert.Equal(5, world.Get(Cap).Value);
            Assert.Equal(2, world.Get(Cap).CreateCount);
        }

        [Fact]
        public void ObjectDeleteRemovesTheObjectAndClearsContainmentReferences()
        {
            WorldState world = new WorldState();
            Apply(world, WireWriter.ObjectCreate(Corpse, "Corpse", 2, ItemTypes.Container, DescriptionFlags.Corpse));
            Apply(world, WireWriter.ObjectCreate(Cap, "Cap", 1, ItemTypes.Armor, 0, containerId: Corpse));

            List<uint> removed = new List<uint>();
            world.ObjectRemoved += (_, id) => removed.Add(id);

            WireWriter w = new WireWriter(Opcodes.ObjectDelete).U32(Corpse).U16(3).Align();
            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));

            Assert.Null(world.Get(Corpse));
            Assert.Equal(new[] { Corpse }, removed);
            Assert.Null(world.Get(Cap).ContainerId);
        }

        [Fact]
        public void PublicUpdatePropertyIntStoresUnderTheServerPropertyId()
        {
            WorldState world = new WorldState();
            Apply(world, WireWriter.ObjectCreate(Cap, "Cap", 1, ItemTypes.Armor, 0));

            WireWriter w = new WireWriter(Opcodes.PublicUpdatePropertyInt).U8(7).U32(Cap).U32(19).I32(500);
            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));

            Assert.Equal(500, world.Get(Cap).Ints[19]);
        }

        [Fact]
        public void PrivateUpdatePropertyIntTargetsThePlayerAndReportsCharacterChange()
        {
            WorldState world = new WorldState();
            world.SetPlayerId(Player);
            int characterUpdates = 0;
            world.CharacterUpdated += (_, _) => characterUpdates++;

            WireWriter w = new WireWriter(Opcodes.PrivateUpdatePropertyInt).U8(1).U32(25).I32(126);
            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));

            Assert.Equal(126, world.Character.Level);
            Assert.Equal(1, characterUpdates);
        }

        [Fact]
        public void APrivateUpdateBeforeThePlayerIsKnownIsIgnoredNotMisapplied()
        {
            WorldState world = new WorldState();
            WireWriter w = new WireWriter(Opcodes.PrivateUpdatePropertyInt).U8(1).U32(25).I32(126);

            Assert.Equal(DecodeOutcome.Ignored, Apply(world, w));
            Assert.Equal(0, world.ObjectCount);
        }

        [Fact]
        public void PublicUpdatePropertyStringHasThePropertyBeforeTheGuidAndAlignsTheString()
        {
            // The one property update whose field order differs from the others.
            WorldState world = new WorldState();
            Apply(world, WireWriter.ObjectCreate(Cap, "Cap", 1, ItemTypes.Armor, 0));

            WireWriter w = new WireWriter(Opcodes.PublicUpdatePropertyString).U8(1).U32(1).U32(Cap).Align().String16L("Renamed Cap");
            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));

            Assert.Equal("Renamed Cap", world.Get(Cap).Name);
            Assert.Equal("Renamed Cap", world.Get(Cap).Strings[1]);
        }

        [Fact]
        public void PublicUpdatePropertyFloatAndBoolAndInt64AndDataIdAllLand()
        {
            WorldState world = new WorldState();
            Apply(world, WireWriter.ObjectCreate(Cap, "Cap", 1, ItemTypes.Armor, 0));

            Assert.Equal(DecodeOutcome.Applied, Apply(world, new WireWriter(Opcodes.PublicUpdatePropertyFloat).U8(1).U32(Cap).U32(29).F64(0.125)));
            Assert.Equal(DecodeOutcome.Applied, Apply(world, new WireWriter(Opcodes.PublicUpdatePropertyBool).U8(1).U32(Cap).U32(69).U32(1)));
            Assert.Equal(DecodeOutcome.Applied, Apply(world, new WireWriter(Opcodes.PublicUpdatePropertyInt64).U8(1).U32(Cap).U32(1).U64(123456789012)));
            Assert.Equal(DecodeOutcome.Applied, Apply(world, new WireWriter(Opcodes.PublicUpdatePropertyDataId).U8(1).U32(Cap).U32(8).U32(0x06001111)));

            WorldObject obj = world.Get(Cap);
            Assert.Equal(0.125, obj.Floats[29]);
            Assert.True(obj.Bools[69]);
            Assert.Equal(123456789012L, obj.Int64s[1]);
            Assert.Equal(0x06001111u, obj.DataIds[8]);
        }

        [Fact]
        public void IdentifyResponsePopulatesTablesProfilesAndSpellsInWriteOrder()
        {
            WorldState world = new WorldState();
            Apply(world, WireWriter.ObjectCreate(Cap, "Leather Cap", 1, ItemTypes.Armor, 0));
            List<WorldObject> appraised = new List<WorldObject>();
            world.ObjectAppraised += (_, o) => appraised.Add(o);

            const uint IntTable = 0x0001, FloatTable = 0x0004, StringTable = 0x0008, SpellBook = 0x0010,
                       Weapon = 0x0020, Armor = 0x0080, ArmorLevels = 0x4000;

            WireWriter w = WireWriter.GameEvent(Player, GameEvents.IdentifyObjectResponse)
                .U32(Cap)
                .U32(IntTable | FloatTable | StringTable | SpellBook | Weapon | Armor | ArmorLevels)
                .U32(1)
                .IntTable(new Dictionary<uint, int> { [28] = 200, [171] = 2 })
                .FloatTable(new Dictionary<uint, double> { [29] = 0.15 })
                .StringTable(new Dictionary<uint, string> { [16] = "A fine cap." })
                .I32(2).U32(2650).U32(4425);

            // ArmorProfile: slash, pierce, bludgeon, cold, fire, acid, nether, lightning
            w.F32(1.0f).F32(1.1f).F32(1.2f).F32(1.3f).F32(1.4f).F32(1.5f).F32(1.6f).F32(1.7f);

            // WeaponProfile
            w.U32(4).U32(35).U32(44).U32(28).F64(0.42).F64(1.05).F64(0).F64(0).F64(0.1).U32(0);

            // ArmorLevels
            w.U32(200).U32(210).U32(220).U32(230).U32(240).U32(250).U32(260).U32(270).U32(280);

            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));

            WorldObject obj = world.Get(Cap);
            Assert.True(obj.HasAppraisalData);
            Assert.True(obj.AppraisalSucceeded);
            Assert.Equal(200, obj.Ints[28]);
            Assert.Equal(2, obj.Ints[171]);
            Assert.Equal(0.15, obj.Floats[29]);
            Assert.Equal("A fine cap.", obj.Strings[16]);
            Assert.Equal(new uint[] { 2650, 4425 }, obj.SpellIds);
            Assert.True(obj.Appraisal.HasArmor);
            Assert.Equal(1.5f, obj.Appraisal.ArmorProtection[5]);
            Assert.True(obj.Appraisal.HasWeapon);
            Assert.Equal(28u, obj.Appraisal.WeaponDamage);
            Assert.Equal(0.42, obj.Appraisal.WeaponVariance);
            Assert.Equal(0.1, obj.Appraisal.WeaponOffense);
            Assert.True(obj.Appraisal.HasArmorLevels);
            Assert.Equal(210u, obj.Appraisal.ArmorLevels[1]);
            Assert.Single(appraised);

            // The envelope named the player; a session joined late learns it here.
            Assert.Equal(Player, world.Character.Id);
        }

        [Fact]
        public void AFailedIdentifyStillMarksTheObjectAsAppraised()
        {
            // Otherwise a looter would ask for the same appraisal forever.
            WorldState world = new WorldState();
            Apply(world, WireWriter.ObjectCreate(Cap, "Cap", 1, ItemTypes.Armor, 0));

            WireWriter w = WireWriter.GameEvent(Player, GameEvents.IdentifyObjectResponse).U32(Cap).U32(0).U32(0);
            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));

            Assert.True(world.Get(Cap).HasAppraisalData);
            Assert.False(world.Get(Cap).AppraisalSucceeded);
        }

        [Fact]
        public void ViewContentsAssignsEachItemToTheContainer()
        {
            WorldState world = new WorldState();
            ContainerContents seen = null;
            world.ContainerViewed += (_, c) => seen = c;

            WireWriter w = WireWriter.GameEvent(Player, GameEvents.ViewContents)
                .U32(Corpse).U32(2).U32(Cap).U32(0).U32(0x80000012).U32(1);

            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));

            Assert.NotNull(seen);
            Assert.Equal(Corpse, seen.ContainerId);
            Assert.Equal(2, seen.Items.Count);
            Assert.Equal(Corpse, world.Get(Cap).ContainerId);
            Assert.Equal(1u, seen.Items[1].ContainerType);
        }

        [Fact]
        public void PutObjInContainerMovesTheItemAndWieldObjectEquipsIt()
        {
            WorldState world = new WorldState();
            world.SetPlayerId(Player);
            Apply(world, WireWriter.ObjectCreate(Cap, "Cap", 1, ItemTypes.Armor, 0, containerId: Corpse));

            Apply(world, WireWriter.GameEvent(Player, GameEvents.InventoryPutObjInContainer).U32(Cap).U32(Player).I32(0).U32(0));
            Assert.Equal(Player, world.Get(Cap).ContainerId);

            Apply(world, WireWriter.GameEvent(Player, GameEvents.WieldObject).U32(Cap).I32(1));
            Assert.Equal(Player, world.Get(Cap).WielderId);
            Assert.Equal(1u, world.Get(Cap).CurrentlyWieldedLocation);
            Assert.Null(world.Get(Cap).ContainerId);
        }

        /// <summary>
        /// ACE's InventoryServerSaveFailed: the item, then the error - zero for a full pack or too
        /// much to carry, whose reasons come in chat. The item stays where it was.
        /// </summary>
        [Fact]
        public void AMoveTheServerRefusesIsReportedWithItsItemAndError()
        {
            WorldState world = new WorldState();
            world.SetPlayerId(Player);
            Apply(world, WireWriter.ObjectCreate(Cap, "Cap", 1, ItemTypes.Armor, 0, containerId: Corpse));
            List<MoveRefusal> refused = new List<MoveRefusal>();
            world.MoveRefused += (_, refusal) => refused.Add(refusal);

            Apply(world, WireWriter.GameEvent(Player, GameEvents.InventoryServerSaveFailed).U32(Cap).U32(0));
            Apply(world, WireWriter.GameEvent(Player, GameEvents.InventoryServerSaveFailed).U32(Cap).U32(0x0036));

            Assert.Equal(new[] { (Cap, 0u), (Cap, 0x36u) }, refused.Select(r => (r.ItemId, r.Error)));
            Assert.Equal(Corpse, world.Get(Cap).ContainerId);

            WireWriter cut = WireWriter.GameEvent(Player, GameEvents.InventoryServerSaveFailed).U32(Cap);
            Assert.Equal(DecodeOutcome.Malformed, Apply(world, cut));
            Assert.Equal(2, refused.Count);
        }

        /// <summary>
        /// The order things came into their containers: taken when an object is created, and
        /// again when its container changes - what Virindi Tank's tracker listed a pack by.
        /// </summary>
        [Fact]
        public void ArrivalOrderFollowsCreationAndEachChangeOfContainer()
        {
            WorldState world = new WorldState();
            world.SetPlayerId(Player);
            Apply(world, WireWriter.ObjectCreate(Cap, "Cap", 1, ItemTypes.Armor, 0, containerId: Corpse));
            Apply(world, WireWriter.ObjectCreate(Cap + 1, "Sword", 1, ItemTypes.MeleeWeapon, 0, containerId: Player));
            Assert.True(world.Get(Cap).ArrivalOrder < world.Get(Cap + 1).ArrivalOrder);

            long before = world.Get(Cap).ArrivalOrder;
            Apply(world, WireWriter.GameEvent(Player, GameEvents.InventoryPutObjInContainer).U32(Cap).U32(Corpse).I32(0).U32(0));
            Assert.Equal(before, world.Get(Cap).ArrivalOrder);

            Apply(world, WireWriter.GameEvent(Player, GameEvents.InventoryPutObjInContainer).U32(Cap).U32(Player).I32(0).U32(0));
            Assert.True(world.Get(Cap).ArrivalOrder > world.Get(Cap + 1).ArrivalOrder);
        }

        [Fact]
        public void SpeechSystemAndTellBecomeChat()
        {
            WorldState world = new WorldState();
            List<ChatMessage> chat = new List<ChatMessage>();
            world.ChatReceived += (_, m) => chat.Add(m);

            Apply(world, new WireWriter(Opcodes.HearSpeech).String16L("hello there").String16L("Bob").U32(0x50000002).U32(2));
            Apply(world, new WireWriter(Opcodes.ServerMessage).String16L("You are too encumbered.").I32(5));
            Apply(world, WireWriter.GameEvent(Player, GameEvents.Tell).String16L("psst").String16L("Alice").U32(0x50000003).U32(Player).U32(3).U32(0));

            Assert.Equal(3, chat.Count);
            Assert.Equal(ChatKind.Speech, chat[0].Kind);
            Assert.Equal("hello there", chat[0].Text);
            Assert.Equal("Bob", chat[0].SenderName);
            Assert.Equal(ChatKind.System, chat[1].Kind);
            Assert.Equal(ChatKind.Tell, chat[2].Kind);
            Assert.Equal("Alice", chat[2].SenderName);
        }

        [Fact]
        public void CodedServerMessagesKeepTheirTemplateIdAndParameter()
        {
            // Seen live: ACE sends a chat-channel join as WeenieErrorWithString with
            // the channel name as the parameter. Presenting the parameter alone reads
            // as if it were the message, so both parts are kept.
            WorldState world = new WorldState();
            List<ChatMessage> chat = new List<ChatMessage>();
            world.ChatReceived += (_, m) => chat.Add(m);

            Apply(world, WireWriter.GameEvent(Player, GameEvents.WeenieErrorWithString).U32(0x051B).String16L("Allegiance"));
            Apply(world, WireWriter.GameEvent(Player, GameEvents.WeenieError).U32(0x051D));

            Assert.Equal(2, chat.Count);
            Assert.Equal(ChatKind.Coded, chat[0].Kind);
            Assert.Equal("Allegiance", chat[0].Text);
            Assert.Equal(0x051Bu, chat[0].ChatType);
            Assert.Contains("0x051B", chat[0].ToString());
            Assert.Contains("Allegiance", chat[0].ToString());

            Assert.Equal(ChatKind.Coded, chat[1].Kind);
            Assert.Empty(chat[1].Text);
            Assert.Equal(0x051Du, chat[1].ChatType);
        }

        [Fact]
        public void ServerNameAndPlayerCreateEstablishTheSession()
        {
            WorldState world = new WorldState();
            string server = null;
            uint player = 0;
            world.ServerConnected += (_, n) => server = n;
            world.PlayerIdentified += (_, id) => player = id;

            Apply(world, new WireWriter(Opcodes.ServerName).I32(3).I32(128).String16L("Kassian's ACE"));
            Apply(world, new WireWriter(Opcodes.PlayerCreate).U32(Player));

            Assert.Equal("Kassian's ACE", world.ServerName);
            Assert.Equal("Kassian's ACE", server);
            Assert.Equal(Player, world.Character.Id);
            Assert.Equal(Player, player);
        }

        [Fact]
        public void PlayerDescriptionLoadsLevelNameAttributesAndSkills()
        {
            WorldState world = new WorldState();
            world.SetPlayerId(Player);

            WireWriter w = WireWriter.GameEvent(Player, GameEvents.PlayerDescription)
                .U32(0x0001 | 0x0010)  // int and string tables
                .U32(1)                // weenie type
                .IntTable(new Dictionary<uint, int> { [25] = 126 })
                .StringTable(new Dictionary<uint, string> { [1] = "Kassian" })
                .U32(0x0003)           // attributes and skills
                .U32(1)                // has health
                .U32(0x1FF);           // every attribute and vital

            // Strength, Endurance, Quickness, Coordination, Focus, Self: ranks, start, xp
            w.U32(90).U32(100).U32(0)
             .U32(50).U32(100).U32(0)
             .U32(60).U32(100).U32(0)
             .U32(70).U32(100).U32(0)
             .U32(80).U32(100).U32(0)
             .U32(40).U32(100).U32(0);

            // Health, Stamina, Mana: ranks, start, xp, current
            w.U32(10).U32(0).U32(0).U32(250)
             .U32(20).U32(0).U32(0).U32(300)
             .U32(30).U32(0).U32(0).U32(350);

            // Skills: Melee Defense (6) specialized, Two Handed (41) untrained
            w.HashTableHeader(2)
             .U32(6).U16(50).U16(1).U32(3).U32(0).U32(10).U32(0).F64(0)
             .U32(41).U16(0).U16(1).U32(1).U32(0).U32(0).U32(0).F64(0);

            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));

            ICharacterView c = world.Character;
            Assert.Equal(126, c.Level);
            Assert.Equal("Kassian", c.Name);
            Assert.Equal(190u, c.Attributes[1].Base);
            Assert.Equal(250u, c.Vitals[1].Current);
            Assert.Equal(2, c.Skills.Count);
            Assert.True(c.Skills[6].IsSpecialized);

            // Melee Defense: init 10 + ranks 50 + (quickness 160 + coordination 170) / 3
            Assert.Equal(170, c.GetSkillBase(6));

            // Two Handed, untrained: only (strength 190 + coordination 170) / 3
            Assert.Equal(120, c.GetSkillBase(41));
        }

        [Fact]
        public void UpdatePositionHonoursTheQuaternionPresenceFlags()
        {
            WorldState world = new WorldState();
            Apply(world, WireWriter.ObjectCreate(Cap, "Cap", 1, ItemTypes.Armor, 0));

            // No W, X or Y components; Z present; velocity present; no placement.
            WireWriter w = new WireWriter(Opcodes.UpdatePosition).U32(Cap)
                .U32(0x08 | 0x10 | 0x20 | 0x01)
                .U32(0xA9B40019).F32(10).F32(20).F32(30)
                .F32(0.7f)                     // Z
                .F32(1).F32(2).F32(3)          // velocity
                .U16(1).U16(2).U16(3).U16(4);  // sequences

            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));

            Location l = world.Get(Cap).Location.Value;
            Assert.Equal(20f, l.Y);
            Assert.Equal(0.7f, l.QZ);
            Assert.Equal(0f, l.QW);
        }

        [Fact]
        public void PrivateUpdateSkillAndAttributeChangeTheCharacter()
        {
            WorldState world = new WorldState();
            world.SetPlayerId(Player);

            Apply(world, new WireWriter(Opcodes.PrivateUpdateAttribute).U8(1).U32(4).U32(70).U32(100).U32(0));
            Apply(world, new WireWriter(Opcodes.PrivateUpdateAttribute).U8(1).U32(3).U32(60).U32(100).U32(0));
            Apply(world, new WireWriter(Opcodes.PrivateUpdateSkill).U8(1).U32(6).U16(50).U16(1).U32(2).U32(0).U32(10).U32(0).F64(0));

            Assert.Equal(170u, world.Character.Attributes[4].Base);
            Assert.Equal(170, world.Character.GetSkillBase(6));
        }

        [Fact]
        public void ATruncatedObjectCreateIsMalformedNotThrown()
        {
            WorldState world = new WorldState();
            byte[] whole = WireWriter.ObjectCreate(Cap, "Leather Cap", 1, ItemTypes.Armor, 0, value: 1).ToArray();

            byte[] cut = new byte[40];
            System.Array.Copy(whole, cut, cut.Length);

            MessageAssembler assembler = new MessageAssembler();
            AcMessage message = null;
            foreach (AcFragment f in PacketWriter.Fragment(Opcodes.ObjectCreate, cut.AsSpan(4), 1))
                assembler.TryAccept(f, out message);

            Assert.Equal(DecodeOutcome.Malformed, MessageDecoder.Apply(message, PacketDirection.Inbound, world));
        }

        [Fact]
        public void AStanceOnlyMotionIsRecordedAndCountsAsNotMoving()
        {
            WorldState world = new WorldState();
            Apply(world, WireWriter.ObjectCreate(Cap, "Drudge", 1, ItemTypes.Creature, DescriptionFlags.Attackable));

            List<WorldObject> moved = new List<WorldObject>();
            world.ObjectMoved += (_, o) => moved.Add(o);

            WireWriter w = WireWriter.Motion(Cap, movementType: 0, stance: 0x3D)
                .InterpretedState(style: 0x3D, forward: MovementState.ReadyCommand);

            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));

            MovementState movement = world.Get(Cap).Movement;
            Assert.NotNull(movement);
            Assert.Equal(MovementKind.Interpreted, movement.Kind);
            Assert.Equal((ushort)0x3D, movement.Stance);

            // Standing ready is a motion message but not motion.
            Assert.False(movement.IsMoving);
            Assert.Single(moved);
        }

        [Fact]
        public void ARunForwardMotionCarriesItsCommandAndSpeed()
        {
            WorldState world = new WorldState();
            Apply(world, WireWriter.ObjectCreate(Cap, "Drudge", 1, ItemTypes.Creature, 0));

            WireWriter w = WireWriter.Motion(Cap, movementType: 0, stance: 0x3D)
                .InterpretedState(style: 0x3D, forward: 0x45, forwardSpeed: 1.5f, turn: 0x6D, turnSpeed: -1f);

            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));

            MovementState movement = world.Get(Cap).Movement;
            Assert.Equal((ushort)0x45, movement.ForwardCommand);
            Assert.Equal(1.5f, movement.ForwardSpeed);
            Assert.Equal((ushort)0x6D, movement.TurnCommand);
            Assert.Equal(-1f, movement.TurnSpeed);
            Assert.True(movement.IsMoving);
        }

        [Fact]
        public void SpeedsAreReadAfterCommandsNotInFlagOrder()
        {
            // The writer emits every command, then every speed - not interleaved in
            // bit order. Reading them interleaved would put a command where a speed
            // belongs and still parse, so this pins the order with values that would
            // be obviously wrong if swapped.
            WorldState world = new WorldState();
            Apply(world, WireWriter.ObjectCreate(Cap, "Drudge", 1, ItemTypes.Creature, 0));

            WireWriter w = WireWriter.Motion(Cap, movementType: 0, stance: 0)
                .InterpretedState(
                    forward: 0x0041, sidestep: 0x0042, turn: 0x0043,
                    forwardSpeed: 2f, sidestepSpeed: 3f, turnSpeed: 4f);

            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));

            MovementState movement = world.Get(Cap).Movement;
            Assert.Equal((ushort)0x0041, movement.ForwardCommand);
            Assert.Equal((ushort)0x0042, movement.SidestepCommand);
            Assert.Equal((ushort)0x0043, movement.TurnCommand);
            Assert.Equal(2f, movement.ForwardSpeed);
            Assert.Equal(3f, movement.SidestepSpeed);
            Assert.Equal(4f, movement.TurnSpeed);
        }

        [Fact]
        public void QueuedAnimationsAreSkippedWithoutLosingAlignment()
        {
            WorldState world = new WorldState();

            WireWriter w = WireWriter.Motion(Cap, movementType: 0, stance: 0x3D)
                .InterpretedState(style: 0x3D, forward: 0x45, queuedAnimations: 3);

            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));
            Assert.Equal((ushort)0x45, world.Get(Cap).Movement.ForwardCommand);
        }

        [Fact]
        public void AMoveToObjectNamesItsTarget()
        {
            WorldState world = new WorldState();

            WireWriter w = WireWriter.Motion(Cap, movementType: 6, stance: 0x3D)
                .U32(Corpse)
                .Origin()
                .MoveToParameters(desiredHeading: 45f)
                .F32(1.25f);                 // run rate

            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));

            MovementState movement = world.Get(Cap).Movement;
            Assert.Equal(MovementKind.MoveToObject, movement.Kind);
            Assert.Equal(Corpse, movement.TargetId);
            Assert.Equal(45f, movement.DesiredHeading);
            Assert.Equal(1.25f, movement.RunRate);
            Assert.True(movement.IsMoving);
        }

        [Fact]
        public void AMoveToPositionCarriesAnOriginNotAFullPosition()
        {
            // An origin is a cell and a point - no rotation. Reading a full position
            // here consumes sixteen bytes too many and wrecks everything after it.
            WorldState world = new WorldState();

            WireWriter w = WireWriter.Motion(Cap, movementType: 7, stance: 0x3D)
                .Origin()
                .MoveToParameters(desiredHeading: 90f)
                .F32(2f);

            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));

            MovementState movement = world.Get(Cap).Movement;
            Assert.Equal(MovementKind.MoveToPosition, movement.Kind);
            Assert.Equal(90f, movement.DesiredHeading);
            Assert.Equal(2f, movement.RunRate);
        }

        [Fact]
        public void ATurnToObjectCarriesItsHeadingBeforeItsParameters()
        {
            WorldState world = new WorldState();

            WireWriter w = WireWriter.Motion(Cap, movementType: 8, stance: 0x3D)
                .U32(Corpse)
                .F32(123f)                   // desired heading, before the parameters
                .TurnToParameters(desiredHeading: 456f);

            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));

            MovementState movement = world.Get(Cap).Movement;
            Assert.Equal(MovementKind.TurnToObject, movement.Kind);
            Assert.Equal(Corpse, movement.TargetId);

            // The parameters' heading is read last and wins.
            Assert.Equal(456f, movement.DesiredHeading);
        }

        [Fact]
        public void TheStickyFlagAppendsAGuidToAnInterpretedState()
        {
            WorldState world = new WorldState();

            WireWriter w = WireWriter.Motion(Cap, movementType: 0, stance: 0x3D, motionFlags: 0x01)
                .InterpretedState(style: 0x3D, forward: 0x45)
                .U32(Corpse);

            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));

            MovementState movement = world.Get(Cap).Movement;
            Assert.Equal(Corpse, movement.TargetId);
        }

        [Fact]
        public void ATurnToHeadingCarriesTheHeading()
        {
            WorldState world = new WorldState();

            WireWriter w = WireWriter.Motion(Cap, movementType: 9, stance: 0x3D)
                .TurnToParameters(desiredHeading: 270f);

            Assert.Equal(DecodeOutcome.Applied, Apply(world, w));

            MovementState movement = world.Get(Cap).Movement;
            Assert.Equal(MovementKind.TurnToHeading, movement.Kind);
            Assert.Equal(270f, movement.DesiredHeading);
        }

        [Fact]
        public void AStopMotionIsUnderstoodEvenThoughItCarriesNothingMore()
        {
            WorldState world = new WorldState();

            Assert.Equal(DecodeOutcome.Applied, Apply(world, WireWriter.Motion(Cap, movementType: 5, stance: 0x3D)));

            MovementState movement = world.Get(Cap).Movement;
            Assert.Equal(MovementKind.StopCompletely, movement.Kind);
            Assert.False(movement.IsMoving);
        }

        [Fact]
        public void ATruncatedMotionIsMalformedNotThrown()
        {
            WorldState world = new WorldState();
            byte[] whole = WireWriter.Motion(Cap, movementType: 0, stance: 0x3D)
                .InterpretedState(style: 0x3D, forward: 0x45, forwardSpeed: 1f)
                .ToArray();

            byte[] cut = new byte[16];
            Array.Copy(whole, cut, cut.Length);

            MessageAssembler assembler = new MessageAssembler();
            AcMessage message = null;
            foreach (AcFragment f in PacketWriter.Fragment(Opcodes.Motion, cut.AsSpan(4), 1))
                assembler.TryAccept(f, out message);

            Assert.Equal(DecodeOutcome.Malformed, MessageDecoder.Apply(message, PacketDirection.Inbound, world));
        }

        [Fact]
        public void UnknownOpcodesAndOutboundMessagesAreIgnored()
        {
            WorldState world = new WorldState();

            // 0xF7E5 is DDD_Interrogation, which has no decoder. Deliberately not 0xF74C, 0xF750
            // or 0xF658: each stood in for "unknown" here until its decoder landed.
            Assert.Equal(DecodeOutcome.Ignored, Apply(world, new WireWriter(0xF7E5).U32(1)));
            Assert.Equal(DecodeOutcome.Ignored, MessageDecoder.Apply(
                WireWriter.ObjectCreate(Cap, "Cap", 1, ItemTypes.Armor, 0).ToMessage(), PacketDirection.Outbound, world));
            Assert.Equal(0, world.ObjectCount);
        }

        [Fact]
        public void SetStackSizeUpdatesStackAndValue()
        {
            WorldState world = new WorldState();
            Apply(world, WireWriter.ObjectCreate(Cap, "Peas", 1, ItemTypes.Food, 0, stackSize: 5, value: 10));

            Apply(world, new WireWriter(Opcodes.SetStackSize).U8(1).U32(Cap).U32(25).U32(50));

            Assert.Equal((ushort)25, world.Get(Cap).StackSize);
            Assert.Equal(50, world.Get(Cap).Value);
        }

        // Bytes captured from AC:Unreal itself, so these vectors cannot inherit an
        // assumption from the decoder or from a test builder. The motion decoder's four
        // layout errors were all missed by tests that made the same wrong guesses as
        // the code, which is why these are raw.
        private const string StandingHex =
            "040000001CF6000003000000010000003D0000803000112B001FFE42E09A2A431F0540421FD0003F00000000000000002C3B5DBF000000000000000001000000";

        private const string WalkingHex =
            "170000001CF600001F000000020000003D00008005000045010000000000803F3000112B001FFE42E09A2A431D0040421FD0003F00000000000000002C3B5DBFBC0000000000000001000000";

        private const string WalkingAndTurningHex =
            "180000001CF600001F070000020000003D00008005000045010000000000803F0D000065010000000000803F3000112B806F0343A01F2843000040421FD0003F00000000000000002C3B5DBFBC0000000000000001000000";

        private const string PositionHex =
            "0500000053F700003000112B001FFE42E09A2A431F0540421FD0003F00000000000000002C3B5DBF000000000000000001000000";

        private static DecodeOutcome ApplyClient(string hex, WorldState world)
            => MessageDecoder.Apply(
                AcMessage.Create(Opcodes.GameAction, System.Convert.FromHexString(hex)),
                PacketDirection.Outbound,
                world);

        // Captured from a live session: selecting a creature (0x8000409E), the client's
        // examine request for it, and a deselect - a health query for object 0.
        private const string QueryHealthSelectHex = "10000000BF0100009E400080";
        private const string IdentifyObjectHex = "11000000C80000009E400080";
        private const string QueryHealthDeselectHex = "09000000BF01000000000000";

        /// <summary>
        /// The server is never told what the player has selected, only asked about it; those
        /// questions are the selection, and a question about nothing is a deselect.
        /// </summary>
        [Fact]
        public void TheClientsQuestionsAboutAnObjectAreWhatIsSelected()
        {
            WorldState world = new WorldState();

            Assert.Equal(DecodeOutcome.Applied, ApplyClient(QueryHealthSelectHex, world));
            Assert.Equal(0x8000409Eu, world.Character.SelectedId);

            Assert.Equal(DecodeOutcome.Applied, ApplyClient(IdentifyObjectHex, world));
            Assert.Equal(0x8000409Eu, world.Character.SelectedId);

            Assert.Equal(DecodeOutcome.Applied, ApplyClient(QueryHealthDeselectHex, world));
            Assert.Equal(0u, world.Character.SelectedId);
        }

        // Captured game events, bytes as the server sent them.
        private const string UseDoneHex = "0600005046000000C701000000000000";
        private const string CloseContainerHex = "060000506C00000052000000182D0080";

        // Combat, as the server sent it. The critical is the one blow in the session
        // that did 15 damage where the rest did between 1 and 4.
        private const string CriticalHitHex =
            "06000050F3000000B20100001100536B656C6574616C204368616D70696F6E00020000000000004029A5A43F0F00000008000000010000000000000000000000";

        // "Inferno", reporting damage type 0x10.
        private const string FireHitHex =
            "06000050E7020000B20100000700496E6665726E6F00000010000000000000608105663F0100000004000000000000000000000000000000";

        private const string EvadedHex = "060000504D000000B40100000B004175726F63682042756C6C000000";

        [Fact]
        public void ACriticalHitIsReportedWithItsDamageTypeAndBodyPart()
        {
            WorldState world = new WorldState();
            DamageTaken taken = null;
            world.DamageTaken += (_, d) => taken = d;

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(CriticalHitHex, world));

            Assert.Equal("Skeletal Champion", taken.Attacker);
            Assert.Equal(DamageTypes.Pierce, taken.DamageType);
            Assert.Equal(15u, taken.Amount);
            Assert.Equal(8u, taken.Location);
            Assert.True(taken.IsCritical);
            Assert.Equal(0.0403, taken.Percentage, 4);
        }

        /// <summary>
        /// A creature called Inferno reporting fire is the reason to believe the first
        /// word is a damage type rather than something else that happens to be small.
        /// </summary>
        [Fact]
        public void AFireAttackerReportsFire()
        {
            WorldState world = new WorldState();
            DamageTaken taken = null;
            world.DamageTaken += (_, d) => taken = d;

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(FireHitHex, world));

            Assert.Equal("Inferno", taken.Attacker);
            Assert.Equal(DamageTypes.Fire, taken.DamageType);
            Assert.Equal("fire", DamageTypes.Name(taken.DamageType));
            Assert.False(taken.IsCritical);
        }

        [Fact]
        public void AnEvadedAttackNamesTheAttackerAndNothingElse()
        {
            WorldState world = new WorldState();
            string evaded = null;
            world.AttackEvaded += (_, who) => evaded = who;

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(EvadedHex, world));

            Assert.Equal("Auroch Bull", evaded);
        }

        [Fact]
        public void ATruncatedBlowIsMalformedRatherThanHalfRead()
        {
            WorldState world = new WorldState();
            DamageTaken taken = null;
            world.DamageTaken += (_, d) => taken = d;

            // Cut off inside the trailing words, after the body part.
            string cut = CriticalHitHex.Substring(0, 96);

            Assert.Equal(DecodeOutcome.Malformed, ApplyEvent(cut, world));
            Assert.Null(taken);
        }

        [Fact]
        public void ABlowReadsAsASentence()
        {
            WorldState world = new WorldState();
            DamageTaken taken = null;
            world.DamageTaken += (_, d) => taken = d;
            ApplyEvent(CriticalHitHex, world);

            Assert.Equal("Skeletal Champion hit lower leg for 15 piercing (4.0%) - critical", taken.ToString());
        }

        // Three enchantments from one live session, bytes as the server sent them.
        // The first is a self-buff, the second a debuff a creature put on the player.
        private const string SelfBuffHex =
            "0600005028020000C2020000561401007502010001000000000000000000000000000000000028400600005000000000008026C4000000000000000004900002340100000000A04100000000";

        private const string DebuffHex =
            "0600005043020000C2020000A30501000A000100E100000000000000000000000000000000806640EB2C008000000000008026C4000000000000000001900000050000000000F0C100000000";

        private const string RemoveBuffHex = "060000504D020000C302000056140100";

        private static DecodeOutcome ApplyEvent(string hex, WorldState world)
            => MessageDecoder.Apply(
                AcMessage.Create(Opcodes.GameEvent, System.Convert.FromHexString(hex)),
                PacketDirection.Inbound,
                world);

        [Fact]
        public void ASelfBuffNamesItsSpellDurationAndCaster()
        {
            WorldState world = new WorldState();

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(SelfBuffHex, world));

            Enchantment buff = Assert.Single(world.Character.Enchantments.Values);

            // 5206 is "Surge of Protection" in the client's own spell table.
            Assert.Equal((ushort)5206, buff.SpellId);
            Assert.Equal((ushort)1, buff.Layer);
            Assert.Equal(12.0, buff.Duration);
            Assert.Equal(0x50000006u, buff.CasterId);
            Assert.Equal(20f, buff.StatModValue);
            Assert.Equal(308u, buff.StatModKey);
            Assert.False(buff.IsDebuff);

            // The retail client's sentinel. Landing here is what says the preceding
            // fields are the right sizes.
            Assert.Equal(-666f, buff.DegradeLimit);
        }

        [Fact]
        public void ADebuffFromACreatureIsRecognisedAsOne()
        {
            WorldState world = new WorldState();

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(DebuffHex, world));

            Enchantment debuff = Assert.Single(world.Character.Enchantments.Values);

            // 1443 is "Bafflement Other V".
            Assert.Equal((ushort)1443, debuff.SpellId);
            Assert.Equal(180.0, debuff.Duration);
            Assert.Equal(-30f, debuff.StatModValue);
            Assert.True(debuff.IsDebuff);

            // Not the player: something else put this here.
            Assert.Equal(0x80002CEBu, debuff.CasterId);
        }

        [Fact]
        public void BothCanBeOnTheCharacterAtOnce()
        {
            WorldState world = new WorldState();

            ApplyEvent(SelfBuffHex, world);
            ApplyEvent(DebuffHex, world);

            Assert.Equal(2, world.Character.Enchantments.Count);
        }

        [Fact]
        public void AnEnchantmentIsRemovedByTheIdItArrivedWith()
        {
            WorldState world = new WorldState();
            ApplyEvent(SelfBuffHex, world);
            ApplyEvent(DebuffHex, world);

            List<uint> removed = new List<uint>();
            world.EnchantmentRemoved += (_, id) => removed.Add(id);

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(RemoveBuffHex, world));

            // The buff goes; the debuff stays.
            Enchantment left = Assert.Single(world.Character.Enchantments.Values);
            Assert.Equal((ushort)1443, left.SpellId);
            Assert.Equal(new uint[] { 0x00011456 }, removed);
        }

        [Fact]
        public void RefreshingAnEnchantmentReplacesItRatherThanAddingASecond()
        {
            WorldState world = new WorldState();

            ApplyEvent(SelfBuffHex, world);
            ApplyEvent(SelfBuffHex, world);

            Assert.Single(world.Character.Enchantments);
        }

        [Fact]
        public void ATruncatedEnchantmentIsMalformed()
        {
            WorldState world = new WorldState();

            // Everything but the trailing spell-set word.
            string cut = SelfBuffHex.Substring(0, SelfBuffHex.Length - 8);

            Assert.Equal(DecodeOutcome.Malformed, ApplyEvent(cut, world));
            Assert.Empty(world.Character.Enchantments);
        }

        [Fact]
        public void AFinishedUseIsReportedWithItsError()
        {
            WorldState world = new WorldState();
            List<uint> finished = new List<uint>();
            world.UseFinished += (_, error) => finished.Add(error);

            DecodeOutcome outcome = MessageDecoder.Apply(
                AcMessage.Create(Opcodes.GameEvent, System.Convert.FromHexString(UseDoneHex)),
                PacketDirection.Inbound,
                world);

            Assert.Equal(DecodeOutcome.Applied, outcome);
            Assert.Equal(new uint[] { 0 }, finished);
        }

        [Fact]
        public void AClosedGroundContainerNamesWhichOne()
        {
            WorldState world = new WorldState();
            List<uint> closed = new List<uint>();
            world.ContainerClosed += (_, id) => closed.Add(id);

            DecodeOutcome outcome = MessageDecoder.Apply(
                AcMessage.Create(Opcodes.GameEvent, System.Convert.FromHexString(CloseContainerHex)),
                PacketDirection.Inbound,
                world);

            Assert.Equal(DecodeOutcome.Applied, outcome);
            Assert.Equal(new uint[] { 0x80002D18 }, closed);
        }

        [Fact]
        public void AUseDoneWithNoErrorCodeIsMalformed()
        {
            WorldState world = new WorldState();

            // The header alone, with the error word missing.
            string cut = UseDoneHex.Substring(0, 24);

            Assert.Equal(
                DecodeOutcome.Malformed,
                MessageDecoder.Apply(
                    AcMessage.Create(Opcodes.GameEvent, System.Convert.FromHexString(cut)),
                    PacketDirection.Inbound,
                    world));
        }

        [Fact]
        public void AStandingClientReportsAStyleAndNoMovement()
        {
            WorldState world = new WorldState();

            Assert.Equal(DecodeOutcome.Applied, ApplyClient(StandingHex, world));

            Assert.Equal(MotionCommands.Ready, world.Character.Motion.CurrentStyle);
            Assert.False(world.Character.Motion.IsMoving);

            Location where = world.Character.Location.Value;
            Assert.Equal(0x2B110030u, where.LandblockCell);
            Assert.Equal(0x2B11u, where.Landblock);
            Assert.Equal(127.06, where.X, 2);
            Assert.Equal(170.60, where.Y, 2);
            Assert.Equal(48.005, where.Z, 3);
        }

        [Fact]
        public void AWalkingClientReportsAForwardCommandAtFullSpeed()
        {
            WorldState world = new WorldState();

            Assert.Equal(DecodeOutcome.Applied, ApplyClient(WalkingHex, world));

            ClientMotionState motion = world.Character.Motion;
            Assert.Equal(MotionCommands.WalkForward, motion.ForwardCommand);
            Assert.Equal(1.0f, motion.ForwardSpeed);
            Assert.Equal(0u, motion.TurnCommand);
            Assert.True(motion.IsMoving);
        }

        [Fact]
        public void AClientWalkingAndTurningReportsBoth()
        {
            WorldState world = new WorldState();

            Assert.Equal(DecodeOutcome.Applied, ApplyClient(WalkingAndTurningHex, world));

            ClientMotionState motion = world.Character.Motion;
            Assert.Equal(MotionCommands.WalkForward, motion.ForwardCommand);
            Assert.Equal(MotionCommands.TurnRight, motion.TurnCommand);
            Assert.Equal(1.0f, motion.TurnSpeed);

            // The longer state must not eat into the position that follows it.
            Assert.Equal(0x2B110030u, world.Character.Location.Value.LandblockCell);
        }

        [Fact]
        public void APositionReportCarriesTheSequencesThatAuthoriseIt()
        {
            WorldState world = new WorldState();

            Assert.Equal(DecodeOutcome.Applied, ApplyClient(PositionHex, world));

            Assert.Equal(0x2B110030u, world.Character.Location.Value.LandblockCell);

            MovementSequences sequences = world.Character.Sequences;
            Assert.Equal((ushort)0, sequences.Instance);
            Assert.Equal(1u, sequences.Contact);

            // A position report says nothing about the body, so it must not invent one.
            Assert.Null(world.Character.Motion);
        }

        [Fact]
        public void ASequenceLaterInTheSessionIsRead()
        {
            WorldState world = new WorldState();
            ApplyClient(WalkingHex, world);

            Assert.Equal((ushort)188, world.Character.Sequences.Instance);
        }

        [Fact]
        public void ATruncatedMovementMessageIsMalformedRatherThanGuessedAt()
        {
            WorldState world = new WorldState();

            // Everything but the last four bytes: the contact flag is missing.
            string cut = WalkingHex.Substring(0, WalkingHex.Length - 8);

            Assert.Equal(DecodeOutcome.Malformed, ApplyClient(cut, world));
            Assert.Null(world.Character.Location);
        }

        [Fact]
        public void AClientActionWithNoDecoderIsIgnoredWithoutComplaint()
        {
            WorldState world = new WorldState();

            // 0x0036, Use, has no decoder: the server's reply says what came of it. (This was
            // 0x01BF until selection began to be read from it.)
            Assert.Equal(DecodeOutcome.Ignored, ApplyClient("0100000036000000DEADBEEF", world));
        }

        // Physics and effects, bytes as the server sent them. The jump is the first of
        // seventeen the client made; the projectile is a Nether Arc that had just hit.
        // The captured session's player is not the builder's.
        private const uint CapturedPlayer = 0x50000006;

        private const string PlayerJumpHex =
            "06000050BA5C33BFBCDF3CC15A9E8C40000000000000000000000000BC000100";

        private const string ProjectileStopHex =
            "172D008000000000000000000000000000000000000000000000000000000100";

        // The player leaving a portal, and a portcullis in landblock 0x2B11 opening.
        private const string PortalExitStateHex = "0600005008044000BC000100";
        private const string PortcullisOpenHex = "2A10B1721C00010000000100";

        // A Great Skeleton (0x80002B63) nocking a Deadly Acid Arrow (0x80002B65).
        private const string ArrowNockedHex = "632B0080652B0080010000000100000000000100";

        // A creature called Flare splattering, and a Nether Bolt exploding.
        private const string FlareSplatterHex = "692C0080640000000000803F";
        private const string NetherBoltExplodeHex = "172D0080050000000000803F";

        // The player picking something up at full volume, and Flare being hit at half.
        private const string PickUpSoundHex = "060000508F0000000000803F";
        private const string FlareHitSoundHex = "692C0080300000000000003F";

        private const string ChatChannelsHex =
            "060000500A0000009502000024140080020000000300000004000000050000000A00000007000000070000000800000009000000";

        // The Bowl (0x80002DD4) the client had just asked to drop.
        private const string DroppedBowlHex = "06000050D10100009A010000D42D0080";

        private static DecodeOutcome ApplyInbound(uint opcode, string hex, WorldState world)
            => MessageDecoder.Apply(
                AcMessage.Create(opcode, System.Convert.FromHexString(hex)),
                PacketDirection.Inbound,
                world);

        [Fact]
        public void AJumpSetsThePlayersVelocityAndCountsAsMotion()
        {
            WorldState world = new WorldState();
            List<WorldObject> moved = new List<WorldObject>();
            world.ObjectMoved += (_, o) => moved.Add(o);

            Assert.Equal(DecodeOutcome.Applied, ApplyInbound(Opcodes.VectorUpdate, PlayerJumpHex, world));

            Velocity v = world.Get(CapturedPlayer).Velocity.Value;
            Assert.Equal(-0.701, v.X, 3);
            Assert.Equal(-11.805, v.Y, 3);
            Assert.Equal(4.394, v.Z, 3);

            // The same 11.8 as every other running jump in the session.
            Assert.Equal(11.83, v.HorizontalSpeed, 2);
            Assert.False(v.IsZero);
            Assert.Single(moved);
        }

        [Fact]
        public void AProjectileComingToRestReportsNoVelocity()
        {
            WorldState world = new WorldState();

            Assert.Equal(DecodeOutcome.Applied, ApplyInbound(Opcodes.VectorUpdate, ProjectileStopHex, world));

            Assert.True(world.Get(0x80002D17).Velocity.Value.IsZero);
        }

        [Fact]
        public void ATruncatedVectorUpdateIsMalformedAndRecordsNothing()
        {
            WorldState world = new WorldState();

            // Everything but the two trailing sequences.
            string cut = PlayerJumpHex.Substring(0, PlayerJumpHex.Length - 8);

            Assert.Equal(DecodeOutcome.Malformed, ApplyInbound(Opcodes.VectorUpdate, cut, world));
            Assert.Equal(0, world.ObjectCount);
        }

        [Fact]
        public void LeavingAPortalReplacesThePhysicsState()
        {
            WorldState world = new WorldState();

            Assert.Equal(DecodeOutcome.Applied, ApplyInbound(Opcodes.SetState, PortalExitStateHex, world));

            // ReportCollisions | Gravity | EdgeSlide: the ordinary walking state.
            Assert.Equal(0x00400408u, world.Get(CapturedPlayer).PhysicsState);
        }

        [Fact]
        public void ADoorOpeningTurnsEthereal()
        {
            WorldState world = new WorldState();
            List<WorldObject> updated = new List<WorldObject>();
            world.ObjectUpdated += (_, o) => updated.Add(o);

            Assert.Equal(DecodeOutcome.Applied, ApplyInbound(Opcodes.SetState, PortcullisOpenHex, world));

            // A static object's id carries its landblock, and 0x2B11 is where the
            // session was.
            WorldObject door = Assert.Single(updated);
            Assert.Equal(0x72B1102Au, door.Id);
            Assert.Equal(0x0001001Cu, door.PhysicsState);
        }

        [Fact]
        public void ATruncatedSetStateIsMalformed()
        {
            WorldState world = new WorldState();

            string cut = PortalExitStateHex.Substring(0, 16);

            Assert.Equal(DecodeOutcome.Malformed, ApplyInbound(Opcodes.SetState, cut, world));
            Assert.Equal(0, world.ObjectCount);
        }

        [Fact]
        public void ANockedArrowIsParentedToTheArchersHand()
        {
            WorldState world = new WorldState();
            List<WorldObject> updated = new List<WorldObject>();
            world.ObjectUpdated += (_, o) => updated.Add(o);

            Assert.Equal(DecodeOutcome.Applied, ApplyInbound(Opcodes.ParentEvent, ArrowNockedHex, world));

            WorldObject arrow = Assert.Single(updated);
            Assert.Equal(0x80002B65u, arrow.Id);
            Assert.Equal(0x80002B63u, arrow.ParentId);

            // 1 is RightHand in the server's ParentLocation enum.
            Assert.Equal(1u, arrow.ParentLocation);
        }

        [Fact]
        public void ATruncatedParentEventIsMalformed()
        {
            WorldState world = new WorldState();

            // Cut inside the placement word.
            string cut = ArrowNockedHex.Substring(0, 30);

            Assert.Equal(DecodeOutcome.Malformed, ApplyInbound(Opcodes.ParentEvent, cut, world));
            Assert.Equal(0, world.ObjectCount);
        }

        [Fact]
        public void EffectsAndSoundsAreUnderstoodAndKeepNothing()
        {
            WorldState world = new WorldState();

            Assert.Equal(DecodeOutcome.Applied, ApplyInbound(Opcodes.PlayEffect, FlareSplatterHex, world));
            Assert.Equal(DecodeOutcome.Applied, ApplyInbound(Opcodes.PlayEffect, NetherBoltExplodeHex, world));
            Assert.Equal(DecodeOutcome.Applied, ApplyInbound(Opcodes.Sound, PickUpSoundHex, world));
            Assert.Equal(DecodeOutcome.Applied, ApplyInbound(Opcodes.Sound, FlareHitSoundHex, world));

            // Understood is not the same as recorded: nothing reads these.
            Assert.Equal(0, world.ObjectCount);
        }

        [Fact]
        public void ATruncatedEffectOrSoundIsMalformed()
        {
            WorldState world = new WorldState();

            // The speed and the volume are missing.
            Assert.Equal(DecodeOutcome.Malformed, ApplyInbound(Opcodes.PlayEffect, FlareSplatterHex.Substring(0, 16), world));
            Assert.Equal(DecodeOutcome.Malformed, ApplyInbound(Opcodes.Sound, PickUpSoundHex.Substring(0, 16), world));
        }

        [Fact]
        public void ChatChannelsAreReadInFullOrNotAtAll()
        {
            WorldState world = new WorldState();

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(ChatChannelsHex, world));

            // The 2006 document's five channels are there, but the message has ten and
            // a server sending only five would be a different server.
            string fiveOnly = ChatChannelsHex.Substring(0, ChatChannelsHex.Length - 40);
            Assert.Equal(DecodeOutcome.Malformed, ApplyEvent(fiveOnly, world));
        }

        [Fact]
        public void AnItemDroppedOnTheGroundLeavesItsContainer()
        {
            WorldState world = new WorldState();
            Apply(world, WireWriter.ObjectCreate(0x80002DD4, "Bowl", 1, ItemTypes.Misc, 0, containerId: Player));

            List<WorldObject> updated = new List<WorldObject>();
            world.ObjectUpdated += (_, o) => updated.Add(o);

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(DroppedBowlHex, world));

            WorldObject bowl = Assert.Single(updated);
            Assert.Null(bowl.ContainerId);
            Assert.Null(bowl.WielderId);
        }

        [Fact]
        public void ADropWithNoItemIdIsMalformed()
        {
            WorldState world = new WorldState();

            Assert.Equal(DecodeOutcome.Malformed, ApplyEvent(DroppedBowlHex.Substring(0, 24), world));
        }

        // ------------------------------------------------------------------- picked up

        // From session-20260929-1118.acap, whose character is Testchar I (0x50000006).
        private const uint Testchar = 0x50000006;
        private const uint Salvage = 0x8000DAE9;
        private const uint Arrow = 0x80000B77, Archer = 0x80000B75;

        /// <summary>Captured, 89.7 s: the salvage the character had just dropped, created on the ground in 0x2B110020.</summary>
        private const string SalvageOnTheGroundHex =
            "E9DA008011010101EF0BF20B000000900F891F009A0200000198020014040000650000002000112B8076B24260E83C4371FD3F42D9153E3F000000000000000084792B3F"
            + "140000202B000034810100020000000000000000000000000000000000000000183C29D10D0053616C7661676520283130302900F851C1260000004012000000F8240100"
            + "080008008F8900006400640001000100000000009A99E94064000900CC263C0000000000";

        /// <summary>Captured, 93.4 s: PickupEvent for it - its id, instance sequence 0, position sequence 3.</summary>
        private const string SalvagePickedUpHex = "E9DA008000000300";

        /// <summary>Captured, 1044.8 s: a skeleton's Acid Arrow, wielded by it.</summary>
        private const string ArrowHex =
            "770B008011010001EF0BF20B0000009F02000000011B02001404020065000000140000202B0000344B0500020000803F000000000000000000000000000000000000000000"
            + "00000098B323100A0041636964204172726F775510F11A00010000100000000100500000000100000000010000031000E803750B008000008000000080005000020000";

        /// <summary>
        /// Another player picking up an item on the ground. ACE tells everyone who can see it with
        /// PickupEvent alone, takes the item out of the world, and forgets that they knew it; if it is
        /// dropped again they are sent it afresh. So it goes, as a deletion would have it.
        /// </summary>
        [Fact]
        public void AnItemSomeoneElsePicksUpIsGoneFromTheGround()
        {
            WorldState world = new WorldState();
            world.SetPlayerId(Testchar);
            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(AcMessage.Create(Opcodes.ObjectCreate, System.Convert.FromHexString(SalvageOnTheGroundHex)), PacketDirection.Inbound, world));
            Assert.Equal(0x2B110020u, world.Get(Salvage).Location.Value.LandblockCell);
            List<uint> removed = new List<uint>();
            world.ObjectRemoved += (_, id) => removed.Add(id);

            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(AcMessage.Create(Opcodes.PickupEvent, System.Convert.FromHexString(SalvagePickedUpHex)), PacketDirection.Inbound, world));

            Assert.Equal(new[] { Salvage }, removed);
            Assert.Null(world.Get(Salvage));
        }

        /// <summary>
        /// Captured, 93.4 s: the character's own pickup of that salvage. ACE puts it in the pack first
        /// - PublicUpdateInstanceID for its container, then InventoryPutObjInContainer - and sends
        /// PickupEvent after, so the item is already the character's: it stays, off the landscape.
        /// </summary>
        [Fact]
        public void TheCharactersOwnPickupStaysInItsPack()
        {
            WorldState world = new WorldState();
            world.SetPlayerId(Testchar);
            MessageDecoder.Apply(AcMessage.Create(Opcodes.ObjectCreate, System.Convert.FromHexString(SalvageOnTheGroundHex)), PacketDirection.Inbound, world);
            List<uint> removed = new List<uint>();
            world.ObjectRemoved += (_, id) => removed.Add(id);

            MessageDecoder.Apply(AcMessage.Create(Opcodes.PublicUpdatePropertyInstanceId, System.Convert.FromHexString("02E9DA00800200000006000050")), PacketDirection.Inbound, world);
            Assert.Equal(DecodeOutcome.Applied, ApplyEvent("060000501B00000022000000E9DA0080060000500000000000000000", world));
            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(AcMessage.Create(Opcodes.PickupEvent, System.Convert.FromHexString(SalvagePickedUpHex)), PacketDirection.Inbound, world));

            Assert.Empty(removed);
            WorldObject salvage = world.Get(Salvage);
            Assert.Equal(Testchar, salvage.ContainerId);
            Assert.Null(salvage.Location);
        }

        /// <summary>
        /// Captured, 1044.8-1047.5 s: the skeleton nocks its arrow (ParentEvent), shoots - the arrow
        /// comes off the string by PickupEvent - and nocks it again. It is the skeleton's throughout.
        /// </summary>
        [Fact]
        public void AnArrowOffTheBowstringIsStillTheArchers()
        {
            WorldState world = new WorldState();
            world.SetPlayerId(Testchar);
            MessageDecoder.Apply(AcMessage.Create(Opcodes.ObjectCreate, System.Convert.FromHexString(ArrowHex)), PacketDirection.Inbound, world);
            MessageDecoder.Apply(AcMessage.Create(Opcodes.ParentEvent, System.Convert.FromHexString("750B0080770B0080010000000100000000000100")), PacketDirection.Inbound, world);
            Assert.Equal(Archer, world.Get(Arrow).WielderId);
            Assert.Equal(Archer, world.Get(Arrow).ParentId);
            List<uint> removed = new List<uint>();
            world.ObjectRemoved += (_, id) => removed.Add(id);

            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(AcMessage.Create(Opcodes.PickupEvent, System.Convert.FromHexString("770B008000000200")), PacketDirection.Inbound, world));
            Assert.Empty(removed);
            Assert.Equal(Archer, world.Get(Arrow).WielderId);
            Assert.Null(world.Get(Arrow).ParentId);

            MessageDecoder.Apply(AcMessage.Create(Opcodes.ParentEvent, System.Convert.FromHexString("750B0080770B0080010000000100000000000300")), PacketDirection.Inbound, world);
            Assert.Equal(Archer, world.Get(Arrow).ParentId);
        }

        [Fact]
        public void APickupOfSomethingUnknownChangesNothingAndAShortOneIsMalformed()
        {
            WorldState world = new WorldState();

            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(AcMessage.Create(Opcodes.PickupEvent, System.Convert.FromHexString(SalvagePickedUpHex)), PacketDirection.Inbound, world));
            Assert.Equal(0, world.ObjectCount);
            Assert.Equal(DecodeOutcome.Malformed, MessageDecoder.Apply(AcMessage.Create(Opcodes.PickupEvent, System.Convert.FromHexString("E9DA0080")), PacketDirection.Inbound, world));
        }
    }
}
