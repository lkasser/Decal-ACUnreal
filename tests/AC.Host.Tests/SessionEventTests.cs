using System;
using System.Collections.Generic;
using AC.Host.Decoding;
using AC.Host.World;
using AC.Protocol;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// What happens to the character as a whole - portal space, a vendor's window, death - and
    /// the chat rooms the server names, read off the wire.
    /// </summary>
    /// <remarks>
    /// The teleport, the client's arrival, the vendor and the room list are the bytes recorded
    /// in session-readonly.acap and session.acap. No recorded session has the character dying,
    /// so the death messages are ACE's writers' layouts, written out here by hand.
    /// </remarks>
    public class SessionEventTests
    {
        private const uint Player = 0x50000006;
        private const uint Vendor = 0x72B12027;

        private static WorldState World()
        {
            WorldState world = new WorldState(() => DateTimeOffset.UnixEpoch);
            world.SetPlayerId(Player);
            WorldObject me = world.GetOrAdd(Player, out _);
            me.Name = "Tester";
            return world;
        }

        private static DecodeOutcome Inbound(WorldState world, uint opcode, string hex)
            => MessageDecoder.Apply(AcMessage.Create(opcode, Convert.FromHexString(hex)), PacketDirection.Inbound, world);

        private static DecodeOutcome Outbound(WorldState world, string hex)
            => MessageDecoder.Apply(AcMessage.Create(Opcodes.GameAction, Convert.FromHexString(hex)), PacketDirection.Outbound, world);

        // ------------------------------------------------------------------- portal space

        /// <summary>
        /// Captured: PlayerTeleport 01000000 - the teleport's number, 1, padded to a word - and,
        /// about a second and a half later, the client's LoginComplete A1020000A1000000, action
        /// 0x00A1 with ordering sequence 0x2A1. The first is going in, the second coming out.
        /// </summary>
        [Fact]
        public void ATeleportIsPortalSpaceUntilTheClientSaysItHasArrived()
        {
            WorldState world = World();
            List<bool> changes = new List<bool>();
            world.PortalSpaceChanged += (_, entered) => changes.Add(entered);

            Assert.Equal(DecodeOutcome.Applied, Inbound(world, Opcodes.PlayerTeleport, "01000000"));
            Assert.True(world.Character.InPortalSpace);
            Assert.Equal(1, world.Character.TeleportSequence);

            Assert.Equal(DecodeOutcome.Applied, Outbound(world, "A1020000A1000000"));
            Assert.False(world.Character.InPortalSpace);

            Assert.Equal(new[] { true, false }, changes);
        }

        /// <summary>
        /// Captured: every login sends LoginComplete, 01000000A1000000, with no teleport before
        /// it. That is not coming out of a portal, and nothing is said.
        /// </summary>
        [Fact]
        public void TheLoginsOwnLoginCompleteIsNotAPortal()
        {
            WorldState world = World();
            List<bool> changes = new List<bool>();
            world.PortalSpaceChanged += (_, entered) => changes.Add(entered);

            Outbound(world, "01000000A1000000");
            Outbound(world, "03000000A1000000");

            Assert.Empty(changes);
            Assert.False(world.Character.InPortalSpace);
        }

        [Fact]
        public void AShortTeleportIsMalformedNotThrown()
        {
            Assert.Equal(DecodeOutcome.Malformed, Inbound(World(), Opcodes.PlayerTeleport, "01"));
        }

        // ------------------------------------------------------------------- vendors

        /// <summary>
        /// The start of a captured ApproachVendor (0x0062) as its GameEvent carries it: the player
        /// 0x50000006, event sequence 0x30, the event, then the vendor 0x72B12027, its item types,
        /// its value range, whether it takes magic, its buy and sell rates (0.8, 1.8) and no
        /// alternative currency. Its 28 items follow in the capture; only the vendor is read.
        /// </summary>
        private const string CapturedApproachVendor =
            "06000050" + "30000000" + "62000000" + "2720B172" + "80000400" + "00000000" + "A0860100" + "01000000"
            + "CDCC4C3F" + "6666E63F" + "00000000" + "00000000" + "0000" + "0000" + "1C000000";

        private static WorldObject PlaceVendor(WorldState world, float x, float useRadius = 3f)
        {
            WorldObject vendor = world.GetOrAdd(Vendor, out _);
            vendor.Name = "Shopkeeper Renald";
            vendor.DescriptionFlags = DescriptionFlags.Vendor | DescriptionFlags.Stuck;
            vendor.UseRadius = useRadius;
            vendor.Location = new Location(0xA9B40021, x, 50, 10, 1, 0, 0, 0);
            return vendor;
        }

        [Fact]
        public void ApproachVendorOpensItsWindow()
        {
            WorldState world = World();
            List<uint> changes = new List<uint>();
            world.VendorChanged += (_, id) => changes.Add(id);

            Assert.Equal(DecodeOutcome.Applied, Inbound(world, Opcodes.GameEvent, CapturedApproachVendor));

            Assert.Equal(Vendor, world.Character.OpenVendorId);
            Assert.Equal(new[] { Vendor }, changes);

            // Refreshed after a sale: still the one window.
            Inbound(world, Opcodes.GameEvent, CapturedApproachVendor);
            Assert.Single(changes);
        }

        /// <summary>
        /// The server says nothing when a customer walks off - ACE only has the vendor say goodbye
        /// once the customer is beyond its use radius. The window closes at that distance, measured
        /// between centres with a metre allowed for the two bodies.
        /// </summary>
        [Fact]
        public void WalkingOutOfTheVendorsReachClosesItsWindow()
        {
            WorldState world = World();
            PlaceVendor(world, 100, useRadius: 3f);
            Inbound(world, Opcodes.GameEvent, CapturedApproachVendor);
            List<uint> changes = new List<uint>();
            world.VendorChanged += (_, id) => changes.Add(id);

            world.SetClientPosition(new Location(0xA9B40021, 102, 50, 10, 1, 0, 0, 0), default);
            world.SetClientPosition(new Location(0xA9B40021, 103.9f, 50, 10, 1, 0, 0, 0), default);
            Assert.Equal(Vendor, world.Character.OpenVendorId);

            world.SetClientPosition(new Location(0xA9B40021, 104.5f, 50, 10, 1, 0, 0, 0), default);
            Assert.Equal(0u, world.Character.OpenVendorId);
            Assert.Equal(new uint[] { 0 }, changes);
        }

        [Fact]
        public void ATeleportOrTheVendorLeavingViewClosesItsWindow()
        {
            WorldState world = World();
            PlaceVendor(world, 100);
            Inbound(world, Opcodes.GameEvent, CapturedApproachVendor);

            Inbound(world, Opcodes.PlayerTeleport, "02000000");
            Assert.Equal(0u, world.Character.OpenVendorId);

            Outbound(world, "04000000A1000000");
            Inbound(world, Opcodes.GameEvent, CapturedApproachVendor);
            Assert.Equal(Vendor, world.Character.OpenVendorId);

            world.Remove(Vendor);
            Assert.Equal(0u, world.Character.OpenVendorId);
        }

        [Fact]
        public void OpeningAChestTakesTheVendorsPlaceButOpeningAPackDoesNot()
        {
            WorldState world = World();
            PlaceVendor(world, 100);
            Inbound(world, Opcodes.GameEvent, CapturedApproachVendor);

            WorldObject pack = world.GetOrAdd(0x80000100, out _);
            pack.ContainerId = Player;
            world.NotifyContainerViewed(new ContainerContents(pack.Id, Array.Empty<ContainedItem>()));
            Assert.Equal(Vendor, world.Character.OpenVendorId);

            WorldObject chest = world.GetOrAdd(0x80000200, out _);
            chest.Location = new Location(0xA9B40021, 101, 50, 10, 1, 0, 0, 0);
            world.NotifyContainerViewed(new ContainerContents(chest.Id, Array.Empty<ContainedItem>()));
            Assert.Equal(0u, world.Character.OpenVendorId);
        }

        // ------------------------------------------------------------------- death

        /// <summary>
        /// VictimNotification (0x01AC), as ACE writes it: one string, the sentence for the one who
        /// died. It is the death, and it is a line in the chat window.
        /// </summary>
        [Fact]
        public void AVictimNotificationIsTheCharactersDeathAndAChatLine()
        {
            WorldState world = World();
            List<string> deaths = new List<string>();
            List<ChatMessage> chat = new List<ChatMessage>();
            world.Died += (_, message) => deaths.Add(message);
            world.ChatReceived += (_, message) => chat.Add(message);

            WireWriter w = WireWriter.GameEvent(Player, GameEvents.VictimNotification).String16L("You were killed by a Drudge Slinker!");
            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(w.ToMessage(), PacketDirection.Inbound, world));

            Assert.Equal(new[] { "You were killed by a Drudge Slinker!" }, deaths);
            ChatMessage line = Assert.Single(chat);
            Assert.Equal(ChatKind.Combat, line.Kind);
            Assert.Equal("You were killed by a Drudge Slinker!\n", ChatLines.Format(line, Player));
        }

        /// <summary>PlayerKilled (0x019E), as ACE writes it: the sentence, the one who died, the killer. Only a chat line.</summary>
        [Fact]
        public void PlayerKilledIsAChatLineForEveryoneNear()
        {
            WorldState world = World();
            List<string> deaths = new List<string>();
            List<ChatMessage> chat = new List<ChatMessage>();
            world.Died += (_, message) => deaths.Add(message);
            world.ChatReceived += (_, message) => chat.Add(message);

            WireWriter w = new WireWriter(Opcodes.PlayerKilled).String16L("Bob was killed by a Drudge Slinker!").U32(0x50000009).U32(0x80001111);
            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(w.ToMessage(), PacketDirection.Inbound, world));

            Assert.Empty(deaths);
            Assert.Equal("Bob was killed by a Drudge Slinker!", Assert.Single(chat).Text);
        }

        // ------------------------------------------------------------------- the server's chat rooms

        /// <summary>
        /// Captured SetTurbineChatChannels (0x0295): the allegiance's room 0x80001424, then general
        /// 2, trade 3, LFG 4, roleplay 5, olthoi 10, the character's society 7 (Celestial Hand),
        /// and the three societies' rooms 7, 8 and 9 - ACE's order.
        /// </summary>
        [Fact]
        public void TheRoomsTheServerNamesAreKept()
        {
            WorldState world = World();

            Assert.Equal(DecodeOutcome.Applied, Inbound(world, Opcodes.GameEvent,
                "060000500A0000009502000024140080020000000300000004000000050000000A00000007000000070000000800000009000000"));

            Assert.Equal(0x80001424u, world.TurbineChannels[TurbineChannel.Allegiance]);
            Assert.Equal(2u, world.TurbineChannels[TurbineChannel.General]);
            Assert.Equal(4u, world.TurbineChannels[TurbineChannel.Lfg]);
            Assert.Equal(10u, world.TurbineChannels[TurbineChannel.Olthoi]);
            Assert.Equal(7u, world.TurbineChannels[TurbineChannel.Society]);
            Assert.Equal(9u, world.TurbineChannels[TurbineChannel.SocietyRadiantBlood]);
        }

        /// <summary>A tell names who sent it, and "/r" answers them.</summary>
        [Fact]
        public void TheLastTellsSenderIsRemembered()
        {
            WorldState world = World();
            WireWriter w = WireWriter.GameEvent(Player, GameEvents.Tell)
                .String16L("hi there").String16L("Bob").U32(0x50000009).U32(Player).U32(3).U32(0);

            MessageDecoder.Apply(w.ToMessage(), PacketDirection.Inbound, world);

            Assert.Equal("Bob", world.LastTellFrom);
        }
    }
}
